using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The town on gentle hills (strawberry 2026-10-13: "add support for the grid to follow gentle hills and
    /// slopes").
    ///
    /// ⭐ EVERY PIECE OF THE TOWN HAD ONLY EVER MET FLAT GROUND, where "the same height" is one number and every join
    /// works by accident. On a hill each piece has to agree with its neighbour about the height at the spot they touch,
    /// and each place two pieces worked it out separately showed up as a crack, a step or a buried corner. So this builds
    /// the demo town's own pipeline -- a real Terrain, the same swells UG_TOWNHILLS rolls under it, GradeTerrain cutting
    /// the ground, then every mesh -- and holds it to the probes the fixes were found with: the ground under the paving,
    /// everything else the town laid over the ground, the pavements where they meet, the paint over what it is painted
    /// on, the lanes' mouths and the corner fill.
    ///
    /// ⚠ EACH PROBE GETS A CONTROL THAT MUST FAIL. Either the old behaviour, switched back on, has to show the defect
    /// (the ground grading's old reach, the level lane end), or the probe is handed the defect directly -- a pavement
    /// lifted 5 cm, a corner sunk a metre -- and has to see it. A zero from a probe that cannot see anything is not a
    /// result.</summary>
    public sealed class TownHillsTests : GameTest
    {
        public override string Name => "editor.town_hills";
        public override int Tier => 2;
        public override double TimeoutSimSeconds => 120;

        /// <summary>The two long swells Main rolls under the demo town for UG_TOWNHILLS: a gentle 1-in-10 at the
        /// steepest at 5 m, raised by their own amplitude so the troughs stay on the map's ground.</summary>
        static Func<float, float, float> Hills(float amp) => (x, z) =>
            amp * (1f + 0.6f * Mathf.Sin(x * Mathf.Tau / 320f + 0.7f) * Mathf.Cos(z * Mathf.Tau / 280f + 1.3f)
                      + 0.4f * Mathf.Sin((x * 0.6f + z * 0.8f) * Mathf.Tau / 410f + 2.1f));

        sealed class HillTown
        {
            public Terrain Terr; public RoadField Roads; public TownRoadGrid Grid; public Node3D Host; public TownRoadGrid.Report Rep;
            public float Relief;
        }

        /// <summary>Main's town build, in Main's order, on its own terrain. <paramref name="oldCover"/>: grade with the
        /// conform's old ceiling reach (the island road's 9.2 m for every line) -- the control for the ground probe.
        /// ⚠ UNDER THE OLD GRADING (TownRoadGrid.OldGrade): the old reach was a defect of the corridor conform that cut the
        /// ground under the streets, and the streets are now cut under by CeilCarriageways instead, which the reach does not
        /// touch -- so without it the control has no defect left to show.</summary>
        HillTown Build(float amp, bool oldCover = false)
        {
            bool wasOld = TownRoadGrid.OldGrade;
            if (oldCover) TownRoadGrid.OldGrade = true;
            try { return BuildTown(amp, oldCover); }
            finally { TownRoadGrid.OldGrade = wasOld; }
        }
        HillTown BuildTown(float amp, bool oldCover)
        {
            var t = new HillTown();
            t.Terr = Terrain.CreateFlat(3, 3);
            World.AddChild(t.Terr);
            if (amp > 0f) t.Terr.AddHeightField(Hills(amp), t.Terr.WorldMinX, t.Terr.WorldMinZ, t.Terr.WorldMaxX, t.Terr.WorldMaxZ);
            t.Roads = new RoadField();
            World.AddChild(t.Roads);
            foreach (var (m, w) in new[] { (0, 9.2f), (1, 6.9f), (6, 4.6f) })
            {
                t.Roads.DebugSetMaterialWidth(m, w);
                t.Roads.DebugSetMaterialDepth(m, 0.22f);       // the asphalt over the kerb's foot, as the game's
            }
            var site = new Vector3((t.Terr.WorldMinX + t.Terr.WorldMaxX) * 0.5f, 0f, (t.Terr.WorldMinZ + t.Terr.WorldMaxZ) * 0.5f);
            site.Y = t.Terr.SampleHeight(site.X, site.Z);
            float lo = float.MaxValue, hi = float.MinValue;
            for (float dx = -180f; dx <= 180f; dx += 20f)
                for (float dz = -180f; dz <= 180f; dz += 20f)
                {
                    float y = t.Terr.SampleHeight(site.X + dx, site.Z + dz);
                    lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y);
                }
            t.Relief = hi - lo;
            t.Grid = new TownRoadGrid { Centre = site, Seed = 1337, Bearing = 0.35f, GradeSite = true, FollowTerrain = false, Diagonal = true };
            t.Rep = t.Grid.Generate(t.Roads, (x, z) => t.Terr.SampleHeight(x, z));
            t.Host = new Node3D();
            World.AddChild(t.Host);
            var walk = new EditorSidewalk { Width = t.Grid.PavementWidth };
            t.Host.AddChild(walk);
            t.Grid.PlanPlots(t.Roads, walk.Width);
            if (oldCover) System.Environment.SetEnvironmentVariable("UG_TOWNOLDCOVER", "1");
            try { t.Grid.GradeTerrain(t.Terr, t.Roads); }
            finally { if (oldCover) System.Environment.SetEnvironmentVariable("UG_TOWNOLDCOVER", null); }
            t.Grid.BuildPatchMeshes(t.Host, t.Roads);
            t.Grid.BuildMedians(t.Host, t.Roads);
            t.Grid.BuildRoadLines(t.Host, t.Roads);
            t.Grid.LaySidewalks(t.Host, t.Roads);
            t.Grid.LaySidewalkCorners(t.Host, t.Roads, walk);
            t.Grid.BuildLanes(t.Host, t.Roads);
            t.Grid.BuildCornerFill(t.Host, t.Roads);
            return t;
        }

        static MeshInstance3D FirstMesh(Node n, string prefix)
        {
            if (n is MeshInstance3D mi && mi.Mesh != null && mi.Name.ToString().StartsWith(prefix)) return mi;
            foreach (var c in n.GetChildren()) { var f = FirstMesh(c, prefix); if (f != null) return f; }
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            var activeBefore = Terrain.Active;
            bool laneBankBefore = TownRoadGrid.NoLaneBank;
            yield return Ticks(1);

            var h = Build(5f);
            yield return Ticks(2);
            Func<float, float, float> ground = (x, z) => h.Terr.SampleHeight(x, z);
            T.Check($"the town builds on the hills ({h.Rep})", h.Rep.Roads > 30 && h.Rep.Crossroads > 4 && h.Rep.DiagonalCrossings > 0);
            T.Check($"...and they ARE hills: {h.Relief:0.0} m of relief under it (a flat site would pass every check below)", h.Relief > 3f);

            // ---- 1. THE GROUND STAYS UNDER THE PAVING. Each ground point takes its nearest road's height, so beside a
            // junction on a slope it can take the OTHER road's, higher up; the ceiling against that has to reach each road's
            // own width (the town's mains are 10.7 m, the island's road it was written for 9.2 m).
            var gp = h.Grid.GroundPokes(h.Roads, ground);
            T.Check($"ground under every street, lane and slab: {gp.pokes}/{gp.samples} points within 3 cm of the surface or through it, worst {gp.worst * 100f:0.0} cm ({gp.what})",
                    gp.samples > 50000 && gp.pokes == 0);

            // ---- 2. NOTHING ELSE THE TOWN LAID IS BURIED: pavements, corners (whose straights held ONE height, sampled up
            // to 30 m out along a street -- a metre under it at a 45-degree crossing), the corner asphalt, the crossings.
            var over = TownRoadGrid.GroundOverMeshes(h.Host, ground);
            foreach (string kind in new[] { "Sidewalk", "Sidewalk_Corner", "Crossing" })
            {
                var m = over.Find(o => o.kind == kind);
                T.Check($"...{kind}: {m.pokes}/{m.samples} top vertices within 3 cm of the ground or under it, worst {m.worst * 100f:0.0} cm",
                        m.samples > 0 && m.pokes == 0);
            }
            // (the corner fill, where it is what you see: under a slab it may sit where it likes, nothing shows it)
            var vf = h.Grid.VisibleFillOverGround(h.Roads, ground);
            T.Check($"...the corner fill where nothing paves over it: {vf.under}/{vf.samples} points within 3 cm of the ground or under it, worst {vf.worst * 100f:0.0} cm",
                    vf.samples > 100 && vf.under == 0);

            // ---- 3. PAVEMENTS MEET AT ONE HEIGHT: the acute corners beside two streets 0.9 m apart, a lane's pavement and
            // its mouth's return, a closed lane's kerb strip.
            var steps = TownRoadGrid.PavementSteps(h.Host);
            T.Check($"pavement pieces meet at one height: {steps.steps}/{steps.joins} meetings step more than 1 cm, worst {steps.worst * 100f:0.0} cm ({steps.between})",
                    steps.joins > 300 && steps.steps == 0);

            // ---- 4. PAINT OVER WHAT IT IS PAINTED ON: a crossing laid at one height (half under the asphalt on a slope),
            // the apron under it coming up through the bars, lines painted on the curve under a flat-ringed road.
            var buried = h.Grid.PaintUnderPaving(h.Host, h.Roads);
            int buriedN = 0; float buriedWorst = float.NegativeInfinity;
            foreach (var b in buried) { buriedN += b.buried; buriedWorst = Mathf.Max(buriedWorst, b.worst); }
            T.Check($"no paint under the paving it is painted on ({buriedN} vertices, worst {buriedWorst * 100f:0.0} cm under)", buried.Count > 0 && buriedN == 0);
            var kinds = new Dictionary<string, (int thin, float least, Vector3 at)>();
            h.Grid.PaintClearance(h.Host, h.Roads, kinds);
            bool hasCrossing = kinds.TryGetValue("Crossing", out var cc);
            // (a few centimetres survive where a crossing runs over a slab bent to meet a main it runs beside -- tiny's
            // slab is lumpy at that scale; the 34 cm of a crossing laid at one height is what this holds off)
            T.Check($"...and no crossing paint more than a few cm under anything over it (thinnest {(hasCrossing ? cc.least * 100f : float.NaN):0.00} cm)",
                    hasCrossing && cc.least > -0.05f);

            // ---- 5. LANE MOUTHS: the lane's end is level from side to side and the street's edge it ends on climbs, so its
            // end ring takes the street's slope.
            float lip = h.Grid.MouthLip;
            T.Check($"a lane's end meets its street's asphalt with no lip ({lip * 100f:0.0} cm at the worst mouth)", h.Grid.LaneRoads.Count > 5 && lip < 0.02f);

            // ---- 6. THE CORNER FILL STAYS UNDER THE PAVING OVER IT -- which on a hill means held there point by point
            // (FillPulledMost is how far it would have come up through) and split where its middle still would.
            T.Check($"corner fill under the paving: {h.Grid.FillPulled} points held down (by up to {h.Grid.FillPulledMost * 100f:0} cm -- what came up through), the worst point inside its triangles {h.Grid.FillThroughMost * 100f:0.0} cm",
                    h.Grid.FillPulledMost > 0.2f && h.Grid.FillThroughMost < 0.03f);
            T.Check($"...and a slab a main runs through meets that main at its height (its cut edge moved by up to {h.Grid.ClipSnapMost * 100f:0} cm)",
                    h.Grid.ClipSnapped > 0 && h.Grid.ClipSnapMost > 0.1f);

            // ================== CONTROLS: each probe above has to see its defect ==================
            // 1. the grading's old reach, on the same hills: the ground came up through the mains' last metre and a half
            var old = Build(5f, oldCover: true);
            yield return Ticks(2);
            var gpOld = old.Grid.GroundPokes(old.Roads, (x, z) => old.Terr.SampleHeight(x, z));
            T.Check($"control: graded with the old 9.2 m ceiling the ground comes through ({gpOld.pokes} points, worst {gpOld.worst * 100f:0.0} cm)",
                    gpOld.pokes > 0 && gpOld.worst > 0.05f);

            // 2. a corner sunk a metre is seen as buried
            var corner = FirstMesh(h.Host, "Sidewalk_Corner");
            if (corner != null)
            {
                corner.Position += new Vector3(0f, -1f, 0f);
                var sunk = TownRoadGrid.GroundOverMeshes(h.Host, ground).Find(o => o.kind == "Sidewalk_Corner");
                corner.Position += new Vector3(0f, 1f, 0f);
                T.Check($"control: a corner sunk a metre reads as buried ({sunk.pokes} vertices)", sunk.pokes > 0);
            }
            else T.Check("control: there is a corner to sink", false);

            // 3. a pavement lifted 5 cm is seen as a step where it meets the next
            var street = FirstMesh(h.Host, "Sidewalk_R");
            if (street != null)
            {
                street.Position += new Vector3(0f, 0.05f, 0f);
                var lifted = TownRoadGrid.PavementSteps(h.Host);
                street.Position -= new Vector3(0f, 0.05f, 0f);
                T.Check($"control: a pavement lifted 5 cm steps where it meets the next ({lifted.steps} meetings, worst {lifted.worst * 100f:0.0} cm)",
                        lifted.steps > 0 && lifted.worst > 0.04f);
            }
            else T.Check("control: there is a street pavement to lift", false);

            // 4. a crossing sunk 3 cm is seen as paint under the paving
            var crossing = FirstMesh(h.Host, "Crossing_");
            if (crossing != null)
            {
                crossing.Position += new Vector3(0f, -0.03f, 0f);
                int sunkPaint = 0;
                foreach (var b in h.Grid.PaintUnderPaving(h.Host, h.Roads)) sunkPaint += b.buried;
                crossing.Position -= new Vector3(0f, -0.03f, 0f);
                T.Check($"control: a crossing sunk 3 cm reads as paint under the asphalt ({sunkPaint} vertices)", sunkPaint > 0);
            }
            else T.Check("control: there is a crossing to sink", false);

            // 5. the lanes rebuilt level-ended stand proud of their streets at the mouth
            TownRoadGrid.NoLaneBank = true;
            try
            {
                var host2 = new Node3D(); World.AddChild(host2);
                h.Grid.BuildLanes(host2, h.Roads);
                T.Check($"control: lanes built with level ends stand {h.Grid.MouthLip * 100f:0.0} cm proud of their street at a mouth", h.Grid.MouthLip > 0.05f);
            }
            finally { TownRoadGrid.NoLaneBank = laneBankBefore; }

            Terrain.Active = activeBefore;
        }
    }
}
