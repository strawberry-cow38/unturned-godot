using System;
using System.Collections.Generic;
using UnityEngine;

namespace SDG.Unturned
{
    // Pure SENTRY TARGETING math (source InteractableSentry.ScanForTargets), extracted so ONE logic drives both
    // sides of the MP split (MP_PLAN §3.1): the server-authoritative ServerSentries tick (scans real zombies,
    // fires ZombieHost.DamageZombie) and the client replica's view-only aim (points the turret + draws tracers).
    // Fed the same candidate set on both sides, the turret picks the SAME target -- so the muzzle visibly tracks
    // whatever the server is actually shooting. No engine/Godot dependency -> L0-testable + pinned to the source.
    public static class SentryTargeting
    {
        // source InteractableSentry: fireTime = firerateTicks/50 * 3.33 ("lower than a player's firerate") -- a
        // sentry fires 3.33x slower than a player holding the same gun. The caller owns the cadence timer.
        public const float FireCadenceMult = 3.33f;
        // source ScanForTargets: a NEW target must sit within a 60-degree forward arc of the head's aim
        // (Vector3.Dot(dirToTarget, aimForward) >= 0.5). The already-acquired target is EXEMPT (full loss bubble),
        // so the turret can't snap to something behind it but won't drop a target that walks around it.
        public const float ForwardArcDot = 0.5f;

        // per-speciality aim / hit / LOS height (source ScanForTargets switch: NORMAL 1.75, SPRINTER 1.0,
        // CRAWLER 0.25, MEGA 2.625). Speciality is the ZombieController.ESpeciality byte (CRAWLER 2, SPRINTER 1,
        // NORMAL/FLANKER/BURNER/ACID upright -> 1.75); the port has no MEGA.
        public static float AimHeight(byte speciality) => speciality switch
        {
            2 => 0.25f,   // CRAWLER
            1 => 1.0f,    // SPRINTER
            _ => 1.75f,   // NORMAL / FLANKER / BURNER / ACID -- upright humanoids
        };

        /// <summary>One scan candidate: a live zombie's ground position + speciality (-> aim height) + whether it
        /// is aggroed. Id is a STABLE handle (the zombie's netId) so keep-target survives the candidate list being
        /// rebuilt+reordered each tick. AimPoint is where the turret aims, the LOS ray ends, and the hit lands.</summary>
        public readonly struct Candidate
        {
            public readonly ulong Id;
            public readonly Vector3 Pos;
            public readonly byte Speciality;
            public readonly bool Hunting;   // source skips !isHunting idle zombies; pass true if a source has no hunting state
            public Candidate(ulong id, Vector3 pos, byte speciality, bool hunting) { Id = id; Pos = pos; Speciality = speciality; Hunting = hunting; }
            public Vector3 AimPoint => Pos + Vector3.up * AimHeight(Speciality);
        }

        /// <summary>Pick the sentry's target from <paramref name="candidates"/>, mirroring ScanForTargets:
        ///   1. KEEP <paramref name="keptId"/> (last tick's target) while it's still present, inside lossRadius
        ///      (clamped to gunRange) and LOS-clear -- so aim stays smooth and doesn't flicker between zombies.
        ///   2. else ACQUIRE the nearest hunting, LOS-clear candidate within detectionRadius (clamped to gunRange)
        ///      that also sits inside the 60-degree forward arc of <paramref name="aimForward"/>.
        /// <paramref name="losClear"/>(from,to) must return true iff no world geometry blocks that ray. Distances
        /// are measured from <paramref name="muzzle"/>. Returns the chosen zombie id, or 0 for "no target".
        /// gunRange clamps both radii to the mounted weapon's reach (source targetDistance = Min(detectionRadius,
        /// maxWeaponDistance)); pass float.PositiveInfinity for "no gun / no clamp".</summary>
        public static ulong ChooseTarget(IReadOnlyList<Candidate> candidates, Vector3 muzzle, Vector3 aimForward,
            float detectionRadius, float lossRadius, float gunRange, Func<Vector3, Vector3, bool> losClear, ulong keptId)
        {
            if (candidates == null) return 0;

            // 1) keep the current target if it's still valid (full loss bubble, LOS-gated) -- find it by stable id
            if (keptId != 0)
            {
                float loss = Mathf.Min(lossRadius, gunRange);
                for (int i = 0; i < candidates.Count; i++)
                {
                    var k = candidates[i];
                    if (k.Id != keptId) continue;
                    if (Vector3.Distance(k.Pos, muzzle) <= loss && losClear(muzzle, k.AimPoint)) return keptId;
                    break;   // the kept target is present but out of range / blocked -> fall through to re-acquire
                }
            }

            // 2) acquire the nearest hunting, in-arc, LOS-clear candidate within detection range
            float best = Mathf.Min(detectionRadius, gunRange);
            ulong chosen = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.Hunting) continue;                                   // source skips idle (!isHunting) zombies
                float d = Vector3.Distance(c.Pos, muzzle);
                if (d > best) continue;
                Vector3 dir = c.Pos - muzzle;
                if (dir.sqrMagnitude > 1e-6f && Vector3.Dot(dir.normalized, aimForward) < ForwardArcDot) continue;   // 60-degree forward-arc gate for a NEW target
                if (!losClear(muzzle, c.AimPoint)) continue;
                best = d; chosen = c.Id;
            }
            return chosen;
        }
    }
}
