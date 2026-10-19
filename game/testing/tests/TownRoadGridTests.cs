using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>A road end's direction leaving its node, flat.</summary>
        static Vector3 ArmOut(RoadField rf, int road, bool atEnd)
        {
            rf.EvaluateAlong(road, atEnd ? rf.RoadLength(road) : 0f, out _, out var tg, snapTerrain: false);
            var t = new Vector3(tg.X, 0f, tg.Z).Normalized();
            return atEnd ? -t : t;
        }

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

            // ⭐ 45-DEGREE INTERSECTIONS (strawberry 2026-10-12: "work on 45° intersections"). A diagonal avenue on a
            // grid big enough for a 45-degree arm to fit (64 m blocks are too short: a crossing alone sits ~30 m
            // out), and the kerb-distance rule held to the geometry it claims -- both at 45, where it must move,
            // and at 90, where it must be the old number BIT FOR BIT, since that is what kept the square town
            // master has already approved untouched.
            {
                var rfD = Field();
                // (a lane in EVERY block here, asked for or not: the lane checks below want lanes all over this town. Lanes
                // only where a building asks are held on a town of their own, after the plots)
                var gD = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                            Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true, AlleysOnRequest = false };
                var repD = gD.Generate(rfD, flat);
                T.Check($"the diagonal crosses the grid ({repD.DiagonalCrossings} crossings)", repD.DiagonalCrossings >= 4);
                int n45 = 0, n90 = 0; float worst45 = 0f; bool exact90 = true;
                for (int j = 0; j < rfD.JunctionCount; j++)
                {
                    var es = rfD.JunctionEdges(j);
                    if (es.Count < 3) continue;
                    foreach (var (road, atEnd) in es)
                    {
                        float th = TownRoadGrid.SharpestCorner(rfD, j, road, atEnd);
                        float hw = 0f;
                        foreach (var (o, _) in es) if (o != road) hw = Mathf.Max(hw, rfD.RoadHalfWidth(o));
                        const float R = 6f;
                        float got = gD.KerbCurveEnd(rfD, j, road, atEnd, hw, R);
                        float deg = Mathf.RadToDeg(th);
                        if (Mathf.Abs(deg - 45f) < 2f)
                        {
                            n45++;
                            float ha = rfD.RoadHalfWidth(road);
                            float want = (hw + ha * Mathf.Cos(th)) / Mathf.Sin(th) + R / Mathf.Tan(th * 0.5f);
                            worst45 = Mathf.Max(worst45, Mathf.Abs(got - want));
                        }
                        else if (Mathf.Abs(deg - 90f) < 0.5f) { n90++; if (got != hw + R) exact90 = false; }
                    }
                }
                T.Check($"...making real 45-degree arms ({n45} of them)", n45 >= 4);
                T.Check($"...whose kerb runs out where the corner geometry says (worst {worst45 * 100f:0.0} cm off the formula)",
                        n45 > 0 && worst45 < 0.01f);
                T.Check($"...and at a right angle the rule is the old one bit for bit ({n90} square arms)", n90 > 0 && exact90);

                // ⭐ A 45-DEGREE JOIN'S CROSSING LIES PARALLEL TO THE ROAD IT JOINS (strawberry 2026-10-12: "the 45°
                // joins should have the crosswalk parallel to the main road's direction"), CLOSE to it ("the
                // crossing + stop line + traffic light/stop sign on angled joins should be way closer to the road
                // its joining"), and still off the kerb's curve on BOTH sides.
                // ⚠⚠ HELD TO THE KERB THAT IS BUILT, NOT TO A FORMULA FOR IT. The first version of this check
                // measured against a 6 m fillet -- the same model the placement used -- and so passed a crossing
                // standing ten metres up the arm from the 4.8 m curve LaySidewalkCorners actually lays at 45
                // degrees. A probe that shares the bug's model cannot see the bug.
                var uAx = new Vector3(Mathf.Cos(0.3f), 0f, Mathf.Sin(0.3f)); var vAx = new Vector3(-uAx.Z, 0f, uAx.X);
                var walkD = new EditorSidewalk(); World.AddChild(walkD);
                gD.LaySidewalkCornersOn(rfD, World, walkD, out var kerbs);
                // where the BUILT kerb stops turning on side s of an arm: the nearest point, on that kerb line,
                // of a corner piece that curves (a straight through-piece has nothing to stop)
                float CurveEnd(TownRoadGrid.CrossingLayout c, int s)
                {
                    float best = float.NaN, off = c.Half + walkD.RoadGap;
                    var ow = -new Vector3(c.Fwd.X, 0f, c.Fwd.Z).Normalized();
                    foreach (var (kn, kp) in kerbs)
                    {
                        if (new Vector2(kn.X - c.Node.X, kn.Z - c.Node.Z).Length() > 0.5f) continue;
                        bool curved = false; float nearOn = float.MaxValue;
                        foreach (var q in kp)
                        {
                            var rel = new Vector3(q.X - c.Node.X, 0f, q.Z - c.Node.Z);
                            float al = rel.Dot(ow), la = rel.Dot(c.Right);
                            if (Mathf.Abs(la - s * off) < 0.002f && al > 0f) nearOn = Mathf.Min(nearOn, al);
                            else curved = true;
                        }
                        if (curved && nearOn < float.MaxValue) best = float.IsNaN(best) ? nearOn : Mathf.Max(best, nearOn);
                    }
                    return best;
                }
                // how far the BUILT kerb on side s has turned away from its straight line, `al` metres out along the arm
                // (0 where it runs straight, or where no corner piece reaches)
                float KerbTurn(TownRoadGrid.CrossingLayout c, int s, float al)
                {
                    float off = c.Half + walkD.RoadGap, best = float.MaxValue;
                    var ow = -new Vector3(c.Fwd.X, 0f, c.Fwd.Z).Normalized();
                    foreach (var (kn, kp) in kerbs)
                    {
                        if (new Vector2(kn.X - c.Node.X, kn.Z - c.Node.Z).Length() > 0.5f) continue;
                        for (int k = 0; k + 1 < kp.Count; k++)
                        {
                            var r0 = new Vector3(kp[k].X - c.Node.X, 0f, kp[k].Z - c.Node.Z); var r1 = new Vector3(kp[k + 1].X - c.Node.X, 0f, kp[k + 1].Z - c.Node.Z);
                            float a0 = r0.Dot(ow), a1 = r1.Dot(ow), l0 = r0.Dot(c.Right), l1 = r1.Dot(c.Right);
                            if ((al - a0) * (al - a1) > 0f || Mathf.Abs(a1 - a0) < 1e-5f) continue;
                            float la = Mathf.Lerp(l0, l1, (al - a0) / (a1 - a0));
                            if (Mathf.Abs(la - s * off) < 4f) best = Mathf.Min(best, Mathf.Abs(la - s * off));
                        }
                    }
                    return best == float.MaxValue ? 0f : best;
                }
                float R2 = gD.CornerKerbRadius, inset = gD.CrossingInset;
                int joining = 0, skewed = 0, gridSquare = 0, gridSkewed = 0, sidesHeld = 0, masts = 0, mastsOff = 0;
                float worstPar = 0f, worstTurn = 0f, controlTurn = 0f, furthest = 0f, worstGridOver = float.MinValue, mastWas = 0f, mastNow = 0f;
                for (int j = 0; j < rfD.JunctionCount; j++)
                {
                    var es = rfD.JunctionEdges(j);
                    if (es.Count < 3) continue;
                    foreach (var (road, atEnd) in es)
                    {
                        if (!gD.LayCrossing(rfD, j, road, atEnd, out var cl)) continue;
                        var outw = -new Vector3(cl.Fwd.X, 0f, cl.Fwd.Z).Normalized();
                        // the road this arm joins: every other arm that is not its own continuation
                        var joins = new List<Vector3>(); float hb = 0f;
                        foreach (var (o, oe) in es)
                        {
                            if (o == road && oe == atEnd) continue;
                            var od = ArmOut(rfD, o, oe);
                            if (Mathf.Abs(od.Dot(outw)) > 0.95f) continue;
                            joins.Add(od); hb = Mathf.Max(hb, rfD.RoadHalfWidth(o));
                        }
                        if (joins.Count == 0) continue;
                        float acute = Mathf.Acos(Mathf.Clamp(Mathf.Abs(joins[0].Dot(outw)), 0f, 1f));
                        if (Mathf.Abs(Mathf.RadToDeg(acute) - 45f) > 2f) continue;
                        // where it starts, the BUILT kerb either side has barely begun to turn: the crossing sits just
                        // inside the curve (strawberry 2026-10-13: "bring the crossings (and stop lines) closer to the
                        // intersection"), not out past it, but where its kerbs still meet it square
                        for (int s = -1; s <= 1; s += 2)
                        {
                            if (float.IsNaN(CurveEnd(cl, s))) continue;    // a tee's straight side: no curve there
                            var k = cl.At(cl.D0, s * cl.Half) - cl.Node; k.Y = 0f;
                            worstTurn = Mathf.Max(worstTurn, KerbTurn(cl, s, k.Dot(outw)));
                            controlTurn = Mathf.Max(controlTurn, KerbTurn(cl, s, k.Dot(outw) - 3f));
                            sidesHeld++;
                        }
                        // the mast (or sign) for this approach stands at its crossing, and nearer than it did
                        float widest = 0f;
                        foreach (var (o, _) in es) widest = Mathf.Max(widest, rfD.RoadHalfWidth(o));
                        float mlat = cl.Half + gD.PavementGap + 0.6f;
                        float back = gD.SignalBack(rfD, j, road, atEnd, widest, mlat);
                        float was = gD.KerbCurveEnd(rfD, j, road, atEnd, widest, R2) + gD.SignalBeforeCurve;
                        float edgeAtMast = cl.D0 + mlat * cl.Shear;
                        masts++; mastWas += was; mastNow += back;
                        // beside its crossing: somewhere along it at the mast's own offset, or within half a metre of it
                        float farAtMast = cl.D1 + mlat * cl.Shear;
                        if (back > was || back < edgeAtMast - 0.5f || back > farAtMast + 0.5f) mastsOff++;
                        bool onGrid = Mathf.Abs(outw.Dot(uAx)) > 0.99f || Mathf.Abs(outw.Dot(vAx)) > 0.99f;
                        if (onGrid)
                        {
                            if (cl.Shear == 0f) gridSquare++; else gridSkewed++;
                            // a square crossing on the road being joined: no further past the kerbs' corner than
                            // a right angle's is past its own (R + inset)
                            float corner = (hb + cl.Half * Mathf.Cos(acute)) / Mathf.Sin(acute);
                            worstGridOver = Mathf.Max(worstGridOver, cl.D0 - corner - (R2 + inset));
                            continue;
                        }
                        joining++;
                        if (cl.Shear != 0f) skewed++;
                        var e = cl.At(cl.D0, cl.Half) - cl.At(cl.D0, -cl.Half);
                        e.Y = 0f; e = e.Normalized();
                        worstPar = Mathf.Max(worstPar, Mathf.Abs(e.X * joins[0].Z - e.Z * joins[0].X));
                        // how far the crossing stands off the kerb of the road it joins, square to that road
                        var n0 = cl.At(cl.D0, 0f) - cl.Node; n0.Y = 0f;
                        float offKerb = Mathf.Abs(n0.X * joins[0].Z - n0.Z * joins[0].X) - hb;
                        furthest = Mathf.Max(furthest, offKerb);
                    }
                }
                T.Check($"...a road joining at 45 degrees is crossed on a slant ({skewed} of its {joining} crossings slanted)",
                        joining >= 4 && skewed == joining);
                T.Check($"...parallel to the road it joins (worst {Mathf.RadToDeg(Mathf.Asin(Mathf.Min(1f, worstPar))):0.000} deg off)",
                        joining > 0 && worstPar < 1e-3f);
                T.Check($"...no further off that road's kerb than a square junction's crossing is (furthest {furthest:0.00} m, square {R2 + inset:0.00} m)",
                        joining > 0 && furthest <= R2 + inset + 0.01f);
                T.Check($"...and every 45-degree crossing starts where its BUILT kerbs have barely begun to turn ({sidesHeld} kerbs; worst {worstTurn * 100f:0} cm off straight) -- where 3 m nearer the node they turn up to {controlTurn:0.00} m (control)",
                        sidesHeld >= 8 && worstTurn <= 0.35f && controlTurn > 0.5f);
                T.Check($"...the road it joins is crossed square ({gridSquare} square, {gridSkewed} slanted), no further past the corner than at a right angle ({worstGridOver:0.00} m over)",
                        gridSkewed == 0 && (gridSquare == 0 || worstGridOver < 0.01f));
                T.Check($"...and each approach's mast stands at its crossing, nearer than before (mean {mastNow / Mathf.Max(1, masts):0.0} m out vs {mastWas / Mathf.Max(1, masts):0.0}; {mastsOff} of {masts} misplaced)",
                        masts > 0 && mastsOff == 0 && mastNow < mastWas);
                // ⚠ AND NO SHORT ROAD KEEPS TWO CROSSINGS THAT OVERLAP. Laid parallel to two different mains, the
                // two ends' crossings on a triangle island's long side met in an X; the built town keeps one.
                // ⚠ ASKED OF A TIGHTER TOWN (70 m blocks). Pulled in to the road they join, the crossings of the
                // 110 m town above no longer crowd any link -- and a check with nothing to catch passes whether
                // the rule exists or not.
                var rfT = Field();
                var gT = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 70f,
                                            Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true };
                gT.Generate(rfT, flat);
                var marksD = new Node3D(); World.AddChild(marksD);
                gT.BuildCrossings(marksD, rfT);
                int both = 0, collide = 0, wouldCollide = 0;
                for (int r = 0; r < rfT.RoadCount; r++)
                {
                    int ja = rfT.RoadEndJunction(r, false), jb = rfT.RoadEndJunction(r, true);
                    if (ja < 0 || jb < 0) continue;
                    if (!gT.LayCrossing(rfT, ja, r, false, out var la) || !gT.LayCrossing(rfT, jb, r, true, out var lb)) continue;
                    bool hit = TownRoadGrid.CrossingsCollide(la, lb, TownRoadGrid.NodeSpan(rfT, r));
                    if (hit) wouldCollide++;
                    if (gT.CrossingBuilt(r, false) && gT.CrossingBuilt(r, true)) { both++; if (hit) collide++; }
                }
                T.Check($"...and no short road keeps two crossings that overlap ({wouldCollide} would have, {collide} still do; {both} roads crossed at both ends)",
                        wouldCollide > 0 && both > 0 && collide == 0);
                marksD.QueueFree();

                // ⭐ BUILDING PLOTS (strawberry 2026-10-12: "have the grid system mark plots for buildings, with fixed
                // sizes and shapes"). Laid on THIS town -- 45-degree avenue, mains, tees, triangle islands -- because
                // a plain grid would let a layout pass that has never met an odd corner. Every claim is checked by
                // code of its own, not by the functions that placed the plots: a plot generator that can see a
                // pavement and a test that asks it whether it can see one are one bug, twice.
                int nPlots = gD.MarkPlots(null, rfD);
                var plots = gD.Plots;
                var kindsSeen = new HashSet<TownRoadGrid.PlotKind>();
                int offSize = 0, offLattice = 0;
                foreach (var pl in plots)
                {
                    if (pl.Outline != null) continue;             // a triangle lot is the shape it found, not a catalogue lot
                    kindsSeen.Add(pl.Kind);
                    // (the lot as LAID: GrowPlots then pushes its yards out, and holds its building zone where it was)
                    var want = TownRoadGrid.SizeOf(pl.Kind);
                    if (Mathf.Abs(pl.BaseWidth - want.Width) > 1e-3f || Mathf.Abs(pl.BaseDepth - want.Depth) > 1e-3f) offSize++;
                    if (Mathf.Abs(pl.BaseWidth / 3f - Mathf.Round(pl.BaseWidth / 3f)) > 1e-3f || Mathf.Abs(pl.BaseDepth / 3f - Mathf.Round(pl.BaseDepth / 3f)) > 1e-3f) offLattice++;
                }
                T.Check($"building plots are marked ({nPlots}, {kindsSeen.Count} of {TownRoadGrid.PlotSizes.Length} kinds)",
                        nPlots >= 20 && kindsSeen.Count >= 5);
                T.Check($"...each EXACTLY its catalogue size ({offSize} off), on the 3 m building lattice ({offLattice} off)",
                        offSize == 0 && offLattice == 0);
                // no two overlap -- an overlap test of the test's own, held to a control that must overlap
                static bool Overlaps(Vector3[] a, Vector3[] b)
                {
                    foreach (var poly in new[] { a, b })
                        for (int e = 0; e < 4; e++)
                        {
                            var d = poly[(e + 1) % 4] - poly[e]; var axis = new Vector2(-d.Z, d.X).Normalized();
                            float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
                            foreach (var q in a) { float t = q.X * axis.X + q.Z * axis.Y; a0 = Mathf.Min(a0, t); a1 = Mathf.Max(a1, t); }
                            foreach (var q in b) { float t = q.X * axis.X + q.Z * axis.Y; b0 = Mathf.Min(b0, t); b1 = Mathf.Max(b1, t); }
                            if (a1 <= b0 + 0.01f || b1 <= a0 + 0.01f) return false;
                        }
                    return true;
                }
                int overlapping = 0;
                for (int x = 0; x < plots.Count; x++)
                    for (int y = x + 1; y < plots.Count; y++)
                        if (Overlaps(plots[x].Corners(), plots[y].Corners())) overlapping++;
                var c0 = plots[0].Corners(); var shifted = new Vector3[4];
                for (int k = 0; k < 4; k++) shifted[k] = c0[k] + plots[0].Along * (plots[0].Width * 0.5f);
                T.Check($"...no two plots overlap ({overlapping} pairs), and the overlap test can see one (control: {Overlaps(c0, shifted)})",
                        overlapping == 0 && Overlaps(c0, shifted));
                // clear of every carriageway and its pavement, every junction slab, and every corner pavement -- by a
                // dense sample of each plot, edge and inside
                float walkW = walkD.Width, keepOff = walkD.RoadGap + walkW;
                var slabBox = new List<(double x0, double x1, double z0, double z1, SDG.Unturned.RoadPatch p)>();
                foreach (var pp in gD.Patches)
                {
                    double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
                    for (int k = 0; k < pp.X.Length; k++) { x0 = Math.Min(x0, pp.X[k]); x1 = Math.Max(x1, pp.X[k]); z0 = Math.Min(z0, pp.Z[k]); z1 = Math.Max(z1, pp.Z[k]); }
                    slabBox.Add((x0, x1, z0, z1, pp));
                }
                var kerbBox = new List<(float x0, float x1, float z0, float z1, List<Vector3> kp)>();
                foreach (var (_, kp) in kerbs)
                {
                    float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                    foreach (var q in kp) { x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X); z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z); }
                    kerbBox.Add((x0, x1, z0, z1, kp));
                }
                bool OnSomething(Vector3 q)
                {
                    if (rfD.DeepestRoadOverlap(q, keepOff, out _, out _)) return true;
                    foreach (var (x0, x1, z0, z1, pp) in slabBox)
                        if (q.X >= x0 - keepOff && q.X <= x1 + keepOff && -q.Z >= z0 - keepOff && -q.Z <= z1 + keepOff && pp.DistanceTo(q.X, -q.Z) < keepOff) return true;
                    foreach (var (kx0, kx1, kz0, kz1, kp) in kerbBox)
                    {
                        if (q.X < kx0 - walkW || q.X > kx1 + walkW || q.Z < kz0 - walkW || q.Z > kz1 + walkW) continue;
                        for (int k = 0; k + 1 < kp.Count; k++)
                        {
                            var a = kp[k]; var b = kp[k + 1];
                            float abx = b.X - a.X, abz = b.Z - a.Z, l2 = abx * abx + abz * abz;
                            float t = l2 < 1e-9f ? 0f : Mathf.Clamp(((q.X - a.X) * abx + (q.Z - a.Z) * abz) / l2, 0f, 1f);
                            if (new Vector2(a.X + abx * t - q.X, a.Z + abz * t - q.Z).Length() < walkW) return true;
                        }
                    }
                    return false;
                }
                bool PlotTouches(Vector3[] c)
                {
                    for (int e = 0; e < c.Length; e++)
                    {
                        var a = c[e]; var b = c[(e + 1) % c.Length];
                        int n = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.X - a.X, b.Z - a.Z).Length() / 0.75f));
                        for (int k = 0; k <= n; k++) if (OnSomething(a.Lerp(b, (float)k / n))) return true;
                    }
                    if (c.Length == 4)
                    {
                        for (float u = 0.125f; u < 1f; u += 0.125f)
                            for (float v = 0.125f; v < 1f; v += 0.125f)
                                if (OnSomething(c[0].Lerp(c[1], u).Lerp(c[3].Lerp(c[2], u), v))) return true;
                        return false;
                    }
                    // (a triangle lot's outline: fanned from its middle)
                    var m = Vector3.Zero; foreach (var q in c) m += q; m /= c.Length;
                    for (int e = 0; e < c.Length; e++)
                        for (float u = 0.125f; u < 1f; u += 0.125f)
                            for (float v = 0.125f; u + v < 1f; v += 0.125f)
                                if (OnSomething(m + (c[e] - m) * u + (c[(e + 1) % c.Length] - m) * v)) return true;
                    return false;
                }
                int touching = 0;
                foreach (var pl in plots) if (PlotTouches(pl.Boundary())) touching++;
                // the control: the same check on a plot slid 3 m toward its own street must see the pavement
                var toward = new Vector3[4];
                for (int k = 0; k < 4; k++) toward[k] = c0[k] + plots[0].Facing * 3f;
                T.Check($"...none on a road, a pavement, a junction slab or a corner pavement ({touching} touch), and the check can see one (control: {PlotTouches(toward)})",
                        touching == 0 && PlotTouches(toward));
                // each fronts its street: front edge just behind the pavement, facing the road
                int offFront = 0; float worstFront = 0f;
                foreach (var pl in plots)
                {
                    if (pl.Outline != null) continue;             // (a triangle lot's front is its outline's, held with the rest of it)
                    float want = rfD.RoadHalfWidth(pl.Road) + walkD.RoadGap + walkW + gD.PlotSetback + pl.Setback;
                    float best = float.MaxValue; Vector3 nearest = Vector3.Zero;
                    float L = rfD.RoadLength(pl.Road);
                    for (float d = 0f; d <= L; d += 0.25f)
                        if (rfD.EvaluateAlong(pl.Road, d, out var q, out _, snapTerrain: false))
                        {
                            float dd = new Vector2(q.X - pl.Front.X, q.Z - pl.Front.Z).Length();
                            if (dd < best) { best = dd; nearest = q; }
                        }
                    var toRoad = new Vector3(nearest.X - pl.Front.X, 0f, nearest.Z - pl.Front.Z);
                    worstFront = Mathf.Max(worstFront, Mathf.Abs(best - want));
                    if (Mathf.Abs(best - want) > 0.05f || toRoad.Dot(pl.Facing) <= 0f) offFront++;
                }
                T.Check($"...each fronts its own street, facing it from just behind the pavement ({offFront} not; worst {worstFront * 100f:0.0} cm off)",
                        offFront == 0);
                // ⭐ YARDS AND ALLEYS (strawberry 2026-10-12: "consider space for alleys, backyards, front yards").
                // Each plot's building zone is the plot less its fixed yards -- still whole 3 m cells -- and every
                // block has a back lane: on its own ground, out to a street at each end, never onto a main, and
                // never under a plot.
                int zoneOff = 0;
                foreach (var pl in plots)
                {
                    if (pl.Outline != null) continue;
                    var ps = TownRoadGrid.SizeOf(pl.Kind);
                    float wantW = pl.Kind == TownRoadGrid.PlotKind.Corner ? ps.Width - ps.FrontYard - ps.SideYard : ps.Width - 2f * ps.SideYard;
                    float wantD = ps.Depth - ps.FrontYard - ps.BackYard;
                    var z = pl.BuildArea();
                    float zw = (z[1] - z[0]).Length(), zd = (z[3] - z[0]).Length();
                    bool lattice = Mathf.Abs(zw / 3f - Mathf.Round(zw / 3f)) < 1e-3f && Mathf.Abs(zd / 3f - Mathf.Round(zd / 3f)) < 1e-3f;
                    bool inside = true;
                    foreach (var q in z)
                    {
                        var rel = q - pl.Front; float al = rel.X * pl.Along.X + rel.Z * pl.Along.Z, fa = -(rel.X * pl.Facing.X + rel.Z * pl.Facing.Z);
                        if (Mathf.Abs(al) > pl.Width * 0.5f + 1e-3f || fa < -1e-3f || fa > pl.Depth + 1e-3f) inside = false;
                    }
                    if (Mathf.Abs(zw - wantW) > 1e-3f || Mathf.Abs(zd - wantD) > 1e-3f || !lattice || !inside || ps.FrontYard < 3f && pl.Kind != TownRoadGrid.PlotKind.Shop) zoneOff++;
                }
                T.Check($"...each with front yard, backyard and side yards round a building zone of whole 3 m cells ({zoneOff} off)", zoneOff == 0);
                var alleys = gD.Alleys;
                int alleyOnPlot = 0, alleyOnRoad = 0, alleyLoose = 0, alleyOnMain = 0;
                foreach (var al in alleys)
                {
                    var ac = al.Corners();
                    foreach (var pl in plots) if (Overlaps(pl.Corners(), ac)) alleyOnPlot++;
                    // its strip is on its own ground -- away from its ends, where a corner may tuck under a pavement
                    var d = (al.B - al.A); float len = d.Length(); d /= len;
                    var n = new Vector3(-d.Z, 0f, d.X) * (al.Width * 0.5f - 0.1f);
                    for (float t = al.Width; t < len - al.Width; t += 1f)
                        foreach (var q in new[] { al.A + d * t, al.A + d * t + n, al.A + d * t - n })
                            if (OnSomething(q)) { alleyOnRoad++; goto nextAlley; }
                    nextAlley:
                    // and each street end comes out at a pavement -- of a side street, never a main
                    foreach (var (end, street) in new[] { (al.A, !al.FromKnee), (al.B, true) })
                    {
                        if (!street) continue;
                        if (!rfD.DeepestRoadOverlap(end, keepOff + 0.75f, out int er, out _))
                        {
                            // or at a junction's slab: a lane can come out where the corner's asphalt begins
                            bool atSlab = false;
                            foreach (var pp in gD.Patches) if (pp.DistanceTo(end.X, -end.Z) < keepOff + 0.75f) { atSlab = true; break; }
                            if (!atSlab) alleyLoose++;
                            continue;
                        }
                        if (gD.IsMain(rfD, er)) alleyOnMain++;
                    }
                }
                T.Check($"...and back lanes through the blocks ({alleys.Count} lanes)", alleys.Count >= 6);
                T.Check($"...never under a plot ({alleyOnPlot}) nor on a road or pavement ({alleyOnRoad})", alleyOnPlot == 0 && alleyOnRoad == 0);
                T.Check($"...each coming out at a street's pavement ({alleyLoose} loose ends), and never onto a main ({alleyOnMain})",
                        alleyLoose == 0 && alleyOnMain == 0);
                // ⭐⭐ NO FRONTAGE LEFT WITH ROOM FOR A PLOT (strawberry 2026-10-13: "fill gaps in zoning with more buildings").
                // Along both sides of every street, a window as narrow as the narrowest kind that street is offered -- plus
                // half a metre -- and as deep as its shallowest, standing on the frontage line, has to run into something: a
                // road or its pavement, a junction's slab or corner, a plot, a lane, the edge of town. Kept off all of them by
                // the planner's own clearance, and measured with this test's geometry rather than PlotFits. The control is the
                // old planner -- the same town with each frontage offered its DEEPEST kind only, as UG_TOWNDEEPONLY=1 does --
                // whose empty frontages this must find: a block whose back lane runs down its middle is ~35 m deep a side,
                // too shallow for a 36 m plot, and the old planner gave up on the whole side.
                {
                    float keepGap = keepOff + gD.PlotClearance + 0.1f;
                    float cbG = Mathf.Cos(0.3f), sbG = Mathf.Sin(0.3f);
                    float fu0 = float.MaxValue, fu1 = float.MinValue, fv0 = float.MaxValue, fv1 = float.MinValue;
                    for (int j = 0; j < rfD.JunctionCount; j++)
                    {
                        var jp = rfD.JunctionPos(j);
                        float u = jp.X * cbG + jp.Z * sbG, v = -jp.X * sbG + jp.Z * cbG;
                        fu0 = Mathf.Min(fu0, u); fu1 = Mathf.Max(fu1, u); fv0 = Mathf.Min(fv0, v); fv1 = Mathf.Max(fv1, v);
                    }
                    bool Blocked(Vector3 q)
                    {
                        float u = q.X * cbG + q.Z * sbG, v = -q.X * sbG + q.Z * cbG;
                        if (u < fu0 || u > fu1 || v < fv0 || v > fv1) return true;
                        if (rfD.DeepestRoadOverlap(q, keepGap, out _, out _)) return true;
                        foreach (var (x0, x1, z0, z1, pp) in slabBox)
                            if (q.X >= x0 - keepGap && q.X <= x1 + keepGap && -q.Z >= z0 - keepGap && -q.Z <= z1 + keepGap && pp.DistanceTo(q.X, -q.Z) < keepGap) return true;
                        float wk = walkW + gD.PlotClearance + 0.1f;
                        foreach (var (kx0, kx1, kz0, kz1, kp) in kerbBox)
                        {
                            if (q.X < kx0 - wk || q.X > kx1 + wk || q.Z < kz0 - wk || q.Z > kz1 + wk) continue;
                            for (int k = 0; k + 1 < kp.Count; k++)
                            {
                                var a = kp[k]; var b = kp[k + 1];
                                float abx = b.X - a.X, abz = b.Z - a.Z, l2 = abx * abx + abz * abz;
                                float t = l2 < 1e-9f ? 0f : Mathf.Clamp(((q.X - a.X) * abx + (q.Z - a.Z) * abz) / l2, 0f, 1f);
                                if (new Vector2(a.X + abx * t - q.X, a.Z + abz * t - q.Z).Length() < wk) return true;
                            }
                        }
                        return false;
                    }
                    bool WindowBlocked(Vector3[] c)
                    {
                        for (int e = 0; e < 4; e++)
                        {
                            var a = c[e]; var b = c[(e + 1) % 4];
                            int n = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.X - a.X, b.Z - a.Z).Length() / 0.75f));
                            for (int k = 0; k <= n; k++) if (Blocked(a.Lerp(b, (float)k / n))) return true;
                        }
                        for (float u = 0.125f; u < 1f; u += 0.125f)
                            for (float v = 0.125f; v < 1f; v += 0.125f)
                                if (Blocked(c[0].Lerp(c[1], u).Lerp(c[3].Lerp(c[2], u), v))) return true;
                        return false;
                    }
                    int FrontageGaps(List<TownRoadGrid.Plot> pls, List<TownRoadGrid.Alley> als, out string firstGap)
                    {
                        firstGap = "";
                        int gaps = 0;
                        foreach (int r in gD.Built)
                        {
                            float wMin = float.MaxValue, dMin = float.MaxValue;
                            foreach (var k in gD.FrontageKinds(gD.IsMain(rfD, r)))
                            { var ps = TownRoadGrid.SizeOf(k); wMin = Mathf.Min(wMin, ps.Width); dMin = Mathf.Min(dMin, ps.Depth); }
                            float win = wMin + 0.5f, L = rfD.RoadLength(r), front = rfD.RoadHalfWidth(r) + walkD.RoadGap + walkW + gD.PlotSetback;
                            foreach (int side in new[] { -1, 1 })
                                for (float d = win * 0.5f; d + win * 0.5f <= L; d += 0.5f)
                                {
                                    if (!rfD.EvaluateAlong(r, d, out var pos, out var tan, snapTerrain: false)) continue;
                                    var along = new Vector3(tan.X, 0f, tan.Z).Normalized();
                                    var outw = new Vector3(-along.Z, 0f, along.X) * side;
                                    var w = new TownRoadGrid.Plot { Width = win, Depth = dMin, Along = along, Facing = -outw, Front = pos + outw * front }.Corners();
                                    bool taken = false;
                                    foreach (var pl in pls) if (Overlaps(w, pl.Corners())) { taken = true; break; }
                                    if (!taken) foreach (var al in als) if (Overlaps(w, al.Corners())) { taken = true; break; }
                                    if (taken || WindowBlocked(w)) continue;
                                    if (gaps++ == 0) firstGap = $"road {r} side {side} at {d:0.0} of {L:0.0} m";
                                    d += wMin;                 // one gap counted once, not once per half metre of it
                                }
                        }
                        return gaps;
                    }
                    var rfD3 = Field();
                    var gD3 = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                                 Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true, FillShallower = false,
                                                 AlleysOnRequest = false, ExpandPlots = false };   // (the lots as laid: grown, they would close the gaps this counts)
                    gD3.Generate(rfD3, flat);
                    var w3 = new EditorSidewalk(); World.AddChild(w3);
                    gD3.LaySidewalkCornersOn(rfD3, World, w3, out _);
                    gD3.MarkPlots(null, rfD3);
                    int gapsNow = FrontageGaps(plots, gD.Alleys, out string gapAt);
                    int gapsOld = FrontageGaps(gD3.Plots, gD3.Alleys, out string oldAt);
                    T.Check($"...and no frontage left with room for a plot ({gapsNow} gaps{(gapsNow > 0 ? $", first on {gapAt}" : "")}; {gD.ShallowPlots} plots laid where only a shallower kind fitted) -- where the deepest kind alone left {gapsOld} (control: {gD3.Plots.Count} plots against {plots.Count}{(gapsOld > 0 ? $", first on {oldAt}" : "")})",
                            gapsNow == 0 && gapsOld > 0 && gD.ShallowPlots > 0 && plots.Count > gD3.Plots.Count);
                }
                // inside the town: nothing on the far side of the outermost street, in open country
                {
                    float cb = Mathf.Cos(0.3f), sb = Mathf.Sin(0.3f);
                    float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                    for (int j = 0; j < rfD.JunctionCount; j++)
                    {
                        var jp = rfD.JunctionPos(j);
                        float u = jp.X * cb + jp.Z * sb, v = -jp.X * sb + jp.Z * cb;
                        u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u); v0 = Mathf.Min(v0, v); v1 = Mathf.Max(v1, v);
                    }
                    int outside = 0;
                    foreach (var pl in plots)
                        foreach (var q in pl.Corners())
                        {
                            float u = q.X * cb + q.Z * sb, v = -q.X * sb + q.Z * cb;
                            if (u < u0 - 0.01f || u > u1 + 0.01f || v < v0 - 0.01f || v > v1 + 0.01f) { outside++; break; }
                        }
                    T.Check($"...and all inside the town's outermost streets ({outside} outside)", outside == 0);
                }
                // ⭐ THE LOTS GROW INTO WHAT IS LEFT (strawberry 2026-10-14: "work on filling spaces with more/expanding existing
                // plots"): only their yards, their fronts on their streets and their building zones where they were laid (the
                // zone check above holds the zones). Counted on the census's own 5 m cells, against the same town ungrown.
                {
                    int grown = 0, badGrow = 0;
                    foreach (var pl in plots)
                    {
                        if (pl.GrowBack > 0f || pl.GrowLeft > 0f || pl.GrowRight > 0f) grown++;
                        if (pl.GrowBack < -1e-4f || pl.GrowLeft < -1e-4f || pl.GrowRight < -1e-4f
                            || pl.GrowBack > gD.ExpandMaxBack + 1e-3f || pl.GrowLeft > gD.ExpandMaxSide + 1e-3f || pl.GrowRight > gD.ExpandMaxSide + 1e-3f) badGrow++;
                    }
                    var rfU = Field();
                    var gU = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                                Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true, AlleysOnRequest = false, ExpandPlots = false };
                    gU.Generate(rfU, flat);
                    var wU = new EditorSidewalk(); World.AddChild(wU);
                    gU.LaySidewalkCornersOn(rfU, World, wU, out _);
                    gU.MarkPlots(null, rfU);
                    T.Check($"lots grow their yards into the ground left round them ({grown} of {plots.Count} grew, {badGrow} outside their limits): "
                            + $"{gD.ZonedCells} 5 m cells zoned, {gD.UnzonedCells} buildable left -- ungrown, {gU.ZonedCells} and {gU.UnzonedCells} (control)",
                            grown > plots.Count / 2 && badGrow == 0 && gD.UnzonedCells < gU.UnzonedCells * 0.6f && gD.ZonedCells > gU.ZonedCells);

                    // ⭐ A LANE ONLY WHERE A BUILDING ASKS (strawberry 2026-10-14: "alleys should only be placed at the request of
                    // a building. ie a shop wants an alley for delivery, house doesnt need it"): the same town again, lanes on
                    // request. Every lane is in a block with a shop or an apartment block in it, a block of houses has none, and
                    // there are fewer of them than one a block (this town, gD).
                    var rfR = Field();
                    var gR = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                                Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true };
                    gR.Generate(rfR, flat);
                    var wR = new EditorSidewalk(); World.AddChild(wR);
                    gR.LaySidewalkCornersOn(rfR, World, wR, out _);
                    gR.MarkPlots(null, rfR);
                    var asking = new HashSet<(int, int)>(); var houseOnly = new HashSet<(int, int)>();
                    foreach (var pl in gR.Plots)
                    {
                        var b = gR.BlockOf(pl.Centre);
                        if (!b.HasValue) continue;
                        if (pl.Kind == TownRoadGrid.PlotKind.Shop || pl.Kind == TownRoadGrid.PlotKind.Apartments) asking.Add(b.Value);
                    }
                    foreach (var pl in gR.Plots) { var b = gR.BlockOf(pl.Centre); if (b.HasValue && !asking.Contains(b.Value)) houseOnly.Add(b.Value); }
                    int unasked = 0, lanedHouses = 0;
                    foreach (var al in gR.Alleys)
                    {
                        // the block a lane serves: where its middle stands (an L's leg from the knee, its own middle)
                        var b = gR.BlockOf((al.A + al.B) * 0.5f);
                        if (!b.HasValue || !asking.Contains(b.Value)) unasked++;
                        if (b.HasValue && houseOnly.Contains(b.Value)) lanedHouses++;
                    }
                    T.Check($"...a back lane only where a shop or apartments asked: {gR.Alleys.Count} lanes in {asking.Count} asking blocks, {unasked} where nothing asked, "
                            + $"{lanedHouses} in the {houseOnly.Count} blocks of houses -- against {gD.Alleys.Count} with a lane in every block (control)",
                            gR.Alleys.Count > 0 && unasked == 0 && lanedHouses == 0 && houseOnly.Count > 0 && gR.Alleys.Count < gD.Alleys.Count);

                    // ⭐ YARD FENCES, DESIGNATED (strawberry 2026-10-14: "designating where yard fences should be (not placing
                    // anything just yet)"): round backyards and side yards, never across a front, one fence where two lots share
                    // a line -- held to the lots' own corners.
                    var fences = gR.Fences;
                    static bool OnLine(Vector3 a, Vector3 b, Vector3 q, float tol)
                    {
                        var d = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = d.LengthSquared();
                        if (l2 < 1e-8f) return false;
                        float t = ((q.X - a.X) * d.X + (q.Z - a.Z) * d.Y) / l2;
                        if (t < -1e-3f || t > 1f + 1e-3f) return false;
                        return new Vector2(a.X + d.X * t - q.X, a.Z + d.Y * t - q.Z).Length() < tol;
                    }
                    bool Fenced(Vector3 q) { foreach (var f in fences) if (OnLine(f.A, f.B, q, 0.05f)) return true; return false; }
                    int acrossFront = 0, backsBare = 0, houses = 0, twice = 0;
                    foreach (var pl in gR.Plots)
                    {
                        var c = pl.Corners();
                        // a front: no fence along its middle stretch (its ends may meet a neighbour's side fence)
                        for (float t = 0.2f; t <= 0.8f; t += 0.1f)
                        {
                            var q = c[0].Lerp(c[1], t);
                            bool sideOfOther = false;
                            foreach (var o in gR.Plots) if (!ReferenceEquals(o, pl)) { var oc = o.Corners(); if (OnLine(oc[1], oc[2], q, 0.05f) || OnLine(oc[2], oc[3], q, 0.05f) || OnLine(oc[3], oc[0], q, 0.05f)) { sideOfOther = true; break; } }
                            if (!sideOfOther && Fenced(q)) { acrossFront++; break; }
                        }
                        if (pl.Kind is TownRoadGrid.PlotKind.HouseSmall or TownRoadGrid.PlotKind.HouseMedium or TownRoadGrid.PlotKind.HouseLarge)
                        {
                            houses++;
                            for (float t = 0.1f; t <= 0.9f; t += 0.2f) if (!Fenced(c[2].Lerp(c[3], t))) { backsBare++; break; }
                        }
                    }
                    for (int x = 0; x < fences.Count; x++)
                        for (int y = x + 1; y < fences.Count; y++)
                        {
                            var fa = fences[x]; var fb = fences[y];
                            var m = (fb.A + fb.B) * 0.5f;
                            if (OnLine(fa.A, fa.B, m, 0.03f) && OnLine(fa.A, fa.B, fb.A.Lerp(fb.B, 0.25f), 0.03f) && OnLine(fa.A, fa.B, fb.A.Lerp(fb.B, 0.75f), 0.03f)) twice++;
                        }
                    T.Check($"...yard fences designated round the yards: {fences.Count} runs; {acrossFront} across a lot's front, {backsBare} of {houses} houses' backyards unfenced, {twice} laid twice",
                            fences.Count > 20 && acrossFront == 0 && backsBare == 0 && houses > 5 && twice == 0);

                    // ⭐ TRIANGLE LOTS (strawberry 2026-10-14: "add support for stuff to be built in triangular plots"): the wedges
                    // the 45-degree avenue cuts off the blocks become lots of their own shape, each with a building zone of whole
                    // 3 m cells inside it -- on free ground, clear of every other lot, off the avenue's pavement, fronting it.
                    // Held on this town (gD), whose avenue makes them; the control is the same town without them.
                    var tris = new List<TownRoadGrid.Plot>();
                    foreach (var pl in plots) if (pl.Outline != null) tris.Add(pl);
                    float diagFront = 0f;
                    foreach (int r in gD.DiagonalRoads) diagFront = Mathf.Max(diagFront, rfD.RoadHalfWidth(r) + walkD.RoadGap + walkW + gD.PlotSetback);
                    int notConvex = 0, onLand = 0, intoLots = 0, zoned = 0, zoneOut = 0, offLatticeT = 0, notFronting = 0;
                    // ...and RIGHT INTO ITS CORNERS (strawberry 2026-10-14: "make sure it fills right to the corners" -- "i meant the
                    // plot, not the actual building"): every side of it lies on what bounds it -- a street's frontage line, or the
                    // side of a lot it runs beside -- or has no open ground just outside it. A point the metre raster cut off square
                    // is a side on neither, with the rest of the point just past it. Open ground: off every road, pavement, slab and
                    // corner pavement (this test's own OnSomething), behind every frontage line, in no lot. ⚠ AND OPEN TO THE
                    // GENERATOR TOO (LotCouldHave): it keeps lots off ground this test has no model of -- an alley's strip, a lane's
                    // square knee, a bay's front, a slab's clearance -- and those were 6 of the first 7 "short" this test found.
                    // The control: the same town with its points as the raster leaves them.
                    float frontKeep = keepOff + gD.PlotSetback - 0.05f;
                    var shortWhere = new List<string>();
                    // the frontage line of each street, as the test sees it: its road sampled every metre
                    var streetPts = new List<(int r, Vector3[] p)>();
                    foreach (int r in gD.Built)
                    {
                        float L = rfD.RoadLength(r); var ps = new List<Vector3>();
                        for (float d = 0f; d <= L; d += 1f) if (rfD.EvaluateAlong(r, d, out var q, out _, snapTerrain: false)) ps.Add(new Vector3(q.X, 0f, q.Z));
                        if (ps.Count >= 2) streetPts.Add((r, ps.ToArray()));
                    }
                    bool OnBound(Vector3 a, Vector3 b, List<TownRoadGrid.Plot> among, TownRoadGrid.Plot self)
                    {
                        var sd = new Vector3(b.X - a.X, 0f, b.Z - a.Z); float len = sd.Length(); if (len < 0.5f) return false; sd /= len;
                        var m = (a + b) * 0.5f; m.Y = 0f;
                        foreach (var (r, ps) in streetPts)
                        {
                            float front = rfD.RoadHalfWidth(r) + walkD.RoadGap + walkW + gD.PlotSetback, best = float.MaxValue; Vector3 tan = Vector3.Zero;
                            for (int k = 0; k + 1 < ps.Length; k++)
                            {
                                var e = ps[k + 1] - ps[k]; float el = e.Length(); if (el < 1e-4f) continue;
                                float u = Mathf.Clamp((m - ps[k]).Dot(e) / (el * el), 0f, 1f);
                                float dd = (ps[k] + e * u - m).Length();
                                if (dd < best) { best = dd; tan = e / el; }
                            }
                            if (Mathf.Abs(best - front) <= 0.3f && Mathf.Abs(tan.Dot(sd)) >= 0.998f) return true;
                        }
                        foreach (var o in among)
                        {
                            if (ReferenceEquals(o, self)) continue;
                            var c = o.Boundary();
                            for (int k = 0; k < c.Length; k++)
                            {
                                var p0 = new Vector3(c[k].X, 0f, c[k].Z); var p1 = new Vector3(c[(k + 1) % c.Length].X, 0f, c[(k + 1) % c.Length].Z);
                                var e = p1 - p0; float el = e.Length(); if (el < 0.5f) continue; e /= el;
                                if (Mathf.Abs(e.Dot(sd)) < 0.998f || Mathf.Abs((m - p0).Dot(new Vector3(-e.Z, 0f, e.X))) > 0.3f) continue;
                                float t0 = (a - p0).Dot(e), t1 = (b - p0).Dot(e);
                                if (Mathf.Max(t0, t1) >= 0.5f && Mathf.Min(t0, t1) <= el - 0.5f) return true;
                            }
                        }
                        return false;
                    }
                    int SidesShortOf(TownRoadGrid gen, List<TownRoadGrid.Plot> lots, List<TownRoadGrid.Plot> among, out int sides)
                    {
                        int nShort = 0; sides = 0; shortWhere.Clear();
                        foreach (var t in lots)
                        {
                            var ol = t.Outline;
                            float tw = 0f; for (int k = 0; k < ol.Length; k++) { var a = ol[k]; var b = ol[(k + 1) % ol.Length]; tw += a.X * b.Z - b.X * a.Z; }
                            for (int k = 0; k < ol.Length; k++)
                            {
                                var a = new Vector3(ol[k].X, 0f, ol[k].Z); var b = new Vector3(ol[(k + 1) % ol.Length].X, 0f, ol[(k + 1) % ol.Length].Z);
                                float len = (b - a).Length(); if (len < 0.05f) continue;
                                sides++;
                                var d = (b - a) / len;
                                var outN = tw > 0f ? new Vector3(d.Z, 0f, -d.X) : new Vector3(-d.Z, 0f, d.X);
                                bool open = false;
                                int ns = Mathf.Max(2, Mathf.CeilToInt(len / 0.5f));
                                for (int u = 1; u < ns && !open; u++)
                                {
                                    var q = a + d * (len * u / ns) + outN * 0.6f;
                                    if (OnSomething(q) || rfD.DeepestRoadOverlap(q, frontKeep, out _, out _)) continue;
                                    bool inLot = false;
                                    foreach (var o in among) if (!ReferenceEquals(o, t) && TownRoadGrid.InsidePoly(o.Boundary(), q)) { inLot = true; break; }
                                    open = !inLot && gen.LotCouldHave(rfD, q);
                                }
                                if (!open || OnBound(a, b, among, t)) continue;
                                nShort++;
                                if (shortWhere.Count < 10) shortWhere.Add($"({(a.X + b.X) * 0.5f:0.0},{(a.Z + b.Z) * 0.5f:0.0}) {len:0.0} m");
                            }
                        }
                        return nShort;
                    }
                    foreach (var t in tris)
                    {
                        var ol = t.Outline;
                        bool neg = false, pos = false;
                        for (int k = 0; k < ol.Length; k++)
                        {
                            var a = ol[k]; var b = ol[(k + 1) % ol.Length]; var c = ol[(k + 2) % ol.Length];
                            float x = (b.X - a.X) * (c.Z - b.Z) - (b.Z - a.Z) * (c.X - b.X);
                            if (x < -1e-3f) neg = true; else if (x > 1e-3f) pos = true;
                        }
                        if (ol.Length < 3 || neg && pos) notConvex++;
                        if (PlotTouches(ol)) onLand++;
                        foreach (var o in plots)
                            if (!ReferenceEquals(o, t) && TownRoadGrid.QuadsOverlap(ol, o.Boundary(), 0.02f)) { intoLots++; break; }
                        if (t.Width > 0f && t.Depth > 0f)
                        {
                            zoned++;
                            foreach (var q in t.BuildArea()) if (!TownRoadGrid.InsidePoly(ol, q)) { zoneOut++; break; }
                            if (Mathf.Abs(t.Width / 3f - Mathf.Round(t.Width / 3f)) > 1e-3f || Mathf.Abs(t.Depth / 3f - Mathf.Round(t.Depth / 3f)) > 1e-3f) offLatticeT++;
                        }
                        // fronting the avenue: some point of its outline's edges within reach of the avenue's frontage line
                        float nearest = float.MaxValue;
                        var edgePts = new List<Vector3>();
                        for (int k = 0; k < ol.Length; k++)
                        {
                            var a = ol[k]; var b = ol[(k + 1) % ol.Length];
                            int nn = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.X - a.X, b.Z - a.Z).Length()));
                            for (int u = 0; u <= nn; u++) edgePts.Add(a.Lerp(b, (float)u / nn));
                        }
                        foreach (int r in gD.DiagonalRoads)
                        {
                            float L = rfD.RoadLength(r);
                            for (float d = 0f; d <= L; d += 1f)
                                if (rfD.EvaluateAlong(r, d, out var rq, out _, snapTerrain: false))
                                    foreach (var q in edgePts) nearest = Mathf.Min(nearest, new Vector2(rq.X - q.X, rq.Z - q.Z).Length());
                        }
                        if (nearest > diagFront + 1.5f) notFronting++;
                    }
                    var rfT0 = Field();
                    var gT0 = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                                 Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true, AlleysOnRequest = false, TriangleLots = false };
                    gT0.Generate(rfT0, flat);
                    var wT0 = new EditorSidewalk(); World.AddChild(wT0);
                    gT0.LaySidewalkCornersOn(rfT0, World, wT0, out _);
                    gT0.MarkPlots(null, rfT0);
                    int tris0 = 0; foreach (var pl in gT0.Plots) if (pl.Outline != null) tris0++;
                    // (the control for the corners: this town again, each wedge's points left as the raster cut them)
                    var rfC0 = Field();
                    var gC0 = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                                 Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true, AlleysOnRequest = false, FillCorners = false };
                    gC0.Generate(rfC0, flat);
                    var wC0 = new EditorSidewalk(); World.AddChild(wC0);
                    gC0.LaySidewalkCornersOn(rfC0, World, wC0, out _);
                    gC0.MarkPlots(null, rfC0);
                    var trisC0 = new List<TownRoadGrid.Plot>(); foreach (var pl in gC0.Plots) if (pl.Outline != null) trisC0.Add(pl);
                    float areaT = 0f, areaC0 = 0f;
                    static float AreaOf(Vector3[] c) { float t2 = 0f; for (int k = 0; k < c.Length; k++) { var a = c[k]; var b = c[(k + 1) % c.Length]; t2 += a.X * b.Z - b.X * a.Z; } return Mathf.Abs(t2) * 0.5f; }
                    foreach (var t in tris) areaT += AreaOf(t.Outline);
                    foreach (var t in trisC0) areaC0 += AreaOf(t.Outline);
                    int shortT = SidesShortOf(gD, tris, plots, out int sidesT);
                    var shortTWhere = string.Join(" ", shortWhere);
                    int shortC0 = SidesShortOf(gC0, trisC0, gC0.Plots, out int sidesC0);
                    if (shortT > 0) Log.Print($"[towngrid test] sides short of what bounds them: {shortTWhere}");
                    T.Check($"...each one right into its corners: {shortT} of {sidesT} sides stop short of what bounds them, {areaT:0} m2 in all "
                            + $"(control, points as the raster cut them: {shortC0} of {sidesC0} short, {areaC0:0} m2)",
                            tris.Count >= 2 && shortT == 0 && shortC0 > 0 && areaT > areaC0);
                    T.Check($"triangle lots in the avenue's wedges: {tris.Count} laid ({zoned} with a building zone); {notConvex} not convex, {onLand} on a road/pavement/slab, "
                            + $"{intoLots} into another lot, {zoneOut} zones outside their wedge, {offLatticeT} off the 3 m lattice, {notFronting} not fronting the avenue -- "
                            + $"{gD.UnzonedCells} buildable cells left, against {gT0.UnzonedCells} with none ({tris0} laid: control)",
                            tris.Count >= 2 && zoned >= 1 && notConvex == 0 && onLand == 0 && intoLots == 0 && zoneOut == 0 && offLatticeT == 0
                            && notFronting == 0 && tris0 == 0 && gD.UnzonedCells < gT0.UnzonedCells);
                }
                int cornerLots = 0, cornerOff = 0;
                foreach (var pl in plots)
                {
                    if (pl.Kind != TownRoadGrid.PlotKind.Corner) continue;
                    cornerLots++;
                    if (pl.Road2 < 0 || Mathf.Abs(pl.BaseWidth - pl.BaseDepth) > 1e-3f) cornerOff++;
                }
                T.Check($"...square corner lots at the square corners, fronting both streets ({cornerLots} lots, {cornerOff} not)",
                        cornerLots >= 4 && cornerOff == 0);
                // ⚠ AND NO CORNER'S PAVEMENT IS BUILDABLE, probed at its hardest spot: just inside the BACK of the
                // pavement, where a kerb that cuts the block's corner leaves it further than (half width + pavement)
                // from both roads. The generator has no test of its own for this -- the junction slab runs under
                // every corner pavement and its margin refuses them (measured: within 0.03-2 m of each back edge) --
                // so this is what says so if the slabs ever stop reaching. A spot well behind must be buildable.
                {
                    int onWalk = 0, onWalkOk = 0, behind = 0, behindOk = 0;
                    foreach (var (kn, kp) in kerbs)
                    {
                        if (kp.Count < 5) continue;                       // a straight through-piece: no corner to it
                        var mid = kp[kp.Count / 2];
                        var outv = new Vector3(mid.X - kn.X, 0f, mid.Z - kn.Z).Normalized();
                        onWalk++; if (gD.Buildable(rfD, mid + outv * (walkW - 0.15f))) onWalkOk++;
                        behind++; if (gD.Buildable(rfD, mid + outv * (walkW + 6f))) behindOk++;
                    }
                    T.Check($"...and the back of every corner's pavement is never buildable ({onWalkOk} of {onWalk} were; control: {behindOk} of {behind} spots behind them are)",
                            onWalk > 0 && onWalkOk == 0 && behindOk > 0);
                }
                // ⭐ PARKING BAYS (strawberry 2026-10-12: "work on widened sections of road, with dotted lines for
                // parking on one or both sides of the street within the widened portion"). Measured on what was
                // BUILT -- the pavement's own mesh, the plots' own corners, the paint's own triangles -- not by asking
                // the generator what it meant to lay.
                {
                    var bays = gD.Bays;
                    int spaces = 0, paired = 0, single = 0;
                    foreach (var bay in bays) { spaces += bay.Spaces; if (gD.BayOpposite(bay) != null) paired++; else single++; }
                    T.Check($"parking bays widen the streets ({bays.Count} bays, {spaces} spaces; {single} on one side only, {paired} with one facing them across the street)",
                            bays.Count >= 4 && spaces >= bays.Count && single >= 1 && paired >= 2);
                    // 0. SIZED TO THIS GAME'S CARS (strawberry 2026-10-12: "make sure they're scaled to fit the vehicles in
                    //    game"). Each car is built and measured whole -- body, wheels, everything that draws -- and must fit
                    //    between the lane line and the kerb with 20 cm either side, and a space must hold it with a metre
                    //    to pull in and out. ⚠ Measured, not remembered: these cars are 3 m across their tyres, and a
                    //    real-world 2.4 x 6 m bay (the control) fits none of them.
                    {
                        string[] cars = { "sedan", "hatchback", "police", "roadster", "wagon", "van", "offroader", "jeep", "humvee", "truck", "ambulance", "golf" };
                        float widest = 0f, longest = 0f; string wideCar = "", longCar = "";
                        foreach (var name in cars)
                        {
                            var v = Vehicle.BuildByName(name);
                            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                            void Walk(Node n, Transform3D xf)
                            {
                                if (n is Node3D n3 && n != v) xf = xf * n3.Transform;
                                if (n is MeshInstance3D mi && mi.Mesh != null && mi.Visible)
                                {
                                    var a = mi.Mesh.GetAabb();
                                    for (int k = 0; k < 8; k++)
                                    {
                                        var q = xf * a.GetEndpoint(k);
                                        x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X); z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z);
                                    }
                                }
                                foreach (var ch in n.GetChildren()) Walk(ch, xf);
                            }
                            Walk(v, Transform3D.Identity);
                            if (x1 - x0 > widest) { widest = x1 - x0; wideCar = name; }
                            if (z1 - z0 > longest) { longest = z1 - z0; longCar = name; }
                            v.Free();
                        }
                        // across: kerb to carriageway edge (no line runs down the bay's open side any more); along: the
                        // space between two of the solid lines across it
                        bool Fits(float depth, float space) => depth - 0.03f >= widest + 0.4f && space - gD.BayLine >= longest + 1f;
                        int fit = 0;
                        foreach (var bay in bays) if (Fits(bay.Depth, bay.SpaceLength)) fit++;
                        T.Check($"...each sized to the game's own cars: widest {widest:0.00} m ({wideCar}), longest {longest:0.00} m ({longCar}) -- {fit} of {bays.Count} bays hold them ({gD.BayDepth:0.0} m deep, {gD.BaySpace:0.0} m spaces), and a real-world 2.4 x 6 m bay would not (control: {(Fits(2.4f, 6f) ? "FITS" : "does not")})",
                                widest > 1f && longest > 2f && fit == bays.Count && !Fits(2.4f, 6f));
                    }
                    // each bay in its road's frame at its middle: arc offset along, metres out on the bay's side. A bay
                    // is only ever laid on straight frontage, so one frame holds for the whole of it.
                    var frames = new Dictionary<TownRoadGrid.ParkingBay, (Vector3 c, Vector3 t, Vector3 o, float mid, float half)>();
                    foreach (var bay in bays)
                    {
                        float m = (bay.A + bay.B) * 0.5f;
                        rfD.EvaluateAlong(bay.Road, m, out var c, out var tg, snapTerrain: false);
                        var t = new Vector3(tg.X, 0f, tg.Z).Normalized();
                        frames[bay] = (c, t, new Vector3(tg.Z, 0f, -tg.X).Normalized() * bay.Side, m, rfD.RoadHalfWidth(bay.Road));
                    }
                    (Vector3 c, Vector3 t, Vector3 o, float mid, float half) Frame(TownRoadGrid.ParkingBay bay) => frames[bay];
                    // 1. THE PAVEMENT STEPS OUT ROUND IT: the built ribbon's kerb, along the bay's full depth, stands the
                    //    bay's depth further out -- and on a stretch with no bay, it does not (the control).
                    var walkHost = new Node3D(); World.AddChild(walkHost);
                    gD.LaySidewalks(walkHost, rfD);
                    var ribbons = new Dictionary<string, Vector3[]>();
                    var ribbonTops = new Dictionary<string, List<(Vector3 a, Vector3 b, Vector3 c)>>();
                    void Collect(Node n)
                    {
                        if (n is MeshInstance3D mi && mi.Mesh != null && mi.Name.ToString().StartsWith("Sidewalk_"))
                        {
                            var arr = mi.Mesh.SurfaceGetArrays(0);
                            var vs = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                            // one side laid in pieces round a lane's mouth ("Sidewalk_R12_p0", "_p1") is still one side
                            string key = mi.Name.ToString();
                            int cut = key.IndexOf("_p", 10);
                            if (cut > 0) key = key.Substring(0, cut);
                            var merged = vs;
                            if (ribbons.TryGetValue(key, out var had)) { merged = new Vector3[had.Length + vs.Length]; had.CopyTo(merged, 0); vs.CopyTo(merged, had.Length); }
                            ribbons[key] = merged;
                            var ix = arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : (int[])arr[(int)Mesh.ArrayType.Index];
                            var tops = new List<(Vector3, Vector3, Vector3)>();
                            int cnt = ix != null ? ix.Length : vs.Length;
                            for (int k = 0; k + 2 < cnt; k += 3)
                            {
                                Vector3 a = vs[ix != null ? ix[k] : k], b = vs[ix != null ? ix[k + 1] : k + 1], c = vs[ix != null ? ix[k + 2] : k + 2];
                                var nn = (b - a).Cross(c - a);
                                if (nn.Length() > 1e-3f && Mathf.Abs(nn.Y) > 0.9f * nn.Length()) tops.Add((a, b, c));
                            }
                            if (ribbonTops.TryGetValue(key, out var hadTops)) tops.AddRange(hadTops);
                            ribbonTops[key] = tops;
                        }
                        foreach (var ch in n.GetChildren()) Collect(ch);
                    }
                    static bool InTri(Vector3 q, Vector3 a, Vector3 b, Vector3 c)
                    {
                        float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                        float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                        float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                        return !((d1 < 0f || d2 < 0f || d3 < 0f) && (d1 > 0f || d2 > 0f || d3 > 0f));
                    }
                    Collect(walkHost);
                    float KerbOut(Vector3[] verts, (Vector3 c, Vector3 t, Vector3 o, float mid, float half) f, float d0, float d1, float sign)
                    {
                        float best = float.MaxValue;
                        foreach (var v in verts)
                        {
                            var rel = v - f.c; float al = rel.X * f.t.X + rel.Z * f.t.Z;
                            if (al < d0 - f.mid || al > d1 - f.mid) continue;
                            best = Mathf.Min(best, (rel.X * f.o.X + rel.Z * f.o.Z) * sign);
                        }
                        return best;
                    }
                    int kerbOut = 0, kerbIn = 0, controls = 0; float worstKerb = 0f;
                    foreach (var bay in bays)
                    {
                        var f = Frame(bay);
                        float plain = f.half + walkD.RoadGap;
                        if (!ribbons.TryGetValue($"Sidewalk_{(bay.Side > 0 ? "R" : "L")}{bay.Road}", out var mine)) continue;
                        float k = KerbOut(mine, f, bay.A + bay.Taper + 0.5f, bay.B - bay.Taper - 0.5f, 1f);
                        worstKerb = Mathf.Max(worstKerb, Mathf.Abs(k - (plain + bay.Depth)));
                        if (Mathf.Abs(k - (plain + bay.Depth)) < 0.02f) kerbOut++;
                        // the control: across the street, where nothing widens, the same measure finds the plain kerb
                        if (gD.BayOpposite(bay) != null) continue;
                        if (!ribbons.TryGetValue($"Sidewalk_{(bay.Side > 0 ? "L" : "R")}{bay.Road}", out var across)) continue;
                        controls++;
                        if (Mathf.Abs(KerbOut(across, f, bay.A + bay.Taper + 0.5f, bay.B - bay.Taper - 0.5f, -1f) - plain) < 0.02f) kerbIn++;
                    }
                    T.Check($"...the pavement stepping out round each, its full width, the bay's depth further out ({kerbOut} of {bays.Count}; worst {worstKerb * 100f:0.0} cm off) -- and not across the street where nothing widens ({kerbIn} of {controls} plain)",
                            kerbOut == bays.Count && controls > 0 && kerbIn == controls);
                    // ...and round its ANGLED ends it is a band of its own width, square to that kerb and following it
                    // (strawberry: "the angled parts for the parking area? dont look like they got widened", then, of a
                    // back edge squared off to the plots instead, "now theyre square?"). From every station along each
                    // angled end, a point just inside the pavement's width square to the kerb is paved wherever the
                    // plots behind have stepped back (in front of the next plot it is clipped), and a point just beyond
                    // that width is not: the back edge runs parallel to the kerb, not to the plots.
                    int bandEnds = 0, bandRight = 0, bandInside = 0, bandBeyond = 0, bandMissing = 0;
                    foreach (var bay in bays)
                    {
                        var f = Frame(bay);
                        string rn = $"Sidewalk_{(bay.Side > 0 ? "R" : "L")}{bay.Road}";
                        if (!ribbons.TryGetValue(rn, out var mine) || !ribbonTops.TryGetValue(rn, out var mineTops)) continue;
                        bool OnRibbon(Vector3 q) { foreach (var (ta, tb, tc) in mineTops) if (InTri(q, ta, tb, tc)) return true; return false; }
                        float plain = f.half + walkD.RoadGap;
                        foreach (var (d0, d1) in new[] { (bay.A, bay.A + bay.Taper), (bay.B, bay.B - bay.Taper) })
                        {
                            Vector3 K(float d) => f.c + f.t * (d - f.mid) + f.o * (plain + bay.Extra(d));
                            var k0 = K(d0); var k1 = K(d1);
                            var u = new Vector3(k1.X - k0.X, 0f, k1.Z - k0.Z); float len = u.Length(); u /= len;
                            var nrm = new Vector3(-u.Z, 0f, u.X); if (nrm.Dot(f.o) < 0f) nrm = -nrm;
                            int seen = 0; bool ok = true;
                            foreach (var v in mine)
                            {
                                var rel = new Vector3(v.X - k0.X, 0f, v.Z - k0.Z);
                                float along = rel.Dot(u), off = rel.Dot(nrm);
                                if (along < len * 0.15f || along > len * 0.85f || Mathf.Abs(off) > 0.03f) continue;
                                var kp = new Vector3(v.X, 0f, v.Z);
                                var pin = kp + nrm * (walkD.Width - 0.15f); var pout = kp + nrm * (walkD.Width + 0.15f);
                                float pinAt = (pin - f.c).Dot(f.t) + f.mid;
                                if (pinAt < bay.Front0 + 0.05f || pinAt > bay.Front1 - 0.05f) continue;   // the clipped end
                                seen++;
                                if (!OnRibbon(pin)) { ok = false; bandMissing++; } else bandInside++;
                                if (OnRibbon(pout)) { ok = false; bandBeyond++; }
                            }
                            if (seen == 0) continue;
                            bandEnds++;
                            if (ok) bandRight++;
                        }
                    }
                    T.Check($"...and round each angled end a band its own width, square to that kerb ({bandRight} of {bandEnds} angled ends; {bandInside} stations paved to within 15 cm of the width, {bandMissing} short of it, {bandBeyond} paved past it)",
                            bandEnds >= bays.Count && bandRight == bandEnds);
                    // 2. THE PLOTS BEHIND STAND BACK: no plot on a bay or its pavement -- and a plot that stepped back,
                    //    put back where it was, would be on one (the control).
                    bool OnBay(Vector3 q)
                    {
                        foreach (var bay in bays)
                        {
                            var f = Frame(bay);
                            var rel = q - f.c; float al = rel.X * f.t.X + rel.Z * f.t.Z, lat = rel.X * f.o.X + rel.Z * f.o.Z;
                            if (al < bay.A - f.mid || al > bay.B - f.mid) continue;
                            float back = Mathf.Max(bay.Extra(f.mid + al), f.mid + al >= bay.Front0 && f.mid + al <= bay.Front1 ? bay.Depth : 0f);
                            if (lat > f.half - 0.1f && lat < f.half + walkD.RoadGap + back + walkW - 0.01f) return true;
                        }
                        return false;
                    }
                    bool PlotOnBay(Vector3[] c)
                    {
                        for (int e = 0; e < 4; e++)
                        {
                            var a = c[e]; var b = c[(e + 1) % 4];
                            int n = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.X - a.X, b.Z - a.Z).Length() / 0.5f));
                            for (int k = 0; k <= n; k++) if (OnBay(a.Lerp(b, (float)k / n))) return true;
                        }
                        return false;
                    }
                    int onBay = 0, stepped = 0, unsteppedOn = 0;
                    foreach (var pl in plots)
                    {
                        if (pl.Outline != null) continue;
                        if (PlotOnBay(pl.Corners())) onBay++;
                        if (pl.Setback < gD.BayDepth - 1e-3f || pl.Kind == TownRoadGrid.PlotKind.Corner) continue;
                        stepped++;
                        var back = pl.Corners();
                        for (int k = 0; k < 4; k++) back[k] += pl.Facing * pl.Setback;
                        if (PlotOnBay(back)) unsteppedOn++;
                    }
                    T.Check($"...the plots behind each stepped back clear of it ({onBay} on a bay or its pavement; {stepped} stepped back, and {unsteppedOn} of those would be on one where they stood)",
                            onBay == 0 && stepped >= bays.Count && unsteppedOn == stepped);
                    // 3. CLEAR OF THE JUNCTIONS: no bay within BayClearOfJunction of a crossing or its stop line, nor on
                    //    a junction's slab.
                    var crossingQuads = new List<Vector3[]>();
                    for (int j = 0; j < rfD.JunctionCount; j++)
                        foreach (var (er, ee) in rfD.JunctionEdges(j))
                            if (gD.LayCrossing(rfD, j, er, ee, out var cl))
                                crossingQuads.Add(new[] { cl.At(cl.D0, -cl.Half), cl.At(cl.D0, cl.Half), cl.At(cl.SD1, cl.Half), cl.At(cl.SD1, -cl.Half) });
                    float SegDist(Vector3 p, Vector3 a, Vector3 b)
                    {
                        float abx = b.X - a.X, abz = b.Z - a.Z, l2 = abx * abx + abz * abz;
                        float tt = l2 < 1e-9f ? 0f : Mathf.Clamp(((p.X - a.X) * abx + (p.Z - a.Z) * abz) / l2, 0f, 1f);
                        return new Vector2(a.X + abx * tt - p.X, a.Z + abz * tt - p.Z).Length();
                    }
                    float ToCrossings(Vector3 q)
                    {
                        float best = float.MaxValue;
                        foreach (var cq in crossingQuads)
                        {
                            // inside the band is no distance at all
                            bool neg = false, pos = false;
                            for (int e = 0; e < 4; e++)
                            {
                                var a = cq[e]; var b = cq[(e + 1) % 4];
                                float cr = (b.X - a.X) * (q.Z - a.Z) - (b.Z - a.Z) * (q.X - a.X);
                                if (cr < 0f) neg = true; else if (cr > 0f) pos = true;
                                best = Mathf.Min(best, SegDist(q, a, b));
                            }
                            if (!(neg && pos)) return 0f;
                        }
                        return best;
                    }
                    // the nearest a bay's asphalt comes to a crossing, and how many of its points lie on a slab
                    float Nearest(TownRoadGrid.ParkingBay bay, (Vector3 c, Vector3 t, Vector3 o, float mid, float half) f, ref int onSlab)
                    {
                        float near = float.MaxValue;
                        for (float d = bay.A; d <= bay.B + 1e-3f; d += 0.5f)
                            foreach (float lat in new[] { f.half, f.half + bay.Extra(d) })
                            {
                                var q = f.c + f.t * (d - f.mid) + f.o * lat;
                                near = Mathf.Min(near, ToCrossings(q));
                                foreach (var pp in gD.Patches) if (pp.DistanceTo(q.X, -q.Z) < 0.25) { onSlab++; break; }
                            }
                        return near;
                    }
                    float nearestCrossing = float.MaxValue; int bayOnSlab = 0;
                    foreach (var bay in bays) nearestCrossing = Mathf.Min(nearestCrossing, Nearest(bay, Frame(bay), ref bayOnSlab));
                    // the control: a bay laid from the very start of a road that has a crossing there must read as on it
                    float controlNear = float.MaxValue; int controlSlab = 0;
                    for (int j = 0; j < rfD.JunctionCount && controlNear == float.MaxValue; j++)
                        foreach (var (er, ee) in rfD.JunctionEdges(j))
                        {
                            if (ee || !gD.LayCrossing(rfD, j, er, false, out _)) continue;
                            var fake = new TownRoadGrid.ParkingBay { Road = er, Side = 1, A = 0f, B = 12f, Depth = gD.BayDepth, Taper = gD.BayTaper };
                            rfD.EvaluateAlong(er, 6f, out var fc, out var ft, snapTerrain: false);
                            var tt = new Vector3(ft.X, 0f, ft.Z).Normalized();
                            controlNear = Nearest(fake, (fc, tt, new Vector3(ft.Z, 0f, -ft.X).Normalized(), 6f, rfD.RoadHalfWidth(er)), ref controlSlab);
                            break;
                        }
                    T.Check($"...each well clear of the junctions: nearest crossing or stop line {nearestCrossing:0.0} m from any bay (wants >= {gD.BayClearOfJunction - 1f:0}), {bayOnSlab} points on a junction slab -- and a bay laid at a road's very start would be {controlNear:0.0} m from one (control)",
                            crossingQuads.Count > 0 && nearestCrossing >= gD.BayClearOfJunction - 1f && bayOnSlab == 0 && controlNear < gD.BayClearOfJunction - 1f);
                    // 4. THE LINES ACROSS, AND NONE ALONG (strawberry 2026-10-13: "change the parking bays to not have dotted
                    //    lines at all, only perpendicular solid ones relative to the road's length"): a SOLID line across the
                    //    bay at each end of every space, nothing down its open side, every bit of paint inside its bay. The
                    //    control: the middle of a space reads as bare -- so "covered" is not just "near a bay".
                    var parkHost = new Node3D(); World.AddChild(parkHost);
                    gD.BuildParking(parkHost, rfD);
                    var paintTris = new List<Vector3>();
                    foreach (var ch in parkHost.GetChildren())
                        if (ch is MeshInstance3D pmi && pmi.Name.ToString() == "Parking_Bays_Paint")
                            paintTris.AddRange((Vector3[])pmi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex]);
                    bool Painted(Vector3 q)
                    {
                        for (int k = 0; k + 2 < paintTris.Count; k += 3)
                        {
                            Vector3 a = paintTris[k], b = paintTris[k + 1], c = paintTris[k + 2];
                            float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                            float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                            float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                            bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
                            if (!(neg && pos)) return true;
                        }
                        return false;
                    }
                    int strayPaint = 0;
                    foreach (var v in paintTris)
                    {
                        bool inside = false;
                        foreach (var bay in bays)
                        {
                            var f = Frame(bay);
                            var rel = v - f.c; float al = rel.X * f.t.X + rel.Z * f.t.Z, lat = rel.X * f.o.X + rel.Z * f.o.Z;
                            if (al >= bay.A - f.mid - 0.01f && al <= bay.B - f.mid + 0.01f && lat >= f.half - 0.01f && lat <= f.half + bay.Extra(f.mid + al) + 0.01f)
                            { inside = true; break; }
                        }
                        if (!inside) strayPaint++;
                    }
                    int edgeBare = 0, crossSolid = 0, crossLines = 0, spaceBare = 0;
                    Vector3 Pt(TownRoadGrid.ParkingBay bay, float d, float lat) { var f = Frame(bay); return f.c + f.t * (d - f.mid) + f.o * lat; }
                    foreach (var bay in bays)
                    {
                        float half = rfD.RoadHalfWidth(bay.Road);
                        float edge = half + gD.BayEdgeGap + gD.BayLine * 0.5f;
                        // nothing down the open side, between the spaces' lines (each line itself crosses it)
                        bool sideBare = true;
                        for (float d = bay.A + 0.3f; d <= bay.B - 0.3f && sideBare; d += 0.05f)
                        {
                            float nearLine = float.MaxValue;
                            for (int k = 0; k <= bay.Spaces; k++) nearLine = Mathf.Min(nearLine, Mathf.Abs(d - (bay.First + k * bay.SpaceLength)));
                            if (nearLine > gD.BayLine && Painted(Pt(bay, d, edge))) sideBare = false;
                        }
                        if (sideBare) edgeBare++;
                        for (int k = 0; k <= bay.Spaces; k++)
                        {
                            crossLines++;
                            float d = bay.First + k * bay.SpaceLength;
                            int con = 0, call = 0;
                            for (float lat = edge + 0.1f; lat <= half + bay.Depth - 0.3f; lat += 0.05f) { call++; if (Painted(Pt(bay, d, lat))) con++; }
                            if (call > 0 && con == call) crossSolid++;
                        }
                        if (!Painted(Pt(bay, bay.First + bay.SpaceLength * 0.5f, half + bay.Depth * 0.5f))) spaceBare++;
                    }
                    T.Check($"...marked with a solid line across at each end of every space ({crossSolid} of {crossLines}), nothing down its open side ({edgeBare} of {bays.Count} bare), every bit of paint inside its bay ({strayPaint} vertices stray) -- and the middle of a space bare ({spaceBare} of {bays.Count})",
                            paintTris.Count > 0 && crossSolid == crossLines && edgeBare == bays.Count && strayPaint == 0 && spaceBare == bays.Count);
                    walkHost.QueueFree(); parkHost.QueueFree();
                }
                // ⭐ WIDE PAVEMENTS (strawberry 2026-10-12: "add support for wider sidewalks ... just make them twice as
                // wide everywhere"). Measured on the built ribbons: each straight run is the town's width; no pavement
                // lies over another anywhere (the wide ones crossed at every acute corner before their corner piece
                // began -- two surfaces at one height); and no corner piece folds over itself where its kerb turns
                // tighter than the pavement is wide.
                {
                    var stHost = new Node3D(); World.AddChild(stHost);
                    var cnHost = new Node3D(); World.AddChild(cnHost);
                    gD.LaySidewalks(stHost, rfD);
                    gD.LaySidewalkCorners(cnHost, rfD, walkD);
                    // the back lanes, built into the same set: every check below holds their pavements, kerb returns and
                    // closed ends to the same rules as the streets'
                    gD.BuildLanes(cnHost, rfD);
                    // a mesh's walking surface: its level triangles, indexed or not
                    List<(Vector3 a, Vector3 b, Vector3 c, float ny)> Tops(MeshInstance3D mi)
                    {
                        var arr = mi.Mesh.SurfaceGetArrays(0);
                        var vs = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                        var ix = arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : (int[])arr[(int)Mesh.ArrayType.Index];
                        var outp = new List<(Vector3, Vector3, Vector3, float)>();
                        int n = ix != null ? ix.Length : vs.Length;
                        for (int k = 0; k + 2 < n; k += 3)
                        {
                            Vector3 a = vs[ix != null ? ix[k] : k], b = vs[ix != null ? ix[k + 1] : k + 1], c = vs[ix != null ? ix[k + 2] : k + 2];
                            var nn = (b - a).Cross(c - a);
                            // ⚠ NOT the slivers: where a corner's back edge meets in a point, two of a quad's corners land
                            // on it a float's width apart, and a triangle of no area has no meaningful way up
                            if (nn.Length() < 1e-3f || Mathf.Abs(nn.Y) < 0.9f * nn.Length()) continue;
                            outp.Add((a, b, c, nn.Y));
                        }
                        return outp;
                    }
                    var meshNodes = new List<MeshInstance3D>();
                    void GatherNodes(Node n)
                    {
                        if (n is MeshInstance3D mi && mi.Mesh != null && mi.Name.ToString().StartsWith("Sidewalk_")) meshNodes.Add(mi);
                        foreach (var ch in n.GetChildren()) GatherNodes(ch);
                    }
                    GatherNodes(stHost); GatherNodes(cnHost);
                    // 1. as wide as the town says: across one station of each straight run, kerb to back edge
                    int straights = 0, rightWidth = 0, laneStraights = 0; float worstWidth = 0f;
                    foreach (var mi in meshNodes)
                    {
                        string nm = mi.Name.ToString();
                        if (!nm.StartsWith("Sidewalk_R") && !nm.StartsWith("Sidewalk_L")) continue;
                        string num = nm.Substring(10); int us = num.IndexOf('_'); if (us >= 0) num = num.Substring(0, us);   // "_p1": one piece of a side
                        if (!int.TryParse(num, out int road)) continue;
                        // a back lane's pavement is its own, narrower width
                        float wantW = gD.LaneRoads.Contains(road) ? gD.LaneWalkWidth : gD.PavementWidth;
                        float L = rfD.RoadLength(road);
                        int rside = nm[9] == 'R' ? 1 : -1;
                        if (gD.BayExtra(road, rside, L * 0.5f - 4f) > 0f || gD.BayExtra(road, rside, L * 0.5f) > 0f
                            || gD.BayExtra(road, rside, L * 0.5f + 4f) > 0f) continue;     // a bay's step: measured with the bays
                        if (!rfD.EvaluateAlong(road, L * 0.5f, out var mc, out var mt, snapTerrain: false)) continue;
                        // straight runs only: across a rounded corner one station's kerb and back are not square to the middle's tangent
                        if (!rfD.EvaluateAlong(road, L * 0.5f - 5f, out _, out var ma, snapTerrain: false) || !rfD.EvaluateAlong(road, L * 0.5f + 5f, out _, out var mb, snapTerrain: false)
                            || ma.Normalized().Dot(mb.Normalized()) < Mathf.Cos(Mathf.DegToRad(0.5f))) continue;
                        var t = new Vector3(mt.X, 0f, mt.Z).Normalized(); var o = new Vector3(mt.Z, 0f, -mt.X).Normalized();
                        var vs = (Vector3[])mi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
                        float nearAl = float.MaxValue;
                        foreach (var v in vs) { float al = (v - mc).Dot(t); if (Mathf.Abs(al) < Mathf.Abs(nearAl)) nearAl = al; }
                        float lo = float.MaxValue, hi = float.MinValue;
                        foreach (var v in vs)
                        {
                            var rel = v - mc;
                            if (Mathf.Abs(rel.Dot(t) - nearAl) > 0.02f) continue;
                            float lat = Mathf.Abs(rel.X * o.X + rel.Z * o.Z);
                            lo = Mathf.Min(lo, lat); hi = Mathf.Max(hi, lat);
                        }
                        if (hi < lo) continue;
                        straights++;
                        if (gD.LaneRoads.Contains(road)) laneStraights++;
                        worstWidth = Mathf.Max(worstWidth, Mathf.Abs(hi - lo - wantW));
                        if (Mathf.Abs(hi - lo - wantW) < 0.02f) rightWidth++;
                    }
                    T.Check($"pavements are the town's {gD.PavementWidth:0.0} m wide, a back lane's {gD.LaneWalkWidth:0.0} m ({rightWidth} of {straights} straight runs, {laneStraights} of them a lane's; worst {worstWidth * 100f:0.0} cm off)",
                            gD.PavementWidth >= 3.99f && straights > 20 && laneStraights > 4 && rightWidth == straights);
                    // 2. nowhere does one pavement lie over another: a point well inside one mesh's walking surface is
                    //    inside no other's. Bucketed by 2 m cells; the control is a ribbon laid over a copy of itself.
                    var tris = new List<(int mesh, Vector3 a, Vector3 b, Vector3 c)>();
                    for (int m = 0; m < meshNodes.Count; m++) foreach (var (a, b, c, _) in Tops(meshNodes[m])) tris.Add((m, a, b, c));
                    string firstOver = "";
                    int Overlapping(List<(int mesh, Vector3 a, Vector3 b, Vector3 c)> ts)
                    {
                        firstOver = "";
                        var grid = new Dictionary<(int, int), List<int>>();
                        for (int k = 0; k < ts.Count; k++)
                        {
                            var (_, a, b, c) = ts[k];
                            int x0 = Mathf.FloorToInt(Mathf.Min(a.X, Mathf.Min(b.X, c.X)) / 2f), x1 = Mathf.FloorToInt(Mathf.Max(a.X, Mathf.Max(b.X, c.X)) / 2f);
                            int z0 = Mathf.FloorToInt(Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) / 2f), z1 = Mathf.FloorToInt(Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)) / 2f);
                            for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                            { if (!grid.TryGetValue((x, z), out var l)) grid[(x, z)] = l = new List<int>(); l.Add(k); }
                        }
                        static bool Inside(Vector3 q, Vector3 a, Vector3 b, Vector3 c)
                        {
                            float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                            float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                            float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                            return d1 > 0f && d2 > 0f && d3 > 0f || d1 < 0f && d2 < 0f && d3 < 0f;
                        }
                        int hits = 0;
                        for (int k = 0; k < ts.Count; k++)
                        {
                            var (mk, a, b, c) = ts[k];
                            var g = (a + b + c) / 3f;
                            bool hit = false;
                            foreach (var q in new[] { g, a.Lerp(g, 0.6f), b.Lerp(g, 0.6f), c.Lerp(g, 0.6f) })
                            {
                                if (!grid.TryGetValue((Mathf.FloorToInt(q.X / 2f), Mathf.FloorToInt(q.Z / 2f)), out var l)) continue;
                                foreach (int o2 in l)
                                {
                                    var (mo, oa, ob, oc) = ts[o2];
                                    if (mo == mk || !Inside(q, oa, ob, oc)) continue;
                                    hit = true;
                                    if (firstOver == "" && mk < meshNodes.Count && mo < meshNodes.Count)
                                        firstOver = $"{meshNodes[mk].Name} over {meshNodes[mo].Name} at ({q.X:0.0}, {q.Z:0.0})";
                                    break;
                                }
                                if (hit) break;
                            }
                            if (hit) hits++;
                        }
                        return hits;
                    }
                    int over = Overlapping(tris);
                    string overAt = firstOver;
                    var dup = new List<(int, Vector3, Vector3, Vector3)>();
                    foreach (var (m, a, b, c) in tris) if (m == 0) { dup.Add((0, a, b, c)); dup.Add((1, a + new Vector3(0.5f, 0f, 0.5f), b + new Vector3(0.5f, 0f, 0.5f), c + new Vector3(0.5f, 0f, 0.5f))); }
                    int dupOver = Overlapping(dup);
                    T.Check($"...and no pavement lies over another ({over} of {tris.Count} surface triangles do{(over > 0 ? $", first {overAt}" : "")}; control: a ribbon over a shifted copy of itself, {dupOver} of {dup.Count})",
                            tris.Count > 500 && over == 0 && dupOver > 0);
                    // 2b. and no plot stands on one -- the BUILT walking surfaces, straights, corners and parking steps
                    //     alike, not the arithmetic that placed the plots (a pavement mitred round a bay's angled kerb
                    //     ran a metre into the next garden, and every check that modelled the pavement agreed with
                    //     the code that laid it). The control: a plot slid 3 m toward its street is on one.
                    var paveGrid = new Dictionary<(int, int), List<int>>();
                    for (int k = 0; k < tris.Count; k++)
                    {
                        var (_, a, b, c) = tris[k];
                        int x0 = Mathf.FloorToInt(Mathf.Min(a.X, Mathf.Min(b.X, c.X)) / 2f), x1 = Mathf.FloorToInt(Mathf.Max(a.X, Mathf.Max(b.X, c.X)) / 2f);
                        int z0 = Mathf.FloorToInt(Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) / 2f), z1 = Mathf.FloorToInt(Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)) / 2f);
                        for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                        { if (!paveGrid.TryGetValue((x, z), out var l)) paveGrid[(x, z)] = l = new List<int>(); l.Add(k); }
                    }
                    string paveHit = "";
                    bool OnPave(Vector3 q)
                    {
                        if (!paveGrid.TryGetValue((Mathf.FloorToInt(q.X / 2f), Mathf.FloorToInt(q.Z / 2f)), out var l)) return false;
                        foreach (int k in l)
                        {
                            var (who, a, b, c) = tris[k];
                            float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                            float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                            float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                            if (d1 > 0f && d2 > 0f && d3 > 0f || d1 < 0f && d2 < 0f && d3 < 0f) { paveHit = $"{meshNodes[who].Name} at {q}"; return true; }
                        }
                        return false;
                    }
                    bool PlotOnPave(Vector3[] c)
                    {
                        for (int e = 0; e < c.Length; e++)
                        {
                            var a = c[e]; var b = c[(e + 1) % c.Length];
                            int n = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.X - a.X, b.Z - a.Z).Length() / 0.5f));
                            for (int k = 0; k <= n; k++) if (OnPave(a.Lerp(b, (float)k / n))) return true;
                        }
                        return false;
                    }
                    int plotsOnPave = 0; string firstOnPave = "";
                    foreach (var pl in plots)
                        if (PlotOnPave(pl.Boundary()))
                        {
                            if (plotsOnPave++ == 0) firstOnPave = $"; first: a {pl.Kind} grown {pl.GrowLeft:0.0}/{pl.GrowRight:0.0}/{pl.GrowBack:0.0} m (left/right/back), on {paveHit}";
                        }
                    var slid = plots[0].Corners();
                    for (int k = 0; k < 4; k++) slid[k] += plots[0].Facing * 3f;
                    T.Check($"...and no plot stands on a built pavement ({plotsOnPave} of {plots.Count} do{firstOnPave}; control: a plot slid 3 m toward its street {(PlotOnPave(slid) ? "is" : "IS NOT")})",
                            plotsOnPave == 0 && PlotOnPave(slid));
                    // 3. no corner folds: every level triangle of a corner piece faces the same way. The control is a
                    //    ribbon laid round a 2.5 m arc at the town's width with nothing to stop it folding.
                    int Folded(List<(Vector3 a, Vector3 b, Vector3 c, float ny)> tops)
                    {
                        int up = 0, down = 0;
                        foreach (var (_, _, _, ny) in tops) { if (ny > 0f) up++; else down++; }
                        return Mathf.Min(up, down);
                    }
                    int folded = 0, cornerPieces = 0;
                    foreach (var mi in meshNodes)
                        if (mi.Name.ToString().StartsWith("Sidewalk_Corner_") || mi.Name.ToString().StartsWith("Sidewalk_LaneMouth_"))
                        { cornerPieces++; if (Folded(Tops(mi)) > 0) folded++; }
                    var arcP = new List<Vector3>(); var arcO = new List<Vector3>();
                    for (int k = 0; k <= 12; k++)
                    {
                        float ang = Mathf.Pi * 0.75f * k / 12f;
                        arcP.Add(new Vector3(Mathf.Cos(ang) * 2.5f, 0f, Mathf.Sin(ang) * 2.5f));
                        arcO.Add(new Vector3(-Mathf.Cos(ang), 0f, -Mathf.Sin(ang)));          // toward the centre, as a corner's is
                    }
                    var foldMi = EditorSidewalk.BuildRibbon(arcP, arcO, gD.PavementWidth, 0.4f, 0.6f, "fold_control");
                    int foldControl = foldMi != null ? Folded(Tops(foldMi)) : 0;
                    T.Check($"...and no corner piece folds over itself ({folded} of {cornerPieces} do; control: {foldControl} folded triangles in a {gD.PavementWidth:0.0} m ribbon round a 2.5 m arc)",
                            cornerPieces > 20 && folded == 0 && foldControl > 0);
                    foldMi?.Free();
                    // 4. EVERY WALKING SURFACE SHADES LEVEL: each corner of a level face carries a normal pointing up (or down, a
                    //    ribbon wound the other way) -- never tipped. A lane mouth's corner once held a hair-thin triangle wound
                    //    backwards where its fan met its straight, a millimetre off the fan's point; GenerateNormals normalises
                    //    every face before it averages, so that sliver tipped the vertices it shared, and a corner whose heights
                    //    read level to the millimetre shaded with a dark wedge and a seam (strawberry 2026-10-13). The control: a
                    //    ribbon with one station's outward turned round folds there, and this sees its normals tip.
                    int Tipped(MeshInstance3D mi)
                    {
                        var arr = mi.Mesh.SurfaceGetArrays(0);
                        var vs = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                        var ns = (Vector3[])arr[(int)Mesh.ArrayType.Normal];
                        var ix = arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : (int[])arr[(int)Mesh.ArrayType.Index];
                        int n = ix != null ? ix.Length : vs.Length, bad = 0;
                        for (int k = 0; k + 2 < n; k += 3)
                        {
                            int i0 = ix != null ? ix[k] : k, i1 = ix != null ? ix[k + 1] : k + 1, i2 = ix != null ? ix[k + 2] : k + 2;
                            var nn = (vs[i1] - vs[i0]).Cross(vs[i2] - vs[i0]);
                            if (nn.Length() < 1e-3f || Mathf.Abs(nn.Y) < 0.9f * nn.Length()) continue;      // not a level face
                            foreach (int iv in new[] { i0, i1, i2 }) if (Mathf.Abs(ns[iv].Y) < 0.9f) bad++;
                        }
                        return bad;
                    }
                    int tipped = 0, tippedMeshes = 0, lanePieces = 0; string firstTipped = "";
                    foreach (var mi in meshNodes)
                    {
                        if (mi.Name.ToString().StartsWith("Sidewalk_Lane")) lanePieces++;
                        int t = Tipped(mi);
                        if (t == 0) continue;
                        tipped += t;
                        if (tippedMeshes++ == 0) firstTipped = mi.Name.ToString();
                    }
                    var tipPts = new List<Vector3>(); var tipOut = new List<Vector3>();
                    for (int k = 0; k < 5; k++) { tipPts.Add(new Vector3(0f, 0f, k * 1.5f)); tipOut.Add(k == 2 ? Vector3.Left : Vector3.Right); }
                    var tipMi = EditorSidewalk.BuildRibbon(tipPts, tipOut, 2f, 0.4f, 0.6f, "tip_control");
                    int tipControl = tipMi != null ? Tipped(tipMi) : 0;
                    tipMi?.Free();
                    T.Check($"...and every walking surface shades level ({tipped} tipped normals on {tippedMeshes} of {meshNodes.Count} pavement meshes{(tippedMeshes > 0 ? $", first {firstTipped}" : "")}; {lanePieces} of them the lanes' kerb returns and closed ends) -- where a ribbon folded at one station tips {tipControl} (control)",
                            tipped == 0 && lanePieces > 20 && tipControl > 0);
                    // ⭐ THE BACK LANES (strawberry 2026-10-13: "replace placeholder alleys with single lane roads with short
                    //    sidewalks/kerbs and no markings"), measured on the meshes BuildLanes built -- which the checks above
                    //    already hold: no lane pavement lies over a street's, no plot stands on one, no kerb return folds.
                    // a. one single-lane road per lane, LaneHalf a side, a lane and not a street
                    //    (a parking lot's way in is built as one too -- two lanes wide, open into the lot at its far end, with no
                    //    pavements of its own: the lot's kerb curves round to meet its mouth's returns. TownParking.)
                    int laneRoadsOk = 0, waysIn = 0, waysInOk = 0;
                    foreach (var lp in gD.LanePlans)
                    {
                        int lr = lp.Road;
                        if (lr < 0 || gD.IsMain(rfD, lr) || gD.Built.Contains(lr)) continue;
                        if (lp.OpenB) { waysIn++; if (Mathf.Abs(rfD.RoadHalfWidth(lr) - gD.ParkingEntranceHalf) < 0.01f) waysInOk++; }
                        else if (Mathf.Abs(rfD.RoadHalfWidth(lr) - gD.LaneHalf) < 0.01f) laneRoadsOk++;
                    }
                    T.Check($"the alleys are single-lane roads now ({gD.LaneRoads.Count} lanes from {gD.Alleys.Count} alleys, {laneRoadsOk} of them {gD.LaneHalf * 2f:0.0} m of carriageway; {waysInOk} of {waysIn} parking lots' ways in {gD.ParkingEntranceHalf * 2f:0.0} m)",
                            gD.LaneRoads.Count >= 6 && gD.LaneRoads.Count == gD.LanePlans.Count && laneRoadsOk + waysIn == gD.LaneRoads.Count && waysInOk == waysIn);
                    // b. each opens off its street THROUGH the street's pavement: along its centreline, from the asphalt's edge
                    //    to where its own pavements begin, there is no pavement at all and it is road all the way -- while the
                    //    street's pavement carries on either side of the opening (2 m past each end of the cut, the control)
                    int mouthsOpen = 0, mouthsRoad = 0, sidesPaved = 0, sidesTried = 0;
                    foreach (var m in gD.LaneMouths)
                    {
                        float upTo = Mathf.Min(m.LaneFrom[0], m.LaneFrom[1]) - 0.3f;
                        bool open = true, isRoad = true;
                        for (float u = 0.3f; u <= upTo; u += 0.25f)
                        {
                            var q = m.Edge + m.Into * u;
                            if (OnPave(q)) open = false;
                            if (!rfD.DeepestRoadOverlap(q, 0.05f, out _, out _)) isRoad = false;
                        }
                        if (open) mouthsOpen++;
                        if (isRoad) mouthsRoad++;
                        foreach (float d in new[] { m.From - 2f, m.To + 2f })
                        {
                            if (!rfD.EvaluateAlong(m.Road, d, out var sp, out var st, snapTerrain: false)) continue;
                            var so = new Vector3(st.Z, 0f, -st.X).Normalized() * m.Side;
                            sidesTried++;
                            if (OnPave(sp + so * (rfD.RoadHalfWidth(m.Road) + walkD.RoadGap + gD.PavementWidth * 0.5f))) sidesPaved++;
                        }
                    }
                    T.Check($"...each opening off its street through the pavement ({mouthsOpen} of {gD.LaneMouths.Count} mouths with no pavement across them, {mouthsRoad} road all the way in) -- the street's pavement carrying on either side ({sidesPaved} of {sidesTried})",
                            gD.LaneMouths.Count >= 6 && mouthsOpen == gD.LaneMouths.Count && mouthsRoad == gD.LaneMouths.Count && sidesTried > 0 && sidesPaved == sidesTried);
                    // c. KERBS AS HIGH AS THE STREETS' (strawberry 2026-10-13: "kerb height should stay the same as the other
                    //    roads too"): down the lane its pavement stands as far over THE LANE'S OWN ASPHALT as a street's
                    //    pavement, MEASURED off the street's own mesh, stands over the street's; each kerb return stands at the
                    //    street's PavementKerb, level -- within a centimetre -- with the street's pavement on one side of it and
                    //    the lane's on the other.
                    //    ⚠⚠ FROM THE ASPHALT, NOT THE KERB'S FOOT ("make sure the alleys and their kerbs arent sinking below
                    //    the road level"). This check first measured the pavement over the kerb's foot, which stands 0.22 m
                    //    BELOW a carriageway's surface in game -- and passed a lane kerb 2 cm under its own road.
                    static float? LevelAt(List<(Vector3 a, Vector3 b, Vector3 c, float ny)> ts, Vector3 q)
                    {
                        foreach (var (a, b, c, _) in ts)
                        {
                            float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                            if (Mathf.Abs(det) < 1e-9f) continue;
                            float l1 = ((b.Z - c.Z) * (q.X - c.X) + (c.X - b.X) * (q.Z - c.Z)) / det;
                            float l2 = ((c.Z - a.Z) * (q.X - c.X) + (a.X - c.X) * (q.Z - c.Z)) / det;
                            float l3 = 1f - l1 - l2;
                            if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                            return l1 * a.Y + l2 * b.Y + l3 * c.Y;
                        }
                        return null;
                    }
                    var topsOf = new Dictionary<MeshInstance3D, List<(Vector3 a, Vector3 b, Vector3 c, float ny)>>();
                    List<(Vector3 a, Vector3 b, Vector3 c, float ny)> TopsOf(MeshInstance3D mi) => topsOf.TryGetValue(mi, out var t) ? t : topsOf[mi] = Tops(mi);
                    var byName = new Dictionary<string, MeshInstance3D>();
                    foreach (var mi in meshNodes) byName[mi.Name.ToString()] = mi;
                    float? LevelOn(IEnumerable<string> names, Vector3 q)
                    {
                        foreach (var nm in names) if (byName.TryGetValue(nm, out var mi)) { var h = LevelAt(TopsOf(mi), q); if (h != null) return h; }
                        return null;
                    }
                    IEnumerable<string> SideNames(int road, int side)
                    {
                        string b = $"Sidewalk_{(side > 0 ? "R" : "L")}{road}";
                        yield return b;
                        for (int k = 0; k < 8; k++) yield return $"{b}_p{k}";
                    }
                    int lowOk = 0, lowTried = 0; float worstLow = 0f, lowestProud = float.MaxValue;
                    // the streets' kerb, off the streets' own pavements: the middle of the first few with nothing at their middles
                    float streetProud = 0f; int streetsMeasured = 0;
                    foreach (int r in gD.Built)
                    {
                        if (streetsMeasured >= 4) break;
                        float L = rfD.RoadLength(r);
                        if (gD.BayExtra(r, 1, L * 0.5f) > 0f || gD.MouthAt(r, 1, L * 0.5f - 2f, L * 0.5f + 2f) != null) continue;
                        rfD.EvaluateAlong(r, L * 0.5f, out var sp, out var st);
                        var so = new Vector3(st.Z, 0f, -st.X).Normalized();
                        var h = LevelOn(SideNames(r, 1), sp + so * (rfD.RoadHalfWidth(r) + walkD.RoadGap + gD.PavementWidth * 0.5f));
                        if (h == null) continue;
                        streetProud += h.Value - (sp.Y + rfD.RoadSurfaceOffset(r)); streetsMeasured++;
                    }
                    streetProud = streetsMeasured > 0 ? streetProud / streetsMeasured : float.NaN;
                    foreach (var lp in gD.LanePlans)
                    {
                        if (lp.OpenB) continue;                    // (a parking lot's way in has no pavements of its own)
                        float L = rfD.RoadLength(lp.Road);
                        rfD.EvaluateAlong(lp.Road, L * 0.5f, out var mp, out var mt);
                        var rt = new Vector3(mt.Z, 0f, -mt.X).Normalized();
                        float asphalt = mp.Y + rfD.RoadSurfaceOffset(lp.Road);          // the lane's own surface there
                        foreach (int sgn in new[] { 1, -1 })
                        {
                            lowTried++;
                            var h = LevelOn(SideNames(lp.Road, sgn), mp + rt * sgn * (gD.LaneHalf + walkD.RoadGap + gD.LaneWalkWidth * 0.5f));
                            if (h == null) continue;
                            float proud = h.Value - asphalt;
                            lowestProud = Mathf.Min(lowestProud, proud);
                            worstLow = Mathf.Max(worstLow, Mathf.Abs(proud - streetProud));
                            if (Mathf.Abs(proud - streetProud) < 0.01f) lowOk++;
                        }
                    }
                    int seams = 0, seamsLevel = 0, atStreetHeight = 0; float worstSeam = 0f;
                    foreach (var lp in gD.LanePlans)
                        foreach (var (m, end) in new[] { (lp.A, "A"), (lp.B, "B") })
                        {
                            if (m == null) continue;
                            for (int c = 0; c < 2; c++)
                            {
                                seams++;
                                if (!byName.TryGetValue($"Sidewalk_LaneMouth_{lp.Road}_{end}{c}", out var corner)) continue;
                                rfD.EvaluateAlong(m.Road, m.StreetArc[c], out var sf, out var stn);
                                var ts = new Vector3(stn.X, 0f, stn.Z).Normalized();
                                var so = new Vector3(stn.Z, 0f, -stn.X).Normalized() * m.Side;
                                float sLat = rfD.RoadHalfWidth(m.Road) + walkD.RoadGap + gD.PavementWidth * 0.5f;
                                float inward = Mathf.Abs(m.StreetArc[c] - m.From) < 1e-3f ? 0.05f : -0.05f;   // toward the mouth
                                var hc = LevelAt(TopsOf(corner), sf + so * sLat + ts * inward);
                                var hs = LevelOn(SideNames(m.Road, m.Side), sf + so * sLat - ts * inward);
                                if (hc != null && Mathf.Abs(hc.Value - sf.Y - gD.PavementKerb) < 0.01f) atStreetHeight++;
                                var lat = new Vector3(-m.Into.Z, 0f, m.Into.X) * (c == 0 ? 1f : -1f) * (gD.LaneHalf + walkD.RoadGap + gD.LaneWalkWidth * 0.5f);
                                var hlc = LevelAt(TopsOf(corner), m.Edge + m.Into * (m.LaneFrom[c] - 0.05f) + lat);
                                var hll = LevelOn(SideNames(lp.Road, 1).Concat(SideNames(lp.Road, -1)), m.Edge + m.Into * (m.LaneFrom[c] + 0.05f) + lat);
                                // (a parking lot's way in: no lane pavement up it -- its return meets the lot's kerb curve there)
                                bool level = hc != null && hs != null && (lp.OpenB || hlc != null && hll != null);
                                if (level)
                                {
                                    float step = Mathf.Max(Mathf.Abs(hc.Value - hs.Value), lp.OpenB ? 0f : Mathf.Abs(hlc.Value - hll.Value));
                                    worstSeam = Mathf.Max(worstSeam, step);
                                    level = step < 0.01f;
                                }
                                if (level) seamsLevel++;
                            }
                        }
                    T.Check($"...with kerbs as high as the streets': the lanes' pavements stand over their own asphalt as the streets' do over theirs, {streetProud * 100f:0.0} cm measured on {streetsMeasured} streets ({lowOk} of {lowTried} measured mid-lane; lowest {lowestProud * 100f:0.0} cm, worst {worstLow * 100f:0.0} cm off); each kerb return at the street's {gD.PavementKerb:0.00} m over its foot ({atStreetHeight} of {seams}) and level with the street's pavement and the lane's where they meet ({seamsLevel} of {seams}; worst step {worstSeam * 100f:0.0} cm)",
                            streetsMeasured > 0 && lowTried >= 12 && lowOk == lowTried && lowestProud > 0.05f
                            && seams == 2 * gD.LaneMouths.Count && atStreetHeight == seams && seamsLevel == seams);
                    // d. a lane end that cannot open onto a street is closed off with a kerb across it, never left as road ending
                    //    in the grass
                    int shortEnds = 0, endKerbs = 0;
                    foreach (var lp in gD.LanePlans) { if (lp.A == null && !lp.KneeA) shortEnds++; if (lp.B == null && !lp.KneeB && !lp.OpenB) shortEnds++; }
                    foreach (var mi in meshNodes) if (mi.Name.ToString().StartsWith("Sidewalk_LaneEnd_")) endKerbs++;
                    T.Check($"...and every lane end that cannot open onto a street closed off with a kerb ({endKerbs} of {shortEnds})", endKerbs == shortEnds);
                    // d2. HARD 90s FOR THE ALLEYS (strawberry 2026-10-13: "make hard 90s with both the road and the sidewalks
                    //     possible ... for alleys ... as an option you can toggle on/off"). Each L's two halves meet at their knee:
                    //     their ASPHALT -- the ribbons as built -- neither overlaps (two coplanar surfaces) nor leaves the outside
                    //     of the turn bare, and their PAVEMENTS carry on across the line they meet on, both sides of the turn.
                    //     The control: the same two straights NOT joined at a node, each ending square as a lone road does,
                    //     overlap on the inside of the turn and leave the outside bare.
                    static List<(Vector3 a, Vector3 b, Vector3 c)> RibbonTop(Mesh m)
                    {
                        var outp = new List<(Vector3, Vector3, Vector3)>();
                        if (m == null) return outp;
                        var arr = m.SurfaceGetArrays(0);
                        var vs = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                        var ix = arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : (int[])arr[(int)Mesh.ArrayType.Index];
                        int n = ix != null ? ix.Length : vs.Length;
                        for (int k = 0; k + 2 < n; k += 3)
                        {
                            Vector3 a = vs[ix != null ? ix[k] : k], b = vs[ix != null ? ix[k + 1] : k + 1], c = vs[ix != null ? ix[k + 2] : k + 2];
                            var nn = (b - a).Cross(c - a);
                            if (nn.Length() > 1e-4f && Mathf.Abs(nn.Y) > 0.9f * nn.Length()) outp.Add((a, b, c));
                        }
                        return outp;
                    }
                    static bool OnTop(List<(Vector3 a, Vector3 b, Vector3 c)> ts, Vector3 q)
                    {
                        foreach (var (a, b, c) in ts)
                        {
                            float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                            float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                            float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                            if (d1 > 0f && d2 > 0f && d3 > 0f || d1 < 0f && d2 < 0f && d3 < 0f) return true;
                        }
                        return false;
                    }
                    // over the corner's square: points on BOTH halves' asphalt, and the turn's outer corner left bare
                    (int onBoth, int bareOuter) Knee(List<(Vector3, Vector3, Vector3)> t1, List<(Vector3, Vector3, Vector3)> t2, Vector3 knee, Vector3 into, Vector3 outp, float half)
                    {
                        int onBoth = 0, bare = 0;
                        var side = new Vector3(-into.Z, 0f, into.X);
                        // (the v grid sits half a step off the u grid, so no sample lands exactly ON the mitre's diagonal)
                        for (float u = -half + 0.15f; u <= half - 0.15f; u += 0.25f)
                            for (float v = -half + 0.275f; v <= half - 0.15f; v += 0.25f)
                            {
                                var q = knee + into * u + side * v;
                                if (OnTop(t1, q) && OnTop(t2, q)) onBoth++;
                            }
                        foreach (int so in new[] { 1, -1 })
                        {
                            var corner = RoadField.CornerPoint(knee, into, outp, half, half, so);
                            var toKnee = new Vector3(knee.X - corner.X, 0f, knee.Z - corner.Z).Normalized();
                            var q = corner + toKnee * 0.3f;
                            if (!OnTop(t1, q) && !OnTop(t2, q)) bare++;
                        }
                        return (onBoth, bare);
                    }
                    int kneesSeen = 0, kneeOverlaps = 0, kneeBare = 0, mitresPaved = 0, mitresTried = 0;
                    foreach (var lp in gD.LanePlans)
                    {
                        if (!lp.KneeB || lp.Partner == null) continue;
                        kneesSeen++;
                        var knee = lp.Pts[^1];
                        var into = lp.Pts[^1] - lp.Pts[^2]; into.Y = 0f; into = into.Normalized();
                        var outp = lp.Partner.Pts[1] - lp.Partner.Pts[0]; outp.Y = 0f; outp = outp.Normalized();
                        var (kOver, kBare) = Knee(RibbonTop(rfD.RoadMeshOf(lp.Road)), RibbonTop(rfD.RoadMeshOf(lp.Partner.Road)), knee, into, outp, gD.LaneHalf);
                        kneeOverlaps += kOver; kneeBare += kBare;
                        // each side's pavement on both sides of the line the halves meet on, halfway across it
                        float kOff = gD.LaneHalf + walkD.RoadGap;
                        foreach (int so in new[] { 1, -1 })
                        {
                            var k = RoadField.CornerPoint(knee, into, outp, kOff, kOff, so);
                            var b = RoadField.CornerPoint(knee, into, outp, kOff + gD.LaneWalkWidth, kOff + gD.LaneWalkWidth, so);
                            var mid = (k + b) * 0.5f;
                            var along = new Vector3(b.Z - k.Z, 0f, -(b.X - k.X)).Normalized();     // across the mitre line
                            mitresTried++;
                            if (OnPave(mid + along * 0.15f) && OnPave(mid - along * 0.15f)) mitresPaved++;
                        }
                    }
                    // the control: the same corner from two lone straights, ending square
                    var rfK = Field();
                    var kneeK = new Vector3(0f, 40f, 0f);
                    int ra = rfK.AddRoadFromPolyline(new List<Vector3> { kneeK + new Vector3(-30f, 0f, 0f), kneeK + new Vector3(-15f, 0f, 0f), kneeK }, 6);
                    int rb = rfK.AddRoadFromPolyline(new List<Vector3> { kneeK, kneeK + new Vector3(0f, 0f, 15f), kneeK + new Vector3(0f, 0f, 30f) }, 6);
                    var (bothK, bareK) = Knee(RibbonTop(rfK.RoadMeshOf(ra)), RibbonTop(rfK.RoadMeshOf(rb)), kneeK, new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), rfK.RoadHalfWidth(ra));
                    T.Check($"...and every L-turn a hard 90 ({kneesSeen} knees): the two halves' asphalt overlapping at {kneeOverlaps} points of the corner and leaving {kneeBare} of its corners bare, their pavements carrying on across the line they meet on ({mitresPaved} of {mitresTried} sides) -- where two lone straights at the same corner overlap at {bothK} and leave {bareK} bare (control)",
                            gD.LaneHardCorners && kneesSeen >= 2 && kneeOverlaps == 0 && kneeBare == 0 && mitresPaved == mitresTried && bothK > 0 && bareK > 0);
                    // e. ON ROADS WITH DEPTH, AS THE GAME'S ARE. This rig's roads are flat -- their asphalt is AT the kerb's
                    //    foot -- so here a kerb counted from its foot and one counted from the asphalt read the same, and the
                    //    first lanes passed (c) with kerbs that stood 2 cm UNDER their own road in game, where the asphalt is
                    //    0.22 m over the foot. So the same town again on a field with that depth: the lane's pavement still
                    //    stands over its asphalt as the street's pavements there stand over theirs. The control: a pavement
                    //    laid the old way, 0.2 m over its foot, on the same lane, reads as under it.
                    {
                        var rfL = Field();
                        foreach (int mat in new[] { 0, 1, 6 }) rfL.DebugSetMaterialDepth(mat, 0.22f);
                        // ...and as the game's town is: its streets holding their own profile, the ground then cut 0.12 m
                        // below them (GradeTerrain) before anything else is laid -- a sampler that drops once they are in
                        // (below the streets as LAID, which is StreetSink under the ground they were profiled off)
                        float groundL = 40f;
                        Func<float, float, float> liveL = (x, z) => groundL;
                        var gL = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                                    Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true, FollowTerrain = false,
                                                    AlleysOnRequest = false };
                        gL.Generate(rfL, liveL);
                        groundL = 40f - gL.StreetSink - gL.GradeDrop;
                        var hostL = new Node3D(); World.AddChild(hostL);
                        var wL = new EditorSidewalk(); hostL.AddChild(wL);
                        gL.LaySidewalkCornersOn(rfL, hostL, wL, out _);
                        // (the control for f, below, planned first: lanes planned on the ground, as a draped town's are --
                        // planned before any lane is BUILT, whose roads would leave no ground for an alley to be laid on)
                        gL.FollowTerrain = true;
                        gL.MarkPlots(null, rfL);
                        float droppedStep = 0f;
                        foreach (var lp in gL.LanePlans) if (lp.A != null) droppedStep = Mathf.Max(droppedStep, lp.A.Edge.Y - lp.Pts[0].Y);
                        gL.FollowTerrain = false;
                        // ...and the hard-90 toggle, both ways, planned before any lane is built: off, an L is one lane round a
                        // curve; on (the default), two straight halves meeting at the knee
                        gL.LaneHardCorners = false;
                        gL.MarkPlots(null, rfL);
                        int curvedPlans = gL.LanePlans.Count, curvedKnees = gL.LanePlans.FindAll(p => p.KneeA || p.KneeB).Count;
                        gL.LaneHardCorners = true;
                        gL.MarkPlots(null, rfL);
                        int hardPlans = gL.LanePlans.Count, hardKnees = gL.LanePlans.FindAll(p => p.KneeB).Count;
                        T.Check($"...the alleys' hard 90s an option: off, {curvedPlans} lanes and {curvedKnees} knee ends (each L one lane round a curve); on, {hardPlans} lanes, {hardKnees} of them L-turns as two halves meeting at a hard 90",
                                curvedKnees == 0 && hardKnees >= 2 && hardPlans == curvedPlans + hardKnees);
                        gL.LaySidewalks(hostL, rfL);            // the streets' own pavements, to hold the lanes' kerbs to
                        gL.BuildLanes(hostL, rfL);
                        var meshesL = new Dictionary<string, MeshInstance3D>();
                        void GatherL(Node n) { if (n is MeshInstance3D mi && mi.Mesh != null) meshesL[mi.Name.ToString()] = mi; foreach (var ch in n.GetChildren()) GatherL(ch); }
                        int lane0 = gL.LaneRoads.Count > 0 ? gL.LaneRoads[0] : -1;
                        float depthL = lane0 >= 0 ? rfL.RoadSurfaceOffset(lane0) : 0f;
                        var oldWalk = new EditorSidewalk { Width = gL.LaneWalkWidth, KerbHeight = 0.2f, RoadGap = walkD.RoadGap };
                        hostL.AddChild(oldWalk);
                        if (lane0 >= 0) oldWalk.LayAlongRoad(oldWalk, rfL, lane0, EditorSidewalk.Side.Right, null, 6f, 6f, piece: "old");
                        GatherL(hostL);
                        // how far a ribbon's surface stands over its lane's asphalt, at the lane's middle on the right
                        float? Proud(string name, int lane)
                        {
                            if (!meshesL.TryGetValue(name, out var mi)) return null;
                            float L = rfL.RoadLength(lane);
                            rfL.EvaluateAlong(lane, L * 0.5f, out var mp, out var mt);
                            var rt = new Vector3(mt.Z, 0f, -mt.X).Normalized();
                            var h = LevelAt(Tops(mi), mp + rt * (gL.LaneHalf + walkD.RoadGap + gL.LaneWalkWidth * 0.5f));
                            return h == null ? null : h.Value - (mp.Y + rfL.RoadSurfaceOffset(lane));
                        }
                        // the streets' kerb on this field, off their own pavements' meshes (a side laid in pieces round a mouth
                        // is "Sidewalk_R12_p0"...: any piece the probe lands on)
                        float streetProudL = 0f; int streetsL = 0;
                        foreach (int r in gL.Built)
                        {
                            if (streetsL >= 4) break;
                            float L = rfL.RoadLength(r);
                            if (gL.BayExtra(r, 1, L * 0.5f) > 0f || gL.MouthAt(r, 1, L * 0.5f - 2f, L * 0.5f + 2f) != null) continue;
                            rfL.EvaluateAlong(r, L * 0.5f, out var sp, out var st);
                            var q = sp + new Vector3(st.Z, 0f, -st.X).Normalized() * (rfL.RoadHalfWidth(r) + walkD.RoadGap + gL.PavementWidth * 0.5f);
                            float? h = null;
                            foreach (var kv in meshesL)
                                if (kv.Key == $"Sidewalk_R{r}" || kv.Key.StartsWith($"Sidewalk_R{r}_p")) { h = LevelAt(Tops(kv.Value), q); if (h != null) break; }
                            if (h == null) continue;
                            streetProudL += h.Value - (sp.Y + rfL.RoadSurfaceOffset(r)); streetsL++;
                        }
                        streetProudL = streetsL > 0 ? streetProudL / streetsL : float.NaN;
                        int deepOk = 0, deepTried = 0; float deepLowest = float.MaxValue;
                        var waysInL = new HashSet<int>();
                        foreach (var lp in gL.LanePlans) if (lp.OpenB) waysInL.Add(lp.Road);
                        foreach (int lr in gL.LaneRoads)
                        {
                            if (waysInL.Contains(lr)) continue;    // (a parking lot's way in has no pavements of its own)
                            deepTried++;
                            var pr = Proud($"Sidewalk_R{lr}", lr);
                            if (pr == null) continue;
                            deepLowest = Mathf.Min(deepLowest, pr.Value);
                            if (Mathf.Abs(pr.Value - streetProudL) < 0.01f) deepOk++;
                        }
                        var oldProud = lane0 >= 0 ? Proud($"Sidewalk_R{lane0}_old", lane0) : null;
                        T.Check($"...and on roads with the game's depth (asphalt {depthL:0.00} m over the kerb's foot) the lanes' kerbs still stand as high over their asphalt as the streets' there, {streetProudL * 100f:0.0} cm measured on {streetsL} ({deepOk} of {deepTried}; lowest {deepLowest * 100f:0.0} cm) -- where one laid the old way, 0.2 m over its foot, stands {(oldProud == null ? "NOWHERE" : $"{oldProud.Value * 100f:0.0} cm")} (control)",
                                depthL > 0.2f && streetsL > 0 && deepTried >= 6 && deepOk == deepTried && deepLowest > 0.05f && oldProud != null && oldProud.Value < 0f);
                        // f. AND LEVEL WITH THE STREET IT OPENS OFF (strawberry 2026-10-13: "they are still sinking below street
                        //    road level??"). The first lanes took their heights from the ground, which the town had already cut
                        //    0.12 m below its streets: a 12 cm step down off every street, and kerbs 10 cm over a lane that low
                        //    still under the street's asphalt. On this field the ground drops the same 0.12 m once the streets
                        //    are in, and at every mouth the lane's asphalt must meet the street's -- where the same lanes planned
                        //    on the ground, as a draped town's are, step down (control).
                        float worstStep = 0f; int mouthsL = 0;
                        foreach (var lp in gL.LanePlans)
                            foreach (var (m, atEnd) in new[] { (lp.A, false), (lp.B, true) })
                            {
                                if (m == null || lp.Road < 0) continue;
                                mouthsL++;
                                rfL.EvaluateAlong(m.Road, m.EdgeArc, out var sq, out _);
                                rfL.EvaluateAlong(lp.Road, atEnd ? rfL.RoadLength(lp.Road) : 0f, out var lq, out _);
                                worstStep = Mathf.Max(worstStep, Mathf.Abs(sq.Y + rfL.RoadSurfaceOffset(m.Road) - lq.Y - rfL.RoadSurfaceOffset(lp.Road)));
                            }
                        T.Check($"...and level with the street each opens off ({mouthsL} mouths, the ground graded {gL.GradeDrop * 100f:0} cm below the streets; worst step {worstStep * 100f:0.0} cm) -- where lanes laid on that ground step down {droppedStep * 100f:0.0} cm (control)",
                                mouthsL >= 6 && worstStep < 0.01f && droppedStep > 0.1f);
                        hostL.QueueFree();
                    }
                    stHost.QueueFree(); cnHost.QueueFree();
                }
                // ⭐ THE BOULEVARD (strawberry 2026-10-13: "a new road type, with a central concrete median/grass +
                // concrete median. 2 + 2 lanes"). One of the town's central main lines, as wide again as its median.
                // Measured on what was built: the texture its lanes are painted from, the median mesh, and the AI
                // lane paths that traffic actually drives.
                {
                    var boul = new List<int>();
                    foreach (int r in gD.Built) if (gD.IsBoulevard(rfD, r)) boul.Add(r);
                    float mainHalf = rfD.RoadHalfWidthOfMaterial(gD.MainMaterial), mHalf = gD.MedianWidth * 0.5f;
                    float boulHalf = boul.Count > 0 ? rfD.RoadHalfWidth(boul[0]) : 0f;
                    T.Check($"a boulevard runs down one of the town's central main lines ({boul.Count} spans), as wide as a main plus its median ({boulHalf:0.00} m half width vs {mainHalf:0.00} + {mHalf:0.00})",
                            boul.Count >= 2 && Mathf.Abs(boulHalf - (mainHalf + mHalf)) < 1e-3f);
                    // (its 2 + 2 lanes are painted, not textured: the lines block below holds every road to its class's
                    // lines, the boulevard's included)
                    // 2. TRAFFIC KEEPS CLEAR OF IT: every AI lane down the boulevard runs a car's half width clear of the
                    //    median's kerb, and still inside the carriageway. The control: a plain main's inside lanes,
                    //    held to the same median, would run along it.
                    const float carHalf = 1.5f;
                    int lanes = 0, clear = 0; float worstGap = float.MaxValue;
                    foreach (var lp in rfD.LanePaths)
                    {
                        if (!boul.Contains(lp.Road)) continue;
                        lanes++;
                        float gap = Mathf.Abs(lp.Offset) - carHalf - mHalf;
                        worstGap = Mathf.Min(worstGap, gap);
                        if (gap >= 0.25f && Mathf.Abs(lp.Offset) + carHalf <= boulHalf) clear++;
                    }
                    float plainInner = float.MaxValue;
                    foreach (var lp in rfD.LanePaths) if (!boul.Contains(lp.Road) && gD.IsMain(rfD, lp.Road)) plainInner = Mathf.Min(plainInner, Mathf.Abs(lp.Offset));
                    T.Check($"...its traffic lanes all clear of the median ({clear} of {lanes} lanes; nearest car {worstGap:0.00} m off its kerb) -- where a plain main's inside lane would be {plainInner - carHalf - mHalf:0.00} (control)",
                            lanes == 4 * boul.Count && clear == lanes && plainInner - carHalf - mHalf < 0.25f);
                    // 3. THE MEDIAN, AS BUILT: down the middle, raised, solid, stopping short of every crossing and its
                    //    stop line, and leaving every side street's mouth open across it.
                    var mHost = new Node3D(); World.AddChild(mHost);
                    gD.BuildMedians(mHost, rfD);
                    var medVerts = new List<Vector3>(); bool solid = false;
                    foreach (var ch in mHost.GetChildren())
                        if (ch is MeshInstance3D mmi && mmi.Mesh != null)
                        {
                            medVerts.AddRange((Vector3[])mmi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex]);
                            foreach (var gc in mmi.GetChildren()) if (gc is StaticBody3D) solid = true;
                        }
                    int offMiddle = 0, nearCrossing = 0, inMouth = 0;
                    float highest = float.MinValue;
                    var medCrossings = new List<Vector3[]>();
                    for (int j = 0; j < rfD.JunctionCount; j++)
                        foreach (var (er, ee) in rfD.JunctionEdges(j))
                            if (boul.Contains(er) && gD.LayCrossing(rfD, j, er, ee, out var cl))
                                medCrossings.Add(new[] { cl.At(cl.D0, -cl.Half), cl.At(cl.D0, cl.Half), cl.At(cl.SD1, cl.Half), cl.At(cl.SD1, -cl.Half) });
                    // the boulevard sampled once, every 2 m, and each junction on it with its other arms
                    var boulSamples = new List<(Vector3 c, Vector3 t, int road)>();
                    foreach (int r in boul)
                    {
                        float L = rfD.RoadLength(r);
                        // every 2 m AND the very end: a median may run right up to a road cut short of its stop line
                        for (float d = 0f; d <= L + 1.99f; d += 2f)
                            if (rfD.EvaluateAlong(r, Mathf.Min(d, L), out var pc, out var tc, snapTerrain: false))
                                boulSamples.Add((pc, new Vector3(tc.X, 0f, tc.Z).Normalized(), r));
                    }
                    var mouths = new List<(Vector3 node, Vector3 dir, float half)>();
                    for (int j = 0; j < rfD.JunctionCount; j++)
                    {
                        var es = rfD.JunctionEdges(j);
                        bool onBoul = false; foreach (var (er, _) in es) if (boul.Contains(er)) onBoul = true;
                        if (!onBoul) continue;
                        var node = rfD.JunctionPos(j);
                        foreach (var (er, ee) in es)
                        {
                            if (boul.Contains(er)) continue;
                            if (!rfD.EvaluateAlong(er, ee ? rfD.RoadLength(er) : 0f, out var tip, out _, snapTerrain: false)) continue;
                            var armDir = new Vector3(tip.X - node.X, 0f, tip.Z - node.Z);
                            if (armDir.Length() < 0.5f) continue;
                            mouths.Add((node, armDir.Normalized(), rfD.RoadHalfWidth(er)));
                        }
                    }
                    foreach (var v in medVerts)
                    {
                        // which boulevard span it is over, and where across it
                        float bestD = float.MaxValue, lat = 0f; int onRoad = -1;
                        foreach (var (pc, t, r) in boulSamples)
                        {
                            var rel = new Vector3(v.X - pc.X, 0f, v.Z - pc.Z);
                            if (Mathf.Abs(rel.Dot(t)) > 1.01f) continue;
                            float l = Mathf.Abs(rel.Dot(new Vector3(t.Z, 0f, -t.X)));
                            if (l < bestD) { bestD = l; lat = l; onRoad = r; }
                        }
                        if (onRoad < 0 || lat > mHalf + 0.01f) offMiddle++;
                        if (onRoad >= 0 && rfD.EvaluateAlong(onRoad, 0f, out var p0, out _, snapTerrain: false))
                            highest = Mathf.Max(highest, v.Y - (p0.Y + rfD.RoadSurfaceOffset(onRoad)));
                        foreach (var cq in medCrossings)
                        {
                            bool inside = true;
                            for (int e = 0; e < 4 && inside; e++)
                            {
                                var a = cq[e]; var b = cq[(e + 1) % 4];
                                // a metre's margin round the band
                                var dir = new Vector3(b.X - a.X, 0f, b.Z - a.Z).Normalized();
                                var nn = new Vector3(-dir.Z, 0f, dir.X);
                                var centre = (cq[0] + cq[1] + cq[2] + cq[3]) / 4f;
                                if (nn.Dot(centre - a) < 0f) nn = -nn;
                                if (nn.Dot(new Vector3(v.X - a.X, 0f, v.Z - a.Z)) < -0.5f) inside = false;
                            }
                            if (inside) { nearCrossing++; break; }
                        }
                        // a side street's mouth: in line with any other arm's carriageway at a junction on the boulevard
                        foreach (var (node, armDir, ahalf) in mouths)
                        {
                            var rel = new Vector3(v.X - node.X, 0f, v.Z - node.Z);
                            float side = Mathf.Abs(rel.Dot(new Vector3(armDir.Z, 0f, -armDir.X)));
                            if (side < ahalf + 1f && rel.Length() < 40f) { inMouth++; break; }
                        }
                    }
                    T.Check($"...and its median down the middle of it ({gD.Medians.Count} medians, {(gD.Medians.Count > 0 && gD.Medians[0].Grass ? "grass" : "concrete")}; {offMiddle} of {medVerts.Count} points off the middle), raised {highest:0.00} m and solid ({solid})",
                            gD.Medians.Count >= 1 && medVerts.Count > 0 && offMiddle == 0 && Mathf.Abs(highest - gD.MedianKerb) < 0.02f && solid);
                    T.Check($"...stopping short of every crossing on it ({nearCrossing} points within half a metre of one, of {medCrossings.Count} crossings) and leaving every side street's mouth open ({inMouth} points in one)",
                            medCrossings.Count + gD.Medians.Count > 0 && nearCrossing == 0 && inMouth == 0);
                    // 4. A GRASS MEDIAN IS PLANTED (strawberry 2026-10-13: "place hedges and trees along the grassy median"):
                    //    trees and hedges, every one on the median's grass, clear of its noses. The control: a concrete
                    //    median gets nothing.
                    bool? wasGrass = gD.MedianGrass;
                    gD.MedianGrass = false; gD.BuildMedians(null, rfD); gD.PlantMedians(rfD, null);
                    int onConcrete = gD.MedianPlants.Count;
                    gD.MedianGrass = true; gD.BuildMedians(null, rfD); gD.PlantMedians(rfD, null);
                    int trees = 0, hedges = 0, offGrass = 0, nearNose = 0;
                    foreach (var (prop, at, road) in gD.MedianPlants)
                    {
                        if (prop == gD.HedgeProp) hedges++; else trees++;
                        var md = gD.Medians.Find(m => m.Road == road);
                        float best = float.MaxValue, along = 0f, latOff = 0f;
                        for (float d = 0f; d <= rfD.RoadLength(road); d += 0.5f)
                        {
                            if (!rfD.EvaluateAlong(road, d, out var pc, out _, snapTerrain: false)) continue;
                            float dd = new Vector2(pc.X - at.X, pc.Z - at.Z).Length();
                            if (dd < best) { best = dd; along = d; latOff = dd; }
                        }
                        if (latOff > gD.MedianWidth * 0.5f - gD.MedianEdge + 0.01f) offGrass++;
                        if (md == null || along < md.A + md.Width - 0.5f || along > md.B - md.Width + 0.5f) nearNose++;
                    }
                    T.Check($"...and a grass median planted with trees and hedges ({trees} trees, {hedges} hedges; {offGrass} off its grass, {nearNose} near a nose) -- a concrete one with nothing ({onConcrete})",
                            trees >= 2 && hedges >= trees && offGrass == 0 && nearNose == 0 && onConcrete == 0);
                    gD.MedianGrass = wasGrass; gD.BuildMedians(null, rfD);
                    mHost.QueueFree();
                }
                // ⭐ THE LINES (strawberry 2026-10-13: "rip the line etc decals and apply them ourselves ... the spline
                // becomes just the tarmac road, decals are a separate thing"; "those double lines extend into the
                // crossing"). Every road plain tarmac; every line its own paint, measured off the painted mesh.
                {
                    // a texture's lines: columns of paint across it, as laterals off the centre
                    Image TexOf(int material)
                    {
                        var mat = rfD.MaterialOfMaterial(material);
                        Texture2D t = mat is StandardMaterial3D sm ? sm.AlbedoTexture : mat is ShaderMaterial shm ? shm.GetShaderParameter("albedo_tex").As<Texture2D>() : null;
                        return t?.GetImage();
                    }
                    int LinesIn(Image img)
                    {
                        if (img == null) return 0;
                        int w = img.GetWidth(), h = img.GetHeight(), runs = 0; bool inRun = false;
                        for (int x = 0; x < w; x++)
                        {
                            bool paint = false;
                            for (int y = 0; y < h && !paint; y += Mathf.Max(1, h / 32)) { var px = img.GetPixel(x, y); paint = px.R > 0.5f || px.G > 0.5f; }
                            if (paint && !inRun) runs++;
                            inRun = paint;
                        }
                        return runs;
                    }
                    var mats = new HashSet<int>();
                    foreach (int r in gD.Built) mats.Add(rfD.RoadMaterialOf(r));
                    int texLines = 0; foreach (int m in mats) texLines += LinesIn(TexOf(m));
                    int origLines = LinesIn(TexOf(gD.MainMaterial));
                    T.Check($"the town's roads are plain tarmac ({mats.Count} materials, {texLines} lines in their textures) -- where the main road's own texture has {origLines} (control)",
                            mats.Count >= 2 && texLines == 0 && origLines >= 3);
                    // the painted lines, as built: triangles with their colour (the yellow texel sits right of the white)
                    var lHost = new Node3D(); World.AddChild(lHost);
                    var cxHost = new Node3D(); World.AddChild(cxHost);
                    gD.BuildCrossings(cxHost, rfD);        // the lines stop at the crossings that were built
                    gD.BuildRoadLines(lHost, rfD);
                    var paint = new List<(Vector3 a, Vector3 b, Vector3 c, bool yellow)>();
                    foreach (var ch in lHost.GetChildren())
                        if (ch is MeshInstance3D lmi && lmi.Mesh != null && lmi.Name.ToString() == "Road_Lines")
                        {
                            var arr = lmi.Mesh.SurfaceGetArrays(0);
                            var vs = (Vector3[])arr[(int)Mesh.ArrayType.Vertex]; var uvs = (Vector2[])arr[(int)Mesh.ArrayType.TexUV];
                            for (int k = 0; k + 2 < vs.Length; k += 3) paint.Add((vs[k], vs[k + 1], vs[k + 2], uvs[k].X > 0.4f));
                        }
                    var pgrid = new Dictionary<(int, int), List<int>>();
                    for (int k = 0; k < paint.Count; k++)
                    {
                        var (a, b, c, _) = paint[k];
                        int x0 = Mathf.FloorToInt(Mathf.Min(a.X, Mathf.Min(b.X, c.X)) / 2f), x1 = Mathf.FloorToInt(Mathf.Max(a.X, Mathf.Max(b.X, c.X)) / 2f);
                        int z0 = Mathf.FloorToInt(Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) / 2f), z1 = Mathf.FloorToInt(Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)) / 2f);
                        for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                        { if (!pgrid.TryGetValue((x, z), out var l)) pgrid[(x, z)] = l = new List<int>(); l.Add(k); }
                    }
                    // what colour of paint is at a point: 0 none, 1 white, 2 yellow
                    int PaintAt(Vector3 q)
                    {
                        if (!pgrid.TryGetValue((Mathf.FloorToInt(q.X / 2f), Mathf.FloorToInt(q.Z / 2f)), out var l)) return 0;
                        foreach (int k in l)
                        {
                            var (a, b, c, y) = paint[k];
                            float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                            float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                            float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                            if (!((d1 < 0f || d2 < 0f || d3 < 0f) && (d1 > 0f || d2 > 0f || d3 > 0f))) return y ? 2 : 1;
                        }
                        return 0;
                    }
                    Vector3 On(int road, float d, float lat)
                    {
                        rfD.EvaluateAlong(road, d, out var pc, out var tc, snapTerrain: false);
                        return pc + new Vector3(tc.Z, 0f, -tc.X).Normalized() * lat;
                    }
                    // 0. THE BACK LANES CARRY NO PAINT AT ALL (strawberry 2026-10-13: "single lane roads ... and no markings"):
                    //    across their whole carriageway, end to end, nothing -- where a main's solid yellow centre, the control,
                    //    reads as painted
                    {
                        int laneSamples = 0, lanePainted = 0, controlPainted = 0, controlTried = 0;
                        foreach (int lr in gD.LaneRoads)
                        {
                            float L = rfD.RoadLength(lr);
                            for (float d = 0.5f; d <= L - 0.5f; d += 1f)
                                foreach (float lat in new[] { -1.5f, -0.75f, 0f, 0.75f, 1.5f })
                                { laneSamples++; if (PaintAt(On(lr, d, lat)) != 0) lanePainted++; }
                        }
                        foreach (int r in gD.Built)
                            if (gD.IsMain(rfD, r) && !gD.IsBoulevard(rfD, r) && controlTried++ < 3 && PaintAt(On(r, rfD.RoadLength(r) * 0.5f, 0f)) == 2) controlPainted++;
                        controlTried = Mathf.Min(controlTried, 3);
                        T.Check($"the back lanes carry no paint ({lanePainted} of {laneSamples} points across their carriageways painted) -- where a main's yellow centre is ({controlPainted} of {controlTried})",
                                laneSamples > 200 && lanePainted == 0 && controlTried > 0 && controlPainted == controlTried);
                    }
                    // 1. EVERY ROAD CARRIES ITS CLASS'S LINES, at their offsets and in their colours, across one whole dash
                    //    period at its middle -- and nothing between them (the lane centres are bare). An EDGE line is owed
                    //    where a straight kerb runs beside the road: read off the built pavement, not the rule that stops it
                    //    (a short link between two 45-degree corners is all kerb curve, and has none).
                    var swHost = new Node3D(); World.AddChild(swHost);
                    gD.LaySidewalks(swHost, rfD);
                    var walkTops = new Dictionary<string, List<(Vector3 a, Vector3 b, Vector3 c)>>();
                    void Walks(Node n)
                    {
                        if (n is MeshInstance3D wmi && wmi.Mesh != null && wmi.Name.ToString().StartsWith("Sidewalk_"))
                        {
                            var arr = wmi.Mesh.SurfaceGetArrays(0);
                            var vs = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                            var ix = arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : (int[])arr[(int)Mesh.ArrayType.Index];
                            var tops = new List<(Vector3, Vector3, Vector3)>();
                            int cnt = ix != null ? ix.Length : vs.Length;
                            for (int k = 0; k + 2 < cnt; k += 3)
                            {
                                Vector3 a = vs[ix != null ? ix[k] : k], b = vs[ix != null ? ix[k + 1] : k + 1], c = vs[ix != null ? ix[k + 2] : k + 2];
                                var nn = (b - a).Cross(c - a);
                                if (nn.Length() > 1e-3f && Mathf.Abs(nn.Y) > 0.9f * nn.Length()) tops.Add((a, b, c));
                            }
                            walkTops[wmi.Name.ToString()] = tops;
                        }
                        foreach (var ch in n.GetChildren()) Walks(ch);
                    }
                    Walks(swHost);
                    bool PavedAt(string name, Vector3 q)
                    {
                        if (!walkTops.TryGetValue(name, out var tops)) return false;
                        foreach (var (a, b, c) in tops)
                        {
                            float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                            float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                            float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                            if (!((d1 < 0f || d2 < 0f || d3 < 0f) && (d1 > 0f || d2 > 0f || d3 > 0f))) return true;
                        }
                        return false;
                    }
                    int roadsChecked = 0, linesWant = 0, linesFound = 0, laneCentres = 0, laneCentresPainted = 0;
                    foreach (int r in gD.Built)
                    {
                        float L = rfD.RoadLength(r);
                        if (L < 40f) continue;
                        float m0 = L * 0.5f - 4.5f, m1 = L * 0.5f + 4.5f;
                        roadsChecked++;
                        var table = gD.OwnLines(rfD, r, withEdges: true);
                        foreach (var (lat, w, colour, dashed, edge) in table)
                        {
                            bool bayHere = false;
                            foreach (var bay in gD.Bays) if (bay.Road == r && bay.Side == Mathf.Sign(lat) && bay.A < m1 + 1f && bay.B > m0 - 1f) bayHere = true;
                            if (edge && bayHere) continue;                        // broken for the bay: measured below
                            // an edge line is owed only beside a straight kerb: the street's own pavement, there
                            if (edge && !PavedAt($"Sidewalk_{(lat > 0f ? "R" : "L")}{r}",
                                                 On(r, L * 0.5f, Mathf.Sign(lat) * (rfD.RoadHalfWidth(r) + walkD.RoadGap + walkD.Width * 0.5f)))) continue;
                            linesWant++;
                            bool found = false;
                            for (float d = m0; d <= m1 && !found; d += 0.25f) found = PaintAt(On(r, d, lat)) == (colour == 1 ? 2 : 1);
                            if (found) linesFound++;
                        }
                        // bare between the lines: halfway between each neighbouring pair across the road -- the kerbs
                        // counted as lines too, so the outside lanes are checked as well
                        var lats = new List<float>(); foreach (var (lat, _, _, _, _) in table) lats.Add(lat);
                        float hr = rfD.RoadHalfWidth(r);
                        lats.Add(hr); lats.Add(-hr);
                        lats.Sort();
                        for (int k = 0; k + 1 < lats.Count; k++)
                        {
                            float mid = (lats[k] + lats[k + 1]) * 0.5f;
                            if (gD.IsBoulevard(rfD, r) && Mathf.Abs(mid) < rfD.MedianHalfOfMaterial(rfD.RoadMaterialOf(r))) continue;  // under the median
                            if (lats[k + 1] - lats[k] < 1f) continue;          // the double yellow's own gap
                            laneCentres++;
                            for (float d = m0; d <= m1; d += 0.5f) if (PaintAt(On(r, d, mid)) != 0) { laneCentresPainted++; break; }
                        }
                    }
                    T.Check($"...every road painted with its class's lines ({linesFound} of {linesWant} lines, over {roadsChecked} roads), and bare between them ({laneCentresPainted} of {laneCentres} lane centres painted)",
                            roadsChecked >= 10 && linesWant > 0 && linesFound == linesWant && laneCentresPainted == 0);
                    swHost.QueueFree();
                    // 2. NONE OF IT INTO A CROSSING: from the node out to its stop line, an approach is bare of road lines
                    //    (the stop line and the zebra are the crossings' own paint, another mesh). The control: a metre
                    //    past the stop line, the approach's solid edge line IS there.
                    int crossingsChecked = 0, lineInCrossing = 0, edgePast = 0;
                    for (int j = 0; j < rfD.JunctionCount; j++)
                        foreach (var (er, ee) in rfD.JunctionEdges(j))
                        {
                            if (!gD.Built.Contains(er) || !gD.CrossingBuilt(er, ee) || !gD.LayCrossing(rfD, j, er, ee, out var cl)) continue;
                            crossingsChecked++;
                            bool dirty = false;
                            // the crossing and the stop line's run-up: the approach's own ground, no other road's lines on it
                            for (float lt = -cl.Half + 0.1f; lt <= cl.Half - 0.1f && !dirty; lt += 0.1f)
                                for (float d = cl.D0; d < cl.SD1 - 0.1f && !dirty; d += 0.25f)
                                    if (PaintAt(cl.At(d, lt)) != 0) dirty = true;
                            if (dirty) lineInCrossing++;
                            // the control: its own lines take up again within one dash period past the stop line -- on a road
                            // with that much room before its far end's crossing (a short link between two has none)
                            float roomHere = rfD.RoadLength(er);
                            if (rfD.EvaluateAlong(er, ee ? rfD.RoadLength(er) : 0f, out var tipH, out _, snapTerrain: false))
                                roomHere -= cl.SD1 - new Vector2(tipH.X - cl.Node.X, tipH.Z - cl.Node.Z).Length();
                            int jFar = rfD.RoadEndJunction(er, !ee);
                            if (jFar >= 0 && gD.CrossingBuilt(er, !ee) && gD.LayCrossing(rfD, jFar, er, !ee, out var clFar)
                                && rfD.EvaluateAlong(er, !ee ? rfD.RoadLength(er) : 0f, out var tipF, out _, snapTerrain: false))
                                roomHere -= clFar.SD1 - new Vector2(tipF.X - clFar.Node.X, tipF.Z - clFar.Node.Z).Length();
                            if (roomHere < gD.LineDash + gD.LineGap + 2f) { edgePast++; continue; }
                            bool resumes = false;
                            foreach (var (olat, _, _, _, _) in gD.OwnLines(rfD, er, withEdges: false))
                            {
                                float armLat = ee ? -olat : olat;
                                for (float d = cl.SD1 + 0.2f; d <= cl.SD1 + gD.LineDash + gD.LineGap + 0.5f && !resumes; d += 0.25f)
                                    if (PaintAt(cl.At(d, armLat)) != 0) resumes = true;
                            }
                            if (resumes) edgePast++;
                        }
                    T.Check($"...and none of it runs into a crossing ({lineInCrossing} of {crossingsChecked} approaches painted over the crossing or up to the stop line; control: their lines take up again just past it, {edgePast} of {crossingsChecked})",
                            crossingsChecked > 10 && lineInCrossing == 0 && edgePast == crossingsChecked);
                    // 3. NO EDGE LINES, AND NO YELLOW ALONG A MEDIAN (strawberry 2026-10-13: "kill the outside lines and the
                    //    thin double yellow when we have a median"): the strip just in from every kerb is bare all along each
                    //    road's middle, and so is the strip beside the boulevard's median. The control: the same walk finds
                    //    the lane lines it should (check 1), so "bare" is not "the probe sees nothing".
                    int stripsChecked = 0, stripsPainted = 0;
                    foreach (int r in gD.Built)
                    {
                        float L = rfD.RoadLength(r);
                        if (L < 40f) continue;
                        float h = rfD.RoadHalfWidth(r), mhr = rfD.MedianHalfOfMaterial(rfD.RoadMaterialOf(r));
                        var lats = new List<float> { h - gD.EdgeLineInset, -(h - gD.EdgeLineInset) };
                        if (mhr > 0f) { lats.Add(mhr + gD.MedianLineInset); lats.Add(-(mhr + gD.MedianLineInset)); }
                        foreach (float lat in lats)
                        {
                            stripsChecked++;
                            for (float d = L * 0.5f - 4.5f; d <= L * 0.5f + 4.5f; d += 0.25f)
                                if (PaintAt(On(r, d, lat)) != 0) { stripsPainted++; break; }
                        }
                    }
                    T.Check($"...with no edge lines along the kerbs, nor a yellow along the boulevard's median ({stripsPainted} of {stripsChecked} strips painted)",
                            stripsChecked > 20 && stripsPainted == 0);
                    // 4. THE DASHES KEEP THEIR STEP THROUGH A JUNCTION A MAIN RUNS THROUGH (strawberry 2026-10-13: "i still
                    //    see the duped lines, they are spaced slightly apart, dead center of the junction"). Its two halves
                    //    meet at the node and each paints right up to it, so the middle of the box holds whatever the two
                    //    halves' rhythms leave there. Walk each dashed line straight through the node: the run across the
                    //    node and the two either side of it are each one whole dash or one whole gap. The walk finding those
                    //    neighbours at all is the control -- a line it misses fails rather than passes.
                    int thruNodes = 0, thruLines = 0, offStep = 0, unpaired = 0; string offAt = "";
                    float stepWalk = 2f * (gD.LineDash + gD.LineGap), stepTol = 0.5f;
                    for (int j = 0; j < rfD.JunctionCount; j++)
                    {
                        var thr = new List<(int road, bool atEnd)>();
                        foreach (var (er, ee) in rfD.JunctionEdges(j)) if (gD.Built.Contains(er) && gD.RunsThrough(er, ee)) thr.Add((er, ee));
                        if (thr.Count != 2) continue;
                        float L0 = rfD.RoadLength(thr[0].road), L1 = rfD.RoadLength(thr[1].road);
                        if (L0 < stepWalk + 1f || L1 < stepWalk + 1f) continue;
                        thruNodes++;
                        var table1 = gD.OwnLines(rfD, thr[1].road, withEdges: false);
                        foreach (var (lat0, _, _, dashed0, _) in gD.OwnLines(rfD, thr[0].road, withEdges: false))
                        {
                            if (!dashed0) continue;
                            // the same paint, as each road reckons its lateral: to the right walking OUT of the node along
                            // road 0 is to the left walking out along road 1
                            float out0 = thr[0].atEnd ? -lat0 : lat0;
                            float lat1 = thr[1].atEnd ? out0 : -out0;
                            bool met = false;
                            foreach (var (l1, _, _, d1, _) in table1) if (d1 && Mathf.Abs(l1 - lat1) < 0.05f) met = true;
                            if (!met) { unpaired++; continue; }
                            thruLines++;
                            // u < 0 out along road 0, u > 0 out along road 1, the node at 0
                            var on = new List<bool>();
                            for (int k = -(int)(stepWalk / 0.05f); k <= (int)(stepWalk / 0.05f); k++)
                            {
                                float u = k * 0.05f, s = Mathf.Abs(u);
                                var (rr, ae, la, LL) = k < 0 ? (thr[0].road, thr[0].atEnd, lat0, L0) : (thr[1].road, thr[1].atEnd, lat1, L1);
                                on.Add(PaintAt(On(rr, ae ? LL - s : s, la)) != 0);
                            }
                            var runs = new List<(bool paint, int a, int b)>();
                            for (int k = 0; k < on.Count; k++)
                                if (runs.Count == 0 || runs[^1].paint != on[k]) runs.Add((on[k], k, k)); else runs[^1] = (on[k], runs[^1].a, k);
                            int nodeAt = on.Count / 2, at = runs.FindIndex(x => x.a <= nodeAt && x.b >= nodeAt);
                            // the run over the node and one whole run either side of it -- never the window's clipped ends
                            bool inStep = at > 1 && at < runs.Count - 2;
                            if (!inStep && offAt == "") offAt = $"node {j}: no whole run either side of it on the line at {lat0:0.0}";
                            for (int k = at - 1; inStep && k <= at + 1; k++)
                            {
                                float len = (runs[k].b - runs[k].a + 1) * 0.05f, want = runs[k].paint ? gD.LineDash : gD.LineGap;
                                if (Mathf.Abs(len - want) <= stepTol) continue;
                                inStep = false;
                                if (offAt == "") offAt = $"{(runs[k].paint ? "a dash" : "a gap")} of {len:0.00} m {(k == at ? "across" : "beside")} node {j} (roads {thr[0].road}/{thr[1].road}, line at {lat0:0.0})";
                            }
                            if (!inStep) offStep++;
                        }
                    }
                    T.Check($"...and their dashes keep step straight through every junction a main runs through ({offStep} of {thruLines} lines out of step over {thruNodes} nodes{(offAt != "" ? $"; first: {offAt}" : "")}; {unpaired} meeting no line on the far side)",
                            thruNodes >= 2 && thruLines >= 4 && offStep == 0 && unpaired == 0);
                    lHost.QueueFree(); cxHost.QueueFree();
                }
                // determinism: the same town again lays the same plots
                {
                    var rfD2 = Field();
                    var gD2 = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                                 Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true, AlleysOnRequest = false };
                    gD2.Generate(rfD2, flat);
                    var w2 = new EditorSidewalk(); World.AddChild(w2);
                    gD2.LaySidewalkCornersOn(rfD2, World, w2, out _);
                    gD2.MarkPlots(null, rfD2);
                    bool same = gD2.Plots.Count == plots.Count && gD2.Alleys.Count == gD.Alleys.Count && gD2.Bays.Count == gD.Bays.Count;
                    for (int k = 0; same && k < plots.Count; k++)
                        same = gD2.Plots[k].Kind == plots[k].Kind && (gD2.Plots[k].Front - plots[k].Front).Length() < 1e-3f;
                    for (int k = 0; same && k < gD.Bays.Count; k++)
                        same = gD2.Bays[k].Road == gD.Bays[k].Road && gD2.Bays[k].Side == gD.Bays[k].Side
                               && Mathf.Abs(gD2.Bays[k].A - gD.Bays[k].A) < 1e-3f && Mathf.Abs(gD2.Bays[k].B - gD.Bays[k].B) < 1e-3f;
                    T.Check($"...and the same seed lays the same plots and bays ({gD2.Plots.Count} vs {plots.Count}; {gD2.Bays.Count} vs {gD.Bays.Count})", same);
                }
            }

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
                        // a main -- or the boulevard, a main with a median down it, on a material derived from the main's
                        int rm = rfA.RoadMaterialOf(road);
                        if (rfA.MaterialSource(rm) != mainMat) streetsThrough++;
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

            // ⭐⭐ WHERE TWO MAINS MEET, NOTHING RUNS THROUGH (strawberry 2026-10-12: "where u have 2 main roads
            // crossing, the splines just continue through, should have the 4 crossings like it did before. the
            // spline continuing rule is just for small roads joining"). The through rule had made one of two
            // crossing mains "the priority road", so a crossing of two four-lane roads had no zebra on any arm.
            // Held both ways, and counted off the BUILT crossing meshes rather than the rule that lays them:
            // every arm of a main-meets-main node is cut and crossed, and a main meeting only small roads still
            // runs through uncut and uncrossed while every small road there is crossed.
            {
                var gM = Grid(); var rfM = Field(); gM.Generate(rfM, flat);
                var marks = new Node3D(); World.AddChild(marks);
                gM.BuildCrossings(marks, rfM);
                var built = new Dictionary<int, int>();
                foreach (var ch in marks.GetChildren())
                {
                    var parts = ch.Name.ToString().Split('_');
                    if (parts.Length == 3 && parts[0] == "Crossing" && int.TryParse(parts[1], out int jj))
                        built[jj] = built.GetValueOrDefault(jj) + 1;
                }
                int mainMeets = 0, mainMeetsUncut = 0, mainMeetsShort = 0, throughNodes = 0, throughWrong = 0, square = 0, slanted = 0;
                for (int j = 0; j < rfM.JunctionCount; j++)
                {
                    var es = rfM.JunctionEdges(j);
                    if (es.Count < 3) continue;
                    var jp = rfM.JunctionPos(j);
                    int mains = 0, uncut = 0, thr = 0;
                    foreach (var (road, atEnd) in es)
                    {
                        if (gM.IsMain(rfM, road)) mains++;
                        if (gM.RunsThrough(road, atEnd)) thr++;
                        if (rfM.EvaluateAlong(road, atEnd ? rfM.RoadLength(road) : 0f, out var tip, out _, snapTerrain: false)
                            && new Vector2(tip.X - jp.X, tip.Z - jp.Z).Length() < 0.5f) uncut++;
                        if (gM.LayCrossing(rfM, j, road, atEnd, out var cl)) { if (cl.Shear == 0f) square++; else slanted++; }
                    }
                    int crossings = built.GetValueOrDefault(j);
                    if (mains >= 3)
                    {
                        mainMeets++;
                        if (uncut > 0 || thr > 0) mainMeetsUncut++;
                        if (crossings != es.Count) mainMeetsShort++;
                    }
                    else if (thr > 0)
                    {
                        throughNodes++;
                        if (thr != 2 || uncut != 2 || crossings != es.Count - 2) throughWrong++;
                    }
                }
                T.Check($"where two mains meet, nothing runs through ({mainMeets} main-meets-main junctions, {mainMeetsUncut} with an arm left uncut)",
                        mainMeets > 0 && mainMeetsUncut == 0);
                T.Check($"...and every arm there is crossed ({mainMeetsShort} junctions short of one crossing per arm)",
                        mainMeets > 0 && mainMeetsShort == 0);
                T.Check($"...while a main meeting only small roads still runs through, uncrossed, with each small road crossed ({throughNodes} such, {throughWrong} wrong)",
                        throughNodes > 0 && throughWrong == 0);
                // ⚠ AND THE SQUARE TOWN'S CROSSINGS ARE UNTOUCHED: laid parallel to a road met at a right angle is
                // square, and must come out with no slant at all -- not a rounding error's worth -- and the
                // crossings and masts must stand where the rule master signed off put them, bit for bit.
                T.Check($"...and on a square grid every crossing is square, exactly ({square} square, {slanted} slanted)",
                        square > 0 && slanted == 0);
                int moved = 0, held = 0;
                for (int j = 0; j < rfM.JunctionCount; j++)
                {
                    var es = rfM.JunctionEdges(j);
                    if (es.Count < 3) continue;
                    float widest = 0f;
                    foreach (var (o, _) in es) widest = Mathf.Max(widest, rfM.RoadHalfWidth(o));
                    foreach (var (road, atEnd) in es)
                    {
                        float old = gM.KerbCurveEnd(rfM, j, road, atEnd, widest, gM.CornerKerbRadius);
                        held++;
                        if (gM.SignalBack(rfM, j, road, atEnd, widest, 5f) != old + gM.SignalBeforeCurve) moved++;
                        if (gM.LayCrossing(rfM, j, road, atEnd, out var cl) && cl.D0 != old + gM.CrossingInset) moved++;
                    }
                }
                T.Check($"...with every crossing and mast where the square rule always put it ({moved} of {held} approaches moved)",
                        held > 0 && moved == 0);
                marks.QueueFree();
            }

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
