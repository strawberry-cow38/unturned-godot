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
        }
        /// <summary>What the last PlanBuildings generated.</summary>
        public readonly List<TownBuilding> Buildings = new();

        /// <summary>Seeds tried for a plan with its front door on the street before settling.</summary>
        public const int FrontTries = 4;
        /// <summary>What the last PlanBuildings retried, and turned round.</summary>
        public int FrontRetried;

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
            };
            switch (pl.Kind)
            {
                case PlotKind.Shop:
                    spec.Kind = BuildingKind.Commercial; spec.MinFloors = 1; spec.MaxFloors = 2;
                    spec.BackSide = Sides.North;
                    if (pl.YardLeft < 1f && pl.YardRight < 1f) spec.NoWindows = Sides.East | Sides.West;
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
                if (pl.Outline == null && pl.Kind != PlotKind.Parking && pl.BuildWidth >= BuildingGen.Cell && pl.BuildDepth >= BuildingGen.Cell)
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
            double genMs = 0; FrontRetried = 0;
            foreach (var pl in pick)
            {
                var b = new TownBuilding { Plot = pl, Spec = SpecFor(pl, index[pl]) };
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                // ⚠ THE FRONT DOOR ON THE STREET. FitPlot may give up the side asked for ("no layout put the front door on the
                // South side: it went where it could") -- 7 of 115 in the first whole town. So another seed, up to FrontTries;
                // and failing those, a door on the plan's NORTH side is turned to the street with the whole building (same
                // rectangle, half round).
                BuildingGenResult first = null; int firstSeed = b.Spec.Seed;
                for (int tryN = 0; tryN < FrontTries; tryN++)
                {
                    var spec = SpecFor(pl, index[pl]); spec.Seed = firstSeed + tryN * 7919;     // (BuildingGenSpec.Copy is internal to the core)
                    BuildingGenResult res = null;
                    try { res = BuildingGen.Generate(spec); }
                    catch (System.Exception e) { b.Failed ??= e.Message; }
                    if (res == null) continue;
                    first ??= res;
                    if (FrontEdge(res) == Sides.South) { b.Result = res; b.Spec = spec; if (tryN > 0) FrontRetried++; break; }
                }
                if (b.Result == null && first != null) { b.Result = first; if (FrontEdge(first) == Sides.North) b.TurnedRound = true; }
                if (b.Result != null) b.Failed = null;
                b.Ms = t0.Elapsed.TotalMilliseconds; genMs += b.Ms;
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
                Buildings.Add(b);
            }
            int ok = 0, fronted = 0; var kinds = new Dictionary<string, int>();
            foreach (var b in Buildings)
            {
                if (b.Result == null) continue;
                ok++;
                bool front = FrontEdge(b.Result) == Sides.South || b.TurnedRound && FrontEdge(b.Result) == Sides.North;
                if (front) fronted++;
                string k = $"{b.Result.Kind}";
                kinds[k] = (kinds.TryGetValue(k, out int c) ? c : 0) + 1;
                if (ProbeEverything || BuildingsMode == "block")
                    Log.Print($"[towngrid]   building on a {b.Plot.Kind} plot: asked {b.Spec.Width}x{b.Spec.Depth} cells, got {b.Result.CellsX}x{b.Result.CellsZ}, "
                            + $"{b.Result.Floors} floor(s), {b.Result.Layout.Count} rooms, front door {(front ? "on the street" : "NOT on the street")}, "
                            + $"{b.Ms:0} ms{(b.Result.Notes.Count > 0 ? " -- " + string.Join("; ", b.Result.Notes) : "")}");
            }
            int failed = Buildings.Count - ok;
            Log.Print($"[towngrid] buildings ({BuildingsMode}): {ok} generated on {pick.Count} plots{(failed > 0 ? $", {failed} FAILED (first: {Buildings.Find(x => x.Failed != null)?.Failed})" : "")}, "
                    + $"{fronted} with the front door on the street ({FrontRetried} on another seed, {Buildings.FindAll(x => x.TurnedRound).Count} turned round); generator {genMs:0} ms ({genMs / Mathf.Max(1, pick.Count):0} ms a building), "
                    + $"pads included {clock.ElapsedMilliseconds} ms -- {string.Join(", ", System.Linq.Enumerable.Select(kinds, kv => $"{kv.Value} {kv.Key}"))}");
            return ok;
        }

        /// <summary>The generator's runtime builder: a BuildingGenResult and the frame it stands in, to the building's nodes. Set
        /// by whoever owns it (the editor's stage and the town share it); null until then, and the town shows massing.</summary>
        public static System.Func<BuildingGenResult, Transform3D, Node3D> RuntimeBuilder;

        /// <summary>Every generated building, built: through RuntimeBuilder where there is one, as massing where not. Returns
        /// the buildings put up, and logs what they cost to build -- the half of the whole-town decision the generator's own
        /// time is not.</summary>
        public int BuildBuildings(Node3D parent)
        {
            if (parent == null || Buildings.Count == 0) return 0;
            if (RuntimeBuilder == null) return BuildBuildingMassing(parent);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int made = 0, failed = 0;
            foreach (var b in Buildings)
            {
                if (b.Result == null) continue;
                Node3D node = null;
                try { node = RuntimeBuilder(b.Result, b.Frame); }
                catch (System.Exception e) { if (failed++ == 0) Log.Err($"[towngrid] building on a {b.Plot.Kind} plot failed to build: {e.Message}"); }
                if (node == null) continue;
                if (node.GetParent() == null) parent.AddChild(node);
                made++;
            }
            Log.Print($"[towngrid] buildings built: {made} in {clock.ElapsedMilliseconds} ms ({clock.ElapsedMilliseconds / Mathf.Max(1, made)} ms a building)"
                    + (failed > 0 ? $", {failed} FAILED" : ""));
            return made;
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
