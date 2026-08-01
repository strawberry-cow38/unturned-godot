using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>
    /// Small scene/geometry helpers that had grown a copy per consumer. See docs/DUPLICATE_AUDIT.md
    /// 1.5 / 1.6 / 1.9 — four byte-identical <c>RotateYTo</c>, three <c>CollectMeshes</c>, and three
    /// polyline-length sums lived across the link and focusable node types.
    ///
    /// Deliberately NOT in here: <c>RoadField.SegLength</c>. That looks like a polyline sum and is a
    /// bezier arc-length ESTIMATE — it samples <c>SplinePos</c> at 16 steps and sums the chords of a
    /// curve, and <c>RoadField</c>'s other distance accumulator is UV distance for texture repeat.
    /// Different maths wearing a similar shape; folding them in here would be the mistake this file
    /// exists to stop.
    /// </summary>
    public static class NodeGeometry
    {
        /// <summary>Orthonormal rotation mapping the mesh's local +Y onto the unit direction <paramref name="u"/>.
        /// Used by every segment-along-a-line node (wire, hose, tow rope) and by the port flow arrows, all of
        /// which model a span as a unit-Y cylinder/quad scaled to length.</summary>
        /// <param name="u">A UNIT direction. The ±0.9999 guards catch the degenerate parallel/antiparallel
        /// cases where the cross product is not a usable axis.</param>
        public static Basis RotateYTo(Vector3 u)
        {
            float d = Vector3.Up.Dot(u);
            if (d > 0.9999f) return Basis.Identity;
            if (d < -0.9999f) return new Basis(Vector3.Right, Mathf.Pi);
            return new Basis(Vector3.Up.Cross(u).Normalized(), Mathf.Acos(Mathf.Clamp(d, -1f, 1f)));
        }

        /// <summary>Depth-first collect of every <see cref="MeshInstance3D"/> under <paramref name="n"/>,
        /// appended to <paramref name="list"/>. What the look-focus outline layer is applied to, so it
        /// deliberately takes ALL meshes — a vehicle's seats and steering wheel are part of the one
        /// combined silhouette, not separate outlines.</summary>
        public static void CollectMeshes(Node n, List<MeshInstance3D> list)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is MeshInstance3D mi) list.Add(mi);
                CollectMeshes(c, list);
            }
        }

        /// <summary>Summed length of a STRAIGHT-segment polyline, in metres. Fewer than two points is 0.
        /// This is a chord sum over stored points — it is not an arc-length estimate, see the note above.</summary>
        public static float PolylineLength(IReadOnlyList<Vector3> points)
        {
            if (points == null || points.Count < 2) return 0f;
            float total = 0f;
            for (int i = 1; i < points.Count; i++) total += points[i].DistanceTo(points[i - 1]);
            return total;
        }
    }
}
