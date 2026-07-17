using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Hardening review M1: reliable reassembly is bounded. Without the cap, a peer that never completes
    // the window-head message could pin ~77 MB per connection (255 msgs x 254 frags x 1183 B). The guard:
    // total buffered fragment bytes per session cap at NetProtocol.MaxReassemblyBufferBytes, incomplete
    // messages older than ReassemblyTtlTicks are evicted, and either event latches
    // NetSession.ReassemblyBudgetExceeded -- which the server session turns into a kick.
    [TestFixture]
    public class ReassemblyBudgetTests
    {
        static NetSession NewSession(out List<byte[]> sent)
        {
            var outbox = new List<byte[]>();
            sent = outbox;
            return new NetSession((buffer, length) =>
            {
                var copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                outbox.Add(copy);
            });
        }

        /// <summary>One ReliableOrdered fragment datagram, crafted the way a hostile client would.</summary>
        static byte[] Fragment(ushort seq, ushort msgId, byte fragIdx, byte fragCount, int payloadLen)
        {
            var w = new NetPakWriter { buffer = new byte[NetProtocol.MaxDatagramBytes] };
            w.Reset();
            NetProtocol.WriteHeader(w, new NetProtocol.Header
            {
                MagicByte = NetProtocol.Magic,
                Version = NetProtocol.Version,
                Channel = NetChannel.ReliableOrdered,
                Seq = seq,
                Ack = 0,
                AckBits = 0,
            });
            w.WriteUInt16(msgId);
            w.WriteUInt8(fragIdx);
            w.WriteUInt8(fragCount);
            w.WriteUInt16((ushort)payloadLen);
            w.WriteBytes(new byte[payloadLen], 0, payloadLen);
            w.Flush();
            var data = new byte[w.writeByteIndex];
            Buffer.BlockCopy(w.buffer, 0, data, 0, w.writeByteIndex);
            return data;
        }

        [Test]
        public void FragmentFlood_NeverBuffersPastTheCap_AndLatchesTheSession()
        {
            var s = NewSession(out _);
            ushort seq = 1;
            // the M1 attack shape: every message declares 255 fragments and never sends the last one,
            // so nothing ever completes and nothing ever delivers
            for (ushort msgId = 0; msgId < 8 && !s.ReassemblyBudgetExceeded; msgId++)
                for (byte frag = 0; frag < 254; frag++)
                {
                    var dg = Fragment(seq++, msgId, frag, 255, NetProtocol.MaxFragmentPayload);
                    if (seq == 0) seq = 1;
                    s.HandleDatagram(1, dg, dg.Length);
                }

            Assert.That(s.ReassemblyBudgetExceeded, Is.True, "the flood tripped the budget");
            Assert.That(s.ReassemblyBufferedBytes, Is.LessThanOrEqualTo(NetProtocol.MaxReassemblyBufferBytes),
                "buffered bytes never exceed the cap");
            Assert.That(s.Diag.ReassemblyOverflowDropped, Is.GreaterThan(0), "over-cap fragments were refused, not stored");
            Assert.That(s.Diag.ReliableMessagesDelivered, Is.Zero);
        }

        [Test]
        public void IncompleteMessage_IsEvictedAfterTtl()
        {
            var s = NewSession(out _);
            // 4 of 10 fragments arrive at tick 1; the rest never come
            for (byte frag = 0; frag < 4; frag++)
            {
                var dg = Fragment((ushort)(frag + 1), 0, frag, 10, 100);
                s.HandleDatagram(1, dg, dg.Length);
            }
            Assert.That(s.ReassemblyCount, Is.EqualTo(1));
            Assert.That(s.ReassemblyBufferedBytes, Is.EqualTo(400));

            s.Tick(NetProtocol.ReassemblyTtlTicks);       // one tick before expiry
            Assert.That(s.ReassemblyCount, Is.EqualTo(1), "still within the TTL");
            s.Tick(NetProtocol.ReassemblyTtlTicks + 1);   // past expiry
            Assert.That(s.ReassemblyCount, Is.Zero, "the rotting message was evicted");
            Assert.That(s.ReassemblyBufferedBytes, Is.Zero, "its bytes were released");
            Assert.That(s.Diag.ReassemblyEvicted, Is.EqualTo(1));
            Assert.That(s.ReassemblyBudgetExceeded, Is.True, "eviction latches the session (acked frags are gone for good)");
        }

        [Test]
        public void CompleteMessages_WaitingForTheHead_AreNotEvicted()
        {
            var s = NewSession(out _);
            // msgId 1 is COMPLETE but must wait for msgId 0 (in-order delivery); it must survive any TTL
            var dg1 = Fragment(1, 1, 0, 1, 64);
            s.HandleDatagram(1, dg1, dg1.Length);
            s.Tick(NetProtocol.ReassemblyTtlTicks * 3);
            Assert.That(s.ReassemblyCount, Is.EqualTo(1), "a complete parked message is not 'rotting'");
            Assert.That(s.ReassemblyBudgetExceeded, Is.False);

            // the head finally arrives -> both deliver in order
            var dg0 = Fragment(2, 0, 0, 1, 32);
            s.HandleDatagram(NetProtocol.ReassemblyTtlTicks * 3, dg0, dg0.Length);
            Assert.That(s.Diag.ReliableMessagesDelivered, Is.EqualTo(2));
            Assert.That(s.ReassemblyBufferedBytes, Is.Zero);
        }

        [Test]
        public void MaxSizeReliableMessage_StillDelivers_UnderTheCap()
        {
            // the legit worst case (a single MaxReliableMessageBytes message, e.g. a join snapshot) is
            // far inside the budget and must be unaffected by the M1 guard
            var h = new NetSimHarness(seed: 77);
            var c = h.ConnectClient();
            var payload = new byte[NetProtocol.MaxReliableMessageBytes];
            payload[0] = 0xAB; payload[payload.Length - 1] = 0xCD;
            Assert.That(c.SendReliable(payload), Is.True);

            byte[] got = null;
            var peer = h.Server.Peers[0];
            Assert.That(h.StepUntil(() => peer.TryReceiveReliable(out got), 600), Is.True, h.SeedInfo);
            Assert.That(got.Length, Is.EqualTo(payload.Length));
            Assert.That(got[0], Is.EqualTo(0xAB));
            Assert.That(got[got.Length - 1], Is.EqualTo(0xCD));
            Assert.That(peer.Session.ReassemblyBudgetExceeded, Is.False, "legit traffic never trips the guard");
            Assert.That(peer.Session.ReassemblyBufferedBytes, Is.Zero, "fully drained after delivery");
        }

        [Test]
        public void Server_KicksThePeer_ThatBreaksTheReassemblyBudget()
        {
            var h = new NetSimHarness(seed: 78);
            var c = h.ConnectClient("griefer");
            var peer = h.Server.Peers[0];

            // inject the attack shape straight into the peer's session (transport-agnostic seam)
            ushort seq = 100;
            for (ushort msgId = 0; msgId < 8 && !peer.Session.ReassemblyBudgetExceeded; msgId++)
                for (byte frag = 0; frag < 254; frag++)
                {
                    var dg = Fragment(seq++, msgId, frag, 255, NetProtocol.MaxFragmentPayload);
                    if (seq == 0) seq = 1;
                    peer.Session.HandleDatagram(h.Server.CurrentTick, dg, dg.Length);
                }
            Assert.That(peer.Session.ReassemblyBudgetExceeded, Is.True);

            h.Step(2);
            Assert.That(h.Server.Peers.Count, Is.Zero, "the abusive session was removed");
            Assert.That(h.Failures.Count, Is.EqualTo(1));
            StringAssert.Contains("reassembly", h.Failures[0].Reason);
            Assert.That(h.Failures[0].IsError, Is.True);
        }
    }
}
