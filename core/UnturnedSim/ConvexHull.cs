using System;
using System.Collections.Generic;
using System.Numerics;

namespace UnturnedSim
{
    /// <summary>The faces of the convex hull of a handful of points.
    ///
    /// Written for a plumb-cut roof slab (RoofShapes.PlumbFaces), whose two faces are different polygons and so cannot
    /// be joined by pairing corners blindly. The mesh and the collider are both built from what this returns, so the
    /// roof you see and the roof you walk on are the same solid -- the property WallSurface's partition exists to keep.
    ///
    /// Brute force over every triple of points: O(n^4), which is nothing at the ~16 points a slab has and would be the
    /// wrong tool at a few hundred.</summary>
    public static class ConvexHull
    {
        public readonly struct Face
        {
            /// <summary>Unit, pointing out of the solid. Every point p of the hull has Normal.p &lt;= D.</summary>
            public readonly Vector3 Normal;
            public readonly float D;
            /// <summary>The face's corners as indices into the points passed in, anticlockwise seen from outside.</summary>
            public readonly int[] Loop;
            public Face(Vector3 normal, float d, int[] loop) { Normal = normal; D = d; Loop = loop; }
        }

        /// <summary>The hull's faces. Points within `eps` of one another count as one (the first one's index is used);
        /// a face is kept only if it has real area, so the supporting planes that merely graze an edge are dropped.
        /// Empty for fewer than four points not all in one plane.</summary>
        public static List<Face> Faces(IReadOnlyList<Vector3> points, float eps = 1e-4f)
        {
            var faces = new List<Face>();
            var ids = new List<int>();
            for (int i = 0; i < points.Count; i++)
            {
                bool dup = false;
                foreach (int j in ids)
                    if (Vector3.DistanceSquared(points[i], points[j]) < eps * eps) { dup = true; break; }
                if (!dup) ids.Add(i);
            }
            int n = ids.Count;
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                    for (int c = b + 1; c < n; c++)
                    {
                        Vector3 pa = points[ids[a]], pb = points[ids[b]], pc = points[ids[c]];
                        var nrm = Vector3.Cross(pb - pa, pc - pa);
                        float len = nrm.Length();
                        if (len < 1e-8f) continue;
                        nrm /= len;
                        float d = Vector3.Dot(nrm, pa), hi = float.MinValue, lo = float.MaxValue;
                        foreach (int k in ids)
                        {
                            float s = Vector3.Dot(nrm, points[k]) - d;
                            hi = MathF.Max(hi, s); lo = MathF.Min(lo, s);
                        }
                        if (hi > eps && lo < -eps) continue;          // points on both sides: not a face
                        if (hi > eps) { nrm = -nrm; d = -d; }        // everything below the plane, seen from outside
                        bool seen = false;
                        foreach (var f in faces)
                            if (Vector3.Dot(f.Normal, nrm) > 1f - 1e-5f && MathF.Abs(f.D - d) < eps * 10f) { seen = true; break; }
                        if (seen) continue;
                        var on = new List<int>();
                        foreach (int k in ids)
                            if (MathF.Abs(Vector3.Dot(nrm, points[k]) - d) <= eps) on.Add(k);
                        var loop = Order(points, on, nrm);
                        if (loop != null) faces.Add(new Face(nrm, d, loop));
                    }
            return faces;
        }

        /// <summary>The points of one face, anticlockwise about its normal; null when they enclose no area (a plane that
        /// only touches the hull along an edge).</summary>
        static int[] Order(IReadOnlyList<Vector3> points, List<int> on, Vector3 normal)
        {
            if (on.Count < 3) return null;
            var c = Vector3.Zero;
            foreach (int k in on) c += points[k];
            c /= on.Count;
            var e1 = points[on[0]] - c;
            if (e1.LengthSquared() < 1e-12f) e1 = points[on[1]] - c;
            e1 = Vector3.Normalize(e1);
            var e2 = Vector3.Cross(normal, e1);
            on.Sort((i, j) =>
            {
                var pi = points[i] - c; var pj = points[j] - c;
                return MathF.Atan2(Vector3.Dot(pi, e2), Vector3.Dot(pi, e1)).CompareTo(MathF.Atan2(Vector3.Dot(pj, e2), Vector3.Dot(pj, e1)));
            });
            float area = 0f;
            for (int i = 1; i + 1 < on.Count; i++)
                area += Vector3.Dot(Vector3.Cross(points[on[i]] - points[on[0]], points[on[i + 1]] - points[on[0]]), normal);
            return area > 1e-6f ? on.ToArray() : null;
        }
    }
}
