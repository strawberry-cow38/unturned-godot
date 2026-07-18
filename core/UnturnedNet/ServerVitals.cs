using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// The server-side owner of player VITALS (docs/MP_VITALS_PLAN.md §3): one authoritative
    /// PlayerVitalsSim per connected player, stepped on the TickSimulation tick (after the player sim,
    /// before combat), keyed by PlayerId like CombatState/Skills/Inventories. This is the ONE
    /// write-through helper for health (§10 risk 6): every mutation -- combat damage, consume heals,
    /// environmental drain, death, respawn -- lands in Sim.Health here and mirrors into
    /// CombatEntity.HealthExact + the coarse wire byte with the SAME Ceiling convention, so the
    /// observer's coarse view can never fork from the owner's exact one.
    ///
    /// Game-side damage sources (zombie bite, fall, OOB, vehicle blast) fire from Godot physics
    /// callbacks OUTSIDE TickSimulation -- they ENQUEUE (never mutate inline) and the queue drains at
    /// one deterministic point in Step (§4 ordering rule / §10 risk 2). Server-side sources that already
    /// run inside the tick (bullets/melee/grenades via ServerCombat.ApplyPlayerDamage, consume via
    /// ServerTransactions) apply directly through the same helpers.
    /// </summary>
    public sealed class ServerVitals
    {
        // damage-cause tags (diagnostics + future death-cause UI; the wire never carries them yet)
        public const byte CauseGeneric = 0;
        public const byte CauseZombie = 1;
        public const byte CauseFall = 2;
        public const byte CauseBlast = 3;
        public const byte CauseOob = 4;
        public const byte CauseConsole = 5;

        public const int BleedIconTicks = 250;   // the SP 5 s bleeding HUD-icon timer (:1859) -- icon only, no DoT

        public sealed class Entry
        {
            public ushort OwnerPlayerId;
            public PlayerVitalsSim Sim = new PlayerVitalsSim();   // THE authoritative vitals
            public bool Bleeding;                 // 5 s icon state, server-side now (set on damage > 1)
            public long BleedUntilTick;
            public bool Broken;                   // broken legs (fall), healed by useHealBroken/respawn.
                                                  // HUD/heal state ONLY -- both bodies compute their own
                                                  // movement-gating Broken locally (§10 risk 7)
            public long LastChangedTick;          // owner-block delta dirtiness (Stamp = tick + 1)
            public Vector3 PrevPos;               // movement proxy feeding the sprint-drain input
            public bool HasPrevPos;

            // last QUANTIZED wire tuple (the owner-block encoding): the entry stamps dirty only when a
            // wire-visible value actually moves, so a quiescent player (full stamina, fed) goes
            // delta-silent instead of re-writing sub-quantum float drift every tick
            internal byte QHealth = 100, QFood = 255, QWater = 255, QStamina = 255, QInfection, QFlags;
        }

        /// <summary>The server's hunger/thirst toggle -- replaces the SP static for the authoritative
        /// world (§10 risk 9: the console `survival` verb mutates ONLY this; the owner block's flags
        /// bit2 mirrors it back into the shell's static so prediction stays in step).</summary>
        public bool SurvivalDrainEnabled;

        /// <summary>The death tail (KillPlayer) lives on ServerCombat; set by NetWorldServer right after
        /// both are constructed. Null only in bare L0 harnesses that never kill.</summary>
        public ServerCombat Combat;

        readonly PlayerCombatReplication _combat;
        readonly PlayerReplication _players;
        readonly SkillsReplication _skills;
        readonly Dictionary<ushort, Entry> _byOwner = new Dictionary<ushort, Entry>();

        struct Pending
        {
            public ushort Victim;
            public float Amount;
            public ushort Attacker;
            public byte Cause;
            public bool IsInfection;
        }
        readonly List<Pending> _queue = new List<Pending>();

        public ServerVitals(PlayerCombatReplication combat, PlayerReplication players, SkillsReplication skills)
        {
            _combat = combat;
            _players = players;
            _skills = skills;
        }

        public int Count => _byOwner.Count;
        public int QueuedCount => _queue.Count;

        public bool TryGet(ushort ownerPlayerId, out Entry entry) => _byOwner.TryGetValue(ownerPlayerId, out entry);

        // see SkillsReplication.Stamp: mutations stamp one tick ahead so a change landing after this
        // tick's snapshot composed still beats the acked baseline (the compose-boundary off-by-one)
        static long Stamp(long tick) => tick + 1;

        // ---- the owner-block quantization (shared with VitalsReplication so hash/golden parity is
        // structural): health rides the coarse-byte Ceiling convention (CombatReplication :489), the
        // 0..1 vitals ride u8 (quantum 1/255 = 0.004, well under the 0.005/s drain rate) ----

        public static byte QuantizeHealth(float health) => (byte)Math.Clamp((int)Math.Ceiling(health), 0, 100);
        public static byte Quantize01(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);

        public const byte FlagBleeding = 1 << 0;
        public const byte FlagBroken = 1 << 1;
        public const byte FlagSurvivalDrain = 1 << 2;   // lets the shell mirror the drain toggle (§10 risk 9)

        public byte FlagsFor(Entry e) => (byte)((e.Bleeding ? FlagBleeding : 0)
                                              | (e.Broken ? FlagBroken : 0)
                                              | (SurvivalDrainEnabled ? FlagSurvivalDrain : 0));

        // ---- lifecycle ----

        public Entry ServerAdd(ushort ownerPlayerId, long tick)
        {
            var e = new Entry { OwnerPlayerId = ownerPlayerId, LastChangedTick = Stamp(tick) };
            _byOwner[ownerPlayerId] = e;
            return e;
        }

        public void ServerRemove(ushort ownerPlayerId) => _byOwner.Remove(ownerPlayerId);

        /// <summary>Respawn reset: a fresh sim (the SP Respawn :1915-1916 parity -- full health, full
        /// food/water/stamina, zero infection) + bleeding/broken cleared, mirrored through.</summary>
        public void ResetFor(ushort ownerPlayerId, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return;
            e.Sim = new PlayerVitalsSim();
            e.Bleeding = false;
            e.BleedUntilTick = 0;
            e.Broken = false;
            MirrorHealth(e, tick);
            StampIfChanged(e, tick);
        }

        // ---- the enqueue entry points (game-side sources: bite / fall / OOB / blast / console) ----

        public void EnqueueDamage(ushort victim, float amount, byte cause, ushort attacker = 0)
        {
            if (amount <= 0f) return;
            _queue.Add(new Pending { Victim = victim, Amount = amount, Attacker = attacker, Cause = cause });
        }

        /// <summary>Raw infection amount (0..1); the IMMUNITY multiplier applies at drain time from the
        /// SERVER's authoritative skills, never the sender body's local defaults (§4).</summary>
        public void EnqueueInfection(ushort victim, float amount)
        {
            if (amount <= 0f) return;
            _queue.Add(new Pending { Victim = victim, Amount = amount, IsInfection = true });
        }

        // ---- the unified health write-through (§10 risk 6: ONE owner of both encodings) ----

        /// <summary>Decrement health through the sim float and mirror. Sets the server bleeding icon on
        /// a real hit (SP :1859 parity). Does NOT run the death tail -- the caller checks
        /// HealthExact &lt;= 0 (ServerCombat.ApplyPlayerDamage) or Step drains into KillPlayer.</summary>
        public void ApplyDamageDirect(ushort victim, float amount, long tick)
        {
            if (!_byOwner.TryGetValue(victim, out var e))
            {
                // no vitals entry (a bare harness driving CombatState directly): keep the pre-split
                // direct decrement so ServerCombat's behavior is unchanged for it
                if (_combat.TryGet(victim, out var cs0))
                {
                    cs0.HealthExact -= amount;
                    cs0.Health = QuantizeHealth(cs0.HealthExact);
                }
                return;
            }
            e.Sim.Health = MathF.Max(0f, e.Sim.Health - amount);
            if (amount > 1f) { e.Bleeding = true; e.BleedUntilTick = tick + BleedIconTicks; }
            MirrorHealth(e, tick);
            StampIfChanged(e, tick);
        }

        /// <summary>Heal through the sim float and mirror (consume useHealth; replaces the OnConsume
        /// :356-360 direct HealthExact patch -- and its RoundToInt divergence, §10 risk 6).</summary>
        public void ApplyHealDirect(ushort victim, float amount, long tick)
        {
            if (!_byOwner.TryGetValue(victim, out var e))
            {
                if (_combat.TryGet(victim, out var cs0))
                {
                    cs0.HealthExact = MathF.Min(100f, cs0.HealthExact + amount);
                    cs0.Health = QuantizeHealth(cs0.HealthExact);
                }
                return;
            }
            e.Sim.Health = MathF.Min(e.Sim.MaxHealth, e.Sim.Health + amount);
            MirrorHealth(e, tick);
            StampIfChanged(e, tick);
        }

        /// <summary>The death write: health floors to 0 through the same mirror (called by
        /// ServerCombat.KillPlayer so console kills / overkill damage leave a coherent 0/0/0).</summary>
        public void ForceDead(ushort victim, long tick)
        {
            if (!_byOwner.TryGetValue(victim, out var e)) return;
            e.Sim.Health = 0f;
            MirrorHealth(e, tick);
            StampIfChanged(e, tick);
        }

        /// <summary>Server Broken flag (HUD/heal state only, §10 risk 7): set by the avatar's own
        /// deterministic landing through the ServerBroken seam; cleared by useHealBroken and respawn.</summary>
        public void SetBroken(ushort ownerPlayerId, bool broken, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e) || e.Broken == broken) return;
            e.Broken = broken;
            StampIfChanged(e, tick);
        }

        public void StopBleeding(ushort ownerPlayerId, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e) || !e.Bleeding) return;
            e.Bleeding = false;
            e.BleedUntilTick = 0;
            StampIfChanged(e, tick);
        }

        // ---- the 50 Hz vitals step ----

        /// <summary>Drain the queue (deterministic enqueue order), then step every ALIVE player's sim
        /// (dead players are never stepped -- PlayerVitalsSim doc :32) with server-derived inputs:
        /// sprint from the replicated stance bits + a real position delta, drain from the server flag,
        /// multipliers from the server's authoritative skills. Deaths route through ServerCombat.KillPlayer
        /// (Alive/Deaths/RespawnAtTick/ServerClearInput/PlayerDiedEvent -- ONE death tail, §2).</summary>
        public void Step(long tick)
        {
            if (_queue.Count > 0)
            {
                for (int i = 0; i < _queue.Count; i++)
                {
                    var q = _queue[i];
                    if (!_combat.TryGet(q.Victim, out var cs) || !cs.Alive) continue;   // late damage on a corpse: dropped
                    if (q.IsInfection)
                    {
                        if (!_byOwner.TryGetValue(q.Victim, out var e)) continue;
                        float mult = _skills.TryGet(q.Victim, out var sk) ? sk.Skills.ImmunityInfectionMultiplier() : 1f;
                        e.Sim.Infection = Math.Clamp(e.Sim.Infection + q.Amount * mult, 0f, 1f);
                        StampIfChanged(e, tick);
                    }
                    else
                    {
                        ApplyDamageDirect(q.Victim, q.Amount, tick);
                        _combat.MarkDirty(cs, tick);
                        if (cs.HealthExact <= 0f) Combat?.KillPlayer(q.Victim, q.Attacker, tick);
                    }
                }
                _queue.Clear();
            }

            float dt = (float)SimClock.FixedDelta;
            foreach (ushort owner in SortedOwners())
            {
                var e = _byOwner[owner];
                if (!_combat.TryGet(owner, out var cs) || !cs.Alive) { e.HasPrevPos = false; continue; }

                bool sprinting = false;
                if (_players.TryGetByOwner(owner, out var pe))
                {
                    // the SP predicate is `moving && Stance == SPRINT` (:1939): stance from the wire's
                    // buttons bits, "moving" proven by a real position delta on the authoritative entity
                    bool moved = e.HasPrevPos && (pe.Pos - e.PrevPos).sqrMagnitude > 1e-6f;
                    e.PrevPos = pe.Pos;
                    e.HasPrevPos = true;
                    sprinting = moved && _players.TryGetHeldInput(owner, out var inp) && inp.Stance == EPlayerStance.SPRINT;
                }

                var m = _skills.TryGet(owner, out var sk) ? MultipliersFor(sk.Skills) : PlayerVitalsSim.Multipliers.None;
                bool died = e.Sim.Step(sprinting, SurvivalDrainEnabled, dt, in m);
                MirrorHealth(e, tick);
                if (e.Bleeding && tick >= e.BleedUntilTick) e.Bleeding = false;
                StampIfChanged(e, tick);
                if (died) Combat?.KillPlayer(owner, 0, tick);
            }
        }

        public static PlayerVitalsSim.Multipliers MultipliersFor(PlayerSkills skills) => new PlayerVitalsSim.Multipliers
        {
            ExerciseStaminaDrain = skills.ExerciseStaminaDrainMultiplier(),
            CardioStaminaRegen = skills.CardioStaminaRegenMultiplier(),
            SurvivalDrain = skills.SurvivalDrainMultiplier(),
            VitalityRegen = skills.VitalityRegenMultiplier(),
        };

        /// <summary>The write-through mirror: the exact float and the coarse Ceiling byte both come from
        /// Sim.Health at every mutation site, so the two encodings can never fork (§10 risk 6). The
        /// combat entity is only re-dirtied when the WIRE byte moved (regen sub-quantum drift must not
        /// spam the SystemPlayerCombat delta).</summary>
        void MirrorHealth(Entry e, long tick)
        {
            if (!_combat.TryGet(e.OwnerPlayerId, out var cs)) return;
            cs.HealthExact = e.Sim.Health;
            byte b = QuantizeHealth(e.Sim.Health);
            if (cs.Health != b)
            {
                cs.Health = b;
                _combat.MarkDirty(cs, tick);
            }
        }

        void StampIfChanged(Entry e, long tick)
        {
            byte h = QuantizeHealth(e.Sim.Health);
            byte f = Quantize01(e.Sim.Food);
            byte w = Quantize01(e.Sim.Water);
            byte s = Quantize01(e.Sim.Stamina);
            byte i = Quantize01(e.Sim.Infection);
            byte fl = FlagsFor(e);
            if (h == e.QHealth && f == e.QFood && w == e.QWater && s == e.QStamina && i == e.QInfection && fl == e.QFlags) return;
            e.QHealth = h; e.QFood = f; e.QWater = w; e.QStamina = s; e.QInfection = i; e.QFlags = fl;
            e.LastChangedTick = Stamp(tick);
        }

        List<ushort> SortedOwners()
        {
            var ids = new List<ushort>(_byOwner.Keys);
            ids.Sort();
            return ids;
        }
    }
}
