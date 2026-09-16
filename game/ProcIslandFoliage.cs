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
            // ⚠ ALREADY BAKED -> REUSE. The bake is keyed by seed and is deterministic, so re-opening the same
            // island would otherwise re-scatter and re-write ~850k instances (30 MB) for a byte-identical
            // result, every time. Delete the directory to force a fresh bake after changing the scatter rules.
            if (Directory.Exists(dst) && Directory.GetFiles(dst, "*.bin").Length > 0)
            {
                Log.Print($"[island-foliage] reusing existing bake content/{dirName}/");
                return dirName;
            }
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

            var blocked = BuildBlockedMask(terr);
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
                        if (blocked.Blocked(px, pz)) continue;
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

        /// <summary>A coarse "something is already here" grid over roads and buildings.
        ///
        /// ⚠ A MASK, NOT A DISTANCE TEST. The obvious version checks every candidate against every road tile and
        /// building: ~600k candidates x ~240 objects is 140M checks and turns island generation into a stall.
        /// Rasterising once and looking up is O(1) per candidate.</summary>
        static Mask BuildBlockedMask(Terrain terr)
        {
            var b = terr.WorldBoundsXZ();
            var m = new Mask
            {
                Cell = 2f,
                MinX = b.MinX,
                MinZ = b.MinZ,
                Nx = Mathf.Max(1, Mathf.CeilToInt((b.MaxX - b.MinX) / 2f)),
                Nz = Mathf.Max(1, Mathf.CeilToInt((b.MaxZ - b.MinZ) / 2f)),
            };
            m.Bits = new bool[m.Nx * m.Nz];

            if (terr.IslandTiles != null)
                foreach (var t in terr.IslandTiles)
                {
                    var w = ProcIslandSpawn.PosFor(terr, t.X, t.Z);
                    m.Stamp(w.X, w.Z, 5f);    // a road tile is 4 m; clear its verge too so grass does not grow through tarmac
                }
            if (terr.IslandBuildings != null)
                foreach (var bd in terr.IslandBuildings)
                {
                    var w = ProcIslandSpawn.PosFor(terr, bd.X, bd.Z);
                    m.Stamp(w.X, w.Z, 8f);    // footprints are unknown, so a radius; better a bald patch than grass inside a wall
                }
            return m;
        }

        struct Mask
        {
            public bool[] Bits;
            public float Cell, MinX, MinZ;
            public int Nx, Nz;

            public void Stamp(float wx, float wz, float radius)
            {
                int r = Mathf.CeilToInt(radius / Cell);
                int cx = (int)((wx - MinX) / Cell), cz = (int)((wz - MinZ) / Cell);
                for (int ix = cx - r; ix <= cx + r; ix++)
                    for (int iz = cz - r; iz <= cz + r; iz++)
                        if (ix >= 0 && iz >= 0 && ix < Nx && iz < Nz) Bits[iz * Nx + ix] = true;
            }

            public bool Blocked(float wx, float wz)
            {
                int ix = (int)((wx - MinX) / Cell), iz = (int)((wz - MinZ) / Cell);
                if (ix < 0 || iz < 0 || ix >= Nx || iz >= Nz) return true;   // off the mask is off the island
                return Bits[iz * Nx + ix];
            }
        }

    }
}
