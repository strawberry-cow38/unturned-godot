using System.Collections.Generic;
using SDG.NetPak;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests.Mocks
{
    /// <summary>
    /// Test-only stand-in for a real per-system snapshot writer (MP_PLAN §4 Phase 2: "prove the framing
    /// MACHINERY with test-only mock IReplicatedSystem implementations" -- there are no real game systems to
    /// replicate yet). Tracks a set of entities (position/yaw/an aux byte, e.g. health) each with its own
    /// lastChangedTick, so WriteDelta only emits what changed since the client's baseline -- exactly the
    /// shape a real system (players, zombies, deployables) will have once Phase 3+ wires one in.
    ///
    /// Values are quantized on write (NetQuantization.QuantizeClampedFloat/QuantizeDegrees) so the
    /// authoritative copy is already bit-identical to what every client reconstructs after the wire
    /// round-trip -- StateHash comparisons need no tolerance, just exact equality.
    /// </summary>
    sealed class MockEntitySystem : IReplicatedSystem
    {
        public sealed class Entity
        {
            public Vector3 Pos;
            public float Yaw;
            public byte Aux;
            public long LastChangedTick;
        }

        public byte SystemId { get; }

        readonly NetEntityRegistry<Entity> _entities = new NetEntityRegistry<Entity>();
        // Tombstones for delta removal blocks -- test-only, so no pruning: production per-system dirty
        // rings would drop tombstones older than NetQuantization.DirtyRingDepthTicks (any client that stale
        // gets a full resend anyway, which never consults this list).
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();

        public MockEntitySystem(byte systemId) { SystemId = systemId; }

        public int EntityCount => _entities.Count;

        public bool TryGet(NetId id, out Entity entity) => _entities.TryGet(id, out entity);

        public void Set(NetId id, Vector3 pos, float yaw, byte aux, long tick)
        {
            var quantized = new Entity
            {
                Pos = new Vector3(
                    NetQuantization.QuantizeClampedFloat(pos.x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits),
                    NetQuantization.QuantizeClampedFloat(pos.y, NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits),
                    NetQuantization.QuantizeClampedFloat(pos.z, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits)),
                Yaw = NetQuantization.QuantizeDegrees(yaw, NetQuantization.YawBits),
                Aux = aux,
                LastChangedTick = tick,
            };
            _entities.Add(id, quantized);
            _removedAtTick.Remove(id.Value);
        }

        public void Remove(NetId id, long tick)
        {
            if (_entities.Remove(id)) _removedAtTick[id.Value] = tick;
        }

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var ids = SortedIds();
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids)
            {
                _entities.TryGet(new NetId(id), out var e);
                WriteEntity(w, id, e);
            }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            foreach (uint id in SortedIds())
            {
                _entities.TryGet(new NetId(id), out var e);
                if (e.LastChangedTick > baselineTick) changed.Add(id);
            }
            var removed = new List<uint>();
            foreach (var kv in _removedAtTick)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed)
            {
                _entities.TryGet(new NetId(id), out var e);
                WriteEntity(w, id, e);
            }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            r.ReadUInt16(out ushort changedCount);
            if (full) _entities.Clear();
            for (int i = 0; i < changedCount; i++)
            {
                var e = ReadEntity(r, out uint id);
                _entities.Add(new NetId(id), e);
            }
            if (!full)
            {
                r.ReadUInt16(out ushort removedCount);
                for (int i = 0; i < removedCount; i++)
                {
                    r.ReadUInt32(out uint id);
                    _entities.Remove(new NetId(id));
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (uint id in SortedIds())
            {
                _entities.TryGet(new NetId(id), out var e);
                h = NetHash.MixUInt32(h, id);
                h = NetHash.MixFloat(h, e.Pos.x);
                h = NetHash.MixFloat(h, e.Pos.y);
                h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixFloat(h, e.Yaw);
                h = NetHash.MixByte(h, e.Aux);
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, uint id, Entity e)
        {
            w.WriteUInt32(id);
            w.WriteClampedFloat(e.Pos.x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
            w.WriteClampedFloat(e.Pos.y, NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits);
            w.WriteClampedFloat(e.Pos.z, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
            w.WriteDegrees(e.Yaw, NetQuantization.YawBits);
            w.WriteUInt8(e.Aux);
        }

        static Entity ReadEntity(NetPakReader r, out uint id)
        {
            r.ReadUInt32(out id);
            r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float x);
            r.ReadClampedFloat(NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits, out float y);
            r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float z);
            r.ReadDegrees(out float yaw, NetQuantization.YawBits);
            r.ReadUInt8(out byte aux);
            return new Entity { Pos = new Vector3(x, y, z), Yaw = yaw, Aux = aux };
        }

        List<uint> SortedIds()
        {
            var ids = new List<uint>();
            foreach (var id in _entities.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }
}
