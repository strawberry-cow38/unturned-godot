using System.Collections.Generic;
using Godot;
using UnturnedSim;

namespace UnturnedGodot
{
    // ==============================================================================================
    // BUILDINGS ON THE PLOTS (strawberry 2026-10-15: "are we ready to just shove the building gen into town gen?" -- "yeh",
    // one block first). The town's half: what each plot asks the generator for, where the building stands and which way it
    // faces, and the level ground under it. Turning a BuildingGenResult into nodes is the generator's own runtime builder
    // (tinyclaw's, lifted out of the editor's stage); until it lands a building is shown as its MASSING -- each floor's box
    // and its front door -- which is all it takes to check the half that is this file's.
    // ==============================================================================================
    public sealed partial class TownRoadGrid
    {
        /// <summary>UG_TOWNBUILDINGS: "block" the block nearest the town's middle, "all" every plot, anything else none.</summary>
        public static string BuildingsMode = System.Environment.GetEnvironmentVariable("UG_TOWNBUILDINGS") ?? "";

        public sealed class TownBuilding
        {
            public Plot Plot; public BuildingGenSpec Spec; public BuildingGenResult Result;
            /// <summary>The generator's lattice in the world: origin its (0, 0) corner at floor level, +X along the street,
            /// +Z away from it -- so the plan's south side, z = 0, is the front, on the street.</summary>
            public Transform3D Frame;
            public float PadY; public double Ms; public string Failed;
            /// <summary>Its front door came out on the plan's north side, and the frame is turned half round to put it on the street.</summary>
            public bool TurnedRound;
            /// <summary>The sides of its plan another building's footprint meets: party walls, butted by the runtime builder.</summary>
            public Sides Party;
        }
        /// <summary>What the last PlanBuildings generated.</summary>
        public readonly List<TownBuilding> Buildings = new();

        /// <summary>The yard a porch or canopy needs in front of the plan to stand clear of the plot's line.</summary>
        public const float PorchRoom = 3.5f;

        /// <summary>The extent of what the building STANDS on the ground with -- its walls, a porch's posts and floor, its steps --
        /// as (x0, z0, x1, z1) in the lattice's metres. Not its roofs: an eave over a pavement is an eave, not a building on it.</summary>
        static (float x0, float z0, float x1, float z1) PlanExtent(BuildingGenResult r)
        {
            float x0 = 0f, z0 = 0f, x1 = r.CellsX * BuildingGen.Cell, z1 = r.CellsZ * BuildingGen.Cell;
            foreach (var w in r.Walls)
            {
                if (w.Kind == SurfaceKind.Roof || w.Kind == SurfaceKind.Floor && w.Y > 0.5f) continue;
                var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(w.Pitch), Mathf.DegToRad(w.Yaw), 0f));
                var o = new Vector3(w.X, w.Y, w.Z);
                foreach (var c in new[] { Vector3.Zero, new Vector3(w.Length, 0f, 0f), new Vector3(0f, w.Height, 0f), new Vector3(w.Length, w.Height, 0f) })
                {
                    var q = o + basis * c;
                    x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X); z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z);
                }
            }
            return (x0, z0, x1, z1);
        }

        /// <summary>Does any of the building's extent stand on paving -- a carriageway, its pavement, a junction's patch? Sampled
        /// every metre round the extent's outline, in the world.</summary>
        bool OnPaving(RoadField roads, TownBuilding b)
        {
            var (x0, z0, x1, z1) = PlanExtent(b.Result);
            // ⚠ A LANE'S PAVEMENT IS NOT A STREET'S (PaintFootprints' lesson again): measured at a street's reach, every house
            // backing onto a lane stood "on the paving" by its back wall
            float keep = PavementGap + _walkWidth, laneKeep = PavementGap + LaneWalkWidth;
            var lanes = new HashSet<int>(LaneRoads);
            bool Paved(Vector3 q)
            {
                q.Y = 0f;
                if (roads.DeepestRoadOverlap(q, laneKeep, out _, out _)) return true;
                return roads.DeepestRoadOverlap(q, keep, out int rd, out _)
                       && (!lanes.Contains(rd) || roads.DeepestRoadOverlap(q, keep, out rd, out _, skipRoad: rd) && !lanes.Contains(rd));
            }
            var corners = new[] { new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), new Vector3(x0, 0f, z1) };
            for (int i = 0; i < 4; i++)
            {
                var a = corners[i]; var c = corners[(i + 1) % 4];
                int n = Mathf.Max(1, Mathf.CeilToInt(a.DistanceTo(c)));
                for (int k = 0; k <= n; k++)
                {
                    var q = b.Frame * a.Lerp(c, (float)k / n);
                    if (Paved(q) || OnAnyPatch(q.X, -q.Z, 0.0)) return true;
                }
            }
            return false;
        }

        /// <summary>Seeds tried for a plan with its front door on the street before settling.</summary>
        public const int FrontTries = 4;
        /// <summary>What the last PlanBuildings retried, and turned round.</summary>
        public int FrontRetried, OnPavingDropped;

        /// <summary>Which side of its plan a building's front door is on (ground floor), or None.</summary>
        static Sides FrontEdge(BuildingGenResult r)
        {
            foreach (var d in r.Doors)
            {
                if (d.Role != DoorRole.Front || d.Floor != 0) continue;
                if (d.AlongX) return d.Line == 0 ? Sides.South : d.Line == r.CellsZ ? Sides.North : Sides.None;
                return d.Line == 0 ? Sides.West : d.Line == r.CellsX ? Sides.East : Sides.None;
            }
            return Sides.None;
        }

        /// <summary>How far the ground floor stands over its pad -- the editor stage's own clearance.</summary>
        public const float BuildingClearance = 0.25f;
        /// <summary>How far round its footprint a building's pad keeps its level before easing back into the ground.</summary>
        public float PadFeather = 3f;

        /// <summary>What a plot asks the generator for. Its building zone is whole 3 m cells by construction (the catalogue's
        /// yards are), so Width x Depth is exactly that, as a plot the building must FIT (FitPlot), with the front door on the
        /// plan's south side -- which the frame turns to face the street. A shop is a business with its back door to the
        /// lane it asked for and no windows down its sides (shops stand shoulder to shoulder); an apartment block is tall.</summary>
        BuildingGenSpec SpecFor(Plot pl, int index)
        {
            ulong h = Mix(((ulong)(uint)Seed << 32) ^ ((ulong)(uint)index * 0x2545F4914F6CDD1DUL) ^ 0xB01D1E5UL);
            var spec = new BuildingGenSpec
            {
                Seed = (int)(h & 0x7FFFFFFF),
                Width = Mathf.Max(1, Mathf.RoundToInt(pl.BuildWidth / BuildingGen.Cell)),
                Depth = Mathf.Max(1, Mathf.RoundToInt(pl.BuildDepth / BuildingGen.Cell)),
                FitPlot = true,
                FrontSide = Sides.South,
                // ⚠ NO BASEMENTS YET: a town's ground is not cut under one until the terrain bending lands (tinyclaw's), and a
                // basement in uncut ground is a stair room whose flight runs into the earth
                Basement = BasementChoice.Forbid,
                // ⚠ A PORCH STANDS OUTSIDE THE FOOTPRINT (bitvox 2026-10-15: "one building there in the small triangle piece of land
                // is extending onto the sidewalk"): a front porch or a shop's canopy reaches PorchRoom past the plan, and a lot
                // with less front yard than that -- a triangle lot's zone is 1.5 m off its line -- put it over the pavement
                Porches = pl.YardFront < PorchRoom ? (pl.YardBack < PorchRoom ? PorchChoice.None : PorchChoice.Back) : PorchChoice.Auto,
            };
            // ⭐ WHAT IS ROUND IT (strawberry 2026-10-15: "give plots some more context about them and their neighbors, and
            // features around them"): no windows in a party wall; the front door toward its own parking lot where it has one
            // beside it ("allowing buildings next to a parking lot use the parking lot as their anchor point for the front");
            // the back door onto a lane behind
            var ctx = Context.TryGetValue(pl, out var cx) ? cx : null;
            if (ctx != null)
            {
                if (ctx.PartyLeft) spec.NoWindows |= LatticeSide(pl, -1);
                if (ctx.PartyRight) spec.NoWindows |= LatticeSide(pl, 1);
                if (ctx.PartyBack) spec.NoWindows |= Sides.North;
                if (ctx.Parking != null && ctx.ParkingSide != 0) spec.FrontSide = LatticeSide(pl, ctx.ParkingSide);
                if (ctx.LaneBehind && !ctx.PartyBack) spec.BackSide = Sides.North;
            }
            switch (pl.Kind)
            {
                case PlotKind.Shop:
                    spec.Kind = BuildingKind.Commercial; spec.MinFloors = 1; spec.MaxFloors = 2;
                    if (ctx == null || !ctx.PartyBack) spec.BackSide = Sides.North;
                    break;
                case PlotKind.Triangle:
                    // a wedge by the avenue: a corner shop as often as a house
                    spec.Kind = (h >> 33 & 1UL) == 0 ? BuildingKind.Commercial : BuildingKind.Residential;
                    spec.MinFloors = 1; spec.MaxFloors = 2;
                    break;
                case PlotKind.Apartments:
                    spec.Kind = BuildingKind.Apartment; spec.MinFloors = 2; spec.MaxFloors = 4;
                    break;
                default:
                    spec.Kind = BuildingKind.Residential; spec.MinFloors = 1;
                    spec.MaxFloors = pl.Kind == PlotKind.HouseSmall ? 1 : 2;
                    break;
            }
            return spec;
        }

        /// <summary>A plot side -- -1 left, +1 right, 0 back, 2 front -- as the side of the building's plan it becomes in FrameFor:
        /// the back is the plan's North, the front its South, and left and right are East or West by which way +X runs.</summary>
        static Sides LatticeSide(Plot pl, int plotSide)
        {
            var zw = new Vector3(-pl.Facing.X, 0f, -pl.Facing.Z).Normalized();
            var xw = Vector3.Up.Cross(zw);
            bool rightIsEast = xw.Dot(new Vector3(pl.Along.X, 0f, pl.Along.Z)) > 0f;
            return plotSide switch
            {
                0 => Sides.North, 2 => Sides.South,
                1 => rightIsEast ? Sides.East : Sides.West,
                _ => rightIsEast ? Sides.West : Sides.East,
            };
        }

        /// <summary>The lattice's frame on a plot: +Z away from the street (the plot's -Facing), +X = up x +Z (a proper
        /// turn, so nothing is mirrored), the origin at whichever front corner of the building zone +X runs from, moved along
        /// the front to centre a building that came out narrower than the zone.</summary>
        static Transform3D FrameFor(Plot pl, int cellsX, float y)
        {
            var zw = new Vector3(-pl.Facing.X, 0f, -pl.Facing.Z).Normalized();
            var xw = Vector3.Up.Cross(zw).Normalized();
            var area = pl.BuildArea();     // front-left, front-right, back-right, back-left; "left" is -Along
            var along = new Vector3(pl.Along.X, 0f, pl.Along.Z).Normalized();
            var origin = xw.Dot(along) > 0f ? area[0] : area[1];
            origin += xw * ((pl.BuildWidth - cellsX * BuildingGen.Cell) * 0.5f);
            origin.Y = y;
            return new Transform3D(new Basis(xw, Vector3.Up, zw), origin);
        }

        /// <summary>The plots that get a building: one block for now (the plot nearest the town's middle and every plot
        /// within a block's reach of it with no street between), or all of them.</summary>
        List<Plot> PlotsForBuildings(RoadField roads)
        {
            var outp = new List<Plot>();
            if (BuildingsMode != "block" && BuildingsMode != "all") return outp;
            foreach (var pl in Plots)
                if (pl.Kind != PlotKind.Parking && pl.BuildWidth >= BuildingGen.Cell - 0.01f && pl.BuildDepth >= BuildingGen.Cell - 0.01f)
                    outp.Add(pl);
            if (BuildingsMode == "all" || outp.Count == 0) return outp;
            Plot seed = null; float best = float.MaxValue;
            foreach (var pl in outp) { float d = Flat(pl.Centre, Centre); if (d < best) { best = d; seed = pl; } }
            bool StreetBetween(Vector3 a, Vector3 b)
            {
                for (int k = 1; k < 16; k++)
                    if (roads.DeepestRoadOverlap(a.Lerp(b, k / 16f), 0f, out int r, out _) && Built.Contains(r)) return true;
                return false;
            }
            outp.RemoveAll(pl => pl != seed && (Flat(pl.Centre, seed.Centre) > 90f || StreetBetween(pl.Centre, seed.Centre)));
            return outp;
        }

        /// <summary>Generate a building for every plot PlotsForBuildings picks, level its pad and work out its frame. Logs
        /// what each one came out as and how long the generator took over it -- the number the whole-town decision waits on.
        /// Returns how many generated.</summary>
        public int PlanBuildings(RoadField roads, Terrain terr)
        {
            Buildings.Clear();
            if (roads == null) return 0;
            var pick = PlotsForBuildings(roads);
            if (pick.Count == 0) return 0;
            var index = new Dictionary<Plot, int>();
            for (int i = 0; i < Plots.Count; i++) index[Plots[i]] = i;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double genMs = 0; FrontRetried = 0; OnPavingDropped = 0;
            // ⚡ THE GENERATOR ON EVERY CORE (a whole town spent 11 s here, 115 buildings at 96 ms one after the other). BuildingGen
            // is the core's -- no Godot in it, its switches [ThreadStatic], its tables read-only -- so a plot's tries are a function
            // of their specs alone. The specs are made here on this thread (they read the plan), and everything after the generator
            // -- the pads, which cut the terrain -- stays on it, in plot order: the same town whatever the scheduling.
            var tries = new BuildingGenSpec[pick.Count, FrontTries];
            for (int i = 0; i < pick.Count; i++)
            {
                int firstSeed = SpecFor(pick[i], index[pick[i]]).Seed;
                for (int tryN = 0; tryN < FrontTries; tryN++)
                {
                    var spec = SpecFor(pick[i], index[pick[i]]); spec.Seed = firstSeed + tryN * 7919;     // (BuildingGenSpec.Copy is internal to the core)
                    tries[i, tryN] = spec;
                }
            }
            var gens = new (BuildingGenResult Res, int Try, BuildingGenResult First, string Err, double Ms)[pick.Count];
            var genClock = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Tasks.Parallel.For(0, pick.Count, i =>
            {
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                // ⚠ THE FRONT DOOR ON THE STREET. FitPlot may give up the side asked for ("no layout put the front door on the
                // South side: it went where it could") -- 7 of 115 in the first whole town. So another seed, up to FrontTries;
                // and failing those, a door on the plan's NORTH side is turned to the street with the whole building (same
                // rectangle, half round).
                BuildingGenResult first = null; string err = null;
                for (int tryN = 0; tryN < FrontTries; tryN++)
                {
                    BuildingGenResult res = null;
                    try { res = BuildingGen.Generate(tries[i, tryN]); }
                    catch (System.Exception e) { err ??= e.Message; }
                    if (res == null) continue;
                    first ??= res;
                    if (FrontEdge(res) == tries[i, tryN].FrontSide) { gens[i] = (res, tryN, first, err, t0.Elapsed.TotalMilliseconds); return; }
                }
                gens[i] = (null, -1, first, err, t0.Elapsed.TotalMilliseconds);
            });
            double genWall = genClock.Elapsed.TotalMilliseconds;
            for (int i = 0; i < pick.Count; i++)
            {
                var pl = pick[i]; var gr = gens[i];
                var b = new TownBuilding { Plot = pl, Spec = tries[i, 0], Failed = gr.Err };
                if (gr.Res != null) { b.Result = gr.Res; b.Spec = tries[i, gr.Try]; if (gr.Try > 0) FrontRetried++; }
                if (b.Result == null && gr.First != null) { b.Result = gr.First; if (b.Spec.FrontSide == Sides.South && FrontEdge(gr.First) == Sides.North) b.TurnedRound = true; }
                if (b.Result != null) b.Failed = null;
                b.Ms = gr.Ms; genMs += b.Ms;
                if (b.Result == null) { Buildings.Add(b); continue; }
                // the pad: the building zone's ground, averaged, levelled -- cut and filled inside, only cut outside
                float sum = 0f; int n = 0;
                var area = pl.BuildArea();
                for (float u = 0f; u <= 1.001f; u += 0.25f)
                    for (float v = 0f; v <= 1.001f; v += 0.25f)
                    {
                        var q = area[0].Lerp(area[1], u).Lerp(area[3].Lerp(area[2], u), v);
                        sum += terr != null ? terr.SampleHeight(q.X, q.Z) : pl.Front.Y; n++;
                    }
                b.PadY = sum / Mathf.Max(1, n);
                b.Frame = FrameFor(pl, b.Result.CellsX, b.PadY + BuildingClearance);
                if (b.TurnedRound)
                {
                    var f0 = b.Frame;
                    b.Frame = new Transform3D(new Basis(-f0.Basis.X, Vector3.Up, -f0.Basis.Z),
                                              f0.Origin + f0.Basis.X * (b.Result.CellsX * BuildingGen.Cell) + f0.Basis.Z * (b.Result.CellsZ * BuildingGen.Cell));
                }
                if (terr != null)
                {
                    var f = b.Frame;
                    var at = f.Origin - Vector3.Up * BuildingClearance;
                    float padY = b.PadY;
                    terr.ConformToSurface(at, f.Basis.X.Normalized(), f.Basis.Z.Normalized(), 0f, b.Result.CellsX * BuildingGen.Cell, 0f, b.Result.CellsZ * BuildingGen.Cell,
                                          (x, z) => padY, Terrain.GridUnit, PadFeather,
                                          keepOut: (x, z) => roads.DeepestRoadOverlap(new Vector3(x, 0f, z), PavementGap + _walkWidth, out _, out _));
                }
                // and never over the paving, whatever stands outside the plan: a building that would is not built (and said so)
                if (OnPaving(roads, b))
                {
                    var (ex0, ez0, ex1, ez1) = PlanExtent(b.Result);
                    Log.Print($"[towngrid]   not built: a {b.Result.Kind} on a {pl.Kind} plot would stand on the paving -- asked {b.Spec.Width}x{b.Spec.Depth} cells "
                            + $"(zone {pl.BuildWidth:0.0}x{pl.BuildDepth:0.0} m, front yard {pl.YardFront:0.0} m), got {b.Result.CellsX}x{b.Result.CellsZ}, "
                            + $"standing x {ex0:0.0}..{ex1:0.0} z {ez0:0.0}..{ez1:0.0}{(b.Result.Notes.Count > 0 ? " -- " + string.Join("; ", b.Result.Notes) : "")}");
                    b.Failed = "it would stand on the paving"; b.Result = null; OnPavingDropped++;
                }
                Buildings.Add(b);
            }
            // ⭐ PARTY WALLS FROM THE FOOTPRINTS AS GENERATED: a side of a building whose edge another building's footprint meets.
            // Its walls on that side are pulled in half their thickness by the runtime builder, so the two buildings' outer faces
            // meet on the lot line -- butted, not one wall drawn inside the other (strawberry: "dont overlap the walls on top of
            // eachother, have both walls butt up to eachother").
            var rects = new List<(TownBuilding b, Vector3[] q)>();
            foreach (var b in Buildings)
                if (b.Result != null)
                {
                    float wx = b.Result.CellsX * BuildingGen.Cell, dz = b.Result.CellsZ * BuildingGen.Cell;
                    rects.Add((b, new[] { b.Frame * Vector3.Zero, b.Frame * new Vector3(wx, 0f, 0f), b.Frame * new Vector3(wx, 0f, dz), b.Frame * new Vector3(0f, 0f, dz) }));
                }
            int partied = 0;
            foreach (var (b, _) in rects)
            {
                float wx = b.Result.CellsX * BuildingGen.Cell, dz = b.Result.CellsZ * BuildingGen.Cell;
                foreach (var (side, a0, a1, outw) in new[] {
                    (Sides.West, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, dz), new Vector3(-1f, 0f, 0f)),
                    (Sides.East, new Vector3(wx, 0f, 0f), new Vector3(wx, 0f, dz), new Vector3(1f, 0f, 0f)),
                    (Sides.South, new Vector3(0f, 0f, 0f), new Vector3(wx, 0f, 0f), new Vector3(0f, 0f, -1f)),
                    (Sides.North, new Vector3(0f, 0f, dz), new Vector3(wx, 0f, dz), new Vector3(0f, 0f, 1f)) })
                {
                    bool met = false;
                    foreach (float f in new[] { 0.2f, 0.5f, 0.8f })
                    {
                        var q = b.Frame * (a0.Lerp(a1, f) + outw * 0.15f);
                        foreach (var (o, oq) in rects) if (!ReferenceEquals(o, b) && InConvex(oq, q.X, q.Z, 0.05f)) { met = true; break; }
                        if (met) break;
                    }
                    if (met) b.Party |= side;
                }
                if (b.Party != Sides.None) partied++;
            }
            // MEASURED: buildings whose footprints OVERLAP (not just touch) -- the inset cannot butt two walls that cross
            int overlaps = 0;
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    var (bi, qi) = rects[i]; var (bj, qj) = rects[j];
                    if (Flat(qi[0], qj[0]) > 80f) continue;
                    // how deep j's corners and edge points reach inside i (and the other way)
                    float deep = 0f;
                    foreach (var (qa, qb) in new[] { (qi, qj), (qj, qi) })
                        for (int e = 0; e < 4; e++)
                            for (float f = 0f; f <= 1.001f; f += 0.125f)
                            {
                                var pt = qb[e].Lerp(qb[(e + 1) % 4], f);
                                if (!InConvex(qa, pt.X, pt.Z, -0.05f)) continue;
                                // its depth: the least distance to qa's edges
                                float dmin = float.MaxValue;
                                for (int k = 0; k < 4; k++)
                                {
                                    var a = qa[k]; var c = qa[(k + 1) % 4];
                                    var ab = new Vector2(c.X - a.X, c.Z - a.Z); float t = Mathf.Clamp(((pt.X - a.X) * ab.X + (pt.Z - a.Z) * ab.Y) / Mathf.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
                                    dmin = Mathf.Min(dmin, new Vector2(a.X + ab.X * t - pt.X, a.Z + ab.Y * t - pt.Z).Length());
                                }
                                deep = Mathf.Max(deep, dmin);
                            }
                    if (deep < 0.05f)
                    {
                        // (MEASURED: two buildings within half a metre of each other, and whether each saw the other as a party side)
                        float gap = float.MaxValue;
                        foreach (var (qa, qb) in new[] { (qi, qj), (qj, qi) })
                            foreach (var pt in qb)
                                for (int k = 0; k < 4; k++)
                                {
                                    var a = qa[k]; var c = qa[(k + 1) % 4];
                                    var ab = new Vector2(c.X - a.X, c.Z - a.Z); float t = Mathf.Clamp(((pt.X - a.X) * ab.X + (pt.Z - a.Z) * ab.Y) / Mathf.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
                                    gap = Mathf.Min(gap, new Vector2(a.X + ab.X * t - pt.X, a.Z + ab.Y * t - pt.Z).Length());
                                }
                        if (gap < 0.5f && ProbeEverything)
                            Log.Print($"[towngrid]   NEAR {gap:0.00} m: {bi.Plot.Kind} (party {bi.Party}) and {bj.Plot.Kind} (party {bj.Party}) near ({qi[0].X:0},{qi[0].Z:0})");
                        continue;
                    }
                    overlaps++;
                    Log.Print($"[towngrid]   OVERLAP {deep:0.00} m: a {bi.Result.Kind} on a {bi.Plot.Kind} plot ({bi.Result.CellsX}x{bi.Result.CellsZ} of {bi.Spec.Width}x{bi.Spec.Depth}, zone {bi.Plot.BuildWidth:0.0}x{bi.Plot.BuildDepth:0.0}) "
                            + $"and a {bj.Result.Kind} on a {bj.Plot.Kind} plot ({bj.Result.CellsX}x{bj.Result.CellsZ} of {bj.Spec.Width}x{bj.Spec.Depth}, zone {bj.Plot.BuildWidth:0.0}x{bj.Plot.BuildDepth:0.0}) "
                            + $"near ({(qi[0].X + qj[0].X) * 0.5f:0},{(qi[0].Z + qj[0].Z) * 0.5f:0})");
                }
            if (overlaps > 0) Log.Print($"[towngrid] buildings overlapping: {overlaps} pair(s)");
            int ok = 0, fronted = 0; var kinds = new Dictionary<string, int>();
            foreach (var b in Buildings)
            {
                if (b.Result == null) continue;
                ok++;
                bool front = FrontEdge(b.Result) == b.Spec.FrontSide || b.TurnedRound && FrontEdge(b.Result) == Sides.North;
                if (front) fronted++;
                string k = $"{b.Result.Kind}";
                kinds[k] = (kinds.TryGetValue(k, out int c) ? c : 0) + 1;
                if (ProbeEverything || BuildingsMode == "block")
                    Log.Print($"[towngrid]   building on a {b.Plot.Kind} plot: asked {b.Spec.Width}x{b.Spec.Depth} cells, got {b.Result.CellsX}x{b.Result.CellsZ}, "
                            + $"{b.Result.Floors} floor(s), {b.Result.Layout.Count} rooms, front door {(front ? "on the street" : "NOT on the street")}, "
                            + $"{b.Ms:0} ms{(b.Result.Notes.Count > 0 ? " -- " + string.Join("; ", b.Result.Notes) : "")}");
            }
            int failed = Buildings.Count - ok;
            Log.Print($"[towngrid] buildings ({BuildingsMode}): {ok} generated on {pick.Count} plots{(failed > 0 ? $", {failed} not built (first: {Buildings.Find(x => x.Failed != null)?.Failed}; {OnPavingDropped} would have stood on the paving)" : "")}, "
                    + $"{fronted} with the front door where it was asked ({FrontRetried} on another seed, {Buildings.FindAll(x => x.TurnedRound).Count} turned round, "
                    + $"{Buildings.FindAll(x => x.Result != null && x.Spec.FrontSide != Sides.South).Count} toward their parking lot), {partied} with a party wall; generator {genWall:0} ms on {System.Environment.ProcessorCount} threads ({genMs / Mathf.Max(1, pick.Count):0} ms a building), "
                    + $"pads included {clock.ElapsedMilliseconds} ms -- {string.Join(", ", System.Linq.Enumerable.Select(kinds, kv => $"{kv.Value} {kv.Key}"))}");
            return ok;
        }

        /// <summary>Player spawns on the town's pavements, for Generate Town: one at the middle of each street long enough, on
        /// alternate sides, standing along it, up to `max`. Returns how many.</summary>
        public int PlaceSpawns(RoadField roads, EditorSpawns spawns, int max = 12)
        {
            if (roads == null || spawns == null) return 0;
            // (into the category showing, which on a fresh map is Player -- as ProcIslandSpawn.PlacePlayerSpawns relies on.
            // ⚠ NOT SetCategoryTo(0): that saves the category it leaves, which writes this map's spawn file to disk)
            int n = 0;
            var terr = Terrain.Active;
            int stride = Mathf.Max(1, Built.Count / Mathf.Max(1, max));     // spread over the town, not the first streets laid
            for (int i = 0; i < Built.Count && n < max; i += stride)
            {
                int r = Built[i];
                if (ProfileOf(r) == StreetProfile.KerbOnly) continue;          // no footway to stand on
                float L = roads.RoadLength(r);
                if (L < 30f) continue;
                float d = L * 0.5f;
                if (!roads.EvaluateAlong(r, d, out var pos, out var tan, snapTerrain: false)) continue;
                var right = new Vector3(tan.Z, 0f, -tan.X).Normalized();
                int side = n % 2 == 0 ? 1 : -1;
                if (MouthAt(r, side, d - 3f, d + 3f) != null || EntranceAt(r, side, d - 3f, d + 3f) != null) side = -side;
                // a metre in from the back of the pavement: on the footway behind a verge, clear of a planter or a lamp at the kerb
                var at = pos + right * (side * (roads.RoadHalfWidth(r) + BayExtra(r, side, d) + PavementGap + PavementWidth - 1f));
                at.Y = (terr != null ? Mathf.Max(terr.SampleHeight(at.X, at.Z), pos.Y) : pos.Y) + PavementKerb + 0.1f;
                // facing along the street (the spawn's yaw: 0 looks down +Z, 180 down -Z -- MapShowcase's)
                spawns.AddSpawn(at, Mathf.RadToDeg(Mathf.Atan2(tan.X, tan.Z)));
                n++;
            }
            Log.Print($"[towngrid] player spawns: {n} on the town's pavements");
            return n;
        }

        /// <summary>UG_TOWNMASSING=1: the buildings as their massing (a box per floor cell, a red post at the front door) -- the
        /// check on the town's half alone.</summary>
        public static bool ShowMassing = System.Environment.GetEnvironmentVariable("UG_TOWNMASSING") == "1";

        /// <summary>Every generated building, BUILT by the generator's runtime builder (BuildingRuntime -- the editor stage's own
        /// path, lifted out of it) in its frame. Returns the buildings put up and logs what building cost: the half of the
        /// whole-town decision the generator's ~90-140 ms a building is not.</summary>
        public int BuildBuildings(Node3D parent, EditorObjects objs)
        {
            if (parent == null || Buildings.Count == 0) return 0;
            if (ShowMassing) return BuildBuildingMassing(parent);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int made = 0, failed = 0, walls = 0, props = 0, clipped = 0;
            var live = new BuildingRuntime.Furnished();
            foreach (var b in Buildings)
            {
                if (b.Result == null) continue;
                Node3D node = null;
                try { node = BuildingRuntime.Build(b.Result, b.Frame, parent, objs, $"Building_{b.Plot.Kind}_{made}", b.Party); }
                catch (System.Exception e) { if (failed++ == 0) Log.Err($"[towngrid] building on a {b.Plot.Kind} plot failed to build: {e.Message}\n{e.StackTrace}"); }
                if (node == null) continue;
                made++; walls += BuildingRuntime.LastWalls; props += BuildingRuntime.LastProps; clipped += BuildingRuntime.LastClipped;
                live.Add(BuildingRuntime.LastFurnished);
            }
            int ceiling = 0; foreach (var l in live.CeilingLights) if (l != null) ceiling++;
            Log.Print($"[towngrid] buildings built: {made} in {clock.ElapsedMilliseconds} ms ({clock.ElapsedMilliseconds / Mathf.Max(1, made)} ms a building), "
                    + $"{walls} surfaces ({clipped} fitted to a party line), {props} pieces of furniture{(failed > 0 ? $", {failed} FAILED" : "")} -- "
                    + $"{live.Colliders} with a collider, {live.Containers} containers, {live.Lamps.Count} lamps ({ceiling} ceiling lights), "
                    + $"{live.Switches.Count} light switches, {live.Outlets.Count} outlets; furnishing: {live.Timings}");
            LogLampDensity(live.Lamps);
            return made;
        }

        /// <summary>How many of the town's lamps a camera can have in range at once: the most lamps within `r` of any one lamp
        /// (a camera standing at the densest spot), at the fade-out distance the lamps actually have (BuildingRuntime.LampFadeBegin
        /// + LampFadeLength) and at 100 m, which is roughly what a view down a street would hold without the fade.</summary>
        static void LogLampDensity(List<LampLight> lamps)
        {
            if (lamps.Count == 0) return;
            var pos = new List<Vector3>(lamps.Count);
            foreach (var l in lamps) if (GodotObject.IsInstanceValid(l) && l.IsInsideTree()) pos.Add(l.GlobalPosition);
            float fade = BuildingRuntime.LampFadeBegin + BuildingRuntime.LampFadeLength;
            int worstFade = 0, worst100 = 0;
            foreach (var a in pos)
            {
                int nf = 0, n100 = 0;
                foreach (var b in pos)
                {
                    float d2 = a.DistanceSquaredTo(b);
                    if (d2 <= fade * fade) nf++;
                    if (d2 <= 100f * 100f) n100++;
                }
                worstFade = Mathf.Max(worstFade, nf); worst100 = Mathf.Max(worst100, n100);
            }
            Log.Print($"[towngrid] lamps: {pos.Count} lights in the town; at the densest spot {worstFade} within {fade:0} m (drawn: they fade out past that) "
                    + $"and {worst100} within 100 m -- against Forward+'s 512 clustered elements a view");
        }

        /// <summary>Until the runtime builder lands: each generated building as its MASSING -- one translucent box per floor
        /// over that floor's own footprint, and a red post at the front door -- in its frame. Returns the buildings shown.</summary>
        public int BuildBuildingMassing(Node3D parent)
        {
            if (parent == null || Buildings.Count == 0) return 0;
            int made = 0;
            var floorMat = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.85f, 0.9f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
            var doorMat = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.1f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            foreach (var b in Buildings)
            {
                if (b.Result == null) continue;
                var root = new Node3D { Name = $"Building_{b.Plot.Kind}_{made}", Transform = b.Frame };
                float storey = 3f;
                var cells = new HashSet<(int, int, int)>();
                foreach (var g in b.Result.Layout)
                    foreach (var r in g.Rects)
                        for (int x = r.X0; x < r.X1; x++) for (int z = r.Z0; z < r.Z1; z++) cells.Add((g.Floor, x, z));
                foreach (var (fl, x, z) in cells)
                    root.AddChild(new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = new Vector3(BuildingGen.Cell - 0.05f, storey - 0.1f, BuildingGen.Cell - 0.05f) },
                        Position = new Vector3((x + 0.5f) * BuildingGen.Cell, fl * storey + storey * 0.5f, (z + 0.5f) * BuildingGen.Cell),
                        MaterialOverride = floorMat,
                    });
                foreach (var d in b.Result.Doors)
                {
                    if (d.Role != DoorRole.Front) continue;
                    var (mx, mz) = d.Middle;
                    root.AddChild(new MeshInstance3D
                    {
                        Mesh = new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.25f, Height = 4f },
                        Position = new Vector3(mx, d.Floor * storey + 2f, mz), MaterialOverride = doorMat,
                    });
                }
                parent.AddChild(root);
                made++;
            }
            return made;
        }
    }
}
