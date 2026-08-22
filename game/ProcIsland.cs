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
            public readonly PoiKind Kind; public readonly float X, Z, HalfSize, GroundY;
            public Poi(PoiKind k, float x, float z, float half, float y) { Kind = k; X = x; Z = z; HalfSize = half; GroundY = y; }
            public override string ToString() => $"{Kind} @ ({X:0},{Z:0}) {HalfSize * 2f:0}m sq y{GroundY:0.#}";
        }

        // The road kit is a 24 m lattice (every piece has its connectors at +/-12, carriageway at z 0.4), so a
        // monument's half-extent is 12 * tiles EXACTLY. Snapped from 55/40/25 for that reason: at 55 the outer
        // tile edge lands 5 m inside the footprint and every gate sits on nothing. 5, 3 and 2 tiles across.
        public const float TileSize = 24f;
        public static int TilesFor(PoiKind k) => k switch { PoiKind.Town => 5, PoiKind.MilitaryBase => 3, _ => 2 };

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
                float half = HalfSizeFor(kind);
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
                if (got) placed.Add(new Poi(kind, bx, bz, half, by));
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
            float inner = poi.HalfSize, outer = poi.HalfSize * 1.6f;
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

        static float HalfWidthFor(LinkKind k) => k switch
        {
            LinkKind.Road => 4.0f,    // 8 m carriageway
            LinkKind.Rail => 3.0f,    // single track + ballast shoulder
            _ => 2.5f,                // dirt trail
        };

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
                var pts = Relax(Route2D(grid, gw, gh, a, b, links[li].Kind, p));
                if (pts.Count >= 2) routes.Add(new Route(links[li].Kind, pts));
            }
            foreach (var r in routes) Carve(grid, gw, gh, r, p);
            return routes;
        }

        /// <summary>Round off the corners. An 8-connected A* can only turn in 45-degree increments and
        /// staircases along any bearing that is not one of its eight -- so the raw path is a run of hard bends,
        /// the worst being the 90 the stub makes when it hands over to the search (strawberry: "avoid hard 90
        /// degree bends"). Windowed average over the interior with the weight tapered to ZERO at both ends, so
        /// the perpendicular departure survives: smoothing the whole polyline would round the stub off and
        /// quietly undo the previous commit.</summary>
        static System.Collections.Generic.List<Vector2> Relax(System.Collections.Generic.List<Vector2> pts)
        {
            const int Pin = 6;        // held exactly at each end -- the stub is StubCells+1 = 6 points
            const int Win = 5, Passes = 4;
            const int Blend = 14;   // free points spent easing off the stub's line
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
            void Ease(int from, int step)
            {
                int a0 = from - step, a1 = from - 2 * step;                 // the last two PINNED points
                if (a1 < 0 || a1 >= cur.Count || a0 < 0 || a0 >= cur.Count) return;
                Vector2 anchor = cur[a0], dir = (cur[a0] - cur[a1]);
                if (dir.Length() < 1e-4f) return;
                float spacing = dir.Length();
                dir = dir.Normalized();
                for (int k = 0; k < Blend; k++)
                {
                    int i = from + k * step;
                    if (i < 0 || i >= cur.Count) return;
                    Vector2 onRay = anchor + dir * (spacing * (k + 1));
                    cur[i] = onRay.Lerp(cur[i], (k + 1) / (float)(Blend + 1));
                }
            }
            Ease(Pin, +1);                    // leaving the head stub
            Ease(cur.Count - 1 - Pin, -1);    // and the tail, walking backwards
            return cur;
        }

        /// <summary>A* from one gate to the other over a slope-weighted grid.</summary>
        static System.Collections.Generic.List<Vector2> Route2D(
            float[,] grid, int gw, int gh, Connector from, Connector to, LinkKind kind, Params p)
        {
            const float Unit = 4f;
            // PERPENDICULAR DEPARTURE. A* is free to pick any of eight directions out of the first cell, so left
            // to itself a route leaves the gate diagonally whenever that is a metre cheaper. Both ends therefore
            // get a straight STUB along the edge normal, and A* only routes between the stub ends -- so the road
            // meets the monument square-on and the terrain-following starts once it is clear of the wall.
            const int StubCells = 5;   // 20 m: long enough to read as perpendicular, short enough not to fight the terrain
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
        static void Carve(float[,] grid, int gw, int gh, Route r, Params p)
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
                        float w = d <= half ? 1f : 1f - Mathf.SmoothStep(half, shoulder, d);
                        float want = ToGrid(sm[i]);
                        // MAX, not assign: consecutive path points overlap, and a plain lerp lets a later point
                        // undo an earlier one's cut. Taking the strongest pull toward the profile keeps the
                        // corridor continuous instead of scalloped.
                        grid[x, y] = Mathf.Lerp(grid[x, y], want, w);
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

        /// <summary>Lay a monument's streets on the 24 m lattice and return the placed props.
        ///
        /// The skeleton is the union of straight-then-turn runs from the centre tile out to each gate's tile, so
        /// every street exists BECAUSE something connects through it -- the same principle as the gates
        /// themselves. Piece choice is then read off the shape: a cell's four lattice neighbours decide whether
        /// it is a crossroads, a T, a straight, a corner or a dead end, and the dead ends are exactly the cells
        /// where a link leaves. Those get the Cap, ramp outward (strawberry: only Cap props should have
        /// connections, on the ramp side).</summary>
        public static System.Collections.Generic.List<MonumentTile> BuildMonument(
            int poiIndex, Poi poi, System.Collections.Generic.List<Connector> cons)
        {
            int n = TilesFor(poi.Kind);
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
            var hub = inners.Count > 0 ? inners[0] : (i: mid, j: mid);
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
            var streetLines = new System.Collections.Generic.List<int>();
            for (int k2 = 1; k2 <= n - 2; k2 += 2) streetLines.Add(k2);
            if (FillsGrid(poi.Kind))
                for (int i2 = 0; i2 < n; i2++)
                    for (int j2 = 0; j2 < n; j2++)
                        if (streetLines.Contains(i2) || streetLines.Contains(j2))
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
        const float Verge = 4f;             // grass between the kerb and the front wall
        /// <summary>How far past the street centreline a block reaches. The block cell spans 12..36 m out; the
        /// facing street's opposite kerb is at 40, since streets sit on every SECOND lattice line.</summary>
        const float BlockReach = 38f;

        /// <summary>Metres from the street's CENTRELINE to this prop's ORIGIN, so that its front wall lands a
        /// verge back from the kerb whatever its own depth is. The old flat 22 m was this number computed once,
        /// for a 20 m-deep building, and then applied to all of them.</summary>
        static float SetbackFor(in BuildingProp b) => HalfCarriageway + Verge + b.Front;

        /// <summary>Whether a prop fits the block it would be put on: narrow enough not to spill into its
        /// neighbours or the cross street, and short enough front-to-back to stay off the far street. Derived
        /// from TileSize rather than hardcoded, so a bigger lattice re-admits the props it currently rules out
        /// (House_03, Medic_1 and the rest) with no new table.</summary>
        static bool Fits(in BuildingProp b) => b.Width <= TileSize - 1f && SetbackFor(b) + b.Back <= BlockReach;

        static BuildingProp[] Fitting(BuildingProp[] all)
        {
            var keep = new System.Collections.Generic.List<BuildingProp>();
            foreach (var b in all) if (Fits(b)) keep.Add(b);
            return keep.ToArray();
        }
        static readonly BuildingProp[] FitHouses = Fitting(Houses), FitStores = Fitting(Stores), FitServices = Fitting(Services);

        /// <summary>The measured footprint of a placed building, by name. Exposed so a check can ask where a
        /// prop's WALL ends up rather than where its origin does -- the origin was never the thing standing in
        /// the road.</summary>
        public static BuildingProp? PropInfo(string name)
        {
            foreach (var set in new[] { Houses, Stores, Services })
                foreach (var b in set) if (b.Name == name) return b;
            return null;
        }

        /// <summary>Where a building's front wall should land: past the kerb by a verge, on every prop.</summary>
        public static float FrontWallFromCentreline => HalfCarriageway + Verge;

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
            int n = TilesFor(poi.Kind);

            var street = new System.Collections.Generic.HashSet<(int, int)>();
            foreach (var t in tiles)
            {
                if (t.Poi != poiIndex) continue;
                int i = Mathf.RoundToInt((t.X - poi.X) / TileSize + (n - 1) * 0.5f);
                int j = Mathf.RoundToInt((t.Z - poi.Z) / TileSize + (n - 1) * 0.5f);
                street.Add((i, j));
            }

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

                        // Deterministic mix: mostly houses, with stores and services salted through. Keyed on
                        // the cell and the seed so a town is the same town every time it is generated.
                        float r = Hash01(i * 71 + poiIndex * 13, j * 37, p.Seed + 4021);
                        var table = r < 0.60f ? FitHouses : r < 0.82f ? FitStores : FitServices;
                        if (table.Length == 0) break;
                        var b = table[(int)(Hash01(i, j * 91 + slot, p.Seed + 907) * (table.Length - 1) + 0.5f)];
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
            System.Collections.Generic.List<Poi> pois, System.Collections.Generic.List<Connector> cons)
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
                int n = TilesFor(poi.Kind);
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
                                if ((cx.ei, cx.ej) == (cy.ei, cy.ej)) return;
                                if ((cx.ei, cx.ej) == (cy.ii, cy.ij)) return;
                                // Adjacency between two gates' cells only matters when the monument is NOT
                                // grid-filled. In a full grid every cell is already a street, so a neighbouring
                                // exit is just another junction -- and QuadCap serves all four directions.
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
                    if (FillsGrid(poi.Kind) && n >= 3)
                    {
                        for (int k = 1; k <= n - 2; k += 2) { cur[a] = k; Recurse(a + 1); }
                    }
                    else
                    {
                        for (int k = 0; k < n; k++) { cur[a] = k; Recurse(a + 1); }
                    }
                }
                Recurse(0);
                for (int a = 0; a < idxs.Count; a++) snapped[idxs[a]] = best[a];
            }

            for (int i = 0; i < cons.Count; i++)
            {
                var c = cons[i];
                var poi = pois[c.Poi];
                int n = TilesFor(poi.Kind);
                float rel = (snapped[i] - (n - 1) * 0.5f) * TileSize;
                float x = c.X, z = c.Z;
                if (Mathf.Abs(c.DirX) > 0.5f) z = poi.Z + rel; else x = poi.X + rel;
                outp.Add(new Connector(c.Poi, c.Link, x, z, c.DirX, c.DirZ, c.Kind));
            }
            return outp;
        }
    }
}
