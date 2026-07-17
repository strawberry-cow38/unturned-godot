using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // ---------------------------------------------------------------------------------------------------
    // Phase 8 world state (MP_PLAN §3.7): day-night from the server tick, crops, and the resource (tree)
    // alive-bitmap. All three ride the snapshot plane like every other system; crops additionally get
    // Plant/Harvest commands + Planted/Harvested events (registered in ServerTransactions), and resources
    // get Harvested/Respawned events for immediacy. SP is untouched: none of this runs on the direct path.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The world clock as a singleton snapshot block (SystemId 10). Time of day is DERIVED, never streamed:
    /// timeOfDay(tick) = frac(BaseTime01 + tick * 0.02 / DayLengthSeconds) -- the snapshot header's
    /// serverTick (present in every snapshot, §2.5 "synced implicitly by every snapshot header") is the
    /// only thing that advances it. The block itself only changes when the server (re)configures the clock
    /// (boot, admin set-time, drift correction), so at steady state it costs one bit per snapshot.
    /// </summary>
    public sealed class WorldClockReplication : IReplicatedSystem
    {
        public const int BaseTimeBits = 16;   // ~1/65k of a day (~1.3 s at PEI's hour-long day) -- ample

        public bool HasClock { get; private set; }
        public float BaseTime01 { get; private set; }         // quantized time of day at tick 0
        public float DayLengthSeconds { get; private set; }
        public long LastChangedTick { get; private set; }

        public byte SystemId => ReplicationIds.SystemWorldClock;

        // see DeployableReplication.Stamp: mutation stamps are tick+1 (compose-boundary off-by-one)
        static long Stamp(long tick) => tick + 1;

        /// <summary>Configure/correct the clock (server side). Values are quantized through the wire
        /// encoding first so authority and replicas hash identically; dirties only on a REAL change, so a
        /// periodic drift-correcting republish of the same value costs nothing.</summary>
        public void ServerConfigure(float baseTime01, float dayLengthSeconds, long tick)
        {
            float b = QuantizeBase(baseTime01);
            if (HasClock && b == BaseTime01 && dayLengthSeconds == DayLengthSeconds) return;
            HasClock = true;
            BaseTime01 = b;
            DayLengthSeconds = dayLengthSeconds;
            LastChangedTick = Stamp(tick);
        }

        /// <summary>The one derivation both sides use (§2.5: tick x 0.02 s x configured day length).</summary>
        public float TimeOfDayAt(long tick)
        {
            if (!HasClock || DayLengthSeconds <= 0f) return 0f;
            float t = BaseTime01 + (float)(tick * SimClock.FixedDelta / DayLengthSeconds);
            return t - Mathf.Floor(t);
        }

        public static float QuantizeBase(float value01)
        {
            float v = value01 - Mathf.Floor(value01);
            var w = new NetPakWriter { buffer = new byte[4] };
            w.Reset();
            w.WriteUnsignedNormalizedFloat(v, BaseTimeBits);
            w.Flush();
            var r = new NetPakReader();
            r.Reset();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadUnsignedNormalizedFloat(BaseTimeBits, out float result);
            return result;
        }

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            w.WriteBit(HasClock);
            if (HasClock) WritePayload(w);
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            bool changed = HasClock && LastChangedTick > baselineTick;
            w.WriteBit(changed);
            if (changed) WritePayload(w);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadBit(out bool present)) return;
            if (!present)
            {
                if (full) HasClock = false;   // full says "no clock configured"; delta says "unchanged"
                return;
            }
            if (!r.ReadUnsignedNormalizedFloat(BaseTimeBits, out float baseTime)) return;
            if (!r.ReadFloat(out float dayLen)) return;
            HasClock = true;
            BaseTime01 = baseTime;
            DayLengthSeconds = dayLen;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            h = NetHash.MixByte(h, HasClock ? (byte)1 : (byte)0);
            h = NetHash.MixFloat(h, BaseTime01);
            h = NetHash.MixFloat(h, DayLengthSeconds);
            return h;
        }

        void WritePayload(NetPakWriter w)
        {
            w.WriteUnsignedNormalizedFloat(BaseTime01, BaseTimeBits);
            w.WriteFloat(DayLengthSeconds);
        }
    }

    // ---- crops (§3.7: "server owns CropManager's clock and the AGRICULTURE second-yield roll") ----

    /// <summary>The def-derived half of crop growth (ItemFarmAsset via farms.tsv): both sides register the
    /// same defs (content-hash-matched), so only the seed id crosses the wire.</summary>
    public sealed class CropNetDef
    {
        public ushort SeedId;
        public uint GrowthSeconds;    // source ItemFarmAsset.growth
        public ushort YieldItemId;    // source ItemFarmAsset.grow
    }

    /// <summary>Instance-scoped crop def registry (no static state -- test isolation for free).</summary>
    public sealed class CropSchema
    {
        readonly Dictionary<ushort, CropNetDef> _bySeed = new Dictionary<ushort, CropNetDef>();
        public void Register(CropNetDef def) => _bySeed[def.SeedId] = def;
        public bool TryGet(ushort seedId, out CropNetDef def) => _bySeed.TryGetValue(seedId, out def);
    }

    public struct PlantCropCommand
    {
        public ushort SeedId;
        public Vector3 Pos;

        public void Write(NetPakWriter w) { w.WriteUInt16(SeedId); NetWire.WritePos(w, Pos); }

        public static bool TryRead(NetPakReader r, out PlantCropCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seedId)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            cmd = new PlantCropCommand { SeedId = seedId, Pos = pos };
            return true;
        }
    }

    public struct HarvestCropCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out HarvestCropCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new HarvestCropCommand { NetId = id };
            return true;
        }
    }

    public struct CropPlantedEvent
    {
        public uint NetId;
        public ushort SeedId;
        public Vector3 Pos;
        public uint PlantedAtTick;
        public bool Grown;   // console `plant <crop> grown` spawns pre-matured

        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(NetId);
            w.WriteUInt16(SeedId);
            NetWire.WritePos(w, Pos);
            w.WriteUInt32(PlantedAtTick);
            w.WriteBit(Grown);
        }

        public static bool TryRead(NetPakReader r, out CropPlantedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort seedId)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadUInt32(out uint planted)) return false;
            if (!r.ReadBit(out bool grown)) return false;
            evt = new CropPlantedEvent { NetId = id, SeedId = seedId, Pos = pos, PlantedAtTick = planted, Grown = grown };
            return true;
        }
    }

    public struct CropHarvestedEvent
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out CropHarvestedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new CropHarvestedEvent { NetId = id };
            return true;
        }
    }

    /// <summary>
    /// Planted crops as an IReplicatedSystem (SystemId 11). The GROWTH CLOCK is the server tick: a replica
    /// derives the growth stage from (snapshotTick - PlantedAtTick) against the def's growth seconds --
    /// stage never crosses the wire, so the block only changes on plant/harvest (the §3.7 "tiny low-cadence
    /// Snap block", and join-consistency comes free through WriteFull).
    /// </summary>
    public sealed class CropReplication : IReplicatedSystem
    {
        public sealed class CropEntity
        {
            public uint NetIdValue { get; internal set; }
            public ushort SeedId { get; internal set; }
            public Vector3 Pos { get; internal set; }
            public long PlantedAtTick { get; internal set; }
            public bool Grown { get; internal set; }   // forced-mature flag (pre-grown spawn / external truth)
            public long LastChangedTick { get; internal set; }
        }

        public byte SystemId => ReplicationIds.SystemCrops;

        public readonly CropSchema Schema = new CropSchema();

        readonly NetEntityRegistry<CropEntity> _crops = new NetEntityRegistry<CropEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();

        public int Count => _crops.Count;

        public bool TryGet(uint netId, out CropEntity e) => _crops.TryGet(new NetId(netId), out e);

        public IEnumerable<CropEntity> All
        {
            get
            {
                foreach (uint id in SortedIds())
                {
                    _crops.TryGet(new NetId(id), out var e);
                    yield return e;
                }
            }
        }

        /// <summary>Growth check both sides agree on: forced-grown, or the tick clock passed the def's
        /// growth time (50 ticks/second -- SimClock.FixedDelta).</summary>
        public bool IsGrown(CropEntity e, long tick)
        {
            if (e.Grown) return true;
            if (!Schema.TryGet(e.SeedId, out var def)) return false;
            return tick - e.PlantedAtTick >= def.GrowthSeconds * 50L;
        }

        // ---- server side ----

        static long Stamp(long tick) => tick + 1;

        public CropEntity ServerPlant(NetId id, ushort seedId, Vector3 pos, long tick, bool grown = false)
        {
            if (!Schema.TryGet(seedId, out _)) return null;   // unknown seed: nothing to grow into
            var e = new CropEntity
            {
                NetIdValue = id.Value,
                SeedId = seedId,
                Pos = PlayerReplication.Quantize(pos),
                PlantedAtTick = tick,
                Grown = grown,
                LastChangedTick = Stamp(tick),
            };
            _crops.Add(id, e);
            _removedAtTick.Remove(id.Value);
            return e;
        }

        /// <summary>External growth truth (the loopback world's CropManager runs UG_FARMSPEED-scaled time):
        /// force the mature flag so replicas agree with the node the player actually sees.</summary>
        public void ServerForceGrown(uint netId, long tick)
        {
            if (!TryGet(netId, out var e) || e.Grown) return;
            e.Grown = true;
            e.LastChangedTick = Stamp(tick);
        }

        public bool ServerRemove(uint netId, long tick)
        {
            if (!_crops.Remove(new NetId(netId))) return false;
            _removedAtTick[netId] = Stamp(tick);
            return true;
        }

        // ---- client-side event application (idempotent -- a delta may have raced the event in) ----

        public void ApplyPlanted(in CropPlantedEvent evt, long tick)
        {
            if (TryGet(evt.NetId, out _)) return;
            _crops.Add(new NetId(evt.NetId), new CropEntity
            {
                NetIdValue = evt.NetId, SeedId = evt.SeedId, Pos = PlayerReplication.Quantize(evt.Pos),
                PlantedAtTick = evt.PlantedAtTick, Grown = evt.Grown, LastChangedTick = tick,
            });
        }

        public void ApplyHarvested(in CropHarvestedEvent evt, long tick) => ServerRemove(evt.NetId, tick);

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var ids = SortedIds();
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids) { _crops.TryGet(new NetId(id), out var e); WriteEntity(w, e); }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            foreach (uint id in SortedIds())
            {
                _crops.TryGet(new NetId(id), out var e);
                if (e.LastChangedTick > baselineTick) changed.Add(id);
            }
            var removed = new List<uint>();
            foreach (var kv in _removedAtTick)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed) { _crops.TryGet(new NetId(id), out var e); WriteEntity(w, e); }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);

            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort count)) return;
            if (full) _crops.Clear();
            for (int i = 0; i < count; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _crops.Add(new NetId(e.NetIdValue), e);
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++)
                {
                    if (!r.ReadUInt32(out uint id)) return;
                    _crops.Remove(new NetId(id));
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (var e in All)
            {
                h = NetHash.MixUInt32(h, e.NetIdValue);
                h = NetHash.MixUInt32(h, e.SeedId);
                h = NetHash.MixFloat(h, e.Pos.x); h = NetHash.MixFloat(h, e.Pos.y); h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixUInt32(h, (uint)e.PlantedAtTick);
                h = NetHash.MixByte(h, e.Grown ? (byte)1 : (byte)0);
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, CropEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            w.WriteUInt16(e.SeedId);
            NetWire.WritePos(w, e.Pos);
            w.WriteUInt32((uint)e.PlantedAtTick);
            w.WriteBit(e.Grown);
        }

        static bool ReadEntity(NetPakReader r, out CropEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort seedId)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadUInt32(out uint planted)) return false;
            if (!r.ReadBit(out bool grown)) return false;
            e = new CropEntity { NetIdValue = id, SeedId = seedId, Pos = pos, PlantedAtTick = planted, Grown = grown };
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
            foreach (var id in _crops.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }

    // ---- resources / trees (§3.7: "deterministic index from Trees.dat order = implicit id") ----

    public struct ResourceHarvestedEvent
    {
        public ushort Index;
        public void Write(NetPakWriter w) => w.WriteUInt16(Index);
        public static bool TryRead(NetPakReader r, out ResourceHarvestedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt16(out ushort index)) return false;
            evt = new ResourceHarvestedEvent { Index = index };
            return true;
        }
    }

    public struct ResourceRespawnedEvent
    {
        public ushort Index;
        public void Write(NetPakWriter w) => w.WriteUInt16(Index);
        public static bool TryRead(NetPakReader r, out ResourceRespawnedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt16(out ushort index)) return false;
            evt = new ResourceRespawnedEvent { Index = index };
            return true;
        }
    }

    /// <summary>
    /// Harvestable map resources (trees/bushes/rocks) as an IReplicatedSystem (SystemId 12). Resources are
    /// authored map data loaded identically on every peer (content-hash-matched), so the deterministic LOAD
    /// ORDER index is the implicit id -- no NetIds, no per-entity transforms on the wire, ever. State is one
    /// alive-bit per resource: WriteFull carries the whole bitmap (the §3.7 join path), WriteDelta carries
    /// (index, alive) changes, and Harvested/Respawned events give clients immediacy for fx.
    /// </summary>
    public sealed class ResourceReplication : IReplicatedSystem
    {
        bool[] _alive = System.Array.Empty<bool>();
        long[] _changedTick = System.Array.Empty<long>();

        public byte SystemId => ReplicationIds.SystemResources;

        public int Count => _alive.Length;

        /// <summary>Bumped on every applied change -- node views poll this instead of diffing the bitmap.</summary>
        public long Version { get; private set; }

        public bool IsAlive(int index) => index >= 0 && index < _alive.Length && _alive[index];

        public int AliveCount
        {
            get { int n = 0; for (int i = 0; i < _alive.Length; i++) if (_alive[i]) n++; return n; }
        }

        // ---- server side ----

        static long Stamp(long tick) => tick + 1;

        /// <summary>Size the bitmap to the world's resource count, all alive (server boot).</summary>
        public void ServerInit(int count, long tick)
        {
            _alive = new bool[count];
            _changedTick = new long[count];
            for (int i = 0; i < count; i++) { _alive[i] = true; _changedTick[i] = Stamp(tick); }
            Version++;
        }

        public bool ServerSetAlive(int index, bool alive, long tick)
        {
            if (index < 0 || index >= _alive.Length || _alive[index] == alive) return false;
            _alive[index] = alive;
            _changedTick[index] = Stamp(tick);
            Version++;
            return true;
        }

        // ---- client-side event application (idempotent) ----

        public void ApplyHarvested(in ResourceHarvestedEvent evt, long tick) => ServerSetAlive(evt.Index, false, tick);
        public void ApplyRespawned(in ResourceRespawnedEvent evt, long tick) => ServerSetAlive(evt.Index, true, tick);

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            w.WriteUInt16((ushort)_alive.Length);
            for (int i = 0; i < _alive.Length; i++) w.WriteBit(_alive[i]);
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<int>();
            for (int i = 0; i < _alive.Length; i++)
                if (_changedTick[i] > baselineTick) changed.Add(i);
            w.WriteUInt16((ushort)changed.Count);
            foreach (int i in changed)
            {
                w.WriteUInt16((ushort)i);
                w.WriteBit(_alive[i]);
            }
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (full)
            {
                if (!r.ReadUInt16(out ushort count)) return;
                if (_alive.Length != count)
                {
                    _alive = new bool[count];
                    _changedTick = new long[count];
                }
                for (int i = 0; i < count; i++)
                {
                    if (!r.ReadBit(out bool alive)) return;
                    _alive[i] = alive;
                }
                Version++;
                return;
            }
            if (!r.ReadUInt16(out ushort changedCount)) return;
            for (int i = 0; i < changedCount; i++)
            {
                if (!r.ReadUInt16(out ushort index)) return;
                if (!r.ReadBit(out bool alive)) return;
                if (index < _alive.Length) _alive[index] = alive;
            }
            if (changedCount > 0) Version++;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            h = NetHash.MixUInt32(h, (uint)_alive.Length);
            for (int i = 0; i < _alive.Length; i++)
                if (!_alive[i]) h = NetHash.MixUInt32(h, (uint)i);   // dead set defines the state; all-alive folds fast
            return h;
        }
    }
}
