using System.Collections.Generic;
using UnityEngine;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server-authoritative half of the SENTRY (SP/MP-unify §3.1). A FixtureKind.Sentry deployable places as a plain
    // ENTITY (no server node) + a VIEW-ONLY client replica (Sentry.Materialize). This system is where the turret
    // actually SHOOTS: each server tick it scans the authoritative zombies, runs the SAME SentryTargeting the client
    // uses to render aim, and on a fire-decision applies server-authoritative damage through the SAME seam player
    // bullets use (ServerCombat.ZombieHost.DamageZombie, attacker 0 = environment). Without this the replicated sentry
    // would render + aim but land zero kills -- a dead prop. Mirrors GasStationServer (a server system, not a node).
    //
    // CUT 1 scope (documented, not hidden):
    //  * Fixed eaglefire mount (no gun-id on the wire yet -- a storage-selected gun is the follow-up, needs a schema field).
    //  * FIRES REGARDLESS OF POWER. The def carries a faithful 50 W Requires_Power port, but the power SOLVE runs
    //    client-side (there is no server-authoritative power solve today), so the server can't gate on it. A working
    //    turret that doesn't yet respect power beats a faithful-but-dead prop; server-side power gating is the follow-up.
    //  * No server-side sweep: a NEW target must be in the placed-facing 60 deg arc (the client's swept head is cosmetic).
    //    A kept target is held through its full loss bubble regardless (SentryTargeting keeps it without the arc check).
    public sealed class ServerSentries
    {
        readonly ZombieReplication _zombies;
        readonly DeployableReplication _deployables;
        readonly ServerCombat _combat;

        sealed class SentryState { public float FireCd; public ulong KeptId; }
        readonly Dictionary<uint, SentryState> _sentries = new();   // sentry entity NetId -> its cadence + kept-target
        readonly List<SentryTargeting.Candidate> _cands = new();    // rebuilt once per tick, shared across sentries
        readonly Dictionary<ulong, uint> _candNetId = new();        // candidate id (= zombie NetId) -> the NetId to damage
        readonly HashSet<uint> _seen = new();

        // Cut-1 fixed mount: eaglefire (same .dat the client's Sentry.Materialize loads, so damage/range/firerate match).
        static GunDef _gun;
        static GunDef Gun()
        {
            if (_gun != null) return _gun;
            try { _gun = GunDef.FromDatText(System.IO.File.ReadAllText(Godot.ProjectSettings.GlobalizePath("res://content/eaglefire.dat"))); }
            catch { _gun = null; }
            return _gun;
        }

        const float DetectionRadius = 48f;         // ItemSentryAsset.detectionRadius
        const float TargetLossRadius = 48f * 1.2f; // Target_Loss anti-flicker bubble
        const float MuzzleHeight = 0.74f;          // the Sentry node's barrel-tip world height above the base (head 0.72 + barrel)

        public ServerSentries(ZombieReplication zombies, DeployableReplication deployables, ServerCombat combat)
        {
            _zombies = zombies; _deployables = deployables; _combat = combat;
        }

        // 50 Hz server tick. Build the zombie candidate list ONCE, then drive every placed sentry through the shared
        // targeting + fire it via the authoritative damage seam (which marks a kill dead + broadcasts the died event).
        public void Tick(long tick, float dt)
        {
            var gun = Gun();
            float gunRange = gun != null ? gun.Range : float.PositiveInfinity;
            float gunDamage = gun != null ? gun.ZombieDamage : 40f;
            float fireInterval = SentryTargeting.FireCadenceMult * (gun != null ? (gun.Firerate + 1) / 50f : 0.2f);   // source: firerate+1 ticks, x3.33 (a sentry fires slower than a held gun)

            // candidates: every live authoritative zombie (IsHunting isn't on the wire yet -> pass true, cut 1)
            _cands.Clear(); _candNetId.Clear();
            foreach (var z in _zombies.All)
            {
                if (z.IsDead) continue;
                _cands.Add(new SentryTargeting.Candidate(z.NetIdValue, z.Pos, z.Speciality, hunting: true));
                _candNetId[z.NetIdValue] = z.NetIdValue;
            }

            System.Func<Vector3, Vector3, bool> losClear = _combat.WorldRay != null
                ? (from, to) => !_combat.WorldRay(from, to, out _, out _)   // clear iff no world geometry between muzzle + target
                : (from, to) => true;

            _seen.Clear();
            foreach (var e in _deployables.All)
            {
                var def = DeployableDef.ById(e.DefId);
                if (def == null || def.Fixture != FixtureKind.Sentry) continue;
                _seen.Add(e.NetIdValue);
                if (!_sentries.TryGetValue(e.NetIdValue, out var st)) { st = new SentryState(); _sentries[e.NetIdValue] = st; }

                Vector3 muzzle = e.Pos + Vector3.up * MuzzleHeight;
                Vector3 aimFwd = YawForward(e.YawDegrees);   // placed-facing; only gates NEW acquisition (kept target ignores it)

                ulong target = SentryTargeting.ChooseTarget(_cands, muzzle, aimFwd, DetectionRadius, TargetLossRadius, gunRange, losClear, st.KeptId);
                st.KeptId = target;

                if (st.FireCd > 0f) st.FireCd -= dt;
                if (target != 0UL && st.FireCd <= 0f && _candNetId.TryGetValue(target, out uint zid))
                {
                    Vector3 aimPoint = AimPointOf(target);
                    Vector3 dir = (aimPoint - muzzle);
                    dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : aimFwd;
                    _combat.DamageZombieExternal(zid, gunDamage, aimPoint, dir, tick);   // server-auth damage + on-kill marks dead + broadcasts ZombieDiedEvent (attacker 0 = environment)
                    st.FireCd = fireInterval;
                }
            }

            // retire state for sentries that are gone (salvaged/destroyed)
            if (_sentries.Count > _seen.Count)
            {
                List<uint> gone = null;
                foreach (var kv in _sentries) if (!_seen.Contains(kv.Key)) (gone ??= new List<uint>()).Add(kv.Key);
                if (gone != null) foreach (var id in gone) _sentries.Remove(id);
            }
        }

        Vector3 AimPointOf(ulong id)
        {
            foreach (var c in _cands) if (c.Id == id) return c.AimPoint;
            return Vector3.zero;
        }

        // Godot -Z-forward yaw convention: a node at RotationDegrees.Y = yaw faces (-sin, 0, -cos).
        static Vector3 YawForward(float yawDegrees)
        {
            float r = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(-Mathf.Sin(r), 0f, -Mathf.Cos(r));
        }

        // test seam: how many sentries the server is currently driving
        public int TrackedCount => _sentries.Count;
    }
}
