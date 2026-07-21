using System.Collections.Generic;
using UnityEngine;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server-authoritative half of the TRAP (SP/MP-unify §3.1). A FixtureKind.Trap deployable places as a plain ENTITY
    // + a VIEW-ONLY client replica (Trap.Materialize renders the archetype by DefId). This system is where the trap
    // actually BITES: each server tick it edge-detects the authoritative zombies entering a placed trap's footprint and
    // applies damage through ServerCombat.DamageZombieExternal -- the SAME seam sentry fire uses, which marks the kill +
    // broadcasts ZombieDiedEvent so the death RENDERS on every client. Non-explosive traps (spike/wire/caltrop) bite the
    // entrant + wear the trap's replicated Health down 5 per pass (break at 0); a landmine detonates an AoE + self-
    // destructs. Mirrors ServerSentries (a server system, not a node); the SP Trap node's logic, over server entities.
    //
    // Zombies AND players trigger it (cut-2: a player stepping in takes the flat PlayerDamage / the landmine's squared
    // blast, via DamagePlayerExternal, attacker 0 = the trap). Player damage is PvP-GATED (source InteractableTrap:198/151
    // require Provider.isPvP) -- on a PvE server traps never bite a player, zombie damage stays always-on; on a PvP server
    // they bite ANY player (owner included, no friend/foe -- source-faithful). VEHICLES are still deferred (no vehicle
    // server-damage seam yet; the SP Trap only chips tires anyway).
    public sealed class ServerTraps
    {
        readonly ZombieReplication _zombies;
        readonly DeployableReplication _deployables;
        readonly ServerCombat _combat;
        readonly PlayerReplication _players;

        sealed class TrapState { public float Age; public bool Armed; public readonly HashSet<uint> Inside = new(); public readonly HashSet<ushort> InsidePlayers = new(); }
        readonly Dictionary<uint, TrapState> _traps = new();   // trap entity NetId -> arm timer + last-tick "inside" sets (edge detection)
        readonly HashSet<uint> _seen = new();
        readonly List<(uint id, Vector3 pos, byte spec)> _nowInside = new();   // reused per trap per tick (no per-trap alloc, mirrors ServerSentries._cands)
        readonly List<(ushort id, Vector3 pos)> _nowInsidePlayers = new();     // reused: players in the footprint this tick

        struct TrapParams { public float ZombieDamage, PlayerDamage, Range2, TriggerRadius; public bool IsExplosive; }
        readonly Dictionary<ushort, TrapParams> _paramCache = new();   // per-archetype params (from Trap.ForDefId), cached by DefId

        const float SetupDelay = 0.25f;    // Trap_Setup_Delay: arm time after placement (so the placer isn't caught by their own trap)
        const float WearPerTrigger = 5f;   // source: a non-explosive trap's Health drops 5 per trigger; at 0 it breaks + is removed

        public ServerTraps(ZombieReplication zombies, DeployableReplication deployables, ServerCombat combat, PlayerReplication players)
        { _zombies = zombies; _deployables = deployables; _combat = combat; _players = players; }

        // read an archetype's zombie-relevant params from the SAME source the client node uses (Trap.ForDefId), so both
        // sides agree. The transient config node is never added to the tree (no _Ready/visual) and is freed immediately.
        TrapParams ParamsFor(ushort defId)
        {
            if (_paramCache.TryGetValue(defId, out var p)) return p;
            var t = Trap.ForDefId(defId);
            p = new TrapParams { ZombieDamage = t.ZombieDamage, PlayerDamage = t.PlayerDamage, Range2 = t.Range2, TriggerRadius = t.TriggerRadius, IsExplosive = t.IsExplosive };
            t.Free();
            _paramCache[defId] = p;
            return p;
        }

        // 50 Hz server tick. Drive every placed trap: arm, then edge-trigger on zombies entering its footprint.
        public void Tick(long tick, float dt)
        {
            System.Func<Vector3, Vector3, bool> losClear = _combat.WorldRay != null
                ? (from, to) => !_combat.WorldRay(from, to, out _, out _)   // clear iff no world geometry between the blast + a target
                : (from, to) => true;

            _seen.Clear();
            foreach (var e in _deployables.All)
            {
                if (e == null) continue;
                var def = DeployableDef.ById(e.DefId);
                if (def == null || def.Fixture != FixtureKind.Trap) continue;
                _seen.Add(e.NetIdValue);
                if (!_traps.TryGetValue(e.NetIdValue, out var st)) { st = new TrapState(); _traps[e.NetIdValue] = st; }
                var p = ParamsFor(e.DefId);

                st.Age += dt;
                if (st.Age < SetupDelay) continue;   // still arming -- inert

                // who's live + inside the footprint this tick (reused lists, cleared per trap -- no per-trap alloc)
                _nowInside.Clear();
                foreach (var z in _zombies.All)
                {
                    if (z.IsDead) continue;
                    if (Vector3.Distance(z.Pos, e.Pos) <= p.TriggerRadius) _nowInside.Add((z.NetIdValue, z.Pos, z.Speciality));
                }
                _nowInsidePlayers.Clear();
                foreach (var pl in _players.All)
                    if (Vector3.Distance(pl.Pos, e.Pos) <= p.TriggerRadius) _nowInsidePlayers.Add((pl.OwnerPlayerId, pl.Pos));

                // arm-seed on the FIRST armed tick: anything already standing in the footprint is seeded so it isn't hit
                // until it leaves + re-enters (source OnTriggerEnter fires on ENTER only, not on a pre-existing overlap).
                if (!st.Armed)
                {
                    st.Armed = true;
                    foreach (var zi in _nowInside) st.Inside.Add(zi.id);
                    foreach (var pi in _nowInsidePlayers) st.InsidePlayers.Add(pi.id);
                    continue;
                }

                // edge-trigger: any zombie OR player NEWLY inside (not in last tick's set) fires the trap
                bool broke = false;
                foreach (var zi in _nowInside)
                {
                    if (st.Inside.Contains(zi.id)) continue;   // already inside last tick -> not a fresh enter
                    if (p.IsExplosive) { Detonate(e, p, tick, losClear); broke = true; break; }   // landmine: AoE + self-destruct on the first entrant
                    Vector3 hit = zi.pos + Vector3.up * SentryTargeting.AimHeight(zi.spec);
                    _combat.DamageZombieExternal(zi.id, p.ZombieDamage, hit, Vector3.up, tick);   // direct bite (renders the death on clients)
                    if (!Wear(e, tick)) { broke = true; break; }
                }
                // players stepping in: PvP-GATED (source InteractableTrap:198/151 -- spike bite + landmine-on-enter both
                // require Provider.isPvP; a PvE server's traps never bite a player). Zombie damage above is always-on.
                if (!broke && _combat.PvPEnabled) foreach (var pi in _nowInsidePlayers)
                {
                    if (st.InsidePlayers.Contains(pi.id)) continue;
                    if (p.IsExplosive) { Detonate(e, p, tick, losClear); broke = true; break; }
                    _combat.DamagePlayerExternal(pi.id, p.PlayerDamage, 0);   // direct bite, attacker 0 = the trap (environment)
                    if (!Wear(e, tick)) { broke = true; break; }
                }
                if (broke) { _traps.Remove(e.NetIdValue); continue; }

                st.Inside.Clear();
                foreach (var zi in _nowInside) st.Inside.Add(zi.id);          // remember for next tick's edge detection
                st.InsidePlayers.Clear();
                foreach (var pi in _nowInsidePlayers) st.InsidePlayers.Add(pi.id);
            }

            // retire state for traps that are gone (worn out / salvaged / destroyed)
            if (_traps.Count > _seen.Count)
            {
                List<uint> gone = null;
                foreach (var kv in _traps) if (!_seen.Contains(kv.Key)) (gone ??= new List<uint>()).Add(kv.Key);
                if (gone != null) foreach (var id in gone) _traps.Remove(id);
            }
        }

        // wear a non-explosive trap's replicated Health down per trigger (source: -5/pass); returns false if it broke
        // (removed at 0). ServerSetScalars updates e.Health in place, so multiple entrants in one tick wear cumulatively.
        bool Wear(DeployableReplication.DeployableEntity e, long tick)
        {
            float newHp = e.Health - WearPerTrigger;
            if (newHp <= 0f) { _deployables.ServerRemove(e.NetIdValue, tick); return false; }
            _deployables.ServerSetScalars(e.NetIdValue, newHp, e.Fuel, e.OnFire, tick);
            return true;
        }

        // LANDMINE: an AoE over Range2 (world-LOS-blocked), then the trap self-destructs (one-shot). Zombies take LINEAR
        // falloff (port convention), players SQUARED (source Player.cs:1975; armor deferred -- matches the grenade path).
        // Vehicles in the blast are still deferred (no vehicle server-damage seam yet).
        void Detonate(DeployableReplication.DeployableEntity e, TrapParams p, long tick, System.Func<Vector3, Vector3, bool> losClear)
        {
            Vector3 center = e.Pos;
            foreach (var z in _zombies.All)
            {
                if (z.IsDead) continue;
                float d = Vector3.Distance(z.Pos, center);
                if (d > p.Range2) continue;
                Vector3 hit = z.Pos + Vector3.up * SentryTargeting.AimHeight(z.Speciality);
                if (!losClear(center + Vector3.up * 0.8f, hit)) continue;   // a wall shields the blast (source ExplosionBlocked)
                float dmg = ExplosionMath.Linear(p.ZombieDamage, d, p.Range2);
                if (dmg > 0f) _combat.DamageZombieExternal(z.NetIdValue, dmg, hit, (z.Pos - center).normalized, tick);
            }
            if (_combat.PvPEnabled) foreach (var pl in _players.All)   // players PvP-gated (source DamageTool.explode:1009); squared falloff
            {
                float d = Vector3.Distance(pl.Pos, center);
                if (d > p.Range2) continue;
                Vector3 hit = pl.Pos + Vector3.up * 0.9f;
                if (!losClear(center + Vector3.up * 0.8f, hit)) continue;
                float dmg = ExplosionMath.Squared(p.PlayerDamage, d, p.Range2);
                if (dmg > 0f) _combat.DamagePlayerExternal(pl.OwnerPlayerId, dmg, 0);
            }
            _deployables.ServerRemove(e.NetIdValue, tick);
        }

        // test seam: how many traps the server is currently driving
        public int TrackedCount => _traps.Count;
    }
}
