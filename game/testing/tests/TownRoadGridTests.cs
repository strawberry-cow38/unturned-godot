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
                var gD = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                            Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true };
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
                float R2 = gD.CornerKerbRadius, inset = gD.CrossingInset;
                int joining = 0, skewed = 0, gridSquare = 0, gridSkewed = 0, sidesHeld = 0, masts = 0, mastsOff = 0;
                float worstPar = 0f, worstClear = float.MaxValue, furthest = 0f, worstGridOver = float.MinValue, mastWas = 0f, mastNow = 0f;
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
                        // off the BUILT curve, both sides, whichever way it is laid
                        for (int s = -1; s <= 1; s += 2)
                        {
                            float ce = CurveEnd(cl, s);
                            if (float.IsNaN(ce)) continue;                 // a tee's straight side: no curve there
                            var k = cl.At(cl.D0, s * cl.Half) - cl.Node; k.Y = 0f;
                            worstClear = Mathf.Min(worstClear, k.Dot(outw) - ce);
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
                        if (back > was || edgeAtMast - back > inset + 0.01f || back > edgeAtMast) mastsOff++;
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
                T.Check($"...and every 45-degree crossing is off the BUILT kerb's curve, both sides ({sidesHeld} kerbs held, closest {worstClear:0.00} m past where it stops turning)",
                        sidesHeld >= 8 && worstClear > -0.001f);
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
                    kindsSeen.Add(pl.Kind);
                    var want = TownRoadGrid.SizeOf(pl.Kind);
                    if (pl.Width != want.Width || pl.Depth != want.Depth) offSize++;
                    if (Mathf.Abs(pl.Width / 3f - Mathf.Round(pl.Width / 3f)) > 1e-4f || Mathf.Abs(pl.Depth / 3f - Mathf.Round(pl.Depth / 3f)) > 1e-4f) offLattice++;
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
                    for (int e = 0; e < 4; e++)
                    {
                        var a = c[e]; var b = c[(e + 1) % 4];
                        int n = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.X - a.X, b.Z - a.Z).Length() / 0.75f));
                        for (int k = 0; k <= n; k++) if (OnSomething(a.Lerp(b, (float)k / n))) return true;
                    }
                    for (float u = 0.125f; u < 1f; u += 0.125f)
                        for (float v = 0.125f; v < 1f; v += 0.125f)
                            if (OnSomething(c[0].Lerp(c[1], u).Lerp(c[3].Lerp(c[2], u), v))) return true;
                    return false;
                }
                int touching = 0;
                foreach (var pl in plots) if (PlotTouches(pl.Corners())) touching++;
                // the control: the same check on a plot slid 3 m toward its own street must see the pavement
                var toward = new Vector3[4];
                for (int k = 0; k < 4; k++) toward[k] = c0[k] + plots[0].Facing * 3f;
                T.Check($"...none on a road, a pavement, a junction slab or a corner pavement ({touching} touch), and the check can see one (control: {PlotTouches(toward)})",
                        touching == 0 && PlotTouches(toward));
                // each fronts its street: front edge just behind the pavement, facing the road
                int offFront = 0; float worstFront = 0f;
                foreach (var pl in plots)
                {
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
                        if (rfD.RoadMaterialOf(er) == gD.MainMaterial) alleyOnMain++;
                    }
                }
                T.Check($"...and back lanes through the blocks ({alleys.Count} lanes)", alleys.Count >= 6);
                T.Check($"...never under a plot ({alleyOnPlot}) nor on a road or pavement ({alleyOnRoad})", alleyOnPlot == 0 && alleyOnRoad == 0);
                T.Check($"...each coming out at a street's pavement ({alleyLoose} loose ends), and never onto a main ({alleyOnMain})",
                        alleyLoose == 0 && alleyOnMain == 0);
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
                int cornerLots = 0, cornerOff = 0;
                foreach (var pl in plots)
                {
                    if (pl.Kind != TownRoadGrid.PlotKind.Corner) continue;
                    cornerLots++;
                    if (pl.Road2 < 0 || pl.Width != pl.Depth) cornerOff++;
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
                        bool Fits(float depth, float space) => depth - gD.BayEdgeGap - gD.BayLine - 0.03f >= widest + 0.4f && space >= longest + 1f;
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
                            ribbons[mi.Name.ToString()] = vs;
                            var ix = arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : (int[])arr[(int)Mesh.ArrayType.Index];
                            var tops = new List<(Vector3, Vector3, Vector3)>();
                            int cnt = ix != null ? ix.Length : vs.Length;
                            for (int k = 0; k + 2 < cnt; k += 3)
                            {
                                Vector3 a = vs[ix != null ? ix[k] : k], b = vs[ix != null ? ix[k + 1] : k + 1], c = vs[ix != null ? ix[k + 2] : k + 2];
                                var nn = (b - a).Cross(c - a);
                                if (nn.Length() > 1e-3f && Mathf.Abs(nn.Y) > 0.9f * nn.Length()) tops.Add((a, b, c));
                            }
                            ribbonTops[mi.Name.ToString()] = tops;
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
                    // ...and round its ANGLED ends the pavement fills back to where the stepped plots stand (strawberry:
                    // "the angled parts for the parking area? dont look like they got widened"). Pushed out square to the
                    // road, a 4 m band along a 47-degree kerb is 2.7 m across square to that kerb, and that was the
                    // complaint. So the back edge is the plots' own line: across the whole stretch of stepped plots, the
                    // built surface reaches the bay's depth plus the pavement's width out, angled ends included. The
                    // control: a metre past that stretch, in front of the next plot, it does not reach that far.
                    int filled = 0, fillControl = 0, fillMeasured = 0; float worstFill = 0f;
                    foreach (var bay in bays)
                    {
                        var f = Frame(bay);
                        string rn = $"Sidewalk_{(bay.Side > 0 ? "R" : "L")}{bay.Road}";
                        if (!ribbonTops.TryGetValue(rn, out var mineTops)) continue;
                        bool OnRibbon(Vector3 q) { foreach (var (ta, tb, tc) in mineTops) if (InTri(q, ta, tb, tc)) return true; return false; }
                        float line = f.half + walkD.RoadGap + bay.Depth + walkD.Width - 0.1f;     // just inside the back edge
                        Vector3 At(float d, float lat) => f.c + f.t * (d - f.mid) + f.o * lat;
                        fillMeasured++;
                        bool all = true; int stations = 0;
                        for (float d = bay.Front0 + 0.1f; d <= bay.Front1 - 0.1f + 1e-3f; d += 0.5f)
                        {
                            stations++;
                            if (!OnRibbon(At(d, line)))
                            {
                                all = false;
                                // how far out the surface does reach here, for the message
                                float reach = 0f;
                                for (float lat = f.half; lat <= line; lat += 0.1f) if (OnRibbon(At(d, lat))) reach = lat - f.half - walkD.RoadGap;
                                worstFill = Mathf.Max(worstFill, bay.Depth + walkD.Width - reach);
                            }
                        }
                        if (all && stations > 0) filled++;
                        if (!OnRibbon(At(bay.Front0 - 1f, line)) && !OnRibbon(At(bay.Front1 + 1f, line))) fillControl++;
                    }
                    T.Check($"...and round its angled ends the pavement fills back to the stepped plots' line ({filled} of {fillMeasured} bays; worst {worstFill:0.00} m short) -- and a metre past them it does not (control: {fillControl} of {fillMeasured})",
                            fillMeasured >= bays.Count && filled == fillMeasured && fillControl == fillMeasured);
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
                    // 4. THE DOTTED LINES: every dot inside its bay, the line down the open side broken into dots, and a
                    //    dotted line across the bay at each end of every space. The control: the middle of a space,
                    //    where nothing is painted, reads as bare -- so "covered" is not just "near a bay".
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
                    int edgeDotted = 0, crossDotted = 0, crossLines = 0, spaceBare = 0;
                    Vector3 Pt(TownRoadGrid.ParkingBay bay, float d, float lat) { var f = Frame(bay); return f.c + f.t * (d - f.mid) + f.o * lat; }
                    foreach (var bay in bays)
                    {
                        float half = rfD.RoadHalfWidth(bay.Road);
                        float edge = half + gD.BayEdgeGap + gD.BayLine * 0.5f;
                        int on = 0, all = 0;
                        for (float d = bay.A + bay.Taper; d <= bay.B - bay.Taper; d += 0.05f) { all++; if (Painted(Pt(bay, d, edge))) on++; }
                        if (all > 0 && on > all * 0.3f && on < all * 0.7f) edgeDotted++;
                        for (int k = 0; k <= bay.Spaces; k++)
                        {
                            crossLines++;
                            float d = bay.First + k * bay.SpaceLength;
                            int con = 0, call = 0;
                            for (float lat = edge + 0.3f; lat <= half + bay.Depth - 0.3f; lat += 0.05f) { call++; if (Painted(Pt(bay, d, lat))) con++; }
                            if (call > 0 && con > call * 0.25f && con < call * 0.75f) crossDotted++;
                        }
                        if (!Painted(Pt(bay, bay.First + bay.SpaceLength * 0.5f, half + bay.Depth * 0.5f))) spaceBare++;
                    }
                    T.Check($"...painted in dots: the line down each bay's open side dotted ({edgeDotted} of {bays.Count}), a dotted line across at each end of every space ({crossDotted} of {crossLines}), every dot inside its bay ({strayPaint} vertices stray) -- and the middle of a space bare ({spaceBare} of {bays.Count})",
                            paintTris.Count > 0 && edgeDotted == bays.Count && crossDotted == crossLines && strayPaint == 0 && spaceBare == bays.Count);
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
                    int straights = 0, rightWidth = 0; float worstWidth = 0f;
                    foreach (var mi in meshNodes)
                    {
                        string nm = mi.Name.ToString();
                        if (!nm.StartsWith("Sidewalk_R") && !nm.StartsWith("Sidewalk_L")) continue;
                        if (!int.TryParse(nm.Substring(10), out int road)) continue;
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
                        worstWidth = Mathf.Max(worstWidth, Mathf.Abs(hi - lo - gD.PavementWidth));
                        if (Mathf.Abs(hi - lo - gD.PavementWidth) < 0.02f) rightWidth++;
                    }
                    T.Check($"pavements are the town's {gD.PavementWidth:0.0} m wide ({rightWidth} of {straights} straight runs; worst {worstWidth * 100f:0.0} cm off)",
                            gD.PavementWidth >= 3.99f && straights > 20 && rightWidth == straights);
                    // 2. nowhere does one pavement lie over another: a point well inside one mesh's walking surface is
                    //    inside no other's. Bucketed by 2 m cells; the control is a ribbon laid over a copy of itself.
                    var tris = new List<(int mesh, Vector3 a, Vector3 b, Vector3 c)>();
                    for (int m = 0; m < meshNodes.Count; m++) foreach (var (a, b, c, _) in Tops(meshNodes[m])) tris.Add((m, a, b, c));
                    int Overlapping(List<(int mesh, Vector3 a, Vector3 b, Vector3 c)> ts)
                    {
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
                                    hit = true; break;
                                }
                                if (hit) break;
                            }
                            if (hit) hits++;
                        }
                        return hits;
                    }
                    int over = Overlapping(tris);
                    var dup = new List<(int, Vector3, Vector3, Vector3)>();
                    foreach (var (m, a, b, c) in tris) if (m == 0) { dup.Add((0, a, b, c)); dup.Add((1, a + new Vector3(0.5f, 0f, 0.5f), b + new Vector3(0.5f, 0f, 0.5f), c + new Vector3(0.5f, 0f, 0.5f))); }
                    int dupOver = Overlapping(dup);
                    T.Check($"...and no pavement lies over another ({over} of {tris.Count} surface triangles do; control: a ribbon over a shifted copy of itself, {dupOver} of {dup.Count})",
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
                    bool OnPave(Vector3 q)
                    {
                        if (!paveGrid.TryGetValue((Mathf.FloorToInt(q.X / 2f), Mathf.FloorToInt(q.Z / 2f)), out var l)) return false;
                        foreach (int k in l)
                        {
                            var (_, a, b, c) = tris[k];
                            float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                            float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                            float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                            if (d1 > 0f && d2 > 0f && d3 > 0f || d1 < 0f && d2 < 0f && d3 < 0f) return true;
                        }
                        return false;
                    }
                    bool PlotOnPave(Vector3[] c)
                    {
                        for (int e = 0; e < 4; e++)
                        {
                            var a = c[e]; var b = c[(e + 1) % 4];
                            int n = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.X - a.X, b.Z - a.Z).Length() / 0.5f));
                            for (int k = 0; k <= n; k++) if (OnPave(a.Lerp(b, (float)k / n))) return true;
                        }
                        return false;
                    }
                    int plotsOnPave = 0;
                    foreach (var pl in plots) if (PlotOnPave(pl.Corners())) plotsOnPave++;
                    var slid = plots[0].Corners();
                    for (int k = 0; k < 4; k++) slid[k] += plots[0].Facing * 3f;
                    T.Check($"...and no plot stands on a built pavement ({plotsOnPave} of {plots.Count} do; control: a plot slid 3 m toward its street {(PlotOnPave(slid) ? "is" : "IS NOT")})",
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
                        if (mi.Name.ToString().StartsWith("Sidewalk_Corner_")) { cornerPieces++; if (Folded(Tops(mi)) > 0) folded++; }
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
                    // 1. 2 + 2 LANES AT THE MAIN ROAD'S OWN PAINTED WIDTHS: the boulevard's texture is road_0's halves
                    //    pulled apart, so its dashed lane lines sit exactly a median's half further out than the main's,
                    //    and its centre yellow sits along each side of the median. Read off the texture, as drawn.
                    Image TexOf(int material)
                    {
                        var mat = rfD.MaterialOfMaterial(material);
                        Texture2D t = mat is StandardMaterial3D sm ? sm.AlbedoTexture : mat is ShaderMaterial shm ? shm.GetShaderParameter("albedo_tex").As<Texture2D>() : null;
                        return t?.GetImage();
                    }
                    List<(float lat, bool yellow)> LinesIn(Image img, float halfW)
                    {
                        var outp = new List<(float, bool)>();
                        if (img == null) return outp;
                        int w = img.GetWidth(), h = img.GetHeight();
                        int start = -1; bool yel = false, dashed = false;
                        for (int x = 0; x <= w; x++)
                        {
                            bool paint = false, isYellow = false, varies = false;
                            if (x < w)
                            {
                                var top = img.GetPixel(x, 0);
                                for (int y = 0; y < h; y += Mathf.Max(1, h / 32))
                                {
                                    var px = img.GetPixel(x, y);
                                    if (px.R > 0.5f || px.G > 0.5f) { paint = true; isYellow |= px.B < 0.3f; }
                                    if (!px.IsEqualApprox(top)) varies = true;
                                }
                            }
                            if (paint && start < 0) { start = x; yel = isYellow; dashed = varies; }
                            if (!paint && start >= 0)
                            {
                                float u = (start + x) * 0.5f / w;
                                outp.Add(((u * 2f - 1f) * halfW, yel));
                                start = -1;
                            }
                        }
                        return outp;
                    }
                    var mainLines = LinesIn(TexOf(gD.MainMaterial), mainHalf);
                    var boulLines = LinesIn(TexOf(gD.BoulevardMaterial), boulHalf);
                    int whiteMoved = 0, whiteMain = 0, yellowEdges = 0;
                    foreach (var (lat, yellow) in mainLines)
                    {
                        if (yellow) continue;
                        whiteMain++;
                        float want = lat + Mathf.Sign(lat) * mHalf;
                        foreach (var (bl, by) in boulLines) if (!by && Mathf.Abs(bl - want) < 0.1f) { whiteMoved++; break; }
                    }
                    foreach (var (bl, by) in boulLines) if (by && Mathf.Abs(Mathf.Abs(bl) - mHalf) < 0.3f) yellowEdges++;
                    T.Check($"...2 + 2 lanes at the main road's painted widths: its {whiteMoved} of {whiteMain} lane lines sit exactly a median's half out from the main's, with a yellow line along each side of the median ({yellowEdges})",
                            whiteMain == 2 && whiteMoved == whiteMain && yellowEdges == 2);
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
                    foreach (var lp in rfD.LanePaths) if (!boul.Contains(lp.Road) && rfD.RoadMaterialOf(lp.Road) == gD.MainMaterial) plainInner = Mathf.Min(plainInner, Mathf.Abs(lp.Offset));
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
                        for (float d = 0f; d <= L; d += 2f)
                            if (rfD.EvaluateAlong(r, d, out var pc, out var tc, snapTerrain: false))
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
                    mHost.QueueFree();
                }
                // determinism: the same town again lays the same plots
                {
                    var rfD2 = Field();
                    var gD2 = new TownRoadGrid { Seed = 7, Centre = Vector3.Zero, Extent = 250f, Spacing = 110f,
                                                 Bearing = 0.3f, SeaLevel = 0f, SeaMargin = 2f, Diagonal = true };
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
                        if (rm != mainMat && rfA.MedianHalfOfMaterial(rm) <= 0f) streetsThrough++;
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
                        if (rfM.RoadMaterialOf(road) == mainMat) mains++;
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
