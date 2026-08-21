using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // PROCEDURAL ISLAND HEIGHTMAP (strawberry 2026-08-21: "island. yes deterministic.")
    //
    // Two properties matter and they fail in different ways, so they are checked separately: that the SAME SEED
    // gives the same map (the seed is the shareable artifact -- if it drifts, nothing else here is worth having),
    // and that the result is actually an ISLAND rather than a plane, a bowl, or a map that happens to be all
    // water. The second needs real measurement: "it generated something" is satisfied by every one of those.
    public sealed class ProcIslandTests : GameTest
    {
        public override string Name => "world.proc_island";
        public override double TimeoutSimSeconds => 60;

        static float[,] Gen(int tiles, int seed) => Gen(tiles, seed, out _);

        static float[,] Gen(int tiles, int seed, out System.Collections.Generic.List<ProcIsland.Poi> pois)
        {
            int gw = tiles * 256 + 1, gh = tiles * 256 + 1;
            var g = new float[gw, gh];
            var pars = ProcIsland.Params.Default(seed);
            ProcIsland.Fill(g, gw, gh, pars);
            pois = ProcIsland.PlacePois(g, gw, gh, pars);   // flattens pads + smooths, so the grid IS the final one
            return g;
        }

        // Land fraction, and how much of the BORDER is dry -- the two numbers that separate an island from the
        // shapes that would also pass a naive "heights vary" check.
        static (float land, float borderLand, float minY, float maxY) Survey(float[,] g)
        {
            int gw = g.GetLength(0), gh = g.GetLength(1);
            float sea = 25.6f;
            int land = 0, border = 0, borderLand = 0;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int x = 0; x < gw; x++)
                for (int y = 0; y < gh; y++)
                {
                    float w = ProcIsland.ToWorld(g[x, y]);
                    lo = Mathf.Min(lo, w); hi = Mathf.Max(hi, w);
                    if (w > sea) land++;
                    if (x == 0 || y == 0 || x == gw - 1 || y == gh - 1)
                    {
                        border++;
                        if (w > sea) borderLand++;
                    }
                }
            return (land / (float)(gw * gh), borderLand / (float)border, lo, hi);
        }

        public override IEnumerable<Step> Run()
        {
            // ---- DETERMINISM. Bit-identical, not approximately equal: the seed is what gets shared, so "close
            // enough" is a different map. Compared exactly, on the raw stored floats.
            var a = Gen(1, 1234);
            var b = Gen(1, 1234);
            int diffs = 0;
            for (int x = 0; x < a.GetLength(0); x++)
                for (int y = 0; y < a.GetLength(1); y++)
                    if (a[x, y] != b[x, y]) diffs++;
            T.Check($"the same seed is bit-identical ({diffs} differing cells of {a.Length})", diffs == 0);

            // ...and the CONTROL, without which the check above is also passed by a generator that ignores its
            // seed entirely and returns the same map every time.
            var c = Gen(1, 9876);
            int seedDiffs = 0;
            for (int x = 0; x < a.GetLength(0); x++)
                for (int y = 0; y < a.GetLength(1); y++)
                    if (a[x, y] != c[x, y]) seedDiffs++;
            T.Check($"control: a DIFFERENT seed is a different map ({seedDiffs} cells differ)",
                seedDiffs > a.Length / 10);

            // ---- IS IT AN ISLAND. Land in the middle, water at every edge.
            var (land, borderLand, lo, hi) = Survey(a);
            T.Check($"it is land, not an ocean ({land * 100f:0.#}% above sea)", land > 0.12f);
            T.Check($"...and ocean, not a continent ({land * 100f:0.#}% above sea)", land < 0.75f);
            T.Check($"...and the coast CLOSES -- the border is all water ({borderLand * 100f:0.##}% of the rim is dry)",
                borderLand < 0.005f);
            T.Check($"...with a seabed below the water and hills above it ({lo:0.#} m to {hi:0.#} m, sea 25.6)",
                lo < 20f && hi > 45f);

            // ---- SIZE IS A PARAMETER, which is the half of the brief that is about later ("configurable to a
            // larger map later on"). A generator that only works at the size it was tuned at has not got it.
            var big = Gen(2, 1234);
            T.Check($"a larger map generates at its own size ({big.GetLength(0)}x{big.GetLength(1)} vs {a.GetLength(0)}x{a.GetLength(1)})",
                big.GetLength(0) == 513 && a.GetLength(0) == 257);
            var (bland, bborder, _, bhi) = Survey(big);
            T.Check($"...and is still an island at that size ({bland * 100f:0.#}% land, {bborder * 100f:0.##}% rim dry, peak {bhi:0.#} m)",
                bland > 0.12f && bland < 0.75f && bborder < 0.005f && bhi > 45f);

            // ---- POIs (strawberry: "placing a few town/military base/construction site markers ... these pois
            // each have a size, they will flatten a terrain area around them, then a slight terrain smoothing").
            Gen(1, 1234, out var pois);
            // EVERY REQUESTED KIND, not just a count. `pois.Count >= 3` passed on a run that placed both
            // construction sites and the base and ZERO of the two towns -- the count was right and the mix was
            // wrong, which is invisible to any check that only counts. Towns are the demanding case (largest
            // footprint, so the hardest to fit inland), which is exactly why counting hides their absence.
            int nTown = 0, nBase = 0, nSite = 0;
            foreach (var q in pois)
            {
                if (q.Kind == ProcIsland.PoiKind.Town) nTown++;
                else if (q.Kind == ProcIsland.PoiKind.MilitaryBase) nBase++;
                else nSite++;
            }
            T.Check($"POIs got placed ({pois.Count}: {string.Join(", ", pois)})", pois.Count >= 3);
            T.Check($"...every requested KIND is present ({nTown} town, {nBase} base, {nSite} site) [{ProcIsland.LastRejectReport}]",
                nTown >= 1 && nBase >= 1 && nSite >= 1);

            // Every one has to be somewhere you could actually build: dry, and clear of the coast by more than
            // the pad it flattens, or the flatten eats the shoreline and leaves a rectangular beach.
            bool allDry = true, allApart = true;
            foreach (var poi in pois)
            {
                if (poi.GroundY <= 25.6f + 3f) allDry = false;
                foreach (var o in pois)
                {
                    if (o.X == poi.X && o.Z == poi.Z) continue;
                    // Chebyshev, matching the square footprints: two axis-aligned squares overlap only when
                    // they overlap on BOTH axes, and a Euclidean test passes diagonal pairs whose corners are
                    // inside each other.
                    float gap = Mathf.Max(Mathf.Abs(o.X - poi.X), Mathf.Abs(o.Z - poi.Z));
                    if (gap < o.HalfSize + poi.HalfSize) allApart = false;
                }
            }
            T.Check("...all on dry land, above the waterline", allDry);
            T.Check("...and none overlapping another", allApart);

            // THE FLATTEN ACTUALLY FLATTENED. Measured as the height spread inside each footprint -- a POI whose
            // pad still has 30 m of relief in it has a marker and nothing else, and every check above would pass.
            float worstSpread = 0f;
            foreach (var poi in pois)
            {
                float plo = float.MaxValue, phi = float.MinValue;
                int cx = Mathf.RoundToInt(poi.X / 4f), cy = Mathf.RoundToInt(poi.Z / 4f);
                int rad = Mathf.FloorToInt(poi.HalfSize / 4f);
                for (int x = Mathf.Max(0, cx - rad); x <= Mathf.Min(a.GetLength(0) - 1, cx + rad); x++)
                    for (int y = Mathf.Max(0, cy - rad); y <= Mathf.Min(a.GetLength(1) - 1, cy + rad); y++)
                    {
                        if (Mathf.Max(Mathf.Abs(x * 4f - poi.X), Mathf.Abs(y * 4f - poi.Z)) > poi.HalfSize) continue;
                        float w = ProcIsland.ToWorld(a[x, y]);
                        plo = Mathf.Min(plo, w); phi = Mathf.Max(phi, w);
                    }
                worstSpread = Mathf.Max(worstSpread, phi - plo);
            }
            T.Check($"...and the ground under them is actually level (worst spread {worstSpread:0.##} m across a footprint)",
                worstSpread < 3.5f);

            // UG_ISLAND_PNG=<path>: dump a preview so the shape can be LOOKED at. Every check above is a
            // statistic, and a plausible land fraction with a closed coast still describes shapes nobody wants
            // -- a ring, a starfish, four blobs. Gated, so a normal run pays nothing.
            var png = System.Environment.GetEnvironmentVariable("UG_ISLAND_PNG");
            if (!string.IsNullOrEmpty(png))
            {
                int gw = a.GetLength(0), gh = a.GetLength(1);
                var img = Image.CreateEmpty(gw, gh, false, Image.Format.Rgb8);
                for (int x = 0; x < gw; x++)
                    for (int y = 0; y < gh; y++)
                    {
                        float w = ProcIsland.ToWorld(a[x, y]);
                        // Water in blue by DEPTH, land in green-to-white by height, with the shoreline a hard
                        // colour break -- a smooth grey ramp hides exactly the thing being inspected.
                        Color col = w <= 25.6f
                            ? new Color(0.05f, 0.18f + 0.30f * Mathf.Clamp((w + 18f) / 43.6f, 0f, 1f), 0.45f)
                            : Color.FromHsv(0.28f - 0.28f * Mathf.Clamp((w - 25.6f) / 70f, 0f, 1f),
                                            0.55f, 0.35f + 0.60f * Mathf.Clamp((w - 25.6f) / 70f, 0f, 1f));
                        img.SetPixel(x, y, col);
                    }
                // POI markers, drawn last so they sit over the terrain: white ring at the footprint, and a
                // fainter ring at the skirt where the flatten blends out -- the two radii are the thing worth
                // eyeballing, because a pad that reads as "stamped on" is a skirt problem, not a size problem.
                foreach (var poi in pois)
                {
                    float rIn = poi.HalfSize / 4f, rOut = poi.HalfSize * 1.6f / 4f;
                    var tint = poi.Kind == ProcIsland.PoiKind.Town ? new Color(1f, 1f, 1f)
                             : poi.Kind == ProcIsland.PoiKind.MilitaryBase ? new Color(1f, 0.35f, 0.35f)
                             : new Color(1f, 0.85f, 0.2f);
                    int pcx = Mathf.RoundToInt(poi.X / 4f), pcy = Mathf.RoundToInt(poi.Z / 4f);
                    void Box(float halfCells, Color c)
                    {
                        int h = Mathf.RoundToInt(halfCells);
                        for (int d = -h; d <= h; d++)
                        {
                            foreach (var (px, py) in new[] { (pcx + d, pcy - h), (pcx + d, pcy + h), (pcx - h, pcy + d), (pcx + h, pcy + d) })
                                if (px >= 0 && py >= 0 && px < gw && py < gh) img.SetPixel(px, py, c);
                        }
                    }
                    Box(rIn, tint);
                    Box(rOut, tint * 0.45f);
                }
                img.SavePng(png);
                GD.Print($"[island] preview -> {png}  ({gw}x{gh})");
            }

            yield break;
        }
    }
}
