using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // B10 (SP/MP-unify) at the L0 layer: the player-appearance fields appended to the combat block (7 worn
    // clothing ids + held item + stance) round-trip full/delta to exact StateHash parity through the real wire,
    // so a joiner's RemotePlayers puppets dress from the replica. Appended to an EXISTING system (SystemId 2), so
    // this also guards that the combat block still round-trips (health/kills/deaths unaffected).
    [TestFixture]
    public class PlayerAppearanceReplicationTests
    {
        [Test]
        public void Appearance_RoundTripsOnTheCombatBlock_IncludingLateJoin()
        {
            var server = new PlayerCombatReplication();
            var h = new ReplicationHarness(20260720, new List<IReplicatedSystem> { server });
            var fromStart = h.AddClient(() => new List<IReplicatedSystem> { new PlayerCombatReplication() });

            // player 1: fully dressed, holding a gun, crouched; player 2: bare
            var p1 = server.ServerAdd(1, Vector3.zero, 30, h.Tick + 1);
            p1.WornShirt = 3; p1.WornPants = 209; p1.WornHat = 185; p1.WornBackpack = 253;
            p1.WornVest = 120; p1.WornMask = 44; p1.WornGlasses = 99;
            p1.HeldId = 4; p1.Stance = 2; p1.Health = 80; p1.Kills = 3;
            server.MarkDirty(p1, h.Tick + 1);
            server.ServerAdd(2, Vector3.zero, 0, h.Tick + 1);   // bare player
            h.Step(10);

            // a late joiner arrives, then p1 swaps a shirt + stands up (a delta on the appearance)
            var late = h.AddClient(() => new List<IReplicatedSystem> { new PlayerCombatReplication() }, "late");
            p1.WornShirt = 7; p1.Stance = 0;
            server.MarkDirty(p1, h.Tick + 1);
            h.Step(20);

            ulong sh = server.StateHash();
            var cA = (PlayerCombatReplication)fromStart.Systems[0];
            var cB = (PlayerCombatReplication)late.Systems[0];
            Assert.That(cA.StateHash(), Is.EqualTo(sh), $"from-start replica == server ({h.Net.SeedInfo})");
            Assert.That(cB.StateHash(), Is.EqualTo(sh), $"late joiner == server ({h.Net.SeedInfo})");
            Assert.That(cA.TryGet(1, out var r1), Is.True);
            Assert.That(r1.WornShirt, Is.EqualTo((ushort)7), "the swapped shirt id replicated (delta)");
            Assert.That(r1.WornPants, Is.EqualTo((ushort)209));
            Assert.That(r1.WornBackpack, Is.EqualTo((ushort)253));
            Assert.That(r1.WornHat, Is.EqualTo((ushort)185));
            Assert.That(r1.HeldId, Is.EqualTo((ushort)4), "the held item id replicated");
            Assert.That(r1.Stance, Is.EqualTo((byte)0), "the stance replicated");
            Assert.That(r1.Health, Is.EqualTo((byte)80), "health still round-trips alongside the new fields");
            Assert.That(r1.Kills, Is.EqualTo((ushort)3), "kills unaffected by the appended block");
            Assert.That(cA.TryGet(2, out var r2), Is.True);
            Assert.That(r2.WornShirt, Is.EqualTo((ushort)0), "the bare player has no clothing");
        }
    }
}
