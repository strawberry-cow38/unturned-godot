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
            public int Towns, Bases, Sites;   // POIs of each kind PER ~0.45 km2 of land -- see PlacePois
            public float SmoothStrength;      // 0..1 of a box blur applied after flattening

            /// <summary>OFF BY DEFAULT (strawberry 2026-09-17: "have it as an off-by-default toggle when
            /// generating the world"). Lakes are a taste thing, not a correctness thing, and an island without
            /// them is the one most people expect.</summary>
            public bool Lakes;
            public float LakeThreshold, LakeDepth, LakeMaxRise;

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
                Towns = 1, Bases = 1, Sites = 2,
                SmoothStrength = 0.55f,
                // How much of the inland relief field becomes water. fBm sits near 0.5, so 0.62 takes roughly
                // the top sixth of it -- a couple of ponds and the odd proper lake per island rather than a
                // flooded interior. Depth is measured DOWN from the waterline, so it is also how deep you can
                // swim in one.
                Lakes = false,
                // ⚠ RARER AND SMALLER (strawberry: "reduce the size and number of lakes by a lot"). fBm sits
                // near 0.5, so 0.76 takes a sliver off the top of the field instead of the top sixth -- a pond
                // or two per island rather than seven lakes.
                LakeThreshold = 0.56f,
                LakeDepth = 4f,
                // ⭐ AND ONLY WHERE THE GROUND IS ALREADY LOW (strawberry: "should carve already low terrain
                // instead of forming cliffs"). THIS is the rule that stops the cliffs, and it is a different
                // rule from making them rarer. A basin blends the existing height down to a bed, so cutting one
                // into a hillside leaves the rim standing as a wall exactly as tall as the ground was -- the
                // depth control cannot help, because the wall is made of the terrain that was already there.
                // Refusing ground more than this far above the waterline means the rim can never be taller
                // than that, whatever the noise says.
                // ⚠ 12 m, AND THE NUMBER CAME OFF A HISTOGRAM. Two guesses failed first: 5 m admitted ZERO of
                // 147457 inland cells. Measuring the distribution instead of reasoning about the height formula
                // showed why -- inland ground on this relief curve runs 5 to 60 m above the waterline with a
                // median around 27, and the 5-10 m band holds 77 cells. Under 15 m is 4118 of them, about 3% of
                // the island, which is the "already low terrain" that actually exists.
                //
                // ⚠⚠ AND THERE IS A HARD CEILING HERE, not a tuning preference. There is ONE waterline for the
                // whole map, so an inland lake can only exist BELOW it -- which means a basin on ground 27 m up
                // has to cut 27 m down, and its rim is a 27 m wall made of the terrain that was already there.
                // No depth setting can soften that. Refusing high ground is the only thing that can, which is
                // exactly what master asked for: "should carve already low terrain instead of forming cliffs".
                LakeMaxRise = 12f,
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
            int lakeCells = 0, lakeInland = 0, lakeLow = 0, lakeNoisy = 0;
            var lakeHist = new int[12];
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

                    // ---- INLAND PONDS AND LAKES (strawberry 2026-09-16: "allow inland ponds/lakes to spawn")
                    //
                    // ⭐ NOTHING HAS TO DRAW THEM. BuildOceanMesh emits a quad wherever a corner samples below
                    // SeaLevelY -- it is not a ring around the island, it is a waterline over the whole map --
                    // so a basin cut below sea level IS a lake the moment the terrain says so. Everything else
                    // follows for the same reason: PlacePois builds its candidate list from cells ABOVE sea
                    // level so nothing gets sited in one, Route2D prices water at +400 so roads go round, and
                    // the sand band at SeaLevelY+4 gives each one a shore without a second rule.
                    //
                    // ⚠ THE NOTE ABOVE SAYS ADDING NOISE TO THE MASK "punches lakes through the middle" -- and
                    // it was right, which is why this is NOT done that way. Doing it in the mask would also
                    // break the island into pieces off the coast, because the mask is what decides where land
                    // IS. A lake is a hole in the HEIGHT, cut after the outline is settled, so the coastline is
                    // untouched.
                    //
                    // Only well inland (`inland` is saturated a little past the shore), so a lake never opens
                    // onto the sea and becomes a bay. Squared basin profile: a flat-bottomed pan with a rim
                    // that climbs, rather than a cone.
                    float lakeN = Fbm(mx / (p.ShapeMetres * 0.42f), my / (p.ShapeMetres * 0.42f), p.Seed + 8821);
                    if (inland > 0.985f)
                    {
                        lakeInland++;
                        // ⚠ THE TWO GATES COUNTED SEPARATELY. Tightening both at once took the lake count to
                        // ZERO and the single "did it flood" number could not say which one did it -- low
                        // ground is rare on this relief curve (world < sea+5 needs relief01 under 0.008) and a
                        // 0.76 noise cut is rare too, so their intersection was empty. A pair of conditions
                        // needs a pair of counters or tuning it is guesswork.
                        if (world < p.SeaLevel + p.LakeMaxRise) lakeLow++;
                        if (lakeN > p.LakeThreshold) lakeNoisy++;
                        // ⚠ THE DISTRIBUTION, not just the pass count. "0 of 147457 cells are low enough" says
                        // the gate is wrong but not what to set it to, and the previous two numbers were both
                        // picked by reasoning about the height formula rather than by looking at what it
                        // produces. A histogram of rise-above-the-waterline answers it directly.
                        int band = Mathf.Clamp(Mathf.FloorToInt((world - p.SeaLevel) / 5f), 0, lakeHist.Length - 1);
                        lakeHist[band]++;
                    }
                    // ⚠ `world` here is the land BEFORE any basin is cut, which is what the low-ground test has
                    // to read: once a cell is carved it is low by definition, and testing after would admit
                    // every cell the first pass touched.
                    if (p.Lakes && inland > 0.985f && lakeN > p.LakeThreshold && world < p.SeaLevel + p.LakeMaxRise)
                    {
                        // ⚠ SATURATE, DO NOT TAPER. The first cut used `basin = depth01^2` over the whole range
                        // above the threshold, which sounds like a nice dish and is in practice a rule that
                        // almost never reaches water: inland ground sits about 3.5 m above the waterline, so a
                        // cell had to reach basin > 0.33, i.e. fBm above 0.84, which it hardly ever is. 19600
                        // cells were "cut" and next to none of them flooded -- the change was real, gentle, and
                        // invisible. Saturating over a narrow band gives a flat-bottomed pan with a defined
                        // shore, which is both what a lake looks like and what actually goes under.
                        // Wider band than the first cut: the transition's WIDTH in metres is what decides how
                        // steep the shore is, and a narrow band on a smooth noise field is a short, steep rim.
                        float basin = Mathf.SmoothStep(p.LakeThreshold, p.LakeThreshold + 0.16f, lakeN);
                        // Blend DOWN to a bed below the waterline. Lerp rather than subtract so the rim meets
                        // the surrounding land exactly at basin = 0 and there is no lip around the shore.
                        float bed = p.SeaLevel - p.LakeDepth;
                        world = Mathf.Lerp(world, bed, basin);
                    }
                    // ⚠ THE METRIC IS "DID IT FLOOD", not "did the rule touch it". Counting cells the rule ran
                    // on reported 19600 lakes on an island with none you could see.
                    if (inland > 0.985f && world < p.SeaLevel) lakeCells++;

                    grid[x, y] = ToGrid(world);
                }
            }
            // ⚠ COUNTED, because "I did not see a lake" and "no lake was cut" are different failures and the
            // fix for each is in a different place. Inland cells is the population the threshold selects FROM;
            // if that is small the gate is too strict, and if it is large but the lake count is zero the
            // threshold is.
            Log.Print($"[island-water] lakes={(p.Lakes ? "on" : "off")}: {lakeCells} cell(s) below the waterline"
                      + $" | inland {lakeInland}, of which low-enough {lakeLow} and noisy-enough {lakeNoisy}"
                      + $" (threshold {p.LakeThreshold:0.00}, max rise {p.LakeMaxRise:0.#} m, depth {p.LakeDepth:0.#} m)");
            var hs = new System.Text.StringBuilder("[island-water] inland height above waterline:");
            for (int b = 0; b < lakeHist.Length; b++) if (lakeHist[b] > 0) hs.Append($" {b * 5}-{b * 5 + 5}m={lakeHist[b]}");
            Log.Print(hs.ToString());
        }

        // ---------------------------------------------------------------- POIs

        public enum PoiKind { Town, MilitaryBase, ConstructionSite }

        /// <summary>A place something will be BUILT. Position and radius are world metres, so this survives any
        /// later change of grid resolution -- these are meant to be read by the road/building/loot stages, and a
        /// marker expressed in grid cells would silently mean a different place at a different map size.</summary>
        public readonly struct Poi
        {
            /// <summary>HALF-EXTENT of an axis-aligned square footprint, in world metres -- so the pad is
            /// HalfSize*2 across. Square rather than round (strawberry 2026-08-21: "they should use squares
            /// instead of circles and be much smaller"), which also suits what goes on them: a street grid, a
            /// compound fence and a site hoarding are all rectangular, and a round pad would have to be
            /// re-squared by every stage that builds on it.</summary>
            /// <summary>⚠ THE LATTICE SIZE IS PER-MONUMENT, NOT PER-KIND (strawberry 2026-09-17: "cities can be
            /// bigger than what we currently generate"). It used to be TilesFor(Kind) -- one number for every
            /// town on every island -- which is the same shape of sameness StreetPlanFor was written to end, one
            /// level up: not just the street pattern repeating, but the town's SIZE.</summary>
            public readonly PoiKind Kind; public readonly float X, Z, HalfSize, GroundY; public readonly int Tiles;
            public Poi(PoiKind k, float x, float z, float half, float y, int tiles = 0)
            { Kind = k; X = x; Z = z; HalfSize = half; GroundY = y; Tiles = tiles > 0 ? tiles : TilesFor(k); }
            public override string ToString() => $"{Kind} @ ({X:0},{Z:0}) {HalfSize * 2f:0}m sq y{GroundY:0.#}";
        }

        // The road kit is a 24 m lattice (every piece has its connectors at +/-12, carriageway at z 0.4), so a
        // monument's half-extent is 12 * tiles EXACTLY. Snapped from 55/40/25 for that reason: at 55 the outer
        // tile edge lands 5 m inside the footprint and every gate sits on nothing. 5, 3 and 2 tiles across.
        public const float TileSize = 24f;
        public static int TilesFor(PoiKind k) => k switch { PoiKind.Town => 5, PoiKind.MilitaryBase => 3, _ => 2 };

        /// <summary>How many tiles across THIS monument is. Towns roll a size; everything else keeps its kind's.
        ///
        /// ⚠ ODD ONLY. The street lines are interior and non-adjacent, and a monument whose gates must land on
        /// one needs the same parity on both axes -- an even lattice has no centre line and its corner cells are
        /// all boundary, which is the exact shape the kit cannot express (see FillsGrid's note about 2x2).
        /// 3 is a hamlet, 5 the old default, 7 and 9 the cities master asked to be able to exceed the old size.</summary>
        public static int TilesForPoi(int poiIndex, PoiKind kind, int seed)
        {
            if (kind != PoiKind.Town) return TilesFor(kind);
            float r = Hash01(poiIndex * 313 + 7, 23, seed + 51001);
            return r < 0.28f ? 3 : r < 0.66f ? 5 : r < 0.90f ? 7 : 9;
        }

        /// <summary>What a town IS, by how much road actually got built on it (strawberry: "each town needs
        /// flags depending on the number of road pieces in the town to determine its 'size'").
        ///
        /// ⚠ COUNTED FROM THE TILES THAT SURVIVED, not from the size it was reserved at. A monument's lattice is
        /// what it was given; its piece count is what the routing, the dead-end prune and the exit fitting left
        /// behind -- and master's own example is about the result ("the monuments with just 2 road line caps are
        /// just 'monument'"), not the intent. A 9-tile city site whose grid mostly failed is a hamlet, and
        /// should be built like one.</summary>
        public enum TownSize { Monument, Small, Medium, City }

        public static TownSize SizeOf(int tileCount) =>
            tileCount <= 3 ? TownSize.Monument
          : tileCount <= 9 ? TownSize.Small
          : tileCount <= 19 ? TownSize.Medium
          : TownSize.City;

        /// <summary>Does this monument get a full street grid, or just an access road?
        ///
        /// A town is streets; a construction site is a track in to a compound and nothing else, which is what it
        /// looks like in life. It is also the only answer that WORKS at 2 tiles: on a 2x2 every cell is a corner,
        /// and a corner exit needs {ramp, inward, lateral} -- the one shape no piece in the kit expresses.</summary>
        public static bool FillsGrid(PoiKind k) => k is PoiKind.Town or PoiKind.MilitaryBase;
        static float HalfSizeFor(PoiKind k) => TilesFor(k) * TileSize * 0.5f;   // 60 / 36 / 24 m

        /// <summary>World height at a grid cell, clamped to the grid.</summary>
        static float HeightAt(float[,] g, int gw, int gh, int x, int y) =>
            ToWorld(g[Mathf.Clamp(x, 0, gw - 1), Mathf.Clamp(y, 0, gh - 1)]);

        /// <summary>Place POIs, flatten a pad under each, then smooth. Returns them in placement order, which is
        /// deterministic for a given seed -- the later stages need a stable identity per POI, not just a set.</summary>
        /// <summary>Why the last run's candidates were rejected, per kind. A POI that fails to place is silent
        /// otherwise -- the list just comes back shorter -- so this is what turns "no base appeared" into which
        /// constraint actually refused it.</summary>
        public static string LastRejectReport = "";

        public static System.Collections.Generic.List<Poi> PlacePois(float[,] grid, int gw, int gh, Params p)
        {
            const float Unit = 4f;

            // COUNTS SCALE WITH LAND AREA, because POI sizes are real-world metres and a small island simply
            // cannot hold a fixed number of them. Asking for 2 towns + a base + 2 sites on a 1024 m map placed
            // 1 town, 0 bases and 2 sites -- the shortfall landed on whichever kind came later in the order,
            // which is arbitrary and was invisible to a check that counted POIs instead of kinds. Densities
            // keep a small map sparse and a large one populated without either being a special case.
            // SAMPLE FROM LAND, NOT FROM THE MAP. Candidates were drawn uniformly over the whole grid, and an
            // island covers roughly a quarter of it -- so ~9 in 10 attempts died in open water before any real
            // constraint was tested (measured: 537 of a military base's 600 attempts rejected as wet/edge, with
            // only 37 rejected for the reason that actually mattered). Drawing from the land list spends every
            // attempt on a candidate that could plausibly work, and makes placement independent of how much of
            // the map happens to be sea.
            var land = new System.Collections.Generic.List<(int X, int Y)>();
            for (int x = 0; x < gw; x++)
                for (int y = 0; y < gh; y++)
                    if (ToWorld(grid[x, y]) > p.SeaLevel) land.Add((x, y));
            int landCells = land.Count;
            if (landCells == 0) { LastRejectReport = "no land"; return new System.Collections.Generic.List<Poi>(); }
            float km2 = landCells * Unit * Unit / 1_000_000f;
            int mult = Mathf.Max(1, Mathf.RoundToInt(km2 / 0.45f));

            // LARGEST FIRST, deliberately: a town needs the biggest clear area, so letting the small sites go
            // first lets them take the only spot a town would have fitted in and the town then fails.
            var want = new System.Collections.Generic.List<PoiKind>();
            for (int i = 0; i < p.Towns * mult; i++) want.Add(PoiKind.Town);
            for (int i = 0; i < p.Bases * mult; i++) want.Add(PoiKind.MilitaryBase);
            for (int i = 0; i < p.Sites * mult; i++) want.Add(PoiKind.ConstructionSite);

            var placed = new System.Collections.Generic.List<Poi>();
            var report = new System.Text.StringBuilder();
            int attempt = 0;
            foreach (var kind in want)
            {
                // ⚠ RESERVE THE SIZE THIS ONE WILL ACTUALLY BE. The roll has to happen BEFORE the search, not
                // after it: a 9-tile city reserved at the old 5-tile half-extent is sited on a patch that cannot
                // hold it, and every later stage reads Poi.HalfSize as the truth about how much room there is.
                int tiles = TilesForPoi(placed.Count, kind, p.Seed);
                float half = tiles * TileSize * 0.5f;
                bool got = false;
                int rWet = 0, rCliff = 0, rClash = 0;
                // BEST-CANDIDATE, not first-fit. Taking the first valid spot clusters everything into whichever
                // corner the hash happened to favour -- all four monuments landed in the upper-right quadrant and
                // two thirds of the land had nothing on it (strawberry: "spread monuments across the whole
                // island"). Instead: consider every valid candidate and keep the one FARTHEST from anything
                // already placed. Note the loop no longer breaks on `got` -- it must see them all to choose.
                float bestScore = -1f, bx = 0f, bz = 0f, by = 0f;
                for (int tries = 0; tries < 600; tries++, attempt++)
                {
                    int pick = (int)(Hash01(attempt, 11, p.Seed + 5551) * (landCells - 1));
                    var cell = land[Mathf.Clamp(pick, 0, landCells - 1)];
                    int cxg = cell.X, cyg = cell.Y;
                    float wx = cxg * Unit, wz = cyg * Unit;

                    // INLAND BY THE FOOTPRINT, not by the whole skirt. Checked on a ring rather than at the
                    // centre, because a centre well above sea level says nothing about a site half of which
                    // hangs over water. But requiring the FULL skirt (radius*1.6) to be dry was too strict to
                    // satisfy: on a 1024 m map it silently placed ZERO towns -- both were requested, both
                    // failed, and `pois.Count >= 3` still passed on two construction sites and a base. The
                    // skirt only tapers, so letting it reach the shore grades a slope into the beach rather
                    // than cutting a shelf; the footprint itself still has to be solid ground.
                    float pad = half * 1.15f;
                    int ringCells = Mathf.CeilToInt(pad / Unit);
                    bool dry = HeightAt(grid, gw, gh, cxg, cyg) > p.SeaLevel + 3f;
                    float lo = float.MaxValue, hi = float.MinValue;
                    // Walk the SQUARE's perimeter, not a circle's: the corners reach 1.41x further than the
                    // edge midpoints, and they are exactly where a square pad hangs over water that a circular
                    // probe would have declared clear.
                    for (int a = 0; a < 16 && dry; a++)
                    {
                        float t = a / 16f * 4f;   // 0..4 around the perimeter
                        int side = (int)t; float f = t - side;
                        float ux = side switch { 0 => -1f + 2f * f, 1 => 1f, 2 => 1f - 2f * f, _ => -1f };
                        float uy = side switch { 0 => -1f, 1 => -1f + 2f * f, 2 => 1f, _ => 1f - 2f * f };
                        int rx = cxg + Mathf.RoundToInt(ux * ringCells);
                        int ry = cyg + Mathf.RoundToInt(uy * ringCells);
                        if (rx < 0 || ry < 0 || rx >= gw || ry >= gh) { dry = false; break; }
                        float h = HeightAt(grid, gw, gh, rx, ry);
                        if (h <= p.SeaLevel + 1.5f) dry = false;
                        lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h);
                    }
                    if (!dry) { rWet++; continue; }

                    // ...and not on a cliff. Flattening a 40 m spread produces a plateau with a sheer wall around
                    // it, which reads far worse than the hill it replaced.
                    if (hi - lo > 26f) { rCliff++; continue; }

                    bool clash = false;
                    foreach (var o in placed)
                    {
                        // CHEBYSHEV, to match square footprints: two squares overlap when they overlap on BOTH
                        // axes, and a Euclidean test would call diagonal neighbours clear while their corners
                        // sit inside each other. 30 m of clear ground between them.
                        float gap = Mathf.Max(Mathf.Abs(o.X - wx), Mathf.Abs(o.Z - wz));
                        if (gap < o.HalfSize + half + 30f) { clash = true; break; }
                    }
                    if (clash) { rClash++; continue; }

                    // Score = distance to the nearest already-placed monument. The FIRST has nothing to be far
                    // from, so it scores by how far inland it survived the ring test instead -- an anchor in the
                    // body of the landmass rather than wherever the first valid hash landed, which tends to be a
                    // shoreline because there is simply more coast than interior.
                    float score;
                    if (placed.Count == 0) score = ringCells;
                    else
                    {
                        score = float.MaxValue;
                        foreach (var o in placed)
                        {
                            float ddx = o.X - wx, ddz = o.Z - wz;
                            score = Mathf.Min(score, Mathf.Sqrt(ddx * ddx + ddz * ddz));
                        }
                    }
                    if (score > bestScore) { bestScore = score; bx = wx; bz = wz; by = HeightAt(grid, gw, gh, cxg, cyg); got = true; }
                }
                if (got) placed.Add(new Poi(kind, bx, bz, half, by, tiles));
                if (!got) report.Append($"{kind} FAILED (wet/edge {rWet}, cliff {rCliff}, too close {rClash}); ");
            }
            LastRejectReport = report.Length == 0 ? "all placed" : report.ToString();

            foreach (var poi in placed) Flatten(grid, gw, gh, poi);
            if (p.SmoothStrength > 0f) Smooth(grid, gw, gh, p.SmoothStrength);
            return placed;
        }

        /// <summary>Level the ground under a POI, blending out over a skirt so it does not become a mesa.</summary>
        static void Flatten(float[,] grid, int gw, int gh, Poi poi)
        {
            const float Unit = 4f;
            float target = ToGrid(poi.GroundY);
            // ⚠ THE PAD MUST CONTAIN THE ROADS ON IT, and it did not: BuildMonument puts its outermost tile
            // centre at (n-1)*TileSize/2, so that tile's far edge lands at n*TileSize/2 -- which is EXACTLY
            // HalfSize. The outer streets therefore sat precisely where the skirt begins to grade away, flush
            // with the boundary and with nothing to spare (strawberry: "make sure the flattened terrain area of
            // the towns is big enough to fully encapsulate the roads"). Half a tile plus the carriageway
            // overhang is what was missing.
            const float PadMargin = TileSize * 0.5f + HalfCarriageway;   // 12 + 8 = 20 m
            float inner = poi.HalfSize + PadMargin, outer = inner * 1.6f;
            int cx = Mathf.RoundToInt(poi.X / Unit), cy = Mathf.RoundToInt(poi.Z / Unit);
            int rad = Mathf.CeilToInt(outer / Unit) + 1;
            for (int x = Mathf.Max(0, cx - rad); x <= Mathf.Min(gw - 1, cx + rad); x++)
                for (int y = Mathf.Max(0, cy - rad); y <= Mathf.Min(gh - 1, cy + rad); y++)
                {
                    // Chebyshev distance = the square's own metric, so the pad and its skirt are both squares.
                    float d = Mathf.Max(Mathf.Abs(x * Unit - poi.X), Mathf.Abs(y * Unit - poi.Z));
                    if (d > outer) continue;
                    // 1 inside the footprint, easing to 0 at the skirt's edge. A hard cutoff at `inner` is what
                    // makes a flattened site look stamped on; the skirt is what makes it look graded.
                    float w = d <= inner ? 1f : 1f - Mathf.SmoothStep(inner, outer, d);
                    grid[x, y] = Mathf.Lerp(grid[x, y], target, w);
                }
        }

        /// <summary>Light box blur over the whole grid. Runs AFTER flattening, so it also softens the skirt seams
        /// the pads leave behind -- doing it before would smooth the terrain and then stamp hard edges back into
        /// it, which is the wrong order for the one job it has.</summary>
        static void Smooth(float[,] grid, int gw, int gh, float strength)
        {
            var src = (float[,])grid.Clone();
            for (int x = 0; x < gw; x++)
                for (int y = 0; y < gh; y++)
                {
                    float sum = 0f; int n = 0;
                    for (int ox = -1; ox <= 1; ox++)
                        for (int oy = -1; oy <= 1; oy++)
                        {
                            int sx = x + ox, sy = y + oy;
                            if (sx < 0 || sy < 0 || sx >= gw || sy >= gh) continue;
                            sum += src[sx, sy]; n++;
                        }
                    grid[x, y] = Mathf.Lerp(src[x, y], sum / n, strength);
                }
        }

        // ------------------------------------------------------- MONUMENT LINKS

        /// <summary>What runs between two monuments. Paved road for permanent places, dirt trail whenever a
        /// construction site is an end (a site is temporary -- nobody lays asphalt to one), rail only for long
        /// hauls between towns and bases, which is where rail earns its keep over a road.</summary>
        public enum LinkKind { Road, Trail, Rail }

        public readonly struct Link
        {
            public readonly int A, B; public readonly LinkKind Kind; public readonly float Length;
            public Link(int a, int b, LinkKind k, float len) { A = a; B = b; Kind = k; Length = len; }
        }

        /// <summary>A gate on a monument's perimeter: where a link meets it, and which way it faces. Position is
        /// ON the square's edge and Dir points OUT of it, so the path stage has a start point and a heading
        /// without having to re-derive either from the geometry.</summary>
        public readonly struct Connector
        {
            public readonly int Poi, Link; public readonly float X, Z, DirX, DirZ; public readonly LinkKind Kind;
            public Connector(int poi, int link, float x, float z, float dx, float dz, LinkKind k)
            { Poi = poi; Link = link; X = x; Z = z; DirX = dx; DirZ = dz; Kind = k; }
            public override string ToString() => $"{Kind} gate on #{Poi} at ({X:0},{Z:0}) facing ({DirX:0.##},{DirZ:0.##})";
        }

        static LinkKind KindFor(PoiKind a, PoiKind b, float length)
        {
            // A construction site is temporary, so whatever reaches it is a dirt trail regardless of what sits
            // at the other end. Checked FIRST: a town-to-site link is a trail, not a road, and ordering this
            // after the town rule would have quietly paved every one of them.
            if (a == PoiKind.ConstructionSite || b == PoiKind.ConstructionSite) return LinkKind.Trail;
            // Rail only between permanent places AND only when it is worth laying: a 300 m railway between two
            // neighbouring towns is not a railway, it is a siding. Threshold in metres so it does not change
            // meaning at another map size.
            if (length > 900f) return LinkKind.Rail;
            return LinkKind.Road;
        }

        /// <summary>Which monuments connect to which.
        ///
        /// TWO TIERS, because a purely distance-based spanning tree connects everything and still reads wrong.
        /// Measured on the first run: the military base's only tree link was a 184 m DIRT TRAIL to a building
        /// site, and its road to town existed only as the spare loop edge. Connected, but backwards -- so the
        /// spine is built over the PERMANENT places (towns and bases) alone, and construction sites are then
        /// hung off it as spurs. That is the order real infrastructure happens in: the road joins the
        /// settlements, and the temporary site gets a track to the nearest one.
        ///
        /// Deterministic: the POI list is already in a seed-stable order and this only sorts and compares.</summary>
        public static System.Collections.Generic.List<Link> BuildLinks(System.Collections.Generic.List<Poi> pois)
        {
            var links = new System.Collections.Generic.List<Link>();
            int n = pois.Count;
            if (n < 2) return links;

            static float Dist(Poi a, Poi b)
            {
                float dx = a.X - b.X, dz = a.Z - b.Z;
                return Mathf.Sqrt(dx * dx + dz * dz);
            }

            var permanent = new System.Collections.Generic.List<int>();
            var temporary = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++)
                (pois[i].Kind == PoiKind.ConstructionSite ? temporary : permanent).Add(i);

            // A map with no permanent places at all still has to join up, so the spine falls back to everything.
            var spine = permanent.Count >= 2 ? permanent : new System.Collections.Generic.List<int>(temporary);

            // --- tier 1: the spine, Prim's over the permanent places.
            if (spine.Count >= 2)
            {
                var inTree = new System.Collections.Generic.HashSet<int> { spine[0] };
                while (inTree.Count < spine.Count)
                {
                    float best = float.MaxValue; int bi = -1, bj = -1;
                    foreach (int i in spine)
                    {
                        if (!inTree.Contains(i)) continue;
                        foreach (int j in spine)
                        {
                            if (inTree.Contains(j)) continue;
                            float d = Dist(pois[i], pois[j]);
                            if (d < best) { best = d; bi = i; bj = j; }
                        }
                    }
                    if (bj < 0) break;
                    inTree.Add(bj);
                    links.Add(new Link(bi, bj, KindFor(pois[bi].Kind, pois[bj].Kind, best), best));
                }

                // One extra spine edge so the road network has a loop -- a map where every journey has exactly
                // one possible route reads as generated. Only worth it once there are 3+ places to loop between.
                if (spine.Count >= 3)
                {
                    float xb = float.MaxValue; int xi = -1, xj = -1;
                    foreach (int i in spine)
                        foreach (int j in spine)
                        {
                            if (j <= i) continue;
                            bool already = false;
                            foreach (var l in links) if ((l.A == i && l.B == j) || (l.A == j && l.B == i)) { already = true; break; }
                            if (already) continue;
                            float d = Dist(pois[i], pois[j]);
                            if (d < xb) { xb = d; xi = i; xj = j; }
                        }
                    if (xi >= 0) links.Add(new Link(xi, xj, KindFor(pois[xi].Kind, pois[xj].Kind, xb), xb));
                }
            }

            // --- tier 2: every construction site gets ONE trail, to its nearest spine member. A spur, not part
            // of the network -- nothing should route THROUGH a building site to get somewhere else.
            foreach (int t in temporary)
            {
                if (spine.Contains(t)) continue;   // the no-permanent-places fallback already joined it
                float best = float.MaxValue; int bj = -1;
                foreach (int j in spine)
                {
                    float d = Dist(pois[t], pois[j]);
                    if (d < best) { best = d; bj = j; }
                }
                if (bj >= 0) links.Add(new Link(t, bj, KindFor(pois[t].Kind, pois[bj].Kind, best), best));
            }
            return links;
        }

        /// <summary>Put a gate on each end of every link, on the perimeter, facing its partner.</summary>
        public static System.Collections.Generic.List<Connector> BuildConnectors(
            System.Collections.Generic.List<Poi> pois, System.Collections.Generic.List<Link> links)
        {
            var cons = new System.Collections.Generic.List<Connector>();
            for (int li = 0; li < links.Count; li++)
            {
                var l = links[li];
                cons.Add(Gate(pois, l.A, l.B, li, l.Kind));
                cons.Add(Gate(pois, l.B, l.A, li, l.Kind));
            }
            return cons;
        }

        /// <summary>Where a ray from `from`'s centre toward `to`'s centre leaves `from`'s square.</summary>
        static Connector Gate(System.Collections.Generic.List<Poi> pois, int from, int to, int link, LinkKind kind)
        {
            var a = pois[from]; var b = pois[to];
            float dx = b.X - a.X, dz = b.Z - a.Z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 1e-3f) return new Connector(from, link, a.X + a.HalfSize, a.Z, 1f, 0f, kind);
            dx /= len; dz /= len;
            // Slab clip against the AXIS-ALIGNED square, not a circle at HalfSize: the gate must sit on the edge
            // the road actually crosses. A radial offset would put it OUTSIDE the pad on the diagonals (a corner
            // is 1.41x further out than an edge) and leave a gap between the monument and its own road.
            float tx = Mathf.Abs(dx) > 1e-6f ? a.HalfSize / Mathf.Abs(dx) : float.MaxValue;
            float tz = Mathf.Abs(dz) > 1e-6f ? a.HalfSize / Mathf.Abs(dz) : float.MaxValue;
            float t = Mathf.Min(tx, tz);
            // DIR IS THE EDGE NORMAL, not the bearing to the partner. It used to be the bearing, which meant a
            // road left the gate at whatever angle its target happened to sit at -- so it met the monument's
            // wall obliquely, which is wrong for anything with a gate, a fence line or a street grid behind it
            // (strawberry: "make sure that roads leave the connection points completely perpendicular").
            // Whichever slab the ray exited is the side it is on, so the normal is that axis.
            float nx = tx <= tz ? Mathf.Sign(dx) : 0f;
            float nz = tx <= tz ? 0f : Mathf.Sign(dz);
            return new Connector(from, link, a.X + dx * t, a.Z + dz * t, nx, nz, kind);
        }

        // ------------------------------------------------------------- ROADS

        /// <summary>A routed path between two gates, in world metres, plus what runs along it.</summary>
        public readonly struct Route
        {
            public readonly LinkKind Kind;
            public readonly System.Collections.Generic.List<Vector2> Points;
            public Route(LinkKind k, System.Collections.Generic.List<Vector2> pts) { Kind = k; Points = pts; }
        }

        /// <summary>The half-width of the ribbon RoadField actually DRAWS: PEI's material is Width 8.0 and
        /// RoadField applies WidthScale 1.15, so the road on screen is 18.4 m across. ⚠ The carve was sized to
        /// the DESIGN widths below (4 m for a Road) and the mesh is more than twice that, so between 4 m and
        /// 9.2 m the ground was only partly levelled and the road was laid straight over it -- which is the
        /// terrain that pokes up through the surface (strawberry: "reduce the amount of clipping of terrain
        /// through the roads"). A carve narrower than its own road cannot help clipping however smooth it is.</summary>
        /// <summary>Diagnostics for the exit-growing pass: how many street cells it added, how many it could
        /// not add because the lattice ended, and how many exits still came up short of an exact cap fit.</summary>
        public static int GrowAdded, GrowBlocked, GrowShort, GrowTrimmed;
        public static float PadWas, PadNow, PadSmallest = float.MaxValue; public static int PadCount;

        public const float RenderedRoadHalf = 9.2f;
        const float CarveMargin = 2.5f;   // levelled a little past the edge, so the blend starts off the tarmac

        /// <summary>What a kind WOULD want if it had its own material. Kept per-kind rather than collapsed,
        /// because the day a Trail gets a narrower material this is the number that should shrink.</summary>
        static float DesignHalfFor(LinkKind k) => k switch
        {
            LinkKind.Road => 4.0f,    // 8 m carriageway
            LinkKind.Rail => 3.0f,    // single track + ballast shoulder
            _ => 2.5f,                // dirt trail
        };

        /// <summary>What to actually flatten: never narrower than the thing being drawn on it. Every kind
        /// currently renders with material 0, so today this is the rendered width for all three.</summary>
        static float HalfWidthFor(LinkKind k) => Mathf.Max(DesignHalfFor(k), RenderedRoadHalf) + CarveMargin;

        /// <summary>How much a route hates climbing. Rail hates it most -- real track tops out around 2-3 %, so
        /// a railway that shrugs at a hillside is the single most obviously-wrong thing this could produce.</summary>
        static float SlopeCostFor(LinkKind k) => k switch
        {
            LinkKind.Rail => 14f,
            LinkKind.Road => 6f,
            _ => 2.5f,                // a trail is allowed to be steep; that is what makes it a trail
        };

        /// <summary>Route every link over the terrain and carve it into the heightmap.
        ///
        /// ROUTES FOLLOW THE GROUND. A straight line between two gates satisfies every connectivity check I would
        /// write and drives through hillsides, so the path is an A* over a cost field where climbing is expensive
        /// and water is nearly impassable -- the route bends around a hill instead of tunnelling it, and the
        /// carve then only has to fix what is left. Cost is per-STEP height change, not absolute height: a road
        /// contouring along a hillside at constant altitude is cheap, which is exactly what a real one does.</summary>
        public static System.Collections.Generic.List<Route> CarveRoutes(
            float[,] grid, int gw, int gh,
            System.Collections.Generic.List<Poi> pois,
            System.Collections.Generic.List<Link> links,
            System.Collections.Generic.List<Connector> cons,
            Params p)
        {
            const float Unit = 4f;
            var routes = new System.Collections.Generic.List<Route>();
            // ⚠ ROADS MUST NOT RUN ALONGSIDE EACH OTHER (strawberry: "they cannot directly cross over eachother,
            // or overlap along their length (perpendicular intersecting is fine)", and again on the renders:
            // "you have road splines crossing in the 3rd scree shot").
            //
            // ⭐ THE CAUSE IS THAT EACH ROUTE IS SOLVED ALONE. Every route is an A* over the SAME cost field, so
            // two links whose ends are anywhere near each other find the same cheap valley and travel down it
            // together, a few metres apart -- not a bug in any one route, an inevitability of solving them
            // independently. Deleting the loser afterwards would disconnect a town; the fix is to make the
            // second route KNOW about the first.
            //
            // So route them in turn and stamp each finished route's corridor into a penalty field the next A*
            // adds to its step cost. The asymmetry does the work by itself: crossing a corridor square-on is a
            // handful of penalised cells and stays affordable, while running parallel pays the penalty for
            // every cell of the way. Perpendicular intersections survive exactly as master asked, and the
            // routes that used to pair up are pushed onto their own line instead.
            var used = new float[gw, gh];
            for (int li = 0; li < links.Count; li++)
            {
                Connector a = default, b = default;
                bool ga = false, gb = false;
                foreach (var c in cons)
                {
                    if (c.Link != li) continue;
                    if (!ga) { a = c; ga = true; }
                    else { b = c; gb = true; }
                }
                if (!ga || !gb) continue;
                var pts = Relax(Route2D(grid, gw, gh, a, b, links[li].Kind, p, used));
                if (pts.Count < 2) continue;
                routes.Add(new Route(links[li].Kind, pts));
                // ⚠ UG_NOROADPENALTY=1 SKIPS THE STAMP. The penalty and the probe that scores it were written
                // in the same change, so "0 m in open country" on its own proves nothing -- it is equally
                // consistent with the penalty working and with there never having been any to find. This is
                // the control: run the same seed with the stamp off and the number has to come back up, or the
                // measurement is describing the island rather than the fix.
                if (!NoRoadPenalty) StampUsed(used, gw, gh, pts);
            }
            foreach (var r in routes) Carve(grid, gw, gh, r, p, pois);
            // ⚠ AFTER every carve, not inside one. Routes cross and run alongside each other, and a smoothing
            // pass folded into Carve would be re-cut by the next route through the same cells -- the same
            // reason Smooth() runs after all the pads rather than per-pad. This is the "slight smoothing pass"
            // proper: the carve lands each grid sample on its own lerp toward the profile, which leaves a
            // low-amplitude ripple between adjacent samples that a wide flat ribbon sitting on top shows up
            // as speckled clipping.
            foreach (var r in routes) SmoothCorridor(grid, gw, gh, r, pois);
            // ⚠⚠ AND THEN RE-LEVEL WHAT THE ROAD ACTUALLY COVERS. SmoothCorridor blurs, and a blur on a
            // HILLSIDE pulls the uphill neighbours into the corridor -- so the very pass that removes the
            // lengthwise ripple raises the corridor's uphill edge into the ribbon. That is the bald patch
            // strawberry sees "when going up/down a slope", and turning the smoothing up (which is what was
            // asked for, and is right for the surroundings) makes that particular symptom WORSE on its own.
            // So: blur wide, then assign the ribbon's own footprint flat, the same way FlattenTownsExactly is
            // the last word on a town. Only terrain ABOVE a surface clips; after this there is none.
            LevelCorridors(grid, gw, gh, routes, pois);
            ReportRoutePairs(routes);
            return routes;
        }

        /// <summary>Assign the ground under every ribbon to that ribbon's own smoothed profile, exactly.
        ///
        /// ⚠ CROSSINGS ARE AVERAGED, NOT OVERWRITTEN. Two routes that genuinely meet must agree on the height
        /// of the cell they share, and letting whichever ran last win puts a step across the junction. The
        /// first writer assigns; a second averages into it, which is a junction that slopes gently from one
        /// road's grade to the other's rather than one that has a kerb across it.</summary>
        static void LevelCorridors(float[,] grid, int gw, int gh,
                                   System.Collections.Generic.List<Route> routes,
                                   System.Collections.Generic.List<Poi> pois)
        {
            const float Unit = 4f;
            // The DRAWN half-width plus a real margin, not the carve width: this is levelling what the mesh
            // covers. ⚠ THE MARGIN IS NOT COSMETIC -- the heightmap is a 4 m grid and SampleHeight INTERPOLATES,
            // so a point at the ribbon's edge reads partly from the first cell OUTSIDE the levelled disc. At
            // +1.5 m that cell was still hillside and the probe still found 1.59 m of terrain above the road.
            // Levelling a grid cell past the edge is what makes the edge itself flat.
            const float Cover = RenderedRoadHalf + 4f;
            int rad = Mathf.CeilToInt(Cover / Unit) + 1;
            var written = new bool[gw, gh];
            foreach (var r in routes)
            {
                int m = r.Points.Count;
                if (m < 2) continue;
                // Same profile the carve used: sample, then box-smooth, so this agrees with the grade the
                // corridor was cut to instead of re-deriving a different one from the blurred ground.
                var prof = new float[m];
                for (int i = 0; i < m; i++)
                {
                    int gx = Mathf.Clamp(Mathf.RoundToInt(r.Points[i].X / Unit), 0, gw - 1);
                    int gy = Mathf.Clamp(Mathf.RoundToInt(r.Points[i].Y / Unit), 0, gh - 1);
                    prof[i] = ToWorld(grid[gx, gy]);
                }
                int win = r.Kind == LinkKind.Rail ? 24 : r.Kind == LinkKind.Road ? 14 : 9;
                var sm = new float[m];
                for (int i = 0; i < m; i++)
                {
                    float sum = 0f; int cnt = 0;
                    for (int k = -win; k <= win; k++)
                    {
                        int j = i + k;
                        if (j < 0 || j >= m) continue;
                        sum += prof[j]; cnt++;
                    }
                    sm[i] = sum / cnt;
                }
                for (int i = 0; i < m; i++)
                {
                    int cx = Mathf.RoundToInt(r.Points[i].X / Unit), cy = Mathf.RoundToInt(r.Points[i].Y / Unit);
                    for (int x = Mathf.Max(0, cx - rad); x <= Mathf.Min(gw - 1, cx + rad); x++)
                        for (int y = Mathf.Max(0, cy - rad); y <= Mathf.Min(gh - 1, cy + rad); y++)
                        {
                            float dx = x * Unit - r.Points[i].X, dy = y * Unit - r.Points[i].Y;
                            if (dx * dx + dy * dy > Cover * Cover) continue;
                            if (InsideTown(x * Unit, y * Unit, pois)) continue;   // the town is still the last word on its own ground
                            float want = ToGrid(TownRamped(x * Unit, y * Unit, sm[i]));
                            grid[x, y] = written[x, y] ? (grid[x, y] + want) * 0.5f : want;
                            written[x, y] = true;
                        }
                }
            }
        }

        /// <summary>How much any two routes share ground, and at what angle they meet. ⚠ THE ANGLE IS THE
        /// WHOLE POINT -- master allows a perpendicular intersection and forbids a shallow one, so a count of
        /// "crossings" would condemn the junctions this generator is supposed to make. Shallow is the defect;
        /// square-on is a crossroads.</summary>
        public static float PairParallelMetres, PairWorstAngle; public static float PairSquareMetres, PairFanMetres;

        static void ReportRoutePairs(System.Collections.Generic.List<Route> routes)
        {
            PairParallelMetres = 0f; PairSquareMetres = 0f; PairFanMetres = 0f; PairWorstAngle = 90f;
            const float Near = 26f;          // inside this the two ribbons (9.2 m each) share shoulder
            const float ShallowDeg = 40f;
            for (int i = 0; i < routes.Count; i++)
                for (int j = i + 1; j < routes.Count; j++)
                {
                    var A = routes[i].Points; var B = routes[j].Points;
                    for (int a = 1; a < A.Count; a++)
                        for (int b = 1; b < B.Count; b++)
                        {
                            var p0 = A[a - 1]; var p1 = A[a];
                            var q0 = B[b - 1]; var q1 = B[b];
                            var da = p1 - p0; var db = q1 - q0;
                            if (da.Length() < 1e-4f || db.Length() < 1e-4f) continue;
                            float mid = ((p0 + p1) * 0.5f).DistanceTo((q0 + q1) * 0.5f);
                            if (mid > Near) continue;
                            // |dot| of the unit tangents: 1 -> the lines are PARALLEL (0 deg between them),
                            // 0 -> perpendicular (90 deg). Acos of it IS the angle between the two roads, so
                            // small means shallow, which is the case master forbids.
                            float ang = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(Mathf.Abs(da.Normalized().Dot(db.Normalized())), 0f, 1f)));
                            if (ang >= ShallowDeg) { PairSquareMetres += da.Length(); continue; }
                            // ⚠ SEPARATE THE GATE FAN. Two streets leaving neighbouring gates on the same face
                            // of a town are 24 m apart and parallel for the length of their stubs, by design --
                            // that is a town with two exits, not two roads sharing a valley. Counting them with
                            // the real offenders makes the number un-actionable: it can never reach zero, so it
                            // stops being able to say whether the thing master is looking at got fixed.
                            var mp = (p0 + p1) * 0.5f;
                            if (InsideAnyTownPad(mp.X, mp.Y, 60f)) { PairFanMetres += da.Length(); continue; }
                            PairParallelMetres += da.Length();
                            if (ang < PairWorstAngle) PairWorstAngle = ang;
                        }
                }
            Log.Print($"[island-routepairs] OPEN COUNTRY {PairParallelMetres:0} m of route within {Near:0} m of another at UNDER {ShallowDeg:0} deg (the forbidden kind)"
                      + (PairParallelMetres > 0f ? $", shallowest {PairWorstAngle:0.#} deg" : "")
                      + $"; {PairFanMetres:0} m is a town's own gate fan; {PairSquareMetres:0} m meets at a proper angle (junctions, allowed)");
        }

        /// <summary>How many points at each end of a route are the straight perpendicular STUB out of the gate.
        /// ⚠ PUBLIC because the joint picker has to land ON the far end of it: sampling a route at a stride that
        /// steps over the stub throws the perpendicular departure away, and the road then leaves the cap at
        /// whatever angle the first sampled point happens to sit at.</summary>
        public const int StubPoints = 6;

        /// <summary>Round off the corners. An 8-connected A* can only turn in 45-degree increments and
        /// staircases along any bearing that is not one of its eight -- so the raw path is a run of hard bends,
        /// the worst being the 90 the stub makes when it hands over to the search (strawberry: "avoid hard 90
        /// degree bends"). Windowed average over the interior with the weight tapered to ZERO at both ends, so
        /// the perpendicular departure survives: smoothing the whole polyline would round the stub off and
        /// quietly undo the previous commit.</summary>
        static System.Collections.Generic.List<Vector2> Relax(System.Collections.Generic.List<Vector2> pts)
        {
            const int Pin = StubPoints;   // held exactly at each end -- the stub is StubCells+1 points
            // ⚠ WIDER AND MORE PASSES (strawberry 2026-09-16: "do some more road bend smoothing. a lotta sharp
            // or unnatural corners around"). An 8-connected A* turns in 45-degree steps and staircases along
            // every other bearing, so what comes out of the search is a run of hard corners with the right
            // overall shape. Smoothing is free here -- Carve runs AFTER Relax, so the corridor is levelled
            // along whatever line this produces rather than along the staircase.
            const int Win = 7, Passes = 9;
            const int Blend = 26;   // free points spent easing off the stub's line: ~100 m, enough for a real S
            // ...and a floor under the RADIUS, because averaging alone converges slowly on the one corner that
            // matters. Measured before this: median corner radius 185 m and sharpest 15 m -- the median says
            // the roads are gentle and the sharpest says there is a hairpin in there somewhere, and it is the
            // hairpin that reads as "unnatural". The limiter below only touches points that are actually too
            // tight, so the rest of the route keeps the line the terrain cost chose for it.
            // ⚠ 90, NOT 55 (strawberry: "the road spline between two curves is perfectly straight and doesnt
            // counter-curve the curve of the piece going into it"). What they are describing is a route made of
            // straights joined by corners instead of one continuous line: the limiter only opened bends that
            // were already tight, so anything the smoothing had flattened STAYED flat and met the next bend at
            // a kink. Raising the floor makes the limiter act on gentle corners too, and because it pulls a
            // point toward the midpoint of its window it lengthens every turn into its neighbours -- which is
            // what puts an opposing curve on the straight between two bends rather than a hinge at each end.
            const float MinRadius = 90f;
            const int LimitPasses = 40;
            if (pts.Count < Pin * 2 + 3) return pts;
            var cur = new System.Collections.Generic.List<Vector2>(pts);
            for (int pass = 0; pass < Passes; pass++)
            {
                var next = new System.Collections.Generic.List<Vector2>(cur);
                for (int i = Pin; i < cur.Count - Pin; i++)
                {
                    Vector2 sum = Vector2.Zero; int n = 0;
                    for (int k = -Win; k <= Win; k++)
                    {
                        int j = i + k;
                        if (j < 0 || j >= cur.Count) continue;
                        sum += cur[j]; n++;
                    }
                    // Taper to zero over the first few free points, or the seam where the pinned stub meets the
                    // smoothed interior is itself a hard bend -- trading one corner for another.
                    // NO TAPER. It used to fade the pull to zero at the seam to "protect" the stub -- but the
                    // stub is PINNED, so the taper protected nothing and left the join unsmoothed, which is the
                    // exact corner that needed rounding. Full weight everywhere that is free to move.
                    next[i] = sum / n;
                }
                cur = next;
            }

            // EASE OFF THE STUB'S LINE. Averaging alone cannot fix the join: the stub is pinned and the smoothed
            // interior is nearly straight, so ALL of the turn between them lands on the first free point --
            // measured 60 degrees, and smoothing HARDER made it 86 because a straighter free side meets the pin
            // at a sharper angle. The corner is not too rough, it is in the wrong place.
            //
            // So blend the first free points between the stub's own CONTINUATION and the smoothed path, weight
            // 0 -> 1. At the seam the route still travels exactly along the stub's heading; by the end of the
            // blend it is fully on the smoothed line. The turn is then spread over Blend points by construction
            // rather than by hoping the averaging spreads it.
            // ⚠⚠ A LERP BETWEEN A RAY AND A PATH CANNOT COUNTER-CURVE, which is why two rounds of turning the
            // smoothing up did nothing for it (strawberry: "the road spline between two curves is perfectly
            // straight and doesnt counter-curve the curve of the piece going into it", then "counter curves
            // dont seem to be working either").
            //
            // The old blend walked a STRAIGHT ray out of the stub and lerped it toward the smoothed path. Every
            // point of that is a weighted average of two nearly-straight lines, so the result is nearly
            // straight too, and all the turning still piles up where the weight finishes. Smoothing harder only
            // straightens the thing being averaged toward.
            //
            // A cubic Hermite is the shape that actually has the property asked for: give it the stub's heading
            // at one end and the route's own heading at the other, and where those two disagree it produces an
            // S -- it curves one way out of the junction and back the other way onto the line, which is exactly
            // "counter-curve the piece going into it". Where they agree it degenerates to the straight line, so
            // a road that genuinely leaves straight still does.
            void Ease(int from, int step)
            {
                int a0 = from - step, a1 = from - 2 * step;                 // the last two PINNED points
                if (a1 < 0 || a1 >= cur.Count || a0 < 0 || a0 >= cur.Count) return;
                Vector2 p0 = cur[a0], t0 = cur[a0] - cur[a1];
                if (t0.Length() < 1e-4f) return;
                t0 = t0.Normalized();
                int last = from + (Blend - 1) * step;
                if (last < 0 || last >= cur.Count) return;
                // The far anchor's own heading, read across the points either side of it rather than from one
                // segment -- a single 4 m step of an 8-connected path is a 45-degree quantum, not a direction.
                int f0 = last - 2 * step, f1 = last + 2 * step;
                if (f0 < 0 || f0 >= cur.Count || f1 < 0 || f1 >= cur.Count) return;
                Vector2 p1 = cur[last], t1 = (cur[f1] - cur[f0]) * step;
                if (t1.Length() < 1e-4f) return;
                t1 = t1.Normalized();
                // Tangent magnitude = the straight-line distance between the anchors. Longer makes the S
                // deeper; this is the standard choice and keeps the curve inside the corridor the A* picked.
                float span = p0.DistanceTo(p1);
                if (span < 1e-3f) return;
                Vector2 m0 = t0 * span, m1 = t1 * span;
                for (int k = 1; k < Blend - 1; k++)
                {
                    int i = from + k * step;
                    if (i < 0 || i >= cur.Count) return;
                    float u = k / (float)(Blend - 1);
                    float h00 = 2f * u * u * u - 3f * u * u + 1f;
                    float h10 = u * u * u - 2f * u * u + u;
                    float h01 = -2f * u * u * u + 3f * u * u;
                    float h11 = u * u * u - u * u;
                    cur[i] = h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
                }
            }
            Ease(Pin, +1);                    // leaving the head stub
            Ease(cur.Count - 1 - Pin, -1);    // and the tail, walking backwards

            // CURVATURE LIMITER. Radius over a +/-2 window is chord / turn; where that is under MinRadius, pull
            // the point toward the midpoint of that window, which is the direction that opens the bend. Run to
            // convergence rather than a fixed strength: one hard pull would flatten a legitimate curve, and
            // many soft ones only keep working where the corner is still too tight.
            for (int pass = 0; pass < LimitPasses; pass++)
            {
                bool any = false;
                var next = new System.Collections.Generic.List<Vector2>(cur);
                // From Pin itself, not Pin+2: the window reads cur[i-2] which reaches INTO the pinned stub, and
                // that is exactly what should anchor it. Starting two points later left the stub/free seam --
                // the sharpest corner on the route by construction -- outside the limiter's reach.
                for (int i = Pin; i < cur.Count - Pin; i++)
                {
                    Vector2 a = cur[i - 2], b = cur[i + 2];
                    Vector2 d0 = cur[i] - a, d1 = b - cur[i];
                    if (d0.Length() < 1e-4f || d1.Length() < 1e-4f) continue;
                    float turn = Mathf.Acos(Mathf.Clamp(d0.Normalized().Dot(d1.Normalized()), -1f, 1f));
                    if (turn < 1e-4f) continue;
                    float radius = a.DistanceTo(b) / turn;
                    if (radius >= MinRadius) continue;
                    any = true;
                    next[i] = cur[i].Lerp((a + b) * 0.5f, 0.35f);
                }
                cur = next;
                if (!any) break;
            }
            return cur;
        }

        /// <summary>A* from one gate to the other over a slope-weighted grid.</summary>
        /// <summary>How dearly a route pays to share ground with one already laid. The inner value applies over
        /// the carriageway itself and the outer over a halo, so roads do not merely avoid overlapping, they
        /// avoid hugging. Tuned against the base step cost of 1: a perpendicular crossing pays for about five
        /// cells (+40 on a route that costs several hundred) while 100 m of parallel running pays for
        /// twenty-five (+200) and loses to almost any detour.</summary>
        static readonly bool NoRoadPenalty = System.Environment.GetEnvironmentVariable("UG_NOROADPENALTY") == "1";
        const float UsedInner = 8f, UsedOuter = 4f;
        // ⚠ THE HALO HAS TO COVER WHERE THE ROUTE ENDS UP, NOT WHERE A* PUT IT. The stamp is taken from the
        // relaxed points, but the NEXT route is relaxed and Hermite-eased AFTER its own A* has already dodged
        // this one -- so the search avoids the corridor and the smoothing then walks part of the path back
        // toward it. Measured across the two commits that strengthened the curves, shallow pairing in open
        // country crept 0 -> 8 -> 33 m with nothing else changed. Widening the outer ring past the distance the
        // ease can move a point is what makes the avoidance survive the smoothing.
        const float UsedInnerR = 14f, UsedOuterR = 46f;

        static void StampUsed(float[,] used, int gw, int gh, System.Collections.Generic.List<Vector2> pts)
        {
            const float Unit = 4f;
            int rad = Mathf.CeilToInt(UsedOuterR / Unit) + 1;
            foreach (var pt in pts)
            {
                int cx = Mathf.RoundToInt(pt.X / Unit), cy = Mathf.RoundToInt(pt.Y / Unit);
                for (int x = Mathf.Max(0, cx - rad); x <= Mathf.Min(gw - 1, cx + rad); x++)
                    for (int y = Mathf.Max(0, cy - rad); y <= Mathf.Min(gh - 1, cy + rad); y++)
                    {
                        float dx = x * Unit - pt.X, dy = y * Unit - pt.Y;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d > UsedOuterR) continue;
                        // ⚠ NOT INSIDE A TOWN. Every road on the island converges on the same few monuments, so
                        // penalising the ground around a town would price the LAST-routed link out of its own
                        // gate and send it round the houses. The pads are already off-limits to the carve for
                        // the same reason -- the town owns that ground, and its own street grid is what roads
                        // are supposed to share there.
                        if (InsideAnyTownPad(x * Unit, y * Unit, UsedOuterR)) continue;
                        float v = d <= UsedInnerR ? UsedInner : UsedOuter;
                        if (v > used[x, y]) used[x, y] = v;   // MAX, not sum: two crossings do not make a wall
                    }
            }
        }

        static System.Collections.Generic.List<Vector2> Route2D(
            float[,] grid, int gw, int gh, Connector from, Connector to, LinkKind kind, Params p, float[,] used = null)
        {
            const float Unit = 4f;
            // PERPENDICULAR DEPARTURE. A* is free to pick any of eight directions out of the first cell, so left
            // to itself a route leaves the gate diagonally whenever that is a metre cheaper. Both ends therefore
            // get a straight STUB along the edge normal, and A* only routes between the stub ends -- so the road
            // meets the monument square-on and the terrain-following starts once it is clear of the wall.
            const int StubCells = StubPoints - 1;   // 20 m: long enough to read as perpendicular, short enough not to fight the terrain
            // THE STUB IS BUILT IN GRID CELLS, not float world metres, and this is not tidiness. It used to walk
            // out from the gate's exact float position while A* snapped to cell centres -- so the stub's last
            // point and A*'s first differed by up to a metre BACKWARDS along the stub, and that one-metre
            // backtrack reads as a ~164-degree reversal in the turn measurement. Every worst-turn on every route
            // was at the seam index (pt 6, and count-6), which is what pointed at it. Snapping both to the same
            // lattice makes the seam a continuation instead of a corner.
            int fgx = Mathf.Clamp(Mathf.RoundToInt(from.X / Unit), 0, gw - 1);
            int fgy = Mathf.Clamp(Mathf.RoundToInt(from.Z / Unit), 0, gh - 1);
            int tgx = Mathf.Clamp(Mathf.RoundToInt(to.X / Unit), 0, gw - 1);
            int tgy = Mathf.Clamp(Mathf.RoundToInt(to.Z / Unit), 0, gh - 1);
            int fdx = Mathf.RoundToInt(from.DirX), fdy = Mathf.RoundToInt(from.DirZ);
            int tdx = Mathf.RoundToInt(to.DirX), tdy = Mathf.RoundToInt(to.DirZ);
            var head = new System.Collections.Generic.List<Vector2>();
            var tail = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i <= StubCells; i++)
            {
                head.Add(new Vector2(Mathf.Clamp(fgx + fdx * i, 0, gw - 1) * Unit, Mathf.Clamp(fgy + fdy * i, 0, gh - 1) * Unit));
                tail.Add(new Vector2(Mathf.Clamp(tgx + tdx * i, 0, gw - 1) * Unit, Mathf.Clamp(tgy + tdy * i, 0, gh - 1) * Unit));
            }
            int sx = Mathf.Clamp(Mathf.RoundToInt(head[head.Count - 1].X / Unit), 0, gw - 1);
            int sy = Mathf.Clamp(Mathf.RoundToInt(head[head.Count - 1].Y / Unit), 0, gh - 1);
            int tx = Mathf.Clamp(Mathf.RoundToInt(tail[tail.Count - 1].X / Unit), 0, gw - 1);
            int ty = Mathf.Clamp(Mathf.RoundToInt(tail[tail.Count - 1].Y / Unit), 0, gh - 1);

            float slopeCost = SlopeCostFor(kind);
            int n = gw * gh;
            var best = new float[n];
            var prev = new int[n];
            for (int i = 0; i < n; i++) { best[i] = float.MaxValue; prev[i] = -1; }

            int Idx(int x, int y) => y * gw + x;
            float H(int x, int y) => Mathf.Abs(x - tx) + Mathf.Abs(y - ty);   // Manhattan, admissible at step cost >= 1

            var open = new System.Collections.Generic.PriorityQueue<int, float>();
            best[Idx(sx, sy)] = 0f;
            open.Enqueue(Idx(sx, sy), H(sx, sy));
            int goal = Idx(tx, ty);
            var seen = new bool[n];

            while (open.Count > 0)
            {
                int cur = open.Dequeue();
                if (seen[cur]) continue;
                seen[cur] = true;
                if (cur == goal) break;
                int cx = cur % gw, cy = cur / gw;
                float ch = ToWorld(grid[cx, cy]);
                for (int ox = -1; ox <= 1; ox++)
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int nx = cx + ox, ny = cy + oy;
                        if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                        int ni = Idx(nx, ny);
                        if (seen[ni]) continue;
                        float nh = ToWorld(grid[nx, ny]);
                        // Water is not impassable-by-rule but is priced out of reach, so a route only crosses it
                        // if there is genuinely no land path -- which on one island there never is. A hard ban
                        // would make A* fail outright on a gate that sits a cell into the shallows.
                        float step = (ox != 0 && oy != 0) ? 1.4142f : 1f;
                        float climb = Mathf.Abs(nh - ch) / Unit;              // gradient of THIS step
                        float cost = step * (1f + slopeCost * climb);
                        if (nh <= p.SeaLevel) cost += 400f;
                        if (used != null) cost += used[nx, ny] * step;   // another road is already here
                        float cand = best[cur] + cost;
                        if (cand < best[ni]) { best[ni] = cand; prev[ni] = cur; open.Enqueue(ni, cand + H(nx, ny)); }
                    }
            }

            var mid = new System.Collections.Generic.List<Vector2>();
            if (prev[goal] < 0 && goal != Idx(sx, sy)) return mid;   // unreachable: caller drops the route
            for (int at = goal; at >= 0; at = prev[at])
            {
                mid.Add(new Vector2((at % gw) * Unit, (at / gw) * Unit));
                if (at == Idx(sx, sy)) break;
            }
            mid.Reverse();

            // mid[0] IS head's last cell and mid[^1] IS tail's last cell -- A* was seeded and targeted there.
            // Appending both ends whole would repeat those points, and a zero-length segment is skipped by the
            // turn measurement but still carves a doubled pass in the corridor.
            var pts = new System.Collections.Generic.List<Vector2>(head);
            for (int i = 1; i < mid.Count - 1; i++) pts.Add(mid[i]);
            for (int i = tail.Count - 1; i >= 0; i--) pts.Add(tail[i]);
            return pts;
        }

        /// <summary>Cut the route into the terrain: level a corridor to a SMOOTHED elevation profile along the
        /// path. The profile is smoothed first, so the road gets a steady gradient instead of inheriting every
        /// bump the ground had -- levelling each point to its own local height would carve a road that is
        /// perfectly flat crosswise and still a staircase lengthwise.</summary>
        /// <summary>True if this grid cell is inside a town's exactly-flattened footprint.
        /// ⚠ Carving must not touch it. The town is flattened FIRST and is the last word on that ground -- a
        /// route cutting a channel back through it is what made neighbouring road props disagree about their
        /// height. Outside the footprint the route owns the ground; inside, the town does. Ordering the two
        /// passes and giving each its own territory is what stops them undoing each other, which layering them
        /// in either order never did.</summary>
        /// <summary>⚠ Must agree with FlattenTownsExactly's pad, or the carve cuts into ground the town just
        /// levelled. Both read the same TownPads list, built once from the tiles, rather than each deriving a
        /// footprint from Poi.HalfSize and drifting apart the moment one of them changes.</summary>
        /// <summary>⚠ Y IS PART OF THE PAD, not a lookup the carve does for itself. The pad is levelled to an
        /// exact height and the route outside it is carved to its own profile; whoever blends the two has to
        /// know both numbers, and re-sampling the grid for the town's height would read whatever the carve had
        /// already written there.</summary>
        static System.Collections.Generic.List<(float X, float Z, float Half, float Y)> TownPads = new();

        static bool InsideTown(float wx, float wz, System.Collections.Generic.List<Poi> pois)
        {
            foreach (var pad in TownPads)
                if (Mathf.Max(Mathf.Abs(wx - pad.X), Mathf.Abs(wz - pad.Z)) <= pad.Half) return true;
            return false;
        }

        /// <summary>The same test, for the crossover that places props along the routes. ⚠ Reads the SAME pad
        /// list rather than re-deriving a footprint -- a second opinion about where a town ends would put
        /// roadside furniture inside one the moment either copy changed. `margin` pushes the boundary out, so a
        /// pole sitting a few metres off the pad edge still counts as in the town.</summary>
        public static bool InsideAnyTownPad(float wx, float wz, float margin = 0f)
        {
            foreach (var pad in TownPads)
                if (Mathf.Max(Mathf.Abs(wx - pad.X), Mathf.Abs(wz - pad.Z)) <= pad.Half + margin) return true;
            return false;
        }

        /// <summary>How far outside a town's pad the ground is graded from the pad's exact height onto the
        /// route's own profile (strawberry 2026-09-16: "theres still sharp dropoffs going in and out of
        /// towns").
        ///
        /// ⭐ THE STEP WAS IN THE GROUND, NOT IN THE ROAD. Carve and SmoothCorridor both skip any cell
        /// InsideTown -- the town owns its ground, which is what stopped the two passes undoing each other --
        /// so the first cell OUTSIDE the pad was carved to the route profile and the last cell inside it was
        /// pad-flat, with nothing in between. Wherever the town sat above or below the country around it that
        /// is a cliff exactly one grid cell wide, and the spline riding over it inherits the whole drop in one
        /// segment. Easing the SPLINE would have hidden it and left the ground edge there for the player to
        /// walk off.
        ///
        /// 40 m is five grid cells and a little over four spline joints at the 8 m stride, so the drop is spread
        /// across enough segments to read as a gradient rather than a step.</summary>
        const float TownRampBand = 40f;

        /// <summary>Blend a carved height toward the nearby town pad's level. Returns `want` untouched away
        /// from every pad, the pad's own height at its edge, and a smoothstep between.
        /// ⚠ NEAREST pad only, by edge distance. Summing or averaging the influence of two towns that happen to
        /// sit within a band of each other would grade the ground to a level neither of them is at.</summary>
        static float TownRamped(float wx, float wz, float want)
        {
            float bestGap = float.MaxValue, padY = 0f;
            foreach (var pad in TownPads)
            {
                float gap = Mathf.Max(Mathf.Abs(wx - pad.X), Mathf.Abs(wz - pad.Z)) - pad.Half;
                if (gap < bestGap) { bestGap = gap; padY = pad.Y; }
            }
            if (bestGap >= TownRampBand || bestGap == float.MaxValue) return want;
            if (bestGap <= 0f) return padY;
            return Mathf.Lerp(padY, want, Mathf.SmoothStep(0f, TownRampBand, bestGap));
        }

        static void Carve(float[,] grid, int gw, int gh, Route r, Params p, System.Collections.Generic.List<Poi> pois)
        {
            const float Unit = 4f;
            int m = r.Points.Count;
            var prof = new float[m];
            for (int i = 0; i < m; i++)
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt(r.Points[i].X / Unit), 0, gw - 1);
                int gy = Mathf.Clamp(Mathf.RoundToInt(r.Points[i].Y / Unit), 0, gh - 1);
                prof[i] = ToWorld(grid[gx, gy]);
            }
            // Box-smooth the profile. Rail gets a much wider window: real track cannot follow ground undulation,
            // it needs cut and fill, and a 3-point smooth on a railway still reads as a rollercoaster.
            // Widened after the perpendicular stubs went in. Forcing a route straight out of a gate means it
            // takes whatever slope sits outside that wall, and the worst trail grade jumped 17.5% -> 33% on the
            // stub alone. Smoothing further along the path grades that out -- it costs more cut-and-fill, which
            // is exactly what a real road does at a junction rather than rearing up at the gate.
            int win = r.Kind == LinkKind.Rail ? 24 : r.Kind == LinkKind.Road ? 14 : 9;
            var sm = new float[m];
            for (int i = 0; i < m; i++)
            {
                float sum = 0f; int cnt = 0;
                for (int k = -win; k <= win; k++)
                {
                    int j = i + k;
                    if (j < 0 || j >= m) continue;
                    sum += prof[j]; cnt++;
                }
                sm[i] = sum / cnt;
            }

            float half = HalfWidthFor(r.Kind), shoulder = half * 2.2f;
            int rad = Mathf.CeilToInt(shoulder / Unit) + 1;
            for (int i = 0; i < m; i++)
            {
                int cx = Mathf.RoundToInt(r.Points[i].X / Unit), cy = Mathf.RoundToInt(r.Points[i].Y / Unit);
                for (int x = Mathf.Max(0, cx - rad); x <= Mathf.Min(gw - 1, cx + rad); x++)
                    for (int y = Mathf.Max(0, cy - rad); y <= Mathf.Min(gh - 1, cy + rad); y++)
                    {
                        float dx = x * Unit - r.Points[i].X, dy = y * Unit - r.Points[i].Y;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d > shoulder) continue;
                        if (InsideTown(x * Unit, y * Unit, pois)) continue;   // the town owns its own ground
                        float w = d <= half ? 1f : 1f - Mathf.SmoothStep(half, shoulder, d);
                        // GRADE ONTO THE TOWN rather than butting against it. Outside the pad this is the
                        // route's own profile; within TownRampBand of one it slides toward the pad's exact
                        // level, reaching it at the boundary -- so the cell just outside the town and the cell
                        // just inside it are at the same height instead of a step apart.
                        float want = ToGrid(TownRamped(x * Unit, y * Unit, sm[i]));
                        // MAX, not assign: consecutive path points overlap, and a plain lerp lets a later point
                        // undo an earlier one's cut. Taking the strongest pull toward the profile keeps the
                        // corridor continuous instead of scalloped.
                        grid[x, y] = Mathf.Lerp(grid[x, y], want, w);
                    }
            }
        }

        /// <summary>Make each town's footprint EXACTLY flat, as the last word on that ground.
        ///
        /// strawberry: "the generator should plot the town and town size in the terrain phase, perfectly
        /// flattening it over the entire footprint. all road props should be at the same vertical position
        /// within each town."
        ///
        /// ⚠ "PERFECTLY" IS THE REQUIREMENT AND IT IS WHY THIS RUNS LAST. Flatten() already levels a pad, but
        /// Smooth() box-blurs the whole grid afterwards and CarveRoutes then cuts corridors through the
        /// monument near its gates -- so by the time anything is placed, the "flat" pad has a blurred rim and
        /// carved channels in it. A pad that is flattened and then edited is not flat, and the difference is
        /// exactly the millimetres a 24 m quad turns into a visible seam.
        ///
        /// ASSIGNED, NOT LERPED, inside the footprint: every cell takes the pad height outright. A weighted
        /// blend leaves a gradient that is small, real, and enough to make neighbouring road props disagree
        /// about their height -- which is the overlap between pieces. The graded skirt lives entirely OUTSIDE
        /// the footprint, where nothing is placed.
        ///
        /// ⭐ This is also what makes "all road props at the same vertical position" fall out for free rather
        /// than being enforced separately: on ground that is exactly level, sampling the terrain under each
        /// tile returns the same number for every tile in the town.</summary>
        public static void FlattenTownsExactly(float[,] grid, int gw, int gh, System.Collections.Generic.List<Poi> pois,
                                               System.Collections.Generic.List<MonumentTile> tiles = null)
        {
            if (pois == null) return;
            TownPads.Clear();   // rebuilt per generation; a stale pad would protect ground that no longer has a town on it
            PadWas = PadNow = 0f; PadSmallest = float.MaxValue; PadCount = 0;
            const float Unit = 4f;
            for (int pi = 0; pi < pois.Count; pi++)
            {
                var poi = pois[pi];
                float target = ToGrid(poi.GroundY);

                // ⚠ SIZED TO THE ROAD THAT IS ACTUALLY THERE, not to the nominal footprint (strawberry: "the
                // small 'towns' which are just a couple road line cap pieces can have their flattened area
                // reduced a lot. towns can have their flattened area reduced a bit").
                //
                // Poi.HalfSize is what the site was RESERVED at. What gets built on it is whatever survived
                // routing, the dead-end prune and the exit fitting -- and on a small monument that can be two
                // cap pieces sitting in the middle of a 48 m plateau. Measuring the tiles themselves shrinks a
                // stub hamlet a lot and a full town a little, from one rule, with no size classes to keep in
                // step with TilesFor().
                float cxW = poi.X, czW = poi.Z, half;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                if (tiles != null)
                    foreach (var t in tiles)
                    {
                        if (t.Poi != pi) continue;
                        if (t.X < minX) minX = t.X; if (t.X > maxX) maxX = t.X;
                        if (t.Z < minZ) minZ = t.Z; if (t.Z > maxZ) maxZ = t.Z;
                    }
                if (minX <= maxX)
                {
                    cxW = (minX + maxX) * 0.5f; czW = (minZ + maxZ) * 0.5f;
                    // Half the span of the tile CENTRES, plus half a tile to reach the prop's edge, plus the
                    // carriageway overhang. No HalfSize term at all -- that is the reservation, not the town.
                    half = Mathf.Max(maxX - minX, maxZ - minZ) * 0.5f + TileSize * 0.5f + HalfCarriageway;
                }
                else half = TileSize * 0.5f + HalfCarriageway;   // no tiles survived: flatten only what a single prop needs

                TownPads.Add((cxW, czW, half, poi.GroundY));
                PadWas += poi.HalfSize + TileSize * 0.5f + HalfCarriageway;   // what the old rule would have flattened
                PadNow += half; PadCount++;
                if (half < PadSmallest) PadSmallest = half;
                float inner = half;
                float outer = inner * 1.5f;
                int rad = Mathf.CeilToInt(outer / Unit) + 1;
                int cx = Mathf.RoundToInt(cxW / Unit), cy = Mathf.RoundToInt(czW / Unit);
                for (int x = Mathf.Max(0, cx - rad); x <= Mathf.Min(gw - 1, cx + rad); x++)
                    for (int y = Mathf.Max(0, cy - rad); y <= Mathf.Min(gh - 1, cy + rad); y++)
                    {
                        // Chebyshev, because the pad is a SQUARE and a radial test would leave its corners
                        // unflattened -- which is precisely where the corner tiles sit.
                        float d = Mathf.Max(Mathf.Abs(x * Unit - cxW), Mathf.Abs(y * Unit - czW));
                        if (d > outer) continue;
                        if (d <= inner) grid[x, y] = target;                       // EXACT
                        else grid[x, y] = Mathf.Lerp(grid[x, y], target, 1f - Mathf.SmoothStep(inner, outer, d));
                    }
            }
        }

        /// <summary>Box-blur the ground along one route's corridor, weighted so the centre is smoothed hardest
        /// and the effect fades out at the shoulder -- blurring to a hard edge would just move the discontinuity
        /// outwards. Two light passes rather than one heavy one: a single wide kernel flattens the corridor into
        /// a trough that reads as a trench from the side.</summary>
        static void SmoothCorridor(float[,] grid, int gw, int gh, Route r, System.Collections.Generic.List<Poi> pois)
        {
            // ⚠ MORE, AND WIDER, ON SLOPES (strawberry 2026-09-16: "a lot of bald patches still on road splines
            // when going up/down a slope. needs more smoothing of the terrain there").
            //
            // A bald patch is the ground coming through the ribbon, and on a slope it is a BREAK of slope, not
            // the slope itself: along a uniform grade the chord between two joints lies on the ground, and at a
            // crest it cuts under it. Two light passes were enough on flat country and are not enough over a
            // brow -- the blur has to reach far enough along the corridor to take the top off the crest rather
            // than just soften it. Five passes over a 2.6x shoulder converges on a corridor whose lengthwise
            // curvature the ribbon can actually follow.
            // ⚠ MORE AGAIN (strawberry: "the road splines need to smooth the terrain under them MORE"). Safe to
            // push because LevelCorridors runs after this and re-assigns the ribbon's own footprint flat -- the
            // blur's job here is purely the APPROACH to the road, where a wider, softer transition is what
            // stops the corridor reading as a trench cut into the hill.
            float half = HalfWidthFor(r.Kind), shoulder = half * 3.4f;
            const float Unit = 4f;
            int rad = Mathf.CeilToInt(shoulder / Unit) + 1;
            for (int pass = 0; pass < 9; pass++)
            {
                var src = (float[,])grid.Clone();
                foreach (var pt in r.Points)
                {
                    int cx = Mathf.RoundToInt(pt.X / Unit), cy = Mathf.RoundToInt(pt.Y / Unit);
                    for (int x = Mathf.Max(1, cx - rad); x <= Mathf.Min(gw - 2, cx + rad); x++)
                        for (int y = Mathf.Max(1, cy - rad); y <= Mathf.Min(gh - 2, cy + rad); y++)
                        {
                            float dx = x * Unit - pt.X, dy = y * Unit - pt.Y;
                            float d = Mathf.Sqrt(dx * dx + dy * dy);
                            if (d > shoulder) continue;
                            if (InsideTown(x * Unit, y * Unit, pois)) continue;   // ...and must not be blurred back out of level
                            float w = d <= half ? 1f : 1f - Mathf.SmoothStep(half, shoulder, d);
                            float avg = (src[x, y] + src[x - 1, y] + src[x + 1, y] + src[x, y - 1] + src[x, y + 1]) * 0.2f;
                            grid[x, y] = Mathf.Lerp(grid[x, y], avg, w * 0.6f);
                        }
                }
            }
        }

        // -------------------------------------------------------- MONUMENTS

        /// <summary>Which piece of the road kit a tile is. Cap variants exist only for the shapes that terminate
        /// a run; there is no Turn_Cap because a corner is never where a road leaves a monument.</summary>
        public enum RoadPiece { Line, Quad, Tee, Turn, LineCap, QuadCap, TeeCap }

        /// <summary>The real prop each piece instantiates. Names verified against
        /// content/objects/guid_mesh.txt -- the kit's Tee cap is `Road_Tee_Cap_1`, not `_0`, so a mapping
        /// written from the pattern rather than from the file silently drops every T-junction gate.</summary>
        public static string PropFor(RoadPiece p) => p switch
        {
            RoadPiece.Line    => "Road_Line_0",
            RoadPiece.Quad    => "Road_Quad_0",
            RoadPiece.Tee     => "Road_Tee_0",
            RoadPiece.Turn    => "Road_Turn_0",
            RoadPiece.LineCap => "Road_Line_Cap_0",
            RoadPiece.QuadCap => "Road_Quad_Cap_0",
            RoadPiece.TeeCap  => "Road_Tee_Cap_1",
            _ => null,
        };

        /// <summary>One placed road prop. YawDeg is a WORLD yaw about up such that the piece's MESH +Y axis ends
        /// up pointing along its facing. Mesh space is Z-up and yaw-only here (mesh x,y,z -> node x,z,-y), so
        /// mesh +Y is world -Z at yaw 0 -- hence atan2(-x, -z) rather than atan2(x, z).
        ///
        /// RECONCILE THIS BEFORE INSTANTIATING ANYTHING. WorldBuilder places retail props with
        /// `Basis(Y, 180 - ey)` -- a 180 correction its own comment describes as "only visible on asymmetric
        /// props like town buildings, hidden on the symmetric lighthouse". Nothing consumes YawDeg yet, so there
        /// is no bug in the world today, but a placer that passes this value straight through as `ey` will get
        /// every piece 180 out. Pass `180 - YawDeg`.
        ///
        /// And note why the test suite cannot settle this: the alignment checks recompute arm directions with
        /// the SAME formula used to place the pieces, so a wrong convention is wrong identically on both sides
        /// and every check still passes. A Line or a Quad is symmetric and would not show it either. The only
        /// things that could are an asymmetric piece rendered in the world, or this note.</summary>
        public readonly struct MonumentTile
        {
            public readonly int Poi; public readonly RoadPiece Piece;
            public readonly float X, Z, YawDeg;
            public MonumentTile(int poi, RoadPiece piece, float x, float z, float yaw)
            { Poi = poi; Piece = piece; X = x; Z = z; YawDeg = yaw; }
            public override string ToString() => $"{Piece} @ ({X:0},{Z:0}) yaw {YawDeg:0}";
        }

        static readonly (int dx, int dz)[] Card = { (0, -1), (1, 0), (0, 1), (-1, 0) };

        static float YawFor(int dx, int dz) => Mathf.RadToDeg(Mathf.Atan2(-dx, -dz));

        /// <summary>The same yaw for a direction that is not a lattice step: the rotation that points a prop's
        /// LOCAL +Y along (dx, dz) in this frame. Every piece of the road kit is authored that way and so are
        /// Fence_Road_0 and Power_Line_0, which is why this one function orients all of them.</summary>
        public static float YawForDir(float dx, float dz) => Mathf.RadToDeg(Mathf.Atan2(-dx, -dz));

        /// <summary>Lay a monument's streets on the 24 m lattice and return the placed props.
        ///
        /// The skeleton is the union of straight-then-turn runs from the centre tile out to each gate's tile, so
        /// every street exists BECAUSE something connects through it -- the same principle as the gates
        /// themselves. Piece choice is then read off the shape: a cell's four lattice neighbours decide whether
        /// it is a crossroads, a T, a straight, a corner or a dead end, and the dead ends are exactly the cells
        /// where a link leaves. Those get the Cap, ramp outward (strawberry: only Cap props should have
        /// connections, on the ramp side).</summary>
        /// <summary>Which lattice lines this monument's streets run along -- AVENUES (constant i) and CROSS
        /// STREETS (constant j), chosen separately and seeded per monument.
        ///
        /// ⭐ THIS IS WHY EVERY ISLAND LOOKED THE SAME (strawberry: "idk if the maps are actually random lol.
        /// they look VERY similar"). The terrain genuinely varies -- every noise call in Fill is seeded, and the
        /// coastline radius swings between 0.29 and 0.68 of the map -- but the TOWNS did not vary at all. The
        /// street lines were `for (k = 1; k <= n-2; k += 2)`, which is {1,3} on every 5-tile town and {1} on
        /// every 3-tile base, symmetric in both axes, on every seed. So the thing a player looks AT closest and
        /// longest was identical island to island, and a varied coastline underneath does not register against
        /// that. A generator can be correctly random and still look repetitive if the randomness is all in the
        /// parts nobody inspects.
        ///
        /// Choosing the two axes independently is what buys the shapes: {1,3}x{1,3} is the nine-block grid it
        /// always was, {2}x{1,3} is a single avenue crossed twice, {1}x{3} is an L of two streets meeting at a
        /// corner. All still obey the kit -- interior lines only, never adjacent, so whole 24 m tiles survive
        /// between them as blocks.
        /// ⚠ PURE, and read by BOTH SnapConnectorsToLattice and BuildMonument. A gate has to land ON a street
        /// line or its access road runs into the back of a block, so the two cannot each have an opinion.</summary>
        public static (int[] I, int[] J) StreetPlanFor(int poiIndex, int n, int seed)
        {
            if (n < 3) return (System.Array.Empty<int>(), System.Array.Empty<int>());
            if (n == 3) return (new[] { 1 }, new[] { 1 });   // one interior line; there is no other plan
            // ⚠ GENERATED, NOT TABULATED, because the lattice is no longer always 5 across. The interior lines
            // are 1..n-2 and a street may never be adjacent to another (the block between them would vanish), so
            // a plan is any arithmetic run with a stride of at least 2. Stride 2 gives single-tile blocks, the
            // dense old grid; stride 3 gives two-tile blocks, which is what a bigger city wants -- otherwise a
            // 9-tile city is just a 5-tile town with more streets rather than bigger blocks.
            int[] Lines(int salt)
            {
                int stride = Hash01(poiIndex * 61 + salt, 3, seed + 60031) < 0.45f ? 3 : 2;
                int start = 1 + (int)(Hash01(poiIndex * 89 + salt, 11, seed + 60037) * (stride - 0.01f));
                var outp = new System.Collections.Generic.List<int>();
                for (int k = start; k <= n - 2; k += stride) outp.Add(k);
                if (outp.Count == 0) outp.Add(1 + (n - 3) / 2);   // never empty: a town with no streets is not a town
                return outp.ToArray();
            }
            return (Lines(17), Lines(41));
        }

        public static System.Collections.Generic.List<MonumentTile> BuildMonument(
            int poiIndex, Poi poi, System.Collections.Generic.List<Connector> cons, int seed = 0)
        {
            int n = poi.Tiles;
            var tiles = new System.Collections.Generic.List<MonumentTile>();
            // lattice cell (i,j) centre, i/j in 0..n-1
            Vector2 CellPos(int i, int j) => new(
                poi.X + (i - (n - 1) * 0.5f) * TileSize,
                poi.Z + (j - (n - 1) * 0.5f) * TileSize);

            int mid = n / 2;
            // NOT seeded with the centre tile. Seeding it unconditionally put a cell in the skeleton that
            // nothing routes through on a one-gate monument -- and on a 2x2 site the centre is laterally
            // adjacent to the exit, which hands the cap a third direction and forces a Quad with a spare arm.
            // The streets are the routes BETWEEN gates; a monument with one gate has no route, just its access.
            var skel = new System.Collections.Generic.HashSet<(int, int)>();
            var exits = new System.Collections.Generic.Dictionary<(int, int), (int dx, int dz)>();   // cell -> outward dir

            // TWO PASSES. The exit cells must be known BEFORE any routing, because a route that happens to
            // pass through one gives it a lateral neighbour -- and {ramp, inward, lateral} is a direction set no
            // piece in the kit can express: the ramp has to be a Cap's stem, and a Tee cannot also serve the
            // direction opposite its stem. The fallback was Quad, whose fourth arm is laid as carriageway into
            // empty ground. So: find every exit first, then route around them.
            var exitCells = new System.Collections.Generic.Dictionary<(int, int), (int dx, int dz)>();
            var inners = new System.Collections.Generic.List<(int i, int j)>();
            foreach (var c in cons)
            {
                if (c.Poi != poiIndex) continue;
                int dx = Mathf.RoundToInt(c.DirX), dz = Mathf.RoundToInt(c.DirZ);
                int ei, ej;
                if (dx != 0)
                {
                    ei = dx > 0 ? n - 1 : 0;
                    ej = Mathf.Clamp(Mathf.RoundToInt((c.Z - poi.Z) / TileSize + (n - 1) * 0.5f), 0, n - 1);
                }
                else
                {
                    ej = dz > 0 ? n - 1 : 0;
                    ei = Mathf.Clamp(Mathf.RoundToInt((c.X - poi.X) / TileSize + (n - 1) * 0.5f), 0, n - 1);
                }
                exitCells[(ei, ej)] = (dx, dz);
                inners.Add((Mathf.Clamp(ei - dx, 0, n - 1), Mathf.Clamp(ej - dz, 0, n - 1)));
            }
            foreach (var kv in exitCells) exits[kv.Key] = kv.Value;

            // Route the centre to each exit's INNER cell, trying both L orders and taking whichever avoids the
            // exit cells entirely. On this lattice one of the two always does unless the inner cell is itself an
            // exit, which only happens if two gates sit back to back on a 2-wide monument.
            // Route between the INNER cells, hub-and-spoke off the first one, rather than out from the centre.
            // ⚠⚠ THE SPOKES ARE FOR MONUMENTS WITHOUT A GRID (strawberry 2026-09-17: "prevent weird layouts. try
            // to avoid adjascent quads or tees, excessive use of quads and tees and turns").
            //
            // On a grid-filled town every gate is snapped ONTO a street line and the grid already joins
            // everything to everything -- so hub-and-spoke L-paths between the inner cells add cells that are
            // not on any street, and every one of those is a corner or a junction that nothing asked for. That
            // is where the excess Turns and Tees came from, and why two of them could end up side by side. A
            // compound (base/site) has no grid, so there the spokes ARE the layout and stay.
            var hub = inners.Count > 0 ? inners[0] : (i: mid, j: mid);
            if (!FillsGrid(poi.Kind))
            foreach (var inner in inners)
            {
                var pathA = new System.Collections.Generic.List<(int, int)>();
                int ci = hub.i, cj = hub.j;
                while (ci != inner.i) { ci += System.Math.Sign(inner.i - ci); pathA.Add((ci, cj)); }
                while (cj != inner.j) { cj += System.Math.Sign(inner.j - cj); pathA.Add((ci, cj)); }

                var pathB = new System.Collections.Generic.List<(int, int)>();
                ci = hub.i; cj = hub.j;
                while (cj != inner.j) { cj += System.Math.Sign(inner.j - cj); pathB.Add((ci, cj)); }
                while (ci != inner.i) { ci += System.Math.Sign(inner.i - ci); pathB.Add((ci, cj)); }

                bool CleanOf(System.Collections.Generic.List<(int, int)> path)
                {
                    foreach (var cell in path)
                        if (exitCells.ContainsKey(cell) && cell != (inner.i, inner.j)) return false;
                    return true;
                }
                var chosen = CleanOf(pathA) ? pathA : CleanOf(pathB) ? pathB : pathA;
                foreach (var cell in chosen) skel.Add(cell);
                skel.Add((inner.i, inner.j));
                skel.Add((hub.i, hub.j));
            }
            // STREETS, NOT PAVEMENT. Filling every lattice cell was the wrong read of "fill the grid": the kit's
            // carriageway is 16 m inside a 24 m tile, so a fully-gridded town came out as one continuous apron
            // with 8 m gaps -- strawberry, on seeing it: "okay not FILL the grid".
            //
            // Streets run along alternating INTERIOR lattice lines instead, which leaves whole 24 m tiles
            // between them as blocks. n=5 gives lines {1,3}: two avenues each way, nine blocks. n=3 gives {1}:
            // a single crossroads with four corner blocks. Interior only, because a street on line 0 or n-1
            // would run along the footprint edge, and the gates have to be non-corner anyway.
            var plan = StreetPlanFor(poiIndex, n, seed);
            if (FillsGrid(poi.Kind))
                for (int i2 = 0; i2 < n; i2++)
                    for (int j2 = 0; j2 < n; j2++)
                        if (System.Array.IndexOf(plan.I, i2) >= 0 || System.Array.IndexOf(plan.J, j2) >= 0)
                            skel.Add((i2, j2));

            foreach (var kv in exitCells) skel.Add(kv.Key);

            // PRUNE DEAD-END STUBS. Every street is a path from the centre out to a gate, so on a monument with
            // ONE gate the centre tile is left hanging with a single neighbour -- and a dead end has to terminate
            // in a Cap, whose ramp then points out of the footprint at nothing. On a 2x2 site every cell is a
            // boundary cell, so there is nowhere for such a stub to face that is not outward. Trimming any
            // non-exit cell with fewer than two neighbours, repeatedly, leaves exactly the cells that lie on a
            // route between gates -- which is the same rule the streets were built on in the first place.
            bool trimmed = true;
            while (trimmed)
            {
                trimmed = false;
                foreach (var cell in new System.Collections.Generic.List<(int, int)>(skel))
                {
                    if (exits.ContainsKey(cell)) continue;
                    int deg = 0;
                    foreach (var d in Card) if (skel.Contains((cell.Item1 + d.dx, cell.Item2 + d.dz))) deg++;
                    if (deg <= 1) { skel.Remove(cell); trimmed = true; }
                }
            }

            // NO STREET ARM MAY OPEN ONTO AIR (strawberry: "roads cannot have exposed non-cap ends exposed to
            // 'air' (not connected to a road)").
            //
            // ⚠ A CAP IS ALLOWED EXACTLY ONE OPENING -- its ramp -- and the kit only fits an exit cell exactly
            // at three shapes: ramp + 1 street OPPOSITE (LineCap), ramp + 2 streets forming a BAR across it
            // (TeeCap), or ramp + 3 streets (QuadCap). Any other count falls through to QuadCap, whose spare
            // arm is then laid as carriageway into empty ground. strawberry, describing precisely that: "the
            // props themselves are quad caps, 2 sides go into other road props, one goes into a spline (cap
            // end) and the other is exposed to 'air'."
            //
            // The old comment here called a spare connector "invisible". It is not -- it is a road surface
            // ending in midair, and it was the shape of 24-29 arms per island.
            //
            // FIXED BY GROWING THE STREET, NOT BY PICKING A NARROWER PIECE. Choosing TeeCap for a shape it
            // cannot express would open a street onto solid kerb, which the note above rightly calls the worse
            // failure. Adding the missing neighbour turns the spare arm into a real road, and that new cell has
            // one neighbour so it terminates in a LineCap -- whose ramp facing outward is a cap end, which is
            // allowed.
            foreach (var kv in new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<(int, int), (int dx, int dz)>>(exits))
            {
                var cell = kv.Key; var ramp = kv.Value;
                var streets = new System.Collections.Generic.List<(int dx, int dz)>();
                foreach (var d in Card)
                    if (!(d.dx == ramp.dx && d.dz == ramp.dz) && skel.Contains((cell.Item1 + d.dx, cell.Item2 + d.dz)))
                        streets.Add(d);

                bool exact = streets.Count == 3
                          || (streets.Count == 1 && streets[0].dx == -ramp.dx && streets[0].dz == -ramp.dz)
                          || (streets.Count == 2
                              && streets[0].dx == -streets[1].dx && streets[0].dz == -streets[1].dz
                              && streets[0].dx * ramp.dx + streets[0].dz * ramp.dz == 0);
                if (exact) continue;

                // Grow toward 3 streets, which QuadCap fits exactly. Inward first: it is always on the lattice
                // (a gate is never a corner), where a lateral may not be.
                foreach (var d in Card)
                {
                    if (streets.Count >= 3) break;
                    if (d.dx == ramp.dx && d.dz == ramp.dz) continue;
                    var nc = (cell.Item1 + d.dx, cell.Item2 + d.dz);
                    if (nc.Item1 < 0 || nc.Item2 < 0 || nc.Item1 >= n || nc.Item2 >= n) { GrowBlocked++; continue; }
                    if (skel.Contains(nc)) continue;
                    skel.Add(nc);
                    streets.Add(d);
                    GrowAdded++;
                }
                if (streets.Count >= 3) continue;

                // ⚠ CANNOT GROW -> SHRINK. Measured: 22 exits per island come up short because the third
                // street cell would fall OUTSIDE the lattice, which happens on the small monuments (n=2, where
                // every cell is a corner). On those a QuadCap can never be satisfied, so leaving it is choosing
                // a piece with an arm that opens onto air by construction.
                // So trim laterals until the shape IS exact: ramp + the inward street alone is a LineCap, which
                // has no spare arm. A lateral removed here is not disconnected -- it keeps whatever other
                // neighbours it had, and if that leaves it a dead end it terminates in its own LineCap, whose
                // outward ramp is a legitimate cap end.
                var inward = (dx: -ramp.dx, dz: -ramp.dz);
                // ⚠ AN EXIT WITH NO STREET AT ALL still gets a LineCap, whose single street arm then points
                // inward at nothing -- a gate opening onto its own empty lattice cell. The inward neighbour is
                // always on the lattice for an edge cell, so there is no reason to leave it unconnected.
                if (streets.Count == 0)
                {
                    var ic = (cell.Item1 + inward.dx, cell.Item2 + inward.dz);
                    if (ic.Item1 >= 0 && ic.Item2 >= 0 && ic.Item1 < n && ic.Item2 < n)
                    { skel.Add(ic); streets.Add(inward); GrowAdded++; }
                }
                foreach (var d in new System.Collections.Generic.List<(int dx, int dz)>(streets))
                {
                    if (streets.Count <= 1) break;
                    if (d.dx == inward.dx && d.dz == inward.dz) continue;   // keep the street INTO the town
                    var lc = (cell.Item1 + d.dx, cell.Item2 + d.dz);
                    if (!skel.Contains(lc)) continue;
                    // ⚠⚠ NEVER TRIM ANOTHER GATE'S EXIT CELL. This loop deletes a lateral neighbour so THIS
                    // exit can be an exact LineCap -- and the neighbour is sometimes the cell another gate's
                    // road is supposed to arrive at. Deleting it removes that gate's cap entirely, and the
                    // spline then ends 24 m short of the nearest piece with nothing in between.
                    // strawberry: "some road splines arent connecting to the prop road caps". Measured on seed
                    // 12345, poi#10: gate (2056,776) had its exit cell (1,2) trimmed away by the +X gate at
                    // (2,2) tidying its own laterals. An exit is not a lateral; it is the reason the monument
                    // has a road at all.
                    if (exits.ContainsKey(lc)) continue;
                    skel.Remove(lc);
                    streets.Remove(d);
                    GrowTrimmed++;
                }
                if (streets.Count > 1 || (streets.Count == 1 && !(streets[0].dx == inward.dx && streets[0].dz == inward.dz)))
                    GrowShort++;
            }

            // ⚠ DROP ANYTHING LEFT COMPLETELY ISOLATED. The nb==0 branch below falls through to Quad -- four
            // carriageway arms on a cell with no neighbours at all, which is four arms into air and the worst
            // single offender left after the exit work. Degree ZERO only, deliberately: the earlier stub prune
            // removes degree <= 1, and re-running that here would delete the one-neighbour stubs the exit
            // growing just added, undoing the fix it is meant to finish.
            foreach (var cell in new System.Collections.Generic.List<(int, int)>(skel))
            {
                if (exits.ContainsKey(cell)) continue;
                int deg0 = 0;
                foreach (var d in Card) if (skel.Contains((cell.Item1 + d.dx, cell.Item2 + d.dz))) deg0++;
                if (deg0 == 0) { skel.Remove(cell); GrowTrimmed++; }
            }

            foreach (var cell in skel)
            {
                var nb = new System.Collections.Generic.List<(int dx, int dz)>();
                foreach (var d in Card)
                    if (skel.Contains((cell.Item1 + d.dx, cell.Item2 + d.dz))) nb.Add(d);

                var pos = CellPos(cell.Item1, cell.Item2);
                bool isExit = exits.TryGetValue(cell, out var outDir);

                // PIECE CHOICE IS THE ARRANGEMENT, NOT THE COUNT. Each prop has its connectors on FIXED local
                // axes, so a piece is only usable if its axes can be rotated onto the directions this cell
                // actually needs:
                //     Line  +Y,-Y      Turn  +X,-Y      Tee  +X,-X,+Y      Quad  all four
                // Choosing by neighbour count alone put a TeeCap on a cell whose two streets ran +X and +Z --
                // perpendicular, which a Tee's straight bar cannot express -- so one street opened onto solid
                // kerb. Quad is the fallback whenever the shape does not fit something narrower; a spare
                // connector is invisible, a missing one is a road into a wall.
                // The -Y arm is the +X arm turned by (x,z) -> (z,-x), NOT the other way. Getting the handedness
                // backwards here picks the wrong neighbour as the +X arm, which is a 90 deg error on its own.
                static (int dx, int dz) MinusYArmOf((int dx, int dz) plusX) => (plusX.dz, -plusX.dx);

                RoadPiece piece; float yaw;
                var need = new System.Collections.Generic.List<(int dx, int dz)>(nb);
                if (isExit) need.Add(outDir);

                if (isExit)
                {
                    // The ramp is the piece's +Y, always -- that is the whole point of a Cap.
                    yaw = YawFor(outDir.dx, outDir.dz);
                    var streets = nb;
                    bool oppositeOnly = streets.Count == 1 && streets[0].dx == -outDir.dx && streets[0].dz == -outDir.dz;
                    bool barAcross = streets.Count == 2
                                     && streets[0].dx == -streets[1].dx && streets[0].dz == -streets[1].dz
                                     && streets[0].dx * outDir.dx + streets[0].dz * outDir.dz == 0;
                    piece = streets.Count == 0 || oppositeOnly ? RoadPiece.LineCap
                          : barAcross ? RoadPiece.TeeCap
                          : RoadPiece.QuadCap;
                }
                else if (nb.Count >= 4) { piece = RoadPiece.Quad; yaw = 0f; }
                else if (nb.Count == 3)
                {
                    var missing = (dx: 0, dz: 0);
                    foreach (var d in Card) if (!nb.Contains(d)) { missing = d; break; }
                    piece = RoadPiece.Tee; yaw = YawFor(-missing.dx, -missing.dz);   // stem opposite the gap
                }
                else if (nb.Count == 2)
                {
                    if (nb[0].dx == -nb[1].dx && nb[0].dz == -nb[1].dz)
                    {
                        piece = RoadPiece.Line; yaw = YawFor(nb[0].dx, nb[0].dz);
                    }
                    else
                    {
                        // TURN's connectors are local +X and -Y (road_connectors.txt: "12 0" and "0 -12"), not
                        // +/-Y like the Line. Under the placement transform mesh +X lands on (-cos y, sin y)
                        // and mesh -Y on (sin y, cos y), so solving mesh+X -> a gives
                        //     cos y = -a.dx,  sin y = a.dz  ->  y = atan2(a.dz, -a.dx).
                        //
                        // Both halves of this were wrong and each cost 90 deg -- the pairing picked the -Y arm
                        // as `a`, and the formula was atan2(-a.dz, a.dx), a further 180 off. strawberry caught
                        // it by LOOKING at a top-down render: "the 'turn' piece needs to be turned 90 degrees
                        // yaw". No check in the suite could, because they all recompute arm directions with the
                        // same formula that places the piece -- see the note on MonumentTile.
                        var a0 = nb[0]; var b0 = nb[1];
                        if (MinusYArmOf(a0) != b0) { (a0, b0) = (b0, a0); }
                        piece = RoadPiece.Turn;
                        yaw = Mathf.RadToDeg(Mathf.Atan2(a0.dz, -a0.dx));
                    }
                }
                else if (nb.Count == 1) { piece = RoadPiece.LineCap; yaw = YawFor(-nb[0].dx, -nb[0].dz); }
                else { piece = RoadPiece.Quad; yaw = 0f; }

                tiles.Add(new MonumentTile(poiIndex, piece, pos.X, pos.Y, yaw));
            }
            return tiles;
        }

        // ------------------------------------------------------- BUILDINGS

        /// <summary>A placed building. Same yaw convention as the road props, and the same caveat about
        /// WorldBuilder's `180 - ey` -- see MonumentTile. Buildings are where it would actually SHOW: they are
        /// the asymmetric props that comment names, so a 180 error puts every front door facing the back garden
        /// while every number in the suite stays green.</summary>
        public readonly struct MonumentBuilding
        {
            public readonly int Poi; public readonly string Prop;
            public readonly float X, Z, YawDeg;
            public MonumentBuilding(int poi, string prop, float x, float z, float yaw)
            { Poi = poi; Prop = prop; X = x; Z = z; YawDeg = yaw; }
            public override string ToString() => $"{Prop} @ ({X:0},{Z:0}) yaw {YawDeg:0}";
        }

        /// <summary>A building prop and the footprint that decides where it can fit.
        ///
        /// Width is ACROSS the street, doubled about the origin because several of these are not centred on it
        /// (House_05 sits 4 m off its own origin). Front and Back are the distances from the origin to the near
        /// and far faces along the facing axis -- separately, because they are not half the depth: House_00's
        /// origin is 3 m behind its porch, House_09's is 3 m in front of its own.
        ///
        /// These were a COMMENT ("12-35 m wide, 15-25 m deep") sitting above a single 22 m constant, and the
        /// range in the comment is exactly the problem: a 39 m-wide clinic and an 18 m-deep-from-origin police
        /// station were being set back the same distance as a small house, so they stood in the carriageway.
        /// Measured off the OBJs in content/objects.</summary>
        public readonly struct BuildingProp
        {
            public readonly string Name; public readonly float Width, Front, Back;
            public BuildingProp(string name, float width, float front, float back)
            { Name = name; Width = width; Front = front; Back = back; }
        }

        static readonly BuildingProp[] Houses =
        {
            new("House_00", 16.5f, 8.3f, 14.2f),
            new("House_01", 13.0f, 12.5f, 12.5f),
            new("House_02", 21.0f, 8.5f, 8.5f),
            new("House_03", 37.0f, 6.5f, 8.5f),
            new("House_04", 21.0f, 8.5f, 8.5f),
            new("House_05", 35.0f, 10.5f, 8.5f),
            new("House_06", 20.5f, 10.5f, 10.5f),
            new("House_07", 17.0f, 10.5f, 10.5f),
            new("House_08", 25.0f, 8.5f, 8.5f),
            new("House_09", 17.0f, 15.0f, 8.5f),
        };
        static readonly BuildingProp[] Stores =
        {
            new("Diner_0", 25.0f, 9.5f, 11.1f),
            new("Diner_1", 14.0f, 9.0f, 9.1f),
            new("Diner_2", 21.7f, 9.1f, 9.1f),
            new("Gas_0", 12.2f, 10.1f, 10.1f),
            new("Bank_0", 22.2f, 11.0f, 11.1f),
            new("Office_0", 28.2f, 9.0f, 9.1f),
            new("Office_1", 25.7f, 12.5f, 12.6f),
            new("Office_2", 18.2f, 9.0f, 9.1f),
            new("Office_3", 21.7f, 8.0f, 8.1f),
        };
        /// <summary>⚠ SPLIT ALONG MASTER'S VOCABULARY, not the old three-way one. The brief is in terms of
        /// houses / businesses / offices / apartments ("smalls can only have houses, and rarely one business...
        /// cities can have apartments and offices, businesses"), and the old Stores table mixed shops with
        /// office blocks while Services mixed civic buildings with apartments -- so neither could be gated the
        /// way the brief describes. These are views over the SAME measured props, regrouped.</summary>
        static readonly BuildingProp[] Businesses =
        {
            new("Diner_0", 25.0f, 9.5f, 11.1f),
            new("Diner_1", 14.0f, 9.0f, 9.1f),
            new("Diner_2", 21.7f, 9.1f, 9.1f),
            new("Gas_0", 12.2f, 10.1f, 10.1f),
            new("Bank_0", 22.2f, 11.0f, 11.1f),
        };
        static readonly BuildingProp[] Offices =
        {
            new("Office_0", 28.2f, 9.0f, 9.1f),
            new("Office_1", 25.7f, 12.5f, 12.6f),
            new("Office_2", 18.2f, 9.0f, 9.1f),
            new("Office_3", 21.7f, 8.0f, 8.1f),
        };
        static readonly BuildingProp[] Apartments =
        {
            new("Apartment_0", 21.2f, 10.0f, 10.1f),
            new("Apartment_1", 20.2f, 11.0f, 11.1f),
            new("Apartment_2", 18.2f, 11.8f, 11.8f),
            new("Apartment_3", 16.7f, 8.0f, 8.1f),
        };
        static readonly BuildingProp[] Civic =
        {
            new("Police_0", 16.2f, 8.0f, 8.1f),
            new("Police_1", 24.7f, 18.1f, 18.1f),
            new("Medic_0", 20.2f, 10.0f, 10.1f),
            new("Medic_1", 39.0f, 10.0f, 10.1f),
            new("Medic_2", 20.7f, 11.0f, 11.1f),
            new("Fire_0", 16.2f, 12.3f, 12.2f),
        };

        static readonly BuildingProp[] Services =
        {
            new("Police_0", 16.2f, 8.0f, 8.1f),
            new("Police_1", 24.7f, 18.1f, 18.1f),
            new("Medic_0", 20.2f, 10.0f, 10.1f),
            new("Medic_1", 39.0f, 10.0f, 10.1f),
            new("Medic_2", 20.7f, 11.0f, 11.1f),
            new("Fire_0", 16.2f, 12.3f, 12.2f),
            new("Apartment_0", 21.2f, 10.0f, 10.1f),
            new("Apartment_1", 20.2f, 11.0f, 11.1f),
            new("Apartment_2", 18.2f, 11.8f, 11.8f),
            new("Apartment_3", 16.7f, 8.0f, 8.1f),
        };

        const float HalfCarriageway = 8f;   // the road surface is 16 m wide

        /// <summary>⚠ THE ROAD PROP IS 24 m WIDE, NOT 16 (strawberry 2026-09-16: "ensure proper spacing for
        /// buildings from the road props"). The carriageway -- the tarmac the material draws -- is 16 m, but the
        /// PIECE is a 24 m square: Road_Line_0's mesh measures exactly -12..12 on both horizontal axes, kerb and
        /// verge geometry included. Setbacks were measured from the carriageway, so a front wall landed at
        /// 8 + 4 = 12 m, which is EXACTLY the prop's own edge -- every building in every town was flush against
        /// the road piece, and the ones whose measured Front was a little optimistic were inside it. Measuring
        /// from the thing that is actually there is the fix; the old number was right about a road that is not
        /// the one being placed.</summary>
        const float PropHalf = TileSize * 0.5f;

        /// <summary>Clear ground between a road PROP's edge and any building wall. Applies at the back as well
        /// as the front, because a block with streets on both sides has a road piece at each end of it.</summary>
        const float BuildClear = 2.5f;

        /// <summary>How far past the street centreline the block cell itself reaches: the far edge of the
        /// 24 m cell behind the street's own tile. Beyond it is the next street's tile, at 36..60.</summary>
        const float BlockFar = TileSize * 1.5f;

        /// <summary>Metres from the street's CENTRELINE to this prop's ORIGIN, so that its front wall lands a
        /// clearance back from the ROAD PIECE whatever its own depth is. The old flat 22 m was this number
        /// computed once, for a 20 m-deep building, and then applied to all of them.</summary>
        static float SetbackFor(in BuildingProp b) => FrontWallFromCentreline + b.Front;

        /// <summary>Whether a prop fits a block with a street on ONE side: narrow enough not to spill into its
        /// neighbours, and short enough to stay inside its own block cell.
        /// ⚠ Derived from TileSize, not hardcoded, so a bigger lattice re-admits the props it rules out
        /// (House_03, Medic_1 and the rest) with no new table.</summary>
        static bool Fits(in BuildingProp b) =>
            b.Width <= TileSize - 2f && SetbackFor(b) + b.Back <= BlockFar;

        /// <summary>...and whether it fits a block with a street on BOTH sides, where the far end has to clear
        /// a second road piece. A 24 m block between two 24 m road tiles is genuinely tight -- most of the
        /// catalogue is 17-25 m deep -- so this set is small on purpose. An empty block is a yard; a building
        /// standing in the far carriageway is the bug being fixed.</summary>
        static bool FitsThrough(in BuildingProp b) =>
            Fits(b) && SetbackFor(b) + b.Back <= BlockFar - BuildClear;

        static BuildingProp[] Fitting(BuildingProp[] all, bool through)
        {
            var keep = new System.Collections.Generic.List<BuildingProp>();
            foreach (var b in all) if (through ? FitsThrough(b) : Fits(b)) keep.Add(b);
            return keep.ToArray();
        }
        static readonly BuildingProp[] FitHouses = Fitting(Houses, false), FitStores = Fitting(Stores, false), FitServices = Fitting(Services, false);
        static readonly BuildingProp[] ThruHouses = Fitting(Houses, true), ThruStores = Fitting(Stores, true), ThruServices = Fitting(Services, true);
        static readonly BuildingProp[] FitBiz = Fitting(Businesses, false), ThruBiz = Fitting(Businesses, true);
        static readonly BuildingProp[] FitOffice = Fitting(Offices, false), ThruOffice = Fitting(Offices, true);
        static readonly BuildingProp[] FitApt = Fitting(Apartments, false), ThruApt = Fitting(Apartments, true);
        static readonly BuildingProp[] FitCivic = Fitting(Civic, false), ThruCivic = Fitting(Civic, true);

        /// <summary>What may stand on a block, by what the town IS (strawberry 2026-09-17: "these gate the types
        /// of buildings that can spawn there. smalls can only have houses, and rarely one business. mediums can
        /// have both businesses and houses, businesses still less common. cities can have apartments and
        /// offices, businesses. houses are a lot less common").
        ///
        /// ⚠ `through` picks the shallow-fitting variant of whichever table wins -- a block with a street behind
        /// it as well as in front has a 24 m road piece at BOTH ends. Choosing the table first and the depth
        /// second keeps the size rule and the fit rule from having to know about each other.</summary>
        /// <summary>Is this prop one of the business buildings? ⚠ Derived from the Businesses table rather than
        /// listed again -- a second copy of "which props are shops" is how adding a new one silently escapes the
        /// one-per-town rule.</summary>
        /// <summary>Another prop from the SAME family whose material this one should wear, or null to keep its
        /// own. Families are the arrays themselves, so a prop added to Houses is in the house palette the moment
        /// it is added and nothing else has to be told.
        /// ⚠ Keyed on the building's own WORLD POSITION, not on an index: an index shifts every downstream
        /// building the moment one block changes its mind, so a one-tile edit would repaint the whole town.</summary>
        public static string MaterialVariantFor(string prop, float x, float z, int seed)
        {
            BuildingProp[] family = null;
            foreach (var set in new[] { Houses, Offices, Apartments })
                foreach (var b in set) if (b.Name == prop) { family = set; break; }
            if (family == null || family.Length < 2) return null;   // civic/business props have no family palette
            float r = Hash01(Mathf.RoundToInt(x), Mathf.RoundToInt(z), seed + 7331);
            return family[Mathf.Clamp((int)(r * family.Length), 0, family.Length - 1)].Name;
        }

        public static bool IsBusinessProp(string name) => IsBusiness(name);

        static bool IsBusiness(string name)
        {
            foreach (var b in Businesses) if (b.Name == name) return true;
            return false;
        }

        static BuildingProp[] TableFor(TownSize size, float r, bool through)
        {
            switch (size)
            {
                // ⚠ BUSINESS SHARE CUT ACROSS ALL THREE (strawberry: "lower the chance of businesses
                // everywhere"). City 24 -> 12, Medium 30 -> 14, Small 8 -> 4. The one-per-town rule alone would
                // not have done it: it caps how many DISTINCT shops a town has, and with the old weights a big
                // town simply hit the cap and then filled the rest of its business rolls with fallback houses --
                // the same buildings, arrived at by a longer route, with the cap doing the work the weights
                // should have been doing.
                case TownSize.City:
                    return r < 0.34f ? (through ? ThruApt : FitApt)
                         : r < 0.66f ? (through ? ThruOffice : FitOffice)
                         : r < 0.78f ? (through ? ThruBiz : FitBiz)
                         : r < 0.88f ? (through ? ThruCivic : FitCivic)
                         : (through ? ThruHouses : FitHouses);          // houses a lot less common
                case TownSize.Medium:
                    return r < 0.70f ? (through ? ThruHouses : FitHouses)
                         : r < 0.84f ? (through ? ThruBiz : FitBiz)     // businesses still less common
                         : (through ? ThruCivic : FitCivic);
                default:                                                 // Small (Monument builds nothing)
                    return r < 0.96f ? (through ? ThruHouses : FitHouses)
                         : (through ? ThruBiz : FitBiz);                 // rarely one business
            }
        }

        /// <summary>The measured footprint of a placed building, by name. Exposed so a check can ask where a
        /// prop's WALL ends up rather than where its origin does -- the origin was never the thing standing in
        /// the road.</summary>
        public static BuildingProp? PropInfo(string name)
        {
            foreach (var set in new[] { Houses, Stores, Services })
                foreach (var b in set) if (b.Name == name) return b;
            return null;
        }

        /// <summary>Where a building's front wall should land: clear of the ROAD PIECE, on every prop.</summary>
        public static float FrontWallFromCentreline => PropHalf + BuildClear;

        /// <summary>Fill a monument's blocks with buildings fronting the streets.
        ///
        /// Every block cell that touches a street gets one building, placed at a fixed setback from THAT
        /// street's centreline and turned to face it. A block cornered by two streets fronts the first one in
        /// cardinal order, deterministically -- a building cannot face two ways and picking by seed would make
        /// the same town render differently between runs.</summary>
        public static System.Collections.Generic.List<MonumentBuilding> PlaceBuildings(
            int poiIndex, Poi poi, System.Collections.Generic.List<MonumentTile> tiles, Params p)
        {
            var outp = new System.Collections.Generic.List<MonumentBuilding>();
            if (!FillsGrid(poi.Kind)) return outp;   // a construction site is a compound, not a street of shops
            int n = poi.Tiles;

            var street = new System.Collections.Generic.HashSet<(int, int)>();
            foreach (var t in tiles)
            {
                if (t.Poi != poiIndex) continue;
                int i = Mathf.RoundToInt((t.X - poi.X) / TileSize + (n - 1) * 0.5f);
                int j = Mathf.RoundToInt((t.Z - poi.Z) / TileSize + (n - 1) * 0.5f);
                street.Add((i, j));
            }

            // ⚠ THE CLASS COMES FROM THE TILES THAT EXIST, counted here rather than from poi.Tiles: the lattice
            // is what the site was given, the street set is what survived routing and pruning, and master's own
            // example is about the result ("the monuments with just 2 road line caps are just 'monument'").
            var size = SizeOf(street.Count);
            if (size == TownSize.Monument) return outp;   // two caps and a road is not a settlement
            // Business props already standing in THIS town. Per-monument, not per-island: two towns each having
            // a petrol station is a map; one town having two is a bug.
            var usedBiz = new System.Collections.Generic.HashSet<string>();

            int slot = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if (street.Contains((i, j))) continue;
                    foreach (var d in Card)
                    {
                        if (!street.Contains((i - d.dx, j - d.dz))) continue;   // the street this block fronts
                        // Position measured out from the STREET cell, not the block cell.
                        float scx = poi.X + ((i - d.dx) - (n - 1) * 0.5f) * TileSize;
                        float scz = poi.Z + ((j - d.dz) - (n - 1) * 0.5f) * TileSize;

                        // Front (-Y) toward the street means +Y points AWAY from it, i.e. along d.
                        float yaw = YawFor(d.dx, d.dz);

                        // ⚠ WHICH TABLE DEPENDS ON THE BLOCK, not just on the roll. A block cell with a street
                        // behind it as well as in front has a 24 m road piece at BOTH ends, so only the shallow
                        // props clear them both; a block on the outside of the grid has open ground behind and
                        // can take the deeper ones. Filtering once, globally, to whichever case is stricter
                        // would have thrown most of the catalogue away on every block -- including the two
                        // thirds of them that have the room.
                        bool through = street.Contains((i + d.dx, j + d.dz));
                        // Deterministic mix, gated by what the town IS. Keyed on the cell and the seed so a town
                        // is the same town every time it is generated.
                        float r = Hash01(i * 71 + poiIndex * 13, j * 37, p.Seed + 4021);
                        var table = TableFor(size, r, through);
                        // An empty block is a yard. Falling back to a table that does NOT fit would put the
                        // wall back in the road, which is the whole thing being fixed.
                        if (table.Length == 0) break;
                        var b = table[(int)(Hash01(i, j * 91 + slot, p.Seed + 907) * (table.Length - 1) + 0.5f)];

                        // ⚠ ONE OF EACH BUSINESS PER TOWN (strawberry 2026-09-17: "limit 1 of each business type
                        // per town"). A town with two banks and three petrol stations reads as a tiling error
                        // rather than a place -- the props are distinct buildings with signage, not
                        // interchangeable filler, which is exactly why the duplicates are obvious.
                        //
                        // ⚠ AND THE FALLBACK IS A HOUSE, NOT AN EMPTY BLOCK. Skipping the block would punch a
                        // hole in the street the moment a town got big enough to want a fifth business, so a
                        // taken business degrades to the thing there is always more of. Walk the table from the
                        // rolled index so the substitute is still seeded rather than always the first entry.
                        if (IsBusiness(b.Name))
                        {
                            if (!usedBiz.Add(b.Name))
                            {
                                bool found = false;
                                int start = (int)(Hash01(i, j * 91 + slot, p.Seed + 907) * (table.Length - 1) + 0.5f);
                                for (int k = 1; k < table.Length && !found; k++)
                                {
                                    var alt = table[(start + k) % table.Length];
                                    if (usedBiz.Add(alt.Name)) { b = alt; found = true; }
                                }
                                if (!found)
                                {
                                    var houses = through ? ThruHouses : FitHouses;
                                    if (houses.Length == 0) break;
                                    b = houses[(int)(Hash01(i * 7, j * 13 + slot, p.Seed + 911) * (houses.Length - 1) + 0.5f)];
                                }
                            }
                        }
                        // Setback is per PROP, measured out from the street cell it fronts.
                        float set = SetbackFor(b);
                        outp.Add(new MonumentBuilding(poiIndex, b.Name, scx + d.dx * set, scz + d.dz * set, yaw));
                        slot++;
                        break;   // one building per block cell, fronting the first street in cardinal order
                    }
                }
            return outp;
        }

        /// <summary>Snap every gate onto its face's lattice line, so a Cap's connector lands exactly on it.
        ///
        /// This is the "you may need to move the connection points to fit" half. The gates were placed by a ray
        /// clip against the footprint, which puts them anywhere along a face; a Cap's connector sits at the tile
        /// centre +/-12, i.e. only ever on a lattice line. Without this the road meets the monument up to 12 m
        /// off the end of the road piece it is supposed to join.</summary>
        public static System.Collections.Generic.List<Connector> SnapConnectorsToLattice(
            System.Collections.Generic.List<Poi> pois, System.Collections.Generic.List<Connector> cons, int seed = 0)
        {
            var outp = new System.Collections.Generic.List<Connector>(cons.Count);
            // Group by monument: the placements interact, so they cannot be decided one at a time.
            var byPoi = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
            for (int i = 0; i < cons.Count; i++)
            {
                if (!byPoi.TryGetValue(cons[i].Poi, out var l)) { l = new System.Collections.Generic.List<int>(); byPoi[cons[i].Poi] = l; }
                l.Add(i);
            }

            var snapped = new int[cons.Count];
            foreach (var kv in byPoi)
            {
                var poi = pois[kv.Key];
                int n = poi.Tiles;
                var idxs = kv.Value;

                // EXHAUSTIVE, not greedy. A gate's lattice line has to avoid every OTHER gate's exit cell and
                // the inner cell behind it, and be non-adjacent to both -- otherwise the exit needs {ramp,
                // inward, lateral}, which no piece expresses: a Cap's ramp must be its stem and a Tee cannot
                // serve the direction opposite its stem. Quad was the fallback and its fourth arm gets laid as
                // carriageway into empty ground.
                //
                // Greedy placement could not solve it: with the first gate pinned at its preferred line, the
                // second sometimes has no legal line at all, and there is nothing to do but emit the stub. The
                // search space is tiny -- at most 5 lines per gate and 3 gates -- so try every combination and
                // score by total displacement from where each gate wanted to be. Deterministic by construction.
                var want = new int[idxs.Count];
                for (int a = 0; a < idxs.Count; a++)
                {
                    var c = cons[idxs[a]];
                    float along = Mathf.Abs(c.DirX) > 0.5f ? c.Z - poi.Z : c.X - poi.X;
                    want[a] = Mathf.Clamp(Mathf.RoundToInt(along / TileSize + (n - 1) * 0.5f), 0, n - 1);
                }

                (int ei, int ej, int ii, int ij) CellsFor(int a, int k)
                {
                    var c = cons[idxs[a]];
                    int dx = Mathf.RoundToInt(c.DirX), dz = Mathf.RoundToInt(c.DirZ);
                    int ei = dx != 0 ? (dx > 0 ? n - 1 : 0) : k;
                    int ej = dx != 0 ? k : (dz > 0 ? n - 1 : 0);
                    return (ei, ej, Mathf.Clamp(ei - dx, 0, n - 1), Mathf.Clamp(ej - dz, 0, n - 1));
                }

                var cur = new int[idxs.Count];
                var best = (int[])want.Clone(); int bestCost = int.MaxValue;
                // ⚠ HOW HARD TO TRY. Level 0 is every rule; 1 drops the adjacency rule; 2 keeps only the one
                // rule that MUST hold. See the fallback note below -- this exists because the old code had no
                // level 2 and fell back to `want`.
                int level = 0;
                void Recurse(int a)
                {
                    if (a == idxs.Count)
                    {
                        // legal?
                        for (int x = 0; x < idxs.Count; x++)
                        {
                            var cx = CellsFor(x, cur[x]);
                            for (int y = 0; y < idxs.Count; y++)
                            {
                                if (x == y) continue;
                                var cy = CellsFor(y, cur[y]);
                                // ⭐ TWO GATES MAY NEVER SHARE AN EXIT CELL, at any level. BuildMonument keys
                                // its exits by cell in a Dictionary, so a collision does not fail -- the second
                                // gate silently OVERWRITES the first, and the monument ends up with one cap
                                // serving two roads. Measured on seed 12345: two route ends 24 m and 17 m from
                                // the nearest cap, one of them meeting a LineCap side-on because the surviving
                                // entry's ramp pointed the other gate's way.
                                if ((cx.ei, cx.ej) == (cy.ei, cy.ej)) return;
                                if (level >= 2) continue;
                                if ((cx.ei, cx.ej) == (cy.ii, cy.ij)) return;
                                // Adjacency between two gates' cells only matters when the monument is NOT
                                // grid-filled. In a full grid every cell is already a street, so a neighbouring
                                // exit is just another junction -- and QuadCap serves all four directions.
                                if (level >= 1) continue;
                                if (!FillsGrid(poi.Kind))
                                    foreach (var d in Card)
                                        if ((cx.ei + d.dx, cx.ej + d.dz) == (cy.ei, cy.ej) || (cx.ei + d.dx, cx.ej + d.dz) == (cy.ii, cy.ij)) return;
                            }
                        }
                        int cost = 0;
                        for (int x = 0; x < idxs.Count; x++) cost += System.Math.Abs(cur[x] - want[x]);
                        if (cost < bestCost) { bestCost = cost; best = (int[])cur.Clone(); }
                        return;
                    }
                    // On a grid-filled monument the corner lattice lines are unusable: a corner exit has two
                    // grid neighbours plus a ramp opposite one of them, and no piece serves that set.
                    // A gate must land ON a street line, or its inner cell is a block and the access road runs
                    // into the back of one. Same alternating set the streets use.
                    // ⚠ AND THE RESTRICTED SET CAN BE UNSATISFIABLE. On n = 3 the street lines are {1} -- a
                    // single line -- so two gates on the SAME face have exactly one cell to share between them
                    // and no assignment can separate them. The search then found nothing, fell back to `want`,
                    // and both gates keyed the same entry in BuildMonument's exit dictionary: one cap, two
                    // roads, the second one's ramp facing the first one's way. Measured on seed 12345, poi#9:
                    // a gate facing -X arriving at a LineCap whose ramp pointed +Z.
                    // Widening at level >= 1 costs a gate whose inner cell is a block rather than a street,
                    // which the growing pass below then fixes by adding one. That is a much smaller price than
                    // a road that never reaches the town.
                    // ⚠ THE LINES THIS MONUMENT ACTUALLY HAS, per face. A gate on an X face varies its `ej`
                    // and its exit cell sits on the grid's EDGE in i, so that cell is a street only if ej is one
                    // of the CROSS streets; a gate on a Z face is the mirror. Offering a fixed {1,3} to both,
                    // as this did, was correct exactly while every town had a {1,3}x{1,3} grid -- which is the
                    // sameness StreetPlanFor exists to end.
                    if (FillsGrid(poi.Kind) && n >= 3 && level == 0)
                    {
                        var pl = StreetPlanFor(kv.Key, n, seed);
                        var lines = Mathf.Abs(cons[idxs[a]].DirX) > 0.5f ? pl.J : pl.I;
                        foreach (int k in lines) { cur[a] = k; Recurse(a + 1); }
                    }
                    else
                    {
                        for (int k = 0; k < n; k++) { cur[a] = k; Recurse(a + 1); }
                    }
                }
                // ⚠ THE FALLBACK WAS THE BUG. This used to be a single Recurse(0), and when no assignment
                // satisfied every rule `best` stayed as `want` -- the unconstrained preferred lines, which is
                // precisely the arrangement the rules exist to forbid, INCLUDING two gates on one cell. A
                // search that gives up by returning the thing it was searching away from is worse than not
                // searching: it looks like it ran.
                //
                // So relax in order of how much each rule costs when broken. Adjacency costs a QuadCap with a
                // spare arm; sharing an inner cell costs a street that serves two gates; sharing the EXIT cell
                // costs a road that does not reach the town at all. Give up the cheap ones first and only take
                // `want` if even the last level is unsatisfiable (more gates than the face has lines).
                for (level = 0; level <= 2 && bestCost == int.MaxValue; level++) Recurse(0);
                for (int a = 0; a < idxs.Count; a++) snapped[idxs[a]] = best[a];
            }

            for (int i = 0; i < cons.Count; i++)
            {
                var c = cons[i];
                var poi = pois[c.Poi];
                int n = poi.Tiles;
                float rel = (snapped[i] - (n - 1) * 0.5f) * TileSize;
                float x = c.X, z = c.Z;
                if (Mathf.Abs(c.DirX) > 0.5f) z = poi.Z + rel; else x = poi.X + rel;
                outp.Add(new Connector(c.Poi, c.Link, x, z, c.DirX, c.DirZ, c.Kind));
            }
            return outp;
        }
    }
}
