using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;
using UnturnedNet.Tests.Mocks;

namespace UnturnedNet.Tests
{
    // The snapshot plane over the REAL Phase 1 wire (MemTransport + NetSession + FaultyLink) -- MP_PLAN §6:
    // "run a scripted scenario through the harness, assert server hash == every client hash". Two systems
    // (10, 11) so every scenario exercises multiple blocks per snapshot.
    [TestFixture]
    public class SnapshotNetworkTests
    {
        static List<IReplicatedSystem> MakeSystems() => new List<IReplicatedSystem>
        {
            new MockEntitySystem(systemId: 10),
            new MockEntitySystem(systemId: 11),
        };

        // Deterministic, bounded-range mutation of two entities across both systems -- no RNG, so the
        // scenario itself is identical every run (only the network's seeded FaultyLink varies delivery).
        static void DriveScenario(ReplicationHarness h, NetId posEntity, NetId auxEntity, long tick)
        {
            var pos = (MockEntitySystem)h.ServerSystems[0];
            var aux = (MockEntitySystem)h.ServerSystems[1];
            pos.Set(posEntity, new Vector3((tick % 200) - 100f, (tick % 50) * 0.1f, 100f - (tick % 200)),
                (tick * 7) % 360, (byte)(tick % 256), tick);
            if (tick % 3 == 0)
                aux.Set(auxEntity, new Vector3(-((tick % 150) - 75f), 0f, (tick % 30)),
                    (tick * 11) % 360, (byte)(255 - (tick % 256)), tick);
        }

        static void AssertSystemsConverge(IReadOnlyList<IReplicatedSystem> server, IReadOnlyList<IReplicatedSystem> client, string context)
        {
            for (int i = 0; i < server.Count; i++)
                Assert.That(client[i].StateHash(), Is.EqualTo(server[i].StateHash()),
                    $"system {server[i].SystemId} out of sync ({context})");
        }

        [Test]
        public void StateHash_Converges_CleanLink_FullThenManyDeltas()
        {
            var harness = new ReplicationHarness(seed: 1, MakeSystems());
            var a = harness.AddClient(MakeSystems, "a");
            var b = harness.AddClient(MakeSystems, "b");

            var posEntity = harness.Ids.Mint();
            var auxEntity = harness.Ids.Mint();

            for (int tick = 0; tick < 300; tick++)
            {
                DriveScenario(harness, posEntity, auxEntity, tick);
                harness.Step();
            }
            harness.Step(20); // settle: let the last snapshots/acks land

            AssertSystemsConverge(harness.ServerSystems, a.Systems, "client a, clean link");
            AssertSystemsConverge(harness.ServerSystems, b.Systems, "client b, clean link");
        }

        [Test]
        public void StateHash_Converges_UnderLossyReorderingLink()
        {
            var adverse = new FaultyLinkConfig { LossProbability = 0.2, ReorderJitterTicks = 3, DuplicateProbability = 0.05 };
            var harness = new ReplicationHarness(seed: 2, MakeSystems(), clientToServer: adverse, serverToClient: adverse);
            var a = harness.AddClient(MakeSystems, "a");

            var posEntity = harness.Ids.Mint();
            var auxEntity = harness.Ids.Mint();

            for (int tick = 0; tick < 600; tick++)
            {
                DriveScenario(harness, posEntity, auxEntity, tick);
                harness.Step();
            }
            // settle well past the dirty-ring depth so a full resend recovers from any stuck baseline
            harness.Step((int)(NetQuantization.DirtyRingDepthTicks * 3));

            AssertSystemsConverge(harness.ServerSystems, a.Systems, $"lossy link ({harness.Net.SeedInfo})");
        }

        [Test]
        public void JoinMidScenario_LateClient_ReachesParityWithFromStartClient()
        {
            var harness = new ReplicationHarness(seed: 3, MakeSystems());
            var early = harness.AddClient(MakeSystems, "early");

            var posEntity = harness.Ids.Mint();
            var auxEntity = harness.Ids.Mint();

            for (int tick = 0; tick < 500; tick++)
            {
                DriveScenario(harness, posEntity, auxEntity, tick);
                harness.Step();
            }

            // a client connects late (mid-scenario) -- it must get a full snapshot and catch up
            var late = harness.AddClient(MakeSystems, "late");
            Assert.That(late.Applier.Diag.SnapshotsApplied, Is.Zero, "hasn't applied anything yet");

            for (int tick = 500; tick < 700; tick++)
            {
                DriveScenario(harness, posEntity, auxEntity, tick);
                harness.Step();
            }
            harness.Step(20);

            Assert.That(late.Applier.Diag.FullSnapshotsApplied, Is.GreaterThan(0), "late join must have received a full snapshot");
            AssertSystemsConverge(harness.ServerSystems, early.Systems, "early client");
            AssertSystemsConverge(harness.ServerSystems, late.Systems, "late-joining client");
        }
    }
}
