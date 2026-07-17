using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // ---------------------------------------------------------------------------------------------------
    // World items (MP_PLAN §3.3): dropped/loot items as server-owned NetId entities. Spawn is a reliable
    // Event carrying the initial throw velocity (clients run the cosmetic tumble locally -- the wire never
    // streams a falling item's transform); a settled-transform Event freezes it where the server's physics
    // did; a removal Event ends it on pickup. The Snap block exists for join-consistency (WriteFull) and
    // scalar corrections -- the events are the live path.
    // ---------------------------------------------------------------------------------------------------

    public struct WorldItemSpawnedEvent
    {
        public uint NetId;
        public ushort ItemId;
        public byte Amount;
        public byte Quality;
        public Vector3 Pos;
        public Vector3 Vel;   // initial throw velocity -- cosmetic tumble seed, ±64 m/s clamp

        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(NetId);
            w.WriteUInt16(ItemId);
            w.WriteUInt8(Amount);
            w.WriteUInt8(Quality);
            NetWire.WritePos(w, Pos);
            NetWire.WriteVel(w, Vel);
        }

        public static bool TryRead(NetPakReader r, out WorldItemSpawnedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort itemId)) return false;
            if (!r.ReadUInt8(out byte amount)) return false;
            if (!r.ReadUInt8(out byte quality)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!NetWire.ReadVel(r, out Vector3 vel)) return false;
            evt = new WorldItemSpawnedEvent { NetId = id, ItemId = itemId, Amount = amount, Quality = quality, Pos = pos, Vel = vel };
            return true;
        }
    }

    public struct WorldItemSettledEvent
    {
        public uint NetId;
        public Vector3 Pos;

        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); NetWire.WritePos(w, Pos); }

        public static bool TryRead(NetPakReader r, out WorldItemSettledEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            evt = new WorldItemSettledEvent { NetId = id, Pos = pos };
            return true;
        }
    }

    public struct WorldItemRemovedEvent
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out WorldItemRemovedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new WorldItemRemovedEvent { NetId = id };
            return true;
        }
    }

    /// <summary>To the requester only: your pickup was legal but the grid had no room -- the item stays in
    /// the world (§2.3's ItemPickupDenied example, made real).</summary>
    public struct ItemPickupDeniedEvent
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out ItemPickupDeniedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new ItemPickupDeniedEvent { NetId = id };
            return true;
        }
    }

    /// <summary>World items as an IReplicatedSystem (SystemId 8). Server side keeps the REAL Item object
    /// (gun ammo/firemode/attachments survive drop->pickup server-side without those fields ever crossing
    /// the wire); replicas carry only what rendering needs (id/amount/quality/pos/settled).</summary>
    public sealed class WorldItemReplication : IReplicatedSystem
    {
        public sealed class WorldItemEntity
        {
            public uint NetIdValue;
            public ushort ItemId;
            public byte Amount;
            public byte Quality;
            public Vector3 Pos;
            public bool Settled;
            public long LastChangedTick;

            /// <summary>Server-only: the real dropped Item (never on the wire; null on replicas).</summary>
            public Item ServerItem;
        }

        public byte SystemId => ReplicationIds.SystemWorldItems;

        readonly NetEntityRegistry<WorldItemEntity> _items = new NetEntityRegistry<WorldItemEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();

        public int Count => _items.Count;

        public bool TryGet(uint netId, out WorldItemEntity e) => _items.TryGet(new NetId(netId), out e);

        public IEnumerable<WorldItemEntity> All
        {
            get
            {
                foreach (uint id in SortedIds())
                {
                    _items.TryGet(new NetId(id), out var e);
                    yield return e;
                }
            }
        }

        // ---- server side ----

        // see DeployableReplication.Stamp: mutation stamps are tick+1 (compose-boundary off-by-one)
        static long Stamp(long tick) => tick + 1;

        public WorldItemEntity ServerSpawn(NetId id, Item item, Vector3 pos, long tick)
        {
            var e = new WorldItemEntity
            {
                NetIdValue = id.Value,
                ItemId = item.id,
                Amount = item.amount,
                Quality = item.quality,
                Pos = PlayerReplication.Quantize(pos),
                LastChangedTick = Stamp(tick),
                ServerItem = item,
            };
            _items.Add(id, e);
            _removedAtTick.Remove(id.Value);
            return e;
        }

        public void ServerSettle(uint netId, Vector3 pos, long tick)
        {
            if (!TryGet(netId, out var e) || e.Settled) return;
            e.Pos = PlayerReplication.Quantize(pos);
            e.Settled = true;
            e.LastChangedTick = Stamp(tick);
        }

        public bool ServerRemove(uint netId, long tick)
        {
            if (!_items.Remove(new NetId(netId))) return false;
            _removedAtTick[netId] = Stamp(tick);
            return true;
        }

        // ---- client-side event application (idempotent -- a delta may have raced the event in) ----

        public void ApplySpawned(in WorldItemSpawnedEvent evt, long tick)
        {
            if (TryGet(evt.NetId, out _)) return;
            var e = new WorldItemEntity
            {
                NetIdValue = evt.NetId, ItemId = evt.ItemId, Amount = evt.Amount, Quality = evt.Quality,
                Pos = PlayerReplication.Quantize(evt.Pos), LastChangedTick = tick,
            };
            _items.Add(new NetId(evt.NetId), e);
        }

        public void ApplySettled(in WorldItemSettledEvent evt, long tick) => ServerSettle(evt.NetId, evt.Pos, tick);

        public void ApplyRemoved(in WorldItemRemovedEvent evt, long tick) => ServerRemove(evt.NetId, tick);

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var ids = SortedIds();
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids) { _items.TryGet(new NetId(id), out var e); WriteEntity(w, e); }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            foreach (uint id in SortedIds())
            {
                _items.TryGet(new NetId(id), out var e);
                if (e.LastChangedTick > baselineTick) changed.Add(id);
            }
            var removed = new List<uint>();
            foreach (var kv in _removedAtTick)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed) { _items.TryGet(new NetId(id), out var e); WriteEntity(w, e); }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);

            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort count)) return;
            if (full) _items.Clear();
            for (int i = 0; i < count; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _items.Add(new NetId(e.NetIdValue), e);
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++)
                {
                    if (!r.ReadUInt32(out uint id)) return;
                    _items.Remove(new NetId(id));
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (var e in All)
            {
                h = NetHash.MixUInt32(h, e.NetIdValue);
                h = NetHash.MixUInt32(h, e.ItemId);
                h = NetHash.MixByte(h, e.Amount);
                h = NetHash.MixByte(h, e.Quality);
                h = NetHash.MixFloat(h, e.Pos.x); h = NetHash.MixFloat(h, e.Pos.y); h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixByte(h, e.Settled ? (byte)1 : (byte)0);
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, WorldItemEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            w.WriteUInt16(e.ItemId);
            w.WriteUInt8(e.Amount);
            w.WriteUInt8(e.Quality);
            NetWire.WritePos(w, e.Pos);
            w.WriteBit(e.Settled);
        }

        static bool ReadEntity(NetPakReader r, out WorldItemEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort itemId)) return false;
            if (!r.ReadUInt8(out byte amount)) return false;
            if (!r.ReadUInt8(out byte quality)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadBit(out bool settled)) return false;
            e = new WorldItemEntity { NetIdValue = id, ItemId = itemId, Amount = amount, Quality = quality, Pos = pos, Settled = settled };
            return true;
        }

        void PruneTombstones(long serverTick)
        {
            List<uint> stale = null;
            foreach (var kv in _removedAtTick)
                if (serverTick - kv.Value > NetQuantization.DirtyRingDepthTicks)
                    (stale ??= new List<uint>()).Add(kv.Key);
            if (stale != null) foreach (uint id in stale) _removedAtTick.Remove(id);
        }

        List<uint> SortedIds()
        {
            var ids = new List<uint>();
            foreach (var id in _items.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }
}
