using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // MP_PLAN §3.5 at the L0 layer: the zombie snapshot block (transform + anim byte + speciality)
    // round-trips full/delta/removal to exact StateHash parity, through the real wire under seeded loss +
    // reorder, at the 12.5 Hz publish cadence the game uses. What the game publishes (ZombieNetSync) and
    // what puppets consume is exactly this data.
    [TestFixture]
    public class ZombieReplicationTests
    {
        [Test]
        public void MovingHorde_UnderLossAndReorder_ConvergesToExactParity_IncludingLateJoin()
        {
            var lossy = new FaultyLinkConfig { LossProbability = 0.25, DuplicateProbability = 0.05, LatencyTicks = 1, ReorderJitterTicks = 2 };
            var serverZombies = new ZombieReplication();
            var h = new ReplicationHarness(20260717, new List<IReplicatedSystem> { serverZombies },
                                           clientToServer: lossy, serverToClient: lossy);
            var fromStart = h.AddClient(() => new List<IReplicatedSystem> { new ZombieReplication() });

            // a small horde: spawn 4, walk them deterministically, publishing at the 12.5 Hz game cadence
            var ids = new NetId[4];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = h.Ids.Mint();
                serverZombies.ServerSpawn(ids[i], (byte)(i % 3 == 2 ? ZombieReplication.SpecialityCrawler : 0),
                                          new Vector3(i * 3f, 0f, 0f), h.Tick + 1);
            }
            for (int step = 0; step < 300; step++)
            {
                if (step % 4 == 0)   // ZombieNetSync.PublishDivisorTicks
                {
                    long t = h.Tick + 1;
                    for (int i = 0; i < ids.Length; i++)
                        serverZombies.ServerPublish(ids[i],
                            new Vector3(i * 3f + step * 0.05f, 0f, (i % 2 == 0 ? 1f : -1f) * step * 0.03f),
                            (step * 7 + i * 90) % 360,
                            (byte)(step % 40 < 30 ? ZombieNetAnim.Walk : ZombieNetAnim.Attack), t);
                }
                h.Step();
            }

            // a late joiner arrives against the accumulated horde (WriteFull IS the join path)
            var late = h.AddClient(() => new List<IReplicatedSystem> { new ZombieReplication() }, "late");

            // one dies + one despawns; the world then goes quiet so replicas can reach exact parity
            serverZombies.ServerSetAnim(ids[1], ZombieNetAnim.Dead, h.Tick + 1);
            serverZombies.ServerRemove(ids[3], h.Tick + 1);
            h.Step(120);   // plenty for loss recovery (stale baselines fall back to full resends)

            ulong server = serverZombies.StateHash();
            var zA = (ZombieReplication)fromStart.Systems[0];
            var zB = (ZombieReplication)late.Systems[0];
            Assert.That(zA.StateHash(), Is.EqualTo(server), $"from-start replica == server ({h.Net.SeedInfo})");
            Assert.That(zB.StateHash(), Is.EqualTo(server), $"late joiner == server ({h.Net.SeedInfo})");
            Assert.That(zA.Count, Is.EqualTo(3), "the removed zombie is gone from replicas");
            Assert.That(zA.TryGet(ids[1], out var deadOnA), Is.True);
            Assert.That(deadOnA.IsDead, Is.True, "the dead zombie's anim byte replicated");
            Assert.That(zA.TryGet(ids[2], out var crawlerOnA), Is.True);
            Assert.That(crawlerOnA.Speciality, Is.EqualTo(ZombieReplication.SpecialityCrawler), "speciality rides the wire");
            Assert.That(server, Is.Not.EqualTo(new ZombieReplication().StateHash()), "the scenario actually produced state");
        }
    }
}
