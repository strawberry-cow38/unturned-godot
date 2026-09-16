using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// "Sign in through Steam" — OpenID 2.0, the public web flow, NOT Steamworks.
///
/// ⭐ WHY THIS AND NOT STEAM AUTH. Steamworks ticket auth (GetAuthSessionTicket +
/// ISteamUserAuth/AuthenticateUserTicket) needs your own App ID and a publisher web key, and App IDs come
/// from shipping on Steam. This port cannot get one. Steam's OpenID provider is a different thing: a
/// public identity provider any website may use, no App ID, no key, nothing registered. It gives a
/// VERIFIED SteamID64 and nothing else — no ownership, no VAC, no friends, no inventory.
///
/// ⚠⚠ THE VERIFICATION STEP IS THE WHOLE SECURITY PROPERTY. The browser redirects to a listener on
/// 127.0.0.1, and ANY process on this machine can hit that URL with whatever claimed_id it likes. The
/// redirect is a claim, not proof. What makes it proof is posting the parameters straight back to Steam
/// with openid.mode=check_authentication and requiring "is_valid:true" in the reply — Steam signs the
/// assertion and only Steam can confirm the signature. Delete that step and this file becomes a very
/// elaborate way to let anything on the box choose its own SteamID.
///
/// The listener binds 127.0.0.1 only (never 0.0.0.0, which would put it on the LAN), on an
/// OS-assigned free port, for one request, with a timeout.
///
/// ✅ THE ACCEPT PATH IS VERIFIED AGAINST LIVE STEAM (2026-09-16), which it had not been when this
/// merged. The worry was specific and worth naming: forged assertions were provably refused, but a
/// verifier that refuses EVERYTHING passes that same test, so "rejects a forgery" was no evidence
/// that a real sign-in is ever accepted. Two observations closed it.
///   1. RESPONSE SHAPE, read off the wire rather than assumed: check_authentication answers
///      "ns:http://specs.openid.net/auth/2.0\nis_valid:false\n" — line-based key:value, bare \n, no
///      CR. That is exactly what the Split('\n') + exact-line match below reads, so the accept branch
///      can actually fire. A JSON or single-line body would have made it dead code that still compiled.
///   2. A REAL SIGNED ASSERTION returned is_valid:true, first attempt, through this exact recipe
///      (every openid.* posted back, mode replaced wholesale). Replaying the SAME assertion then
///      returned is_valid:false — Steam spends the nonce. That replay is the control that makes the
///      true worth anything: a rubber-stamp would have said true twice.
/// Driven from a throwaway page (claw.bitvox.me/steamcheck/) because the flow needs a real human and
/// a browser, which no test here can supply.
///
/// ⚠ WHAT THAT DOES NOT COVER, so nobody promotes it: it proves the RECIPE and Steam's behaviour, not
/// the C# around them. The HttpListener bind, the browser handoff and the claimed_id parse below are
/// still unexercised end to end — lower doubt, not zero, and a desktop run is what settles them.
/// </summary>
public static class SteamSignIn
{
    const string OpenIdEndpoint = "https://steamcommunity.com/openid/login";
    const string NsOpenId = "http://specs.openid.net/auth/2.0";
    const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";
    static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);   // a human has to find the browser, log in, maybe do 2FA

    public sealed class Result
    {
        public string SteamId64;      // null on failure
        public string Error;          // null on success
        public bool Cancelled;        // the wait ran out or the window was closed
    }

    public static async Task<Result> SignInAsync(Action<string> log, CancellationToken ct = default)
    {
        HttpListener listener = null;
        try
        {
            int port = FreePort();
            string prefix = $"http://127.0.0.1:{port}/steam/";
            listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            string url = BuildAuthUrl(prefix, $"http://127.0.0.1:{port}/");
            log("Opening Steam in your browser...");
            if (!OpenBrowser(url, log))
                return new Result { Error = "couldn't open a browser. Sign-in URL: " + url };

            var ctxTask = listener.GetContextAsync();
            var done = await Task.WhenAny(ctxTask, Task.Delay(Patience, ct));
            if (done != ctxTask)
                return new Result { Cancelled = true, Error = "timed out waiting for the browser (3 min)" };

            var ctx = await ctxTask;
            var q = ctx.Request.QueryString;

            // The browser is done either way — say so before we spend time verifying.
            string claimed = q["openid.claimed_id"];
            bool shaped = !string.IsNullOrEmpty(claimed) && claimed.StartsWith("https://steamcommunity.com/openid/id/", StringComparison.Ordinal);

            log("Verifying the assertion with Steam...");
            bool valid = shaped && await CheckAuthenticationAsync(q, ct);

            Respond(ctx, valid
                ? "<h2>Signed in.</h2><p>You can close this tab and go back to the launcher.</p>"
                : "<h2>Sign-in failed.</h2><p>Steam did not confirm that assertion. Nothing was saved.</p>");

            if (!shaped) return new Result { Error = "Steam returned no usable identity (openid.claimed_id missing or wrong shape)" };
            if (!valid) return new Result { Error = "Steam did NOT confirm that sign-in (is_valid was not true) -- refusing it" };

            string id = claimed.Substring("https://steamcommunity.com/openid/id/".Length).Trim('/');
            foreach (char c in id) if (c < '0' || c > '9') return new Result { Error = "verified assertion carried a non-numeric SteamID: " + id };
            return new Result { SteamId64 = id };
        }
        catch (OperationCanceledException) { return new Result { Cancelled = true, Error = "cancelled" }; }
        catch (Exception ex) { return new Result { Error = ex.Message }; }
        finally { try { listener?.Stop(); listener?.Close(); } catch { } }
    }

    /// <summary>POST every openid.* parameter back to Steam with mode=check_authentication. Steam replies
    /// "is_valid:true" only for an assertion it actually signed. This is what turns a redirect into proof.</summary>
    static async Task<bool> CheckAuthenticationAsync(System.Collections.Specialized.NameValueCollection q, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>();
        foreach (string key in q.AllKeys)
        {
            if (key == null || !key.StartsWith("openid.", StringComparison.Ordinal)) continue;
            form.Add(new KeyValuePair<string, string>(key, q[key]));
        }
        // Replace the mode wholesale rather than appending a second one -- a duplicate key is undefined here.
        form.RemoveAll(kv => kv.Key == "openid.mode");
        form.Add(new KeyValuePair<string, string>("openid.mode", "check_authentication"));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var resp = await http.PostAsync(OpenIdEndpoint, new FormUrlEncodedContent(form), ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        // The body is key:value lines. Require the exact line, not a substring of the whole document.
        foreach (var line in body.Split('\n'))
            if (line.Trim() == "is_valid:true") return true;
        return false;
    }

    static string BuildAuthUrl(string returnTo, string realm)
    {
        var sb = new StringBuilder(OpenIdEndpoint);
        sb.Append("?openid.ns=").Append(Uri.EscapeDataString(NsOpenId));
        sb.Append("&openid.mode=checkid_setup");
        sb.Append("&openid.return_to=").Append(Uri.EscapeDataString(returnTo));
        sb.Append("&openid.realm=").Append(Uri.EscapeDataString(realm));
        sb.Append("&openid.identity=").Append(Uri.EscapeDataString(IdentifierSelect));
        sb.Append("&openid.claimed_id=").Append(Uri.EscapeDataString(IdentifierSelect));
        return sb.ToString();
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static void Respond(HttpListenerContext ctx, string html)
    {
        try
        {
            byte[] b = Encoding.UTF8.GetBytes("<html><body style=\"font-family:sans-serif;background:#16181d;color:#d8dee9;padding:40px\">" + html + "</body></html>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.OutputStream.Close();
        }
        catch { }
    }

    static bool OpenBrowser(string url, Action<string> log)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
        catch { }
        foreach (var (exe, args) in new[] { ("xdg-open", url), ("open", url), ("cmd", "/c start \"\" \"" + url + "\"") })
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); return true; }
            catch { }
        }
        return false;
    }
}

