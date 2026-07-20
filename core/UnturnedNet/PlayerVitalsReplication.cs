using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// SP/MP-unify wave 2 (B5), SystemId 13 -- OWNER-ONLY fine vitals (food/water/stamina/infection),
    /// modeled 1:1 on SkillsReplication (the owner-only pilot). Resolves the shipped split-authority bug:
    /// HP was server-adopted but food/water/stamina/infection ran the shell's LOCAL sim and the `died`
    /// result was DISCARDED under NetVitalsAdopted -> you drained to Food=0 and never died, because the
    /// server ran no hunger sim.
    ///
    /// The server holds one <see cref="PlayerVitalsSim"/> per connected player -- the SAME sim the SP shell
    /// runs -- stepped every 50 Hz tick BETWEEN VehicleHost.Step and Combat.Step (NetWorldServer). HP is
    /// NEVER owned by the vitals sim: each tick Sim.Health is re-seeded from the single HP authority
    /// (CombatState.HealthExact via <see cref="HealthOf"/>), the sim steps, and the delta is routed OUT --
    /// starvation LOSS through the queued <see cref="DamageSink"/> (ServerCombat.DamagePlayerExternal, env
    /// attacker 0, death-capable, landing in THIS tick's Combat.Step), regen through the direct
    /// <see cref="RegenSink"/> HealthExact raise. Death/respawn stay owned by ServerCombat. The HP-delta
    /// routing runs only while <see cref="SurvivalDrain"/> is on: OFF (the strawberry default) leaves the
    /// coarse-HP path byte-identical -- no passive regen, no starvation -- while stamina/infection still
    /// step + replicate. Stamina is server-owned but sprint stays client-auth: the server derives
    /// `sprinting` from the ADOPTED stance (<see cref="SprintingOf"/>), no second body.
    ///
    /// The client holds exactly one replica (its own); ReadSnapshot is the only writer there. The owner
    /// block hashes the QUANTIZED wire floats (round-tripped through the 8-bit encoding) so the server's
    /// StateHashFor matches the replica's StateHash with exact equality. NOT in EnableSyncCheck (owner-only,
    /// excluded by design like Skills/Inventory). Owner: tinyclaw.
    /// </summary>
    public sealed class PlayerVitalsReplication : IReplicatedSystem
    {
        /// <summary>Wire precision for each 0..1 vital -- 8 bits = ~1/256 grain, plenty for a HUD bar.</summary>
        public const int VitalsBits = 8;

        public sealed class VitalsEntry
        {
            public ushort OwnerPlayerId;
            public PlayerVitalsSim Sim = new PlayerVitalsSim();   // server: stepped; client: value-holder written by ReadSnapshot
            public bool Bleeding, Broken;                          // status bits carried on the wire (server has no source yet -> false)
            public long LastChangedTick;

            // dirty-detection cache: the last QUANTIZED wire values, so an idle/unchanged block costs no
            // owner-block delta (a full-stamina, non-sprinting, uninfected player stamps nothing).
            float _qF = -1f, _qW = -1f, _qS = -1f, _qI = -1f;
            bool _qBl, _qBr, _hasQ;

            /// <summary>Bump LastChangedTick only when the QUANTIZED value actually changes. Mirrors the
            /// compose-boundary +1 stamp (a change landing after this tick's snapshot composed still beats the
            /// acked baseline).</summary>
            public void StampIfChanged(long tick)
            {
                float f = Q(Sim.Food), w = Q(Sim.Water), s = Q(Sim.Stamina), inf = Q(Sim.Infection);
                if (_hasQ && f == _qF && w == _qW && s == _qS && inf == _qI && Bleeding == _qBl && Broken == _qBr) return;
                _qF = f; _qW = w; _qS = s; _qI = inf; _qBl = Bleeding; _qBr = Broken; _hasQ = true;
                LastChangedTick = tick + 1;
            }
        }

        public byte SystemId => ReplicationIds.SystemVitals;

        readonly Dictionary<ushort, VitalsEntry> _byOwner = new Dictionary<ushort, VitalsEntry>();

        public int Count => _byOwner.Count;

        public bool TryGet(ushort ownerPlayerId, out VitalsEntry entry) => _byOwner.TryGetValue(ownerPlayerId, out entry);

        // ---- server seams (wired in NetWorldHost; null on a bare replica / L0 hash-only harness) ----

        /// <summary>Server authority for the hunger/regen HP routing. OFF (default) keeps the coarse-HP path
        /// byte-identical -- no starvation, no passive regen; the fine vitals (stamina/food/water/infection)
        /// still step + replicate. The DedicatedServer/loopback own this (F1 `survival on|off`).</summary>
        public bool SurvivalDrain;

        public Func<ushort, bool> IsAlive;                                 // CombatState.IsAlive -- a corpse doesn't drain (server owns death/respawn)
        public Func<ushort, bool> SprintingOf;                            // adopted stance / held MoveInput.Stance == SPRINT
        public Func<ushort, PlayerVitalsSim.Multipliers> MultipliersOf;   // the connected player's PlayerSkills multipliers
        public Func<ushort, float> HealthOf;                             // the single HP authority (CombatState.HealthExact) re-seeded each tick
        public Action<ushort, float> DamageSink;                         // starvation loss -> Combat.DamagePlayerExternal (queued, death-capable)
        public Action<ushort, float> RegenSink;                          // regen gain -> direct HealthExact raise

        public VitalsEntry ServerAdd(ushort ownerPlayerId, long tick)
        {
            var e = new VitalsEntry { OwnerPlayerId = ownerPlayerId, LastChangedTick = tick + 1 };
            _byOwner[ownerPlayerId] = e;
            return e;
        }

        public void ServerRemove(ushort ownerPlayerId) => _byOwner.Remove(ownerPlayerId);

        /// <summary>One 50 Hz vitals step for every living server-owned player -- stepped BETWEEN
        /// VehicleHost.Step and Combat.Step so a queued starvation drain lands in THIS tick's Combat.Step.
        /// HP is re-seeded from the single authority, the sim steps, and the delta is routed OUT (never a
        /// direct HealthExact write for DAMAGE). Deterministic order (sorted owners, no RNG).</summary>
        public void ServerStep(long tick, float dt)
        {
            if (_byOwner.Count == 0) return;
            var owners = new List<ushort>(_byOwner.Keys);
            owners.Sort();
            foreach (ushort pid in owners)
            {
                var e = _byOwner[pid];
                if (IsAlive != null && !IsAlive(pid)) continue;   // server owns death/respawn; a corpse doesn't drain

                // HP is NEVER owned by the vitals sim: re-seed from the single authority, step, route the delta.
                float hpBefore = HealthOf != null ? HealthOf(pid) : e.Sim.Health;
                e.Sim.Health = hpBefore;
                bool sprinting = SprintingOf != null && SprintingOf(pid);
                var m = MultipliersOf != null ? MultipliersOf(pid) : PlayerVitalsSim.Multipliers.None;
                e.Sim.Step(sprinting, SurvivalDrain, dt, m);   // fine vitals always step; food/water drain gated inside by SurvivalDrain
                float delta = e.Sim.Health - hpBefore;
                // the HP-delta routing (starvation damage + passive regen) is the survival mechanic itself:
                // OFF => the coarse-HP path is byte-untouched (det. point 6). The un-routed Sim.Health mutation
                // is discarded -- next tick re-seeds from the authority.
                if (SurvivalDrain)
                {
                    if (delta < 0f) DamageSink?.Invoke(pid, -delta);        // starvation/dehydration/infection loss (queued env damage)
                    else if (delta > 0f) RegenSink?.Invoke(pid, delta);     // regen while fed + hydrated (direct HealthExact raise)
                }
                e.StampIfChanged(tick);
            }
        }

        /// <summary>Consume raise (ServerTransactions.OnConsume): apply a consumable's food/water/stamina/
        /// infection effects to the server sim + the bleeding/broken clears. The wire echo re-adopts them onto
        /// the owner shell. No-op on a player with no vitals entry.</summary>
        public void ServerRaise(ushort ownerPlayerId, float food, float water, float stamina, float infectionDelta,
                                bool stopsBleeding, bool healsBroken, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return;
            e.Sim.Food = Clamp01(e.Sim.Food + food);
            e.Sim.Water = Clamp01(e.Sim.Water + water);
            e.Sim.Stamina = Clamp01(e.Sim.Stamina + stamina);
            e.Sim.Infection = Clamp01(e.Sim.Infection + infectionDelta);
            if (stopsBleeding) e.Bleeding = false;
            if (healsBroken) e.Broken = false;
            e.LastChangedTick = tick + 1;   // stamp now; ServerStep's StampIfChanged re-affirms on the living player
        }

        // ---- IReplicatedSystem (owner-only: both paths write the SAME single-entry shape) ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => WriteOwnerBlock(w, ctx.ClientPlayerId, always: true);

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            bool dirty = _byOwner.TryGetValue(ctx.ClientPlayerId, out var e) && e.LastChangedTick > baselineTick;
            WriteOwnerBlock(w, ctx.ClientPlayerId, always: dirty);
        }

        void WriteOwnerBlock(NetPakWriter w, ushort clientPlayerId, bool always)
        {
            if (!always || !_byOwner.TryGetValue(clientPlayerId, out var e)) { w.WriteUInt8(0); return; }
            w.WriteUInt8(1);
            w.WriteUInt16(e.OwnerPlayerId);
            w.WriteUnsignedNormalizedFloat(Clamp01(e.Sim.Food), VitalsBits);
            w.WriteUnsignedNormalizedFloat(Clamp01(e.Sim.Water), VitalsBits);
            w.WriteUnsignedNormalizedFloat(Clamp01(e.Sim.Stamina), VitalsBits);
            w.WriteUnsignedNormalizedFloat(Clamp01(e.Sim.Infection), VitalsBits);
            w.WriteBit(e.Bleeding);
            w.WriteBit(e.Broken);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt8(out byte count)) return;
            if (count == 0) return;   // owner-only: full snapshots simply re-state my entry; nothing to clear
            if (!r.ReadUInt16(out ushort owner)) return;
            if (!r.ReadUnsignedNormalizedFloat(VitalsBits, out float food)) return;
            if (!r.ReadUnsignedNormalizedFloat(VitalsBits, out float water)) return;
            if (!r.ReadUnsignedNormalizedFloat(VitalsBits, out float stamina)) return;
            if (!r.ReadUnsignedNormalizedFloat(VitalsBits, out float infection)) return;
            if (!r.ReadBit(out bool bleeding)) return;
            if (!r.ReadBit(out bool broken)) return;
            if (!_byOwner.TryGetValue(owner, out var e))
            {
                e = new VitalsEntry { OwnerPlayerId = owner };
                _byOwner[owner] = e;
            }
            e.Sim.Food = food; e.Sim.Water = water; e.Sim.Stamina = stamina; e.Sim.Infection = infection;
            e.Bleeding = bleeding; e.Broken = broken;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            var owners = new List<ushort>(_byOwner.Keys);
            owners.Sort();
            foreach (ushort id in owners) h = MixEntry(h, _byOwner[id]);
            return h;
        }

        /// <summary>Owner-only parity: the server's hash of ONE player's entry, comparable against that
        /// client's replica StateHash() (which only ever contains its own entry).</summary>
        public ulong StateHashFor(ushort ownerPlayerId)
        {
            ulong h = NetHash.FnvOffset;
            if (_byOwner.TryGetValue(ownerPlayerId, out var e)) h = MixEntry(h, e);
            return h;
        }

        static ulong MixEntry(ulong h, VitalsEntry e)
        {
            h = NetHash.MixUInt32(h, e.OwnerPlayerId);
            h = NetHash.MixFloat(h, Q(e.Sim.Food));       // hash the QUANTIZED wire value (server raw -> round-trip;
            h = NetHash.MixFloat(h, Q(e.Sim.Water));      // client stores the round-trip, Q is idempotent on it),
            h = NetHash.MixFloat(h, Q(e.Sim.Stamina));    // so StateHashFor == the owner replica's StateHash exactly.
            h = NetHash.MixFloat(h, Q(e.Sim.Infection));
            h = NetHash.MixByte(h, e.Bleeding ? (byte)1 : (byte)0);
            h = NetHash.MixByte(h, e.Broken ? (byte)1 : (byte)0);
            return h;
        }

        static float Q(float v) => NetQuantization.QuantizeUnsignedNormalizedFloat(Clamp01(v), VitalsBits);
        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
