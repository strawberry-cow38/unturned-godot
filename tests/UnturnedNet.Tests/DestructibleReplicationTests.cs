using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Destructible props (rubble) at the L0 layer on the full NetWorldServer/NetWorldClient stack over
    // deterministic MemTransport: the ServerDestructibles health pool + Rubble_Reset respawn clock driving
    // the DestructibleReplication(16) alive-bitmap -- damage accrual, destroy, respawn, the Destroyed/Restored
    // events, join-bitmap history for a late joiner, and the indestructible (no-metadata) slot.
    [TestFixture]
    public class DestructibleReplicationTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        [Test]
        public void Damage_Accrues_ThenDestroys_ReplicatesAndRespawns()
        {
            var h = new TransactionalHarness(90901);
            long boot = h.Server.Session.CurrentTick;
            h.Server.DestructibleHost.ServerInit(40, boot);
            h.Server.DestructibleHost.SetMeta(5, maxHealth: 50f, resetTicks: 5);   // dies in 2 hits of 30, respawns after 5 ticks
            h.Connected("a");
            var a = h.Clients[0];
            int destroyed = 0, restored = 0;
            a.ObjectDestroyed += _ => destroyed++;
            a.ObjectRestored += _ => restored++;

            h.StepUntil(() => a.Destructibles.Count == 40);
            Assert.That(a.Destructibles.Count, Is.EqualTo(40), "the join full carried the whole rubble bitmap");
            Assert.That(a.Destructibles.AliveCount, Is.EqualTo(40), "everything intact at boot");

            // first hit: 50 -> 20, survives
            Assert.That(h.Server.DestructibleHost.DamageObject(5, 30f, h.Server.Session.CurrentTick), Is.False, "first hit doesn't break it");
            Assert.That(h.Server.Destructibles.IsAlive(5), Is.True, "still standing after one hit");

            // second hit: 20 -> 0, breaks
            Assert.That(h.Server.DestructibleHost.DamageObject(5, 30f, h.Server.Session.CurrentTick), Is.True, "second hit crosses 0 -> destroyed");
            h.StepUntil(() => !a.Destructibles.IsAlive(5));
            Assert.That(a.Destructibles.IsAlive(5), Is.False, "the broken prop's bit flipped on the replica");
            Assert.That(destroyed, Is.EqualTo(1), "the ObjectDestroyed fact fired once");
            Assert.That(a.Destructibles.StateHash(), Is.EqualTo(h.Server.Destructibles.StateHash()), "bitmap parity after break");

            // idempotent: a hit on an already-broken prop does nothing
            Assert.That(h.Server.DestructibleHost.DamageObject(5, 30f, h.Server.Session.CurrentTick), Is.False, "already broken -- no double break");

            // respawn: the reset timer elapses, Tick() restores it
            long destroyedTick = h.Server.Session.CurrentTick;
            h.StepUntil(() => h.Server.Session.CurrentTick > destroyedTick + 6);
            h.Server.DestructibleHost.Tick(h.Server.Session.CurrentTick);   // the sim step DestructibleNetSync normally drives
            h.StepUntil(() => a.Destructibles.IsAlive(5));
            Assert.That(a.Destructibles.IsAlive(5), Is.True, "the prop respawned on the replica");
            Assert.That(restored, Is.EqualTo(1), "the ObjectRestored fact fired");
        }

        [Test]
        public void IndestructibleSlot_IgnoresDamage()
        {
            var h = new TransactionalHarness(90902);
            h.Server.DestructibleHost.ServerInit(10, h.Server.Session.CurrentTick);
            // index 3 gets NO metadata (an out-of-season holiday reservation) -> maxHealth 0 -> indestructible
            Assert.That(h.Server.DestructibleHost.DamageObject(3, 9999f, h.Server.Session.CurrentTick), Is.False, "no-metadata slot can't be damaged");
            Assert.That(h.Server.Destructibles.IsAlive(3), Is.True, "indestructible slot stays intact");
            Assert.That(h.Server.DestructibleHost.DamageObject(99, 10f, h.Server.Session.CurrentTick), Is.False, "out-of-range index is a safe no-op");
        }

        [Test]
        public void LateJoiner_SeesBrokenHistory_InTheJoinBitmap()
        {
            var h = new TransactionalHarness(90903);
            h.Server.DestructibleHost.ServerInit(64, h.Server.Session.CurrentTick);
            h.Server.DestructibleHost.SetMeta(9, 10f, 100);
            h.Server.DestructibleHost.SetMeta(63, 10f, 100);
            h.Connected("a");
            h.Server.DestructibleHost.DamageObject(9, 10f, h.Server.Session.CurrentTick);
            h.Server.DestructibleHost.DamageObject(63, 10f, h.Server.Session.CurrentTick);

            var late = h.AddClient("late");
            h.StepUntil(() => late.State == NetSessionState.Connected && late.Destructibles.Count == 64);
            Assert.That(late.Destructibles.IsAlive(9), Is.False, "join bitmap: broken index 9");
            Assert.That(late.Destructibles.IsAlive(63), Is.False, "join bitmap: broken index 63");
            Assert.That(late.Destructibles.AliveCount, Is.EqualTo(62));
            Assert.That(late.Destructibles.StateHash(), Is.EqualTo(h.Server.Destructibles.StateHash()), "join bitmap parity");
        }
    }
}
