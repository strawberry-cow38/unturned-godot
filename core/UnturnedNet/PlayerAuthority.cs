using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// On-foot client authority (mp-clientauth-foot, wire Version 9) -- the Part A vehicle model applied
    /// to the walking player, ending the two-body movement fork at the root: the OWNER's client runs the
    /// only physics body its movement has (the SP-quality shell, unchanged) and STREAMS its transform;
    /// the server envelope-validates each claim and ADOPTS it through the existing ServerDrive seam
    /// (publish + mark externally-driven -- ServerStep/PlayerNetSync never integrate an owner again).
    /// The server keeps the ENVELOPE, not the centimetre: gross cheats (speed/fly/teleport) roll the
    /// client back via PlayerRecovEvent exactly like VehicleRecovEvent rolls a driver back; anything
    /// inside the envelope is the client's word, which is the retail posture for driving and this
    /// co-op port's chosen posture for walking (CLIENT_PREDICTION_PLAN §5.1's carve-out, widened).
    /// The predict/reconcile + rewind/replay stack this replaces (C1-C3) is deleted, not bypassed.
    /// </summary>
    public struct PlayerStateCommand
    {
        public ushort Seq;         // client-local, monotonically increasing (wrap via NetSeq); latest-wins
        public byte RecovAck;      // echo of the last PlayerRecovEvent counter received (0 = none yet)
        public Vector3 Pos;        // the shell's post-move position, on the snapshot's exact position grid
        public float YawDegrees;   // facing, wrapped [0,360) by the wire encoding
        public float PitchDegrees; // look pitch (dressing: future head-aim/anim for observers; not validated)
        public Vector3 LinVel;     // movement-sim velocity (recov re-seed + observer dressing; NetWire clamp +-64 m/s)
        public byte Buttons;       // MoveInput encoding: bit 0 = jump (effect dressing), bits 1-2 = stance
                                   // (drives the server body's hitbox capsule + zombie stealth radius)
        public bool Grounded;      // the shell's deterministic grounded flag (diagnostics/future envelope refinement)

        public bool Jump => (Buttons & MoveInput.ButtonJump) != 0;
        public EPlayerStance Stance => new MoveInput { Buttons = Buttons }.Stance;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt16(Seq);
            w.WriteUInt8(RecovAck);
            NetWire.WritePos(w, Pos);
            w.WriteDegrees(YawDegrees, NetQuantization.YawBits);
            w.WriteDegrees(PitchDegrees, NetQuantization.PitchBits);
            NetWire.WriteVel(w, LinVel);
            w.WriteUInt8(Buttons);
            w.WriteBit(Grounded);
        }

        public static bool TryRead(NetPakReader r, out PlayerStateCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort seq)) return false;
            if (!r.ReadUInt8(out byte recovAck)) return false;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!r.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            if (!r.ReadDegrees(out float pitch, NetQuantization.PitchBits)) return false;
            if (!NetWire.ReadVel(r, out Vector3 vel)) return false;
            if (!r.ReadUInt8(out byte buttons)) return false;
            if (!r.ReadBit(out bool grounded)) return false;
            cmd = new PlayerStateCommand
            {
                Seq = seq, RecovAck = recovAck, Pos = pos, YawDegrees = yaw, PitchDegrees = pitch,
                LinVel = vel, Buttons = buttons, Grounded = grounded,
            };
            return true;
        }
    }

    /// <summary>
    /// The server's rollback of an out-of-envelope walking claim -- the on-foot VehicleRecovEvent.
    /// ReliableOrdered, unicast to the owner: the last-GOOD published position + the last adopted sim
    /// velocity + the incremented counter. The client teleports its shell there (TeleportTo -- interp
    /// snapshots reset), re-seeds the sim velocity, and echoes RecovCounter on its next state send;
    /// the server discards claims whose ack lags (the freeze-until-echo wait). Facing deliberately
    /// does NOT ride: rolling back a mouse-look would fight the player's hand for zero anti-cheat gain.
    /// </summary>
    public struct PlayerRecovEvent
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public byte RecovCounter;

        public void Write(NetPakWriter w)
        {
            NetWire.WritePos(w, Pos);
            NetWire.WriteVel(w, Vel);
            w.WriteUInt8(RecovCounter);
        }

        public static bool TryRead(NetPakReader r, out PlayerRecovEvent evt)
        {
            evt = default;
            if (!NetWire.ReadPos(r, out Vector3 pos)) return false;
            if (!NetWire.ReadVel(r, out Vector3 vel)) return false;
            if (!r.ReadUInt8(out byte counter)) return false;
            evt = new PlayerRecovEvent { Pos = pos, Vel = vel, RecovCounter = counter };
            return true;
        }
    }

    /// <summary>
    /// Server-side on-foot authority arbitration (the ServerVehicles shape, engine-free): registers
    /// CommandPlayerState on the §2.3 choke point, envelope-validates each claim against the entity's
    /// last published position, and adopts or recovs. All constants spec-derived, never tuned magic.
    /// </summary>
    public sealed class ServerPlayerAuthority
    {
        // ---- the on-foot plausibility envelope ----
        /// <summary>Horizontal base speed: the fastest on-foot stance (PlayerMovementDef.SPEED_SPRINT,
        /// 7 m/s -- the server never trusts the CLAIMED stance for validation, so the cap is the max).</summary>
        public const float MaxSpeedMps = PlayerMovementDef.SPEED_SPRINT;
        /// <summary>The retail 25 % slack (the Part A vehicle envelope's EnvelopeSlack, from U3
        /// VehicleAsset.cs:2319-2333) -- what keeps legit high-ping + jitter from EVER false-tripping.</summary>
        public const float EnvelopeSlack = 1.25f;
        /// <summary>Vertical climb cap: jump takeoff is 7 m/s (PlayerMovementDef.JUMP), but the shell's
        /// binary StepUp pops +0.5 m (PlayerController.StepHeight) inside one tick, which measured over
        /// the min-clamped 2-tick window is 12.5 m/s apparent climb -- x the 1.25 slack = 15.6, so 16.
        /// Statelessly bounds ASCENT rate; sustained-hover is out of envelope scope, same as the vehicle
        /// envelope (MP_PLAN §7 revisit item for untrusted hosting).</summary>
        public const float ValidSpeedUp = 16f;
        /// <summary>Fall cap: |PlayerMovementDef.TERMINAL_VELOCITY| = 100 m/s is a LEGAL sustained fall
        /// speed for a player (unlike retail's 25 m/s car default) -- cap at terminal + 10 %.</summary>
        public const float ValidSpeedDown = 110f;
        /// <summary>Elapsed-tick clamp floor: the state stream is per-tick, but a burst delivered inside
        /// one server tick must not shrink the cap to zero (the ServerVehicles rule).</summary>
        public const int EnvelopeMinTicks = 2;
        /// <summary>Elapsed clamp ceiling: 1 s (the vehicle envelope uses 0.5 s; on-foot base speed is low
        /// enough that the resulting blink-teleport bound -- 7 x 1.0 x 1.25 = 8.75 m -- stays under even a
        /// slow car's half-second bound, while a full second of REAL network hitch at sprint (7 m moved)
        /// resumes without a rubber-band). A longer real stall rolls the walker back via recov: safe,
        /// just abrupt. Fabricated silence banks nothing beyond MaxSpeed x Slack sustained.</summary>
        public const int EnvelopeMaxTicks = 50;

        /// <summary>Test seam (the DisableReplay pattern): with the envelope off every claim adopts
        /// verbatim -- the teeth proof that the envelope, not luck, is what rejects the cheats.</summary>
        public bool DisableEnvelope;

        /// <summary>The raw adopted claim beyond what the entity itself stores -- the game-side follower
        /// body (PlayerNetSync) dresses stance/pitch/velocity from here.</summary>
        public struct DrivenPlayerState
        {
            public Vector3 Pos;
            public float YawDegrees, PitchDegrees;
            public Vector3 Vel;
            public byte Buttons;
            public bool Grounded;
            public bool Jump => (Buttons & MoveInput.ButtonJump) != 0;
            public EPlayerStance Stance => new MoveInput { Buttons = Buttons }.Stance;
        }

        sealed class DrivenState
        {
            public bool HasSeq; public ushort LastSeq;
            public long LastAcceptedTick;          // envelope interval baseline
            public byte RecovCounter;              // increments per violation (the retail input.recov shape)
            public bool Recovering;                // discard claims until RecovAck echoes the counter
            public bool HasAdopted;
            public DrivenPlayerState Adopted;
        }
        readonly Dictionary<ushort, DrivenState> _driven = new Dictionary<ushort, DrivenState>();

        readonly PlayerReplication _players;
        readonly PlayerCombatReplication _combat;
        readonly Func<ushort, bool> _isSeated;     // VehicleHost.IsDriver: the seat teleport owns a driver's entity (§3.6)
        readonly Func<long> _tick;
        readonly Action<ushort, byte[]> _sendTo;   // recov is an owner-unicast reliable event

        public ServerPlayerAuthority(PlayerReplication players, PlayerCombatReplication combat,
                                     Func<ushort, bool> isSeated, Func<long> tick, Action<ushort, byte[]> sendTo)
        {
            _players = players; _combat = combat; _isSeated = isSeated; _tick = tick; _sendTo = sendTo;
        }

        public void Register(CommandRegistry commands)
        {
            // sender identity from the CONNECTION, never the payload (§2.3): a corpse's and a seated
            // driver's walk claims drop at the choke point, exactly like CommandMoveInput's gate
            commands.Register<PlayerStateCommand>(ReplicationIds.CommandPlayerState, PlayerStateCommand.TryRead,
                (sender, cmd) => OnPlayerState(sender, cmd),
                validate: (sender, cmd) => _combat.IsAlive(sender) && !_isSeated(sender));
        }

        /// <summary>True once this owner's stream has had a claim ACCEPTED -- the entity is client-driven
        /// (ExternallyDriven via ServerDrive) and the follower body tracks it.</summary>
        public bool IsClientDriven(ushort playerId)
            => _driven.TryGetValue(playerId, out var st) && st.HasAdopted;

        public bool TryGetDrivenState(ushort playerId, out DrivenPlayerState state)
        {
            if (_driven.TryGetValue(playerId, out var st) && st.HasAdopted)
            {
                state = st.Adopted;
                return true;
            }
            state = default;
            return false;
        }

        /// <summary>The adopt-or-recov pipeline (the ServerVehicles.OnVehicleState shape): seq
        /// latest-wins -> recov ack gate -> the plausibility envelope -> ServerDrive (publish + mark
        /// externally-driven) or roll the owner back. NaN/extent sanity is structural: every field
        /// decodes from bounded ClampedFloat/Degrees bit-fields. Runs at command-dispatch time inside
        /// TickSimulation.</summary>
        void OnPlayerState(ushort sender, PlayerStateCommand cmd)
        {
            if (!_players.TryGetByOwner(sender, out var e)) return;
            long tick = _tick();

            if (!_driven.TryGetValue(sender, out var st))
                // first claim ever: the interval baseline opens at the max window -- the entity sits at
                // the server spawn and the shell spawned there too, so the first delta is ~zero anyway
                _driven[sender] = st = new DrivenState { LastAcceptedTick = tick - EnvelopeMaxTicks };

            // latest-wins by Seq (UnreliableSequenced dedups per datagram, but a fragment/burst boundary
            // can still deliver two commands out of order)
            if (st.HasSeq && !NetSeq.IsNewer(cmd.Seq, st.LastSeq)) return;
            st.HasSeq = true; st.LastSeq = cmd.Seq;

            // recov ack wait: while recovering, discard every claim whose RecovAck lags the counter --
            // the client is still walking a rolled-back-in-flight position. The event rides
            // ReliableOrdered, so the echo WILL come; the claim that carries it resumes below.
            if (st.Recovering)
            {
                if (cmd.RecovAck != st.RecovCounter) return;
                st.Recovering = false;
            }

            // ---- the envelope, against the entity's last PUBLISHED position (quantized, like the claim) ----
            float dt = Math.Clamp(tick - st.LastAcceptedTick, EnvelopeMinTicks, EnvelopeMaxTicks) * (float)SimClock.FixedDelta;
            bool violation = false;
            if (!DisableEnvelope)
            {
                float dx = cmd.Pos.x - e.Pos.x, dz = cmd.Pos.z - e.Pos.z;
                float cap = MaxSpeedMps * dt * EnvelopeSlack;
                violation = dx * dx + dz * dz > cap * cap;
                if (!violation)
                {
                    float dy = cmd.Pos.y - e.Pos.y;
                    float validSpeed = dy > 0f ? ValidSpeedUp : ValidSpeedDown;
                    violation = Math.Abs(dy) / dt > validSpeed;
                }
            }

            if (violation)
            {
                st.RecovCounter++;
                st.Recovering = true;
                if (NetLog.Enabled)
                {
                    float dxl = cmd.Pos.x - e.Pos.x, dzl = cmd.Pos.z - e.Pos.z;
                    NetLog.Sink($"[NET] player {sender}: walk claim out of envelope (d {Math.Sqrt(dxl * dxl + dzl * dzl):0.0} m horiz / dy {cmd.Pos.y - e.Pos.y:0.0} m in {dt:0.00} s) -> recov #{st.RecovCounter}");
                }
                var evt = new PlayerRecovEvent
                {
                    Pos = e.Pos,
                    Vel = st.HasAdopted ? st.Adopted.Vel : Vector3.zero,
                    RecovCounter = st.RecovCounter,
                };
                _sendTo?.Invoke(sender, NetMessagePak.Pack(ReplicationIds.EventPlayerRecov, evt.Write));
                return;   // the entity keeps the last-good -- observers never see the violating claim
            }

            // ---- adopt: the owner's report becomes the entity's truth ----
            st.LastAcceptedTick = tick;
            st.HasAdopted = true;
            st.Adopted = new DrivenPlayerState
            {
                Pos = cmd.Pos, YawDegrees = cmd.YawDegrees, PitchDegrees = cmd.PitchDegrees,
                Vel = cmd.LinVel, Buttons = cmd.Buttons, Grounded = cmd.Grounded,
            };
            _players.ServerDrive(sender, cmd.Pos, cmd.YawDegrees, cmd.Seq, tick);
        }

        /// <summary>A leaving peer's authority window dies with it -- a recycled playerId starts clean.</summary>
        public void OnPeerDisconnected(ushort playerId) => _driven.Remove(playerId);
    }
}
