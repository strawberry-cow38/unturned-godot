using System.Text;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Golden bytes + round-trip for the MoveInput wire format (demo walkers / loopback -- the shell
    // client streams PlayerStateCommand since v9). The DATAGRAM is a MoveInputPacket since C1
    // (count:2 bits + count MoveInput entries oldest-first); each entry is seq:16 + moveX:8 + moveY:8
    // + yaw:11 + buttons:8 = 51 bits. v9 (mp-clientauth-foot) REMOVED the C2 hasClaim/ClaimedPos claim
    // fields -- the server ack band they fed is deleted (re-goldened in the same commit as the
    // NetProtocol.Version 9 bump). A failure here means the movement command's format drifted -- an
    // intentional change bumps NetProtocol.Version and re-goldens in the same commit.
    [TestFixture]
    public class MoveInputWireTests
    {
        static string ToHex(byte[] buffer)
        {
            var sb = new StringBuilder(buffer.Length * 2);
            foreach (byte b in buffer) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        [Test]
        public void MoveInputPacket_SingleEntry_GoldenBytes()
        {
            // count:2 + one 51-bit entry = 53 bits -> 7 payload bytes after the id byte.
            // Re-goldened for Version = 9 (the claim bit is gone; the entry payload is the v3/v4 shape).
            var pkt = new MoveInputPacket { Count = 1, I0 = new MoveInput { Seq = 258, MoveX = -0.5f, MoveY = 1f, YawDegrees = 90f, Buttons = MoveInput.ButtonJump } };
            var bytes = NetMessagePak.Pack(ReplicationIds.CommandMoveInput, pkt.Write);
            Assert.That(ToHex(bytes), Is.EqualTo(GoldenSingle));
        }

        [Test]
        public void MoveInputPacket_ThreeEntries_GoldenBytes()
        {
            // the steady-state redundancy shape: the newest input + the 2 previous, oldest-first.
            var pkt = new MoveInputPacket
            {
                Count = 3,
                I0 = new MoveInput { Seq = 258, MoveX = -0.5f, MoveY = 1f, YawDegrees = 90f, Buttons = MoveInput.ButtonJump },
                I1 = new MoveInput { Seq = 259, MoveX = 0f, MoveY = 1f, YawDegrees = 91f, Buttons = MoveInput.PackStance(EPlayerStance.SPRINT) },
                I2 = new MoveInput { Seq = 260, MoveX = 0.25f, MoveY = -1f, YawDegrees = 271f, Buttons = 0 },
            };
            var bytes = NetMessagePak.Pack(ReplicationIds.CommandMoveInput, pkt.Write);
            Assert.That(ToHex(bytes), Is.EqualTo(GoldenTriple));
        }

        // goldened for Version = 9 on first landing; locked from then on
        const string GoldenSingle = "01090400FF012800";
        const string GoldenTriple = "010B0400FF0128602000E0AF4002040120FF050600";

        [Test]
        public void MoveInputPacket_RoundTrip_AndRejects()
        {
            var w = new NetPakWriter { buffer = new byte[64] };
            var r = new NetPakReader();
            // 1..3 entries round-trip with every field intact
            for (byte count = 1; count <= MoveInputPacket.MaxInputs; count++)
            {
                var pkt = new MoveInputPacket { Count = count };
                for (int i = 0; i < count; i++)
                {
                    var m = new MoveInput { Seq = (ushort)(100 + i), MoveX = i * 0.5f - 0.5f, MoveY = 1f, YawDegrees = 30f * (i + 1), Buttons = (byte)(i == 1 ? MoveInput.ButtonJump : 0) };
                    if (i == 0) pkt.I0 = m; else if (i == 1) pkt.I1 = m; else pkt.I2 = m;
                }
                w.Reset();
                pkt.Write(w);
                w.Flush();
                r.Reset();
                r.SetBufferSegment(w.buffer, w.writeByteIndex);
                Assert.That(MoveInputPacket.TryRead(r, out var read), Is.True);
                Assert.That(read.Count, Is.EqualTo(count));
                for (int i = 0; i < count; i++)
                {
                    Assert.That(read.Get(i).Seq, Is.EqualTo((ushort)(100 + i)), $"entry {i} seq survives (count {count})");
                    Assert.That(read.Get(i).Jump, Is.EqualTo(i == 1), $"entry {i} buttons survive (count {count})");
                }
            }
            // count 0 is malformed (a datagram always carries at least the newest input)
            w.Reset();
            w.WriteBits(0u, 2);
            w.Flush();
            r.Reset();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(MoveInputPacket.TryRead(r, out _), Is.False, "count 0 is rejected");
            // a truncated packet (count says 3, only 2 entries present) is rejected, never misparsed
            w.Reset();
            w.WriteBits(3u, 2);
            new MoveInput { Seq = 1, MoveY = 1f }.Write(w);
            new MoveInput { Seq = 2, MoveY = 1f }.Write(w);
            w.Flush();
            r.Reset();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(MoveInputPacket.TryRead(r, out _), Is.False, "a truncated packet is rejected");
        }

        [Test]
        public void MoveInputV2_RoundTrip_CarriesJumpBit()
        {
            var w = new NetPakWriter { buffer = new byte[64] };
            w.Reset();
            new MoveInput { Seq = 7, MoveX = 0.25f, MoveY = -1f, YawDegrees = 271f, Buttons = MoveInput.ButtonJump }.Write(w);
            w.Flush();

            var r = new NetPakReader();
            r.Reset();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(MoveInput.TryRead(r, out var read), Is.True);
            Assert.That(read.Seq, Is.EqualTo(7));
            Assert.That(read.Jump, Is.True, "bit 0 = jump must survive the round trip");
            Assert.That(read.Buttons, Is.EqualTo(MoveInput.ButtonJump));
            Assert.That(read.MoveY, Is.LessThan(-0.99f));

            // and buttons = 0 reads back as no jump (the common idle case)
            w.Reset();
            new MoveInput { Seq = 8, MoveX = 0f, MoveY = 0f, YawDegrees = 0f, Buttons = 0 }.Write(w);
            w.Flush();
            r.Reset();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(MoveInput.TryRead(r, out var idle), Is.True);
            Assert.That(idle.Jump, Is.False);
        }

        [Test]
        public void MoveInput_StanceBits_RoundTrip()
        {
            // the mp-inchworm-era stance bits: the RESULTING on-foot stance rides buttons bits 1-2
            // (since v9 the SHELL's stance rides PlayerStateCommand instead, same encoding -- this locks
            // the shared codec + the jump bit's independence).
            var w = new NetPakWriter { buffer = new byte[64] };
            var r = new NetPakReader();
            foreach (var stance in new[] { EPlayerStance.STAND, EPlayerStance.SPRINT, EPlayerStance.CROUCH, EPlayerStance.PRONE })
            {
                w.Reset();
                new MoveInput { Seq = 5, MoveY = 1f, Buttons = (byte)(MoveInput.ButtonJump | MoveInput.PackStance(stance)) }.Write(w);
                w.Flush();
                r.Reset();
                r.SetBufferSegment(w.buffer, w.writeByteIndex);
                Assert.That(MoveInput.TryRead(r, out var read), Is.True);
                Assert.That(read.Stance, Is.EqualTo(stance), $"stance {stance} must survive the round trip");
                Assert.That(read.Jump, Is.True, "the jump bit must be unaffected by the stance bits");
            }
            // only the four on-foot stances exist on the wire; anything else degrades to STAND
            Assert.That(new MoveInput { Buttons = MoveInput.PackStance(EPlayerStance.DRIVING) }.Stance, Is.EqualTo(EPlayerStance.STAND));
            // buttons = 0 (idle / any pre-stance payload) decodes as STAND -- the old stand-walk meaning
            Assert.That(new MoveInput { Buttons = 0 }.Stance, Is.EqualTo(EPlayerStance.STAND));
            Assert.That(new MoveInput { Buttons = MoveInput.ButtonJump }.Stance, Is.EqualTo(EPlayerStance.STAND));
        }

        [Test]
        public void MoveInputV1_Truncated_IsRejected()
        {
            // a v1-shaped payload (no buttons byte, 43 bits -> 6 bytes) must fail TryRead, not misparse:
            // the version gate rejects old peers at the handshake, this locks the belt-and-braces behavior
            var w = new NetPakWriter { buffer = new byte[64] };
            w.Reset();
            w.WriteUInt16(9);
            w.WriteSignedNormalizedFloat(0f, 8);
            w.WriteSignedNormalizedFloat(1f, 8);
            w.WriteDegrees(180f, NetQuantization.YawBits);
            w.Flush();

            var r = new NetPakReader();
            r.Reset();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(MoveInput.TryRead(r, out _), Is.False, "a truncated (v1) MoveInput must be rejected");
        }
    }
}
