using System;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // NetMessagePak.Pack must never ship a TRUNCATED message. Until 2026-09-02 a payload larger than the
    // buffer set NetPakWriter.BufferOverflow, wrote nothing, and Pack returned whatever had fit -- a
    // well-formed datagram with a length prefix promising bytes that were not there. The receiver's TryRead
    // failed and the whole message was dropped as malformed. That is the mechanism behind "names and pfps
    // aren't showing on the server" (the profile command carries a PNG); this pins the mechanism itself.
    [TestFixture]
    public class NetMessagePakTests
    {
        static byte[] Payload(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

        [Test]
        public void a_payload_larger_than_the_default_buffer_is_packed_whole_not_truncated()
        {
            var payload = Payload(3000, 1);   // > 256 (default) and > 1024 (first growth step)
            var msg = NetMessagePak.Pack(200, w => { w.WriteUInt32((uint)payload.Length); w.WriteBytes(payload); });
            // TEETH: the pre-fix Pack returned 5 bytes here (id + length, no payload) -- this is the assertion
            // that catches it, and the one that guards the bug from coming back through any other command.
            Assert.That(msg.Length, Is.EqualTo(1 + 4 + payload.Length), "id + u32 length + every payload byte");
            Assert.That(msg[0], Is.EqualTo(200), "the message id leads");
            var r = new SDG.NetPak.NetPakReader();
            r.SetBufferSegment(msg, msg.Length);
            Assert.That(r.ReadUInt8(out byte id) && id == 200, Is.True);
            Assert.That(r.ReadUInt32(out uint len) && len == payload.Length, Is.True, "the length prefix survived");
            var back = new byte[len];
            Assert.That(r.ReadBytes(back), Is.True, "the bytes the prefix promised are actually there");
            Assert.That(back, Is.EqualTo(payload), "byte-for-byte");
        }

        [Test]
        public void a_payload_at_the_reliable_ceiling_still_packs_and_one_past_it_throws_instead_of_truncating()
        {
            int fits = NetMessagePak.MaxMessageBytes - 1 - 4;   // id + u32 length + bytes == exactly the ceiling
            var big = Payload(fits, 2);
            var msg = NetMessagePak.Pack(201, w => { w.WriteUInt32((uint)big.Length); w.WriteBytes(big); });
            Assert.That(msg.Length, Is.EqualTo(NetMessagePak.MaxMessageBytes), "a message the transport can carry is built whole");

            var tooBig = Payload(fits + 1, 3);
            // The transport would refuse this anyway (NetSession.SendReliable returns false past the ceiling);
            // failing HERE names the message instead of silently losing it after a successful-looking pack.
            Assert.Throws<InvalidOperationException>(() =>
                NetMessagePak.Pack(202, w => { w.WriteUInt32((uint)tooBig.Length); w.WriteBytes(tooBig); }));
        }

        [Test]
        public void a_small_payload_is_unchanged_by_the_growth_path()
        {
            // the common case must stay byte-identical: one buffer, no retry, same bytes as before
            var msg = NetMessagePak.Pack(7, w => { w.WriteUInt16(0xBEEF); w.WriteUInt8(9); });
            Assert.That(msg, Is.EqualTo(new byte[] { 7, 0xEF, 0xBE, 9 }).Or.EqualTo(new byte[] { 7, 0xBE, 0xEF, 9 }), "id + the two fields, nothing else");
        }
    }
}
