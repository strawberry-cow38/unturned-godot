using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The town road grid (strawberry 2026-10-11: "work on making a proper road grid system with ur
    /// new splines").
    ///
    /// ⭐ THE CLAIMS UNDER TEST ARE THE ONES THAT CANNOT BE SEEN IN A RENDER. A picture of a town shows
    /// streets; it cannot show that the same seed gives the same town, that the streets are JOINED rather
    /// than merely crossing, or that a street stopped at the water's edge because it was told to rather than
    /// because the map happened to run out. Each gate below therefore gets a CONTROL that must fail -- a gate
    /// test on terrain the gate never trips is a check that passes whether the gate exists or not.</summary>
    public sealed class TownRoadGridTests : GameTest
    {
        public override string Name => "editor.town_road_grid";
        public override double TimeoutSimSeconds => 60;

        // A RoadField with materials but no terrain: Terr stays null, so the joints keep the Y the generator
        // gave them and a height assertion measures the GENERATOR rather than the heightmap.
        RoadField Field()
        {
            var rf = new RoadField();
            World.AddChild(rf);
            rf.DebugSetMaterialWidth(0, 9.2f);    // Highway_0 -- main street, the generator's default
            rf.DebugSetMaterialWidth(1, 6.9f);    // Highway_1 -- side street
            // ⚠ The rig must give a width to the materials the GENERATOR actually uses. A material with no
            // width reads back 0, LayAlongRoad bails on `half <= 0`, and the pavement checks below would be
            // measuring nothing at all.
            return rf;
        }

        static TownRoadGrid Grid(int seed = 7) => new TownRoadGrid
        {
            Seed = seed, Centre = Vector3.Zero, Extent = 200f, Spacing = 64f,
            Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f,
        };

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);
            Func<float, float, float> flat = (x, z) => 40f;

            // ---- 1. IT BUILDS A GRID, AND THE GRID IS JOINED.
            var rfA = Field();
            var repA = Grid().Generate(rfA, flat);
            T.Check($"a grid builds ({repA})", repA.Roads > 0);
            T.Check($"...with interior crossroads, not just touching ends ({repA.Crossroads} four-way)", repA.Crossroads > 0);
            T.Check($"...and RoadField agrees they are joined ({rfA.Junctions().Count} nodes with 2+ roads)",
                    rfA.Junctions().Count >= repA.Crossroads);

            // ⭐ JOINED MEANS COINCIDENT. A junction whose roads end a metre apart still counts as "bound":
            // the binding is bookkeeping, the geometry is the claim. Measure the worst gap.
            float worst = 0f;
            foreach (var (pos, ends) in rfA.Junctions())
                foreach (var (road, atEnd) in ends)
                {
                    int jn = rfA.JointCount(road);
                    if (jn < 2) continue;
                    if (!rfA.EvaluateAlong(road, atEnd ? rfA.RoadLength(road) : 0f, out var p, out _, snapTerrain: false)) continue;
                    worst = Mathf.Max(worst, new Vector2(p.X - pos.X, p.Z - pos.Z).Length());
                }
            T.Check($"every bound road end sits ON its junction node (worst {worst:0.000} m)", worst < 0.05f);

            // ---- 2. DETERMINISM. ⚠ THE REASON THIS TEST EXISTS: the editor roads harness re-rolls its roads
            // every launch, which was only noticed after it wrecked an A/B. A generated town has to be
            // reproducible or nothing may ever be built on it.
            var rfB = Field();
            var repB = Grid().Generate(rfB, flat);
            T.Check($"the same seed gives the same town (checksum {repA.Checksum:X16} vs {repB.Checksum:X16})",
                    repA.Checksum == repB.Checksum && repA.Roads == repB.Roads && repA.Junctions == repB.Junctions);
            // ...and the control: the checksum must be a function OF the layout, not a constant.
            var rfC = Field();
            var repC = Grid(8).Generate(rfC, flat);
            T.Check($"...and a different seed gives a different one ({repC.Checksum:X16})", repC.Checksum != repA.Checksum);

            // ---- 3. THE WATER GATE: a shallow ford that two dry nodes sit either side of.
            // ⚠⚠ GETTING THIS SHAPE RIGHT TOOK TWO MUTATIONS, and both earlier shapes passed with the gate
            // REMOVED -- which is the only reason they were caught:
            //   - a COASTLINE (`x > 0 ? dry : wet`): every wet node is refused by the NODE gate, so no span
            //     ever reaches the water and the span gate was never exercised.
            //   - a steep-sided CHANNEL: the near-vertical banks are refused by the SPAN GRADE gate first,
            //     so again nothing reached the water gate.
            // What reaches it is a dip SHALLOW enough to drive into (0.156 < MaxGrade) and NARROWER than the
            // node spacing, so the nodes either side are dry and the road between them is not.
            // ⚠ PointStep bounds the gate's resolution -- it samples, it does not integrate -- so the test
            // tightens it to 4 m rather than pretending a 12 m stride can see a 13 m ford.
            Func<float, float, float> ford = (x, z) => 6f - 5f * Mathf.Max(0f, 1f - Mathf.Abs(x - 100f) / 32f);
            var rfW = Field();
            var gw = Grid(); gw.PointStep = 4f; gw.Generate(rfW, ford);
            float gate = gw.SeaLevel + gw.SeaMargin;
            float lowest = float.MaxValue; int pts = 0;
            for (int r = 0; r < rfW.RoadCount; r++)
            {
                float L = rfW.RoadLength(r);
                for (int k = 0; k <= 20; k++)
                    if (rfW.EvaluateAlong(r, L * k / 20f, out var p, out _, snapTerrain: false)) { lowest = Mathf.Min(lowest, p.Y); pts++; }
            }
            T.Check($"no street is laid through the water (lowest {lowest:0.00} m over {pts} samples, gate at {gate:0.0})",
                    pts > 0 && lowest > gate);
            // CONTROL: drop the gate to the sea floor and the SAME ford must now be driven through, or the
            // check above is passing on ground that never reached the gate.
            var rfW2 = Field();
            var gw2 = Grid(); gw2.PointStep = 4f; gw2.SeaLevel = -100f; gw2.Generate(rfW2, ford);
            float lowest2 = float.MaxValue;
            for (int r = 0; r < rfW2.RoadCount; r++)
            {
                float L2 = rfW2.RoadLength(r);
                for (int k = 0; k <= 20; k++)
                    if (rfW2.EvaluateAlong(r, L2 * k / 20f, out var p, out _, snapTerrain: false)) lowest2 = Mathf.Min(lowest2, p.Y);
            }
            T.Check($"...and with the gate lowered the ford IS driven through ({lowest2:0.00} m, below the {gate:0.0} gate)",
                    lowest2 < gate);
            T.Check($"...so the ford really was in the way ({rfW.RoadCount} streets with the gate, {rfW2.RoadCount} without)",
                    rfW2.RoadCount > rfW.RoadCount);

            // ---- 4. THE GRADE GATE, on a constant ramp too steep for a street.
            // ⚠ ISOLATE THE GATE UNDER TEST. The first version of this left MaxNodeSlope at its default and
            // built NOTHING on a 0.30 ramp -- every node was refused before a span was ever considered, so
            // "no street exceeds its grade" was true of a town with no streets. Opening the node gate here
            // means the only thing that can reject a span is the span grade, which is the claim.
            Func<float, float, float> ramp = (x, z) => 60f + x * 0.30f;
            var rfG = Field();
            var gg = Grid(); gg.MaxGrade = 0.16f; gg.MaxNodeSlope = 1f; var repG = gg.Generate(rfG, ramp);
            float steepest = 0f;
            for (int r = 0; r < rfG.RoadCount; r++)
            {
                float L = rfG.RoadLength(r);
                if (!rfG.EvaluateAlong(r, 0f, out var p0, out _, snapTerrain: false)) continue;
                if (!rfG.EvaluateAlong(r, L, out var p1, out _, snapTerrain: false)) continue;
                float run = new Vector2(p1.X - p0.X, p1.Z - p0.Z).Length();
                if (run > 1f) steepest = Mathf.Max(steepest, Mathf.Abs(p1.Y - p0.Y) / run);
            }
            T.Check($"no street exceeds its grade on a 0.30 ramp (steepest {steepest:0.000} vs cap {gg.MaxGrade:0.00}, {repG.Roads} built)",
                    repG.Roads > 0 && steepest <= gg.MaxGrade + 0.001f);
            // CONTROL: lift the cap and the cross-slope streets must come back steeper than 0.16, proving the
            // ramp really is steep where the gate was refusing to build.
            var rfG2 = Field();
            var gg2 = Grid(); gg2.MaxGrade = 2f; gg2.MaxNodeSlope = 1f; gg2.Generate(rfG2, ramp);
            float steepest2 = 0f;
            for (int r = 0; r < rfG2.RoadCount; r++)
            {
                float L = rfG2.RoadLength(r);
                if (!rfG2.EvaluateAlong(r, 0f, out var p0, out _, snapTerrain: false)) continue;
                if (!rfG2.EvaluateAlong(r, L, out var p1, out _, snapTerrain: false)) continue;
                float run = new Vector2(p1.X - p0.X, p1.Z - p0.Z).Length();
                if (run > 1f) steepest2 = Mathf.Max(steepest2, Mathf.Abs(p1.Y - p0.Y) / run);
            }
            T.Check($"...and with the cap lifted the steep ones ARE built ({steepest2:0.000})", steepest2 > 0.16f);

            // ---- 5. THE SIDEWALK TRIM, which is what stops four ribbons meeting in the middle of a crossroads.
            var rfS = Field();
            Grid().Generate(rfS, flat);
            int road0 = -1;
            for (int r = 0; r < rfS.RoadCount; r++)
                if (rfS.RoadEndJunction(r, false) >= 0 && rfS.RoadEndJunction(r, true) >= 0) { road0 = r; break; }
            T.Check("a street bound at both ends exists to trim", road0 >= 0);
            if (road0 >= 0)
            {
                var walk = new EditorSidewalk(); World.AddChild(walk);
                var untrimmed = new List<Node3D>(); var trimmed = new List<Node3D>();
                walk.LayAlongRoad(walk, rfS, road0, EditorSidewalk.Side.Right, untrimmed);
                walk.LayAlongRoad(walk, rfS, road0, EditorSidewalk.Side.Right, trimmed, 10f, 10f);
                T.Check($"both lay ({untrimmed.Count} / {trimmed.Count})", untrimmed.Count == 1 && trimmed.Count == 1);
                if (untrimmed.Count == 1 && trimmed.Count == 1)
                {
                    float lu = ((MeshInstance3D)untrimmed[0]).Mesh.GetAabb().Size.Length();
                    float lt = ((MeshInstance3D)trimmed[0]).Mesh.GetAabb().Size.Length();
                    T.Check($"the trim shortens the ribbon ({lt:0.0} vs {lu:0.0})", lt < lu - 10f);
                }
                // ...and a span that is ALL intersection gets no pavement rather than a sliver inside it.
                var none = new List<Node3D>();
                walk.LayAlongRoad(walk, rfS, road0, EditorSidewalk.Side.Right, none, 1e4f, 1e4f);
                T.Check("a span shorter than its own trims is paved not at all", none.Count == 0);
            }
            yield break;
        }
    }
}
