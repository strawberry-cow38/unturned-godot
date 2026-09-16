using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    /// <summary>Grass, flowers and pebbles for a GENERATED island (strawberry 2026-09-16: "no foliage").
    ///
    /// ⚠ WHY THIS BAKES A FILE INSTEAD OF SCATTERING INTO THE SCENE. Foliage in this port is bake-driven:
    /// FoliageField.LoadGrass() discovers `content/&lt;MapDir&gt;/*.bin` and each .bin is the per-instance transform
    /// list a python baker wrote for one retail map. Everything downstream assumes that -- the editor's foliage
    /// brush, the per-instance "a human placed this, do not clear it on re-bake" flag, the authoring cells. A
    /// generated island that scattered straight into MultiMeshes would render fine and then be the one map
    /// whose foliage you cannot paint, and whose hand-placed grass a re-bake silently eats. Writing the bake is
    /// what makes a generated map an ordinary map to every tool that comes after.
    /// It also means reopening the island shows the same foliage, which scattering at load would not.
    ///
    /// ⚠ THE ART IS COPIED, NOT REGENERATED. LoadType reads &lt;name&gt;.obj and &lt;name&gt;_tex.png from the SAME
    /// directory as the .bin, so the island's bake dir needs its own copies -- they are 2-3 KB each, and the
    /// alternative (teaching FoliageField to look in two places) changes a path every retail map depends on.
    /// .gdignore comes too: without it Godot tries to import these as project assets.
    /// </summary>
    public static class ProcIslandFoliage
    {
        public const string SourceDir = "res://content/foliage/";   // PEI's bake -- the meshes and textures, not its positions

        /// <summary>Roughly how many metres between instances of each type. Grass is the dense one; the others
        /// are accents. ⚠ These are SPACINGS, not counts, so the totals scale with however much land the seed
        /// produced rather than being a number tuned against one island.</summary>
        static readonly (string Prefix, float Spacing, float Jitter)[] Kinds =
        {
            ("grass",       2.2f, 1.0f),
            ("flowers",     9.0f, 4.0f),
            ("pebble",     11.0f, 5.0f),
        };

        /// <summary>Bake foliage for this island into content/foliage_&lt;mapKey&gt;/ and return the MapDir name to
        /// point FoliageField at. Returns null when there is nothing to copy from.</summary>
        public static string Bake(Terrain terr, int seed, string mapKey)
        {
            if (terr == null) return null;
            string src = ProjectSettings.GlobalizePath(SourceDir);
            if (!Directory.Exists(src)) { Log.Print($"[island-foliage] no source bake at {SourceDir} -- skipping"); return null; }

            string dirName = "foliage_" + mapKey;
            string dst = ProjectSettings.GlobalizePath($"res://content/{dirName}/");
            // ⚠⚠ THE REUSE CACHE IS GONE, AND IT WAS SHIPPING TREES ONTO ROADS.
            //
            // It keyed on the SEED and reused any existing bake, with a comment saying "delete the directory to
            // force a fresh bake after changing the scatter rules". That is a correctness requirement expressed
            // as a note to a human, and it held for exactly as long as nobody changed anything: the scatter
            // refuses ground that is not Grass, so a bake encodes where the ROADS, TOWNS and PAINT were at the
            // moment it ran. Every one of those moved repeatedly while the key did not, so re-opening seed
            // 12345 replayed trees from an island that no longer existed -- standing in the middle of
            // carriageway that had been routed somewhere else since. strawberry: "!!! trees on the road! is our
            // foliage being done correctly?"
            //
            // A seed is not a version. The honest key would have to include every input the scatter reads,
            // which is the whole generator -- so the cache is dropped instead. It bought 788 ms of a 14.6 s
            // generation (5%) and cost a class of bug that looks exactly like a scatter fault.
            if (Directory.Exists(dst))
                foreach (var stale in Directory.GetFiles(dst, "*.bin")) { try { File.Delete(stale); } catch { } }
            try
            {
                Directory.CreateDirectory(dst);
                foreach (string f in Directory.GetFiles(src))
                {
                    string nm = Path.GetFileName(f);
                    if (nm.EndsWith(".bin")) continue;   // ⚠ NEVER copy the source .bin -- those are PEI's POSITIONS
                    File.Copy(f, Path.Combine(dst, nm), overwrite: true);
                }
            }
            catch (System.Exception ex) { Log.Err($"[island-foliage] could not prepare {dirName}: {ex.Message}"); return null; }

            var bounds = terr.WorldBoundsXZ();
            int total = 0, types = 0;

            foreach (string objPath in Directory.GetFiles(src, "*.obj"))
            {
                string nm = Path.GetFileNameWithoutExtension(objPath);
                var kind = System.Array.Find(Kinds, k => nm.StartsWith(k.Prefix));
                if (kind.Prefix == null) continue;

                // A per-type seed, so adding a type does not reshuffle the ones already placed.
                var rng = new System.Random(seed * 7919 + nm.GetHashCode());
                var xforms = new List<(Vector3 Pos, float Yaw, float Scale)>();
                for (float x = bounds.MinX; x < bounds.MaxX; x += kind.Spacing)
                    for (float z = bounds.MinZ; z < bounds.MaxZ; z += kind.Spacing)
                    {
                        float px = x + (float)(rng.NextDouble() * 2 - 1) * kind.Jitter;
                        float pz = z + (float)(rng.NextDouble() * 2 - 1) * kind.Jitter;
                        // ⚠ THE PAINT IS THE RULE (strawberry: "grass and flowers and bushes and trees dont
                        // spawn on dirt. only on grass"). This replaced a rasterised road/building mask that
                        // computed the same answer a second way -- two sources of truth that could drift, and
                        // one of them invisible to the player. Reading the splat means the scatter refuses
                        // exactly the ground that LOOKS built on, including anything a human paints later.
                        if (terr.SampleDominantLayer(px, pz) != ProcIslandSpawn.GrassLayer) continue;
                        float y = terr.SampleHeight(px, pz);
                        if (Terrain.HasWater && y < Terrain.SeaLevelY + 0.6f) continue;   // no grass in the surf
                        // Slope: foliage standing straight up out of a cliff face reads as a bug. Sampled rather
                        // than assumed, the same way the spawn chooser does it.
                        float h1 = terr.SampleHeight(px + 1.5f, pz), h2 = terr.SampleHeight(px - 1.5f, pz);
                        float h3 = terr.SampleHeight(px, pz + 1.5f), h4 = terr.SampleHeight(px, pz - 1.5f);
                        float lo = Mathf.Min(Mathf.Min(h1, h2), Mathf.Min(h3, h4));
                        float hi = Mathf.Max(Mathf.Max(h1, h2), Mathf.Max(h3, h4));
                        if (hi - lo > 2.2f) continue;
                        xforms.Add((new Vector3(px, y, pz),
                                    (float)(rng.NextDouble() * Mathf.Tau),
                                    0.85f + (float)rng.NextDouble() * 0.4f));
                    }

                if (xforms.Count == 0) continue;
                WriteBin(Path.Combine(dst, nm + ".bin"), xforms);
                total += xforms.Count; types++;
            }

            Log.Print($"[island-foliage] baked {total} instance(s) across {types} type(s) -> content/{dirName}/");
            return types > 0 ? dirName : null;
        }

        /// <summary>The .bin v1 format FoliageField already reads: int32 count, then 12 floats per instance --
        /// Unity basis columns X/Y/Z then position.
        ///
        /// ⚠ WRITTEN IN UNITY CONVENTION, because the loader converts ON READ (`negate Z on each axis' z and
        /// pos.z`). Writing Godot-space values here would round-trip to a mirrored island, and a mirrored
        /// scatter of grass looks exactly like a correct one -- there is no asymmetric blade to give it away.
        /// So the inverse is applied deliberately rather than discovered later:
        ///   godot X col (s·cosθ, 0, -s·sinθ)  &lt;-  x = ( s·cosθ, 0,  s·sinθ)
        ///   godot Y col (0, s, 0)             &lt;-  y = ( 0,      s,  0     )
        ///   godot Z col (s·sinθ, 0, s·cosθ)   &lt;-  z = (-s·sinθ, 0,  s·cosθ)
        ///   godot pos  (px, py, pz)           &lt;-  p = ( px,    py, -pz    )
        /// </summary>
        static void WriteBin(string path, List<(Vector3 Pos, float Yaw, float Scale)> xforms)
        {
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(xforms.Count);
            foreach (var (pos, yaw, s) in xforms)
            {
                float c = Mathf.Cos(yaw) * s, sn = Mathf.Sin(yaw) * s;
                bw.Write(c);    bw.Write(0f);  bw.Write(sn);     // X column
                bw.Write(0f);   bw.Write(s);   bw.Write(0f);     // Y column
                bw.Write(-sn);  bw.Write(0f);  bw.Write(c);      // Z column
                bw.Write(pos.X); bw.Write(pos.Y); bw.Write(-pos.Z);
            }
        }


        // ---- harvestable resources: trees, bushes, mushrooms, ore ------------------------------------------
        /// <summary>Per-type scatter spacing for ResourceField's contents. Trees first because they are the
        /// shape of the island; the rest are dressing. ⚠ Prefix match, longest-first where it matters
        /// ("Bush_Mauve" must not be read as a plain Bush).</summary>
        /// ⚠ CALIBRATED AGAINST THE REAL MAP, NOT BY EYE. The first cut used 15-24 m spacings and produced
        /// 18,173 resources -- against PEI's 1,694 across the same 26 types. Ten times retail density is not a
        /// forest, it is a wall: the first render was hemmed in on both sides with the road barely visible
        /// through it. The numbers below land in retail's order of magnitude. A count is the only honest way to
        /// judge this; "looks foresty" would have shipped the wall.
        static readonly (string Prefix, float Spacing, float Clearance)[] ResKinds =
        {
            ("Birch",            42f, 17f),
            ("Maple",            45f, 17f),
            ("Pine",             40f, 17f),
            ("Bush",             62f, 12f),
            ("Mushroom",        120f,  9f),
            ("Metal",           190f, 12f),
            ("Clay",            165f, 12f),
        };

        /// <summary>Bake trees, bushes, mushrooms and ore for this island into content/resources_&lt;mapKey&gt;/.
        ///
        /// ⚠ A DIFFERENT SYSTEM AND A DIFFERENT FILE FORMAT FROM THE FOLIAGE ABOVE, which is worth saying out
        /// loud because they look alike. Foliage is decor in a MultiMesh; a resource is HARVESTABLE -- it gets a
        /// trunk collider, hit points, a log drop and a regrow timer. And the .bin is not the same shape:
        /// foliage stores a 12-float basis, a resource stores NINE floats (pos, euler, scale) which
        /// ResourceField rebuilds as `Y(180-ey) * X(ex) * Z(-ez)`. Writing one in the other's layout does not
        /// error -- it produces trees at plausible-looking wrong angles.
        ///
        /// ⚠ resources.txt IS COPIED VERBATIM. Its ROW ORDER is the wire index for resource identity ("the
        /// deterministic index space: instances register in manifest x .bin order on every peer"), so
        /// rewriting or re-sorting it for a generated map would desync which tree is which in multiplayer.
        /// The island only adds .bin files beside it; it never touches the manifest.</summary>
        public static string BakeResources(Terrain terr, int seed, string mapKey)
        {
            if (terr == null) return null;
            string src = ProjectSettings.GlobalizePath("res://content/resources/");
            if (!Directory.Exists(src)) { Log.Print("[island-res] no source resources -- skipping"); return null; }

            string dirName = "resources_" + mapKey;
            string dst = ProjectSettings.GlobalizePath($"res://content/{dirName}/");
            // Same as the foliage above: keyed by seed, invalidated by everything else. A stale resource bake is
            // the one that puts a 20 m pine in a carriageway.
            if (Directory.Exists(dst))
                foreach (var stale in Directory.GetFiles(dst, "*.bin")) { try { File.Delete(stale); } catch { } }
            try
            {
                Directory.CreateDirectory(dst);
                foreach (string f in Directory.GetFiles(src))
                {
                    string nm = Path.GetFileName(f);
                    if (nm.EndsWith(".bin")) continue;   // ⚠ PEI's tree POSITIONS -- never copied
                    File.Copy(f, Path.Combine(dst, nm), overwrite: true);   // includes resources.txt VERBATIM (wire index) + lods.txt
                }
            }
            catch (System.Exception ex) { Log.Err($"[island-res] could not prepare {dirName}: {ex.Message}"); return null; }

            string manifest = Path.Combine(src, "resources.txt");
            if (!File.Exists(manifest)) { Log.Print("[island-res] no resources.txt in the source -- skipping"); return null; }

            var bounds = terr.WorldBoundsXZ();
            // Every resource competes for ground with every other, so one shared occupancy list stops a
            // mushroom growing inside a pine. Kept per-bake, not per-type.
            var taken = new List<Vector3>();
            int total = 0, types = 0;

            foreach (string line in File.ReadAllLines(manifest))
            {
                var sp = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 2) continue;
                string name = sp[0];
                string holiday = sp.Length >= 3 ? sp[2] : "NONE";
                if (holiday != "NONE") continue;   // seasonal content stays gated; the island is not Christmas
                var kind = System.Array.Find(ResKinds, k => name.StartsWith(k.Prefix));
                if (kind.Prefix == null) continue;

                var rng = new System.Random(seed * 6271 + name.GetHashCode());
                var recs = new List<(Vector3 Pos, float Yaw, float Scale)>();
                for (float x = bounds.MinX; x < bounds.MaxX; x += kind.Spacing)
                    for (float z = bounds.MinZ; z < bounds.MaxZ; z += kind.Spacing)
                    {
                        float px = x + (float)(rng.NextDouble() * 2 - 1) * kind.Spacing * 0.45f;
                        float pz = z + (float)(rng.NextDouble() * 2 - 1) * kind.Spacing * 0.45f;
                        if (terr.SampleDominantLayer(px, pz) != ProcIslandSpawn.GrassLayer) continue;   // only on grass, same rule as the foliage
                        float y = terr.SampleHeight(px, pz);
                        if (Terrain.HasWater && y < Terrain.SeaLevelY + 1.5f) continue;   // nothing grows in the tide
                        float h1 = terr.SampleHeight(px + 2f, pz), h2 = terr.SampleHeight(px - 2f, pz);
                        float h3 = terr.SampleHeight(px, pz + 2f), h4 = terr.SampleHeight(px, pz - 2f);
                        float lo = Mathf.Min(Mathf.Min(h1, h2), Mathf.Min(h3, h4));
                        float hi = Mathf.Max(Mathf.Max(h1, h2), Mathf.Max(h3, h4));
                        if (hi - lo > 3.0f) continue;   // a tree on a cliff face leans out of it
                        bool clash = false;
                        foreach (var t in taken)
                            if (Near(px, t.X, pz, t.Z, kind.Clearance)) { clash = true; break; }
                        if (clash) continue;
                        var pos = new Vector3(px, y, pz);
                        recs.Add((pos, (float)(rng.NextDouble() * 360.0), 0.9f + (float)rng.NextDouble() * 0.25f));
                        taken.Add(pos);
                    }

                if (recs.Count == 0) continue;
                WriteResourceBin(Path.Combine(dst, name + ".bin"), recs);
                total += recs.Count; types++;
            }

            Log.Print($"[island-res] baked {total} resource(s) across {types} type(s) -> content/{dirName}/");
            return types > 0 ? dirName : null;
        }

        static bool Near(float ax, float bx, float az, float bz, float r)
        {
            float dx = ax - bx, dz = az - bz;
            return dx * dx + dz * dz < r * r;
        }

        /// <summary>ResourceField's .bin: int32 count, then per instance pos(3), euler(3), scale(3).
        ///
        /// ⚠ NINE floats, NOT the foliage bake's twelve, and the rotation is EULER rather than a basis. The
        /// reader rebuilds `Y(180 - ey) * X(ex) * Z(-ez)` and negates pos.z, so an upright trunk with world yaw
        /// t is ex=0, ez=0, ey=180-t. Getting this wrong yields trees standing at wrong angles, which reads as
        /// a modelling problem rather than a format one.</summary>
        static void WriteResourceBin(string path, List<(Vector3 Pos, float Yaw, float Scale)> recs)
        {
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(recs.Count);
            foreach (var (pos, yaw, s) in recs)
            {
                bw.Write(pos.X); bw.Write(pos.Y); bw.Write(-pos.Z);       // negate-Z position, as every placement here does
                bw.Write(0f); bw.Write(180f - yaw); bw.Write(0f);          // upright: only yaw, expressed as the reader's ey
                bw.Write(s); bw.Write(s); bw.Write(s);
            }
        }

    }
}
