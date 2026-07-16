using System;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Connection lifecycle per MP_PLAN §2.2: Connect -> Accept{playerId, serverTick} / Reject{reason},
    // 1 Hz keepalive when idle, ~5 s of silence = disconnect through the ServerTransportConnectionFailureCallback
    // seam. All deterministic ticks over MemTransport -- no sleeps, no sockets.
    [TestFixture]
    public class SessionLifecycleTests
    {
        [Test]
        public void Connect_Accepts_AndAssignsDistinctPlayerIds()
        {
            var h = new NetSimHarness(seed: 1);
            int joins = 0;
            h.Server.PeerConnected += _ => joins++;

            var a = h.AddClient("alice");
            var b = h.AddClient("bob");
            a.Connect();
            b.Connect();
            Assert.That(h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected, 100),
                Is.True, $"both clients connect over a perfect link ({h.SeedInfo})");

            Assert.That(h.Server.Peers.Count, Is.EqualTo(2));
            Assert.That(joins, Is.EqualTo(2));
            Assert.That(a.PlayerId, Is.Not.EqualTo(b.PlayerId));
            Assert.That(h.Server.FindPeer(a.PlayerId).Name, Is.EqualTo("alice"));
            Assert.That(h.Server.FindPeer(b.PlayerId).Name, Is.EqualTo("bob"));
        }

        [Test]
        public void Connect_Succeeds_UnderHeavyLoss()
        {
            // 40% loss both directions: the 0.5 s Connect retry + idempotent re-Accept must converge
            const int seed = 20260716;
            var lossy = new FaultyLinkConfig { LossProbability = 0.4, ReorderJitterTicks = 2 };
            var h = new NetSimHarness(seed, lossy, new FaultyLinkConfig { LossProbability = 0.4, ReorderJitterTicks = 2 });
            var c = h.AddClient();
            c.Connect();
            Assert.That(h.StepUntil(() => c.State == NetSessionState.Connected, 240),
                Is.True, $"handshake must survive 40% loss within the 5 s connect budget ({h.SeedInfo})");
            Assert.That(h.Server.Peers.Count, Is.EqualTo(1));
        }

        [Test]
        public void VersionMismatch_IsRejected()
        {
            var h = new NetSimHarness(seed: 2); // server speaks Version 1
            var c = h.AddClient("timetraveler", version: (byte)(NetProtocol.Version + 1));
            c.Connect();
            Assert.That(h.StepUntil(() => c.State == NetSessionState.Disconnected, 100),
                Is.True, $"mismatched client must be told, not time out ({h.SeedInfo})");
            Assert.That(c.DisconnectReason, Is.EqualTo(NetDisconnectReason.Rejected));
            Assert.That(c.RejectReason, Is.EqualTo(NetRejectReason.VersionMismatch));
            Assert.That(h.Server.Peers.Count, Is.EqualTo(0), "no session is built for a rejected version");
        }

        [Test]
        public void ServerFull_IsRejected()
        {
            var h = new NetSimHarness(seed: 3, maxPeers: 1);
            var a = h.ConnectClient("first");
            var b = h.AddClient("second");
            b.Connect();
            Assert.That(h.StepUntil(() => b.State == NetSessionState.Disconnected, 100), Is.True, h.SeedInfo);
            Assert.That(b.RejectReason, Is.EqualTo(NetRejectReason.ServerFull));
            Assert.That(a.State, Is.EqualTo(NetSessionState.Connected), "first client is unaffected");
        }

        [Test]
        public void ConnectTimeout_WhenNobodyAnswers()
        {
            var net = new MemNetwork(4);
            // no server bound at all -- datagrams go nowhere
            var c = new NetClientSession(new MemClientTransport(net));
            c.Connect();
            for (int i = 0; i < NetProtocol.ConnectTimeoutTicks + 5 && c.State == NetSessionState.Connecting; i++)
            {
                net.Tick();
                c.Tick();
            }
            Assert.That(c.State, Is.EqualTo(NetSessionState.Disconnected));
            Assert.That(c.DisconnectReason, Is.EqualTo(NetDisconnectReason.Timeout));
        }

        [Test]
        public void IdleClientSilence_TimesOutPeer_ThroughFailureCallbackSeam()
        {
            var h = new NetSimHarness(seed: 5);
            var c = h.ConnectClient();
            NetDisconnectReason gotReason = NetDisconnectReason.None;
            h.Server.PeerDisconnected += (_, reason) => gotReason = reason;

            // the client goes dark: only the network + server tick from here on
            for (int i = 0; i < NetProtocol.TimeoutTicks + 20; i++)
            {
                h.Net.Tick();
                h.Server.Tick();
            }

            Assert.That(h.Server.Peers.Count, Is.EqualTo(0), $"silent peer must be dropped ({h.SeedInfo})");
            Assert.That(gotReason, Is.EqualTo(NetDisconnectReason.Timeout));
            Assert.That(h.Failures.Count, Is.EqualTo(1), "timeout feeds the ServerTransportConnectionFailureCallback seam");
            Assert.That(h.Failures[0].IsError, Is.True, "timeout is an error-class failure");
        }

        [Test]
        public void DeadServer_TimesOutClient()
        {
            var h = new NetSimHarness(seed: 6);
            var c = h.ConnectClient();

            // the server goes dark: only the network + client tick from here on
            for (int i = 0; i < NetProtocol.TimeoutTicks + 20; i++)
            {
                h.Net.Tick();
                c.Tick();
            }

            Assert.That(c.State, Is.EqualTo(NetSessionState.Disconnected), h.SeedInfo);
            Assert.That(c.DisconnectReason, Is.EqualTo(NetDisconnectReason.Timeout));
        }

        [Test]
        public void KeepAlives_HoldAnIdleSessionOpen_At1Hz()
        {
            var h = new NetSimHarness(seed: 7);
            var c = h.ConnectClient();
            long sentAtConnect = c.Session.Diag.DatagramsSent;

            h.Step(1000); // 20 s of nothing but keepalives

            Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), $"idle session stays alive ({h.SeedInfo})");
            Assert.That(h.Server.Peers.Count, Is.EqualTo(1));
            Assert.That(h.Failures.Count, Is.EqualTo(0));
            long idleSends = c.Session.Diag.DatagramsSent - sentAtConnect;
            Assert.That(idleSends, Is.InRange(18, 25), "keepalive cadence should be ~1 Hz over 20 s, not a chatty ack ping-pong");
        }

        [Test]
        public void GracefulClientDisconnect_RemovesPeer_AsNonError()
        {
            var h = new NetSimHarness(seed: 8);
            var c = h.ConnectClient();
            NetDisconnectReason gotReason = NetDisconnectReason.None;
            h.Server.PeerDisconnected += (_, reason) => gotReason = reason;

            c.Disconnect();
            h.Step(10);

            Assert.That(h.Server.Peers.Count, Is.EqualTo(0), h.SeedInfo);
            Assert.That(gotReason, Is.EqualTo(NetDisconnectReason.Requested));
            Assert.That(h.Failures.Count, Is.EqualTo(1));
            Assert.That(h.Failures[0].IsError, Is.False, "a requested disconnect is not an error");
        }

        [Test]
        public void ServerKick_TellsTheClient()
        {
            var h = new NetSimHarness(seed: 9);
            var c = h.ConnectClient();
            h.Server.DisconnectPeer(h.Server.Peers[0]);
            Assert.That(h.StepUntil(() => c.State == NetSessionState.Disconnected, 50), Is.True, h.SeedInfo);
            Assert.That(c.DisconnectReason, Is.EqualTo(NetDisconnectReason.Kicked));
            Assert.That(h.Server.Peers.Count, Is.EqualTo(0));
        }

        [Test]
        public void AcceptCarriesServerTick_ForClockSync()
        {
            var h = new NetSimHarness(seed: 10);
            h.Step(123); // let the server tick a while before anyone joins
            var c = h.ConnectClient();
            Assert.That(c.ServerTickAtAccept, Is.GreaterThanOrEqualTo(123), h.SeedInfo);
            Assert.That(c.PlayerId, Is.Not.Zero);
        }
    }
}
