using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// PROCEDURAL ISLAND HEIGHTMAP (strawberry 2026-08-21: "lets just get a heightmap for now, on a small map,
    /// but configurable to a larger map later on. island. yes deterministic.")
    ///
    /// Writes into the grid Terrain already uses, so nothing downstream needs to know a map was generated: the
    /// result saves through Terrain.SaveHeightmap to the same editor_{name}_heightmap.bin every hand-made map
    /// uses, opens in the map editor, and can be sculpted afterwards. No new format, no new loader.
    ///
    /// DETERMINISM IS A REQUIREMENT, so the noise is built here rather than taken from FastNoiseLite. A seeded
    /// engine generator is reproducible only as long as the engine's implementation is, and "same seed, same
    /// map" has to survive a Godot upgrade -- the seed is the thing a player would share. Everything below is
    /// integer hashing and float arithmetic with no library calls, so the same seed produces bit-identical
    /// output on any build. gun.proc_island asserts exactly that.
    /// </summary>
    public static class ProcIsland
    {
        // Terrain stores heights NORMALISED: world Y = g * TILE_HEIGHT - TILE_HEIGHT/2. Everything in this file
        // works in WORLD METRES and converts once at the end, because a coastline argued about in 0.512-vs-0.515
        // is unreadable and the sea level is a world number.
        const float TileHeight = 2048f;
        public static float ToGrid(float worldY) => (worldY + TileHeight * 0.5f) / TileHeight;
        public static float ToWorld(float g) => g * TileHeight - TileHeight * 0.5f;

        public struct Params
        {
            public int Seed;
            public float SeaLevel;      // world Y of the water plane
            public float PeakHeight;    // world Y the highest inland ground reaches
            public float SeabedDepth;   // world Y the sea floor settles to off the coast
            public float CoastFalloff;  // >1 = a tighter island with more open water around it
            public float ShapeMetres;   // size of the biggest landform features, IN WORLD METRES
            public float WarpMetres;    // how far the coastline is dragged sideways, in metres
            public float WarpScale;     // the size of the warp's own swirls, in metres

            public static Params Default(int seed) => new()
            {
                Seed = seed,
                SeaLevel = 25.6f,       // Terrain.SeaLevelY's default, = PEI's 0.1 * 256
                PeakHeight = 96f,
                SeabedDepth = -18f,
                CoastFalloff = 2.0f,   // 1.7 grew an island that crowded the map edges
                ShapeMetres = 420f,     // 5 octaves from here = 420/210/105/52/26 m, so the coast has detail
                                        // down to ~26 m while the outline is decided at ~420 m.
                WarpMetres = 130f,
                // WARP WAVELENGTH MUST BE SMALLER THAN THE ISLAND. At 380 m on a ~700 m island the warp field
                // was near-constant across the whole landmass, so it TRANSLATED it -- the island slid off centre
                // and stretched into an oval instead of growing bays. Same failure as using one shared offset
                // for both axes, one level up: a distortion coarser than its subject is a move, not a distortion.
                WarpScale = 210f,
            };
        }

        // --- deterministic integer hash -> [0,1). Wang-style avalanche; no library, no float seeding. ---
        static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) + (uint)(y * 668265263) + (uint)(seed * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;   // 24 bits -> exactly representable in a float
            }
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        static float ValueNoise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = Smooth(x - xi), fy = Smooth(y - yi);
            float a = Hash01(xi, yi, seed), b = Hash01(xi + 1, yi, seed);
            float c = Hash01(xi, yi + 1, seed), d = Hash01(xi + 1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        /// <summary>Fractal sum. Normalised by the actual amplitude sum, not by an assumed 2 -- otherwise the
        /// octave count silently becomes a contrast knob and re-tuning octaves shifts every coastline.</summary>
        static float Fbm(float x, float y, int seed, int octaves = 5, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, norm = 0f, fx = x, fy = y;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * ValueNoise(fx, fy, seed + i * 1013);
                norm += amp;
                amp *= gain; fx *= lacunarity; fy *= lacunarity;
            }
            return sum / norm;
        }

        /// <summary>Fill a Terrain grid (normalised heights, [x,y]) with an island. gw/gh are the grid dims
        /// Terrain.CreateFlat produced, so size is whatever the caller built -- that is the whole of
        /// "configurable to a larger map later".</summary>
        public static void Fill(float[,] grid, int gw, int gh, Params p)
        {
            // WORLD METRES, NOT GRID CELLS. The first version scaled the noise in cells, which made every
            // feature size depend on the map's resolution -- and worse, hid the real bug: at 190 cells on a
            // 257-cell grid the whole map spanned 1.35 noise units, so the fBm never varied and the result was
            // a smooth dome. Every statistical check still passed (land fraction, closed coast, height range);
            // only looking at the rendered heightmap showed a dinner plate. Metres also make "the same seed at
            // a bigger size" mean a LARGER island rather than a stretched one.
            const float Unit = 4f;   // Terrain.UNIT: world metres per grid cell
            float cx = (gw - 1) * 0.5f, cy = (gh - 1) * 0.5f;
            float maxR = Mathf.Min(cx, cy);   // MIN, not the diagonal: on a non-square map the short axis decides
                                              // whether the coast closes; a diagonal radius runs the island off
                                              // the short edges while leaving water down the long ones.
            for (int x = 0; x < gw; x++)
            {
                for (int y = 0; y < gh; y++)
                {
                    float mx = x * Unit, my = y * Unit;

                    // Warp the SAMPLE POSITION, and use two different offsets per axis -- one shared offset
                    // moves every point along the same diagonal, which slides the island instead of deforming
                    // its coast. Applied before the radial term so the falloff circle itself is distorted;
                    // warping only the noise leaves a circular coast with a rough edge.
                    float wx = (ValueNoise(mx / p.WarpScale, my / p.WarpScale, p.Seed + 7717) - 0.5f) * 2f * p.WarpMetres;
                    float wy = (ValueNoise(mx / p.WarpScale + 31.7f, my / p.WarpScale - 12.3f, p.Seed + 991) - 0.5f) * 2f * p.WarpMetres;

                    float gx = x + wx / Unit, gy = y + wy / Unit;
                    float dx = (gx - cx) / maxR, dy = (gy - cy) / maxR;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);

                    float fall = Mathf.Pow(Mathf.Clamp(1f - r, 0f, 1f), p.CoastFalloff);

                    float n = Fbm((mx + wx) / p.ShapeMetres, (my + wy) / p.ShapeMetres, p.Seed);

                    // THE MASK DECIDES WHERE THE COAST IS. Multiplied, not added: adding noise to a falloff
                    // floats islands off the coast and punches lakes through the middle, while multiplying
                    // keeps ONE landmass whose outline is the noise.
                    float land = fall * (0.20f + 0.80f * n);

                    // ...AND THAT IS ALL IT DECIDES. Elevation is a SEPARATE field, because deriving height
                    // from the same radial mask makes the whole interior a function of distance-from-centre --
                    // concentric rings, a dome with a red middle, no matter how much noise is in the outline.
                    // The second render showed exactly that: a believable coast wrapped around a smooth cone.
                    // `inland` only fades the relief out at the shoreline so beaches stay low.
                    float inland = Mathf.SmoothStep(0.06f, 0.13f, land);   // saturates JUST inland of the shore. At 0.34 it
                    // was still climbing across the whole island, so height tracked distance-from-centre through
                    // this term and the interior came out a smooth dome with a high middle -- the exact thing
                    // separating relief from the mask was meant to fix.
                    float relief = Fbm(mx / (p.ShapeMetres * 0.55f), my / (p.ShapeMetres * 0.55f), p.Seed + 2027);
                    // Ridged: folding about 0.5 turns rounded hummocks into ridgelines, which is what reads as
                    // terrain at eye height rather than from above.
                    float ridged = 1f - Mathf.Abs(Fbm(mx / (p.ShapeMetres * 0.30f), my / (p.ShapeMetres * 0.30f), p.Seed + 4241) * 2f - 1f);
                    float relief01 = Mathf.Clamp(0.55f * relief + 0.45f * ridged, 0f, 1f);
                    // BOTTOM-HEAVY, deliberately. fBm is centred near 0.5, so feeding it straight in puts the
                    // whole island near peak height -- a mesa with a nice coastline, which is what the previous
                    // render was. Raising it to a power pushes the bulk of the land low and leaves the high
                    // ground as isolated hills, which is both what real terrain does and what leaves somewhere
                    // flat enough to put a town on later.
                    relief01 = Mathf.Pow(relief01, 2.2f);

                    float world = land > 0.06f
                        ? p.SeaLevel - 1.5f + (p.PeakHeight - (p.SeaLevel - 1.5f)) * inland * (0.07f + 0.93f * relief01)
                        : Mathf.Lerp(p.SeabedDepth, p.SeaLevel - 1.5f, land / 0.06f);

                    grid[x, y] = ToGrid(world);
                }
            }
        }
    }
}
