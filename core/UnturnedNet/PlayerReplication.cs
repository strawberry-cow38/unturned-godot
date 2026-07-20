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
        public const byte SystemSkills = 5;         // Phase 6: owner-only experience + level bytes (SkillsReplication.cs, §3.2)
        public const byte SystemDeployables = 6;    // Phase 6: the power-graph inputs -- entities/wires/scalars (DeployableReplication.cs, §3.1)
        public const byte SystemInventory = 7;      // Phase 6: owner-only full-grid block (InventoryReplication.cs, §3.3)
        public const byte SystemWorldItems = 8;     // Phase 6: dropped/loot items as NetId entities (WorldItemReplication.cs, §3.3)
        public const byte SystemVehicles = 9;       // Phase 7: transform + velocities + wheel steer + scalars @25 Hz (VehicleReplication.cs, §3.6)
        public const byte SystemWorldClock = 10;    // Phase 8: day-night base + day length; time derives from the snapshot tick (WorldReplication.cs, §3.7)
        public const byte SystemCrops = 11;         // Phase 8: planted crops -- growth derives from PlantedAtTick (WorldReplication.cs, §3.7)
        public const byte SystemResources = 12;     // Phase 8: tree/resource alive-bitmap keyed by load-order index (WorldReplication.cs, §3.7)
        // SP/MP-unify wave 2 (v11): three new systems allocated together, composed after Resources, EXCLUDED
        // from EnableSyncCheck (owner-only / relevancy-filtered). Registered as empty stubs by the reservation
        // commit; bodies filled by their owners (13 = tinyclaw, 14/15 = cow tools).
        public const byte SystemVitals = 13;        // owner-only fine vitals (food/water/stamina/infection); resolves the long-reserved SystemId 13 (B5). PlayerVitalsReplication.cs
        public const byte SystemContainers = 14;    // world containers/store-shelves as replicated fixtures (A1). ContainerReplication.cs
        public const byte SystemAnimals = 15;       // wildlife (deer/pig/cow) puppets (A5). AnimalReplication.cs
        public const byte SystemSyncCheck = 255;    // hardening: rolling per-system StateHash block for desync detection, composed LAST
                                                    // when SnapshotComposer.EnableSyncCheck is on; never a real system id (reserved)

        // CommandRegistry id space (0 = snapshot ack, reserved)
        public const byte CommandMoveInput = 1;
        public const byte CommandFire = 2;          // Phase 5: the client aim ray -- the server steps the bullet
        public const byte CommandMelee = 3;
        public const byte CommandGrenade = 4;
        public const byte CommandReload = 5;
        public const byte CommandUpgradeSkill = 6;     // Phase 6 (§3.2)
        public const byte CommandPlaceDeployable = 7;  // Phase 6 (§3.1)
        public const byte CommandSalvageDeployable = 8;
        public const byte CommandConnectWire = 9;
        public const byte CommandRemoveWire = 10;
        public const byte CommandToggleDeployable = 11;
        public const byte CommandMoveItem = 12;        // Phase 6 (§3.3)
        public const byte CommandDropItem = 13;
        public const byte CommandPickupItem = 14;
        public const byte CommandEquipItem = 15;
        public const byte CommandCraft = 16;
        public const byte CommandConsume = 17;
        public const byte CommandOpenStorage = 18;
        public const byte CommandCloseStorage = 19;
        public const byte CommandConsole = 20;         // Phase 6: DevConsole mutations, server-gated (§2.3 "including DevConsole")
        public const byte CommandEnterVehicle = 21;    // Phase 7 (§3.6): transactional, gated server-side (occupancy + reach)
        public const byte CommandExitVehicle = 22;
        public const byte CommandDriveInput = 23;      // Phase 7: @50 Hz UnreliableSeq, driver-only, feeds Vehicle.Drive
        public const byte CommandPlantCrop = 24;       // Phase 8 (§3.7): consumes the seed item; server owns the growth clock
        public const byte CommandHarvestCrop = 25;     // Phase 8: server checks growth + rolls the AGRICULTURE second yield
        public const byte CommandVehicleState = 26;    // Part A (CLIENT_PREDICTION_PLAN §5.2): the predicted DRIVER's reported vehicle state @25 Hz UnreliableSeq -- envelope-validated at the choke point, then ADOPTED as the vehicle's truth (retail client authority). CommandDriveInput 23 stays registered as the non-predicted fallback.
        public const byte CommandPlayerState = 27;     // mp-clientauth-foot (wire v9): the OWNER's on-foot transform stream @50 Hz UnreliableSeq -- the vehicle client-authority model applied to walking (PlayerAuthority.cs): envelope-validated, then adopted via ServerDrive. Replaces MoveInput as the shell client's movement wire; MoveInput 1 stays registered for demo walkers/loopback.

        // SP/MP-unify wave 2 (v11): four new client->server commands, allocated together with the systems above.
        public const byte CommandPickupDeployable = 28;   // B2: return a placed deployable to the bag (distinct from Salvage's scrap)
        public const byte CommandExtractFuel = 29;        // A2: pull fuel from a gas-station pump into a held can
        public const byte CommandAttachTow = 30;          // B11: tie a rope between two vehicles (tower NetId, towed NetId)
        public const byte CommandDetachTow = 31;          // B11: untie a vehicle's rope (either end)

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
        public const byte EventXpAwarded = 10;         // Phase 6 (§3.2, to the owner)
        public const byte EventDeployablePlaced = 11;  // Phase 6 (§3.1, topology = reliable facts)
        public const byte EventDeployableRemoved = 12;
        public const byte EventWireConnected = 13;
        public const byte EventWireRemoved = 14;
        public const byte EventDeployableToggled = 15;
        public const byte EventWorldItemSpawned = 16;  // Phase 6 (§3.3)
        public const byte EventWorldItemSettled = 17;
        public const byte EventWorldItemRemoved = 18;
        public const byte EventItemPickupDenied = 19;  // to the requester: pickup validated but the grid was full
        public const byte EventConsoleResult = 20;     // to the sender: the server's console verdict line
        public const byte EventStorageOpened = 21;     // to the opener (open/close arbitration, §3.7)
        public const byte EventStorageClosed = 22;
        public const byte EventVehicleEntered = 23;    // Phase 7 (§3.6): occupancy facts (also ride the snapshot; events give immediacy)
        public const byte EventVehicleExited = 24;
        public const byte EventCropPlanted = 25;       // Phase 8 (§3.7): plant/harvest facts (also ride the snapshot; events give fx immediacy)
        public const byte EventCropHarvested = 26;
        public const byte EventResourceHarvested = 27; // Phase 8: tree/resource alive-bit flips by load-order index
        public const byte EventResourceRespawned = 28;
        public const byte EventVehicleRecov = 29;      // Part A: server rollback of an out-of-envelope predicted driver (retail tellRecov, U3 InteractableVehicle.cs:2095-2109) -- ReliableOrdered, driver-unicast
        public const byte EventMisprediction = 30;     // C3 rewind+replay correction fact -- RETIRED by mp-clientauth-foot (wire v9): with client-authoritative on-foot movement there is no server sim of the owner to mispredict against. Id never reused (append-only registry).
        public const byte EventPlayerRecov = 31;       // mp-clientauth-foot (wire v9): server rollback of an out-of-envelope on-foot claim (the VehicleRecov 29 shape for walkers) -- ReliableOrdered, owner-unicast; client teleports to the last-good pos, echoes the counter in its state stream
    }

    /// <summary>
    /// The first real command (MP_PLAN §4 Phase 3): the client's per-tick movement intent, sent on the
    /// UnreliableSequenced channel at 50 Hz. Carries the input sequence number from day one (§5 item 6 --
    /// the field prediction v1 and future rollback both key on), local-space move axes, and the facing.
    /// The server never trusts more than this: position is integrated server-side (PlayerReplication).
    /// </summary>
    public struct MoveInput
    {
        // v2 buttons bitfield (PEI_CLIENT_PLAN §3 C2): bit 0 = jump; bits 1-2 = the on-foot stance the
        // client sim consumed (the mp-inchworm fix: without it a sprinting shell predicted SPEED_SPRINT
        // while the server avatar integrated STAND-WALK -- the reconciler dragged the gap back every tick).
        // The RESULTING stance rides, not the X/Z/Shift key edges: state self-corrects over the latest-wins
        // unreliable channel after a drop, edges don't. 0 = STAND so a buttons-less MoveInput keeps the old
        // stand-walk meaning. Bits 3-7 headroom. Adding a bit is NOT a wire break; widening the field is.
        public const byte ButtonJump = 1 << 0;
        const int StanceShift = 1;
        const byte StanceMask = 0b11;

        public ushort Seq;        // client-local, monotonically increasing (wrap-around via NetSeq)
        public float MoveX;       // strafe axis [-1,1] (quantized to 8 bits on the wire)
        public float MoveY;       // forward axis [-1,1]
        public float YawDegrees;  // facing, wrapped into [0,360) by the wire encoding
        public byte Buttons;      // v2: held-button bits (ButtonJump | PackStance(...))
        // v9 (mp-clientauth-foot): the C2 ClaimedPos/HasClaim claim fields are GONE from the wire --
        // the shell client no longer sends MoveInput at all (it streams PlayerStateCommand and the
        // server adopts it); MoveInput remains the demo-walker/loopback movement intent only.

        public bool Jump => (Buttons & ButtonJump) != 0;

        /// <summary>The on-foot stance carried in buttons bits 1-2 -- what the server avatar must
        /// integrate at so client-predicted and server-integrated per-tick distances match.</summary>
        public EPlayerStance Stance
        {
            get
            {
                switch ((Buttons >> StanceShift) & StanceMask)
                {
                    case 1: return EPlayerStance.SPRINT;
                    case 2: return EPlayerStance.CROUCH;
                    case 3: return EPlayerStance.PRONE;
                    default: return EPlayerStance.STAND;
                }
            }
        }

        /// <summary>Encode an on-foot stance into buttons bits 1-2. Only the four wire stances exist;
        /// anything else (DRIVING/SITTING never send MoveInput anyway) degrades to STAND.</summary>
        public static byte PackStance(EPlayerStance stance)
        {
            switch (stance)
            {
                case EPlayerStance.SPRINT: return 1 << StanceShift;
                case EPlayerStance.CROUCH: return 2 << StanceShift;
                case EPlayerStance.PRONE:  return 3 << StanceShift;
                default: return 0;
            }
        }

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            w.WriteSignedNormalizedFloat(Clamp1(MoveX), 8);
            w.WriteSignedNormalizedFloat(Clamp1(MoveY), 8);
            w.WriteDegrees(YawDegrees, NetQuantization.YawBits);
            w.WriteUInt8(Buttons);   // v2 (NetProtocol.Version 3): the buttons byte -- v2 peers version-reject before ever parsing this
        }

        public static bool TryRead(NetPakReader r, out MoveInput cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float mx)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float my)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadUInt8(out byte buttons)) return false;
            cmd = new MoveInput { Seq = seq, MoveX = mx, MoveY = my, YawDegrees = yaw, Buttons = buttons };
            return true;
        }

        static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// C1 (CLIENT_PREDICTION_PLAN §4.2): the MoveInput DATAGRAM -- the newest input plus up to two
    /// previous ones, oldest-first, each a full MoveInput entry with its own explicit seq (seqs are
    /// usually consecutive, but the seq-0 wrap skip makes an implicit newest-minus-i encoding wrong once
    /// per 65535 inputs). A single lost or jitter-overtaken datagram no longer leaves a hole the server
    /// must guess across (coast/hole-substitution on held axes -- the residual high-RTT inchworm's main
    /// engine, plan §4.1 H1); a hole now needs 3 CONSECUTIVE datagram losses (~0.001% at 2% loss vs 2%
    /// with one input per datagram). ServerQueueInput's strictly-increasing-seq guard makes the
    /// redundant backfill idempotent, so the receiver just enqueues every entry oldest-first. This is
    /// the port-shaped equivalent of retail's reliable input channel (U3 PlayerInput.cs:1713 sends
    /// inputs ENetReliability.Reliable and the server never speculates) -- the same guarantee, the
    /// server integrates the real input stream, without retail's freeze-on-loss added latency.
    /// MP_PLAN §2.3 specified this ("carrying the last 3 inputs redundantly"); the shipped v3-v4 wire
    /// carried one. Wire: count:2 bits (1-3, 0 rejected), then count MoveInput entries oldest-first --
    /// the NetProtocol.Version 4->5 break.
    /// </summary>
    public struct MoveInputPacket
    {
        public const int MaxInputs = 3;

        public byte Count;
        public MoveInput I0, I1, I2;   // oldest-first; only the first Count are valid

        public MoveInput Get(int i) => i == 0 ? I0 : (i == 1 ? I1 : I2);

        public void Write(NetPakWriter w)
        {
            w.WriteBits(Count, 2);
            for (int i = 0; i < Count; i++) Get(i).Write(w);
        }

        public static bool TryRead(NetPakReader r, out MoveInputPacket pkt)
        {
            pkt = default;
            if (!r.ReadBits(2, out uint count) || count == 0 || count > MaxInputs) return false;
            pkt.Count = (byte)count;
            for (int i = 0; i < pkt.Count; i++)
            {
                if (!MoveInput.TryRead(r, out var m)) return false;
                if (i == 0) pkt.I0 = m; else if (i == 1) pkt.I1 = m; else pkt.I2 = m;
            }
            return true;
        }
    }

    // (MispredictionEvent -- EventId 30 -- deleted by mp-clientauth-foot v9: with client-authoritative
    // on-foot movement there is no server sim of the owner to mispredict against. Id retired, never reused.)

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
            // mp-event-coalesce (v10): the owner-facing ACK for the redundant combat carry -- the highest
            // combat seq the server has applied from this owner's PlayerStateCommand stream. Rides every
            // snapshot beside LastProcessedInputSeq; the owner's client drops acked events from its pending
            // ring (NetWorldClient.AckCombat) so it stops re-including them.
            public ushort LastProcessedCombatSeq { get; internal set; }
            public long LastChangedTick { get; internal set; }

            // server-only integration state; null on client replicas
            internal PlayerMovementSim Sim;
            internal MoveInput CurrentInput;
            internal bool HasInput;
            // true once ServerDrive has taken over this entity: either an in-process shell (the
            // listen-server / SP-loopback local player) writing its own result, or -- since v9 -- the
            // owner's envelope-validated claim stream (ServerPlayerAuthority); the internal flat-ground
            // integration must not fight either.
            internal bool ExternallyDriven;
            /// <summary>Read-only view for game-side consumers (the follower sync, tests).</summary>
            public bool IsExternallyDriven => ExternallyDriven;
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

        /// <summary>The peer's currently-held MoveInput (the held-keys model's latest). False when none is
        /// held -- never received one yet, or cleared by death/vehicle-enter (ServerClearInput). ServerStep's
        /// flat demo integration reads this view.</summary>
        public bool TryGetHeldInput(ushort ownerPlayerId, out MoveInput input)
        {
            input = default;
            if (!TryGetByOwner(ownerPlayerId, out var e) || !e.HasInput) return false;
            input = e.CurrentInput;
            return true;
        }

        /// <summary>Latest-wins held input: MoveInput rides UnreliableSequenced, so a reordered stale
        /// command must never override a newer one already applied. (The v9 note: the C1-C2 in-order
        /// jitter buffer + coast/hole machinery that used to live here served the server-side avatar
        /// integration of the OWNER -- deleted with that model; demo walkers integrate held-latest.)</summary>
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

        /// <summary>Authoritative write-through for an entity whose movement runs OUTSIDE this class:
        /// the listen-server / SP-loopback local player's shell writing its own in-process result, and
        /// -- since v9 -- the owner's envelope-validated claim stream (ServerPlayerAuthority adopts each
        /// accepted PlayerStateCommand through THIS seam). Marks the entity externally driven so
        /// ServerStep stops integrating it.</summary>
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

        /// <summary>mp-event-coalesce (v10): stamp the owner-facing combat ack (the highest applied combat
        /// seq) onto the entity so it rides the next snapshot beside LastProcessedInputSeq. Marks the entity
        /// dirty only when the value actually advances, so a steady stream costs no extra delta traffic. The
        /// owner's client reads it back and drops acked events from its redundant pending ring.</summary>
        public void SetCombatAck(ushort ownerPlayerId, ushort combatSeq, long tick)
        {
            if (!TryGetByOwner(ownerPlayerId, out var e)) return;
            if (e.LastProcessedCombatSeq == combatSeq) return;
            e.LastProcessedCombatSeq = combatSeq;
            e.LastChangedTick = tick;
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
            if (!TryGetByOwner(ownerPlayerId, out var e)) return;
            e.HasInput = false;
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
                h = NetHash.MixUInt32(h, e.LastProcessedCombatSeq);   // v10 (mp-event-coalesce): the combat ack
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
            w.WriteUInt16(e.LastProcessedCombatSeq);   // v10 (mp-event-coalesce): the combat ack rides beside the input ack
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
            if (!r.ReadUInt16(out ushort combatSeq)) return false;   // v10 (mp-event-coalesce): the combat ack
            e = new PlayerEntity
            {
                NetIdValue = id,
                OwnerPlayerId = owner,
                Pos = new Vector3(x, y, z),
                YawDegrees = yaw,
                LastProcessedInputSeq = seq,
                LastProcessedCombatSeq = combatSeq,
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
