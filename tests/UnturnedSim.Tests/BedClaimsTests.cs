using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests for bed ownership and respawn selection. Every one of these is a case you would otherwise
    // have to verify by dying repeatedly in a live world.
    [TestFixture]
    public class BedClaimsTests
    {
        const ulong Me = 111, Rival = 222;

        static BedClaims ThreeBeds()
        {
            var b = new BedClaims();
            b.Register(1, new Vector3(10f, 0f, 0f));
            b.Register(2, new Vector3(20f, 0f, 0f));
            b.Register(3, new Vector3(30f, 0f, 0f));
            return b;
        }

        [Test]
        public void With_No_Bed_You_Have_No_Spawn_And_Fall_Back_To_The_Map()
        {
            var beds = ThreeBeds();
            Assert.That(beds.TryGetSpawn(Me, out _, out _), Is.False);
        }

        [Test]
        public void Claiming_A_Bed_Makes_It_Your_Spawn()
        {
            var beds = ThreeBeds();
            Assert.That(beds.Claim(2, Me, 0), Is.True);
            Assert.That(beds.TryGetSpawn(Me, out var pos, out _), Is.True);
            Assert.That(pos.x, Is.EqualTo(20f));
        }

        [Test]
        public void You_Only_Ever_Have_One_Bed()
        {
            // The rule that stops a player seeding spawn points across the map.
            var beds = ThreeBeds();
            beds.Claim(1, Me, 0);
            beds.Claim(3, Me, 10);

            Assert.That(beds.IsClaimed(1), Is.False, "the old bed should have been released");
            Assert.That(beds.OwnerOf(3), Is.EqualTo(Me));
            Assert.That(beds.TryGetSpawn(Me, out var pos, out _), Is.True);
            Assert.That(pos.x, Is.EqualTo(30f));
        }

        [Test]
        public void You_Cannot_Take_Someone_Elses_Bed()
        {
            var beds = ThreeBeds();
            Assert.That(beds.Claim(1, Rival, 0), Is.True);
            Assert.That(beds.CanClaim(1, Me, 10), Is.False);
            Assert.That(beds.Claim(1, Me, 10), Is.False);
            Assert.That(beds.OwnerOf(1), Is.EqualTo(Rival), "a claim that fails must not half-apply");
        }

        [Test]
        public void A_Contested_Bed_Cannot_Be_Flipped_Every_Frame()
        {
            var beds = ThreeBeds();
            beds.Claim(1, Me, 100.0);
            beds.Unclaim(Me, 100.0);
            Assert.That(beds.CanClaim(1, Rival, 100.1), Is.False, "still settling");
            Assert.That(beds.CanClaim(1, Rival, 100.0 + BedClaims.ClaimCooldown + 0.01), Is.True);
        }

        [Test]
        public void Re_Claiming_Your_Own_Bed_Is_Allowed_And_Idempotent()
        {
            var beds = ThreeBeds();
            beds.Claim(1, Me, 0);
            Assert.That(beds.Claim(1, Me, 10), Is.True);
            Assert.That(beds.OwnerOf(1), Is.EqualTo(Me));
            Assert.That(beds.TryGetOwnedBedId(Me, out int id), Is.True);
            Assert.That(id, Is.EqualTo(1));
        }

        [Test]
        public void Destroying_A_Bed_Takes_Its_Owners_Spawn_With_It()
        {
            // This is what blowing up a base is FOR.
            var beds = ThreeBeds();
            beds.Claim(2, Me, 0);
            Assert.That(beds.Remove(2), Is.True);
            Assert.That(beds.TryGetSpawn(Me, out _, out _), Is.False);
            Assert.That(beds.TryGetOwnedBedId(Me, out _), Is.False, "a dangling owner index would resurrect a dead bed");
        }

        [Test]
        public void Removing_An_Unclaimed_Bed_Disturbs_Nobody()
        {
            var beds = ThreeBeds();
            beds.Claim(1, Me, 0);
            Assert.That(beds.Remove(3), Is.True);
            Assert.That(beds.TryGetSpawn(Me, out var pos, out _), Is.True);
            Assert.That(pos.x, Is.EqualTo(10f));
        }

        [Test]
        public void Unclaiming_Frees_The_Bed_For_Someone_Else()
        {
            var beds = ThreeBeds();
            beds.Claim(1, Me, 0);
            Assert.That(beds.Unclaim(Me, 0), Is.True);
            Assert.That(beds.IsClaimed(1), Is.False);
            Assert.That(beds.Claim(1, Rival, 10), Is.True);
        }

        [Test]
        public void Claims_Against_A_Missing_Bed_Fail_Rather_Than_Throw()
        {
            var beds = ThreeBeds();
            Assert.That(beds.CanClaim(99, Me, 0), Is.False);
            Assert.That(beds.Claim(99, Me, 0), Is.False);
            Assert.That(beds.Remove(99), Is.False);
        }

        [Test]
        public void A_Nobody_Cannot_Claim()
        {
            var beds = ThreeBeds();
            Assert.That(beds.CanClaim(1, 0UL, 0), Is.False, "player id 0 means unowned, not a player");
        }

        // ---- Adopt: applying a decision made somewhere else (a server telling a replica how the world is) ----

        [Test]
        public void Adopt_Applies_An_Ownership_Decision_Without_Re_Judging_It()
        {
            // The whole point: Claim would REFUSE this (the settle window has not passed on this clock),
            // and a replica that refuses what the server already did is a replica that is simply wrong.
            var beds = ThreeBeds();
            beds.Claim(1, Rival, 100);
            Assert.That(beds.CanClaim(1, Me, 100), Is.False, "Claim would refuse -- someone else's bed, mid-cooldown");
            Assert.That(beds.Adopt(1, Me, 100), Is.True);
            Assert.That(beds.OwnerOf(1), Is.EqualTo(Me));
            Assert.That(beds.TryGetSpawn(Me, out _, out _), Is.True);
            Assert.That(beds.TryGetSpawn(Rival, out _, out _), Is.False, "the loser's spawn goes with the bed");
        }

        [Test]
        public void Adopt_Still_Enforces_One_Bed_Per_Player()
        {
            // Structural, not a rule that may be waived: the owner index maps to a single id, so adopting a
            // second bed for the same player must free the first or the table contradicts itself.
            var beds = ThreeBeds();
            Assert.That(beds.Adopt(1, Me, 0), Is.True);
            Assert.That(beds.Adopt(2, Me, 1), Is.True);
            Assert.That(beds.OwnerOf(1), Is.EqualTo(0UL), "the first bed came free");
            Assert.That(beds.OwnerOf(2), Is.EqualTo(Me));
            Assert.That(beds.TryGetOwnedBedId(Me, out int held), Is.True);
            Assert.That(held, Is.EqualTo(2));
        }

        [Test]
        public void Adopting_Owner_Zero_Releases_The_Bed()
        {
            var beds = ThreeBeds();
            beds.Adopt(1, Me, 0);
            Assert.That(beds.Adopt(1, 0UL, 1), Is.True);
            Assert.That(beds.IsClaimed(1), Is.False);
            Assert.That(beds.TryGetSpawn(Me, out _, out _), Is.False);
            Assert.That(beds.TryGetOwnedBedId(Me, out _), Is.False, "the owner index has to let go too");
        }

        [Test]
        public void Adopting_The_Same_Owner_Twice_Changes_Nothing()
        {
            // Events can arrive re-ordered or replayed; adopting must be idempotent or a duplicate would
            // release the bed it is confirming.
            var beds = ThreeBeds();
            beds.Adopt(1, Me, 0);
            Assert.That(beds.Adopt(1, Me, 1), Is.True);
            Assert.That(beds.OwnerOf(1), Is.EqualTo(Me));
            Assert.That(beds.TryGetOwnedBedId(Me, out int held) && held == 1, Is.True);
        }

        [Test]
        public void Adopting_An_Unknown_Bed_Fails_Rather_Than_Throwing()
        {
            var beds = ThreeBeds();
            Assert.That(beds.Adopt(99, Me, 0), Is.False);
            Assert.That(beds.TryGetOwnedBedId(Me, out _), Is.False);
        }
    }
}
