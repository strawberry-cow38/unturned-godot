using System;
using System.Security.Cryptography;
using NUnit.Framework;
using UnturnedNet;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// The server's admission decision: a real token AND proof the peer holds the key it binds.
    ///
    /// ⭐ The test that justifies the design is <see cref="a_stolen_token_with_the_thiefs_own_signature_is_refused"/>.
    /// Everything else here fails loudly in ways an implementer would notice; that one fails SILENTLY, by
    /// letting somebody join as another player, and it is the exact attack that "just check the token" gets
    /// wrong while passing every other test in this file.
    ///
    /// Fixtures come from stmauth's real minting path against a throwaway service key, binding a genuine
    /// P-256 client keypair -- the private half below is generated for this file and is not a secret.
    /// </summary>
    public class AuthChallengeTests
    {
        const string ServicePub = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE0xv9U9iECWsXHRYD1X1MF0VqfNbDrSURQrUi5UzUTkVGvzC9FTfapQ20+RfIOcj4QRjqpKP+YjW6L+aG5bTr5g==";
        const string Token      = "v1.eyJzaWQiOiI3NjU2MTE5ODAxMjM0NTY3OCIsInBrIjoiTUZrd0V3WUhLb1pJemowQ0FRWUlLb1pJemowREFRY0RRZ0FFX3IwektHY2FyTGkwb0RCMzNXYmlSb04tZGktQ19oMEZlUVdaS195N1RqTzhQYV85RW1ISmNKaUZmYUpSVWd4dkEtUXpxSWZ2bFFFdlRCVnBUaW5jQmciLCJpYXQiOjE3ODk1OTA3MjMsImV4cCI6MTc5MjE4MjcyM30.7u821SwwCNI0Am4fMPtN2CbjfmxHHqfxotNCU7o55YNXVZnPCx1g__QuY6htjZS9aVH6HEPgrKnw6f4J32ymfQ";
        const string ClientPriv = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgGzcPmho0aQO9tkKDtjccvCglB97DCBvvxKdWHrkCdLahRANCAAT+vTMoZxqsuLSgMHfdZuJGg352L4L+HQV5BZkr/LtOM7w9r/0SYclwmIV9olFSDG8D5DOoh++VAS9MFWlOKdwG";
        const long Iat = 1789590723, Exp = 1792182723;
        const long Now = Iat + 60;

        static byte[] Pub => Convert.FromBase64String(ServicePub);

        static ECDsa TheRealClient()
        {
            var k = ECDsa.Create();
            k.ImportPkcs8PrivateKey(Convert.FromBase64String(ClientPriv), out _);
            return k;
        }

        [Test]
        public void the_real_client_signing_the_servers_nonce_is_admitted()
        {
            byte[] nonce = AuthChallenge.NewNonce();
            using var me = TheRealClient();
            byte[] sig = me.SignData(nonce, HashAlgorithmName.SHA256);

            bool ok = AuthChallenge.TryAdmit(Token, Pub, nonce, sig, Now, out var sid, out var err);
            Assert.That(ok, Is.True, $"a legitimate join was refused: {err}");
            Assert.That(sid, Is.EqualTo("76561198012345678"));
        }

        /// ⭐ THE ONE. The attacker has a real token (copied -- it is plain text in a file) and signs the
        /// server's nonce perfectly, with a keypair they generated themselves. Every individual piece
        /// verifies. Admitting this is impersonation, and it is what accepting a bare token means.
        [Test]
        public void a_stolen_token_with_the_thiefs_own_signature_is_refused()
        {
            byte[] nonce = AuthChallenge.NewNonce();
            using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] sig = thief.SignData(nonce, HashAlgorithmName.SHA256);   // valid signature, wrong key

            bool ok = AuthChallenge.TryAdmit(Token, Pub, nonce, sig, Now, out var sid, out var err);
            Assert.That(ok, Is.False,
                "someone who merely COPIED a token joined as its owner -- the token is being treated as a password");
            Assert.That(sid, Is.Null, "a refused peer must not leak an identity to the caller");
            Assert.That(err, Does.Contain("signature"));
        }

        /// The other half of the same coin: a peer with a keypair and no token proves only that keys exist.
        [Test]
        public void a_perfect_signature_over_a_forged_token_is_refused()
        {
            byte[] nonce = AuthChallenge.NewNonce();
            using var me = TheRealClient();
            byte[] sig = me.SignData(nonce, HashAlgorithmName.SHA256);
            var parts = Token.Split('.');
            string bent = $"{parts[0]}.{parts[1]}.{parts[2].Substring(0, 10)}{(parts[2][10] == 'A' ? 'B' : 'A')}{parts[2].Substring(11)}";

            Assert.That(AuthChallenge.TryAdmit(bent, Pub, nonce, sig, Now, out _, out var err), Is.False,
                        "a tampered token was accepted because the signature checked out");
            Assert.That(err, Does.StartWith("token:"));
        }

        /// ⚠ Replay. A signature is only a proof of THIS session; reusing one from another nonce is exactly
        /// what a recorded handshake gives an attacker.
        [Test]
        public void a_signature_over_a_different_nonce_is_refused()
        {
            using var me = TheRealClient();
            byte[] oldNonce = AuthChallenge.NewNonce();
            byte[] sig = me.SignData(oldNonce, HashAlgorithmName.SHA256);
            byte[] thisSession = AuthChallenge.NewNonce();

            Assert.That(AuthChallenge.TryAdmit(Token, Pub, thisSession, sig, Now, out _, out _), Is.False,
                        "a signature from an earlier challenge was replayed successfully");
        }

        [Test]
        public void an_expired_token_is_refused_however_good_the_signature()
        {
            byte[] nonce = AuthChallenge.NewNonce();
            using var me = TheRealClient();
            byte[] sig = me.SignData(nonce, HashAlgorithmName.SHA256);
            Assert.That(AuthChallenge.TryAdmit(Token, Pub, nonce, sig, Exp, out _, out var err), Is.False);
            Assert.That(err, Does.Contain("expired"));
        }

        [Test]
        public void malformed_challenge_material_is_refused_without_throwing()
        {
            using var me = TheRealClient();
            byte[] nonce = AuthChallenge.NewNonce();
            byte[] sig = me.SignData(nonce, HashAlgorithmName.SHA256);

            Assert.That(AuthChallenge.TryAdmit(Token, Pub, new byte[8], sig, Now, out _, out _), Is.False, "short nonce");
            Assert.That(AuthChallenge.TryAdmit(Token, Pub, null, sig, Now, out _, out _), Is.False, "null nonce");
            Assert.That(AuthChallenge.TryAdmit(Token, Pub, nonce, new byte[10], Now, out _, out _), Is.False, "short signature");
            Assert.That(AuthChallenge.TryAdmit(Token, Pub, nonce, null, Now, out _, out _), Is.False, "null signature");
            Assert.That(AuthChallenge.TryAdmit("nonsense", Pub, nonce, sig, Now, out _, out _), Is.False, "junk token");
        }

        /// A nonce a server could guess is not a challenge. This cannot prove randomness, but it does catch
        /// the implementation that returns a constant, a counter, or a zeroed buffer.
        [Test]
        public void nonces_are_not_predictable_or_repeated()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 200; i++)
            {
                byte[] n = AuthChallenge.NewNonce();
                Assert.That(n.Length, Is.EqualTo(AuthChallenge.NonceBytes));
                Assert.That(Array.TrueForAll(n, b => b == 0), Is.False, "an all-zero nonce");
                Assert.That(seen.Add(Convert.ToBase64String(n)), Is.True, "NewNonce repeated itself");
            }
        }

        [Test]
        public void every_refusal_says_why()
        {
            using var me = TheRealClient();
            byte[] nonce = AuthChallenge.NewNonce();
            using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            foreach (var (tok, sig, label) in new[]
            {
                (Token, thief.SignData(nonce, HashAlgorithmName.SHA256), "wrong key"),
                ("nonsense", me.SignData(nonce, HashAlgorithmName.SHA256), "junk token"),
            })
            {
                AuthChallenge.TryAdmit(tok, Pub, nonce, sig, Now, out _, out var err);
                Assert.That(err, Is.Not.Null.And.Not.Empty, $"silent refusal for {label}");
            }
        }
    }
}
