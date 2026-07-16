using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The ReliableOrdered channel promise (MP_PLAN §2.2): every message arrives exactly once, in order,
    // over any FaultyLink -- 30% loss, reordering, duplication -- with fragmentation for messages over
    // the 1200-byte MTU budget. Deterministic seeded sims; the seed is in every failure message.
    [TestFixture]
    public class ReliableChannelTests
    {
        static byte[] NumberedMessage(int id, int length)
        {
            var data = new byte[length];
            data[0] = (byte)id;
            data[1] = (byte)(id >> 8);
            for (int i = 2; i < length; i++) data[i] = (byte)(id + i);
            return data;
        }

        static int MessageId(byte[] data) => data[0] | (data[1] << 8);

        static bool MessagePayloadValid(byte[] data)
        {
            int id = MessageId(data);
            for (int i = 2; i < data.Length; i++)
                if (data[i] != (byte)(id + i)) return false;
            return true;
        }

        [Test]
        public void ReliableOrdered_ExactlyOnceInOrder_Under30PctLossAndReorder()
        {
            const int seed = 424242;
            const int messageCount = 300;
            var adverse = new FaultyLinkConfig { LossProbability = 0.3, DuplicateProbability = 0.05, ReorderJitterTicks = 4, LatencyTicks = 1 };
            var h = new NetSimHarness(seed,
                new FaultyLinkConfig { LossProbability = 0.3, DuplicateProbability = 0.05, ReorderJitterTicks = 4, LatencyTicks = 1 },
                adverse);
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];

            var received = new List<byte[]>();
            int sent = 0;
            bool done = h.StepUntil(() =>
            {
                if (sent < messageCount) c.SendReliable(NumberedMessage(sent++, 32));
                while (peer.TryReceiveReliable(out var msg)) received.Add(msg);
                return received.Count == messageCount && sent == messageCount;
            }, 4000);

            Assert.That(done, Is.True, $"all {messageCount} messages must get through 30% loss ({h.SeedInfo})");
            for (int i = 0; i < messageCount; i++)
            {
                Assert.That(MessageId(received[i]), Is.EqualTo(i), $"message {i} out of order ({h.SeedInfo})");
                Assert.That(MessagePayloadValid(received[i]), Is.True, $"message {i} corrupted ({h.SeedInfo})");
            }
            // exactly once: nothing extra shows up after a generous drain
            h.Step(200);
            Assert.That(peer.TryReceiveReliable(out _), Is.False, $"no duplicate deliveries ({h.SeedInfo})");
            Assert.That(c.Session.Diag.ReliableRetransmits, Is.GreaterThan(0), "loss this heavy must have caused retransmits");
        }

        [Test]
        public void ReliableOrdered_ServerToClient_SameGuarantees()
        {
            const int seed = 555;
            const int messageCount = 100;
            var h = new NetSimHarness(seed,
                new FaultyLinkConfig { LossProbability = 0.25, ReorderJitterTicks = 3 },
                new FaultyLinkConfig { LossProbability = 0.25, ReorderJitterTicks = 3 });
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];

            var received = new List<int>();
            int sent = 0;
            bool done = h.StepUntil(() =>
            {
                if (sent < messageCount) peer.SendReliable(NumberedMessage(sent++, 16));
                while (c.TryReceiveReliable(out var msg)) received.Add(MessageId(msg));
                return received.Count == messageCount;
            }, 3000);

            Assert.That(done, Is.True, $"server->client reliable must converge ({h.SeedInfo})");
            Assert.That(received, Is.EqualTo(new List<int>(System.Linq.Enumerable.Range(0, messageCount))), h.SeedInfo);
        }

        [Test]
        public void Ordering_SurvivesAggressiveReorder_WithoutLoss()
        {
            const int seed = 8080;
            var h = new NetSimHarness(seed,
                new FaultyLinkConfig { ReorderJitterTicks = 8 },
                new FaultyLinkConfig { ReorderJitterTicks = 8 });
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];

            for (int i = 0; i < 50; i++) c.SendReliable(NumberedMessage(i, 8));
            var received = new List<int>();
            h.StepUntil(() =>
            {
                while (peer.TryReceiveReliable(out var msg)) received.Add(MessageId(msg));
                return received.Count == 50;
            }, 500);

            Assert.That(received, Is.EqualTo(new List<int>(System.Linq.Enumerable.Range(0, 50))),
                $"heavy reorder on the wire, still in-order at the app ({h.SeedInfo})");
        }

        [Test]
        public void DuplicateSuppression_UnderAggressiveDuplication()
        {
            const int seed = 1234;
            var h = new NetSimHarness(seed,
                new FaultyLinkConfig { DuplicateProbability = 0.5 },
                new FaultyLinkConfig { DuplicateProbability = 0.5 });
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];

            for (int i = 0; i < 100; i++) c.SendReliable(NumberedMessage(i, 8));
            var received = new List<int>();
            h.StepUntil(() =>
            {
                while (peer.TryReceiveReliable(out var msg)) received.Add(MessageId(msg));
                return received.Count >= 100;
            }, 500);
            h.Step(100); // drain window: any duplicate would surface here
            while (peer.TryReceiveReliable(out var msg)) received.Add(MessageId(msg));

            Assert.That(received, Is.EqualTo(new List<int>(System.Linq.Enumerable.Range(0, 100))),
                $"50% duplication must deliver exactly once ({h.SeedInfo})");
            Assert.That(peer.Session.Diag.DuplicateFragmentsDropped, Is.GreaterThan(0),
                $"the dup knob demonstrably fired ({h.SeedInfo})");
        }

        [Test]
        public void Fragmentation_100kB_RoundTrips_OverALossyLink()
        {
            const int seed = 987654;
            const int payloadSize = 100_000;
            var h = new NetSimHarness(seed,
                new FaultyLinkConfig { LossProbability = 0.2, ReorderJitterTicks = 3 },
                new FaultyLinkConfig { LossProbability = 0.2, ReorderJitterTicks = 3 });
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];

            // deterministic pseudo-random payload
            var payload = new byte[payloadSize];
            var rng = new Random(seed);
            rng.NextBytes(payload);

            Assert.That(c.SendReliable(payload), Is.True);
            byte[] got = null;
            bool done = h.StepUntil(() => peer.TryReceiveReliable(out got), 4000);

            Assert.That(done, Is.True, $"100 kB reliable payload must reassemble through 20% loss ({h.SeedInfo})");
            Assert.That(got.Length, Is.EqualTo(payloadSize), h.SeedInfo);
            Assert.That(got, Is.EqualTo(payload), $"byte-exact after fragmentation + loss + reorder ({h.SeedInfo})");

            int minFragments = (payloadSize + NetProtocol.MaxFragmentPayload - 1) / NetProtocol.MaxFragmentPayload;
            Assert.That(c.Session.Diag.ReliableFragmentsSent, Is.GreaterThanOrEqualTo(minFragments),
                $"payload must actually have fragmented (expected >= {minFragments} fragments, {h.SeedInfo})");
            Assert.That(c.Session.Diag.WriterErrors, Is.Zero, "every fragment fit the 1200-byte budget");
        }

        [Test]
        public void ZeroLengthMessage_IsDelivered()
        {
            var h = new NetSimHarness(seed: 11);
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];
            Assert.That(c.SendReliable(new byte[0]), Is.True);
            byte[] got = null;
            Assert.That(h.StepUntil(() => peer.TryReceiveReliable(out got), 50), Is.True, h.SeedInfo);
            Assert.That(got.Length, Is.Zero);
        }

        [Test]
        public void OversizedReliableMessage_IsRefused()
        {
            var h = new NetSimHarness(seed: 12);
            var c = h.ConnectClient();
            Assert.That(c.SendReliable(new byte[NetProtocol.MaxReliableMessageBytes + 1]), Is.False,
                "fragCount is 8 bits; messages beyond 255 fragments are refused, not silently truncated");
            Assert.That(c.SendReliable(new byte[NetProtocol.MaxReliableMessageBytes]), Is.True);
        }
    }
}
