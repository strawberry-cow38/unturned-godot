using System.Text;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Golden bytes + round-trip for the MoveInput wire format. The DATAGRAM is a MoveInputPacket since
    // C1+C2 (CLIENT_PREDICTION_PLAN §4.2, the Version 4->5 break): count:2 bits + count MoveInput
    // entries oldest-first, each seq:16 + moveX:8 + moveY:8 + yaw:11 + buttons:8 + hasClaim:1
    // (+ claimed post-move position on the snapshot grid when set -- retail's clientPosition,
    // U3 PlayerInput.cs:867-873). A failure here means the movement command's format drifted -- an
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
        public void MoveInputPacket_SingleEntry_NoClaim_GoldenBytes()
        {
            // the claimless spawn/demo-walker shape: count:2 + one 52-bit entry (51 + hasClaim:0) = 54
            // bits -> 7 payload bytes after the id byte. The entry is the exact v3/v4 golden input;
            // goldened for Version = 5 (the redundancy count + claim bit landed).
            var pkt = new MoveInputPacket { Count = 1, I0 = new MoveInput { Seq = 258, MoveX = -0.5f, MoveY = 1f, YawDegrees = 90f, Buttons = MoveInput.ButtonJump } };
            var bytes = NetMessagePak.Pack(ReplicationIds.CommandMoveInput, pkt.Write);
            Assert.That(ToHex(bytes), Is.EqualTo("01090400FF012800"));
        }

        [Test]
        public void MoveInputPacket_SingleEntry_WithClaim_GoldenBytes()
        {
            // the real client's shape: the claimed post-move position (retail's clientPosition) rides
            // the snapshot's exact grid -- x:20 + y:18 + z:20 bits after the claim bit = 112 bits total
            // -> 14 payload bytes after the id byte.
            var pkt = new MoveInputPacket
            {
                Count = 1,
                I0 = new MoveInput { Seq = 258, MoveX = -0.5f, MoveY = 1f, YawDegrees = 90f, Buttons = MoveInput.ButtonJump,
                                     HasClaim = true, ClaimedPos = new UnityEngine.Vector3(12.5f, -3.25f, -100.125f) },
            };
            var bytes = NetMessagePak.Pack(ReplicationIds.CommandMoveInput, pkt.Write);
            Assert.That(ToHex(bytes), Is.EqualTo("01090400FF0128200301F9016F0E1C"));
        }

        [Test]
        public void MoveInputPacket_ThreeEntries_GoldenBytes()
        {
            // the steady-state shape: the newest input + the 2 previous, oldest-first, consecutive seqs,
            // claims on every entry (a real client always claims).
            var pkt = new MoveInputPacket
            {
                Count = 3,
                I0 = new MoveInput { Seq = 258, MoveX = -0.5f, MoveY = 1f, YawDegrees = 90f, Buttons = MoveInput.ButtonJump,
                                     HasClaim = true, ClaimedPos = new UnityEngine.Vector3(1f, 2f, 3f) },
                I1 = new MoveInput { Seq = 259, MoveX = 0f, MoveY = 1f, YawDegrees = 91f, Buttons = MoveInput.PackStance(SDG.Unturned.EPlayerStance.SPRINT),
                                     HasClaim = true, ClaimedPos = new UnityEngine.Vector3(1f, 2f, 3.09f) },
                I2 = new MoveInput { Seq = 260, MoveX = 0.25f, MoveY = -1f, YawDegrees = 271f, Buttons = 0,
                                     HasClaim = true, ClaimedPos = new UnityEngine.Vector3(1f, 2f, 3.18f) },
            };
            var bytes = NetMessagePak.Pack(ReplicationIds.CommandMoveInput, pkt.Write);
            Assert.That(ToHex(bytes), Is.EqualTo("010B0400FF012860000104020C10602000E0AF400203082010608017040120FF05061840008100037401"));
        }

        [Test]
        public void MoveInput_Claim_RoundTrip_GridExact()
        {
            // the claim must survive the round trip EXACTLY on the snapshot position grid: an adopted
            // claim becomes the entity position, and the ack the owner receives must equal its recorded
            // prediction to the grid point (that equality is what makes adoption a ZERO correction)
            var w = new NetPakWriter { buffer = new byte[64] };
            w.Reset();
            var claim = new UnityEngine.Vector3(123.456f, 45.678f, -789.012f);
            new MoveInput { Seq = 9, MoveY = 1f, HasClaim = true, ClaimedPos = claim }.Write(w);
            w.Flush();
            var r = new NetPakReader();
            r.Reset();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(MoveInput.TryRead(r, out var read), Is.True);
            Assert.That(read.HasClaim, Is.True);
            Assert.That(read.ClaimedPos, Is.EqualTo(PlayerReplication.Quantize(claim)),
                        "the wire claim IS the quantized claim -- grid-exact, no extra tolerance anywhere");

            // and a claimless entry reads back claimless (never a fabricated zero-claim)
            w.Reset();
            new MoveInput { Seq = 10, MoveY = 1f }.Write(w);
            w.Flush();
            r.Reset();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(MoveInput.TryRead(r, out var bare), Is.True);
            Assert.That(bare.HasClaim, Is.False, "no claim bit -> no claim; the server must have nothing to adopt");
        }

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
        public void MoveInput_StanceBits_RoundTrip_AndNoWireBreak()
        {
            // the mp-inchworm fix: the RESULTING on-foot stance rides buttons bits 1-2 so the server
            // avatar integrates at the speed the shell predicted. Same byte, same layout -- NOT a wire
            // break (the golden above still holds); this locks the codec + the jump bit's independence.
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
            // the version gate rejects v2 peers at the handshake, this locks the belt-and-braces behavior
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
