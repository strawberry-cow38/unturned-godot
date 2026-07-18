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
        // C2 (CLIENT_PREDICTION_PLAN §4.2, Version 5): the shell's post-move position for THIS input's
        // tick -- the direct analogue of retail's WalkingPlayerInputPacket.clientPosition ("Resulting
        // transform.position immediately after movement.simulate was called", U3 PlayerInput.cs:854-857,
        // captured :1607, on the wire :867-873). The server's ack band compares it against the avatar's
        // own result and ADOPTS a sub-band claim (retail's sub-2cm ack, :1820-1838) so healthy two-solve
        // skew resolves server-ward -- invisibly -- instead of as client-visible correction traffic.
        // Rides the same position grid as the snapshot (PlayerReplication.Quantize), so an adopted claim
        // acks back as EXACTLY the client's recorded prediction.
        public Vector3 ClaimedPos;
        // On the wire as one bit: claimless senders (headless demo walkers whose flat prediction doesn't
        // track a real-physics avatar) must not have a fabricated (0,0,0) "claim" adopted. Also cleared
        // at consume time on the synthesized ticks (hole-substitution / hold) whose held input carries a
        // claim belonging to a DIFFERENT seq.
        public bool HasClaim;

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
            // C2 (Version 5): hasClaim:1, then the claimed post-move position on the snapshot's exact
            // position grid -- only when the sender actually captured one
            w.WriteBits(HasClaim ? 1u : 0u, 1);
            if (HasClaim)
            {
                w.WriteClampedFloat(ClaimedPos.x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
                w.WriteClampedFloat(ClaimedPos.y, NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits);
                w.WriteClampedFloat(ClaimedPos.z, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);
            }
        }

        public static bool TryRead(NetPakReader r, out MoveInput cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float mx)) return false;
            if (!r.ReadSignedNormalizedFloat(8, out float my)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadUInt8(out byte buttons)) return false;
            if (!r.ReadBits(1, out uint hasClaim)) return false;
            float cx = 0f, cy = 0f, cz = 0f;
            if (hasClaim != 0)
            {
                if (!r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out cx)) return false;
                if (!r.ReadClampedFloat(NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits, out cy)) return false;
                if (!r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out cz)) return false;
            }
            cmd = new MoveInput { Seq = seq, MoveX = mx, MoveY = my, YawDegrees = yaw, Buttons = buttons,
                                  ClaimedPos = new Vector3(cx, cy, cz), HasClaim = hasClaim != 0 };
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
            // -- the mp-inputbuffer fix: the per-peer in-order MoveInput queue (real Unturned's
            //    serversidePackets, PlayerInput.cs:1054) the C2 avatar driver consumes ONE input per tick
            //    from (TryConsumeInput), so the server integrates the same input stream, in the same order
            //    and count, the client predicted. CurrentInput above stays the latest-RECEIVED (the
            //    ServerStep held-keys demo path reads it); AppliedInput is the latest CONSUMED -- what the
            //    avatar coasts on when the queue starves.
            internal Queue<MoveInput> PendingInputs;
            internal MoveInput AppliedInput;
            internal bool HasApplied;
            internal ushort LastConsumedSeq;
            internal byte PrimeWait;
            internal bool Primed;
            // consecutive starved-coast ticks (reset by any motion consume; at MaxCoastTicks the coast
            // becomes a HOLD), and how many of those coasts are integrations the client may ALSO have
            // predicted -- a delayed (not lost) input arriving later is consumed ack-only against this
            // debt so its tick is never integrated twice
            internal int CoastTicks;
            internal int CoastDebt;
            // true once ServerDrive has taken over this entity: an in-process shell (the listen-server /
            // SP-loopback local player, MP_PLAN §4 Phase 4) steps the REAL sim-core + physics and writes
            // the result here; the internal flat-ground integration must not fight it.
            internal bool ExternallyDriven;
            // C2 anti-cheat: the remaining claim-adoption allowance (metres). Accrues at
            // AdoptBudgetMetersPerSecond up to the cap; each adoption drains the GROWTH of the
            // claim-vs-body skew since the last adoption (AdoptedSkew is that baseline).
            internal float AdoptBudget;
            internal float AdoptedSkew;
            // C2 hysteresis (the Schmitt trigger): adoption disengages past the band and re-engages only
            // once the skew has CONVERGED under AdoptReentryMeters -- see ServerTryAdoptClaim.
            internal bool AdoptEngaged;
            /// <summary>Read-only view for game-side drivers (C2 PlayerNetSync must not adopt an entity
            /// another shell already ServerDrives -- double-driving the seam would fight over it).</summary>
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
        /// flat demo integration reads this view; the C2 avatar driver consumes TryConsumeInput instead
        /// (in-order, count-preserving -- the mp-inputbuffer fix).</summary>
        public bool TryGetHeldInput(ushort ownerPlayerId, out MoveInput input)
        {
            input = default;
            if (!TryGetByOwner(ownerPlayerId, out var e) || !e.HasInput) return false;
            input = e.CurrentInput;
            return true;
        }

        // ---- the mp-inputbuffer jitter buffer (the sprint-stop yank fix) ----
        // MoveInput rides UnreliableSequenced at one per client tick; the avatar driver must integrate
        // that stream in the same order and COUNT the client predicted, or the integrated-tick counts
        // drift under jitter (two inputs in one server-tick window / a stale re-integration) and the
        // accumulated gap resolves as one hard correction the instant the player stops. Tunables:

        /// <summary>Queue depth cap: arrivals beyond this drop the OLDEST queued input (a hitch burst must
        /// bound added input latency and memory, and the freshest intent matters most). This enqueue-side
        /// cap is the ONLY place a queued input is ever dropped -- consumption never skips one (a queued
        /// input is a tick the client already predicted; discarding it to drain faster put the server one
        /// integration behind and the deficit resolved as a correction at the next stop).</summary>
        public const int MaxQueuedInputs = 8;
        /// <summary>Seq holes at most this size (dropped datagrams) substitute one coast tick per missing
        /// input, keeping the integration count aligned; bigger jumps (hitch / cap drops) adopt directly
        /// and let the reconciler absorb the difference.</summary>
        public const int MaxGapCoastTicks = 2;
        /// <summary>Starvation coast bound: an empty queue coasts the last consumed input for at most this
        /// many consecutive ticks (~240 ms, enough to ride out routine jitter gaps), then HOLDS -- zero
        /// motion until real input resumes. Uncapped, a long outage (heavy loss / stall / pre-disconnect)
        /// ghost-ran the avatar on stale "sprint forward" for as long as the outage lasted.</summary>
        public const int MaxCoastTicks = 12;
        /// <summary>Consumption starts once this many inputs are buffered (or after an equal number of
        /// ticks with anything queued, so a sparse sender is never stalled) -- the shallow standing buffer
        /// that absorbs arrival jitter of the same magnitude. Costs its depth in ticks of added input
        /// latency (~40 ms), invisible locally behind client-side prediction.</summary>
        public const int PrimeDepth = 2;

        /// <summary>Latest-wins input queue: MoveInput rides UnreliableSequenced, so a reordered stale
        /// command must never override a newer one already applied. Also appends to the in-order
        /// PendingInputs queue TryConsumeInput drains (the guard keeps queued seqs strictly increasing;
        /// the depth cap drops the oldest).</summary>
        public void ServerQueueInput(ushort ownerPlayerId, in MoveInput input)
        {
            if (!TryGetByOwner(ownerPlayerId, out var e) || e.Sim == null) return;
            if (e.HasInput && !NetSeq.IsNewer(input.Seq, e.CurrentInput.Seq)) return;
            e.CurrentInput = input;
            e.HasInput = true;
            e.PendingInputs ??= new Queue<MoveInput>();
            if (e.PendingInputs.Count >= MaxQueuedInputs) e.PendingInputs.Dequeue();
            e.PendingInputs.Enqueue(input);
        }

        /// <summary>One tick's input for the avatar driver (PlayerNetSync) -- the in-order consume that
        /// replaces reading the held latest. Integrates AT MOST ONE tick of motion per call and never
        /// skips a queued input -- real Unturned's serversidePackets model (PlayerInput.cs:1723-1734)
        /// dequeues one packet per qualifying tick and lets the buffer absorb a burst as bounded added
        /// latency; the enqueue-side MaxQueuedInputs cap is the only drop. When the queue is starved it
        /// COASTS on the last consumed input (the held-keys model's virtue on an unreliable wire: a lost
        /// datagram's axes are almost always the held ones) for at most MaxCoastTicks, then HOLDS (zero
        /// motion, stance STAND) until real input resumes. Every starved coast may be an early
        /// integration of a tick whose input was merely DELAYED, not lost -- CoastDebt remembers them,
        /// and when the delayed inputs arrive bunched, debt-many are consumed instantly ack-only (seqs
        /// claimed, no second integration), so a stall-burst leaves neither a double-integrated segment
        /// nor a standing backlog. A small seq hole substitutes one coast tick per missing input so the
        /// count stays aligned with the client's prediction. False = nothing to integrate (no input
        /// since spawn/clear) -- stand still, like TryGetHeldInput. The returned Seq is the ack the
        /// caller must pair with the produced position (during a coast/hold it repeats the last consumed
        /// seq, which the client's reconciler already treats as stale).</summary>
        public bool TryConsumeInput(ushort ownerPlayerId, out MoveInput input)
            => TryConsumeInput(ownerPlayerId, out input, out _);

        /// <summary>The full consume: seqAdvanced reports whether the returned input carries a FRESH seq
        /// (a real dequeue or a hole-substitution that claimed the lost seq) or a stale REPEAT (starved
        /// coast, hold, prime-wait). The distinction is the C1.5 phantom-pairing fix: a coast tick still
        /// integrates motion on the avatar body, but publishing that advanced position under the repeated
        /// seq re-pairs an already-acked seq with a NEWER position -- and the 25 Hz jittered snapshot
        /// stream often shows the client ONLY the phantom pairing for that seq, which measures as 1-3
        /// ticks of error that was never real (the residual high-RTT inchworm's dominant engine, found by
        /// the plan §3 WAN harness). The avatar driver must NOT ServerDrive a stale-seq tick's result --
        /// hold the entity at the last exact (pos, seq) pairing until the stream resumes, exactly like
        /// retail's never-speculate server (U3 PlayerInput.cs: no packet -> no simulate -> no new state).</summary>
        public bool TryConsumeInput(ushort ownerPlayerId, out MoveInput input, out bool seqAdvanced)
        {
            input = default;
            seqAdvanced = false;
            if (!TryGetByOwner(ownerPlayerId, out var e) || e.Sim == null) return false;
            var q = e.PendingInputs;
            if (q != null && q.Count > 0 && !e.Primed)
            {
                // fill the shallow jitter buffer before the first consume; the tick counter keeps a
                // sparse sender (a single held input) from waiting forever
                if (q.Count < PrimeDepth + 1 && ++e.PrimeWait <= PrimeDepth)
                {
                    input = e.AppliedInput;
                    return e.HasApplied;
                }
                e.Primed = true;
            }
            // repay coast debt: these queued seqs are the stall's delayed inputs and their ticks were
            // already integrated by the starved coasts -- claim them (and any lost seq's hole among
            // them) without a second integration. A jump past the substitutable window means the coasts
            // stood in for nothing recoverable: void the debt and let the adopt below handle it.
            bool repaidDequeue = false, repaidHole = false;
            while (q != null && q.Count > 0 && e.CoastDebt > 0)
            {
                int gap = (ushort)(q.Peek().Seq - e.LastConsumedSeq);
                if (gap > MaxGapCoastTicks + 1) { e.CoastDebt = 0; break; }
                if (gap > 1) { e.LastConsumedSeq++; repaidHole = true; repaidDequeue = false; }
                else { var pre = q.Dequeue(); e.LastConsumedSeq = pre.Seq; e.AppliedInput = pre; repaidDequeue = true; repaidHole = false; }
                e.CoastDebt--;
            }
            // C1.6 (the §3 harness's second find): when repayment drains the queue EMPTY, the last
            // repaid input doubles as THIS tick's consume. Without this, a multi-tick starve (a latency
            // step, a burst) parked the driver in a PERMANENT stale regime: every later tick's single
            // arrival was repaid ack-only, the consume then re-starved (debt re-armed, seq repeat), so
            // write-backs stayed suppressed for whole seconds and the owner's corrections arrived as
            // batched 0.2-0.4 m lumps instead of a drizzle. The integrated axes are IDENTICAL either way
            // (the starved coast would have integrated this very input); what changes is bookkeeping --
            // the seq advances (exact pairing, write-back resumes) and the debt actually drains.
            if ((repaidDequeue || repaidHole) && q.Count == 0)
            {
                input = e.AppliedInput;
                if (repaidHole) { input.Seq = e.LastConsumedSeq; input.HasClaim = false; }
                e.CoastTicks = 0;
                seqAdvanced = true;
                return true;
            }
            if (q == null || q.Count == 0)
            {
                if (!e.HasApplied) return false;   // nothing consumed since spawn/clear: stand still
                input = e.AppliedInput;            // stale seq: the repeated ack is ignored client-side
                if (e.CoastTicks >= MaxCoastTicks)
                {
                    // past the jitter-gap budget this is an outage, not a gap: hold still instead of
                    // ghost-running stale intent (motion zeroed, yaw kept, no fresh ack emitted)
                    StripMotion(ref input);
                    return true;
                }
                e.CoastTicks++;
                if (e.CoastDebt < MaxCoastTicks) e.CoastDebt++;
                return true;
            }
            int dist = (ushort)(q.Peek().Seq - e.LastConsumedSeq);
            if (e.HasApplied && dist > 1 && dist <= MaxGapCoastTicks + 1)
            {
                // a dropped datagram's tick: coast in its place so the count stays aligned -- and CLAIM
                // the hole's seq (the client predicted and recorded it; the sequenced channel can never
                // deliver it late once a newer seq got through). Pairing the coast tick's position with
                // the substituted seq keeps every published (pos, seq) ack exact -- returning the stale
                // seq here let the 25 Hz snapshot sampler pair coast-advanced positions with an
                // already-predicted seq, a phantom one-tick correction at every substitution.
                e.LastConsumedSeq++;
                e.AppliedInput.Seq = e.LastConsumedSeq;
                input = e.AppliedInput;
                input.HasClaim = false;   // the held input's ClaimedPos belongs to ITS seq, not the substituted one
                seqAdvanced = true;       // the claimed hole seq is fresh -- its (pos, seq) pairing is exact
                return true;
            }
            var inp = q.Dequeue();
            e.LastConsumedSeq = inp.Seq;
            e.AppliedInput = inp;
            e.HasApplied = true;
            e.CoastTicks = 0;
            input = inp;
            seqAdvanced = true;
            return true;
        }

        /// <summary>Zero a returned input's motion (axes, jump, stance -> STAND) while keeping seq and
        /// yaw: a hold tick -- the avatar stands (facing still tracks) and no fresh ack is emitted.</summary>
        static void StripMotion(ref MoveInput input)
        {
            input.MoveX = 0f;
            input.MoveY = 0f;
            input.Buttons = 0;
            input.HasClaim = false;   // the stale claim must not be adopted onto a hold tick
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

        // ---- C2: the server ack band (CLIENT_PREDICTION_PLAN §4.2, retail's model adapted) ----
        // Retail re-simulates the client's inputs and ACKS any result within errorToleranceDistance =
        // 0.02 m of the claimed clientPosition -- below tolerance the client keeps its position and the
        // skew is simply tolerated (U3 PlayerInput.cs:1820-1838). The port's adoption is ENTITY-ONLY:
        // a sub-band claim becomes the PUBLISHED state (so the ack the owner receives is exactly its
        // recorded prediction -> zero correction, and observers render the owner's own view of itself),
        // while the avatar BODY keeps its own untainted physics path -- the body is never steered by a
        // claim. The first cut nudged the body onto the claim and the §3 WAN harness caught the flaw:
        // two delayed controllers chasing each other (the body adopting RTT-stale claims while the owner
        // eased toward the body's RTT-stale acks) is an oscillator -- the sprint baseline got WORSE
        // (2.7 -> 18 m/min). Body-sovereign adoption has no feedback path: claims can never move server
        // physics, so the worst a lying claim can ever do is place the PUBLISHED position AckBandMeters
        // from the true body.

        /// <summary>Adoption band: a claim within this of the avatar body's own result is publishable,
        /// and therefore also the hard ceiling on the standing published-vs-true skew a client can hold.
        /// 2x the client dead-zone (0.04) and 4x retail's 2 cm -- we carry two-distinct-physics-solves
        /// noise retail's re-simulation doesn't. Tuned on the §3 WAN harness.</summary>
        public const float AckBandMeters = 0.08f;
        /// <summary>Anti-cheat ramp bound (the part retail gets implicitly from re-simulating the
        /// inputs: a cheater can skew at most 2 cm per 80 ms packet ~= 0.25 m/s). The budget drains on
        /// the GROWTH of the claim-vs-body skew, so it meters how fast the published lie can ramp --
        /// while the band caps how big it can ever stand. A steady healthy skew (the same few mm every
        /// tick) drains ~nothing, so legitimate tracking never duty-cycles.</summary>
        public const float AdoptBudgetMetersPerSecond = 0.5f;
        /// <summary>Budget accrual cap: bounds the burst a long-quiet player can bank (just under 2x the
        /// band -- an occasional full-band adoption stays possible, a teleport never).</summary>
        public const float AdoptBudgetCapMeters = 0.15f;
        /// <summary>The hysteresis re-entry threshold (the Schmitt trigger's lower edge). After a real
        /// over-band divergence the published frame is the BODY's, and the client eases toward it; if
        /// adoption re-engaged the moment the skew dipped back under the band, the ack frame would flip
        /// to the client's own (RTT-stale) claims 8 cm away and UNDO the correction -- a permanent limit
        /// cycle at the band edge (the §3 sprint baseline measured it: 19 m/min of oscillation).
        /// Re-engaging only once the skew has CONVERGED under this -- safely inside the client's 0.04
        /// dead-zone -- makes the frame flip invisible: below the dead-zone NEITHER frame corrects.</summary>
        public const float AdoptReentryMeters = 0.03f;

        /// <summary>The C2 ack-band decision for one write-back: claimedPos is the client's post-move
        /// position for the input the avatar just integrated (MoveInput.ClaimedPos), serverPos the
        /// avatar body's own result. Engaged and within band AND skew-growth budget: returns the
        /// wire-quantized claim; the caller publishes it (ServerDrive) INSTEAD of the body position --
        /// the body itself is never moved. Beyond the band: disengages -- the body position is published
        /// and the client corrects, exactly as before C2, until the skew converges under
        /// AdoptReentryMeters (the hysteresis above). Engine-free so the band/budget/hysteresis policy
        /// is L0-testable; claims arrive wire-quantized, so NaN/extent sanity is structural (quantized
        /// ints), not a gate.</summary>
        public bool ServerTryAdoptClaim(ushort ownerPlayerId, Vector3 claimedPos, Vector3 serverPos, float dt, out Vector3 adoptedPos)
        {
            adoptedPos = default;
            if (!TryGetByOwner(ownerPlayerId, out var e)) return false;
            e.AdoptBudget = System.Math.Min(AdoptBudgetCapMeters, e.AdoptBudget + AdoptBudgetMetersPerSecond * dt);
            var claim = Quantize(claimedPos);
            float dist = (claim - serverPos).magnitude;
            if (dist > AckBandMeters)                 // real divergence: publish the body's truth, the client corrects
            {
                e.AdoptEngaged = false;               // ... and stay on the body's frame until CONVERGED (hysteresis)
                return false;
            }
            if (!e.AdoptEngaged)
            {
                if (dist > AdoptReentryMeters) return false;   // still converging: keep the frames from flipping mid-correction
                e.AdoptEngaged = true;
                e.AdoptedSkew = dist;                 // fresh engagement: the entry skew is the new growth baseline
            }
            float growth = System.Math.Max(0f, dist - e.AdoptedSkew);
            if (growth > e.AdoptBudget) return false; // the lie is ramping faster than the allowance: the body's truth is published
            e.AdoptBudget -= growth;
            e.AdoptedSkew = dist;
            adoptedPos = claim;
            return true;
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
        /// last MoveInput; fresh inputs are rejected at the dispatch gate until respawn). Also drains the
        /// in-order queue and coast state -- stale queued walk intents must not keep an avatar moving
        /// after death/vehicle-enter -- and re-arms the jitter-buffer prime for the resumed stream.</summary>
        public void ServerClearInput(ushort ownerPlayerId)
        {
            if (!TryGetByOwner(ownerPlayerId, out var e)) return;
            e.HasInput = false;
            e.HasApplied = false;
            e.PendingInputs?.Clear();
            e.Primed = false;
            e.PrimeWait = 0;
            e.CoastTicks = 0;
            e.CoastDebt = 0;
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
