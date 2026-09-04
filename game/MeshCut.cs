using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>Triangle-level mesh surgery for load-time edits (the bus bi-fold door): plane splits with attribute
    /// interpolation, box subtraction, convex caps over a cut, plain quads. Works on surface 0 of a mesh; normals,
    /// UVs and colours ride along (tangents are dropped).</summary>
    public static class MeshCut
    {
        public struct V { public Vector3 P, N; public Vector2 T; public Color C; }
        public struct Tri { public V A, B, C; }
        public sealed class Set { public List<Tri> Tris = new(); public bool HasN, HasT, HasC; }

        public static Set Read(Mesh m)
        {
            var set = new Set();
            if (m == null || m.GetSurfaceCount() == 0) return set;
            var arrays = m.SurfaceGetArrays(0);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var nv = arrays[(int)Mesh.ArrayType.Normal]; var nrm = nv.VariantType != Variant.Type.Nil ? nv.AsVector3Array() : null;
            var uvv = arrays[(int)Mesh.ArrayType.TexUV]; var uv = uvv.VariantType != Variant.Type.Nil ? uvv.AsVector2Array() : null;
            var cv = arrays[(int)Mesh.ArrayType.Color]; var col = cv.VariantType != Variant.Type.Nil ? cv.AsColorArray() : null;
            if (nrm != null && nrm.Length != verts.Length) nrm = null;
            if (uv != null && uv.Length != verts.Length) uv = null;
            if (col != null && col.Length != verts.Length) col = null;
            set.HasN = nrm != null; set.HasT = uv != null; set.HasC = col != null;
            var idxVar = arrays[(int)Mesh.ArrayType.Index];
            int[] idx;
            if (idxVar.VariantType != Variant.Type.Nil) idx = idxVar.AsInt32Array();
            else { idx = new int[verts.Length]; for (int i = 0; i < idx.Length; i++) idx[i] = i; }
            V At(int i) => new V { P = verts[i], N = nrm != null ? nrm[i] : Vector3.Up, T = uv != null ? uv[i] : Vector2.Zero, C = col != null ? col[i] : Colors.White };
            for (int t = 0; t + 2 < idx.Length; t += 3) set.Tris.Add(new Tri { A = At(idx[t]), B = At(idx[t + 1]), C = At(idx[t + 2]) });
            return set;
        }

        public static ArrayMesh Commit(Set set)
        {
            if (set == null || set.Tris.Count == 0) return null;
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var tr in set.Tris)
                foreach (var q in new[] { tr.A, tr.B, tr.C })
                {
                    if (set.HasN) st.SetNormal(q.N);
                    if (set.HasT) st.SetUV(q.T);
                    if (set.HasC) st.SetColor(q.C);
                    st.AddVertex(q.P);
                }
            if (!set.HasN) st.GenerateNormals();
            return st.Commit();
        }

        static float Axis(Vector3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
        static V Lerp(V a, V b, float f) => new V { P = a.P.Lerp(b.P, f), N = a.N.Lerp(b.N, f).Normalized(), T = a.T.Lerp(b.T, f), C = a.C.Lerp(b.C, f) };

        /// <summary>Split one triangle by the plane `axis = value` (Sutherland-Hodgman both ways): the parts on the low
        /// side go to `below`, the rest to `above`; the points created ON the plane are reported through `cut`.</summary>
        public static void SplitTri(Tri tr, int axis, float value, List<Tri> below, List<Tri> above, List<V> cut)
        {
            var src = new[] { tr.A, tr.B, tr.C };
            for (int side = 0; side < 2; side++)
            {
                bool low = side == 0;
                var poly = new List<V>(5);
                for (int k = 0; k < 3; k++)
                {
                    var a = src[k]; var b = src[(k + 1) % 3];
                    float da = Axis(a.P, axis) - value, db = Axis(b.P, axis) - value;
                    bool ina = low ? da <= 0f : da >= 0f, inb = low ? db <= 0f : db >= 0f;
                    if (ina) poly.Add(a);
                    if (ina != inb)
                    {
                        var m = Lerp(a, b, Mathf.Clamp(da / (da - db), 0f, 1f));
                        poly.Add(m);
                        if (low && cut != null) cut.Add(m);   // each crossing once
                    }
                }
                if (poly.Count < 3) continue;
                var dst = low ? below : above;
                for (int k = 1; k + 1 < poly.Count; k++) dst.Add(new Tri { A = poly[0], B = poly[k], C = poly[k + 1] });   // fan keeps the source winding
            }
        }

        public static (Set below, Set above) Split(Set src, int axis, float value, List<V> cut)
        {
            var lo = new Set { HasN = src.HasN, HasT = src.HasT, HasC = src.HasC };
            var hi = new Set { HasN = src.HasN, HasT = src.HasT, HasC = src.HasC };
            foreach (var tr in src.Tris) SplitTri(tr, axis, value, lo.Tris, hi.Tris, cut);
            return (lo, hi);
        }

        /// <summary>Everything OUTSIDE the box stays (triangles straddling it are clipped); what is inside is dropped,
        /// its vertices reported through `dropped` so a caller can reuse their UV/colour.</summary>
        public static Set SubtractBox(Set src, Aabb box, List<V> dropped)
        {
            var kept = new Set { HasN = src.HasN, HasT = src.HasT, HasC = src.HasC };
            var lo = box.Position; var hi = box.End;
            foreach (var tr in src.Tris)
            {
                bool Out(int ax, float min, float max)
                {
                    float a = Axis(tr.A.P, ax), b = Axis(tr.B.P, ax), c = Axis(tr.C.P, ax);
                    return (a <= min && b <= min && c <= min) || (a >= max && b >= max && c >= max);
                }
                if (Out(0, lo.X, hi.X) || Out(1, lo.Y, hi.Y) || Out(2, lo.Z, hi.Z)) { kept.Tris.Add(tr); continue; }
                var inside = new List<Tri> { tr };
                for (int ax = 0; ax < 3; ax++)
                {
                    float min = Axis(lo, ax), max = Axis(hi, ax);
                    var next = new List<Tri>();
                    foreach (var part in inside)
                    {
                        var below = new List<Tri>(); var above = new List<Tri>();
                        SplitTri(part, ax, min, below, above, null);   // below the box's min on this axis = outside
                        kept.Tris.AddRange(below);
                        foreach (var p2 in above)
                        {
                            var b2 = new List<Tri>(); var a2 = new List<Tri>();
                            SplitTri(p2, ax, max, b2, a2, null);      // above its max = outside
                            kept.Tris.AddRange(a2);
                            next.AddRange(b2);
                        }
                    }
                    inside = next;
                }
                if (dropped != null) foreach (var d in inside) { dropped.Add(d.A); dropped.Add(d.B); dropped.Add(d.C); }
            }
            return kept;
        }

        /// <summary>Close a planar cut: the convex hull of the cut points (in the plane `axis = const`) as a fan with the
        /// given normal, one UV/colour for the whole cap.</summary>
        public static void CapHull(Set dst, List<V> cut, int axis, Vector3 normal, Vector2 uv, Color col)
        {
            if (cut == null || cut.Count < 3) return;
            int u = axis == 0 ? 1 : 0, w = axis == 2 ? 1 : 2;   // the two in-plane axes
            var pts = new List<Vector3>();
            foreach (var c in cut)
            {
                bool dup = false;
                foreach (var q in pts) if (q.DistanceSquaredTo(c.P) < 1e-6f) { dup = true; break; }
                if (!dup) pts.Add(c.P);
            }
            if (pts.Count < 3) return;
            pts.Sort((a, b) => { int r = Axis(a, u).CompareTo(Axis(b, u)); return r != 0 ? r : Axis(a, w).CompareTo(Axis(b, w)); });
            float Cross(Vector3 o, Vector3 a, Vector3 b) => (Axis(a, u) - Axis(o, u)) * (Axis(b, w) - Axis(o, w)) - (Axis(a, w) - Axis(o, w)) * (Axis(b, u) - Axis(o, u));
            var hull = new List<Vector3>();   // Andrew's monotone chain
            foreach (var p in pts) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0f) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
            int lowerN = hull.Count + 1;
            for (int i = pts.Count - 2; i >= 0; i--) { var p = pts[i]; while (hull.Count >= lowerN && Cross(hull[^2], hull[^1], p) <= 0f) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
            hull.RemoveAt(hull.Count - 1);
            if (hull.Count < 3) return;
            V Mk(Vector3 p) => new V { P = p, N = normal, T = uv, C = col };
            for (int k = 1; k + 1 < hull.Count; k++) dst.Tris.Add(new Tri { A = Mk(hull[0]), B = Mk(hull[k]), C = Mk(hull[k + 1]) });
        }

        public static void Quad(Set dst, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 uv, Color col)
        {
            V Mk(Vector3 p) => new V { P = p, N = normal, T = uv, C = col };
            dst.Tris.Add(new Tri { A = Mk(a), B = Mk(b), C = Mk(c) });
            dst.Tris.Add(new Tri { A = Mk(a), B = Mk(c), C = Mk(d) });
        }
    }
}
