using SDG.NetPak;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests.Mocks
{
    /// <summary>Test-only append-only command ids (MP_PLAN §2.3: "registered explicitly ... greppable,
    /// AOT-safe, no reflection"). Id 0 is reserved by SnapshotComposer.AckCommandId.</summary>
    static class MockCommandIds
    {
        public const byte Move = 1;   // transactional-class: reliable
        public const byte Input = 2;  // input-like class: unreliable-sequenced
    }

    static class MockEventIds
    {
        public const byte EntityDestroyed = 1;
    }

    /// <summary>A transactional-class mock command (rides ReliableOrdered per MP_PLAN §2.3): move an entity
    /// to an exact position. Exercises the validation choke point via an ownership check in tests.</summary>
    readonly struct MockMoveCommand
    {
        public readonly uint EntityId;
        public readonly float X, Y, Z;

        public MockMoveCommand(uint entityId, float x, float y, float z)
        {
            EntityId = entityId; X = x; Y = y; Z = z;
        }

        public static bool TryRead(NetPakReader r, out MockMoveCommand cmd)
        {
            bool ok = r.ReadUInt32(out uint id);
            ok &= r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float x);
            ok &= r.ReadClampedFloat(NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits, out float y);
            ok &= r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float z);
            cmd = new MockMoveCommand(id, x, y, z);
            return ok;
        }

        public byte[] Pack()
        {
            // structs can't capture `this` in a lambda -- copy fields to locals first.
            uint entityId = EntityId; float x = X, y = Y, z = Z;
            return NetMessagePak.Pack(MockCommandIds.Move, w =>
            {
                w.WriteUInt32(entityId);
                w.WriteClampedFloat(x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
                w.WriteClampedFloat(y, NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits);
                w.WriteClampedFloat(z, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
            });
        }
    }

    /// <summary>An input-like mock command (rides UnreliableSequenced per MP_PLAN §2.3): per-tick move axes.
    /// Mirrors MoveInput's shape without the 3x redundancy scheme (a Phase 4 concern).</summary>
    readonly struct MockInputCommand
    {
        public readonly uint Seq;
        public readonly sbyte AxisX, AxisZ;

        public MockInputCommand(uint seq, sbyte axisX, sbyte axisZ)
        {
            Seq = seq; AxisX = axisX; AxisZ = axisZ;
        }

        public static bool TryRead(NetPakReader r, out MockInputCommand cmd)
        {
            bool ok = r.ReadUInt32(out uint seq);
            ok &= r.ReadInt8(out sbyte ax);
            ok &= r.ReadInt8(out sbyte az);
            cmd = new MockInputCommand(seq, ax, az);
            return ok;
        }

        public byte[] Pack()
        {
            uint seq = Seq; sbyte ax = AxisX, az = AxisZ;
            return NetMessagePak.Pack(MockCommandIds.Input, w =>
            {
                w.WriteUInt32(seq);
                w.WriteInt8(ax);
                w.WriteInt8(az);
            });
        }
    }

    /// <summary>A discrete server->client fact (rides ReliableOrdered per MP_PLAN §2.3's event plane).</summary>
    readonly struct MockEntityDestroyedEvent
    {
        public readonly uint EntityId;
        public readonly byte Reason;

        public MockEntityDestroyedEvent(uint entityId, byte reason)
        {
            EntityId = entityId; Reason = reason;
        }

        public static bool TryRead(NetPakReader r, out MockEntityDestroyedEvent evt)
        {
            bool ok = r.ReadUInt32(out uint id);
            ok &= r.ReadUInt8(out byte reason);
            evt = new MockEntityDestroyedEvent(id, reason);
            return ok;
        }

        public byte[] Pack()
        {
            uint entityId = EntityId; byte reason = Reason;
            return NetMessagePak.Pack(MockEventIds.EntityDestroyed, w =>
            {
                w.WriteUInt32(entityId);
                w.WriteUInt8(reason);
            });
        }
    }
}
