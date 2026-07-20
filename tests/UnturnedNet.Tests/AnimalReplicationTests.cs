using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // A5 (SP/MP-unify) at the L0 layer: the animal snapshot block (transform + anim byte + species) round-trips
    // full/delta/removal to exact StateHash parity through the real wire under seeded loss + reorder, at the
    // 12.5 Hz publish cadence AnimalNetSync uses, including a late joiner rebuilding from the WriteFull join path.
    // Structurally identical to ZombieReplicationTests (animals ARE the zombie shape, minus the combat host).
    [TestFixture]
    public class AnimalReplicationTests
    {
        [Test]
        public void GrazingHerd_UnderLossAndReorder_ConvergesToExactParity_IncludingLateJoin()
        {
            var lossy = new FaultyLinkConfig { LossProbability = 0.25, DuplicateProbability = 0.05, LatencyTicks = 1, ReorderJitterTicks = 2 };
            var serverAnimals = new AnimalReplication();
            var h = new ReplicationHarness(20260720, new List<IReplicatedSystem> { serverAnimals },
                                           clientToServer: lossy, serverToClient: lossy);
            var fromStart = h.AddClient(() => new List<IReplicatedSystem> { new AnimalReplication() });

            // a small herd: deer/pig/cow, wandering deterministically, published at the 12.5 Hz game cadence
            var ids = new NetId[4];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = h.Ids.Mint();
                serverAnimals.ServerSpawn(ids[i], (byte)(i % 3), new Vector3(i * 4f, 0f, 0f), h.Tick + 1);
            }
            for (int step = 0; step < 300; step++)
            {
                if (step % 4 == 0)   // AnimalNetSync.PublishDivisorTicks
                {
                    long t = h.Tick + 1;
                    for (int i = 0; i < ids.Length; i++)
                        serverAnimals.ServerPublish(ids[i],
                            new Vector3(i * 4f + step * 0.04f, 0f, (i % 2 == 0 ? 1f : -1f) * step * 0.02f),
                            (step * 5 + i * 60) % 360,
                            (byte)(step % 30 < 18 ? AnimalNetAnim.Walk : AnimalNetAnim.Eat), t);
                }
                h.Step();
            }

            // a late joiner arrives against the accumulated herd (WriteFull IS the join path)
            var late = h.AddClient(() => new List<IReplicatedSystem> { new AnimalReplication() }, "late");

            // one animal grazes still (Idle) + one is despawned (streamed out); the world then goes quiet
            serverAnimals.ServerPublish(ids[1], new Vector3(1f, 0f, 0f), 0f, (byte)AnimalNetAnim.Idle, h.Tick + 1);
            serverAnimals.ServerRemove(ids[3], h.Tick + 1);
            h.Step(120);   // plenty for loss recovery (stale baselines fall back to full resends)

            ulong server = serverAnimals.StateHash();
            var aA = (AnimalReplication)fromStart.Systems[0];
            var aB = (AnimalReplication)late.Systems[0];
            Assert.That(aA.StateHash(), Is.EqualTo(server), $"from-start replica == server ({h.Net.SeedInfo})");
            Assert.That(aB.StateHash(), Is.EqualTo(server), $"late joiner == server ({h.Net.SeedInfo})");
            Assert.That(aA.Count, Is.EqualTo(3), "the streamed-out animal is gone from replicas");
            Assert.That(aA.TryGet(ids[2], out var cow), Is.True);
            Assert.That(cow.Species, Is.EqualTo((byte)2), "the species byte (cow) rides the wire");
            Assert.That(aA.TryGet(ids[1], out var still), Is.True);
            Assert.That(still.AnimState, Is.EqualTo((byte)AnimalNetAnim.Idle), "the anim byte replicated");
            Assert.That(aA.TryGet(ids[3], out _), Is.False, "the removal round-tripped");
            Assert.That(server, Is.Not.EqualTo(new AnimalReplication().StateHash()), "the scenario actually produced state");
        }
    }
}
