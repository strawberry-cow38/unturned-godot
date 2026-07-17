using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;
using UnturnedNet.Tests.Mocks;

namespace UnturnedNet.Tests
{
    // Hardening Part C: runtime desync detection. The server (opt-in: EnableSyncCheck) appends a rolling
    // per-system StateHash block to every Nth snapshot; the client compares each hash against its replica
    // right after applying that snapshot and raises DesyncDetected once the mismatch confirms over
    // DesyncConfirmChecks consecutive checks. Checked systems are the globally-mirrored ones only --
    // owner-only and relevancy-filtered systems differ per client by design and are never hashed.
    [TestFixture]
    public class DesyncDetectionTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        const ushort GEN = TransactionalFixtures.GeneratorId;

        // ---- composer/applier unit level (mock systems, no transport) ----

        [Test]
        public void SyncCheckBlock_FollowsTheCadence_AndPassesOnParity()
        {
            var serverSys = new MockEntitySystem(1);
            var ids = new NetIdMinter();
            serverSys.Set(ids.Mint(), new Vector3(1f, 0f, 2f), 90f, 7, tick: 1);
            var composer = new SnapshotComposer(new IReplicatedSystem[] { serverSys });
            composer.EnableSyncCheck(5, 1);

            var clientSys = new MockEntitySystem(1);
            var applier = new SnapshotApplier(new IReplicatedSystem[] { clientSys });

            for (long tick = 1; tick <= 10; tick++)
            {
                var snapshot = composer.Compose(tick, clientPlayerId: 7, viewPos: Vector3.zero);
                Assert.That(applier.Apply(snapshot, snapshot.Length), Is.True);
            }

            Assert.That(composer.Diag.SyncCheckBlocksWritten, Is.EqualTo(2), "hash block rides ticks 5 and 10 only");
            Assert.That(applier.Diag.SyncChecksPassed, Is.EqualTo(2), "replica in parity: both checks pass");
            Assert.That(applier.Diag.SyncChecksFailed, Is.Zero);
        }

        [Test]
        public void SyncCheck_IsPureOptIn_NothingWrittenWhenDisabled()
        {
            var serverSys = new MockEntitySystem(1);
            var composer = new SnapshotComposer(new IReplicatedSystem[] { serverSys });   // never enabled
            var clientSys = new MockEntitySystem(1);
            var applier = new SnapshotApplier(new IReplicatedSystem[] { clientSys });

            for (long tick = 1; tick <= 10; tick++)
            {
                var snapshot = composer.Compose(tick, 7, Vector3.zero);
                applier.Apply(snapshot, snapshot.Length);
            }
            Assert.That(composer.Diag.SyncCheckBlocksWritten, Is.Zero);
            Assert.That(applier.Diag.SyncChecksPassed + applier.Diag.SyncChecksFailed, Is.Zero);
        }

        [Test]
        public void TamperedReplica_FailsTheCheck_AndReportsTheSystem()
        {
            var serverSys = new MockEntitySystem(1);
            var ids = new NetIdMinter();
            serverSys.Set(ids.Mint(), new Vector3(1f, 0f, 2f), 90f, 7, tick: 1);
            var composer = new SnapshotComposer(new IReplicatedSystem[] { serverSys });
            composer.EnableSyncCheck(1, 1);

            var clientSys = new MockEntitySystem(1);
            var applier = new SnapshotApplier(new IReplicatedSystem[] { clientSys }) { DesyncConfirmChecks = 2 };
            var reports = new System.Collections.Generic.List<DesyncReport>();
            applier.DesyncDetected += reports.Add;

            var s1 = composer.Compose(1, 7, Vector3.zero);
            applier.Apply(s1, s1.Length);
            composer.SetClientBaseline(7, 1);   // ack -> the follow-ups are DELTAS (a full would wipe the tamper)
            Assert.That(applier.Diag.SyncChecksPassed, Is.EqualTo(1), "healthy before the tamper");

            // corrupt the replica AFTER applying -- from here on its hash can't match the server's
            clientSys.Set(new NetId(9999), new Vector3(50f, 0f, 50f), 0f, 1, tick: 1);

            var s2 = composer.Compose(2, 7, Vector3.zero);
            applier.Apply(s2, s2.Length);
            composer.SetClientBaseline(7, 2);
            Assert.That(reports, Is.Empty, "one mismatch is not yet confirmed (threshold 2 absorbs wire races)");

            var s3 = composer.Compose(3, 7, Vector3.zero);
            applier.Apply(s3, s3.Length);
            Assert.That(reports.Count, Is.EqualTo(1), "second consecutive mismatch confirms the desync");
            Assert.That(reports[0].SystemId, Is.EqualTo(1), "the report names the diverged system");
            Assert.That(reports[0].ServerTick, Is.EqualTo(3));
            Assert.That(reports[0].ServerHash, Is.Not.EqualTo(reports[0].ClientHash));
        }

        // ---- full-stack level (NetWorldServer/NetWorldClient over MemTransport) ----

        [Test]
        public void SimultaneousJoiners_FullyReplicate_ToEachOther()
        {
            // Regression for the bug the desync detector caught on its first run: two Connects landing in
            // the SAME server tick meant the first joiner's join snapshot was composed mid-tick (inside
            // PeerConnected), before the second joiner's entities existed -- and since those entities were
            // stamped with that very tick, the first client's ack of it suppressed them from every later
            // delta. Each joiner then permanently lacked the other's combat entity. The join snapshot now
            // composes in TickReplication, after ALL of the tick's mutation.
            var h = new TransactionalHarness(31005).Connected("a", "b");   // both Connect before any step
            h.Step(30);

            var a = h.Clients[0];
            var b = h.Clients[1];
            Assert.That(a.CombatState.Count, Is.EqualTo(2), $"a sees both combat entities (seed={h.Net.Seed})");
            Assert.That(b.CombatState.Count, Is.EqualTo(2), $"b sees both combat entities (seed={h.Net.Seed})");
            Assert.That(a.CombatState.StateHash(), Is.EqualTo(h.Server.CombatState.StateHash()), "a combat parity");
            Assert.That(b.CombatState.StateHash(), Is.EqualTo(h.Server.CombatState.StateHash()), "b combat parity");
            Assert.That(a.Players.StateHash(), Is.EqualTo(h.Server.Players.StateHash()), "a players parity");
        }

        [Test]
        public void HealthyRun_WithMovementAndTransactions_NeverFalseAlarms()
        {
            var h = new TransactionalHarness(31001);
            h.Server.EnableSyncCheck(10);
            h.Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            int desyncs = 0;
            a.DesyncDetected += _ => desyncs++;
            b.DesyncDetected += _ => desyncs++;

            h.Grant(a.PlayerId, new Item(GEN));
            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);   // reliable events in the mix
            long t = 0;
            h.Step(400, () =>
            {
                t++;
                a.SendMoveInput(0f, 1f, (t * 3) % 360);   // constant world churn
                b.SendMoveInput(1f, 0f, (t * 7) % 360);
            });

            Assert.That(a.Applier.Diag.SyncChecksPassed, Is.GreaterThan(10), $"checks actually ran (seed={h.Net.Seed})");
            Assert.That(b.Applier.Diag.SyncChecksPassed, Is.GreaterThan(10));
            Assert.That(a.Applier.Diag.SyncChecksFailed + b.Applier.Diag.SyncChecksFailed, Is.Zero,
                "a clean in-order run never even single-fails a check");
            Assert.That(desyncs, Is.Zero);
        }

        [Test]
        public void HealthyRun_UnderLossAndReorder_NeverConfirmsADesync()
        {
            var adverse = new SDG.NetTransport.Mem.FaultyLinkConfig { LossProbability = 0.2, ReorderJitterTicks = 2, LatencyTicks = 1 };
            var h = new TransactionalHarness(31002, adverse,
                new SDG.NetTransport.Mem.FaultyLinkConfig { LossProbability = 0.2, ReorderJitterTicks = 2, LatencyTicks = 1 });
            h.Server.EnableSyncCheck(10);
            h.Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            int desyncs = 0;
            a.DesyncDetected += _ => desyncs++;
            b.DesyncDetected += _ => desyncs++;

            h.Grant(a.PlayerId, new Item(GEN));
            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);
            long t = 0;
            h.Step(600, () =>
            {
                t++;
                a.SendMoveInput(0f, 1f, (t * 3) % 360);
                b.SendMoveInput(1f, 0f, (t * 7) % 360);
            });

            Assert.That(a.Applier.Diag.SyncChecksPassed, Is.GreaterThan(5), $"checks survive the loss (seed={h.Net.Seed})");
            Assert.That(desyncs, Is.Zero, $"loss/reorder must never CONFIRM a desync (seed={h.Net.Seed})");
        }

        [Test]
        public void ForcedDivergence_IsDetected_AndOnlyOnTheDivergedClient()
        {
            var h = new TransactionalHarness(31003);
            h.Server.EnableSyncCheck(10);
            h.Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            var aReports = new System.Collections.Generic.List<DesyncReport>();
            int bDesyncs = 0;
            a.DesyncDetected += aReports.Add;
            b.DesyncDetected += _ => bDesyncs++;

            h.Step(50);   // steady state, checks passing
            Assert.That(a.Applier.Diag.SyncChecksFailed, Is.Zero);

            // force the divergence: a phantom deployable exists only in a's replica (the shape of a real
            // replication bug -- client state the server doesn't have)
            a.Deployables.ApplyPlaced(new DeployablePlacedEvent
            {
                NetId = 99999, DefId = GEN, OwnerPlayerId = a.PlayerId,
                Pos = new Vector3(5f, 0f, 5f), YawDegrees = 0f,
            }, a.Applier.LastAppliedServerTick);

            Assert.That(h.StepUntil(() => aReports.Count > 0, 100), Is.True,
                $"the divergence is confirmed within a few check intervals (seed={h.Net.Seed})");
            Assert.That(aReports[0].SystemId, Is.EqualTo(ReplicationIds.SystemDeployables),
                "the report names the diverged system");
            Assert.That(aReports[0].ServerHash, Is.Not.EqualTo(aReports[0].ClientHash));
            Assert.That(bDesyncs, Is.Zero, "the healthy client never alarms");
        }
    }

    // Hardening review L1: a client acking a tick the server never composed (e.g. 0xFFFFFFFF) must not
    // poison its own baseline -- the composer clamps future acks at the source.
    [TestFixture]
    public class BaselineAckClampTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        [Test]
        public void FutureBaselineAck_IsRejected_AtTheComposer()
        {
            var composer = new SnapshotComposer(new IReplicatedSystem[] { new MockEntitySystem(1) });
            composer.CurrentTick = () => 100;

            composer.SetClientBaseline(1, 4294967295L);   // uint.MaxValue, the L1 poison value
            Assert.That(composer.GetClientBaseline(1), Is.Zero, "the future ack was refused");
            Assert.That(composer.Diag.FutureBaselineAcksRejected, Is.EqualTo(1));

            composer.SetClientBaseline(1, 100);   // the current tick itself is a legal ack
            Assert.That(composer.GetClientBaseline(1), Is.EqualTo(100));
        }

        [Test]
        public void PoisonedAck_DoesNotStarveTheClientsDeltas()
        {
            var h = new TransactionalHarness(31004).Connected("victim", "mover");
            var victim = h.Clients[0];
            var mover = h.Clients[1];

            h.Step(20);   // steady snapshot flow

            // the victim's own (buggy/hostile) stack acks a tick from the far future
            victim.Session.SendUnreliableSequenced(NetMessagePak.Pack(SnapshotComposer.AckCommandId,
                w => w.WriteUInt32(0xFFFFFFFF)));
            h.Step(5);
            Assert.That(h.Server.Composer.Diag.FutureBaselineAcksRejected, Is.GreaterThan(0),
                $"the poisoned ack reached the composer and was refused (seed={h.Net.Seed})");

            // the world keeps changing; the victim must keep receiving real deltas about it
            h.Step(100, () => mover.SendMoveInput(0f, 1f, 90f));
            h.Step(10, () => mover.SendMoveInput(0f, 0f, 90f));   // held-input model: an explicit stop
            h.Step(60);   // decelerate + settle + replicate the final state

            Assert.That(victim.Players.StateHash(), Is.EqualTo(h.Server.Players.StateHash()),
                $"victim replica still tracks the server after the poisoned ack (seed={h.Net.Seed})");
        }
    }
}
