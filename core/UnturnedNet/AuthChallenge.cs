using System;
using System.Security.Cryptography;

namespace UnturnedNet
{
    /// <summary>
    /// The whole server-side admission decision for an identity, in one place: is this token real, and is
    /// the peer presenting it the one it was issued to?
    ///
    /// ⭐ WHY THIS IS A SEPARATE TYPE FROM THE WIRE. The handshake is a single Connect datagram today, so
    /// carrying a proof needs a challenge round trip -- and that is a protocol bump, which waits its turn.
    /// The DECISION does not have to wait, and it is the half that must not be wrong: a mistake in framing
    /// produces a peer that cannot join, loudly. A mistake here produces a peer that joins as someone else,
    /// silently. So the crypto lands first, tested, and the wire change afterwards is plumbing that carries
    /// bytes to a function whose behaviour is already pinned.
    ///
    /// ⚠ THE TWO CHECKS ARE NOT INTERCHANGEABLE AND BOTH ARE REQUIRED.
    ///   1. The TOKEN says Steam vouched for a SteamID and names a public key. It is copyable text: anyone
    ///      who has seen one can present it.
    ///   2. The SIGNATURE says this peer holds the private half of the key the token names. It is worthless
    ///      without check 1, because an attacker can mint a keypair and sign anything with it -- what stops
    ///      that is the token binding a key to an identity the attacker does not control.
    /// Accept on the token alone and it is a bearer password. Accept on the signature alone and it proves
    /// only that the peer owns a key, which everyone does.
    ///
    /// ⚠ THE NONCE MUST BE THE SERVER'S. A client-chosen challenge is not a challenge -- it is a value the
    /// client may replay from a previous session, or from another server, forever. NewNonce() exists so the
    /// caller does not reach for something convenient and predictable (a tick, a player id, a timestamp).
    /// </summary>
    public static class AuthChallenge
    {
        public const int NonceBytes = 32;

        /// <summary>Cryptographically random, never a counter or a clock. 32 bytes so a birthday collision
        /// across every session this port will ever host stays impossible rather than unlikely.</summary>
        public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceBytes);

        /// <summary>The admission decision. Returns false with a reason for every failure, because a
        /// verifier that refuses silently is indistinguishable from one that is broken -- which is the
        /// failure this whole feature spent a day being one step away from.</summary>
        public static bool TryAdmit(string token, byte[] servicePubSpki, byte[] nonce, byte[] signature,
                                    long nowUnix, out string steamId, out string error)
        {
            steamId = null; error = null;

            if (nonce == null || nonce.Length != NonceBytes) { error = $"nonce must be {NonceBytes} bytes"; return false; }
            if (signature == null || signature.Length != 64) { error = $"signature is {signature?.Length ?? 0} bytes, expected 64 (P1363 r||s)"; return false; }

            // 1. Is the token real, and what key does it bind?
            if (!AuthToken.TryVerify(token, servicePubSpki, nowUnix, out string sid, out string clientPubB64u, out string tokErr))
            { error = "token: " + tokErr; return false; }

            // 2. Does this peer hold that key? The token's own pk is the ONLY key tried -- taking a key from
            //    anywhere else in the message would let the peer nominate the key that checks its own
            //    signature, which is not a check at all.
            byte[] pub;
            try { pub = FromB64Url(clientPubB64u); }
            catch { error = "token binds an unreadable client key"; return false; }

            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(pub, out _);
                if (!ec.VerifyData(nonce, signature, HashAlgorithmName.SHA256))
                { error = "signature does not match the key this token binds"; return false; }
            }
            catch (Exception e) { error = "client key is unusable: " + e.Message; return false; }

            steamId = sid;
            return true;
        }

        static byte[] FromB64Url(string s) =>
            Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
    }
}
