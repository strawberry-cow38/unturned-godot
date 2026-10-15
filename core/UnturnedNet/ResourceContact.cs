using System;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public enum ResourceContactKind : byte { Hit, Felled }
    public enum ResourceContactMaterial : byte { Tree, Ore }

    /// <summary>An accepted cosmetic fact. The existing resource bitmap remains
    /// the lasting authority; receiving this event never harvests or rewards.</summary>
    public struct ResourceContactEvent
    {
        public ResourceContactKind Kind;
        public ResourceContactMaterial Material;
        public ushort Index;
        public uint Tick;
        // Hit: actual contact. Felled: original resource root.
        public Vector3 Position, Direction;
        public float Health, Damage, TrunkLength, DebrisSeconds;

        public bool IsValid => Kind <= ResourceContactKind.Felled && Material <= ResourceContactMaterial.Ore
            && Finite(Position) && (Kind == ResourceContactKind.Hit
                ? float.IsFinite(Health) && Health >= 0 && float.IsFinite(Damage) && Damage >= 0
                : Finite(Direction) && Math.Abs(Direction.y) <= 0.001f
                  && Math.Abs(Direction.sqrMagnitude - 1f) <= 0.01f
                  && float.IsFinite(TrunkLength) && TrunkLength > 0
                  && float.IsFinite(DebrisSeconds)
                  && (Material == ResourceContactMaterial.Tree ? DebrisSeconds > 0 : DebrisSeconds == 0));
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        public void Write(NetPakWriter writer)
        {
            if (!IsValid) throw new ArgumentException("Invalid accepted resource contact");
            writer.WriteUInt8((byte)Kind); writer.WriteUInt8((byte)Material);
            writer.WriteUInt16(Index); writer.WriteUInt32(Tick);
            NetWire.WritePos(writer, Position);
            if (Kind == ResourceContactKind.Hit)
            { writer.WriteFloat(Health); writer.WriteFloat(Damage); }
            else
            { NetWire.WriteDir(writer, Direction); writer.WriteFloat(TrunkLength); writer.WriteFloat(DebrisSeconds); }
        }

        public static bool TryRead(NetPakReader reader, out ResourceContactEvent result)
        {
            result = default;
            if (!reader.ReadUInt8(out byte kind) || kind > (byte)ResourceContactKind.Felled
                || !reader.ReadUInt8(out byte material) || material > (byte)ResourceContactMaterial.Ore
                || !reader.ReadUInt16(out ushort index) || !reader.ReadUInt32(out uint tick)
                || !NetWire.ReadPos(reader, out var position)) return false;
            var candidate = new ResourceContactEvent {
                Kind = (ResourceContactKind)kind, Material = (ResourceContactMaterial)material,
                Index = index, Tick = tick, Position = position,
            };
            if (candidate.Kind == ResourceContactKind.Hit)
            {
                if (!reader.ReadFloat(out candidate.Health) || !reader.ReadFloat(out candidate.Damage)) return false;
            }
            else if (!NetWire.ReadDir(reader, out candidate.Direction) || !reader.ReadFloat(out candidate.TrunkLength)
                || !reader.ReadFloat(out candidate.DebrisSeconds)) return false;
            if (!candidate.IsValid) return false;
            result = candidate;
            return true;
        }
    }

    public sealed partial class NetWorldClient
    {
        public event Action<ResourceContactEvent> ResourceContact;
        void RegisterResourceContactEvents() => Events.Register<ResourceContactEvent>(
            ReplicationIds.EventResourceContact, ResourceContactEvent.TryRead, value => ResourceContact?.Invoke(value));
    }
}
