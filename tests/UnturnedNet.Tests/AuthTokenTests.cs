using System;
using System.Security.Cryptography;
using NUnit.Framework;
using UnturnedNet;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// Cross-runtime golden. The fixtures below were produced by stmauth's REAL minting path (node,
    /// server.js mintToken) against a throwaway test key -- not hand-built here, because a fixture this
    /// file constructs only proves .NET agrees with itself, which is exactly the bug that cannot happen.
    /// The bug that CAN happen is node and .NET disagreeing, and only a token node actually emitted
    /// can catch it.
    ///
    /// ⚠ TIME IS PINNED TO THE TOKEN, never to the clock. Verifying "now" against a frozen fixture means
    /// the accept tests quietly turn into expiry tests the day the token lapses -- passing for years and
    /// then failing for a reason that has nothing to do with the code.
    /// </summary>
    public class AuthTokenTests
    {
        // Public half only. The production signing key is on the box at 0600 and is not in this repo.
        const string ServicePubB64 =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE0xv9U9iECWsXHRYD1X1MF0VqfNbDrSURQrUi5UzUTkVGvzC9FTfapQ20+RfIOcj4QRjqpKP+YjW6L+aG5bTr5g==";

        const string Token =
            "v1.eyJzaWQiOiI3NjU2MTE5ODAxMjM0NTY3OCIsInBrIjoiUVVKRFJFVkdSMGhKU2t0TVRVNVBVRkZTVTFSVlZsZFlXVm93TVRJek5EVTJOemc1IiwiaWF0IjoxNzg5NTc2NDIwLCJleHAiOjE3OTIxNjg0MjB9.yo1ruzEJjd98_57Yxfz5vuAS7A2aRa9YUxA4WqB2M-1f8PjT5ZUFAvyzNeHfCiqwQJQhD4VLIiShaEtSGUH9Mw";

        /// The SAME payload, signed the way node does it if nobody thinks about encodings: DER, 71 bytes.
        const string DerSignedToken =
            "v1.eyJzaWQiOiI3NjU2MTE5ODAxMjM0NTY3OCIsInBrIjoiUVVKRFJFVkdSMGhKU2t0TVRVNVBVRkZTVTFSVlZsZFlXVm93TVRJek5EVTJOemc1IiwiaWF0IjoxNzg5NTc2NDIwLCJleHAiOjE3OTIxNjg0MjB9.MEUCIFEPj9SyNCKr6rrcd0TASQOexMt-7EOmoQ1sug0ECeGKAiEA4migsFlXnDWPnkhox0ExrcLTdUvSV_MrDj4jZsPZpUg";

        const long Iat = 1789576420;
        const long Exp = 1792168420;
        const long WellInside = Iat + 60;

        static byte[] Pub => Convert.FromBase64String(ServicePubB64);

        [Test]
        public void a_token_node_really_minted_verifies_in_dotnet()
        {
            bool ok = AuthToken.TryVerify(Token, Pub, WellInside, out var sid, out var pk, out var err);
            Assert.That(ok, Is.True, $"the cross-runtime path is broken: {err}");
            Assert.That(sid, Is.EqualTo("76561198012345678"));
            Assert.That(pk, Is.EqualTo("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5"));
        }

        /// The interop trap, asserted rather than assumed. If someone drops `dsaEncoding: 'ieee-p1363'`
        /// from the service, every token it mints looks like this one -- and the whole system fails as
        /// "nobody can log in", which sends you looking at Steam, at Caddy, at the handshake, anywhere
        /// but at two libraries picking different defaults.
        [Test]
        public void a_der_signature_is_refused_and_says_so()
        {
            bool ok = AuthToken.TryVerify(DerSignedToken, Pub, WellInside, out _, out _, out var err);
            Assert.That(ok, Is.False, "a DER-encoded signature was ACCEPTED -- the encodings are not pinned");
            Assert.That(err, Does.Contain("64"), $"the error should name the length problem, got: {err}");
        }

        [Test]
        public void expiry_is_enforced_at_the_boundary_not_a_day_later()
        {
            Assert.That(AuthToken.TryVerify(Token, Pub, Exp - 1, out _, out _, out _), Is.True,
                        "one second before expiry must still be valid");
            Assert.That(AuthToken.TryVerify(Token, Pub, Exp, out _, out _, out var atExp), Is.False,
                        "exp itself must NOT be valid -- the check is >=, and an off-by-one here is a day of free access");
            Assert.That(atExp, Does.Contain("expired"));
        }

        [Test]
        public void tampering_with_the_payload_is_caught()
        {
            var p = Token.Split('.');
            char c = p[1][10];
            string swapped = p[1].Substring(0, 10) + (c == 'A' ? 'B' : 'A') + p[1].Substring(11);
            Assert.That(AuthToken.TryVerify($"{p[0]}.{swapped}.{p[2]}", Pub, WellInside, out _, out _, out _),
                        Is.False, "a changed payload still verified");
        }

        [Test]
        public void tampering_with_the_signature_is_caught()
        {
            var p = Token.Split('.');
            char c = p[2][10];
            string swapped = p[2].Substring(0, 10) + (c == 'A' ? 'B' : 'A') + p[2].Substring(11);
            Assert.That(AuthToken.TryVerify($"{p[0]}.{p[1]}.{swapped}", Pub, WellInside, out _, out _, out _),
                        Is.False, "a changed signature still verified");
        }

        [Test]
        public void a_different_service_key_does_not_verify()
        {
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.That(AuthToken.TryVerify(Token, other.ExportSubjectPublicKeyInfo(), WellInside, out _, out _, out _),
                        Is.False, "any P-256 key verified this token -- the signature is not actually being checked");
        }

        [Test]
        public void malformed_input_is_refused_without_throwing()
        {
            foreach (var bad in new[] { "", "nonsense", "a.b", "a.b.c.d", "v2.x.y", "v1.!!!.???" })
            {
                Assert.DoesNotThrow(() => AuthToken.TryVerify(bad, Pub, WellInside, out _, out _, out _),
                                    $"threw on \"{bad}\" -- a malformed token is an ordinary event on a public port");
                Assert.That(AuthToken.TryVerify(bad, Pub, WellInside, out _, out _, out _), Is.False, bad);
            }
        }

        /// Every refusal names its own cause. A verifier that refuses everything with one blank message is
        /// indistinguishable from a verifier that is simply broken, which is the failure this whole
        /// feature has been one step away from all day.
        [Test]
        public void every_refusal_explains_itself()
        {
            foreach (var bad in new[] { "", "nonsense", "v2.a.b", DerSignedToken })
            {
                AuthToken.TryVerify(bad, Pub, WellInside, out _, out _, out var err);
                Assert.That(err, Is.Not.Null.And.Not.Empty, $"silent refusal for \"{bad}\"");
            }
        }
    }
}
