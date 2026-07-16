using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The Phase-1 soak (MP_PLAN §4): 10k ticks of continuous bidirectional traffic over an adverse link,
    // asserting the reliable window never stalls and every accounting structure stays bounded -- the
    // properties that keep a long-lived session from slowly rotting. Fully deterministic; ~1 s wall clock.
    [TestFixture]
    public class SessionSoakTests
    {
        static byte[] Numbered(int id, int length)
        {
            var data = new byte[length];
            data[0] = (byte)id;
            data[1] = (byte)(id >> 8);
            data[2] = (byte)(id >> 16);
            for (int i = 3; i < length; i++) data[i] = (byte)(id * 31 + i);
            return data;
        }

        static int NumberOf(byte[] data) => data[0] | (data[1] << 8) | (data[2] << 16);

        [Test]
        public void Soak_10kTicks_WindowNeverStalls_AccountingStaysBounded()
        {
            const int seed = 20260716;
            const int soakTicks = 10_000;
            const int maxBacklog = 400; // messages sent-but-undelivered; a stalled window blows past this fast

            var adverse = new FaultyLinkConfig { LossProbability = 0.25, DuplicateProbability = 0.03, ReorderJitterTicks = 3, LatencyTicks = 1 };
            var h = new NetSimHarness(seed,
                new FaultyLinkConfig { LossProbability = 0.25, DuplicateProbability = 0.03, ReorderJitterTicks = 3, LatencyTicks = 1 },
                adverse);
            var c = h.ConnectClient();
            var peer = h.Server.Peers[0];

            int clientSent = 0, serverSent = 0;
            var serverGot = new List<int>();
            var clientGot = new List<int>();
            int clientSnapshotsGot = 0, lastSnapshotId = -1;

            for (int tick = 0; tick < soakTicks; tick++)
            {
                // client: one small reliable command per tick; server: one unreliable snapshot per tick
                // plus a reliable event every 5th tick -- both directions exercised simultaneously
                c.SendReliable(Numbered(clientSent++, 8));
                peer.SendUnreliableSequenced(Numbered(tick, 16));
                if (tick % 5 == 0) peer.SendReliable(Numbered(serverSent++, 8));

                h.Step();

                while (peer.TryReceiveReliable(out var msg)) serverGot.Add(NumberOf(msg));
                while (c.TryReceiveReliable(out var msg)) clientGot.Add(NumberOf(msg));
                while (c.TryReceiveUnreliable(out var msg))
                {
                    int id = NumberOf(msg);
                    if (id <= lastSnapshotId) Assert.Fail($"stale snapshot surfaced at tick {tick} ({h.SeedInfo})");
                    lastSnapshotId = id;
                    clientSnapshotsGot++;
                }

                // ---- bounded-accounting invariants, checked EVERY tick ----
                if (c.Session.InFlightMessageCount > NetProtocol.SendWindowMessages)
                    Assert.Fail($"client in-flight window exceeded at tick {tick}: {c.Session.InFlightMessageCount} ({h.SeedInfo})");
                if (peer.Session.ReassemblyCount > NetProtocol.RecvWindowMessages)
                    Assert.Fail($"server reassembly window exceeded at tick {tick}: {peer.Session.ReassemblyCount} ({h.SeedInfo})");
                int backlog = clientSent - serverGot.Count;
                if (backlog > maxBacklog)
                    Assert.Fail($"reliable window stalled: backlog {backlog} at tick {tick} ({h.SeedInfo})");
                if (c.Session.Diag.OutOfWindowDropped + peer.Session.Diag.OutOfWindowDropped > 0)
                    Assert.Fail($"window discipline violated at tick {tick} ({h.SeedInfo})");
            }

            Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), $"session survived the soak ({h.SeedInfo})");
            Assert.That(h.Failures.Count, Is.Zero, h.SeedInfo);

            // stop sending, let the tail drain -- everything sent must eventually land, still in order
            bool drained = h.StepUntil(() =>
            {
                while (peer.TryReceiveReliable(out var msg)) serverGot.Add(NumberOf(msg));
                while (c.TryReceiveReliable(out var msg)) clientGot.Add(NumberOf(msg));
                return serverGot.Count == clientSent && clientGot.Count == serverSent;
            }, 2000);
            Assert.That(drained, Is.True,
                $"tail must drain: server got {serverGot.Count}/{clientSent}, client got {clientGot.Count}/{serverSent} ({h.SeedInfo})");

            for (int i = 0; i < serverGot.Count; i++)
                if (serverGot[i] != i) Assert.Fail($"client->server order broke at {i}: got {serverGot[i]} ({h.SeedInfo})");
            for (int i = 0; i < clientGot.Count; i++)
                if (clientGot[i] != i) Assert.Fail($"server->client order broke at {i}: got {clientGot[i]} ({h.SeedInfo})");

            // post-drain: once the last acks make it back through the lossy link, all send-side state retires
            bool retired = h.StepUntil(() =>
                c.Session.InFlightMessageCount == 0 && c.Session.PendingSendCount == 0
                && peer.Session.InFlightMessageCount == 0 && peer.Session.ReassemblyCount == 0, 500);
            Assert.That(retired, Is.True,
                $"send-side accounting must fully retire after delivery: client in-flight {c.Session.InFlightMessageCount}, " +
                $"pending {c.Session.PendingSendCount}, server in-flight {peer.Session.InFlightMessageCount} ({h.SeedInfo})");

            // sanity on the sim itself: the adverse link really was adverse, RTT estimation converged
            Assert.That(c.Session.Diag.ReliableRetransmits, Is.GreaterThan(0), h.SeedInfo);
            Assert.That(c.Session.Diag.StaleUnreliableDropped, Is.GreaterThan(0),
                $"snapshots flow server->client, reorder must have produced stale drops there ({h.SeedInfo})");
            Assert.That(clientSnapshotsGot, Is.GreaterThan(soakTicks / 2), $"most snapshots should land ({h.SeedInfo})");
            Assert.That(c.Session.SrttTicks, Is.InRange(0.5, 25.0), $"smoothed RTT should be sane, got {c.Session.SrttTicks} ({h.SeedInfo})");
        }
    }
}
