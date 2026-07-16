using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport;
using SDG.NetTransport.Mem;

namespace UnturnedNet.Tests
{
    // MemTransport is the workhorse under every net test, so its own guarantees get pinned first:
    // determinism per seed, the four FaultyLink knobs actually doing their jobs, and endpoint equality
    // (the property the server session keys its peer table on).
    [TestFixture]
    public class MemTransportTests
    {
        static byte[] Payload(int i) => new[] { (byte)i, (byte)(i >> 8), (byte)0xAB, (byte)0xCD };

        // Runs a fixed scenario: one client sends `count` numbered datagrams, one per tick, draining the
        // server every tick; returns the payload ids in arrival order.
        static List<int> RunScenario(int seed, FaultyLinkConfig link, int count, int drainTicks = 50)
        {
            var net = new MemNetwork(seed) { ClientToServer = link };
            var server = new MemServerTransport(net);
            server.Initialize(null);
            var client = new MemClientTransport(net);
            client.Initialize(null, null);

            var arrivals = new List<int>();
            var rx = new byte[64];
            for (int tick = 0; tick < count + drainTicks; tick++)
            {
                net.Tick();
                if (tick < count)
                {
                    var p = Payload(tick);
                    client.Send(p, p.Length, ENetReliability.Unreliable);
                }
                while (server.Receive(rx, out long size, out _))
                {
                    Assert.That(size, Is.EqualTo(4));
                    arrivals.Add(rx[0] | (rx[1] << 8));
                }
            }
            return arrivals;
        }

        [Test]
        public void SameSeed_SameDeliverySchedule()
        {
            var link = new FaultyLinkConfig { LossProbability = 0.3, DuplicateProbability = 0.1, ReorderJitterTicks = 4, LatencyTicks = 2 };
            var a = RunScenario(777, link, 200);
            var b = RunScenario(777, new FaultyLinkConfig { LossProbability = 0.3, DuplicateProbability = 0.1, ReorderJitterTicks = 4, LatencyTicks = 2 }, 200);
            Assert.That(a, Is.EqualTo(b), "identical seed + config + call order must produce an identical delivery schedule (seed=777)");
            Assert.That(a.Count, Is.GreaterThan(0));
        }

        [Test]
        public void Loss_DropsRoughlyTheConfiguredFraction()
        {
            const int seed = 4242;
            var arrivals = RunScenario(seed, new FaultyLinkConfig { LossProbability = 0.3 }, 1000);
            // binomial(1000, 0.7): mean 700, sigma ~14.5 -- +/-5 sigma keeps this robust to RNG-stream tweaks
            Assert.That(arrivals.Count, Is.InRange(620, 780), $"~70% of 1000 should survive 30% loss (seed={seed})");
        }

        [Test]
        public void Reorder_ProducesOutOfOrderArrivals_WithoutLosingAny()
        {
            const int seed = 99;
            var arrivals = RunScenario(seed, new FaultyLinkConfig { ReorderJitterTicks = 3 }, 100);
            Assert.That(arrivals.Count, Is.EqualTo(100), $"jitter must not lose datagrams (seed={seed})");
            var sorted = new List<int>(arrivals);
            sorted.Sort();
            Assert.That(sorted, Is.EqualTo(new List<int>(System.Linq.Enumerable.Range(0, 100))), $"every datagram arrives exactly once (seed={seed})");
            Assert.That(arrivals, Is.Not.EqualTo(sorted), $"jitter should produce at least one inversion (seed={seed})");
        }

        [Test]
        public void Duplication_DeliversExtras()
        {
            const int seed = 31337;
            var arrivals = RunScenario(seed, new FaultyLinkConfig { DuplicateProbability = 0.5 }, 100);
            Assert.That(arrivals.Count, Is.InRange(130, 170), $"~50% duplication on 100 sends (seed={seed})");
        }

        [Test]
        public void Latency_DelaysDeliveryByExactlyThatManyTicks()
        {
            var net = new MemNetwork(1) { ClientToServer = new FaultyLinkConfig { LatencyTicks = 5 } };
            var server = new MemServerTransport(net);
            server.Initialize(null);
            var client = new MemClientTransport(net);
            client.Initialize(null, null);

            var p = Payload(0);
            client.Send(p, p.Length, ENetReliability.Unreliable); // sent at tick 0
            var rx = new byte[64];
            long arrivedAt = -1;
            for (int i = 0; i < 10 && arrivedAt < 0; i++)
            {
                net.Tick();
                if (server.Receive(rx, out _, out _)) arrivedAt = net.CurrentTick;
            }
            Assert.That(arrivedAt, Is.EqualTo(5));
        }

        [Test]
        public void ServerConnections_AreEndpointEquatable()
        {
            var net = new MemNetwork(1);
            var server = new MemServerTransport(net);
            server.Initialize(null);
            var clientA = new MemClientTransport(net);
            clientA.Initialize(null, null);
            var clientB = new MemClientTransport(net);
            clientB.Initialize(null, null);

            var p = Payload(1);
            clientA.Send(p, p.Length, ENetReliability.Unreliable);
            clientA.Send(p, p.Length, ENetReliability.Unreliable);
            clientB.Send(p, p.Length, ENetReliability.Unreliable);
            net.Tick();

            var rx = new byte[64];
            Assert.That(server.Receive(rx, out _, out ITransportConnection c1), Is.True);
            Assert.That(server.Receive(rx, out _, out ITransportConnection c2), Is.True);
            Assert.That(server.Receive(rx, out _, out ITransportConnection c3), Is.True);

            // fresh object per datagram (like UDP), equal by endpoint -- dictionary keying works
            Assert.That(ReferenceEquals(c1, c2), Is.False);
            Assert.That(c1.Equals(c2), Is.True);
            Assert.That(c1.GetHashCode(), Is.EqualTo(c2.GetHashCode()));
            Assert.That(c1.Equals(c3), Is.False);

            var dict = new Dictionary<ITransportConnection, int> { [c1] = 7 };
            Assert.That(dict.ContainsKey(c2), Is.True);
            Assert.That(dict.ContainsKey(c3), Is.False);
        }

        [Test]
        public void ServerReply_ReachesTheRightClient()
        {
            var net = new MemNetwork(1);
            var server = new MemServerTransport(net);
            server.Initialize(null);
            var clientA = new MemClientTransport(net);
            clientA.Initialize(null, null);
            var clientB = new MemClientTransport(net);
            clientB.Initialize(null, null);

            var p = Payload(9);
            clientA.Send(p, p.Length, ENetReliability.Unreliable);
            net.Tick();
            var rx = new byte[64];
            Assert.That(server.Receive(rx, out _, out ITransportConnection conn), Is.True);

            var reply = new byte[] { 0xEE };
            conn.Send(reply, 1, ENetReliability.Unreliable);
            net.Tick();
            Assert.That(clientA.Receive(rx, out long size), Is.True, "reply routed to sender");
            Assert.That(size, Is.EqualTo(1));
            Assert.That(rx[0], Is.EqualTo(0xEE));
            Assert.That(clientB.Receive(rx, out _), Is.False, "other client got nothing");
        }
    }
}
