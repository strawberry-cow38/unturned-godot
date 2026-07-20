using System.Collections.Generic;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>What an animal replica needs to render: one anim byte (grazing wildlife: idle/graze variants +
    /// walk, plus dead). The species byte selects the rig/texture client-side (AnimalCatalog).</summary>
    public enum AnimalNetAnim : byte { Idle = 0, Walk = 1, Eat = 2, Glance = 3, Dead = 4 }

    /// <summary>
    /// Wildlife as an IReplicatedSystem (SP/MP-unify wave 2 A5, SystemId 15). SERVER side: the real AnimalAgent
    /// brains own the wander behaviour; a game-side sync (AnimalNetSync) publishes their transform/anim/species
    /// into these entities every 4th tick (12.5 Hz -- the §2.5 cadence, shared with zombies). CLIENT side:
    /// replicas drive interpolated AnimalPuppets -- no wander, no terrain-follow ever runs from this data.
    /// Mirrors ZombieReplication exactly (transform + anim byte + a kind byte); relevancy-ringed, NOT in
    /// EnableSyncCheck (clients receive a nearby subset, never derive the whole set).
    /// </summary>
    public sealed class AnimalReplication : IReplicatedSystem
    {
        public sealed class AnimalEntity
        {
            public uint NetIdValue { get; internal set; }
            public Vector3 Pos { get; internal set; }
            public float YawDegrees { get; internal set; }
            public byte AnimState { get; internal set; }
            public byte Species { get; internal set; }   // AnimalCatalog index (0 deer / 1 pig / 2 cow ...)
            public long LastChangedTick { get; internal set; }

            public bool IsDead => AnimState == (byte)AnimalNetAnim.Dead;
        }

        public byte SystemId => ReplicationIds.SystemAnimals;

        /// <summary>Interest policy (§2.6): null = AllRelevant (byte-identical to the pre-ring wire, for L0
        /// tests). The game host sets a ring on dedicated/loopback servers; client replicas never set it.</summary>
        public InterestPolicy Interest;

        readonly NetEntityRegistry<AnimalEntity> _animals = new NetEntityRegistry<AnimalEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();
        readonly RelevancyTracker _relevancy = new RelevancyTracker();

        /// <summary>Disconnect/rejoin hygiene: per-client relevancy state must not leak to a recycled id.</summary>
        public void ForgetClient(ushort clientPlayerId) => _relevancy.ForgetClient(clientPlayerId);

        public int Count => _animals.Count;

        public bool TryGet(NetId id, out AnimalEntity entity) => _animals.TryGet(id, out entity);

        public IEnumerable<AnimalEntity> All
        {
            get
            {
                foreach (uint id in SortedIds())
                {
                    _animals.TryGet(new NetId(id), out var e);
                    yield return e;
                }
            }
        }

        // ---- server side ----

        public AnimalEntity ServerSpawn(NetId id, byte species, Vector3 pos, long tick)
        {
            var e = new AnimalEntity
            {
                NetIdValue = id.Value,
                Species = species,
                Pos = PlayerReplication.Quantize(pos),
                YawDegrees = 0f,
                AnimState = (byte)AnimalNetAnim.Idle,
                LastChangedTick = tick,
            };
            _animals.Add(id, e);
            _removedAtTick.Remove(id.Value);
            return e;
        }

        /// <summary>Publish the brain's current state (quantized; dirty only on real change, so a grazing
        /// animal costs no delta bytes between snapshots).</summary>
        public void ServerPublish(NetId id, Vector3 pos, float yawDegrees, byte animState, long tick)
        {
            if (!_animals.TryGet(id, out var e)) return;
            var newPos = PlayerReplication.Quantize(pos);
            float newYaw = NetQuantization.QuantizeDegrees(yawDegrees, NetQuantization.YawBits);
            if (newPos == e.Pos && newYaw == e.YawDegrees && animState == e.AnimState) return;
            e.Pos = newPos;
            e.YawDegrees = newYaw;
            e.AnimState = animState;
            e.LastChangedTick = tick;
        }

        public void ServerRemove(NetId id, long tick)
        {
            if (_animals.Remove(id)) _removedAtTick[id.Value] = tick;
            _relevancy.ForgetEntity(id.Value);   // the tombstone reaches every client; per-client state drops
        }

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var ids = SortedIds();
            if (Interest != null)
            {
                var included = new List<uint>();
                foreach (uint id in ids)
                {
                    _animals.TryGet(new NetId(id), out var e);
                    if (Interest.IsRelevant(ctx.ViewPos, e.Pos)) included.Add(id);
                }
                _relevancy.ResetFull(ctx.ClientPlayerId, included, ctx.ServerTick);
                ids = included;
            }
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids)
            {
                _animals.TryGet(new NetId(id), out var e);
                WriteEntity(w, e);
            }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            var removed = new List<uint>();
            foreach (uint id in SortedIds())
            {
                _animals.TryGet(new NetId(id), out var e);
                if (Interest == null)
                {
                    if (e.LastChangedTick > baselineTick) changed.Add(id);
                }
                else if (_relevancy.ShouldWrite(ctx.ClientPlayerId, id, Interest.IsRelevant(ctx.ViewPos, e.Pos),
                                                e.LastChangedTick, baselineTick, ctx.ServerTick))
                {
                    changed.Add(id);
                }
            }
            if (Interest != null) _relevancy.CollectRemovals(ctx.ClientPlayerId, baselineTick, removed);
            foreach (var kv in _removedAtTick)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed)
            {
                _animals.TryGet(new NetId(id), out var e);
                WriteEntity(w, e);
            }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);

            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort changedCount)) return;
            if (full) _animals.Clear();
            for (int i = 0; i < changedCount; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _animals.Add(new NetId(e.NetIdValue), e);
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++)
                {
                    if (!r.ReadUInt32(out uint id)) return;
                    _animals.Remove(new NetId(id));
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (uint id in SortedIds())
            {
                _animals.TryGet(new NetId(id), out var e);
                h = NetHash.MixUInt32(h, id);
                h = NetHash.MixFloat(h, e.Pos.x);
                h = NetHash.MixFloat(h, e.Pos.y);
                h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixFloat(h, e.YawDegrees);
                h = NetHash.MixByte(h, e.AnimState);
                h = NetHash.MixByte(h, e.Species);
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, AnimalEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            NetWire.WritePos(w, e.Pos);
            w.WriteDegrees(e.YawDegrees, NetQuantization.YawBits);
            w.WriteUInt8(e.AnimState);
            w.WriteUInt8(e.Species);
        }

        static bool ReadEntity(NetPakReader r, out AnimalEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadUInt8(out byte anim)) return false;
            if (!r.ReadUInt8(out byte species)) return false;
            e = new AnimalEntity { NetIdValue = id, Pos = pos, YawDegrees = yaw, AnimState = anim, Species = species };
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
            foreach (var id in _animals.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }
}
