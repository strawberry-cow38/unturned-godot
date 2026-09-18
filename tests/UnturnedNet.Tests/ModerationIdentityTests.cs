using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// Bans matched on a verified SteamId, which is what ServerModeration's own class comment said to do
    /// "when identities arrive": match on those, keep address and name as fallbacks.
    ///
    /// ⚠ The semantics are the OPPOSITE of the player-save re-key in the same branch. A save must never be
    /// reachable by a weaker handle -- breadth there is impersonation. A ban should be reachable by ANY handle
    /// that still fits -- breadth here is the point. Same ingredients, opposite rule.
    /// </summary>
    public class ModerationIdentityTests
    {
        const string Sid = "76561198012345678";
        const string OtherSid = "76561190000000000";
        const uint IpA = 0x0A000001u, IpB = 0x0A000002u;
        const long Now = 1_700_000_000;

        static ServerModeration WithSteamBan()
        {
            var m = new ServerModeration();
            m.Add(IpA, Sid, "evader", 0, "cheating", ModerationKind.Ban);
            return m;
        }

        [Test]
        public void a_steamid_ban_follows_the_account_through_a_new_address_and_a_new_name()
        {
            // The entire reason for re-keying: neither handle that used to be checked survives this.
            Assert.That(WithSteamBan().IsBanned(IpB, Sid, "a_brand_new_name", Now, out var hit), Is.True,
                        "changed IP and name walked straight through an identity ban");
            Assert.That(hit.Reason, Is.EqualTo("cheating"));
        }

        /// ⭐ The bug this is really guarding. An unguarded string compare makes "" == "" true, so ONE identity
        /// ban would bar every player who has not signed in -- which is everyone today. It would present as
        /// "the server stopped letting anyone in" right after someone banned a cheater.
        [Test]
        public void an_identity_ban_does_not_bar_everyone_who_has_no_identity()
        {
            var m = new ServerModeration();
            m.Add(0u, Sid, "", 0, "cheating", ModerationKind.Ban);   // no ip, no name: identity only
            Assert.That(m.IsBanned(IpB, "", "innocent_bystander", Now, out _), Is.False,
                        "a peer with no SteamId matched an identity ban -- \"\" == \"\" bars the whole server");
            Assert.That(m.IsBanned(IpB, OtherSid, "someone_else", Now, out _), Is.False,
                        "a DIFFERENT verified account matched it");
            Assert.That(m.IsBanned(IpB, Sid, "any_name", Now, out _), Is.True,
                        "and the actual account must still be caught");
        }

        [Test]
        public void a_legacy_ban_with_no_steamid_still_works_exactly_as_before()
        {
            var m = new ServerModeration();
            m.Add(IpA, "oldcheater", 0, "pre-identity ban", ModerationKind.Ban);   // the old 5-arg overload
            Assert.That(m.IsBanned(IpA, "", "someone", Now, out _), Is.True, "IP fallback stopped working");
            Assert.That(m.IsBanned(IpB, "", "oldcheater", Now, out _), Is.True, "name fallback stopped working");
            Assert.That(m.IsBanned(IpB, Sid, "unrelated", Now, out _), Is.False,
                        "a signed-in stranger was caught by a ban that names neither of their handles");
        }

        [Test]
        public void lifting_a_ban_by_identity_lifts_it()
        {
            var m = WithSteamBan();
            Assert.That(m.Remove(0u, Sid, ""), Is.EqualTo(1));
            Assert.That(m.IsBanned(IpA, Sid, "evader", Now, out _), Is.False);
        }

        /// Add() merges rows that already match, and must never shorten a sentence. Identity is a new way for
        /// two rows to be "the same person", so it has to participate in that merge or a timed identity ban
        /// would replace a permanent address ban and quietly free someone.
        [Test]
        public void a_timed_identity_ban_cannot_shorten_a_permanent_one()
        {
            var m = new ServerModeration();
            m.Add(IpA, Sid, "evader", 0, "permanent", ModerationKind.Ban);
            m.Add(IpA, Sid, "evader", Now + 600, "ten minutes", ModerationKind.Ban);
            Assert.That(m.IsBanned(IpA, Sid, "evader", Now, out var hit), Is.True);
            Assert.That(hit.IsPermanent, Is.True, "a 10-minute ban overwrote a permanent one");
            Assert.That(m.Count, Is.EqualTo(1), "the rows did not merge -- the same person is listed twice");
        }

        [Test]
        public void expiry_still_applies_to_identity_bans()
        {
            var m = new ServerModeration();
            m.Add(IpA, Sid, "evader", Now + 60, "timed", ModerationKind.Ban);
            Assert.That(m.IsBanned(IpA, Sid, "evader", Now, out _), Is.True);
            Assert.That(m.IsBanned(IpA, Sid, "evader", Now + 61, out _), Is.False, "an expired identity ban held");
        }

        [Test]
        public void the_shared_rule_is_what_all_three_operations_use()
        {
            // Matches() exists because this logic was copied into IsBanned, Add and Remove, and a fourth
            // handle would have meant a fourth place to forget. Assert the rule directly so a future handle
            // is added once.
            var e = new BanEntry { Ipv4 = IpA, SteamId = Sid, Name = "evader" };
            Assert.That(ServerModeration.Matches(e, 0u, Sid, ""), Is.True);
            Assert.That(ServerModeration.Matches(e, IpA, "", ""), Is.True);
            Assert.That(ServerModeration.Matches(e, 0u, "", "evader"), Is.True);
            Assert.That(ServerModeration.Matches(e, 0u, "", ""), Is.False, "matched a peer carrying no handles at all");
            Assert.That(ServerModeration.Matches(new BanEntry { SteamId = "" }, 0u, "", ""), Is.False,
                        "an empty entry matched an empty peer");
        }
    }
}
