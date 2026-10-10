using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    /// <summary>Plants a POOL of resource models across a map's terrain and writes the `.bin` placement files
    /// ResourceField already loads (strawberry 2026-10-10: "wire up redwood and cacti foliage like existing trees
    /// as spawns from the pools of model").
    ///
    /// ⚠ THIS IS A GENERATOR, NOT A RUNTIME SYSTEM. It runs once from `--scatterres`, writes `.bin` files, and the
    /// committed result is what ships -- so a planted world is bit-identical on every peer with no new
    /// determinism surface, and redwoods load through exactly the path Birch and Pine do. "Like existing trees"
    /// is meant literally: after this runs there is nothing special about them.
    ///
    /// ⚠⚠ ON INVENTING SPAWN RULES. EditorFoliage deliberately does NOT implement retail's BAKE, because the
    /// per-asset rules live in FoliageInfoAsset which this port has never extracted, and inventing them "would
    /// produce a scatter that looks authored but matches nothing in the source". That reasoning is sound and is
    /// NOT violated here: redwood and cactus have no retail resource at all (searched the masterbundle rip with
    /// Birch/Pine as controls -- 278/431 hits against 0 for both), so there is no source truth for an authored
    /// rule to contradict. These rules are ours because they have to be, not because the real ones were skipped.</summary>
    public static class ResourceScatter
    {
        /// <summary>One plantable pool: some models, and the ground they belong on.</summary>
        public sealed class Pool
        {
            public string Name;                        // for the log + the RNG seed
            public string[] Models;                    // the pool -- each instance picks one at random
            public PlayerController.Surf[] Surfaces;   // ground it will grow on
            public float PerSqKm = 400f;               // density, per km^2 of QUALIFYING ground (not of the map)
            public float MinSpacing = 12f;             // metres between two of these
            public float MaxSlope = 0.35f;             // rise/run; a redwood does not grow on a cliff
            public float MinHeight = 2f;               // metres ABOVE THE WATERLINE -- nothing plants in the surf
            public float ScaleMin = 0.9f, ScaleMax = 1.15f;
        }

        public static readonly Pool Redwood = new Pool
        {
            Name = "Redwood",
            Models = new[] { "Redwood_0", "Redwood_1", "Redwood_2" },
            // Forest ground. NOT sand or gravel: a 60-78 m tree needs soil, and seeing one on a beach would
            // read as a bug faster than any amount of density tuning.
            Surfaces = new[] { PlayerController.Surf.Grass, PlayerController.Surf.Dirt },
            PerSqKm = 55f,          // RARE on purpose: these are 3x the height of every other tree, so a stand
                                    // of them is a landmark. At PEI's ~4 km^2 of land that is a couple of hundred.
            MinSpacing = 26f,       // their canopies are 22-26 m across -- any closer and they interpenetrate
            MaxSlope = 0.30f,
            MinHeight = 3f,
            ScaleMin = 0.85f, ScaleMax = 1.2f,
        };

        public static readonly Pool Cactus = new Pool
        {
            Name = "Cactus",
            Models = new[] { "Cactus_Column_0", "Cactus_Column_1", "Cactus_Column_2",
                             "Cactus_Branched_0", "Cactus_Branched_1", "Cactus_Branched_2",
                             "Cactus_Barrel_0", "Cactus_Barrel_2",
                             "Cactus_Paddles_0", "Cactus_Paddles_1", "Cactus_Paddles_2",
                             "Cactus_Cluster_0", "Cactus_Cluster_1", "Cactus_Cluster_2" },
            Surfaces = new[] { PlayerController.Surf.Sand },
            PerSqKm = 900f,
            MinSpacing = 5f,
            MaxSlope = 0.40f,
            // ⚠ 6 m, much higher than the redwood's 3. On the maps that exist today the only Sand is BEACH, and
            // a cactus at the waterline is the single most obviously wrong place to put one. This keeps them on
            // dunes and dry inland sand and accepts planting none at all on a map that has no such ground --
            // which is the correct outcome for PEI/Washington/Yukon until a desert map exists.
            MinHeight = 6f,
            ScaleMin = 0.8f, ScaleMax = 1.35f,
        };

        /// <summary>Plant every pool over the active terrain and write the .bin files into <paramref name="outDir"/>.
        /// Returns the total planted. Deterministic: same terrain + same seed = same world, every time.</summary>
        public static int Run(Terrain terrain, string outDir, float halfExtent, uint seed = 20261010u)
        {
            if (terrain == null) { Log.Err("[scatter] no terrain"); return 0; }
            Directory.CreateDirectory(outDir);
            var census = Census(terrain, halfExtent);
            int total = 0;
            foreach (var pool in new[] { Redwood, Cactus }) total += Plant(terrain, pool, outDir, halfExtent, seed, census);
            return total;
        }

        /// <summary>What this map's ground actually IS, before anything is planted on it. A density is only
        /// meaningful against the area that qualifies, and "the pool hit its target" cannot distinguish a map
        /// with the right ground from one whose splatmap is being misread -- so the census is printed every run
        /// and names the thing it measured.</summary>
        static Dictionary<PlayerController.Surf, float> Census(Terrain terrain, float halfExtent)
        {
            var count = new Dictionary<PlayerController.Surf, int>();
            int above = 0, n = 0;
            const int Step = 16;   // metres; ~65k samples over a 4 km square
            for (float x = -halfExtent; x < halfExtent; x += Step)
                for (float z = -halfExtent; z < halfExtent; z += Step)
                {
                    float y = terrain.SampleHeight(x, z);
                    n++;
                    if (y - Terrain.SeaLevelY < 0.5f) continue;   // seabed: not ground anything plants on (see the waterline note in Plant)
                    above++;
                    var sf = terrain.SurfAt(x, z);
                    count.TryGetValue(sf, out int c); count[sf] = c + 1;
                }
            var parts = new List<string>();
            foreach (var kv in count) parts.Add($"{kv.Key} {kv.Value * 100f / Mathf.Max(1, above):0.#}%");
            parts.Sort();
            Log.Print($"[scatter] ground census: {above}/{n} samples above water -> {string.Join(", ", parts)}");
            // km^2 of each surface, so a pool's density means "per km^2 of ground it can actually use".
            float cellKm2 = (Step * Step) / 1_000_000f;
            var area = new Dictionary<PlayerController.Surf, float>();
            foreach (var kv in count) area[kv.Key] = kv.Value * cellKm2;
            return area;
        }

        static int Plant(Terrain terrain, Pool pool, string outDir, float halfExtent, uint seed,
                         Dictionary<PlayerController.Surf, float> area)
        {
            // Seeded off the POOL NAME as well as the run seed, so adding a pool cannot shift an existing one's
            // placements -- re-running after a new pool lands must not move every redwood in the world.
            uint s = seed ^ Hash(pool.Name);
            // ⚠ TARGET COMES FROM THE QUALIFYING AREA, not the map's bounding square. Against the square, a
            // cactus density meant for a desert asked PEI for 15,099 of them and then spent 1.8 MILLION rejected
            // samples failing to find room -- the number was not just slow, it was meaningless, because "per km^2
            // of map" says nothing about a surface that covers 2% of it. Per km^2 of ground the pool can USE is
            // the same number on every map, which is what makes one density portable.
            float usable = 0f;
            foreach (var sf in pool.Surfaces) { area.TryGetValue(sf, out float a); usable += a; }
            int target = Mathf.RoundToInt(pool.PerSqKm * usable);
            var placed = new List<Vector3>();
            var byModel = new Dictionary<string, List<Transform3D>>();
            foreach (var m in pool.Models) byModel[m] = new List<Transform3D>();

            // Rejection sampling with a spacing test. Tries hard but gives up: a map with no qualifying ground
            // must plant NOTHING rather than spin, and must say so.
            int attempts = 0, maxAttempts = target * 120 + 2000;
            // WHY a sample was refused, counted. Without this a pool that plants nothing and a pool that plants
            // far too much look identical from the outside -- a single total cannot tell "there is no sand here"
            // from "the whole island reads as sand", and those need opposite fixes.
            int rejLow = 0, rejSurf = 0, rejSlope = 0, rejSpace = 0;
            float sp2 = pool.MinSpacing * pool.MinSpacing;
            while (placed.Count < target && attempts++ < maxAttempts)
            {
                float x = (NextFloat(ref s) * 2f - 1f) * halfExtent;
                float z = (NextFloat(ref s) * 2f - 1f) * halfExtent;
                float y = terrain.SampleHeight(x, z);
                // ⚠⚠ ABOVE THE WATERLINE, not above y=0. PEI's sea level is 25.6 (0.1 * 256 from the map's own
                // seaLevel, and it is PER-MAP), so a raw-Y gate of 6 sits TWENTY METRES UNDERWATER: the first run
                // of this planted 15,099 cacti, essentially all of them on the sandy SEABED, and reported a
                // confident 100% of target while doing it. The ground census is what exposed it -- "Sand 56%" on
                // a green farming island is not a density problem, it is a seabed being counted as land.
                if (y - Terrain.SeaLevelY < pool.MinHeight) { rejLow++; continue; }
                if (!Has(pool.Surfaces, terrain.SurfAt(x, z))) { rejSurf++; continue; }
                // Slope from the same central difference the terrain's own steepness repaint uses.
                const float d = 2f;
                float hx = (terrain.SampleHeight(x + d, z) - terrain.SampleHeight(x - d, z)) / (2f * d);
                float hz = (terrain.SampleHeight(x, z + d) - terrain.SampleHeight(x, z - d)) / (2f * d);
                if (Mathf.Sqrt(hx * hx + hz * hz) > pool.MaxSlope) { rejSlope++; continue; }
                bool clear = true;
                foreach (var p in placed)
                { float dx = p.X - x, dz = p.Z - z; if (dx * dx + dz * dz < sp2) { clear = false; break; } }
                if (!clear) { rejSpace++; continue; }

                string model = pool.Models[(int)(NextFloat(ref s) * pool.Models.Length) % pool.Models.Length];
                float yaw = NextFloat(ref s) * 360f;
                float sc = Mathf.Lerp(pool.ScaleMin, pool.ScaleMax, NextFloat(ref s));
                placed.Add(new Vector3(x, y, z));
                byModel[model].Add(new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(yaw)).Scaled(Vector3.One * sc),
                                                   new Vector3(x, y, z)));
            }

            foreach (var kv in byModel) WriteBin(Path.Combine(outDir, kv.Key + ".bin"), kv.Value);
            Log.Print($"[scatter] {pool.Name}: planted {placed.Count}/{target} across {pool.Models.Length} models "
                    + $"on {usable:0.##} km2 of usable ground "
                    + $"({attempts} attempts){(placed.Count == 0 ? "  -- NO QUALIFYING GROUND ON THIS MAP" : "")}");
            Log.Print($"[scatter]   refused: {rejLow} under {pool.MinHeight:0.#} m above water (sea y={Terrain.SeaLevelY:0.#}), {rejSurf} wrong surface, "
                    + $"{rejSlope} too steep, {rejSpace} too close");
            return placed.Count;
        }

        /// <summary>Write the placement file ResourceField.ReadInstances reads back: an int32 count then nine
        /// floats per instance (pos, euler degrees, scale).
        /// ⚠⚠ Z IS STORED NEGATED. The reader builds its position as `(px, py, -pz)` -- every placement file in
        /// this project is in the +Z = world -Z convention -- so writing the world Z straight through mirrors the
        /// whole scatter about the Z axis, which on a symmetrical-looking island is a bug you can stare past.
        /// ⚠ The euler must also round-trip the reader's `Y(180 - ey)`: a yaw of `ey` comes back as 180 - ey, so
        /// the value written is 180 - yaw.</summary>
        static void WriteBin(string path, List<Transform3D> xs)
        {
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(xs.Count);
            foreach (var t in xs)
            {
                var sc = t.Basis.Scale;
                float yaw = Mathf.RadToDeg(Mathf.Atan2(-t.Basis.X.Z, t.Basis.X.X));
                bw.Write(t.Origin.X); bw.Write(t.Origin.Y); bw.Write(-t.Origin.Z);
                bw.Write(0f); bw.Write(180f - yaw); bw.Write(0f);
                bw.Write(sc.X); bw.Write(sc.Y); bw.Write(sc.Z);
            }
        }

        static bool Has(PlayerController.Surf[] set, PlayerController.Surf s)
        { foreach (var v in set) if (v == s) return true; return false; }

        static uint Hash(string s)
        { uint h = 2166136261u; foreach (char c in s) { h ^= c; h *= 16777619u; } return h; }

        /// <summary>xorshift32, the same family the material palettes use: a named, reproducible generator rather
        /// than GD.Randf, because this writes FILES and a run has to be repeatable to be reviewable.</summary>
        static float NextFloat(ref uint s)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            return (s & 0xFFFFFF) / 16777216f;
        }
    }
}
