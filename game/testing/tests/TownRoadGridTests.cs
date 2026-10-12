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
            rf.DebugSetMaterialWidth(0, 9.2f);    // Highway_0 -- the four-lane main street
            rf.DebugSetMaterialWidth(1, 6.9f);    // Highway_1
            rf.DebugSetMaterialWidth(6, 4.6f);    // White_0 -- the two-lane side street, the grid's default
            // ⚠ EVERY MATERIAL THE GENERATOR MIGHT USE. A material with no width reads back 0, which makes a
            // zero-width road: no setback at a junction, no pavement, nothing. The defaults changed to a
            // two-class town (0 and 6) and this rig still only knew 0 and 1 -- the test caught it as "every
            // arm stops short of its node: nearest 0.0 m back", which is precisely what it is for.
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

            // ⭐ THE CORNERS ARE ROUNDED, NOT SLABBED (strawberry 2026-10-12: "work on 45° intersections. as well
            // as curved corner pieces"). A two-arm node used to be paved as a junction and came out a sharp
            // point; now the two spans are fused into one road on an arc. Three things have to be true, and
            // each is checked on its own because each fails differently:
            //   - it HAPPENED (a count of rounded corners, and no two-arm junction left over);
            //   - the fused road really TURNS, about a right angle end to end -- a fusion that kept the
            //     straight and dropped the arc would pass the count;
            //   - it turns SMOOTHLY: no jump in heading between samples 2 m apart. An arc stitched badly to
            //     its straights shows here as a kink, and uneven spacing as Catmull-Rom wiggle.
            T.Check($"the town's corners are rounded into curves ({repA.Bends} rounded)", repA.Bends > 0);
            T.Check($"...and none is left slabbed as a two-arm junction ({repA.Corners} corner junctions)", repA.Corners == 0);
            {
                float bestTurn = 0f, worstStep = 0f;
                for (int r = 0; r < rfA.RoadCount; r++)
                {
                    float L = rfA.RoadLength(r);
                    if (L < 8f) continue;
                    Vector3 Dir(float d) { rfA.EvaluateAlong(r, d, out _, out var tg, snapTerrain: false); return new Vector3(tg.X, 0f, tg.Z).Normalized(); }
                    var first = Dir(1f); var last = Dir(L - 1f);
                    bestTurn = Mathf.Max(bestTurn, Mathf.RadToDeg(first.AngleTo(last)));
                    var prev = first;
                    for (float d = 3f; d < L - 1f; d += 2f)
                    {
                        var cur = Dir(d);
                        worstStep = Mathf.Max(worstStep, Mathf.RadToDeg(prev.AngleTo(cur)));
                        prev = cur;
                    }
                }
                T.Check($"...a rounded corner really turns (largest end-to-end turn on one road {bestTurn:0.#}deg)",
                        bestTurn > 80f && bestTurn < 100f);
                T.Check($"...and turns smoothly, no kink or wiggle (largest heading change over 2 m: {worstStep:0.#}deg)",
                        worstStep < 15f);
            }
            // ⚠⚠ AND THE BUILT ROAD FOLLOWS THE CIRCLE IT WAS GIVEN. The two checks above passed while the
            // built curves strayed 33 cm off the true arc and turned the WRONG way at both joins -- master saw
            // it ("are the curves overshooting and correcting as they connect lol?"). A cap on the heading
            // change per 2 m catches a KINK; an overshoot is small at every step and only shows as a
            // DEVIATION from the intended path and a turn AGAINST the bend. So those are what is held here,
            // measured by the generator's own probe on the roads it actually built.
            {
                var gF = Grid();
                gF.Generate(Field(), flat);
                T.Check($"...a rounded corner is built ON its circle, not overshooting it (worst stray {gF.BendWorstOffCm:0.0} cm)",
                        gF.BendWorstOffCm < 2f);
                T.Check($"...and never turns against the bend at the joins beyond spline noise (worst {gF.BendWorstReverseDegPerM:0.00} deg/m)",
                        gF.BendWorstReverseDegPerM < 0.5f);
            }
            // ⭐ THE CONTROL: the same town with rounding off must come back with NO bends and its corners
            // slabbed -- otherwise "rounded" above could be counting something that was always there.
            {
                var rfSharp = Field();
                var gSharp = Grid(); gSharp.CurveCorners = false;
                var repSharp = gSharp.Generate(rfSharp, flat);
                T.Check($"control: with rounding off there are no bends and the corners are junctions again ({repSharp.Bends} bends, {repSharp.Corners} corners)",
                        repSharp.Bends == 0 && repSharp.Corners > 0);
            }

            // ⭐ JOINED, BUT NOT OVERLAPPING (strawberry 2026-10-11: "use tiny's road-merging mechanic instead
            // of overlapping roads").
            //
            // ⚠ THIS CHECK HAS NOW BEEN WRONG THREE TIMES, AND EVERY TIME IT WAS THE TEST DESCRIBING THE
            // OLD IMPLEMENTATION. First it asserted every bound end sat exactly ON its node, true of
            // overlapping ribbons. Then that a "through" street reached the node while the others stopped
            // short, true of the hand-rolled dominant-axis setback. Then that EVERY arm is cut back and one
            // patch paves the middle, true of tiny's node geometry. Now the priority road runs through
            // uncut again (strawberry 2026-10-12: "couldnt we just have the main road spline continue
            // through this intersection instead?"), so the shape is a THROUGH PAIR plus CUT ARMS.
            // ⭐ SO ASSERT BOTH HALVES, AND MEASURE THE CUT AGAINST WHAT IT HAS TO CLEAR rather than
            // against a constant. A cut arm must stop at least the widest carriageway at that node back,
            // because that is the road it would otherwise be standing in -- a fixed 9.2 m only happened to
            // hold while every junction had a four-lane arm, and would pass a side-street node that cut
            // nothing. Written this way the check still fails the thing it is for (an arm reaching its
            // node) and now also fails the new way it can break (two pairs left uncut, which is two
            // coplanar ribbons overlapping at right angles).
            float leastCut = float.MaxValue; int ends = 0, through = 0, nodesThrough = 0, twoPairs = 0, tooClose = 0;
            int streetsThrough = 0; int mainMat = Grid().MainMaterial;
            foreach (var (pos, es) in rfA.Junctions())
            {
                float widest = 0f;
                foreach (var (r2, _) in es) widest = Mathf.Max(widest, rfA.RoadHalfWidth(r2));
                int uncutHere = 0;
                foreach (var (road, atEnd) in es)
                {
                    if (!rfA.EvaluateAlong(road, atEnd ? rfA.RoadLength(road) : 0f, out var p, out _, snapTerrain: false)) continue;
                    float d = new Vector2(p.X - pos.X, p.Z - pos.Z).Length();
                    ends++;
                    if (d < 0.5f)
                    {
                        through++; uncutHere++;
                        if (rfA.RoadMaterialOf(road) != mainMat) streetsThrough++;
                        continue;
                    }
                    leastCut = Mathf.Min(leastCut, d);
                    if (d + 0.01f < widest) tooClose++;
                }
                if (uncutHere > 0) nodesThrough++;
                if (uncutHere > 2) twoPairs++;
            }
            T.Check($"the priority road runs through its node ({through} uncut arms over {nodesThrough} junctions)",
                    through > 0 && nodesThrough > 0);
            T.Check($"...one pair per node, never two ({twoPairs} nodes left 3+ arms uncut)", twoPairs == 0);
            // ⚠⚠ AND ONLY EVER A MAIN. A through ribbon carries its own baked lines across the node -- right
            // for a main, wrong for a street, whose crossings it would then paint a centre line through. That
            // shipped once (the rule was widened to any collinear pair to raise a coverage number) and no
            // check here could see it, because every one of them was about GEOMETRY and this is about WHICH
            // ROAD. master's spec: "their lines continue through the junction without crossings on their parts".
            T.Check($"...and the road that runs through is always a MAIN, never a street ({streetsThrough} street arms left uncut)",
                    streetsThrough == 0);
            T.Check($"every other arm stops clear of the widest road it meets ({ends - through} cut ends, "
                  + $"{tooClose} too close, nearest {leastCut:0.0} m back)",
                    ends - through > 0 && tooClose == 0);
            T.Check($"...and every node it cut is paved ({repA.Patched} patches for {repA.Junctions} junctions)",
                    repA.Patched > 0 && repA.Patched >= repA.Crossroads);
            // ⚠ AND THE GRAPH SURVIVED THE CUTS. An arm that stops short must still be BOUND, or the town
            // looks right and routes as a pile of disconnected stubs.
            int bound = 0;
            for (int r = 0; r < rfA.RoadCount; r++)
                if (rfA.RoadEndJunction(r, false) >= 0 && rfA.RoadEndJunction(r, true) >= 0) bound++;
            T.Check($"every street is still bound at both ends ({bound} of {rfA.RoadCount})", bound == rfA.RoadCount);

            // THE FALLBACK still has to work: where tiny's geometry declines a node, the grid keeps the plain
            // kerb-line setback rather than leaving arms crossing each other. Exercised explicitly, because in
            // a healthy town it never runs and would rot unnoticed.
            var rfF = Field();
            var gf = Grid(); gf.UseNodePatches = false;
            var repF = gf.Generate(rfF, flat);
            T.Check($"the no-patch fallback still builds a joined town ({repF.Roads} roads, {repF.Junctions} junctions, {repF.Patched} patches)",
                    repF.Roads > 0 && repF.Junctions > 0 && repF.Patched == 0);
            float fallbackWorst = 0f;
            foreach (var (pos, es) in rfF.Junctions())
                foreach (var (road, atEnd) in es)
                    if (rfF.EvaluateAlong(road, atEnd ? rfF.RoadLength(road) : 0f, out var p, out _, snapTerrain: false))
                        fallbackWorst = Mathf.Max(fallbackWorst, new Vector2(p.X - pos.X, p.Z - pos.Z).Length());
            T.Check($"...with its arms held off the centreline too ({fallbackWorst:0.0} m)", fallbackWorst > 1f);

            // ---- 1b. TINY'S NODE GEOMETRY, through the new facade (strawberry: "use tiny's road-merging
            // mechanic instead of overlapping roads"). Four arms meeting at the origin -- a plain crossroads --
            // must come back with a cut for each arm and a patch to pave the middle.
            // ⚠ FRAME: InfiniteRoads' z is the NEGATED world z, so the arms are built in that frame here. A
            // mirror swaps which side is counter-clockwise, and the fillets are built on that ordering.
            {
                var spokes = new List<SDG.Unturned.InfiniteRoads.TownArm>();
                foreach (var (ax, az) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
                {
                    int m = 25;
                    var xs = new double[m]; var zs = new double[m]; var hs = new float[m];
                    for (int k = 0; k < m; k++) { xs[k] = ax * k * 8.0; zs[k] = az * k * 8.0; hs[k] = 30f; }
                    spokes.Add(new SDG.Unturned.InfiniteRoads.TownArm(xs, zs, hs, atEnd: false, halfWidth: 9.2));
                }
                bool got = SDG.Unturned.InfiniteRoads.TownNode(spokes, 0, 0, out var trims, out var patch);
                T.Check("tiny's node geometry accepts a four-arm crossroads", got && trims != null && patch != null);
                if (got)
                {
                    T.Check($"...a cut for every arm ({trims.Length})", trims.Length == 4);
                    float worstCut = 0f; foreach (var t in trims) worstCut = Mathf.Max(worstCut, (float)t);
                    T.Check($"...each arm cut back past its fillet, not to the centre (worst {worstCut:0.0} m)",
                            worstCut > 9.2f);
                    T.Check($"...and a patch with triangles to pave the middle ({patch.Tris.Length / 3} tris, {patch.X.Length} verts)",
                            patch.Tris.Length >= 3 && patch.X.Length >= 3);
                    // The patch has to actually COVER the hole the cuts leave, or the junction is a gap.
                    T.Check($"...covering the node itself (distance {patch.DistanceTo(0, 0):0.00} m)",
                            patch.DistanceTo(0, 0) < 0.01);
                }
            }

            // ---- 1c. THE CORNERS TURN THE RIGHT WAY (strawberry 2026-10-12: "your corners go the opposite
            // way, they go outside, should go inside").
            //
            // ⚠ COUNTING THEM PROVES NOTHING -- the inside-out version built exactly as many. The shape is
            // the claim: a street corner is the BLOCK's corner rounded off, so its centre of curvature sits
            // in the block and the kerb bulges TOWARD the junction. An arc centred on the node instead keeps
            // every point the same distance from it, which is the bug, and this check is what separates them.
            {
                var cornerWalk = new EditorSidewalk(); World.AddChild(cornerWalk);
                int cn = Grid().LaySidewalkCornersOn(rfA, World, cornerWalk, out var shapes);
                T.Check($"corners are laid ({cn})", cn > 0 && shapes.Count == cn);
                int inward = 0, outward2 = 0;
                foreach (var (cnode, cpts) in shapes)
                {
                    if (cpts.Count < 3) continue;
                    float D(Vector3 q) => new Vector2(q.X - cnode.X, q.Z - cnode.Z).Length();
                    float mid = D(cpts[cpts.Count / 2]), tips = Mathf.Min(D(cpts[0]), D(cpts[^1]));
                    if (mid < tips - 0.5f) inward++; else outward2++;
                }
                T.Check($"...and every one bulges TOWARD its junction ({inward} inward, {outward2} not)",
                        inward > 0 && outward2 == 0);
            }

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
