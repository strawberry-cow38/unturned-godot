using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // GENERATE THE LODs RETAIL NEVER SHIPPED.
    //
    // 245 of 583 props have no LODGroup in Unturned at all -- not a gap in our extraction (every lower LOD
    // retail declares, we already pull: 201 of 201). Nelson simply never authored them, because retail culls
    // objects at a 447m region limit and so never needed one. The set is exactly the expensive one:
    // Apartment_0..3, Office_1..3, Mall_0, Ambulance, APC_Desert -- the tallest things on the map draw full
    // detail at every distance.
    //
    // strawberry decided to generate them ("either way we need those lods" / "generate them"). Worth being
    // explicit that this is a DEPARTURE from the port's 1:1 rule rather than a fidelity fix: retail has no
    // lower mesh for these, so anything here is ours. Props that DO have authored LODGroups are left strictly
    // alone -- their bands and meshes stay Nelson's.
    //
    // OFFLINE, not at load. ImporterMesh.GenerateLods is Godot's own decimator so there is no new dependency,
    // but running it over 245 props every boot would pay real load time for a result that never changes. This
    // writes `<name>_lod1.obj` in the convention the loader already reads, so nothing at runtime changes: the
    // moment the files exist, LodPlanFor picks them up.
    public static class LodBaker
    {
        /// <summary>Godot's decimator preserves a hard edge when the angle between faces exceeds these. Left at
        /// the import defaults: guessing tighter values to "look better" on 245 props at once, unmeasured, is
        /// exactly the kind of tuning that should follow a render rather than precede it.</summary>
        const float NormalMergeAngle = 60f;
        const float NormalSplitAngle = 25f;

        public const string GeneratedTable = "lods_generated.txt";

        /// <summary>Screen heights for the generated bands: the MEDIAN of what retail authored across its own
        /// 201 two-level props (h0 0.20294, h1 0.02813). Nelson's thresholds are near-uniform, so the median is
        /// a real central value rather than a number I picked -- and it puts a 17m building's LOD1 switch at
        /// ~73m and its cull at ~523m, which the 447m region cap then bites.</summary>
        const float H0 = 0.20294f, H1 = 0.02813f;

        /// <summary>Layer by size. SMALL and MEDIUM overlap heavily in retail's own table (both median ~2-3m),
        /// so this cannot be exact; LARGE is the separable one (min 5.09, p25 12). It matters less than it
        /// looks: for most props the per-height cull binds before the layer cap does.</summary>
        static string LayerFor(float size) => size >= 12f ? "LARGE" : size >= 4f ? "MEDIUM" : "SMALL";

        public static void BakeAll(string dir, bool dryRun)
        {
            if (!Directory.Exists(dir)) { GD.PrintErr($"[bakelods] no such dir {dir}"); return; }
            // WHICH PROPS ARE OURS TO TOUCH. The criterion is "retail authored a LODGroup for it", read from
            // lods.txt -- NOT "a _lod1.obj exists on disk". Those differ the moment this tool has run once: the
            // file-existence test skipped all 281 props it had just written, produced an empty band table, and
            // reported success. Idempotence has to be judged against the SOURCE of truth, not against our own
            // previous output.
            var retailAuthored = new HashSet<string>();
            string lodsTxt = Path.Combine(dir, "lods.txt");
            if (File.Exists(lodsTxt))
                foreach (var line in File.ReadAllLines(lodsTxt))
                {
                    if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                    var c = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                    if (c.Length < 5) continue;
                    int levels = 0;
                    foreach (var h in c[4].Split(',')) if (h.Trim().Length > 0 && h.Trim() != "-") levels++;
                    if (levels > 1) retailAuthored.Add(c[1]);
                }
            GD.Print($"[bakelods] retail authored a lower LOD for {retailAuthored.Count} props -- leaving those alone");

            var bases = new List<string>();
            foreach (var p in Directory.GetFiles(dir, "*.obj"))
            {
                string n = Path.GetFileNameWithoutExtension(p);
                if (n.Contains("_lod")) continue;              // already a LOD level
                if (retailAuthored.Contains(n)) continue;      // Nelson's -- never overwrite
                bases.Add(n);
            }
            bases.Sort();
            GD.Print($"[bakelods] {bases.Count} props with no LOD chain{(dryRun ? " (DRY RUN)" : "")}");

            int made = 0, skipped = 0, totalBefore = 0, totalAfter = 0;
            var table = new List<string>();
            foreach (var name in bases)
            {
                var src = ObjMesh.Load(Path.Combine(dir, name + ".obj"));
                if (src == null || src.GetSurfaceCount() == 0) { skipped++; continue; }
                var arrays = src.SurfaceGetArrays(0);
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                if (verts.Length < 24) { skipped++; continue; }   // too small to be worth a level; decimating a crate gains nothing

                // WELD FIRST, OR THE DECIMATOR CANNOT DO ANYTHING. ObjMesh.Load builds an UNINDEXED triangle
                // soup -- it fills Vertex/Normal/TexUV/Color and no Index array -- so every triangle owns its
                // own three vertices and no two triangles share an edge. Edge-collapse decimation works on
                // shared edges, so on soup it has nothing to collapse: the first run of this tool skipped all
                // 382 props and reported a tidy "0% off", which reads like the props being unsuitable rather
                // than the input being the wrong shape.
                //
                // Welded on (position, uv) rather than position alone: position-only would fuse the two sides
                // of a texture seam and smear the atlas across it. Hard-edge normals are left to GenerateLods'
                // own merge/split angles, which is what those parameters are for.
                arrays = Weld(arrays);
                var baseIdx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                int beforeTris = baseIdx.Length / 3;

                var im = new ImporterMesh();
                im.AddSurface(Mesh.PrimitiveType.Triangles, arrays);
                im.GenerateLods(NormalMergeAngle, NormalSplitAngle, new Godot.Collections.Array());

                int levels = im.GetSurfaceLodCount(0);
                if (levels == 0) { skipped++; continue; }   // decimator found nothing to remove
                // Godot orders its generated levels coarsest-last. Take ONE level -- the last -- rather than the
                // whole chain: 167 of the 201 props retail DID author stop at a single lod1, so one extra level
                // is the convention this project already renders, and matching it keeps LodPlanFor's band maths
                // identical for both populations.
                var lodIdx = im.GetSurfaceLodIndices(0, levels - 1);
                int afterTris = lodIdx.Length / 3;
                if (afterTris == 0 || afterTris >= beforeTris) { skipped++; continue; }

                totalBefore += beforeTris; totalAfter += afterTris;
                made++;
                GD.Print($"[bakelods] {name,-26} {beforeTris,6} -> {afterTris,6} tris  ({100f * (1f - (float)afterTris / beforeTris):0}% off, {levels} levels offered)");
                if (!dryRun) WriteObj(Path.Combine(dir, name + "_lod1.obj"), arrays, lodIdx);
                var ab = src.GetAabb();
                float size = Mathf.Max(ab.Size.X, Mathf.Max(ab.Size.Y, ab.Size.Z));
                table.Add($"{name}\t{LayerFor(size)}\t{size.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\t{H0.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)},{H1.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (!dryRun && table.Count > 0)
            {
                // THE MESHES ALONE ARE INERT. LevelRanges keys on the retail lods.txt GUID table, and these props
                // are precisely the ones absent from it -- so it returns null, WorldBuilder's `ranges != null &&
                // ranges.Length > 1` gate never opens, and the generated files are never loaded by anything. I
                // shipped a commit claiming "the loader picks them up because the files exist"; it does not.
                // This companion table is what makes them reachable, keyed by NAME since they have no GUID entry.
                table.Sort();
                File.WriteAllText(Path.Combine(dir, GeneratedTable),
                    "# name<TAB>layer<TAB>size<TAB>h0,h1 -- bands for props retail shipped with no LODGroup.\n"
                  + "# h0/h1 are the MEDIAN authored screen heights across the 201 props retail DID author\n"
                  + $"# ({H0} / {H1}); using retail's own central values rather than numbers of my choosing.\n"
                  + string.Join("\n", table) + "\n");
                GD.Print($"[bakelods] wrote {GeneratedTable} with {table.Count} bands");
            }
            GD.Print($"[bakelods] wrote {made}, skipped {skipped}; {totalBefore} -> {totalAfter} tris "
                   + $"({(totalBefore > 0 ? 100f * (1f - (float)totalAfter / totalBefore) : 0f):0}% off across the set)");
        }

        /// <summary>Turn an unindexed triangle soup into an indexed mesh by fusing vertices that share a
        /// position AND a uv. Returns arrays with an Index buffer added.</summary>
        static Godot.Collections.Array Weld(Godot.Collections.Array arrays)
        {
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var cols = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
            bool hasN = norms.Length == verts.Length, hasUv = uvs.Length == verts.Length, hasC = cols.Length == verts.Length;

            var map = new Dictionary<(float, float, float, float, float), int>();
            var oV = new List<Vector3>(); var oN = new List<Vector3>(); var oU = new List<Vector2>(); var oC = new List<Color>();
            var idx = new List<int>(verts.Length);
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                var uv = hasUv ? uvs[i] : Vector2.Zero;
                var key = (v.X, v.Y, v.Z, uv.X, uv.Y);
                if (!map.TryGetValue(key, out int at))
                {
                    at = oV.Count; map[key] = at;
                    oV.Add(v);
                    if (hasN) oN.Add(norms[i]);
                    if (hasUv) oU.Add(uv);
                    if (hasC) oC.Add(cols[i]);
                }
                idx.Add(at);
            }
            var outA = new Godot.Collections.Array();
            outA.Resize((int)Mesh.ArrayType.Max);
            outA[(int)Mesh.ArrayType.Vertex] = oV.ToArray();
            if (hasN) outA[(int)Mesh.ArrayType.Normal] = oN.ToArray();
            if (hasUv) outA[(int)Mesh.ArrayType.TexUV] = oU.ToArray();
            if (hasC) outA[(int)Mesh.ArrayType.Color] = oC.ToArray();
            outA[(int)Mesh.ArrayType.Index] = idx.ToArray();
            return outA;
        }

        /// <summary>Write one LOD level as an .obj the existing loader can read. Vertices are REMAPPED to only
        /// those the level references -- a decimated level that still shipped the full vertex list would shrink
        /// the triangle count while keeping the file (and the vertex buffer) the same size.</summary>
        static void WriteObj(string path, Godot.Collections.Array arrays, int[] idx)
        {
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var cols = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
            bool hasN = norms.Length == verts.Length, hasUv = uvs.Length == verts.Length, hasC = cols.Length == verts.Length;

            var remap = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (int i in idx)
                if (!remap.ContainsKey(i)) { remap[i] = order.Count + 1; order.Add(i); }   // .obj is 1-indexed

            using var w = new StreamWriter(path);
            w.WriteLine("# generated by LodBaker -- retail ships no LODGroup for this prop");
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (int i in order)
            {
                var v = verts[i];
                // Per-vertex colour rides on the v line as `v x y z r g b`, which is what ObjMesh.Load reads
                // back; dropping it would silently un-tint every billboard-ad prop that bakes its palette in.
                if (hasC) w.WriteLine($"v {v.X.ToString(ci)} {v.Y.ToString(ci)} {v.Z.ToString(ci)} {cols[i].R.ToString(ci)} {cols[i].G.ToString(ci)} {cols[i].B.ToString(ci)}");
                else w.WriteLine($"v {v.X.ToString(ci)} {v.Y.ToString(ci)} {v.Z.ToString(ci)}");
            }
            if (hasUv) foreach (int i in order) w.WriteLine($"vt {uvs[i].X.ToString(ci)} {uvs[i].Y.ToString(ci)}");
            if (hasN) foreach (int i in order) w.WriteLine($"vn {norms[i].X.ToString(ci)} {norms[i].Y.ToString(ci)} {norms[i].Z.ToString(ci)}");
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = remap[idx[t]], b = remap[idx[t + 1]], c = remap[idx[t + 2]];
                if (hasUv && hasN) w.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                else if (hasN) w.WriteLine($"f {a}//{a} {b}//{b} {c}//{c}");
                else if (hasUv) w.WriteLine($"f {a}/{a} {b}/{b} {c}/{c}");
                else w.WriteLine($"f {a} {b} {c}");
            }
        }
    }
}
