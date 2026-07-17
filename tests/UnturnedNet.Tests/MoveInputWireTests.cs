using System.Text;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Golden bytes + round-trip for the MoveInput v2 wire format (PEI_CLIENT_PLAN §3 C2: the buttons byte,
    // bit 0 = jump -- the NetProtocol.Version 2->3 break). A failure here means the movement command's
    // format drifted -- an intentional change bumps NetProtocol.Version and re-goldens in the same commit.
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
        public void MoveInputV2_GoldenBytes()
        {
            // seq:16 + moveX:8(signed-normalized) + moveY:8 + yaw:11 + buttons:8 -- 51 bits -> 7 payload
            // bytes after the id byte. Goldened for Version = 3 (the buttons byte landed).
            var cmd = new MoveInput { Seq = 258, MoveX = -0.5f, MoveY = 1f, YawDegrees = 90f, Buttons = MoveInput.ButtonJump };
            var bytes = NetMessagePak.Pack(ReplicationIds.CommandMoveInput, cmd.Write);
            Assert.That(ToHex(bytes), Is.EqualTo("010201C07F000A00"));
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
