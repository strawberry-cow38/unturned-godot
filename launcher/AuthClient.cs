using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Signs in through stmauth (claw.bitvox.me/stmauth) and keeps the credential it hands back.
///
/// ⭐ WHY THIS REPLACED TALKING TO STEAM DIRECTLY. The launcher used to run the OpenID flow itself and
/// keep the SteamID. That verification was real -- it was proven against live Steam -- but it convinced
/// only the launcher: a game server receiving "I am 7656..." from a client has no way to check it, and
/// the claim and the machine making it are the same machine. stmauth exists to be the third party, so
/// the launcher's job is now to collect a SIGNED token rather than to verify anything itself.
///
/// ⚠ THE KEYPAIR IS THE POINT, not the token. The token says "Steam vouched for this SteamID, and that
/// identity belongs to whoever holds THIS public key". A stolen token is useless without the private
/// half, because the server makes the client sign a server-chosen nonce with it at join. Copy the token
/// out of this folder and you can prove nothing; that is the whole difference between a credential and a
/// bearer password.
///
/// ⚠ POLLING, NOT A REDIRECT BACK TO 127.0.0.1, and that is deliberate rather than lazy. A loopback
/// listener requires the browser to be on THIS machine. Polling does not, so the sign-in works when the
/// browser is a phone -- which is not a hypothetical: every real Steam sign-in tested today was done on
/// one. The state is 24 random bytes, and the token it returns is bound to a key only this box holds,
/// so guessing a state buys an attacker a credential they cannot use.
/// </summary>
public static class AuthClient
{
    public const string DefaultBase = "https://claw.bitvox.me/stmauth";
    static string Base => Environment.GetEnvironmentVariable("UG_STMAUTH") is string s && s.Length > 0 ? s : DefaultBase;

    static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);       // a human, a browser, possibly 2FA
    static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    public sealed class Result
    {
        public string Token;        // null on failure
        public string SteamId64;    // parsed out of the token payload, for display
        public string Error;
        public bool Cancelled;
    }

    /// <summary>The private key never leaves this file, and the file never leaves the box. 0600 where the
    /// platform has it; on Windows the user profile is the boundary.</summary>
    public static string KeyPath(string baseDir) => Path.Combine(baseDir, "client_key.pem");
    public static string TokenPath(string baseDir) => Path.Combine(baseDir, "auth_token.txt");

    /// <summary>Load the client keypair, generating one on first use. Returns the PUBLIC half as base64url
    /// SPKI -- the exact bytes a server will later import to check a nonce signature.</summary>
    public static string EnsureKeyPair(string baseDir, out ECDsa key)
    {
        string p = KeyPath(baseDir);
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(p))
        {
            try { key.ImportFromPem(File.ReadAllText(p)); }
            catch
            {
                // A corrupt key is regenerated rather than fatal -- but that INVALIDATES the stored token,
                // which is bound to the old public half. Saying so is the difference between "sign in
                // again" and a mystery rejection at join.
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                File.WriteAllText(p, key.ExportECPrivateKeyPem());
                Harden(p);
                try { File.Delete(TokenPath(baseDir)); } catch { }
            }
        }
        else
        {
            File.WriteAllText(p, key.ExportECPrivateKeyPem());
            Harden(p);
        }
        return B64Url(key.ExportSubjectPublicKeyInfo());
    }

    static void Harden(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }   // a filesystem that cannot express this must not stop the launcher starting
    }

    public static async Task<Result> SignInAsync(string baseDir, Action<string> log, CancellationToken ct = default)
    {
        try
        {
            string pk = EnsureKeyPair(baseDir, out _);
            string state = B64Url(RandomNumberGenerator.GetBytes(24));
            string url = $"{Base}/login?state={Uri.EscapeDataString(state)}&pk={Uri.EscapeDataString(pk)}";

            log("Opening Steam in your browser...");
            if (!OpenBrowser(url, log))
                return new Result { Error = "couldn't open a browser. Sign-in URL: " + url };
            log("Waiting for you to finish (you can do this on your phone).");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(PollEvery, ct);
                HttpResponseMessage r;
                // A poll that throws is a blip, not a failure: the deadline is what ends this, so a
                // dropped connection mid-sign-in costs one retry rather than the whole attempt.
                try { r = await http.GetAsync($"{Base}/token?state={Uri.EscapeDataString(state)}", ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { continue; }

                if (r.StatusCode == System.Net.HttpStatusCode.Accepted) continue;       // 202: not yet
                if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new Result { Error = "the sign-in expired before it finished" };
                if (!r.IsSuccessStatusCode) continue;

                string token = (await r.Content.ReadAsStringAsync(ct)).Trim();
                if (!token.StartsWith("v1.", StringComparison.Ordinal))
                    return new Result { Error = "the service returned something that is not a token" };

                File.WriteAllText(TokenPath(baseDir), token);
                Harden(TokenPath(baseDir));
                return new Result { Token = token, SteamId64 = SteamIdOf(token) };
            }
            return new Result { Cancelled = true, Error = "timed out waiting for the browser (3 min)" };
        }
        catch (OperationCanceledException) { return new Result { Cancelled = true, Error = "cancelled" }; }
        catch (Exception ex) { return new Result { Error = ex.Message }; }
    }

    /// <summary>Read the SteamID out of a stored token, for display only.
    /// ⚠ NOT a verification, and the launcher must never present it as one -- it reads the payload without
    /// checking the signature, because it has no service public key and no business deciding. The server
    /// verifies. A launcher that "validated" a token here would be checking its own homework.</summary>
    public static string SteamIdOf(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            string json = System.Text.Encoding.UTF8.GetString(FromB64Url(parts[1]));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("sid").GetString();
        }
        catch { return null; }
    }

    /// <summary>The stored token, or "" -- expiry is the SERVER's call, not ours.</summary>
    public static string LoadToken(string baseDir)
    {
        try { return File.Exists(TokenPath(baseDir)) ? File.ReadAllText(TokenPath(baseDir)).Trim() : ""; }
        catch { return ""; }
    }

    static bool OpenBrowser(string url, Action<string> log)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
        catch (Exception ex) { log("(could not open a browser: " + ex.Message + ")"); return false; }
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    static byte[] FromB64Url(string s) =>
        Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
}
