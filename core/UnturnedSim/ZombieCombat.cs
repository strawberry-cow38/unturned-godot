using System;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>Where a shot landed. Approximated from hit HEIGHT rather than bone data, the same rule
    /// the old controller used for headshots -- the top ~18% of a zombie is its skull. Arm is absent on
    /// purpose: separating arms from the torso needs the rig's bone layout, and inventing a band for it
    /// would be a guess dressed as a limb.</summary>
    public enum ZombieLimb : byte
    {
        Skull = 0,
        Spine = 1,
        Leg = 2,
    }

    public struct ZombieHit
    {
        public ZombieId Id;
        public int Row;
        public Vector3 Point;      // world-space entry point
        public float Distance;     // along the ray
        public ZombieLimb Limb;
        public bool Hit;
    }

    /// <summary>
    /// The maths that replaces the physics raycast.
    ///
    /// In the old system "what did this bullet hit" was a Godot ray against colliders, which is why every
    /// shootable zombie needed a live body, which is why every zombie paid for a swept move. Here a
    /// zombie is a capsule described by its kind record, and a bullet is a ray tested against it
    /// analytically. Nothing about being shootable touches the physics engine, so a zombie is equally
    /// shootable at 5 m and 500 m, in any tier, with or without a rig (plan section 5).
    /// </summary>
    public static class ZombieCombat
    {
        /// <summary>Ray against a vertical capsule standing at <paramref name="foot"/>. Returns the
        /// distance along the ray to the entry point, or -1 for a miss. <paramref name="dir"/> must be
        /// normalised.</summary>
        public static float RayCapsule(Vector3 origin, Vector3 dir, Vector3 foot, float radius, float height)
        {
            // The capsule's axis runs between the two sphere centres, which are inset by the radius.
            float span = MathF.Max(0f, height - 2f * radius);
            Vector3 a = new Vector3(foot.x, foot.y + radius, foot.z);
            Vector3 b = new Vector3(foot.x, foot.y + radius + span, foot.z);

            Vector3 ba = b - a;
            Vector3 oa = origin - a;
            float baba = Vector3.Dot(ba, ba);
            float bard = Vector3.Dot(ba, dir);
            float baoa = Vector3.Dot(ba, oa);
            float rdoa = Vector3.Dot(dir, oa);
            float oaoa = Vector3.Dot(oa, oa);

            float qa = baba - bard * bard;
            float qb = baba * rdoa - baoa * bard;
            float qc = baba * oaoa - baoa * baoa - radius * radius * baba;
            float disc = qb * qb - qa * qc;
            if (disc < 0f) return -1f;

            float sq = MathF.Sqrt(disc);

            if (MathF.Abs(qa) > 1e-9f)
            {
                float t = (-qb - sq) / qa;
                float y = baoa + t * bard;
                if (y > 0f && y < baba) return t >= 0f ? t : -1f;      // through the cylindrical body

                // Past an end: fall through to whichever hemisphere the ray is aimed at.
                Vector3 oc = y <= 0f ? oa : origin - b;
                float cb = Vector3.Dot(dir, oc);
                float cc = Vector3.Dot(oc, oc) - radius * radius;
                float ch = cb * cb - cc;
                if (ch <= 0f) return -1f;
                float tc = -cb - MathF.Sqrt(ch);
                return tc >= 0f ? tc : -1f;
            }

            // Ray parallel to the capsule's axis (straight up or down at it): only the caps can be hit.
            {
                Vector3 oc = bard > 0f ? oa : origin - b;
                float cb = Vector3.Dot(dir, oc);
                float cc = Vector3.Dot(oc, oc) - radius * radius;
                float ch = cb * cb - cc;
                if (ch <= 0f) return -1f;
                float tc = -cb - MathF.Sqrt(ch);
                return tc >= 0f ? tc : -1f;
            }
        }

        // PER-LIMB zombie damage multipliers (strawberry 2026-08-15: "separate multipliers for each zombie body
        // part"). Before this, DamageHit applied a FLAT amount and IsHeadshot only tinted the hitmarker -- a
        // zombie headshot dealt exactly what a shin did.
        //
        // Sized against the design goal ("almost all guns should 1 shot headshot zombies, take considerably more
        // shots on the body, even more on limbs") and a 100 HP zombie: at Skull 5.0 anything from 20 damage up
        // one-shots a head (5.56 = 20 -> 100). Pistol cartridges do not -- 9x19 at 12 gives 60, so two. Raising
        // Skull to 8.5 would pull the pistols in; that is a design call, not a derivation.
        public const float SkullMult = 5.0f;
        public const float SpineMult = 1.0f;
        public const float LegMult = 0.5f;

        public static float MultFor(ZombieLimb limb)
            => limb == ZombieLimb.Skull ? SkullMult : limb == ZombieLimb.Spine ? SpineMult : LegMult;

        /// <summary>Which limb a world-space point corresponds to on a zombie standing at
        /// <paramref name="foot"/>. Skull is the top 18%, matching the old controller's headshot rule
        /// (worldPoint.Y - position.Y > height * 0.82).</summary>
        public static ZombieLimb LimbAt(Vector3 point, Vector3 foot, float height)
        {
            float up = point.y - foot.y;
            if (up > height * 0.82f) return ZombieLimb.Skull;
            if (up > height * 0.45f) return ZombieLimb.Spine;
            return ZombieLimb.Leg;
        }
    }
}
