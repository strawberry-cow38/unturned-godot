using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>⭐⭐ THE JOINT RULE EVERY TOOL THAT TILES A RIGID PROP ALONG A SPLINE NEEDS.
    ///
    /// Master 2026-10-09, on the first New Rail render: "nice but you can see a seam at each chunk". Two
    /// rigid tiles meeting at a turn of θ = pitch/R have FLAT PARALLEL ENDS, so their end planes splay about
    /// the centreline: the outer corners part by halfWidth*θ and the inner ones overlap by the same. On the
    /// showcase's 203 m bend that was 3.4 cm of open slot at every joint. No exporter can fix it -- a
    /// flat-ended tile cannot follow an arc.
    ///
    /// ⚠ IT IS NOT A SMALL EFFECT AT WIDTH. It scales with halfWidth TIMES pitch, so the 6.9 m-wide, 2 m rail
    /// unit opens 3.4 cm where a 17 m-wide, 48 m bridge span opens 2.04 m at R=200. That is why the bridge
    /// spans had to be cut into short deck units before they could follow a road at all.
    ///
    /// ⭐ SHARED ON PURPOSE. This lived inside EditorRailSpline until the bridge tool needed exactly the same
    /// rule with a different width; a second copy is how the two drift apart and only one of them gets the
    /// next fix. See [[reference_unturned_spline_tiling_wedge]].</summary>
    public static class SplineTiling
    {
        /// <summary>How far to back a tile off ALONG ITS OWN AXIS so its outer corner lands on its
        /// neighbour's, for a joint turning `turn` radians.
        ///
        /// ⭐⭐ FREE ON A STRAIGHT, which is the property worth having: turn = 0 gives 0, so a straight run is
        /// placed exactly as it was before this existed -- and a dead-straight control render is what proved
        /// that case was already clean.
        ///
        /// The inner corners then bury 2*halfWidth*turn, which is invisible: solid inside solid. The tiles'
        /// top surfaces cross TRANSVERSALLY at θ rather than lying coplanar, so the overlap reads as a crease
        /// and not a z-fight -- under 0.2 mm of separation at the rail's showcase bend.
        ///
        /// ⚠ Clamped to half the pitch so a walk using it always advances. It only binds past
        /// turn ≈ pitch/(2*halfWidth), far tighter than either tool warns about.</summary>
        /// <summary>A smooth curve through clicked points -- Catmull-Rom-ish handles at a sixth of the
        /// neighbour span, or dead-straight segments when `smooth` is false. Lifted out of EditorFenceRoad so
        /// the block-barrier tool walks the SAME curve the guardrail does: two tools that lay props along a
        /// clicked path and disagree about what curve the path describes is a bug waiting for a map that uses
        /// both along one road.</summary>
        public static Curve3D CurveThrough(IReadOnlyList<Vector3> pts, bool smooth = true)
        {
            var c = new Curve3D();
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 inH = Vector3.Zero, outH = Vector3.Zero;
                if (smooth && pts.Count > 2)
                {
                    var prev = pts[Mathf.Max(i - 1, 0)];
                    var next = pts[Mathf.Min(i + 1, pts.Count - 1)];
                    var t = (next - prev) / 6f;
                    inH = -t; outH = t;
                }
                c.AddPoint(pts[i], inH, outH);
            }
            return c;
        }

        /// <summary>Stand a prop at `at` facing `yawDeg`, tilted onto the ground's slope but no further than
        /// `maxTiltDeg`. `rawTiltDeg` reports the slope it actually found, before the clamp, so a caller can
        /// tell "flat ground" from "a cliff we refused to follow".</summary>
        public static Basis SeatedBasis(Terrain terr, Vector3 at, float yawDeg, float maxTiltDeg,
                                        out float rawTiltDeg)
        {
            rawTiltDeg = 0f;
            var stand = EditorObjects.FromEuler(270f, yawDeg, 0f);
            if (terr == null) return stand;
            var nrm = terr.NormalAt(at.X, at.Z);
            var axis = Vector3.Up.Cross(nrm);
            if (axis.LengthSquared() < 1e-8f) return stand;
            float raw = Vector3.Up.AngleTo(nrm);
            rawTiltDeg = Mathf.RadToDeg(raw);
            return new Basis(axis.Normalized(), Mathf.Min(raw, Mathf.DegToRad(maxTiltDeg))) * stand;
        }

        public static float OverlapFor(float halfWidth, float pitch, float turn)
            => Mathf.Min(halfWidth * Mathf.Abs(turn), pitch * 0.5f);
    }
}
