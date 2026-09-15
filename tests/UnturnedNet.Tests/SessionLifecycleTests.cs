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
        public void Accept_CarriesTheServersActiveHoliday()
        {
            // P3 holiday parity (wire v6, PREDICTION_GEOMETRY_DIAGNOSIS §2 footnote 1): ~285 placed props
            // carry COLLIDERS and are gated by activeHoliday, which each machine derived from its LOCAL
            // wall clock -- a client across a holiday boundary silently built a different static
            // collision set the content hash never catches. The server's holiday now rides the Accept;
            // the client builds ITS world with it, never the local clock's.
            var h = new NetSimHarness(seed: 4, activeHoliday: "CHRISTMAS");
            var c = h.ConnectClient("santa");
            Assert.That(c.ServerHoliday, Is.EqualTo("CHRISTMAS"),
                "the joining client learns the SERVER world's holiday from the Accept");

            var none = new NetSimHarness(seed: 5);   // default server: no holiday configured
            var c2 = none.ConnectClient("plain");
            Assert.That(c2.ServerHoliday, Is.EqualTo(""), "an unconfigured server sends the empty holiday");
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

        // v51: the reject now carries the REFUSING SERVER'S protocol version, because "version mismatch" on
        // its own tells a player nothing about who has to move. This is the live case, not a hypothetical --
        // the vox server sat on protocol 45 while clients were on 51, up and ticking and answering its status
        // port in 18 ms, and read to strawberry as simply "down" (2026-09-15).
        [Test]
        public void VersionMismatch_TellsTheClientWhatTheServerIsRunning()
        {
            var h = new NetSimHarness(seed: 2);
            var c = h.AddClient("timetraveler", version: (byte)(NetProtocol.Version + 1));
            c.Connect();
            Assert.That(h.StepUntil(() => c.State == NetSessionState.Disconnected, 100), Is.True, h.SeedInfo);
            Assert.That(c.RejectServerVersion, Is.EqualTo(NetProtocol.Version),
                "the refused client must learn which version the SERVER speaks, not just that they differ");

            string msg = NetRejectText.Describe(c.RejectReason, c.RejectServerVersion, (byte)(NetProtocol.Version + 1));
            Assert.That(msg, Does.Contain(NetProtocol.Version.ToString()), "message names the server's version");
            Assert.That(msg, Does.Contain((NetProtocol.Version + 1).ToString()), "message names ours too");
        }

        // A server older than v51 sends the reason byte and STOPS. That server is precisely the one whose
        // version we most want to report, so the missing byte must degrade to a worded-differently message --
        // never to a lost Reject, and never to a sentence claiming a version we were not told.
        [Test]
        public void Describe_DegradesWhenTheServerDidNotSayItsVersion()
        {
            string known = NetRejectText.Describe(NetRejectReason.VersionMismatch, 45, 51);
            string unknown = NetRejectText.Describe(NetRejectReason.VersionMismatch, 0, 51);
            Assert.That(known, Does.Contain("45").And.Contain("51"));
            Assert.That(unknown, Does.Not.Contain("45"), "cannot name a version the server never sent");
            Assert.That(unknown, Does.Not.Contain(" 0"), "0 is the ABSENT marker and must never reach the player");
            Assert.That(unknown, Is.Not.Empty.And.Not.EqualTo(known));
        }

        // None means nothing was heard from a server at all -- a timeout, a closed port, a wrong address.
        // Wording it as a refusal would invent an interaction that never happened, which is the same class
        // of wrong as showing the player nothing.
        [Test]
        public void Describe_NoneIsNotWordedAsARefusal()
        {
            string msg = NetRejectText.Describe(NetRejectReason.None);
            Assert.That(msg.ToLowerInvariant(), Does.Not.Contain("refus").And.Not.Contain("reject"));
            Assert.That(msg.ToLowerInvariant(), Does.Contain("offline").Or.Contain("reach"));
        }

        // Retry is only honest where waiting can actually change the answer. A banned or wrong-version client
        // retrying is a client being lied to by its own UI.
        [Test]
        public void OnlyTransientReasonsAreRetryable()
        {
            Assert.That(NetRejectText.IsRetryable(NetRejectReason.ServerStarting), Is.True);
            Assert.That(NetRejectText.IsRetryable(NetRejectReason.ServerFull), Is.True);
            foreach (var r in new[] { NetRejectReason.VersionMismatch, NetRejectReason.ContentMismatch,
                                      NetRejectReason.Banned, NetRejectReason.WrongPassword, NetRejectReason.PingTooHigh })
                Assert.That(NetRejectText.IsRetryable(r), Is.False, $"{r} cannot be fixed by trying again");
        }

        // The retry decision, which the client uses to tell "wait, it will clear" from "no, and it will stay
        // no". Retrying a ban is a progress bar over a definite refusal; NOT retrying a starting server sends
        // the player back to the menu a second before it would have let them in.
        [Test]
        public void RetryDecisionRespectsBothTheReasonAndTheBudget()
        {
            Assert.That(NetRejectText.ShouldRetryReject(NetRejectReason.ServerStarting, 0, 4), Is.True);
            Assert.That(NetRejectText.ShouldRetryReject(NetRejectReason.ServerFull, 3, 4), Is.True, "the last attempt in the budget is still an attempt");
            Assert.That(NetRejectText.ShouldRetryReject(NetRejectReason.ServerStarting, 4, 4), Is.False, "the budget is a limit, not a suggestion");
            foreach (var r in new[] { NetRejectReason.VersionMismatch, NetRejectReason.Banned,
                                      NetRejectReason.ContentMismatch, NetRejectReason.WrongPassword })
                Assert.That(NetRejectText.ShouldRetryReject(r, 0, 4), Is.False, $"{r} must never be retried, however much budget is left");
            // A zero budget must stop everything, including the retryable reasons -- otherwise "disable retry"
            // silently does not.
            Assert.That(NetRejectText.ShouldRetryReject(NetRejectReason.ServerStarting, 0, 0), Is.False);
        }

        // Every reason must produce its own sentence. A default that silently covers a new enum value is how
        // a future reason ships showing the generic "could not reach the server" and looks like a timeout.
        [Test]
        public void EveryReasonHasItsOwnDistinctMessage()
        {
            var seen = new System.Collections.Generic.Dictionary<string, NetRejectReason>();
            foreach (NetRejectReason r in System.Enum.GetValues(typeof(NetRejectReason)))
            {
                string m = NetRejectText.Describe(r, 45, 51);
                Assert.That(m, Is.Not.Empty, $"{r} has no message");
                if (seen.TryGetValue(m, out var other))
                    Assert.Fail($"{r} and {other} share a message -- a new reason fell through to the default: \"{m}\"");
                seen[m] = r;
                Assert.That(NetRejectText.Short(r), Is.Not.Empty, $"{r} has no short form");
            }
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
