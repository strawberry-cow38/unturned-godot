using System;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Golden byte tests locking wire format v1 (MP_PLAN §6): the exact hex for fixed inputs. If any of
    // these fail, the format drifted -- an intentional change must bump NetProtocol.Version and re-golden
    // these constants in the same commit.
    [TestFixture]
    public class PacketHeaderGoldenTests
    {
        static string ToHex(byte[] buffer, int length)
        {
            var sb = new System.Text.StringBuilder(length * 2);
            for (int i = 0; i < length; i++) sb.Append(buffer[i].ToString("X2"));
            return sb.ToString();
        }

        [Test]
        public void Header_GoldenBytes()
        {
            // magic:8 + version:8 + channel:3 + seq:16 + ack:16 + ackBits:32 = 83 bits -> 11 bytes,
            // LSB-first NetPak packing. Hand-derived, locked forever (for Version = 1).
            var w = new NetPakWriter { buffer = new byte[32] };
            w.Reset();
            NetProtocol.WriteHeader(w, new NetProtocol.Header
            {
                MagicByte = NetProtocol.Magic, // 0x75
                Version = 1,
                Channel = NetChannel.ReliableOrdered, // 1
                Seq = 0x1234,
                Ack = 0x00FF,
                AckBits = 0xDEADBEEF,
            });
            w.Flush();
            Assert.That(w.writeByteIndex, Is.EqualTo(11), "83 header bits round up to 11 bytes");
            Assert.That(ToHex(w.buffer, w.writeByteIndex), Is.EqualTo("7501A191F80778F76DF506"));
        }

        [Test]
        public void KeepAliveDatagram_GoldenBytes()
        {
            // The first datagram a fresh session emits if asked to keepalive: seq=1 (0 is reserved),
            // nothing received yet so ack=0/ackBits=0, control type KeepAlive=5. Header + type = 91 bits.
            // Re-goldened for Version=11 (mp-sp-unify wave 2: SystemVitals(13) + SystemContainers(14) +
            // SystemAnimals(15) registered as empty stubs + commands 28-31 reserved -- ONLY the version byte
            // moved 0A->0B, per the §6 re-golden-with-version-bump discipline); before that Version=10
            // (mp-event-coalesce: the combat commands fold redundantly into the
            // PlayerStateCommand transform stream + a per-owner combat-seq ack -- only the version byte
            // moved, per the §6 re-golden-with-version-bump discipline); before that Version=9
            // (mp-clientauth-foot: on-foot client authority -- CommandPlayerState
            // 27 + EventPlayerRecov 31, MoveInput drops the C2 claim fields; v8 reserved by the pending
            // owner-vitals branch); before that Version=7 (mp-geomfix P3: Accept gained the server's
            // activeHoliday string), Version=6 (mp-predict-a A2: vehicle client authority -- CommandVehicleState 26 +
            // EventVehicleRecov 29), Version=5 (mp-predict-c C1: the MoveInput datagram became
            // MoveInputPacket carrying the last 3 inputs), Version=4 (mp-exitfix: VehicleExitedEvent gained
            // the authoritative exit spot), Version=3 (PEI client C2: MoveInput gained the buttons byte) and
            // Version=2 (Phase 4: Connect gained contentHash) -- each time the only byte that moved is the
            // version byte, per the §6 re-golden-with-version-bump discipline.
            byte[] captured = null;
            int capturedLen = 0;
            var session = new NetSession((buf, len) => { captured = (byte[])buf.Clone(); capturedLen = len; });
            session.SendControl(NetControlType.KeepAlive);
            Assert.That(captured, Is.Not.Null);
            Assert.That(capturedLen, Is.EqualTo(12));
            Assert.That(ToHex(captured, capturedLen), Is.EqualTo("750B08000000000000002800"));
        }

        [Test]
        public void Header_RoundTrips()
        {
            var original = new NetProtocol.Header
            {
                MagicByte = NetProtocol.Magic,
                Version = 7,
                Channel = NetChannel.UnreliableSequenced,
                Seq = 65535,
                Ack = 32768,
                AckBits = 0x80000001,
            };
            var w = new NetPakWriter { buffer = new byte[32] };
            w.Reset();
            NetProtocol.WriteHeader(w, original);
            w.Flush();

            var r = new NetPakReader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(NetProtocol.TryReadHeader(r, out var read), Is.True);
            Assert.That(read.MagicByte, Is.EqualTo(original.MagicByte));
            Assert.That(read.Version, Is.EqualTo(original.Version));
            Assert.That(read.Channel, Is.EqualTo(original.Channel));
            Assert.That(read.Seq, Is.EqualTo(original.Seq));
            Assert.That(read.Ack, Is.EqualTo(original.Ack));
            Assert.That(read.AckBits, Is.EqualTo(original.AckBits));
        }

        [Test]
        public void TruncatedHeader_IsRejected()
        {
            var r = new NetPakReader();
            r.SetBufferSegment(new byte[] { 0x75, 0x01, 0x08 }, 3);
            Assert.That(NetProtocol.TryReadHeader(r, out _), Is.False);
        }

        [Test]
        public void SeqMath_WrapsCorrectly()
        {
            Assert.That(NetSeq.IsNewer(2, 1), Is.True);
            Assert.That(NetSeq.IsNewer(1, 2), Is.False);
            Assert.That(NetSeq.IsNewer(1, 1), Is.False);
            Assert.That(NetSeq.IsNewer(1, 65535), Is.True, "wrap: 1 comes after 65535");
            Assert.That(NetSeq.IsNewer(65535, 1), Is.False);
            Assert.That(NetSeq.Diff(1, 65535), Is.EqualTo(2));
            Assert.That(NetSeq.Diff(65535, 1), Is.EqualTo(-2));
            Assert.That(NetSeq.Diff(5, 5), Is.EqualTo(0));
            Assert.That(NetSeq.IsNewerOrEqual(5, 5), Is.True);
            Assert.That(NetSeq.IsNewer(unchecked((ushort)(65535 + 32767)), 65535), Is.True, "just inside the half-space window");
        }

        [Test]
        public void FragmentBudget_FitsTheDatagramBudget()
        {
            // header 83 bits + msgId:16 + fragIdx:8 + fragCount:8 + byteLen:16 = 131 bits -> 17 bytes aligned
            Assert.That((NetProtocol.HeaderBits + 48 + 7) / 8 + NetProtocol.MaxFragmentPayload,
                Is.EqualTo(NetProtocol.MaxDatagramBytes));
            // header 83 bits + byteLen:16 = 99 bits -> 13 bytes aligned
            Assert.That((NetProtocol.HeaderBits + 16 + 7) / 8 + NetProtocol.MaxUnreliablePayload,
                Is.EqualTo(NetProtocol.MaxDatagramBytes));
        }
    }
}
