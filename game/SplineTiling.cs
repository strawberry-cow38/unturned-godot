using Godot;

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
        public static float OverlapFor(float halfWidth, float pitch, float turn)
            => Mathf.Min(halfWidth * Mathf.Abs(turn), pitch * 0.5f);
    }
}
