using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // A1 (SP/MP-unify) at the L0 layer: the container FIXTURE block (kind/pos/yaw/grid dims + the display
    // digest) round-trips full/delta/removal to exact StateHash parity through the real wire under loss +
    // reorder, and a late joiner rebuilds the whole set from the WriteFull join path. What ContainerNetSync
    // publishes and StorageReplicaView consumes is exactly this data.
    [TestFixture]
    public class ContainerReplicationTests
    {
        static ContainerDisplayCell[] Cells(params (byte cell, ushort id, byte rot)[] cs)
        {
            var arr = new ContainerDisplayCell[cs.Length];
            for (int i = 0; i < cs.Length; i++)
                arr[i] = new ContainerDisplayCell { Cell = cs[i].cell, ItemId = cs[i].id, Rot = cs[i].rot };
            return arr;
        }

        [Test]
        public void Fixtures_UnderLossAndReorder_ConvergeToExactParity_IncludingLateJoin()
        {
            var lossy = new FaultyLinkConfig { LossProbability = 0.25, DuplicateProbability = 0.05, LatencyTicks = 1, ReorderJitterTicks = 2 };
            var server = new ContainerReplication();
            var h = new ReplicationHarness(20260720, new List<IReplicatedSystem> { server },
                                           clientToServer: lossy, serverToClient: lossy);
            var fromStart = h.AddClient(() => new List<IReplicatedSystem> { new ContainerReplication() });

            // register a handful of world-build fixtures: a DISPLAY shelf (8x3 tiers, id 0) + three solid props (8x6)
            var ids = new NetId[4];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = h.Ids.Mint();
                server.ServerRegisterFixture(ids[i], (ushort)i, new Vector3(i * 6f, 0f, i * 2f), i * 30f,
                                             8, (byte)(i == 0 ? 3 : 6), h.Tick + 1);
            }
            // the display shelf shows some loot on its tiers (linear cell index y*Width+x, per the digest)
            server.ServerSetDisplay(ids[0].Value, Cells(((byte)0, (ushort)16, (byte)0), ((byte)3, (ushort)81, (byte)1), ((byte)10, (ushort)97, (byte)0)), h.Tick + 1);
            h.Step(40);

            // a late joiner arrives against the accumulated set (WriteFull IS the join path)
            var late = h.AddClient(() => new List<IReplicatedSystem> { new ContainerReplication() }, "late");

            // a player takes an item off the shelf (the digest shrinks) + one fixture is retired
            server.ServerSetDisplay(ids[0].Value, Cells(((byte)0, (ushort)16, (byte)0), ((byte)10, (ushort)97, (byte)0)), h.Tick + 1);
            server.ServerRemove(ids[3].Value, h.Tick + 1);
            h.Step(120);   // plenty for loss recovery (stale baselines fall back to full resends)

            ulong sh = server.StateHash();
            var cA = (ContainerReplication)fromStart.Systems[0];
            var cB = (ContainerReplication)late.Systems[0];
            Assert.That(cA.StateHash(), Is.EqualTo(sh), $"from-start replica == server ({h.Net.SeedInfo})");
            Assert.That(cB.StateHash(), Is.EqualTo(sh), $"late joiner == server ({h.Net.SeedInfo})");
            Assert.That(cA.Count, Is.EqualTo(3), "the retired fixture is gone from replicas");
            Assert.That(cA.TryGet(ids[0].Value, out var shelf), Is.True);
            Assert.That(shelf.KindId, Is.EqualTo((ushort)0));
            Assert.That(shelf.Display.Length, Is.EqualTo(2), "the taken item left the display digest");
            Assert.That(shelf.Width, Is.EqualTo((byte)8));
            Assert.That(shelf.Height, Is.EqualTo((byte)3), "the display shelf's tier-count dims replicated");
            Assert.That(cA.TryGet(ids[3].Value, out _), Is.False, "the retired fixture round-tripped its removal");
            Assert.That(sh, Is.Not.EqualTo(new ContainerReplication().StateHash()), "the scenario actually produced state");
        }
    }
}
