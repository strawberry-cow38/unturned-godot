using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// The append-only wire-id registries (MP_PLAN §2.3/§5 item 2). Once an id ships it is never reused or
    /// renumbered, even if the system/command is later retired. Command id 0 is reserved for the snapshot
    /// ack (SnapshotComposer.AckCommandId).
    /// </summary>
    public static class ReplicationIds
    {
        // IReplicatedSystem.SystemId space
        public const byte SystemPlayers = 1;
        public const byte SystemPlayerCombat = 2;   // Phase 5: alive/coarse-health/kills/deaths (CombatReplication.cs)
        public const byte SystemZombies = 3;        // Phase 5: transform + anim byte + speciality @12.5 Hz (ZombieReplication.cs)
        public const byte SystemProjectiles = 4;    // Phase 5: server-spawned grenades in flight

        // CommandRegistry id space (0 = snapshot ack, reserved)
        public const byte CommandMoveInput = 1;
        public const byte CommandFire = 2;          // Phase 5: the client aim ray -- the server steps the bullet
        public const byte CommandMelee = 3;
        public const byte CommandGrenade = 4;
        public const byte CommandReload = 5;

        // EventRegistry id space (server -> client, ReliableOrdered)
        public const byte EventJoinSnapshot = 1;   // the join-time FULL snapshot rides the reliable channel (§2.2: fragmentation is safe there)
        public const byte EventHitConfirm = 2;     // Phase 5 combat facts (CombatReplication.cs)
        public const byte EventImpactFx = 3;
        public const byte EventPlayerDied = 4;
        public const byte EventPlayerRespawned = 5;
        public const byte EventZombieHit = 6;
        public const byte EventZombieDied = 7;
        public const byte EventAttackSwing = 8;
        public const byte EventGrenadeExploded = 9;
    }

    /// <summary>
    /// The first real command (MP_PLAN §4 Phase 3): the client's per-tick movement intent, sent on the
    /// UnreliableSequenced channel at 50 Hz. Carries the input sequence number from day one (§5 item 6 --
    /// the field prediction v1 and future rollback both key on), local-space move axes, and the facing.
    /// The server never trusts more than this: position is integrated server-side (PlayerReplication).
    /// </summary>
    public struct MoveInput
    {
        public ushort Seq;        // client-local, monotonically increasing (wrap-around via NetSeq)
        public float MoveX;       // strafe axis [-1,1] (quantized to 8 bits on the wire)
        public float MoveY;       // forward axis [-1,1]
        public float YawDegrees;  // facing, wrapped into [0,360) by the wire encoding

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            w.WriteSignedNormalizedFloat(Clamp1(MoveX), 8);
            w.WriteSignedNormalizedFloat(Clamp1(MoveY), 8);
            w.WriteDegrees(YawDegrees, NetQuantization.YawBits);
        }

        public static bool TryRead(NetPakReader r, out MoveInput cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float mx)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float my)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            cmd = new MoveInput { Seq = seq, MoveX = mx, MoveY = my, YawDegrees = yaw };
            return true;
        }

        static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// Players as the first real IReplicatedSystem (MP_PLAN §4 Phase 3). One class serves both sides:
    /// the server mutates via the Server* methods (spawn on join, latest-wins MoveInput queue, a 50 Hz
    /// ServerStep that integrates PlayerMovementSim on flat ground), the client only ever writes through
    /// ReadSnapshot. Wire values are quantized at the authority (NetQuantization round-trip) so server and
    /// replica StateHash compare with exact equality, never a tolerance.
    ///
    /// Deliberately demo-grade movement: flat ground, stand stance, no jump/collision -- Phase 4 replaces
    /// this integration with the real PlayerController sim/shell split. The wire format (including
    /// LastProcessedInputSeq, which prediction v1 will consume) is the part designed to last.
    /// </summary>
    public sealed class PlayerReplication : IReplicatedSystem
    {
        public sealed class PlayerEntity
        {
            public uint NetIdValue { get; internal set; }
            public ushort OwnerPlayerId { get; internal set; }
            public Vector3 Pos { get; internal set; }
            public float YawDegrees { get; internal set; }
            public ushort LastProcessedInputSeq { get; internal set; }
            public long LastChangedTick { get; internal set; }

            // server-only integration state; null on client replicas
            internal PlayerMovementSim Sim;
            internal MoveInput CurrentInput;
            internal bool HasInput;
            // true once ServerDrive has taken over this entity: an in-process shell (the listen-server /
            // SP-loopback local player, MP_PLAN §4 Phase 4) steps the REAL sim-core + physics and writes
            // the result here; the internal flat-ground integration must not fight it.
            internal bool ExternallyDriven;
        }

        public byte SystemId => ReplicationIds.SystemPlayers;

        readonly NetEntityRegistry<PlayerEntity> _players = new NetEntityRegistry<PlayerEntity>();
        readonly Dictionary<ushort, uint> _netIdByOwner = new Dictionary<ushort, uint>();
        // removal tombstones for delta blocks; a baseline older than the dirty ring gets a full resend
        // (which never consults this), so entries older than the ring depth are pruned on write
        readonly Dictionary<uint, long> _removedAtTick = new Dictionary<uint, long>();

        public int Count => _players.Count;

        public IEnumerable<PlayerEntity> All
        {
            get
            {
                foreach (var id in _players.Ids)
                {
                    _players.TryGet(id, out var e);
                    yield return e;
                }
            }
        }

        public bool TryGetByOwner(ushort ownerPlayerId, out PlayerEntity entity)
        {
            entity = null;
            return _netIdByOwner.TryGetValue(ownerPlayerId, out uint id) && _players.TryGet(new NetId(id), out entity);
        }

        // ---- server side ----

        public PlayerEntity ServerSpawn(NetId id, ushort ownerPlayerId, Vector3 pos, long tick)
        {
            var e = new PlayerEntity
            {
                NetIdValue = id.Value,
                OwnerPlayerId = ownerPlayerId,
                Pos = Quantize(pos),
                YawDegrees = 0f,
                LastChangedTick = tick,
                Sim = new PlayerMovementSim(),
            };
            _players.Add(id, e);
            _netIdByOwner[ownerPlayerId] = id.Value;
            _removedAtTick.Remove(id.Value);
            return e;
        }

        public void ServerRemove(ushort ownerPlayerId, long tick)
        {
            if (!_netIdByOwner.TryGetValue(ownerPlayerId, out uint id)) return;
            _netIdByOwner.Remove(ownerPlayerId);
            if (_players.Remove(new NetId(id))) _removedAtTick[id] = tick;
        }

        /// <summary>Latest-wins input queue: MoveInput rides UnreliableSequenced, so a reordered stale
        /// command must never override a newer one already applied.</summary>
        public void ServerQueueInput(ushort ownerPlayerId, in MoveInput input)
        {
            if (!TryGetByOwner(ownerPlayerId, out var e) || e.Sim == null) return;
            if (e.HasInput && !NetSeq.IsNewer(input.Seq, e.CurrentInput.Seq)) return;
            e.CurrentInput = input;
            e.HasInput = true;
        }

        /// <summary>One 50 Hz authoritative movement step for every server-owned player. Held-key model:
        /// the latest received input keeps applying every tick until replaced (single loss costs nothing).
        /// Externally-driven entities (ServerDrive) are skipped -- their shell already stepped the real
        /// sim-core + physics this tick.</summary>
        public void ServerStep(long tick, float dt)
        {
            foreach (uint id in SortedIds())
            {
                _players.TryGet(new NetId(id), out var e);
                if (e.Sim == null || !e.HasInput || e.ExternallyDriven) continue;

                var input = e.CurrentInput;
                var newPos = IntegrateFlat(e.Sim, in input, e.Pos, dt);
                float newYaw = NetQuantization.QuantizeDegrees(input.YawDegrees, NetQuantization.YawBits);
                bool changed = newPos != e.Pos || newYaw != e.YawDegrees || input.Seq != e.LastProcessedInputSeq;
                e.Pos = newPos;
                e.YawDegrees = newYaw;
                e.LastProcessedInputSeq = input.Seq;
                if (changed) e.LastChangedTick = tick;
            }
        }

        /// <summary>THE shared movement integration -- one tick of PlayerMovementSim on flat ground,
        /// local axes rotated by yaw (forward = +Z at yaw 0, yaw counter-clockwise), result quantized to
        /// the wire grid. The server's ServerStep and the client's prediction (ClientPrediction,
        /// MP_PLAN §2.5b "server runs the same inputs through the same sim-core") both call THIS, so a
        /// predicted trajectory is bit-identical to the authoritative one under identical inputs.</summary>
        public static Vector3 IntegrateFlat(PlayerMovementSim sim, in MoveInput input, Vector3 pos, float dt)
        {
            var vel = sim.Step(new Vector2(input.MoveX, input.MoveY), wantJump: false, grounded: true, dt);
            float yawRad = input.YawDegrees * (Mathf.PI / 180f);
            float sin = Mathf.Sin(yawRad), cos = Mathf.Cos(yawRad);
            var worldDelta = new Vector3(
                (vel.x * cos + vel.z * sin) * dt,
                0f,
                (vel.z * cos - vel.x * sin) * dt);
            return Quantize(pos + worldDelta);
        }

        /// <summary>Authoritative write-through for an entity whose sim runs OUTSIDE this class -- the
        /// listen-server / SP-loopback local player, whose PlayerController shell steps the same sim-core
        /// plus real collision in-process (MP_PLAN §2.1: SP = the server world + loopback; prediction is a
        /// pass-through). Marks the entity externally driven so ServerStep stops integrating it.</summary>
        public void ServerDrive(ushort ownerPlayerId, Vector3 pos, float yawDegrees, ushort lastProcessedInputSeq, long tick)
        {
            if (!TryGetByOwner(ownerPlayerId, out var e)) return;
            e.ExternallyDriven = true;
            var newPos = Quantize(pos);
            float newYaw = NetQuantization.QuantizeDegrees(yawDegrees, NetQuantization.YawBits);
            bool changed = newPos != e.Pos || newYaw != e.YawDegrees || lastProcessedInputSeq != e.LastProcessedInputSeq;
            e.Pos = newPos;
            e.YawDegrees = newYaw;
            e.LastProcessedInputSeq = lastProcessedInputSeq;
            if (changed) e.LastChangedTick = tick;
        }

        /// <summary>Server-side teleport (Phase 5 respawn): move the entity without consulting its input.
        /// Works for driven and undriven entities alike -- a driven shell re-asserts its own transform on
        /// its next ServerDrive anyway.</summary>
        public void ServerTeleport(ushort ownerPlayerId, Vector3 pos, long tick)
        {
            if (!TryGetByOwner(ownerPlayerId, out var e)) return;
            var newPos = Quantize(pos);
            if (newPos == e.Pos) return;
            e.Pos = newPos;
            e.LastChangedTick = tick;
        }

        /// <summary>Drop the held-keys input (Phase 5 death: a corpse must stop integrating the victim's
        /// last MoveInput; fresh inputs are rejected at the dispatch gate until respawn).</summary>
        public void ServerClearInput(ushort ownerPlayerId)
        {
            if (TryGetByOwner(ownerPlayerId, out var e)) e.HasInput = false;
        }

        /// <summary>Round a position through the exact wire encoding -- authoritative state and client
        /// prediction both live on this grid so parity checks are exact equality, never a tolerance.</summary>
        public static Vector3 Quantize(Vector3 pos) => new Vector3(
            NetQuantization.QuantizeClampedFloat(pos.x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits),
            NetQuantization.QuantizeClampedFloat(pos.y, NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits),
            NetQuantization.QuantizeClampedFloat(pos.z, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits));

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            var ids = SortedIds();
            w.WriteUInt16((ushort)ids.Count);
            foreach (uint id in ids)
            {
                _players.TryGet(new NetId(id), out var e);
                WriteEntity(w, e);
            }
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<uint>();
            foreach (uint id in SortedIds())
            {
                _players.TryGet(new NetId(id), out var e);
                if (e.LastChangedTick > baselineTick) changed.Add(id);
            }
            var removed = new List<uint>();
            foreach (var kv in _removedAtTick)
                if (kv.Value > baselineTick) removed.Add(kv.Key);
            removed.Sort();

            w.WriteUInt16((ushort)changed.Count);
            foreach (uint id in changed)
            {
                _players.TryGet(new NetId(id), out var e);
                WriteEntity(w, e);
            }
            w.WriteUInt16((ushort)removed.Count);
            foreach (uint id in removed) w.WriteUInt32(id);

            // prune tombstones older than the dirty ring: any client that stale gets a full snapshot anyway
            PruneTombstones(ctx.ServerTick);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort changedCount)) return;
            if (full)
            {
                _players.Clear();
                _netIdByOwner.Clear();
            }
            for (int i = 0; i < changedCount; i++)
            {
                if (!ReadEntity(r, out var e)) return;
                _players.Add(new NetId(e.NetIdValue), e);
                _netIdByOwner[e.OwnerPlayerId] = e.NetIdValue;
            }
            if (!full)
            {
                if (!r.ReadUInt16(out ushort removedCount)) return;
                for (int i = 0; i < removedCount; i++)
                {
                    if (!r.ReadUInt32(out uint id)) return;
                    if (_players.TryGet(new NetId(id), out var gone)) _netIdByOwner.Remove(gone.OwnerPlayerId);
                    _players.Remove(new NetId(id));
                }
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            foreach (uint id in SortedIds())
            {
                _players.TryGet(new NetId(id), out var e);
                h = NetHash.MixUInt32(h, id);
                h = NetHash.MixUInt32(h, e.OwnerPlayerId);
                h = NetHash.MixFloat(h, e.Pos.x);
                h = NetHash.MixFloat(h, e.Pos.y);
                h = NetHash.MixFloat(h, e.Pos.z);
                h = NetHash.MixFloat(h, e.YawDegrees);
                h = NetHash.MixUInt32(h, e.LastProcessedInputSeq);
            }
            return h;
        }

        static void WriteEntity(NetPakWriter w, PlayerEntity e)
        {
            w.WriteUInt32(e.NetIdValue);
            w.WriteUInt16(e.OwnerPlayerId);
            w.WriteClampedFloat(e.Pos.x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
            w.WriteClampedFloat(e.Pos.y, NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits);
            w.WriteClampedFloat(e.Pos.z, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
            w.WriteDegrees(e.YawDegrees, NetQuantization.YawBits);
            w.WriteUInt16(e.LastProcessedInputSeq);
        }

        static bool ReadEntity(NetPakReader r, out PlayerEntity e)
        {
            e = null;
            if (!r.ReadUInt32(out uint id)) return false;
            if (!r.ReadUInt16(out ushort owner)) return false;
            if (!r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float x)) return false;
            if (!r.ReadClampedFloat(NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits, out float y)) return false;
            if (!r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float z)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadUInt16(out ushort seq)) return false;
            e = new PlayerEntity
            {
                NetIdValue = id,
                OwnerPlayerId = owner,
                Pos = new Vector3(x, y, z),
                YawDegrees = yaw,
                LastProcessedInputSeq = seq,
            };
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
            foreach (var id in _players.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }
    }
}
