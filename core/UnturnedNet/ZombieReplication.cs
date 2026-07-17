using System.Collections.Generic;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>What a zombie replica needs to render: locomotion family, one anim byte (§3.5
    /// "transform + anim-state byte + speciality").</summary>
    public enum ZombieNetAnim : byte { Idle = 0, Walk = 1, Attack = 2, Dead = 3 }

    /// <summary>
    /// Zombies as an IReplicatedSystem (MP_PLAN §3.5, SystemId 3). SERVER side: the real ZombieController
    /// brains own all behavior; a game-side sync (ZombieNetSync) publishes their transform/anim/speciality
    /// into these entities every 4th tick (12.5 Hz -- the §2.5 cadence table; cadence is the PUBLISHER's
    /// choice, the wire framing doesn't change). CLIENT side: replicas drive IsPuppet ZombieControllers that
    /// interpolate -- no AI, no nav, no physics ever runs from this data.
    /// </summary>
    public sealed class ZombieReplication : IReplicatedSystem
    {
        /// <summary>Mirror of the game's ZombieController.ESpeciality byte (NORMAL 0, SPRINTER 1, CRAWLER 2,
        /// FLANKER 3, BURNER 4, ACID 5) -- core only needs CRAWLER for the short hitbox.</summary>
        public const byte SpecialityCrawler = 2;

        /// <summary>Zone-cylinder height (crawlers hug the ground -- matches ZombieController's capsules).</summary>
        public static float HeightFor(byte speciality) => speciality == SpecialityCrawler ? 0.8f : 1.8f;

        public sealed class ZombieEntity
        {
            public uint NetIdValue { get; internal set; }
            public Vector3 Pos { get; internal set; }
            public float YawDegrees { get; internal set; }
            public byte AnimState { get; internal set; }
            public byte Speciality { get; internal set; }
            public long LastChangedTick { get; internal set; }

            public bool IsDead => AnimState == (byte)ZombieNetAnim.Dead;
        }

        public byte SystemId => ReplicationIds.SystemZombies;

        /// <summary>Phase 8 interest policy (§2.6): null = AllRelevant (byte-identical to the pre-Phase-8
        /// wire). The game host sets rings + the nav-pocket CellOf on dedicated/loopback servers; client
        /// replicas never set it.</summary>
        public InterestPolicy Interest;

        readonly NetEntityRegistry<ZombieEntity> _zombies = new NetEntityRegistry<ZombieEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();
        readonly RelevancyTracker _relevancy = new RelevancyTracker();

        /// <summary>Disconnect/rejoin hygiene: per-client relevancy state must not leak to a recycled id.</summary>
        public void ForgetClient(ushort clientPlayerId) => _relevancy.ForgetClient(clientPlayerId);

        public int Count => _zombies.Count;

        public bool TryGet(NetId id, out ZombieEntity entity) => _zombies.TryGet(id, out entity);

        public IEnumerable<ZombieEntity> All
        {
            get
            {
                foreach (uint id in SortedIds())
                {
                    _zombies.TryGet(new NetId(id), out var e);
                    yield return e;
                }
            }
        }

        // ---- server side ----

        public ZombieEntity ServerSpawn(NetId id, byte speciality, Vector3 pos, long tick)
        {
            var e = new ZombieEntity
            {
                NetIdValue = id.Value,
                Speciality = speciality,
                Pos = PlayerReplication.Quantize(pos),
                YawDegrees = 0f,
                AnimState = (byte)ZombieNetAnim.Idle,
                LastChangedTick = tick,
            };
            _zombies.Add(id, e);
            _removedAtTick.Remove(id.Value);
            return e;
        }

        /// <summary>Publish the brain's current state (quantized; dirty only on real change, so an idle
        /// zombie costs no delta bytes between snapshots).</summary>
        public void ServerPublish(NetId id, Vector3 pos, float yawDegrees, byte animState, long tick)
        {
            if (!_zombies.TryGet(id, out var e)) return;
            var newPos = PlayerReplication.Quantize(pos);
            float newYaw = NetQuantization.QuantizeDegrees(yawDegrees, NetQuantization.YawBits);
            if (newPos == e.Pos && newYaw == e.YawDegrees && animState == e.AnimState) return;
            e.Pos = newPos;
            e.YawDegrees = newYaw;
            e.AnimState = animState;
            e.LastChangedTick = tick;
        }

        /// <summary>Immediate anim flip (e.g. ServerCombat marks a killed zombie Dead the same tick, so a
        /// bullet can never re-hit it inside the publish window).</summary>
        public void ServerSetAnim(NetId id, ZombieNetAnim anim, long tick)
        {
            if (!_zombies.TryGet(id, out var e) || e.AnimState == (byte)anim) return;
            e.AnimState = (byte)anim;
            e.LastChangedTick = tick;
        }

        public void ServerRemove(NetId id, long tick)
        {
            if (_zombies.Remove(id)) _removedAtTick[id.Value] = tick;
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
                    _zombies.TryGet(new NetId(id), out var e);
                    if (Interest.IsRelevant(ctx.ViewPos, e.Pos)) included.Add(id);
                }
                _relevancy.ResetFull(ctx.ClientPlayerId, included, ctx.ServerTick);
                ids = included;
            }
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids)
            {
                _zombies.TryGet(new NetId(id), out var e);
                WriteEntity(w, e);
            }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            var removed = new List<uint>();
            foreach (uint id in SortedIds())
            {
                _zombies.TryGet(new NetId(id), out var e);
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
            // relevancy-exit removals ride the same wire removals as despawns -- replicas can't tell, and
            // don't need to (the entity re-enters through ShouldWrite when it becomes relevant again)
            if (Interest != null) _relevancy.CollectRemovals(ctx.ClientPlayerId, baselineTick, removed);
            foreach (var kv in _removedAtTick)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed)
            {
                _zombies.TryGet(new NetId(id), out var e);
                WriteEntity(w, e);
            }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);

            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort changedCount)) return;
            if (full) _zombies.Clear();
            for (int i = 0; i < changedCount; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _zombies.Add(new NetId(e.NetIdValue), e);
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++)
                {
                    if (!r.ReadUInt32(out uint id)) return;
                    _zombies.Remove(new NetId(id));
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (uint id in SortedIds())
            {
                _zombies.TryGet(new NetId(id), out var e);
                h = NetHash.MixUInt32(h, id);
                h = NetHash.MixFloat(h, e.Pos.x);
                h = NetHash.MixFloat(h, e.Pos.y);
                h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixFloat(h, e.YawDegrees);
                h = NetHash.MixByte(h, e.AnimState);
                h = NetHash.MixByte(h, e.Speciality);
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, ZombieEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            NetWire.WritePos(w, e.Pos);
            w.WriteDegrees(e.YawDegrees, NetQuantization.YawBits);
            w.WriteUInt8(e.AnimState);
            w.WriteUInt8(e.Speciality);
        }

        static bool ReadEntity(NetPakReader r, out ZombieEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadUInt8(out byte anim)) return false;
            if (!r.ReadUInt8(out byte speciality)) return false;
            e = new ZombieEntity { NetIdValue = id, Pos = pos, YawDegrees = yaw, AnimState = anim, Speciality = speciality };
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
            foreach (var id in _zombies.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }

    /// <summary>
    /// Short-lived server-spawned projectiles (SystemId 4) -- currently just grenades (§3.4 "grenades =
    /// server-spawned short-lived entities: Snap while flying, explosion Event"). Same full/delta/tombstone
    /// shape as the other systems; entities live a couple of seconds so the block stays tiny.
    /// </summary>
    public enum ProjectileKind : byte { Grenade = 0 }

    public sealed class ProjectileReplication : IReplicatedSystem
    {
        public sealed class ProjectileEntity
        {
            public uint NetIdValue { get; internal set; }
            public byte Kind { get; internal set; }
            public Vector3 Pos { get; internal set; }
            public long LastChangedTick { get; internal set; }
        }

        public byte SystemId => ReplicationIds.SystemProjectiles;

        readonly NetEntityRegistry<ProjectileEntity> _entities = new NetEntityRegistry<ProjectileEntity>();
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();

        public int Count => _entities.Count;

        public bool TryGet(NetId id, out ProjectileEntity entity) => _entities.TryGet(id, out entity);

        public IEnumerable<ProjectileEntity> All
        {
            get
            {
                foreach (uint id in SortedIds())
                {
                    _entities.TryGet(new NetId(id), out var e);
                    yield return e;
                }
            }
        }

        public ProjectileEntity ServerSpawn(NetId id, ProjectileKind kind, Vector3 pos, long tick)
        {
            var e = new ProjectileEntity { NetIdValue = id.Value, Kind = (byte)kind, Pos = PlayerReplication.Quantize(pos), LastChangedTick = tick };
            _entities.Add(id, e);
            _removedAtTick.Remove(id.Value);
            return e;
        }

        public void ServerPublish(NetId id, Vector3 pos, long tick)
        {
            if (!_entities.TryGet(id, out var e)) return;
            var newPos = PlayerReplication.Quantize(pos);
            if (newPos == e.Pos) return;
            e.Pos = newPos;
            e.LastChangedTick = tick;
        }

        public void ServerRemove(NetId id, long tick)
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
                WriteEntity(w, e);
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
                WriteEntity(w, e);
            }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);

            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort changedCount)) return;
            if (full) _entities.Clear();
            for (int i = 0; i < changedCount; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _entities.Add(new NetId(e.NetIdValue), e);
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++)
                {
                    if (!r.ReadUInt32(out uint id)) return;
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
                h = NetHash.MixByte(h, e.Kind);
                h = NetHash.MixFloat(h, e.Pos.x);
                h = NetHash.MixFloat(h, e.Pos.y);
                h = NetHash.MixFloat(h, e.Pos.z);
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, ProjectileEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            w.WriteUInt8(e.Kind);
            NetWire.WritePos(w, e.Pos);
        }

        static bool ReadEntity(NetPakReader r, out ProjectileEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt8(out byte kind)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            e = new ProjectileEntity { NetIdValue = id, Kind = kind, Pos = pos };
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
            foreach (var id in _entities.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }
}
