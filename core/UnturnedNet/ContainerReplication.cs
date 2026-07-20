using System.Collections.Generic;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // ---------------------------------------------------------------------------------------------------
    // World containers -- store shelves / loot crates (MP_PLAN A1) as server-owned NetId FIXTURE entities
    // (SystemId 14). The fixture (kind/pos/yaw/dims) is STATIC: placed at world-build, parsed+quantized
    // identically on both sides -- it only crosses the wire so a joiner (or a client walking into range)
    // materializes it. The mutable part is the DISPLAY digest (what item sits in each visible tier cell), a
    // server-derived read-only projection of the crate's inventory grid that rides WriteDelta on change.
    // No new command (open/close reuse 18/19) and no new event -- fixtures ride the join FULL snapshot.
    // Relevancy-filtered (a ~426-fixture PEI world would blow the join snapshot un-ringed); NOT in the desync
    // sync-check (a relevancy-filtered system hashes the whole set server-side vs a nearby client subset ->
    // guaranteed mismatch, exactly like WorldItems + Zombies). Cross of DeployableReplication (server-owned
    // fixture) + WorldItemReplication (relevancy ring).
    // ---------------------------------------------------------------------------------------------------

    /// <summary>One visible display cell projected from a container's grid: the tier/slot index, the item
    /// shown there, and its rotation. Server-derived; the client just draws it on the shelf tiers.</summary>
    public struct ContainerDisplayCell
    {
        public byte Cell;
        public ushort ItemId;
        public byte Rot;
    }

    /// <summary>Containers as an IReplicatedSystem (SystemId 14). The server registers the world-build
    /// fixtures + projects their display; the client materializes them via StorageReplicaView. The server
    /// keeps the real crate grid (InventoryReplication owns the contents) -- this system carries only the
    /// fixture + the display digest, never the live grid.</summary>
    public sealed class ContainerReplication : IReplicatedSystem
    {
        public sealed class ContainerEntity
        {
            public uint NetIdValue;
            public ushort KindId;     // which container (StoreShelf gondola / LootCrate + table) -- ContainerSchema key
            public Vector3 Pos;
            public float YawDegrees;
            public byte Width;        // crate grid dims -> the client node's storage page
            public byte Height;
            public ContainerDisplayCell[] Display = System.Array.Empty<ContainerDisplayCell>();
            public long LastChangedTick;
        }

        public byte SystemId => ReplicationIds.SystemContainers;

        /// <summary>Relevancy ring (A1): a joiner gets only nearby fixtures; more come as ViewPos moves in.
        /// null = AllRelevant (byte-identical to the pre-ring wire, for L0 tests).</summary>
        public InterestPolicy Interest;

        readonly NetEntityRegistry<ContainerEntity> _containers = new NetEntityRegistry<ContainerEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();
        readonly RelevancyTracker _relevancy = new RelevancyTracker();

        public void ForgetClient(ushort clientPlayerId) => _relevancy.ForgetClient(clientPlayerId);
        public int Count => _containers.Count;
        public bool TryGet(uint netId, out ContainerEntity e) => _containers.TryGet(new NetId(netId), out e);

        public IEnumerable<ContainerEntity> All
        {
            get { foreach (uint id in SortedIds()) { _containers.TryGet(new NetId(id), out var e); yield return e; } }
        }

        // ---- server side ----
        static long Stamp(long tick) => tick + 1;   // compose-boundary off-by-one, see DeployableReplication.Stamp

        /// <summary>Register a world-build fixture (ContainerNetSync mints one NetId per parsed container).</summary>
        public ContainerEntity ServerRegisterFixture(NetId id, ushort kindId, Vector3 pos, float yawDegrees, byte width, byte height, long tick)
        {
            var e = new ContainerEntity
            {
                NetIdValue = id.Value, KindId = kindId, Pos = PlayerReplication.Quantize(pos),
                // quantize yaw at store time (like Pos) so the server's StateHash matches the wire-quantized value
                // the client reads back -- else relevancy/desync parity fails on the sub-degree rounding (WriteDegrees).
                YawDegrees = NetQuantization.QuantizeDegrees(yawDegrees, NetQuantization.YawBits), Width = width, Height = height, LastChangedTick = Stamp(tick),
            };
            _containers.Add(id, e);
            _removedAtTick.Remove(id.Value);
            return e;
        }

        /// <summary>Update the display digest (ContainerNetSync projects it from the crate grid on change).</summary>
        public void ServerSetDisplay(uint netId, ContainerDisplayCell[] display, long tick)
        {
            if (!TryGet(netId, out var e)) return;
            e.Display = display ?? System.Array.Empty<ContainerDisplayCell>();
            e.LastChangedTick = Stamp(tick);
        }

        public bool ServerRemove(uint netId, long tick)
        {
            if (!_containers.Remove(new NetId(netId))) return false;
            _removedAtTick[netId] = Stamp(tick);
            _relevancy.ForgetEntity(netId);
            return true;
        }

        // ---- IReplicatedSystem ----
        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var ids = SortedIds();
            if (Interest != null)
            {
                var included = new List<uint>();
                foreach (uint id in ids) { _containers.TryGet(new NetId(id), out var e); if (Interest.IsRelevant(ctx.ViewPos, e.Pos)) included.Add(id); }
                _relevancy.ResetFull(ctx.ClientPlayerId, included, ctx.ServerTick);
                ids = included;
            }
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids) { _containers.TryGet(new NetId(id), out var e); WriteEntity(w, e); }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            var removed = new List<uint>();
            foreach (uint id in SortedIds())
            {
                _containers.TryGet(new NetId(id), out var e);
                if (Interest == null)
                {
                    if (e.LastChangedTick > baselineTick) changed.Add(id);
                }
                else if (_relevancy.ShouldWrite(ctx.ClientPlayerId, id, Interest.IsRelevant(ctx.ViewPos, e.Pos), e.LastChangedTick, baselineTick, ctx.ServerTick))
                {
                    changed.Add(id);
                }
            }
            if (Interest != null) _relevancy.CollectRemovals(ctx.ClientPlayerId, baselineTick, removed);
            foreach (var kv in _removedAtTick) if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed) { _containers.TryGet(new NetId(id), out var e); WriteEntity(w, e); }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);

            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort count)) return;
            if (full) _containers.Clear();
            for (int i = 0; i < count; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _containers.Add(new NetId(e.NetIdValue), e);
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++) { if (!r.ReadUInt32(out uint id)) return; _containers.Remove(new NetId(id)); }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (var e in All)
            {
                h = NetHash.MixUInt32(h, e.NetIdValue);
                h = NetHash.MixUInt32(h, e.KindId);
                h = NetHash.MixFloat(h, e.Pos.x); h = NetHash.MixFloat(h, e.Pos.y); h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixFloat(h, e.YawDegrees);
                h = NetHash.MixByte(h, e.Width); h = NetHash.MixByte(h, e.Height);
                h = NetHash.MixUInt32(h, (uint)e.Display.Length);
                foreach (var d in e.Display) { h = NetHash.MixByte(h, d.Cell); h = NetHash.MixUInt32(h, d.ItemId); h = NetHash.MixByte(h, d.Rot); }
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, ContainerEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            w.WriteUInt16(e.KindId);
            NetWire.WritePos(w, e.Pos);
            w.WriteDegrees(e.YawDegrees, NetQuantization.YawBits);
            w.WriteUInt8(e.Width);
            w.WriteUInt8(e.Height);
            w.WriteUInt8((byte)e.Display.Length);   // a shelf's visible tiers are few (<= 255 cells)
            foreach (var d in e.Display) { w.WriteUInt8(d.Cell); w.WriteUInt16(d.ItemId); w.WriteUInt8(d.Rot); }
        }

        static bool ReadEntity(NetPakReader r, out ContainerEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort kindId)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadUInt8(out byte width)) return false;
            if (!r.ReadUInt8(out byte height)) return false;
            if (!r.ReadUInt8(out byte dcount)) return false;
            var display = new ContainerDisplayCell[dcount];
            for (int i = 0; i < dcount; i++)
            {
                if (!r.ReadUInt8(out byte cell)) return false;
                if (!r.ReadUInt16(out ushort itemId)) return false;
                if (!r.ReadUInt8(out byte rot)) return false;
                display[i] = new ContainerDisplayCell { Cell = cell, ItemId = itemId, Rot = rot };
            }
            e = new ContainerEntity { NetIdValue = id, KindId = kindId, Pos = pos, YawDegrees = yaw, Width = width, Height = height, Display = display };
            return true;
        }

        void PruneTombstones(long serverTick)
        {
            List<uint> stale = null;
            foreach (var kv in _removedAtTick) if (serverTick - kv.Value > NetQuantization.DirtyRingDepthTicks) (stale ??= new List<uint>()).Add(kv.Key);
            if (stale != null) foreach (uint id in stale) _removedAtTick.Remove(id);
        }

        List<uint> SortedIds()
        {
            var ids = new List<uint>();
            foreach (var id in _containers.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }
}
