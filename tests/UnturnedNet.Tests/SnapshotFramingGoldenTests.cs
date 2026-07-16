using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;
using UnturnedNet.Tests.Mocks;

namespace UnturnedNet.Tests
{
    // Golden byte tests locking the snapshot wire framing (MP_PLAN §2.4/§6): serverTick:32 + baselineTick:32
    // (0 = full) + repeated system blocks (systemId:8 + byteLen:16 + AlignToByte + payload). If any of these
    // fail, the framing or quantization drifted -- an intentional change must bump NetProtocol.Version and
    // re-golden these constants in the same commit.
    [TestFixture]
    public class SnapshotFramingGoldenTests
    {
        static string ToHex(byte[] buffer)
        {
            var sb = new StringBuilder(buffer.Length * 2);
            foreach (byte b in buffer) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        [Test]
        public void FullSnapshot_OneEntity_OneSystemBlock_GoldenBytes()
        {
            var server = new MockEntitySystem(systemId: 10);
            server.Set(new NetId(1), new Vector3(12.5f, 1.0f, -30.25f), 90f, 77, tick: 1);
            var composer = new SnapshotComposer(new List<IReplicatedSystem> { server });

            var bytes = composer.Compose(serverTick: 1000, clientPlayerId: 5, Vector3.zero);

            // serverTick:32 (1000 = 0x000003E8) + baselineTick:32 (0 = full) + systemId:8 (10) + byteLen:16
            // + one entity block (id:32 + pos quantized 11.8/9.8/11.8 bits + yaw 11 bits + aux:8).
            Assert.That(ToHex(bytes), Is.EqualTo("E8030000000000000A10000100010000000C040C08103E60003501"));
        }

        [Test]
        public void DeltaSnapshot_NoChanges_GoldenBytes()
        {
            var server = new MockEntitySystem(systemId: 10);
            server.Set(new NetId(1), Vector3.zero, 0f, 0, tick: 1);
            var composer = new SnapshotComposer(new List<IReplicatedSystem> { server });
            composer.SetClientBaseline(clientPlayerId: 2, baselineTick: 550);

            // baseline (550) is within the 64-tick dirty ring of serverTick (600) -> delta; nothing changed
            // since tick 550 (the entity's lastChangedTick is 1) -> zero entries, zero removals.
            var bytes = composer.Compose(serverTick: 600, clientPlayerId: 2, Vector3.zero);

            Assert.That(ToHex(bytes), Is.EqualTo("58020000260200000A040000000000"));
        }

        [Test]
        public void MockMoveCommand_GoldenBytes()
        {
            var cmd = new MockMoveCommand(entityId: 7, x: 4f, y: 0.5f, z: -8f);
            Assert.That(ToHex(cmd.Pack()), Is.EqualTo("010700000004040008883F00"));
        }

        [Test]
        public void MockEntityDestroyedEvent_GoldenBytes()
        {
            var evt = new MockEntityDestroyedEvent(entityId: 42, reason: 3);
            Assert.That(ToHex(evt.Pack()), Is.EqualTo("012A00000003"));
        }
    }
}
