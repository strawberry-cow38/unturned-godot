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

        // Half-extents, so these are 110 m / 80 m / 50 m squares. Cut from 135/95/65 radii (270/190/130 m
        // across) on the first look at a render: at that size a town pad covered a quarter of a small island and
        // the map read as a set of discs rather than as terrain with places on it.
        static float HalfSizeFor(PoiKind k) => k switch
        {
            PoiKind.Town => 55f,                 // the biggest footprint: a street grid needs room
            PoiKind.MilitaryBase => 40f,
            _ => 25f,                            // construction site: a couple of shells and a crane
        };

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
                // Bounded attempts, and the bound is per-POI: a map with nowhere left to put a town should place
                // fewer POIs, not spin. Reporting how many actually landed is the caller's job.
                for (int tries = 0; tries < 600 && !got; tries++, attempt++)
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

                    placed.Add(new Poi(kind, wx, wz, half, HeightAt(grid, gw, gh, cxg, cyg)));
                    got = true;
                }
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
            return new Connector(from, link, a.X + dx * t, a.Z + dz * t, dx, dz, kind);
        }
    }
}
