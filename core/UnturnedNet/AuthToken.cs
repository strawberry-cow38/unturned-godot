using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnturnedNet
{
    /// <summary>
    /// Verifies a stmauth identity token: proof, checkable by a dedicated server with no network and no
    /// dependencies, that Steam vouched for a SteamID and bound it to a client public key.
    ///
    /// ⭐ WHY A TOKEN AND NOT A LOOKUP. A server asking a service "is this player real?" puts that service
    /// in the join path, so its downtime becomes the game's downtime. A signed token inverts that: the
    /// server checks a signature against one public key it already holds and never contacts anything.
    /// stmauth being down stops NEW sign-ins and nothing else.
    ///
    /// ⚠ WHAT THIS DOES NOT PROVE, and the distinction is the whole security story: a valid token proves
    /// the SteamID was verified and names a public key. It does NOT prove the peer sending it is that
    /// player -- a token is copyable text. The caller must still make the peer sign a SERVER-CHOSEN NONCE
    /// with the private half of `clientPubKey` (see the join handshake). Treating a bare valid token as
    /// authentication turns it into a bearer password that anyone who sniffs it can replay.
    ///
    /// ⚠ P-256 RATHER THAN Ed25519, forced by this side of the wire. net8.0 has no built-in Ed25519, and
    /// this repo's only packages are Avalonia and NUnit; choosing it would have put BouncyCastle in every
    /// dedicated server forever to save 32 bytes a token. ECDsa + SHA-256 is in the base class library.
    ///
    /// ⚠ SIGNATURES ARE IEEE-P1363 (r||s, 64 bytes), NOT DER -- which is .NET's default and NOT node's.
    /// The default-to-default path across the two runtimes produces a signature this rejects every time,
    /// and the symptom is "nobody can log in", not "two libraries disagree about framing". AuthTokenTests
    /// pins it with a token the real minting path actually produced.
    /// </summary>
    public static class AuthToken
    {
        public const string Version = "v1";

        /// <param name="nowUnix">Seconds since epoch. Passed in rather than read from the clock so a test
        /// can pin it -- an expiry test against DateTime.UtcNow silently stops testing expiry the day the
        /// fixture's token runs out, and then passes forever for the wrong reason.</param>
        public static bool TryVerify(string token, byte[] servicePubSpki, long nowUnix,
                                     out string steamId, out string clientPubKey, out string error)
        {
            steamId = null; clientPubKey = null; error = null;
            if (string.IsNullOrEmpty(token)) { error = "empty token"; return false; }

            string[] parts = token.Split('.');
            if (parts.Length != 3) { error = $"expected 3 segments, got {parts.Length}"; return false; }
            if (parts[0] != Version) { error = $"unknown token version \"{parts[0]}\""; return false; }

            byte[] sig, payload;
            try { sig = FromB64Url(parts[2]); payload = FromB64Url(parts[1]); }
            catch { error = "a segment is not base64url"; return false; }
            if (sig.Length != 64) { error = $"signature is {sig.Length} bytes, expected 64 (P1363 r||s)"; return false; }

            // Verify the bytes AS RECEIVED, never a re-serialisation of the parsed object: a JSON round
            // trip can reorder keys or renormalise numbers, and then a signature over "what we meant"
            // stops matching the signature over "what was sent".
            byte[] signedBytes = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(servicePubSpki, out _);
                if (!ec.VerifyData(signedBytes, sig, HashAlgorithmName.SHA256))
                { error = "signature does not verify against the service key"; return false; }
            }
            catch (Exception e) { error = "service key is unusable: " + e.Message; return false; }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                long exp = root.GetProperty("exp").GetInt64();
                if (nowUnix >= exp) { error = $"expired {nowUnix - exp}s ago"; return false; }
                string sid = root.GetProperty("sid").GetString();
                if (sid == null || sid.Length != 17) { error = "sid is not a SteamID64"; return false; }
                foreach (char c in sid) if (c < '0' || c > '9') { error = "sid is not numeric"; return false; }
                steamId = sid;
                clientPubKey = root.GetProperty("pk").GetString();
                if (string.IsNullOrEmpty(clientPubKey)) { error = "token binds no client key"; return false; }
                return true;
            }
            catch (Exception e) { error = "payload is not the expected JSON: " + e.Message; return false; }
        }

        static byte[] FromB64Url(string s) =>
            Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
    }
}
