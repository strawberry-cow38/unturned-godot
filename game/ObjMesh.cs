using Godot;
using System.Collections.Generic;
using System.Globalization;

namespace UnturnedGodot
{
    // Minimal runtime Wavefront .obj loader for the extracted Unturned object meshes
    // (UnityPy mesh.export() = raw Unity coords). Converts to Godot: negate Z + reverse
    // winding, matching the terrain's (x,y,z)->(x,y,-z) convention. UVs are V-flipped
    // (Unity V-up -> Godot V-down).
    public static partial class ObjMesh
    {
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        // diagnostic: UG_CONV env var switches the Unity->Godot mesh convention to hunt the mirror.
        //   0 = negZ + reverse winding (current)   1 = raw Unity (no negate, orig winding)
        //   2 = negX + reverse winding             3 = negZ + reverse winding + U-flip UV
        // Unity(LH)->Godot(RH): use RAW geometry (CONV 1). negate-Z (old CONV 0) reflected every mesh -> chiral
        // features (embossed text on signs) came out MIRRORED. Raw keeps chirality; CullMode.Disabled + explicit
        // normals handle winding/lighting. Placement (Main.cs) + terrain (Terrain.cs) match this (no Z-negate).
        static readonly int CONV = int.TryParse(System.Environment.GetEnvironmentVariable("UG_CONV"), out var _c) ? _c : 1;

        // Load cache: the launch WARMUP preloads every core (vanilla) mesh into here, so the world build reuses
        // them instead of re-parsing the .obj on first use (kills the first-use hitch). Keyed by absolute path.
        static readonly Dictionary<string, ArrayMesh> _cache = new();
        static readonly Dictionary<ArrayMesh, Vector3[]> _faces = new();   // the loader's own triangle list (3 verts/tri) -> collision shapes without a GetFaces() readback
        /// <summary>ConcavePolygonShape3D for a mesh this loader built, from the triangle list it already has in hand.
        /// mesh.CreateTrimeshShape() re-derives the same array through GetFaces() (a surface read-back): 295 unique
        /// props cost 658 ms of a PEI load that way. Falls back to the engine path for meshes it did not load.</summary>
        public static Shape3D TrimeshShape(ArrayMesh m)
            => m != null && _faces.TryGetValue(m, out var f) && f.Length >= 3 ? new ConcavePolygonShape3D { Data = f } : m?.CreateTrimeshShape();

        /// <summary>Forget a cached mesh so the next Load re-reads it from disk.
        ///
        /// Needed because a file on this path can CHANGE at runtime: the building tool bakes a prop and can
        /// bake it again under the same name. Clearing the caller's own cache is not enough -- this one is
        /// keyed by path with no expiry, so the second bake kept serving the first bake's geometry (and its
        /// collider) until a restart, which breaks the edit-bake-look loop on its second lap.</summary>
        public static void Forget(string globalPath)
        {
            _cache.Remove(globalPath);
            var drop = new List<ArrayMesh>();
            foreach (var kv in _lensCache) if (kv.Key == null) drop.Add(kv.Key);
            _lensCache.Clear();      // keyed by the mesh instance we just dropped; cheap to rebuild
            _multiCache.Clear();
        }
        public static int CachedCount => _cache.Count;
        public static bool IsCached(string globalPath) => _cache.ContainsKey(globalPath);

        // BINARY MESH CACHE (strawberry 2026-09-03 loading optimizations; disk over compute): 302 prop OBJs are text-parsed on
        // every PEI load (~255 ms). The parsed arrays are written once to user://mesh_cache/<name>_<size>_<conv>.omb and read
        // back as raw floats after that. Keyed by the OBJ's size + CONV so an edited/re-ripped file re-parses. Versioned.
        const int BinVersion = 1;
        static string BinPath(string globalPath)
        {
            long len = 0; try { len = new System.IO.FileInfo(globalPath).Length; } catch { }
            return ProjectSettings.GlobalizePath($"user://mesh_cache/{System.IO.Path.GetFileNameWithoutExtension(globalPath)}_{len}_{CONV}.omb");
        }
        static ArrayMesh TryLoadBin(string globalPath, out Vector3[] faces)
        {
            faces = null;
            string bp = BinPath(globalPath);
            if (!System.IO.File.Exists(bp)) return null;
            try
            {
                using var br = new System.IO.BinaryReader(new System.IO.BufferedStream(System.IO.File.OpenRead(bp), 1 << 16));
                if (br.ReadInt32() != 0x424D4F31 || br.ReadInt32() != BinVersion) return null;   // "OMB1"
                int n = br.ReadInt32(); if (n <= 0 || n % 3 != 0 || n > 4_000_000) return null;
                var v = new Vector3[n]; var nr = new Vector3[n]; var uv = new Vector2[n]; var col = new Color[n];
                for (int i = 0; i < n; i++) v[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                for (int i = 0; i < n; i++) nr[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                for (int i = 0; i < n; i++) uv[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
                bool hasCol = br.ReadBoolean();
                if (hasCol) for (int i = 0; i < n; i++) col[i] = new Color(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), 1f);
                else for (int i = 0; i < n; i++) col[i] = Colors.White;
                if (br.ReadInt32() != 0x444E4521) return null;
                var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
                arr[(int)Mesh.ArrayType.Vertex] = v; arr[(int)Mesh.ArrayType.Normal] = nr; arr[(int)Mesh.ArrayType.TexUV] = uv; arr[(int)Mesh.ArrayType.Color] = col;
                var m = new ArrayMesh(); m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
                faces = v; return m;
            }
            catch (System.Exception e) { GD.PushWarning($"[objmesh] bad mesh cache {bp}: {e.Message}"); return null; }
        }
        static void TrySaveBin(string globalPath, Vector3[] v, Vector3[] nr, Vector2[] uv, Color[] col)
        {
            try
            {
                string bp = BinPath(globalPath);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(bp));
                bool hasCol = false; foreach (var c in col) if (c != Colors.White) { hasCol = true; break; }
                using var bw = new System.IO.BinaryWriter(new System.IO.BufferedStream(System.IO.File.Create(bp), 1 << 16));
                bw.Write(0x424D4F31); bw.Write(BinVersion); bw.Write(v.Length);
                foreach (var p in v) { bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Z); }
                foreach (var p in nr) { bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Z); }
                foreach (var p in uv) { bw.Write(p.X); bw.Write(p.Y); }
                bw.Write(hasCol); if (hasCol) foreach (var c in col) { bw.Write(c.R); bw.Write(c.G); bw.Write(c.B); }
                bw.Write(0x444E4521);
            }
            catch (System.Exception e) { GD.PushWarning($"[objmesh] could not write mesh cache: {e.Message}"); }
        }

        public static ArrayMesh Load(string globalPath)
        {
            if (_cache.TryGetValue(globalPath, out var hit)) return hit;
            if (System.IO.File.Exists(globalPath))
            {
                var bin = TryLoadBin(globalPath, out var bfaces);
                if (bin != null) { _cache[globalPath] = bin; _faces[bin] = bfaces; return bin; }
            }
            var pos = new List<Vector3>();
            var col = new List<Color>();   // optional per-vertex colour: "v x y z r g b" (billboard ad geometry baked from its palette)
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var outV = new List<Vector3>();
            var outC = new List<Color>();
            var outN = new List<Vector3>();
            var outU = new List<Vector2>();
            foreach (var raw in System.IO.File.ReadLines(globalPath))
            {
                if (raw.Length < 2) continue;
                if (raw[0] == 'v' && raw[1] == ' ')
                {
                    var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    pos.Add(CONV switch
                    {
                        1 => new Vector3(F(p[1]), F(p[2]), F(p[3])),     // raw Unity
                        2 => new Vector3(-F(p[1]), F(p[2]), F(p[3])),    // negate X
                        _ => new Vector3(F(p[1]), F(p[2]), -F(p[3])),    // negate Z (0,3)
                    });
                    col.Add(p.Length >= 7 ? new Color(F(p[4]), F(p[5]), F(p[6])) : Colors.White);   // baked palette colour, or white (no tint)
                }
                else if (raw[0] == 'v' && raw[1] == 'n')
                {
                    var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    nrm.Add(CONV switch
                    {
                        1 => new Vector3(F(p[1]), F(p[2]), F(p[3])),
                        2 => new Vector3(-F(p[1]), F(p[2]), F(p[3])),
                        _ => new Vector3(F(p[1]), F(p[2]), -F(p[3])),
                    });
                }
                else if (raw[0] == 'v' && raw[1] == 't')
                {
                    var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    uv.Add(CONV == 3 ? new Vector2(1f - F(p[1]), 1f - F(p[2])) : new Vector2(F(p[1]), 1f - F(p[2])));   // V-flip (Unity V-up->Godot V-down); CONV3 also U-flips
                }
                else if (raw[0] == 'f' && raw[1] == ' ')
                {
                    var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    int n = p.Length - 1;
                    var vi = new int[n]; var ti = new int[n]; var ni = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        var s = p[i + 1].Split('/');
                        vi[i] = int.Parse(s[0]) - 1;
                        ti[i] = (s.Length > 1 && s[1].Length > 0) ? int.Parse(s[1]) - 1 : -1;
                        ni[i] = (s.Length > 2 && s[2].Length > 0) ? int.Parse(s[2]) - 1 : -1;
                    }
                    for (int i = 1; i + 1 < n; i++)   // fan triangulate; ALWAYS reverse winding: Unity(LH) verts in Godot(RH) face
                    {                                 // inward with the orig order -> reverse so faces point OUT (fixes "inside out"; verts unchanged = no re-mirror)
                        foreach (int k in new[] { 0, i + 1, i })
                        {
                            outV.Add(pos[vi[k]]);
                            outC.Add(vi[k] >= 0 && vi[k] < col.Count ? col[vi[k]] : Colors.White);
                            outN.Add(ni[k] >= 0 && ni[k] < nrm.Count ? nrm[ni[k]] : Vector3.Up);
                            outU.Add(ti[k] >= 0 && ti[k] < uv.Count ? uv[ti[k]] : Vector2.Zero);
                        }
                    }
                }
            }
            if (outV.Count == 0) return null;
            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            var vArr = outV.ToArray(); var nArr = outN.ToArray(); var uArr = outU.ToArray(); var cArr = outC.ToArray();
            arr[(int)Mesh.ArrayType.Vertex] = vArr;
            arr[(int)Mesh.ArrayType.Normal] = nArr;
            arr[(int)Mesh.ArrayType.TexUV] = uArr;
            arr[(int)Mesh.ArrayType.Color] = cArr;   // white unless the obj carried baked vertex colours
            TrySaveBin(globalPath, vArr, nArr, uArr, cArr);   // next load reads the arrays instead of re-parsing the text
            var m = new ArrayMesh();
            m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            _cache[globalPath] = m;
            _faces[m] = vArr;   // non-indexed triangles: exactly what GetFaces() would hand back
            return m;
        }

        // ---- emissive lens split -------------------------------------------------------------------------
        // A lamp's glowing part is geometry the prop mesh already HAS, it just gets drawn with the same grey
        // material as the pole. SplitLens carves those triangles onto their own surface so the caller can hand
        // them an emissive material, and hands back the remainder for the body -- so the glow sits on the real
        // lens instead of a stand-in disc floating under the fixture.
        static readonly Dictionary<ArrayMesh, (ArrayMesh Body, ArrayMesh Lens)> _lensCache = new();

        // Which triangles are "the lens" is decided by PALETTE TEXEL, not by a typed-in bounding box. The
        // extracted props share a tiny palette texture (Street_Light_0's is 2x2) and the bulb is the only
        // geometry on its warm tan entry, so the texel IS the identity -- a box of hardcoded coordinates would
        // silently select the wrong faces the moment the mesh is re-extracted. Load() V-flips UVs, which puts
        // that entry in the u>0.5, v>0.5 quadrant.
        static bool LensUv(Vector2 a, Vector2 b, Vector2 c)
            => a.X > 0.5f && a.Y > 0.5f && b.X > 0.5f && b.Y > 0.5f && c.X > 0.5f && c.Y > 0.5f;

        public static (ArrayMesh Body, ArrayMesh Lens) SplitLens(ArrayMesh src)
        {
            if (src == null) return (null, null);
            if (_lensCache.TryGetValue(src, out var hit)) return hit;
            if (src.GetSurfaceCount() < 1) return _lensCache[src] = (src, null);

            var a0 = src.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var U = a0[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();
            if (V.Length < 3 || U.Length != V.Length) return _lensCache[src] = (src, null);

            var bv = new List<Vector3>(); var bn = new List<Vector3>(); var bu = new List<Vector2>(); var bc = new List<Color>();
            var lv = new List<Vector3>(); var ln = new List<Vector3>(); var lu = new List<Vector2>(); var lc = new List<Color>();
            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                bool lens = LensUv(U[i], U[i + 1], U[i + 2]);
                var dv = lens ? lv : bv; var dn = lens ? ln : bn; var du = lens ? lu : bu; var dc = lens ? lc : bc;
                for (int k = 0; k < 3; k++)
                {
                    dv.Add(V[i + k]);
                    dn.Add(i + k < N.Length ? N[i + k] : Vector3.Up);
                    du.Add(U[i + k]);
                    dc.Add(i + k < C.Length ? C[i + k] : Colors.White);
                }
            }
            if (lv.Count == 0) return _lensCache[src] = (src, null);   // no lens on this prop: leave it whole

            CapHoles(lv, ln, lu, lc);
            var res = (Build(bv, bn, bu, bc), Build(lv, ln, lu, lc));
            _lensCache[src] = res;
            return res;
        }

        // General UV-predicate cousin of SplitLens: carve every triangle whose UV CENTROID satisfies `uvIn` onto its
        // own surface, hand back the rest as body. For palette regions outside SplitLens's hardcoded u>0.5,v>0.5 lens
        // quadrant -- e.g. Lamp_1's shade is the top-left 181-grey texel, which after Load's V-flip is u<0.5, v>0.5.
        // No cache (callers pass a stable ArrayMesh) and no hole-cap (the region is an overlay, not a solid).
        public static (ArrayMesh Body, ArrayMesh Region) SplitByUvCentroid(ArrayMesh src, System.Func<Vector2, bool> uvIn)
        {
            if (src == null || src.GetSurfaceCount() < 1) return (src, null);
            var a0 = src.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var U = a0[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();
            if (V.Length < 3 || U.Length != V.Length) return (src, null);

            var bv = new List<Vector3>(); var bn = new List<Vector3>(); var bu = new List<Vector2>(); var bc = new List<Color>();
            var rv = new List<Vector3>(); var rn = new List<Vector3>(); var ru = new List<Vector2>(); var rc = new List<Color>();
            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                Vector2 ctr = (U[i] + U[i + 1] + U[i + 2]) / 3f;
                bool hit = uvIn(ctr);
                var dv = hit ? rv : bv; var dn = hit ? rn : bn; var du = hit ? ru : bu; var dc = hit ? rc : bc;
                for (int k = 0; k < 3; k++)
                {
                    dv.Add(V[i + k]);
                    dn.Add(i + k < N.Length ? N[i + k] : Vector3.Up);
                    du.Add(U[i + k]);
                    dc.Add(i + k < C.Length ? C[i + k] : Colors.White);
                }
            }
            if (rv.Count == 0) return (src, null);
            return (Build(bv, bn, bu, bc), Build(rv, rn, ru, rc));
        }

        // Geometry-predicate split: carve every triangle whose CENTROID + average vertex NORMAL satisfy `faceIn` onto
        // its own surface. For emitters with NO palette texel that must be found by shape -- e.g. Lamp_0's desk-lamp
        // head has no "bulb" texel, so the down-facing opening face is picked by (on the head Z>0.6, normal points down
        // and to the chosen side).
        public static (ArrayMesh Body, ArrayMesh Region) SplitByFace(ArrayMesh src, System.Func<Vector3, Vector3, bool> faceIn)
        {
            if (src == null || src.GetSurfaceCount() < 1) return (src, null);
            var a0 = src.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var U = a0[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();
            if (V.Length < 3) return (src, null);

            var bv = new List<Vector3>(); var bn = new List<Vector3>(); var bu = new List<Vector2>(); var bc = new List<Color>();
            var rv = new List<Vector3>(); var rn = new List<Vector3>(); var ru = new List<Vector2>(); var rc = new List<Color>();
            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                Vector3 ctr = (V[i] + V[i + 1] + V[i + 2]) / 3f;
                Vector3 nrm = (i + 2 < N.Length) ? (N[i] + N[i + 1] + N[i + 2]).Normalized() : Vector3.Up;
                bool hit = faceIn(ctr, nrm);
                var dv = hit ? rv : bv; var dn = hit ? rn : bn; var du = hit ? ru : bu; var dc = hit ? rc : bc;
                for (int k = 0; k < 3; k++)
                {
                    dv.Add(V[i + k]);
                    dn.Add(i + k < N.Length ? N[i + k] : Vector3.Up);
                    du.Add(i + k < U.Length ? U[i + k] : Vector2.Zero);
                    dc.Add(i + k < C.Length ? C[i + k] : Colors.White);
                }
            }
            if (rv.Count == 0) return (src, null);
            return (Build(bv, bn, bu, bc), Build(rv, rn, ru, rc));
        }

        // Like SplitByFace but the predicate gets the triangle's three VERTICES, not just centroid+normal -- needed when
        // the rule is about a vertex EXTENT rather than an average. A clock hand is identified by its MAX reach from the
        // dial centre (the hand's tip); a centroid can't tell a 0-to-0.37 hand from a 0-to-0.50 dial-disc triangle whose
        // average radius happens to land in the hand's band. Passing the verts lets the caller test max-reach directly.
        public static (ArrayMesh Body, ArrayMesh Region) SplitByFaceVerts(ArrayMesh src, System.Func<Vector3, Vector3, Vector3, bool> triIn)
        {
            if (src == null || src.GetSurfaceCount() < 1) return (src, null);
            var a0 = src.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var U = a0[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();
            if (V.Length < 3) return (src, null);

            var bv = new List<Vector3>(); var bn = new List<Vector3>(); var bu = new List<Vector2>(); var bc = new List<Color>();
            var rv = new List<Vector3>(); var rn = new List<Vector3>(); var ru = new List<Vector2>(); var rc = new List<Color>();
            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                bool hit = triIn(V[i], V[i + 1], V[i + 2]);
                var dv = hit ? rv : bv; var dn = hit ? rn : bn; var du = hit ? ru : bu; var dc = hit ? rc : bc;
                for (int k = 0; k < 3; k++)
                {
                    dv.Add(V[i + k]);
                    dn.Add(i + k < N.Length ? N[i + k] : Vector3.Up);
                    du.Add(i + k < U.Length ? U[i + k] : Vector2.Zero);
                    dc.Add(i + k < C.Length ? C[i + k] : Colors.White);
                }
            }
            if (rv.Count == 0) return (src, null);
            return (Build(bv, bn, bu, bc), Build(rv, rn, ru, rc));
        }

        // Rotate a mesh's vertices AND normals about the Y axis by `rad`, returning a new mesh. Used to bake a clock hand's
        // angular offset into its geometry so it points at 12 in its own frame -- then the runtime rotation is the raw time
        // angle with no per-hand constant to apply to the wrong hand (tinyclaw: pay the offset once in geometry, not per frame).
        public static ArrayMesh RotateAboutY(ArrayMesh src, float rad)
        {
            if (src == null || src.GetSurfaceCount() < 1) return src;
            var a0 = src.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var U = a0[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();
            var b = new Basis(Vector3.Up, rad);
            var v = new List<Vector3>(); var n = new List<Vector3>(); var u = new List<Vector2>(); var c = new List<Color>();
            for (int i = 0; i < V.Length; i++)
            {
                v.Add(b * V[i]);
                n.Add(i < N.Length ? b * N[i] : Vector3.Up);
                u.Add(i < U.Length ? U[i] : Vector2.Zero);
                c.Add(i < C.Length ? C[i] : Colors.White);
            }
            return Build(v, n, u, c);
        }

        // ---- multi-way UV split -------------------------------------------------------------------------
        // SplitLens is binary (lens vs body) because a streetlight has one bulb. A traffic light has THREE
        // lenses that must light independently, so this generalises it: N predicates over a triangle's UVs, N+1
        // meshes back (one per predicate, plus whatever matched nothing).
        //
        // Predicates are given in GODOT uv space -- Load V-flips, so a texel at OBJ v[0.5,1) arrives at v(0,0.5].
        // Deriving the cells from the palette beforehand means no texture load at runtime and no colour matching.
        static readonly Dictionary<(ArrayMesh, int), ArrayMesh[]> _multiCache = new();

        public static ArrayMesh[] SplitByUv(ArrayMesh src, int key, params System.Func<Vector2, Vector2, Vector2, bool>[] preds)
        {
            if (src == null || preds.Length == 0) return null;
            if (_multiCache.TryGetValue((src, key), out var hit)) return hit;
            if (src.GetSurfaceCount() < 1) return null;

            var a0 = src.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var U = a0[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();
            if (V.Length < 3 || U.Length != V.Length) return null;

            int n = preds.Length + 1;   // last bucket = the body
            var vs = new List<Vector3>[n]; var ns = new List<Vector3>[n];
            var us = new List<Vector2>[n]; var cs = new List<Color>[n];
            for (int i = 0; i < n; i++) { vs[i] = new(); ns[i] = new(); us[i] = new(); cs[i] = new(); }

            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                int b = preds.Length;   // default: body
                for (int p = 0; p < preds.Length; p++)
                    if (preds[p](U[i], U[i + 1], U[i + 2])) { b = p; break; }
                for (int k = 0; k < 3; k++)
                {
                    vs[b].Add(V[i + k]);
                    ns[b].Add(i + k < N.Length ? N[i + k] : Vector3.Up);
                    us[b].Add(U[i + k]);
                    cs[b].Add(i + k < C.Length ? C[i + k] : Colors.White);
                }
            }
            var outp = new ArrayMesh[n];
            for (int i = 0; i < n; i++) outp[i] = Build(vs[i], ns[i], us[i], cs[i]);
            _multiCache[(src, key)] = outp;
            return outp;
        }

        /// <summary>Traffic_Light_0's three lens groups, derived from its 4x2 palette: red at u[.50,.75),
        /// amber at u[.75,1), both on the upper texel row (godot v&lt;.5); green spans u[.50,1) on the lower row
        /// (godot v&gt;.5). Returns {red, amber, green, body} -- 4 tris per lens, 2 signal heads per prop.</summary>
        public static ArrayMesh[] SplitTrafficLenses(ArrayMesh src)
        {
            static bool Up(Vector2 a, Vector2 b, Vector2 c) => a.Y < 0.5f && b.Y < 0.5f && c.Y < 0.5f;
            static bool Lo(Vector2 a, Vector2 b, Vector2 c) => a.Y > 0.5f && b.Y > 0.5f && c.Y > 0.5f;
            static bool InU(Vector2 a, Vector2 b, Vector2 c, float lo, float hi)
                => a.X >= lo && a.X < hi && b.X >= lo && b.X < hi && c.X >= lo && c.X < hi;
            return SplitByUv(src, 1,
                (a, b, c) => Up(a, b, c) && InU(a, b, c, 0.50f, 0.75f),   // red
                (a, b, c) => Up(a, b, c) && InU(a, b, c, 0.75f, 1.01f),   // amber
                (a, b, c) => Lo(a, b, c) && InU(a, b, c, 0.50f, 1.01f));  // green
        }

        // ---- per-HEAD traffic split ---------------------------------------------------------------------
        // A mast arm carries TWO signal heads, and strawberry wants them on independent timers, so each head needs
        // its own geometry rather than sharing one surface per colour. The heads separate cleanly along the mast
        // axis (mesh-local Y): on Traffic_Light_0 the lower head's lenses sit at Y 2.7-4.1 and the upper at 6.7-8.1.
        //
        // The cut is FOUND, not hardcoded: sort the lens triangles by centroid along the axis and take the largest
        // gap. A hardcoded 5.5 would work here and quietly mis-split the moment a prop has differently-spaced heads
        // or a third aspect, and a wrong split shows up as one head that never lights -- easy to miss in a screenshot.
        static readonly Dictionary<ArrayMesh, (ArrayMesh[][] Heads, ArrayMesh Body)> _headCache = new();

        /// <summary>Traffic_Light_0 split per signal HEAD: Heads[h] = {red, amber, green} for head h (ordered low to
        /// high along the mast), plus the shared body. Two heads on this prop; the count follows the geometry.</summary>
        public static (ArrayMesh[][] Heads, ArrayMesh Body) SplitTrafficLensesPerHead(ArrayMesh src)
        {
            if (src == null) return (null, null);
            if (_headCache.TryGetValue(src, out var hit)) return hit;

            var parts = SplitTrafficLenses(src);
            if (parts == null || parts.Length != 4) return (null, null);

            // Every lens triangle's centroid along the mast axis, from all three colours together -- one head's
            // three lenses are far closer to each other than to the other head's, so the biggest gap is between heads.
            var cents = new List<float>();
            for (int c = 0; c < 3; c++)
            {
                if (parts[c] == null) continue;
                var v = parts[c].SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (int i = 0; i + 2 < v.Length; i += 3) cents.Add((v[i].Y + v[i + 1].Y + v[i + 2].Y) / 3f);
            }
            if (cents.Count < 2) return _headCache[src] = (null, parts[3]);
            cents.Sort();
            float cut = 0f, best = 0f;
            for (int i = 1; i < cents.Count; i++)
                if (cents[i] - cents[i - 1] > best) { best = cents[i] - cents[i - 1]; cut = (cents[i] + cents[i - 1]) * 0.5f; }
            // No meaningful gap = a single-head prop. Return it as one head rather than inventing an empty second.
            if (best < 0.5f) return _headCache[src] = (new[] { new[] { parts[0], parts[1], parts[2] } }, parts[3]);

            var heads = new ArrayMesh[2][];
            for (int h = 0; h < 2; h++) heads[h] = new ArrayMesh[3];
            for (int c = 0; c < 3; c++)
            {
                if (parts[c] == null) continue;
                var a0 = parts[c].SurfaceGetArrays(0);
                var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var U = a0[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();
                var vs = new List<Vector3>[2]; var ns = new List<Vector3>[2];
                var us = new List<Vector2>[2]; var cs = new List<Color>[2];
                for (int h = 0; h < 2; h++) { vs[h] = new(); ns[h] = new(); us[h] = new(); cs[h] = new(); }
                for (int i = 0; i + 2 < V.Length; i += 3)
                {
                    int h = (V[i].Y + V[i + 1].Y + V[i + 2].Y) / 3f < cut ? 0 : 1;
                    for (int k = 0; k < 3; k++)
                    {
                        vs[h].Add(V[i + k]);
                        ns[h].Add(i + k < N.Length ? N[i + k] : Vector3.Up);
                        us[h].Add(U[i + k]);
                        cs[h].Add(i + k < C.Length ? C[i + k] : Colors.White);
                    }
                }
                for (int h = 0; h < 2; h++) heads[h][c] = Build(vs[h], ns[h], us[h], cs[h]);
            }
            return _headCache[src] = (heads, parts[3]);
        }

        // ---- stump split --------------------------------------------------------------------------------
        // Destroying a prop currently hides every mesh it owns, so a smashed streetlight vanishes off the pavement
        // entirely. Retail leaves the footing behind. SplitBelow carves the part that should SURVIVE a break onto
        // its own surface, so the caller can register only the rest with the destructible field.
        //
        // The rule is "every vertex below the cut", which deliberately sends a straddling triangle UPWARD. On
        // Street_Light_0 the plinth is a closed box from local Z -1.0 to +1.0 while the pole's side faces are
        // full-height quads running Z 0 -> 6.07, so they cross the cut; sending them up means the pole leaves
        // cleanly and the stump that remains is a sealed block rather than a box with a slice taken out of it.
        // No clipping, no new geometry -- the model already separates at exactly the right place.
        static readonly Dictionary<(ArrayMesh, int), (ArrayMesh Below, ArrayMesh Above)> _cutCache = new();

        public static (ArrayMesh Below, ArrayMesh Above) SplitBelow(ArrayMesh src, float cut, int axis = 2)
        {
            if (src == null) return (null, null);
            var key = (src, (int)(cut * 1000f) * 4 + axis);
            if (_cutCache.TryGetValue(key, out var hit)) return hit;
            if (src.GetSurfaceCount() < 1) return _cutCache[key] = (null, src);

            var a0 = src.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var U = a0[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();
            if (V.Length < 3) return _cutCache[key] = (null, src);

            var bv = new List<Vector3>(); var bn = new List<Vector3>(); var bu = new List<Vector2>(); var bc = new List<Color>();
            var av = new List<Vector3>(); var an = new List<Vector3>(); var au = new List<Vector2>(); var ac = new List<Color>();
            float Axis(Vector3 p) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                bool below = Axis(V[i]) <= cut + 1e-4f && Axis(V[i + 1]) <= cut + 1e-4f && Axis(V[i + 2]) <= cut + 1e-4f;
                var dv = below ? bv : av; var dn = below ? bn : an; var du = below ? bu : au; var dc = below ? bc : ac;
                for (int k = 0; k < 3; k++)
                {
                    dv.Add(V[i + k]);
                    dn.Add(i + k < N.Length ? N[i + k] : Vector3.Up);
                    du.Add(i + k < U.Length ? U[i + k] : Vector2.Zero);
                    dc.Add(i + k < C.Length ? C[i + k] : Colors.White);
                }
            }
            var res = (Build(bv, bn, bu, bc), Build(av, an, au, ac));
            _cutCache[key] = res;
            return res;
        }

        static ArrayMesh Build(List<Vector3> v, List<Vector3> n, List<Vector2> u, List<Color> c)
        {
            if (v.Count == 0) return null;
            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = v.ToArray();
            arr[(int)Mesh.ArrayType.Normal] = n.ToArray();
            arr[(int)Mesh.ArrayType.TexUV] = u.ToArray();
            arr[(int)Mesh.ArrayType.Color] = c.ToArray();
            var m = new ArrayMesh();
            m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return m;
        }

        // The extracted bulb is an OPEN box -- 5 quads, not 6. Its top face points up into the housing shell, so
        // the artist deleted it as an interior face nobody could see. Harmless while it was shaded like the pole;
        // an emissive lens with a hole in it is not, since the hole shows the unlit inside of the far wall.
        //
        // The missing face is found by BOUNDARY EDGE (one used by a single triangle) rather than by assuming which
        // side is absent, so this closes whatever the split actually leaves open, on any prop, in any orientation.
        static (int, int, int) Key(Vector3 p)
            => ((int)Mathf.Round(p.X * 10000f), (int)Mathf.Round(p.Y * 10000f), (int)Mathf.Round(p.Z * 10000f));

        static void CapHoles(List<Vector3> v, List<Vector3> n, List<Vector2> u, List<Color> c)
        {
            var pos = new Dictionary<(int, int, int), Vector3>();
            var uvs = new Dictionary<(int, int, int), Vector2>();
            var dir = new HashSet<((int, int, int), (int, int, int))>();
            for (int i = 0; i + 2 < v.Count; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    var ka = Key(v[i + k]); var kb = Key(v[i + (k + 1) % 3]);
                    pos[ka] = v[i + k]; uvs[ka] = u[i + k];
                    dir.Add((ka, kb));
                }
            // an interior edge is walked once in each direction; a boundary edge only one way
            var open = new Dictionary<(int, int, int), (int, int, int)>();
            foreach (var (a, b) in dir) if (!dir.Contains((b, a)) && !open.ContainsKey(a)) open[a] = b;

            int guard = 0;
            while (open.Count > 0 && guard++ < 64)
            {
                var loop = new List<(int, int, int)>();
                var start = open.Keys.GetEnumerator(); start.MoveNext();
                var cur = start.Current;
                while (open.TryGetValue(cur, out var nxt))
                {
                    loop.Add(cur); open.Remove(cur); cur = nxt;
                    if (cur.Equals(loop[0])) break;
                }
                if (loop.Count < 3) continue;
                // Fan the hole with the winding REVERSED relative to the boundary walk, so the cap faces outward
                // like the shell around it rather than inward.
                for (int i = 1; i + 1 < loop.Count; i++)
                {
                    var t = new[] { pos[loop[0]], pos[loop[i + 1]], pos[loop[i]] };
                    var fn = (t[1] - t[0]).Cross(t[2] - t[0]).Normalized();
                    for (int k = 0; k < 3; k++)
                    {
                        v.Add(t[k]);
                        n.Add(fn);
                        u.Add(uvs[k == 0 ? loop[0] : (k == 1 ? loop[i + 1] : loop[i])]);
                        c.Add(Colors.White);
                    }
                }
            }
        }
    }
}
