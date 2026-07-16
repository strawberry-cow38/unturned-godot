using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The UnreliableSequenced channel promise (MP_PLAN §2.2): newest-seq-wins -- the app only ever sees
    // monotonically newer state (inputs/snapshots), stale and duplicate datagrams drop on the floor.
    [TestFixture]
    public class UnreliableSeqTests
    {
        static byte[] Snapshot(int id)
        {
            return new[] { (byte)id, (byte)(id >> 8), (byte)0x5A };
        }

        static int SnapshotId(byte[] data) => data[0] | (data[1] << 8);

        [Test]
        public void NewestWins_StaleDroppedUnderReorder()
        {
            const int seed = 7777;
            var h = new NetSimHarness(seed,
                new FaultyLinkConfig { ReorderJitterTicks = 5, LatencyTicks = 1 },
                new FaultyLinkConfig { ReorderJitterTicks = 5, LatencyTicks = 1 });
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];

            const int count = 200;
            int sent = 0;
            var deliveredIds = new List<int>();
            h.StepUntil(() =>
            {
                if (sent < count) c.SendUnreliableSequenced(Snapshot(sent++));
                while (peer.TryReceiveUnreliable(out var msg)) deliveredIds.Add(SnapshotId(msg));
                return sent == count && deliveredIds.Count > 0 && peer.Session.Diag.StaleUnreliableDropped > 0
                    && deliveredIds[deliveredIds.Count - 1] >= count - 1;
            }, count + 100);

            Assert.That(deliveredIds.Count, Is.GreaterThan(0), h.SeedInfo);
            for (int i = 1; i < deliveredIds.Count; i++)
                Assert.That(deliveredIds[i], Is.GreaterThan(deliveredIds[i - 1]),
                    $"delivered snapshot ids must be strictly increasing -- stale never surfaces ({h.SeedInfo})");
            Assert.That(peer.Session.Diag.StaleUnreliableDropped, Is.GreaterThan(0),
                $"the reorder jitter must actually have produced stale drops ({h.SeedInfo})");
        }

        [Test]
        public void Duplicates_NeverSurface()
        {
            const int seed = 4141;
            var h = new NetSimHarness(seed,
                new FaultyLinkConfig { DuplicateProbability = 1.0 }, // every datagram arrives twice
                null);
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];

            var deliveredIds = new List<int>();
            for (int i = 0; i < 20; i++)
            {
                c.SendUnreliableSequenced(Snapshot(i));
                h.Step();
                while (peer.TryReceiveUnreliable(out var msg)) deliveredIds.Add(SnapshotId(msg));
            }
            h.Step(10);
            while (peer.TryReceiveUnreliable(out var msg)) deliveredIds.Add(SnapshotId(msg));

            Assert.That(deliveredIds, Is.EqualTo(new List<int>(System.Linq.Enumerable.Range(0, 20))),
                $"100% duplication: each snapshot surfaces exactly once ({h.SeedInfo})");
            Assert.That(peer.Session.Diag.StaleUnreliableDropped, Is.GreaterThanOrEqualTo(20), h.SeedInfo);
        }

        [Test]
        public void LossJustDropsThem_NoRetransmit()
        {
            const int seed = 616;
            var h = new NetSimHarness(seed, new FaultyLinkConfig { LossProbability = 0.5 }, null);
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];
            long retransmitsBefore = c.Session.Diag.ReliableRetransmits;

            int delivered = 0;
            for (int i = 0; i < 200; i++)
            {
                c.SendUnreliableSequenced(Snapshot(i));
                h.Step();
                while (peer.TryReceiveUnreliable(out _)) delivered++;
            }

            Assert.That(delivered, Is.InRange(60, 140), $"~50% of 200 should survive, none resent ({h.SeedInfo})");
            Assert.That(c.Session.Diag.ReliableRetransmits, Is.EqualTo(retransmitsBefore),
                "unreliable loss must never trigger the reliable machinery");
        }

        [Test]
        public void OversizedUnreliablePayload_IsRefused()
        {
            var h = new NetSimHarness(seed: 13);
            var c = h.ConnectClient();
            Assert.That(c.SendUnreliableSequenced(new byte[NetProtocol.MaxUnreliablePayload + 1]), Is.False,
                "unreliable payloads never fragment -- one lost fragment would waste the whole snapshot");
            Assert.That(c.SendUnreliableSequenced(new byte[NetProtocol.MaxUnreliablePayload]), Is.True);
        }
    }
}
