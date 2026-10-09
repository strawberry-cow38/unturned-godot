using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>Sweep a tunnel's own cross-section along a spline, as one continuous mesh.
    ///
    /// ⭐⭐ WHY NOT TILE IT, when the fence, rail and bridge all do. Master, on the tiled first version:
    /// "theres gaps and probably overlaps inside the tunnel. you need cuts/blending." They are right, and it
    /// is structural rather than a tuning miss: a rigid section has FLAT PARALLEL ENDS, so on a curve one of
    /// its surfaces gaps and the other overlaps, always. The joint overlap that fixes a bridge deck is sized
    /// for the section's outer half-width (12 m), but a tunnel's bore wall is at 8 m -- so closing the shell
    /// over-closes the bore by (12-8)*turn and leaves a lip every joint on the one surface you stand inside.
    /// No single overlap can settle both walls at once, because they sit at different radii.
    ///
    /// A sweep has no joints to mismatch. Consecutive rings SHARE their vertices, so the miter at every
    /// station is exact by construction at any radius -- and it is cheaper too, one mesh against the dozens
    /// of props tiling needed.
    ///
    /// ⚠ This is for HOLLOW sections only. The fence, rail and bridge keep tiling: their overlap is buried
    /// inside solid geometry, so they genuinely do not have this problem.</summary>
    public static class TunnelMesh
    {
        /// <summary>The section's open chains in its end plane, as (x, height) pairs.
        ///
        /// ⭐ READ OFF THE SHIPPED PROP, not retyped. Tunnel_Line_0 is a pure prism, so every edge lying in
        /// an end plane bounds exactly one wall face and the whole set IS the profile. It comes out as two
        /// OPEN arches -- the bore and the outer shell, both floorless, which is why the road stays drawn.</summary>
        public static List<Vector2[]> ProfileFrom(ArrayMesh src)
        {
            if (src == null || src.GetSurfaceCount() < 1) return null;
            var a0 = src.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (V.Length < 3) return null;
            float ylo = float.MaxValue;
            foreach (var v in V) ylo = Mathf.Min(ylo, v.Y);

            // weld by position, then collect the edges that lie in the end plane
            var key = new Dictionary<Vector3, int>();
            var pos = new List<Vector3>();
            var rep = new int[V.Length];
            for (int i = 0; i < V.Length; i++)
            {
                var k = new Vector3(Mathf.Round(V[i].X * 1000f) / 1000f, Mathf.Round(V[i].Y * 1000f) / 1000f,
                                    Mathf.Round(V[i].Z * 1000f) / 1000f);
                if (!key.TryGetValue(k, out int id)) { id = pos.Count; key[k] = id; pos.Add(k); }
                rep[i] = id;
            }
            var adj = new Dictionary<int, List<int>>();
            void Link(int a, int b)
            {
                if (a == b) return;
                if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<int>();
                if (!la.Contains(b)) la.Add(b);
            }
            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                int[] r = { rep[i], rep[i + 1], rep[i + 2] };
                for (int k = 0; k < 3; k++)
                {
                    int a = r[k], b = r[(k + 1) % 3];
                    if (Mathf.Abs(pos[a].Y - ylo) < 1e-2f && Mathf.Abs(pos[b].Y - ylo) < 1e-2f) { Link(a, b); Link(b, a); }
                }
            }
            if (adj.Count == 0) return null;

            // ⚠ WALK FROM THE ENDPOINTS. Both chains are OPEN (a tunnel arch has no floor), so starting
            // anywhere and stopping when the walk runs out splits one arch into two fragments -- which is
            // exactly what a first pass that assumed closed loops did, reporting three "loops" for two walls.
            var outp = new List<Vector2[]>();
            var used = new HashSet<int>();
            var starts = new List<int>();
            foreach (var kv in adj) if (kv.Value.Count == 1) starts.Add(kv.Key);
            foreach (var kv in adj) if (kv.Value.Count != 1) starts.Add(kv.Key);   // closed chains, if any
            foreach (var st in starts)
            {
                if (used.Contains(st)) continue;
                var chain = new List<int> { st };
                used.Add(st);
                int prev = -1, cur = st;
                while (true)
                {
                    int nxt = -1;
                    foreach (var w in adj[cur]) if (w != prev && !used.Contains(w)) { nxt = w; break; }
                    if (nxt < 0) break;
                    chain.Add(nxt); used.Add(nxt); prev = cur; cur = nxt;
                }
                if (chain.Count < 2) continue;
                var pts = new Vector2[chain.Count];
                for (int i = 0; i < chain.Count; i++) pts[i] = new Vector2(pos[chain[i]].X, pos[chain[i]].Z);
                outp.Add(pts);
            }
            return outp.Count > 0 ? outp : null;
        }

        /// <summary>Sweep the profile through a run of centreline stations.
        ///
        /// Each station contributes one ring; consecutive rings are stitched into quads that SHARE their
        /// vertices, which is the whole point -- there is no joint to open or bury. `up` is world up and the
        /// lateral axis is derived per station, so the tube banks with the road's grade without twisting.</summary>
        /// <summary>The authored bore's half-width: the INNER chain's lateral reach, taken as whichever of
        /// the two arches is narrower rather than assumed to be first in the list.</summary>
        public static float BoreHalfWidth(List<Vector2[]> profile)
        {
            if (profile == null || profile.Count == 0) return 0f;
            float inner = float.MaxValue;
            foreach (var ch in profile)
            {
                float w = 0f;
                foreach (var p in ch) w = Mathf.Max(w, Mathf.Abs(p.X));
                inner = Mathf.Min(inner, w);
            }
            return inner;
        }

        public static ArrayMesh Sweep(List<Vector2[]> profile, IReadOnlyList<Vector3> centre, float lateral = 1f)
        {
            if (profile == null || centre == null || centre.Count < 2) return null;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            var frames = new Basis[centre.Count];
            for (int i = 0; i < centre.Count; i++)
            {
                var fwd = (i == 0 ? centre[1] - centre[0]
                         : i == centre.Count - 1 ? centre[i] - centre[i - 1]
                         : centre[i + 1] - centre[i - 1]).Normalized();
                if (fwd.LengthSquared() < 1e-8f) fwd = Vector3.Forward;
                var right = Vector3.Up.Cross(fwd);
                if (right.LengthSquared() < 1e-8f) right = Vector3.Right;
                right = right.Normalized();
                frames[i] = new Basis(right, right.Cross(fwd).Normalized() * -1f, fwd);
            }
            // ⚠ WIDENED LATERALLY ONLY. Master: "make the tunnel wider to fit the whole road spline + a
            // small border." Scaling the height with it would make a wide carriageway's tunnel absurdly
            // tall; a road tunnel gets wider, not proportionally bigger, so the arch goes elliptical.
            Vector3 At(int i, Vector2 p) => centre[i] + frames[i].X * (p.X * lateral) + Vector3.Up * p.Y;

            float runLen = 0f;
            var sAlong = new float[centre.Count];
            for (int i = 1; i < centre.Count; i++) { runLen += centre[i].DistanceTo(centre[i - 1]); sAlong[i] = runLen; }

            foreach (var chain in profile)
            {
                // arc length across the profile, so a future texture maps evenly rather than per-segment
                var u = new float[chain.Length];
                for (int k = 1; k < chain.Length; k++) u[k] = u[k - 1] + chain[k].DistanceTo(chain[k - 1]);
                float uLen = Mathf.Max(u[chain.Length - 1], 1e-4f);

                for (int i = 0; i + 1 < centre.Count; i++)
                    for (int k = 0; k + 1 < chain.Length; k++)
                    {
                        Vector3 a = At(i, chain[k]), b = At(i, chain[k + 1]);
                        Vector3 c = At(i + 1, chain[k + 1]), d = At(i + 1, chain[k]);
                        var n = (b - a).Cross(d - a);
                        n = n.LengthSquared() < 1e-12f ? Vector3.Up : n.Normalized();

                        // ⚠⚠ ORIENTED AWAY FROM THE CENTRELINE, winding and all. The profile chains come out
                        // of the mesh in whatever direction the walk happened to take, so a cross product
                        // gives an arbitrary side -- and the first sweep rendered the whole bore DARK because
                        // the shell's normals faced into the hillside and no light ever reached them. Under
                        // cull_disabled Godot flips the normal for BACK faces, so it is not enough to flip
                        // the normal alone: the exterior has to be the front face. Flip both together.
                        var mid = (a + b + c + d) * 0.25f;
                        var outRef = mid - (centre[i] + centre[i + 1]) * 0.5f;
                        bool flip = outRef.LengthSquared() > 1e-8f && n.Dot(outRef) < 0f;
                        if (flip) n = -n;

                        // ⚠⚠ WHITE VERTEX COLOURS, because the material MULTIPLIES BY THEM. Every prop mesh
                        // here comes through ObjMesh.Load, which writes a white COLOR array, and MatFor sets
                        // VertexColorUseAsAlbedo = true -- so a generated mesh with no colour array is
                        // albedo x nothing, and the whole bore renders near-black. Three fixes went past this
                        // (the duplicated winding, the normal direction, the material itself) because the
                        // diagnostic compared against ContentProvider.ParseObj, which does NOT write colours,
                        // rather than against the loader the props actually use.
                        // [[feedback_an_instrument_must_name_what_it_measured]]
                        // ⚠ NO SetNormal HERE. Six attempts at hand-authored normals ended with the project's
                        // own normal_probe painting the whole tube BLACK, roof included, while the
                        // construction read correctly on paper. Winding is the thing this code can be sure
                        // of -- the flip above makes every quad face away from the centreline -- so the
                        // normals are generated from it below instead of asserted here.
                        void Put(Vector3 p, float uu, float vv)
                        { st.SetColor(Colors.White); st.SetUV(new Vector2(uu, vv)); st.AddVertex(p); }
                        float v0 = sAlong[i], v1 = sAlong[i + 1];
                        if (flip)
                        {
                            Put(a, u[k] / uLen, v0); Put(c, u[k + 1] / uLen, v1); Put(b, u[k + 1] / uLen, v0);
                            Put(a, u[k] / uLen, v0); Put(d, u[k] / uLen, v1); Put(c, u[k + 1] / uLen, v1);
                        }
                        else
                        {
                            Put(a, u[k] / uLen, v0); Put(b, u[k + 1] / uLen, v0); Put(c, u[k + 1] / uLen, v1);
                            Put(a, u[k] / uLen, v0); Put(c, u[k + 1] / uLen, v1); Put(d, u[k] / uLen, v1);
                        }
                        // ⚠⚠ ONE WINDING ONLY, and the MATERIAL draws both sides. A tunnel is seen from
                        // inside and from the hillside, so the first version emitted a reversed copy of every
                        // triangle as well -- on a CullMode.Disabled material that is two COINCIDENT faces
                        // with opposite normals, which z-fight and shade the whole bore dark. Exactly the
                        // defect I had just finished diagnosing on the rail's end caps, reintroduced by hand.
                        // Under cull_disabled Godot flips the normal for back faces itself, so one winding
                        // is both correct and enough. See [[feedback_godot_mirror_front_facing_trap]].
                    }
            }
            st.GenerateNormals();   // from the winding, which the per-quad flip above has made consistent
            return st.Commit();
        }
    }
}
