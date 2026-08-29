using System;
using System.Collections.Generic;

namespace UnturnedSim
{
    /// <summary>Is a point under cover? strawberry_cow: "make floors/roofs occlude rain. rain doesnt exist,
    /// but just add the necessary framework."
    ///
    /// FRAMEWORK ONLY -- this answers the question and spawns nothing. Rain today is a screen-space overlay
    /// (RainOverlay), and world rain does not exist; whatever eventually falls should ask this rather than
    /// grow its own idea of what a roof is.
    ///
    /// IT ASKS GEOMETRY, NOT SurfaceKind. Kind is documented in this codebase as "defaults and labels only --
    /// Rebuild never reads it", and a surface's ability to keep rain off is a fact about which way it faces:
    /// a wall laid flat shelters whatever is under it whatever it is called, and a "floor" stood on its edge
    /// shelters nothing. Keying on Kind would let a mislabelled surface silently stop working.
    ///
    /// OPENINGS COUNT. A stairwell in a floor, or a skylight, is a hole rain comes through -- and openings
    /// are the reason this cannot be an AABB test.</summary>
    public static class RainShelter
    {
        /// <summary>Below this, a surface is edge-on to falling rain and its horizontal footprint is a line.
        /// It is the same quantity the plane intersection divides by, so one constant governs "does not
        /// occlude" and "the maths would blow up" -- two thresholds here would eventually disagree and the
        /// disagreement would be a divide by nearly zero.</summary>
        public const float MinFacing = 1e-3f;

        /// <summary>A surface's local axes and normal in world space, from its yaw and pitch.
        ///
        /// Mirrors the engine's own transform rather than inventing one: surfaces are spawned with
        /// RotationDegrees = (pitch, yaw, 0) and Godot composes Euler YXZ, so the basis is Ry(yaw)*Rx(pitch)
        /// and UVToWorld(u,v) is Position + basis*(u,v,0). Derived rather than copied, and then checked
        /// against the engine in an in-engine test -- because a second copy of a transform is exactly the
        /// kind of thing that agrees today and drifts in a month.</summary>
        public static void Frame(float yawDeg, float pitchDeg,
                                 out (float X, float Y, float Z) ax,
                                 out (float X, float Y, float Z) ay,
                                 out (float X, float Y, float Z) n)
        {
            float a = yawDeg * (float)Math.PI / 180f, p = pitchDeg * (float)Math.PI / 180f;
            float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
            float cp = (float)Math.Cos(p), sp = (float)Math.Sin(p);

            ax = (ca, 0f, -sa);                       // Rx leaves the X axis alone
            ay = (sp * sa, cp, sp * ca);
            n  = (cp * sa, -sp, cp * ca);
        }

        /// <summary>Does this surface block rain falling straight down at all?</summary>
        public static bool Occludes(WallPlan p)
        {
            if (p == null) return false;
            Frame(p.Yaw, p.Pitch, out _, out _, out var n);
            return Math.Abs(n.Y) > MinFacing;
        }

        /// <summary>Where a straight-up ray from (x,y,z) meets this surface, if it does.
        ///
        /// Returns the height of the cover. Strictly ABOVE the query point: the floor you are standing on is
        /// not what keeps the rain off you, and counting it would make every outdoor spot on a floor slab
        /// report as sheltered -- which is every spot a player can stand.</summary>
        public static bool CoverHeight(WallPlan s, float x, float y, float z, out float coverY)
        {
            coverY = 0f;
            if (s == null) return false;
            Frame(s.Yaw, s.Pitch, out var ax, out var ay, out var n);
            if (Math.Abs(n.Y) <= MinFacing) return false;             // edge-on: no footprint to hide under

            // Vertical ray vs the surface's plane.
            float dx = s.X - x, dy = s.Y - y, dz = s.Z - z;
            float t = (dx * n.X + dy * n.Y + dz * n.Z) / n.Y;
            if (t <= 0f) return false;                                // at or below the query point

            // The hit, in the surface's own (u,v).
            float hx = x - s.X, hy = y + t - s.Y, hz = z - s.Z;
            float u = hx * ax.X + hy * ax.Y + hz * ax.Z;
            float v = hx * ay.X + hy * ay.Y + hz * ay.Z;

            if (v < -WallOpenings.Eps || v > s.Height + WallOpenings.Eps) return false;

            // TRAPEZOID EDGES, not the bounding rectangle. A hip end and a cross-wing valley are genuine
            // trapezoids, and sheltering out to their bounding box would put a dry strip in mid-air beside
            // every hip roof in the game.
            float f = s.Height > 1e-4f ? v / s.Height : 0f;
            float left = s.InsetL0 + (s.InsetL1 - s.InsetL0) * f;
            float right = s.Length - (s.InsetR0 + (s.InsetR1 - s.InsetR0) * f);
            if (u < left - WallOpenings.Eps || u > right + WallOpenings.Eps) return false;

            // A hole is a hole: rain comes through a stairwell or a skylight. But an opening is only a hole
            // when nothing is filling it -- an intact window keeps the weather out, which is what a window
            // is FOR, and a shut door does the same. Both are already modelled, so this asks the existing
            // state rather than inventing a "lets rain through" flag that could disagree with what is drawn.
            if (LetsRainThrough(s.Openings, u, v)) return false;

            coverY = y + t;
            return true;
        }

        /// <summary>Is (u,v) inside an opening that rain can actually get through?
        ///
        /// Kept private: nothing else needs point-in-opening yet, and a shared helper with one caller is a
        /// guess about the future. Promote it to WallOpenings the moment there is a second.</summary>
        static bool LetsRainThrough(IReadOnlyList<WallOpening> openings, float u, float v)
        {
            if (openings == null) return false;
            foreach (var o in openings)
            {
                if (u < o.U || u > o.U + o.Width || v < o.V || v > o.V + o.Height) continue;
                if (o.HasGlass) continue;                                   // intact glass keeps rain out
                if (o.DoorProp != null && !o.DoorOpen) continue;            // so does a shut door
                return true;
            }
            return false;
        }

        /// <summary>The LOWEST cover above a point, across every surface. That is the ceiling you are under,
        /// and it is the one a falling drop meets first -- which matters the moment anything wants to know
        /// WHERE to stop a drop rather than merely whether to.</summary>
        public static bool CoverAbove(IReadOnlyList<WallPlan> plans, float x, float y, float z, out float coverY)
        {
            coverY = 0f;
            if (plans == null) return false;
            bool any = false;
            foreach (var s in plans)
            {
                if (!CoverHeight(s, x, y, z, out float c)) continue;
                if (!any || c < coverY) { coverY = c; any = true; }
            }
            return any;
        }

        public static bool IsSheltered(IReadOnlyList<WallPlan> plans, float x, float y, float z)
            => CoverAbove(plans, x, y, z, out _);
    }
}
