using System.Text;
using NUnit.Framework;
using SDG.NetPak;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Golden bytes for the Phase 6 wire messages (MP_PLAN §6: "every command/event/snapshot-block
    // serializer gets a test asserting exact hex output for a fixed input"). A failure here means the
    // transactional wire format drifted -- an intentional change bumps NetProtocol.Version and re-goldens
    // these constants in the same commit.
    [TestFixture]
    public class TransactionalWireGoldenTests
    {
        static string ToHex(byte[] buffer)
        {
            var sb = new StringBuilder(buffer.Length * 2);
            foreach (byte b in buffer) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        static string Pack(byte id, System.Action<NetPakWriter> write) => ToHex(NetMessagePak.Pack(id, write));

        [Test]
        public void UpgradeSkillCommand_GoldenBytes()
        {
            var cmd = new UpgradeSkillCommand { Speciality = 2, Index = 5 };
            Assert.That(Pack(ReplicationIds.CommandUpgradeSkill, cmd.Write), Is.EqualTo("060205"));
        }

        [Test]
        public void PlaceDeployableCommand_GoldenBytes()
        {
            var cmd = new PlaceDeployableCommand { DefId = 458, Pos = new Vector3(-2f, 0f, 12.5f), YawDegrees = 90f };
            Assert.That(Pack(ReplicationIds.CommandPlaceDeployable, cmd.Write), Is.EqualTo("07CA01FE030008C040400001"));
        }

        [Test]
        public void PickupDeployableCommand_GoldenBytes()
        {
            var cmd = new PickupDeployableCommand { NetId = 42 };   // B2: {uint NetId}, same shape as Salvage; id 28 (0x1C) + 42 (LE uint32)
            Assert.That(Pack(ReplicationIds.CommandPickupDeployable, cmd.Write), Is.EqualTo("1C2A000000"));
        }

        [Test]
        public void ExtractFuelCommand_GoldenBytes()
        {
            var cmd = new ExtractFuelCommand { PumpNetId = 42 };   // A2: {uint PumpNetId}, same shape as Salvage/Pickup; id 29 (0x1D) + 42 (LE uint32)
            Assert.That(Pack(ReplicationIds.CommandExtractFuel, cmd.Write), Is.EqualTo("1D2A000000"));
        }

        [Test]
        public void AttachTowCommand_GoldenBytes()
        {
            var cmd = new AttachTowCommand { TowerNetId = 7, TowedNetId = 42 };   // B11: {uint TowerNetId, uint TowedNetId}; id 30 (0x1E) + 7 (LE) + 42 (LE)
            Assert.That(Pack(ReplicationIds.CommandAttachTow, cmd.Write), Is.EqualTo("1E070000002A000000"));
        }

        [Test]
        public void DetachTowCommand_GoldenBytes()
        {
            var cmd = new DetachTowCommand { NetId = 42 };   // B11: {uint NetId} (either end), same shape as Pickup/Extract; id 31 (0x1F) + 42 (LE uint32)
            Assert.That(Pack(ReplicationIds.CommandDetachTow, cmd.Write), Is.EqualTo("1F2A000000"));
        }

        [Test]
        public void ConnectWireCommand_GoldenBytes()
        {
            var cmd = new ConnectWireCommand { SrcId = 7, SrcPort = 0, DstId = 9, DstPort = 1 };
            Assert.That(Pack(ReplicationIds.CommandConnectWire, cmd.Write), Is.EqualTo("0907000000000900000001"));
        }

        [Test]
        public void WireConnectedEvent_GoldenBytes()
        {
            var evt = new WireConnectedEvent { WireId = 11, SrcId = 7, SrcPort = 0, DstId = 9, DstPort = 1 };
            Assert.That(Pack(ReplicationIds.EventWireConnected, evt.Write), Is.EqualTo("0D0B00000007000000000900000001"));
        }

        [Test]
        public void WorldItemSpawnedEvent_GoldenBytes()
        {
            var evt = new WorldItemSpawnedEvent
            {
                NetId = 3, ItemId = 13, Amount = 1, Quality = 100,
                Pos = new Vector3(0.5f, 1f, 1.2f), Vel = new Vector3(0f, 2f, 2.5f),
            };
            Assert.That(Pack(ReplicationIds.EventWorldItemSpawned, evt.Write), Is.EqualTo("10030000000D00016400040C0810C0191010011104"));
        }

        [Test]
        public void SkillsOwnerBlock_GoldenBytes()
        {
            // one player's owner-only block through the REAL snapshot framing: full snapshot, single system
            var skills = new SkillsReplication();
            skills.ServerAdd(9, tick: 1);
            skills.ServerAward(9, 30, tick: 1);
            skills.ServerTryUpgrade(9, 0, 0, tick: 1);   // OVERKILL -> level 1, 20 XP left
            var composer = new SnapshotComposer(new System.Collections.Generic.List<IReplicatedSystem> { skills });

            var bytes = composer.Compose(serverTick: 100, clientPlayerId: 9, Vector3.zero);

            // serverTick:32 + baseline:32(0=full) + systemId:8(5) + byteLen:16 + count:8(1) + owner:16(9)
            // + experience:32(20) + 22 level bytes (OVERKILL=1, rest 0)
            Assert.That(ToHex(bytes), Is.EqualTo(
                "6400000000000000051D000109001400000001000000000000000000000000000000000000000000"));
        }
    }
}
