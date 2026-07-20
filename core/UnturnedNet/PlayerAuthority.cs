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

        // mp-event-coalesce (wire v10): a REDUNDANT list of recent combat events (Fire/Melee/Grenade/
        // Reload) the client keeps re-including every tick until the server ACKs them (see AckCombat). The
        // server dedups by a strictly-increasing combat seq, so a single dropped state datagram no longer
        // stalls combat (no reliable-ordered head-of-line block) and no event is double-processed. On send
        // Events points at the client's pending ring (only the first EventCount entries are valid); on read
        // it is a freshly-allocated array sized to EventCount.
        public CarriedCombatEvent[] Events;
        public byte EventCount;

        /// <summary>Wire cap on the redundant carry -- also the client ring depth. 16 events x ~14 B = ~224 B,
        /// well under the 1200 B datagram budget on top of the ~14 B base transform packet. A count byte
        /// above this on read = malformed = reject.</summary>
        public const int MaxCarriedEvents = 16;

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
            // v10: the redundant combat-event carry, oldest-first (see CarriedCombatEvent). EventCount is
            // bounded by MaxCarriedEvents at the fill site; Events holds at least that many valid entries.
            w.WriteUInt8(EventCount);
            for (int i = 0; i < EventCount; i++) Events[i].Write(w);
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
            if (!r.ReadUInt8(out byte eventCount)) return false;
            if (eventCount > MaxCarriedEvents) return false;   // malformed carry -> reject the whole claim
            CarriedCombatEvent[] events = null;
            if (eventCount > 0)
            {
                events = new CarriedCombatEvent[eventCount];
                for (int i = 0; i < eventCount; i++)
                    if (!CarriedCombatEvent.TryRead(r, out events[i])) return false;
            }
            cmd = new PlayerStateCommand
            {
                Seq = seq, RecovAck = recovAck, Pos = pos, YawDegrees = yaw, PitchDegrees = pitch,
                LinVel = vel, Buttons = buttons, Grounded = grounded,
                Events = events, EventCount = eventCount,
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
        // ---- the on-foot plausibility envelope: a per-axis leaky allowance (token bucket) ----
        // The Part A vehicle envelope caps each packet by speed x SERVER-ARRIVAL elapsed time. That
        // formula false-trips walkers: WAN jitter COMPRESSES arrivals -- claims spanning ~5 client
        // ticks routinely land 1-2 server ticks apart (the sequenced channel drops the overtaken ones)
        // -- so 5 ticks of legit sprint motion measured against 2 ticks of arrival time reads as a
        // violation (the WAN courses measured 2-11 false recovs each under the per-packet form). The
        // bucket separates the two bounds honestly:
        //   SUSTAINED rate -- allowance accrues per SERVER tick (uninflatable real time; a cheater
        //                     cannot mint it with seq games) at max-speed x the retail 1.25 slack;
        //   BURST size     -- on every ACCEPT the leftover bank is pinned down to a small jitter
        //                     RESERVE (arrival compression never exceeds it), and the bank grows
        //                     toward the 1 s CEILING only across real arrival silence -- so a network
        //                     hitch's spanning claim is covered, while a cheater with a FLOWING claim
        //                     stream can never blink farther than the reserve.
        // A real stall past the ceiling still rolls the walker back via recov: safe, just abrupt.
        // Sustained-hover stays out of envelope scope, same as the vehicle envelope (MP_PLAN §7).

        /// <summary>Horizontal base speed: the fastest on-foot stance (PlayerMovementDef.SPEED_SPRINT,
        /// 7 m/s -- the server never trusts the CLAIMED stance for validation, so the cap is the max).</summary>
        public const float MaxSpeedMps = PlayerMovementDef.SPEED_SPRINT;
        /// <summary>The retail 25 % slack (U3 VehicleAsset.cs:2319-2333, the Part A constant).</summary>
        public const float EnvelopeSlack = 1.25f;
        /// <summary>Horizontal accrual: sprint x slack per second of server time = the sustained ceiling
        /// a cheater can never exceed.</summary>
        public const float HorizontalRate = MaxSpeedMps * EnvelopeSlack;          // 8.75 m/s
        /// <summary>Horizontal jitter reserve: ~10 ticks (0.2 s) of sprint-rate compression headroom --
        /// twice the WAN profile's worst arrival bunching -- and the standing blink bound while claims
        /// are flowing.</summary>
        public const float HorizontalReserve = 1.75f;
        /// <summary>Horizontal bank ceiling: 1 s of real silence (a full-sprint 1 s hitch resumes
        /// without a rubber-band; the post-silence blink bound stays under even a slow car's Part A
        /// half-second bound).</summary>
        public const float HorizontalCeiling = HorizontalRate * 1f;               // 8.75 m
        /// <summary>Vertical climb accrual: jump takeoff is 7 (PlayerMovementDef.JUMP), the binary
        /// StepUp pops +0.5 m in one tick, and sprinting the steepest walkable slope climbs ~10 m/s --
        /// 16 covers all sustained legit ascent with slack.</summary>
        public const float UpRate = 16f;
        /// <summary>Climb reserve: one 0.5 m step pop + a compressed jump-arc arrival window + margin.
        /// The instant-fly blink bound while claims are flowing.</summary>
        public const float UpReserve = 2f;
        public const float UpCeiling = UpRate * 1f;
        /// <summary>Fall accrual: |PlayerMovementDef.TERMINAL_VELOCITY| = 100 m/s is a LEGAL sustained
        /// fall speed (unlike retail's 25 m/s car default) -- terminal + 10 %.</summary>
        public const float DownRate = 110f;
        /// <summary>Fall reserve: a compressed terminal-fall arrival window (0.2 s at terminal).</summary>
        public const float DownReserve = 22f;
        public const float DownCeiling = DownRate * 1f;

        /// <summary>Test seam (the DisableReplay pattern): with the envelope off every claim adopts
        /// verbatim -- the teeth proof that the envelope, not luck, is what rejects the cheats.</summary>
        public bool DisableEnvelope;

        /// <summary>mp-event-coalesce (v10): the sink for each deduped carried combat event -- wired in
        /// NetWorldHost to route Kind -> ServerCombat.OnFire/OnMelee/OnGrenade/OnReload (ServerCombat lives
        /// on a different object, so the authority can't call it directly). (sender, event, tick).</summary>
        public Action<ushort, CarriedCombatEvent, long> CombatDispatch;

        /// <summary>Test seam (the DisableEnvelope pattern): with the dedup off, every carried event is
        /// dispatched on every delivery -- the teeth proof that the strictly-increasing combat-seq guard,
        /// not incidental fire-rate/cooldown timing, is what makes the redundant carry idempotent.</summary>
        public bool DisableCombatDedup;

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
            public long LastAccrueTick;            // server tick the banks last accrued at
            public float HBank, UpBank, DownBank;  // the per-axis allowance banks (metres)
            public byte RecovCounter;              // increments per violation (the retail input.recov shape)
            public bool Recovering;                // discard claims until RecovAck echoes the counter
            public bool HasAdopted;
            public DrivenPlayerState Adopted;
            // mp-event-coalesce (v10): the strictly-increasing combat-seq guard that dedups the redundant
            // combat-event carry (the ServerQueueInput shape). Resets clean with the DrivenState on recycle.
            public bool HasCombatSeq; public ushort LastCombatSeq;
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
                // first claim ever: the banks open at their ceilings (join forgiveness) -- the entity
                // sits at the server spawn and the shell spawned there too, so the first delta is ~zero
                _driven[sender] = st = new DrivenState
                {
                    LastAccrueTick = tick,
                    HBank = HorizontalCeiling, UpBank = UpCeiling, DownBank = DownCeiling,
                };

            // latest-wins by Seq (UnreliableSequenced dedups per datagram, but a fragment/burst boundary
            // can still deliver two commands out of order)
            if (st.HasSeq && !NetSeq.IsNewer(cmd.Seq, st.LastSeq)) return;
            st.HasSeq = true; st.LastSeq = cmd.Seq;

            // ---- mp-event-coalesce (v10): apply the redundant combat carry BEFORE the recov-ack gate and
            // the envelope, so a player who momentarily trips the movement envelope still legitimately fired
            // their gun -- combat is not gated on the movement claim's outcome. Events arrive oldest-first;
            // the strictly-increasing combat-seq guard (the ServerQueueInput shape) makes re-delivery
            // idempotent, so a dropped state datagram costs nothing and no event is ever double-processed.
            // The ack (highest applied seq) rides the next snapshot via SetCombatAck; the owner's client
            // then drops those events from its pending ring. ----
            for (int i = 0; i < cmd.EventCount; i++)
            {
                var ev = cmd.Events[i];
                bool dispatch = DisableCombatDedup || !st.HasCombatSeq || NetSeq.IsNewer(ev.Seq, st.LastCombatSeq);
                if (dispatch) CombatDispatch?.Invoke(sender, ev, tick);
                // advance the ack monotonically regardless (never regress it, even with dedup disabled)
                if (!st.HasCombatSeq || NetSeq.IsNewer(ev.Seq, st.LastCombatSeq))
                {
                    st.HasCombatSeq = true;
                    st.LastCombatSeq = ev.Seq;
                }
            }
            if (st.HasCombatSeq) _players.SetCombatAck(sender, st.LastCombatSeq, tick);

            // recov ack wait: while recovering, discard every claim whose RecovAck lags the counter --
            // the client is still walking a rolled-back-in-flight position. The event rides
            // ReliableOrdered, so the echo WILL come; the claim that carries it resumes below.
            if (st.Recovering)
            {
                if (cmd.RecovAck != st.RecovCounter) return;
                st.Recovering = false;
            }

            // ---- the envelope: at each NEW arrival tick, pin the leftover banks down to the jitter
            // reserve (a flowing stream never accumulates blink allowance), then accrue the real server
            // time since the last arrival (silence banks, up to the ceiling). Claims landing in the SAME
            // server tick share that tick's bank un-pinned -- a queued hitch backlog is N small claims
            // bursting in one tick and must spend the silence they represent, not be pinned apart by
            // their first sibling. Then measure the claim's delta against the entity's last PUBLISHED
            // position (quantized, like the claim). ----
            if (tick > st.LastAccrueTick)
            {
                float accrue = (tick - st.LastAccrueTick) * (float)SimClock.FixedDelta;
                st.LastAccrueTick = tick;
                st.HBank = Math.Min(HorizontalCeiling, Math.Min(st.HBank, HorizontalReserve) + HorizontalRate * accrue);
                st.UpBank = Math.Min(UpCeiling, Math.Min(st.UpBank, UpReserve) + UpRate * accrue);
                st.DownBank = Math.Min(DownCeiling, Math.Min(st.DownBank, DownReserve) + DownRate * accrue);
            }

            float dx = cmd.Pos.x - e.Pos.x, dz = cmd.Pos.z - e.Pos.z;
            float dist = (float)Math.Sqrt(dx * dx + dz * dz);
            float dy = cmd.Pos.y - e.Pos.y;
            bool violation = !DisableEnvelope
                && (dist > st.HBank || (dy > 0f ? dy > st.UpBank : -dy > st.DownBank));

            if (violation)
            {
                st.RecovCounter++;
                st.Recovering = true;
                if (NetLog.Enabled)
                    NetLog.Sink($"[NET] player {sender}: walk claim out of envelope (d {dist:0.0} m horiz vs bank {st.HBank:0.0} / dy {dy:0.0} m vs {(dy > 0f ? st.UpBank : st.DownBank):0.0}) -> recov #{st.RecovCounter}");
                var evt = new PlayerRecovEvent
                {
                    Pos = e.Pos,
                    Vel = st.HasAdopted ? st.Adopted.Vel : Vector3.zero,
                    RecovCounter = st.RecovCounter,
                };
                _sendTo?.Invoke(sender, NetMessagePak.Pack(ReplicationIds.EventPlayerRecov, evt.Write));
                return;   // the entity keeps the last-good -- observers never see the violating claim; banks undrained
            }

            // ---- adopt: drain the spent motion (the reserve pin happens at the next arrival tick) ----
            st.HBank -= dist;
            st.UpBank -= Math.Max(dy, 0f);
            st.DownBank -= Math.Max(-dy, 0f);
            st.HasAdopted = true;
            st.Adopted = new DrivenPlayerState
            {
                Pos = cmd.Pos, YawDegrees = cmd.YawDegrees, PitchDegrees = cmd.PitchDegrees,
                Vel = cmd.LinVel, Buttons = cmd.Buttons, Grounded = cmd.Grounded,
            };
            _players.ServerDrive(sender, cmd.Pos, cmd.YawDegrees, cmd.Seq, tick);
        }

        /// <summary>
        /// P3a (SP/MP-unify) server-authoritative respawn reposition. The owner's entity is client-driven, so
        /// its next PlayerStateCommand would ServerDrive it right back off the respawn point -- a bare
        /// ServerTeleport is lost. Ride the recov primitive instead: publish the entity at SpawnPos NOW (both
        /// what observers see and the last-good the envelope validates the resume claim against), open the
        /// freeze window (bump the counter + Recovering -> every claim whose RecovAck lags is discarded, exactly
        /// the out-of-envelope rollback path), and unicast a PlayerRecovEvent so the client teleports its shell
        /// to SpawnPos and echoes the counter. The dead-window claims were already dropped at the IsAlive gate;
        /// this closes the tight respawn race where a still-in-flight death-position claim would otherwise drag
        /// the freshly-respawned entity back. Returns false when this owner has no client-auth stream yet (never
        /// sent a PlayerStateCommand) -- the caller then falls back to a plain ServerTeleport.
        /// </summary>
        public bool RepositionOwner(ushort playerId, Vector3 pos, long tick)
        {
            if (!_driven.TryGetValue(playerId, out var st)) return false;   // not client-driven -> caller ServerTeleports
            // publish the entity at the spawn (observers + the envelope baseline the resume claim measures against)
            _players.ServerTeleport(playerId, pos, tick);
            // the last-good the recov teleports the shell to must be the ENTITY's quantized pos, so the resume
            // claim (shell now sitting there, re-quantized) lands a ~zero delta and adopts clean
            Vector3 landed = _players.TryGetByOwner(playerId, out var e) ? e.Pos : pos;
            st.RecovCounter++;
            st.Recovering = true;
            var evt = new PlayerRecovEvent { Pos = landed, Vel = Vector3.zero, RecovCounter = st.RecovCounter };
            _sendTo?.Invoke(playerId, NetMessagePak.Pack(ReplicationIds.EventPlayerRecov, evt.Write));
            if (NetLog.Enabled)
                NetLog.Sink($"[NET] player {playerId}: server respawn reposition -> recov #{st.RecovCounter} to ({landed.x:0.0},{landed.y:0.0},{landed.z:0.0}) (freeze until echo)");
            return true;
        }

        /// <summary>Test view (P3a): the owner's recov counter -- bumps once per envelope rollback AND once per
        /// server respawn reposition. 0 = never rolled back. Proves the freeze-until-echo primitive fired.</summary>
        public byte DebugRecovCounter(ushort playerId) => _driven.TryGetValue(playerId, out var st) ? st.RecovCounter : (byte)0;
        /// <summary>Test view (P3a): is the owner mid-freeze (discarding claims until the RecovAck echoes)?</summary>
        public bool DebugRecovering(ushort playerId) => _driven.TryGetValue(playerId, out var st) && st.Recovering;

        /// <summary>A leaving peer's authority window dies with it -- a recycled playerId starts clean.</summary>
        public void OnPeerDisconnected(ushort playerId) => _driven.Remove(playerId);
    }
}
