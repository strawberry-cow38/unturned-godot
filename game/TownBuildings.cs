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
        }
        /// <summary>What the last PlanBuildings generated.</summary>
        public readonly List<TownBuilding> Buildings = new();

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
            double genMs = 0;
            foreach (var pl in pick)
            {
                var b = new TownBuilding { Plot = pl, Spec = SpecFor(pl, index[pl]) };
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                try { b.Result = BuildingGen.Generate(b.Spec); }
                catch (System.Exception e) { b.Failed = e.Message; }
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
                if (terr != null)
                {
                    var f = b.Frame;
                    var at = f.Origin - Vector3.Up * BuildingClearance;
                    float padY = b.PadY;
                    terr.ConformToSurface(at, f.Basis.X, f.Basis.Z, 0f, b.Result.CellsX * BuildingGen.Cell, 0f, b.Result.CellsZ * BuildingGen.Cell,
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
                bool front = false;
                foreach (var d in b.Result.Doors) if (d.Role == DoorRole.Front && d.Floor == 0 && d.AlongX && d.Line == 0) front = true;
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
                    + $"{fronted} with the front door on the street; generator {genMs:0} ms ({genMs / Mathf.Max(1, pick.Count):0} ms a building), "
                    + $"pads included {clock.ElapsedMilliseconds} ms -- {string.Join(", ", System.Linq.Enumerable.Select(kinds, kv => $"{kv.Value} {kv.Key}"))}");
            return ok;
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
