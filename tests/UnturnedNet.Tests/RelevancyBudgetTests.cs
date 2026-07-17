using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;
using UnturnedNet.Tests.Mocks;

namespace UnturnedNet.Tests
{
    // Phase 8 interest management + byte budget (MP_PLAN §2.6), proven at the L0 layer: the relevancy
    // POLICY (distance rings + relevancy cells) filters per-client snapshot composition on the ViewPos hook
    // that has been in the signature since Phase 2 -- with NO wire-format change; and the composer's
    // per-client byte budget keeps every datagram under the cap while priority accumulators + per-system
    // baselines guarantee a budget-skipped system's state is never lost, only deferred.
    [TestFixture]
    public class RelevancyBudgetTests
    {
        // ---- relevancy: rings + cells (§2.6 "distance rings + the 19 zombie nav-pocket cells") ----

        [Test]
        public void FarEntity_AbsentFromFarClientsSnapshot_PresentInNearClients()
        {
            var zombies = new ZombieReplication { Interest = new InterestPolicy { RingRadius = 50f } };
            zombies.ServerSpawn(new NetId(1), 0, new Vector3(10f, 0f, 0f), tick: 1);
            zombies.ServerSpawn(new NetId(2), 0, new Vector3(500f, 0f, 0f), tick: 1);
            var composer = new SnapshotComposer(new IReplicatedSystem[] { zombies });

            var nearReplica = new ZombieReplication();
            var farReplica = new ZombieReplication();
            var nearApplier = new SnapshotApplier(new IReplicatedSystem[] { nearReplica });
            var farApplier = new SnapshotApplier(new IReplicatedSystem[] { farReplica });

            // client A stands at the origin, client B stands by the far zombie -- SAME server state,
            // different snapshots (this is the §4 Phase 8 relevancy test, verbatim)
            var snapA = composer.Compose(2, 1, new Vector3(0f, 0f, 0f));
            var snapB = composer.Compose(2, 2, new Vector3(500f, 0f, 0f));
            Assert.That(nearApplier.Apply(snapA, snapA.Length), Is.True);
            Assert.That(farApplier.Apply(snapB, snapB.Length), Is.True);

            Assert.That(nearReplica.TryGet(new NetId(1), out _), Is.True, "near zombie present for A");
            Assert.That(nearReplica.TryGet(new NetId(2), out _), Is.False, "far zombie ABSENT from A's snapshot");
            Assert.That(farReplica.TryGet(new NetId(2), out _), Is.True, "far zombie present for B (B stands next to it)");
            Assert.That(farReplica.TryGet(new NetId(1), out _), Is.False, "origin zombie absent from B's snapshot");
        }

        [Test]
        public void SharedRelevancyCell_GrantsRelevance_PastTheRing()
        {
            // cells stand in for the 19 PEI nav pockets: everything in x [400,600) is "town 7"
            int CellOf(Vector3 p) => p.x >= 400f && p.x < 600f ? 7 : -1;
            var policy = new InterestPolicy { RingRadius = 10f, CellOf = CellOf };

            Assert.That(policy.IsRelevant(new Vector3(450f, 0f, 0f), new Vector3(590f, 0f, 0f)), Is.True,
                        "same pocket -> relevant at 140 m, far past the 10 m ring (the whole town's horde stays visible)");
            Assert.That(policy.IsRelevant(new Vector3(450f, 0f, 0f), new Vector3(300f, 0f, 0f)), Is.False,
                        "different pocket + outside the ring -> not relevant");
            Assert.That(policy.IsRelevant(new Vector3(50f, 0f, 0f), new Vector3(55f, 0f, 0f)), Is.True,
                        "open country (-1 cell): the ring still applies");
            Assert.That(policy.IsRelevant(new Vector3(50f, 0f, 0f), new Vector3(90f, 0f, 0f)), Is.False,
                        "open country outside the ring, no shared cell");
        }

        [Test]
        public void RelevancyTransitions_EnterOnApproach_RemoveOnLeave_OnTheFullStack()
        {
            // full NetWorldServer/NetWorldClient stack: a STATIONARY zombie (it never re-dirties!) must
            // appear when the client's avatar walks into range -- that's the ack-safe tracker, not dirty
            // ticks -- and be removed from the replica when the avatar leaves.
            var h = new TransactionalHarness(90901);
            h.Server.Zombies.Interest = new InterestPolicy { RingRadius = 50f };
            h.Server.Zombies.ServerSpawn(h.Server.Ids.Mint(), 0, new Vector3(100f, 0f, 0f), h.Server.Session.CurrentTick);
            h.Connected("walker");
            var a = h.Clients[0];

            h.Step(30);
            Assert.That(a.Zombies.Count, Is.EqualTo(0), "spawned far -> absent from the replica");

            h.Server.Players.ServerTeleport(a.PlayerId, new Vector3(80f, 0f, 0f), h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => a.Zombies.Count == 1), Is.True,
                        "walking into the ring surfaced the (never-dirtied) zombie");

            h.Server.Players.ServerTeleport(a.PlayerId, new Vector3(0f, 0f, 0f), h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => a.Zombies.Count == 0), Is.True,
                        "leaving the ring removed it from the replica (relevancy-exit rides the removals list)");

            // and it comes back -- enter/exit/re-enter is stable
            h.Server.Players.ServerTeleport(a.PlayerId, new Vector3(90f, 0f, 0f), h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => a.Zombies.Count == 1), Is.True, "re-entering surfaces it again");
        }

        [Test]
        public void RelevancyTransitions_SurviveLossAndReorder()
        {
            // the enter/exit bookkeeping is resend-until-acked: under 25% loss both transitions still land
            var loss = new SDG.NetTransport.Mem.FaultyLinkConfig { LossProbability = 0.25, LatencyTicks = 1, ReorderJitterTicks = 2 };
            var h = new TransactionalHarness(90902, loss, loss);
            h.Server.Zombies.Interest = new InterestPolicy { RingRadius = 50f };
            h.Server.Zombies.ServerSpawn(h.Server.Ids.Mint(), 0, new Vector3(100f, 0f, 0f), h.Server.Session.CurrentTick);
            h.Connected("walker");
            var a = h.Clients[0];

            h.Server.Players.ServerTeleport(a.PlayerId, new Vector3(80f, 0f, 0f), h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => a.Zombies.Count == 1, 800), Is.True,
                        $"enter transition survived loss (seed={h.Net.Seed})");
            h.Server.Players.ServerTeleport(a.PlayerId, new Vector3(0f, 0f, 0f), h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => a.Zombies.Count == 0, 800), Is.True,
                        $"exit transition survived loss (seed={h.Net.Seed})");
        }

        [Test]
        public void WorldItems_UseTheSameRingPolicy()
        {
            var h = new TransactionalHarness(90903);
            h.Server.WorldItems.Interest = new InterestPolicy { RingRadius = 30f };
            TransactionalFixtures.RegisterAssets();
            h.Connected("looter");
            var a = h.Clients[0];

            // spawned far BEFORE the client could see it -- and the spawn event's broadcast races nothing
            // here because the entity predates the join (join full is the only carrier, and it filters)
            h.Server.WorldItems.ServerSpawn(h.Server.Ids.Mint(), new SDG.Unturned.Item(TransactionalFixtures.ScrapId),
                                            new Vector3(200f, 0f, 0f), h.Server.Session.CurrentTick);
            h.Step(30);
            Assert.That(a.WorldItems.Count, Is.EqualTo(0), "far ground item absent from the replica");

            h.Server.Players.ServerTeleport(a.PlayerId, new Vector3(190f, 0f, 0f), h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => a.WorldItems.Count == 1), Is.True, "walking up surfaced the ground item");
        }

        // ---- byte budget + priority accumulators (§2.6, in SnapshotComposer) ----

        static MockEntitySystem FatSystem(byte systemId, int entities, long tick)
        {
            var s = new MockEntitySystem(systemId);
            for (int i = 1; i <= entities; i++)
                s.Set(new NetId((uint)i), new Vector3(i * 0.5f, 0f, i * 0.25f), (i * 7) % 360, (byte)(i & 0xFF), tick);
            return s;
        }

        [Test]
        public void Budget_EveryComposedSnapshot_StaysUnderTheCap()
        {
            // two ~500-byte systems against a 600-byte budget: only one fits per snapshot, ever
            var sysA = FatSystem(1, 40, tick: 1);
            var sysB = FatSystem(2, 40, tick: 1);
            var composer = new SnapshotComposer(new IReplicatedSystem[] { sysA, sysB }) { BudgetBytes = 600 };

            for (long tick = 2; tick < 30; tick++)
            {
                var snap = composer.Compose(tick, 1, Vector3.zero);
                Assert.That(snap.Length, Is.LessThanOrEqualTo(600), $"snapshot at tick {tick} within budget");
            }
            Assert.That(composer.Diag.OversizedBlocksSkipped, Is.GreaterThan(0), "the budget actually bit");
        }

        [Test]
        public void Budget_SkippedBlocks_LoseNothing_AndStarvedSystemsRotateIn()
        {
            // The §4 Phase 8 budget test: under sustained per-tick mutation of BOTH fat systems, with room
            // for only ONE block per snapshot, the priority accumulator alternates them in -- and because a
            // skipped system's baseline stays pinned until an ack proves delivery, the moment the churn
            // stops BOTH replicas reach exact StateHash parity: deferred, never lost.
            var sysA = FatSystem(1, 40, tick: 1);
            var sysB = FatSystem(2, 40, tick: 1);
            var composer = new SnapshotComposer(new IReplicatedSystem[] { sysA, sysB }) { BudgetBytes = 600 };

            var repA = new MockEntitySystem(1);
            var repB = new MockEntitySystem(2);
            var applier = new SnapshotApplier(new IReplicatedSystem[] { repA, repB });

            // join: the full snapshot rides the reliable channel with its own big budget (the §2.2 join
            // path) -- both blocks fit there
            var join = composer.Compose(2, 1, Vector3.zero, maxBytes: NetProtocol.MaxReliableMessageBytes / 2);
            Assert.That(applier.Apply(join, join.Length), Is.True);
            composer.SetClientBaseline(1, 2);

            ulong hashA0 = repA.StateHash(), hashB0 = repB.StateHash();
            bool aAdvanced = false, bAdvanced = false;
            for (long tick = 3; tick < 43; tick++)
            {
                // churn: every entity in both systems moves every tick
                for (uint i = 1; i <= 40; i++)
                {
                    sysA.Set(new NetId(i), new Vector3(i + tick * 0.1f, 0f, 0f), (tick * 3) % 360, (byte)tick, tick);
                    sysB.Set(new NetId(i), new Vector3(-(i + tick * 0.1f), 0f, 0f), (tick * 5) % 360, (byte)tick, tick);
                }
                var snap = composer.Compose(tick, 1, Vector3.zero);
                Assert.That(snap.Length, Is.LessThanOrEqualTo(600), "under budget during churn");
                Assert.That(applier.Apply(snap, snap.Length), Is.True);
                composer.SetClientBaseline(1, tick);   // deterministic ack (lossless link)
                if (repA.StateHash() != hashA0) { aAdvanced = true; hashA0 = repA.StateHash(); }
                if (repB.StateHash() != hashB0) { bAdvanced = true; hashB0 = repB.StateHash(); }
            }
            Assert.That(aAdvanced && bAdvanced, Is.True,
                        "both systems kept flowing under sustained overload (priority rotation, no permanent starvation)");

            // churn stops -> within a few snapshots both replicas catch ALL deferred state (nothing lost)
            bool parity = false;
            for (long tick = 43; tick < 60 && !parity; tick++)
            {
                var snap = composer.Compose(tick, 1, Vector3.zero);
                Assert.That(snap.Length, Is.LessThanOrEqualTo(600));
                applier.Apply(snap, snap.Length);
                composer.SetClientBaseline(1, tick);
                parity = repA.StateHash() == sysA.StateHash() && repB.StateHash() == sysB.StateHash();
            }
            Assert.That(repA.StateHash(), Is.EqualTo(sysA.StateHash()), "system 1 replica reached exact parity");
            Assert.That(repB.StateHash(), Is.EqualTo(sysB.StateHash()), "system 2 replica reached exact parity");
        }

        [Test]
        public void Budget_NeverSkipped_IsByteIdenticalToTheUnbudgetedComposer()
        {
            // behavior-neutrality lock: a world that fits the budget composes the EXACT bytes the
            // pre-Phase-8 composer produced (registration order, same baselines) -- policy, not protocol
            var sysA = FatSystem(1, 5, tick: 1);
            var sysB = FatSystem(2, 5, tick: 1);
            var budgeted = new SnapshotComposer(new IReplicatedSystem[] { sysA, sysB }) { BudgetBytes = 300 };
            var roomy = new SnapshotComposer(new IReplicatedSystem[] { sysA, sysB });

            var s1 = budgeted.Compose(2, 1, Vector3.zero);
            var s2 = roomy.Compose(2, 1, Vector3.zero);
            Assert.That(s1, Is.EqualTo(s2), "full snapshots byte-identical");

            budgeted.SetClientBaseline(1, 2);
            roomy.SetClientBaseline(1, 2);
            sysA.Set(new NetId(1), new Vector3(9f, 0f, 9f), 45f, 9, tick: 3);
            var d1 = budgeted.Compose(4, 1, Vector3.zero);
            var d2 = roomy.Compose(4, 1, Vector3.zero);
            Assert.That(d1, Is.EqualTo(d2), "delta snapshots byte-identical when nothing was ever skipped");
        }
    }
}
