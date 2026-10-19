using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // ==============================================================================================
    // GREENS (strawberry 2026-10-14: "work on some procedural filling for dead space / empty lots / parks. using
    // hedges as splines").
    // ==============================================================================================
    public sealed partial class TownRoadGrid
    {
        /// <summary>UG_TOWNNOGREENS=1: the dead space stays bare grass -- the control.</summary>
        public bool Greens = System.Environment.GetEnvironmentVariable("UG_TOWNNOGREENS") != "1";

        /// <summary>⭐ A HEDGE AS A SPLINE: the line it is laid along (plan points, y unused), whether it closes on itself, how
        /// deep its hedge is (metres across), and what it is for ("row" down a strip, "park" round a pocket park).</summary>
        public sealed class HedgeRun { public List<Vector3> Pts = new(); public bool Closed; public float Depth; public string Kind = ""; }
        /// <summary>One piece of hedge as it stands: the middle of its core on the ground, the way its length runs, how long and
        /// deep its core is laid (the prop stretched to fit: whole pieces butted end to end), and whether it is turned end for
        /// end (retail lays a hedge row's pieces both ways round, 90 and 270, so the foliage does not repeat).</summary>
        public sealed class HedgePiece { public Vector3 At, Dir; public float Length, Depth; public bool Flip; public HedgeRun Run; }
        /// <summary>A pocket park in a piece of dead space too big for a hedgerow, or on an EMPTY LOT (one nothing on the 3 m
        /// lattice fits in, `Lot`): its hedge line (closed; the hedge itself is a run open where the way in is), the way in,
        /// its trees and benches, its area and its middle.</summary>
        public sealed class PocketPark
        {
            public Vector3 Centre, Entrance; public float Area; public HedgeRun Hedge; public Plot Lot;
            public readonly List<Vector3> Trees = new();
            /// <summary>Where a tree could stand, in the order the seed picked, and how many it gets: chosen after the benches,
            /// which have first call on open ground.</summary>
            public readonly List<Vector3> TreeSpots = new(); public int TreeWant;
            /// <summary>Each bench: where it stands and the way its sitter faces (a plan direction).</summary>
            public readonly List<(Vector3 at, Vector3 faces)> Benches = new();
        }
        /// <summary>A bush: where, which, how big.</summary>
        public readonly List<(Vector3 at, string prop, float scale)> Bushes = new();
        /// <summary>What the last PlanGreens laid.</summary>
        public readonly List<HedgeRun> HedgeRuns = new();
        public readonly List<HedgePiece> HedgePieces = new();
        public readonly List<PocketPark> PocketParks = new();
        /// <summary>Trees standing on their own in a wide spot of dead space no park was laid in -- the widest place in a strip.</summary>
        public readonly List<Vector3> LoneTrees = new();
        /// <summary>The dead space the last PlanGreens found: square metres of ground a lot could have that nothing was laid
        /// on, in how many pieces, and how much of it a hedgerow or a park now takes (square metres within reach of one).</summary>
        public int DeadSpace, DeadSpacePieces, DeadSpaceGreened;
        /// <summary>(UG_TOWNPROBES=1) the dead space, a metre square at a time: drawn magenta over the plots.</summary>
        public readonly List<Vector3> DeadSpaceCells = new();
        /// <summary>A poor town's leftover dead space is painted DIRT, not left as grass (strawberry 2026-10-19: "dirt patches ->
        /// nice parks"). Set from GreenRichness in ApplyClassAndWealth; PaintDeadSpaceDirt does the painting.</summary>
        public bool DirtDeadSpace;
        /// <summary>Every dead-space cell's world position rounded to the metre, for PaintDeadSpaceDirt. Always gathered (cheap),
        /// unlike DeadSpaceCells which is a probe aid.</summary>
        readonly HashSet<(int, int)> _deadCells = new();
        /// <summary>A piece of dead space this big (m2) and this wide somewhere (half-width, m) is a park; a strip this wide
        /// (m) and a run this long (m) gets a hedgerow; a park's hedge stands this far in from its edge (m), its trees this
        /// far apart (m) and this far in (m).</summary>
        public float ParkMinArea = 250f, ParkMinHalfWidth = 5f, RowMinWidth = 1.2f, RowMinLength = 4.5f;
        public float ParkHedgeInset = 1f, ParkTreeSpacing = 7f, ParkTreeInset = 4.5f, ParkGateWidth = 3.5f;
        /// <summary>⭐ THE HEDGE'S CORE, MEASURED OFF ITS MESH: Hedge_0 is a solid box from x -2.0 to +2.0 and y -0.5 to +0.5 (its
        /// leaves hang on out to 2.27 and 0.92), so pieces butt core to core at a 4.0 m pitch -- and that is RETAIL'S pitch
        /// too: PEI's hedge rows stand 3.86-4.03 m apart, not the 4.57 m the bounding box says. Laid at the bbox, each joint
        /// was a 0.57 m slot the leaves did not close. ⚠ AND IT IS A Z-UP MESH: retail stands every hedge at 270 degrees about
        /// X (placements.txt), like the benches -- set down plainly it lay on its side, half under the ground.</summary>
        public const float HedgeCoreLength = 4f, HedgeCoreDepth = 1f;
        /// <summary>How deep a hedge's core may be laid (m): a narrow strip squeezes it, never thinner than this.</summary>
        public float HedgeDepthMax = 1.2f, HedgeDepthMin = 0.6f;
        /// <summary>Benches go only in parks this big (m2), and only where one has this much room: a pad this wide along the
        /// bench and this deep (seat and legroom), clear of every hedge, tree and bush by this much (m).</summary>
        public float BenchMinParkArea = 600f, BenchPadWidth = 3f, BenchPadDepth = 3.2f, BenchClear = 0.8f, BenchTreeClear = 3.5f;
        public string[] BushProps = { "Bush_0", "Bush_1" };
        /// <summary>⭐ HEDGES WHERE THEY MAKE SENSE, NOT EVERYWHERE (strawberry 2026-10-14: "dont *require* hedges everywhere"): the
        /// chance a park is left open (no hedge at all, bushes round its edge), and the chance a strip gets a hedgerow rather
        /// than a line of bushes. A park that does get one has it only along its STREET side -- where it backs onto yards,
        /// they are fenced already.</summary>
        public float ParkOpenChance = 0.3f, RowHedgeChance = 0.55f;
        /// <summary>A park's edge is its street side when it runs within this far (m) of a street's frontage line.</summary>
        public float ParkStreetReach = 4f;
        /// <summary>A bush's scale (the stock ones are 6 m across) and its footprint radius at scale 1 (m).</summary>
        public float BushScale = 0.45f, BushRadius = 2.9f;
        /// <summary>How far from anything a lot could not have (m) a lone tree in a strip stands at the least.</summary>
        public float LoneTreeHalfWidth = 3f;
        public string[] ParkTrees = { "Maple_0", "Maple_1", "Birch_0", "Birch_1" };
        public string ParkBench = "Bench_Wood_0";
        public float ParkTreeScale = 0.55f;

        /// <summary>⭐ THE DEAD SPACE, FILLED. Ground a lot could have that no lot took -- the strip between a row's backs and
        /// the street, the gap between two lots that grew unevenly, the notch beside a triangle lot, the odd corner of the
        /// town -- is found a metre at a time, in the grid's own frame so the strips along its streets lie square to the
        /// raster. Each piece of it that is long and thin gets a HEDGEROW down its middle: its skeleton, traced into lines,
        /// smoothed into a spline, and a hedge laid along it piece by piece. Each big enough to stand in gets a POCKET PARK:
        /// a hedge round it a couple of metres in from its edge, open where it is nearest a street, and trees inside.
        /// ⚠ THE GROUND IS STAMPED, NOT ASKED. LotCouldHave a cell at a time is ~80 us -- the triangle lots' raster near the
        /// avenue alone took 2.7 s that way -- so every rule it has is laid onto the raster whole instead: each street's
        /// frontage strip, each lane's reach, each slab's margin, each bay's front, each lot and alley.</summary>
        void PlanGreens(RoadField roads)
        {
            HedgeRuns.Clear(); HedgePieces.Clear(); PocketParks.Clear(); DeadSpaceCells.Clear(); LoneTrees.Clear(); Bushes.Clear(); _deadCells.Clear();
            DeadSpace = DeadSpacePieces = DeadSpaceGreened = 0;
            if (!Greens || roads == null || _frameU1 <= _frameU0 || _frameV1 <= _frameV0) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const float Cell = 1f;
            float U0 = _frameU0, V0 = _frameV0;
            int nu = Mathf.CeilToInt((_frameU1 - _frameU0) / Cell), nv = Mathf.CeilToInt((_frameV1 - _frameV0) / Cell);
            if (nu <= 2 || nv <= 2 || (long)nu * nv > 4_000_000) return;
            float c = Mathf.Cos(Bearing), s = Mathf.Sin(Bearing);
            Vector2 UV(Vector3 w) { float dx = w.X - Centre.X, dz = w.Z - Centre.Z; return new Vector2(dx * c + dz * s, -dx * s + dz * c); }
            Vector3 W(int i, int j) => ToWorld(U0 + (i + 0.5f) * Cell, V0 + (j + 0.5f) * Cell, 0f);
            // what says no to a lot in each cell: 0 nothing (dead space), else Why* -- and which lot, where it is one
            var why = new byte[nu, nv];
            var lotOf = new int[nu, nv];
            byte paint = WhyStreet;
            // a capsule round a segment, in plan
            void Capsule(Vector3 a, Vector3 b, float r)
            {
                var ua = UV(a); var ub = UV(b);
                int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(ua.X, ub.X) - r - U0) / Cell)), i1 = Mathf.Min(nu - 1, Mathf.CeilToInt((Mathf.Max(ua.X, ub.X) + r - U0) / Cell));
                int j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(ua.Y, ub.Y) - r - V0) / Cell)), j1 = Mathf.Min(nv - 1, Mathf.CeilToInt((Mathf.Max(ua.Y, ub.Y) + r - V0) / Cell));
                var ab = ub - ua; float l2 = Mathf.Max(1e-8f, ab.LengthSquared());
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                    {
                        if (why[i, j] != 0) continue;
                        var q = new Vector2(U0 + (i + 0.5f) * Cell, V0 + (j + 0.5f) * Cell);
                        float t = Mathf.Clamp((q - ua).Dot(ab) / l2, 0f, 1f);
                        if ((ua + ab * t - q).Length() < r) why[i, j] = paint;
                    }
            }
            // a convex polygon, filled
            void Poly(Vector3[] p, int id)
            {
                if (p == null || p.Length < 3) return;
                float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
                foreach (var v in p) { var uv = UV(v); a0 = Mathf.Min(a0, uv.X); a1 = Mathf.Max(a1, uv.X); b0 = Mathf.Min(b0, uv.Y); b1 = Mathf.Max(b1, uv.Y); }
                int i0 = Mathf.Max(0, Mathf.FloorToInt((a0 - U0) / Cell) - 1), i1 = Mathf.Min(nu - 1, Mathf.CeilToInt((a1 - U0) / Cell) + 1);
                int j0 = Mathf.Max(0, Mathf.FloorToInt((b0 - V0) / Cell) - 1), j1 = Mathf.Min(nv - 1, Mathf.CeilToInt((b1 - V0) / Cell) + 1);
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                        if (why[i, j] == 0 && InsidePoly(p, W(i, j))) { why[i, j] = paint; lotOf[i, j] = id; }
            }
            // 1. everything a lot could not have. Each street's frontage strip (a lot stands behind it) ...
            long t0 = clock.ElapsedMilliseconds;
            paint = WhyStreet;
            foreach (int r in Built)
            {
                var (ps, _) = RoadSamples(roads, r);
                float front = roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback - 0.05f;
                if (ps.Length == 1) { Capsule(ps[0], ps[0], front); continue; }
                // (a metre-sample at a time is thousands of capsules; a chord as long as the road stays within 3 cm of it)
                int k0 = 0;
                while (k0 < ps.Length - 1)
                {
                    int k1 = k0 + 1;
                    while (k1 + 1 < ps.Length)
                    {
                        var a = ps[k0]; var b = ps[k1 + 1];
                        var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = Mathf.Max(1e-8f, ab.LengthSquared());
                        bool straight = true;
                        for (int m = k0 + 1; m <= k1 && straight; m++)
                        {
                            var q = new Vector2(ps[m].X - a.X, ps[m].Z - a.Z);
                            float t = Mathf.Clamp(q.Dot(ab) / l2, 0f, 1f);
                            straight = (q - ab * t).Length() < 0.03f;
                        }
                        if (!straight) break;
                        k1++;
                    }
                    Capsule(ps[k0], ps[k1], front);
                    k0 = k1;
                }
            }
            // ...each back lane's reach, run on square past an L's knee to cover its corner ...
            long tStreets = clock.ElapsedMilliseconds - t0;
            paint = WhyLane;
            float laneReach = LaneHalf + PavementGap + LaneWalkWidth + PlotClearance;
            foreach (var lp in LanePlans)
            {
                var pts = lp.Pts;
                if (pts == null || pts.Count < 2) continue;
                for (int k = 1; k < pts.Count; k++)
                {
                    var a = pts[k - 1]; var b = pts[k];
                    var d = new Vector3(b.X - a.X, 0f, b.Z - a.Z); float len = d.Length();
                    if (len < 1e-4f) continue;
                    d /= len;
                    if (k == 1 && lp.KneeA) a -= d * laneReach;
                    if (k == pts.Count - 1 && lp.KneeB) b += d * laneReach;
                    Capsule(a, b, laneReach);
                }
            }
            // ...each junction slab's margin: its triangles rastered, then everything within `keep` of them (a chamfer distance
            // from the slab's own cells -- RoadPatch.DistanceTo a cell at a time walks every triangle, and was 1.5 s of this)
            float keep = PavementGap + _walkWidth + PlotClearance;
            long tSlab = clock.ElapsedMilliseconds;
            var slab = new float[nu, nv];
            for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) slab[i, j] = 1e6f;
            foreach (var pp in Patches)
            {
                if (pp?.X == null || pp.Tris == null) continue;
                for (int t = 0; t + 2 < pp.Tris.Length; t += 3)
                {
                    var a = UV(new Vector3((float)pp.X[pp.Tris[t]], 0f, -(float)pp.Z[pp.Tris[t]]));
                    var b = UV(new Vector3((float)pp.X[pp.Tris[t + 1]], 0f, -(float)pp.Z[pp.Tris[t + 1]]));
                    var d = UV(new Vector3((float)pp.X[pp.Tris[t + 2]], 0f, -(float)pp.Z[pp.Tris[t + 2]]));
                    int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.X, Mathf.Min(b.X, d.X)) - U0) / Cell)), i1 = Mathf.Min(nu - 1, Mathf.CeilToInt((Mathf.Max(a.X, Mathf.Max(b.X, d.X)) - U0) / Cell));
                    int j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.Y, Mathf.Min(b.Y, d.Y)) - V0) / Cell)), j1 = Mathf.Min(nv - 1, Mathf.CeilToInt((Mathf.Max(a.Y, Mathf.Max(b.Y, d.Y)) - V0) / Cell));
                    float Cross(Vector2 o, Vector2 p1, Vector2 p2) => (p1.X - o.X) * (p2.Y - o.Y) - (p1.Y - o.Y) * (p2.X - o.X);
                    for (int i = i0; i <= i1; i++)
                        for (int j = j0; j <= j1; j++)
                        {
                            var q = new Vector2(U0 + (i + 0.5f) * Cell, V0 + (j + 0.5f) * Cell);
                            float c1 = Cross(a, b, q), c2 = Cross(b, d, q), c3 = Cross(d, a, q);
                            if ((c1 >= 0f && c2 >= 0f && c3 >= 0f) || (c1 <= 0f && c2 <= 0f && c3 <= 0f)) slab[i, j] = 0f;
                        }
                }
            }
            Chamfer(slab, nu, nv);
            for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) if (why[i, j] == 0 && slab[i, j] <= keep + 0.5f * Cell) why[i, j] = WhySlab;
            tSlab = clock.ElapsedMilliseconds - tSlab;
            // ...each parking bay's front (the lots there stand back the bay's depth more) ...
            paint = WhyStreet;
            foreach (var bay in Bays)
            {
                float front = roads.RoadHalfWidth(bay.Road) + PavementGap + _walkWidth + PlotSetback;
                for (float t = bay.Front0; t <= bay.Front1; t += 0.5f)
                {
                    if (!roads.EvaluateAlong(bay.Road, t, out var bp, out var bt, snapTerrain: false)) continue;
                    var right = new Vector3(bt.Z, 0f, -bt.X).Normalized() * bay.Side;
                    var mid = bp + right * (front + bay.Depth * 0.5f);
                    Capsule(mid, mid, bay.Depth * 0.5f + 0.75f);
                }
            }
            // ...and every lot and alley
            long tPlots = clock.ElapsedMilliseconds;
            paint = WhyLot;
            for (int k = 0; k < Plots.Count; k++) Poly(Plots[k].Boundary(), k);
            paint = WhyLane;
            foreach (var al in Alleys) Poly(al.Corners(), -1);
            tPlots = clock.ElapsedMilliseconds - tPlots;
            var shut = new bool[nu, nv];
            for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) shut[i, j] = why[i, j] != 0;
            // 2. how far each open cell is from ground a lot could not have: a chamfer distance, two passes
            var dt = new float[nu, nv];
            for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) dt[i, j] = shut[i, j] ? 0f : 1e6f;
            // (the frame's own edge is the town's edge: open country beyond it, nothing to lay)
            for (int i = 0; i < nu; i++) { dt[i, 0] = Mathf.Min(dt[i, 0], 1f); dt[i, nv - 1] = Mathf.Min(dt[i, nv - 1], 1f); }
            for (int j = 0; j < nv; j++) { dt[0, j] = Mathf.Min(dt[0, j], 1f); dt[nu - 1, j] = Mathf.Min(dt[nu - 1, j], 1f); }
            Chamfer(dt, nu, nv);
            // 3. each piece of it, 8-connected
            var seen = new bool[nu, nv];
            var greened = new bool[nu, nv];
            ulong seedH = Mix(((ulong)(uint)Seed << 32) ^ 0x6EE45UL);
            for (int si = 0; si < nu; si++)
                for (int sj = 0; sj < nv; sj++)
                {
                    if (shut[si, sj] || seen[si, sj]) continue;
                    var cells = new List<(int, int)>();
                    var st = new Stack<(int, int)>(); st.Push((si, sj)); seen[si, sj] = true;
                    int ci0 = si, ci1 = si, cj0 = sj, cj1 = sj; float maxDt = 0f;
                    while (st.Count > 0)
                    {
                        var (i, j) = st.Pop(); cells.Add((i, j));
                        ci0 = Math.Min(ci0, i); ci1 = Math.Max(ci1, i); cj0 = Math.Min(cj0, j); cj1 = Math.Max(cj1, j);
                        maxDt = Mathf.Max(maxDt, dt[i, j]);
                        for (int di = -1; di <= 1; di++)
                            for (int dj = -1; dj <= 1; dj++)
                            {
                                int a = i + di, b = j + dj;
                                if (a < 0 || b < 0 || a >= nu || b >= nv || shut[a, b] || seen[a, b]) continue;
                                seen[a, b] = true; st.Push((a, b));
                            }
                    }
                    DeadSpace += cells.Count;
                    foreach (var (i, j) in cells) { var wc = W(i, j); _deadCells.Add((Mathf.RoundToInt(wc.X), Mathf.RoundToInt(wc.Z))); if (ProbeEverything) DeadSpaceCells.Add(wc); }
                    if (cells.Count < 6) continue;
                    DeadSpacePieces++;
                    // the piece on its own grid, a cell of border round it
                    int w = ci1 - ci0 + 3, h = cj1 - cj0 + 3;
                    var on = new bool[w, h];
                    foreach (var (i, j) in cells) on[i - ci0 + 1, j - cj0 + 1] = true;
                    Vector3 Wl(int li, int lj) => W(li - 1 + ci0, lj - 1 + cj0);
                    float Dl(int li, int lj) => dt[li - 1 + ci0, lj - 1 + cj0];
                    float halfMax = maxDt - 0.5f;
                    int runsBefore = HedgeRuns.Count, parksBefore = PocketParks.Count;
                    string what = "left";
                    // what it runs along: a lot's FRONT is the slack between a lot standing back and its street -- its front
                    // yard, to look at; a hedge there would wall the lot off from the street it faces
                    int lotTouch = 0, frontTouch = 0;
                    foreach (var (i, j) in cells)
                        foreach (var (a, b) in new[] { (i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1) })
                        {
                            if (a < 0 || b < 0 || a >= nu || b >= nv || why[a, b] != WhyLot) continue;
                            lotTouch++;
                            if (AtLotFront(Plots[lotOf[a, b]], W(a, b))) frontTouch++;
                        }
                    if (cells.Count >= ParkMinArea && halfMax >= ParkMinHalfWidth) { PlanPocketPark(roads, on, w, h, Wl, Dl, cells.Count, ref seedH); what = "park"; }
                    else if (lotTouch > 0 && frontTouch >= 0.3f * lotTouch) what = $"left: along lot fronts ({frontTouch} of {lotTouch} lot cells beside it are a front)";
                    else if (2f * halfMax >= RowMinWidth) { what = PlanHedgerows(on, w, h, Wl, Dl, ref seedH); }
                    // ...and a tree in each spot of it wide enough to stand one (6 m across), clear of any hedge laid down it
                    if (what != "park" && halfMax >= LoneTreeHalfWidth)
                    {
                        var spots = new List<(int, int)>();
                        foreach (var (i, j) in cells) if (dt[i, j] - 0.5f >= LoneTreeHalfWidth) spots.Add((i, j));
                        spots.Sort((x, y) => dt[y.Item1, y.Item2].CompareTo(dt[x.Item1, x.Item2]));
                        int placed = 0;
                        foreach (var (i, j) in spots)
                        {
                            if (placed >= 3) break;
                            var q = W(i, j);
                            bool clear = true;
                            foreach (var t in LoneTrees) if (Flat(t, q) < ParkTreeSpacing) { clear = false; break; }
                            for (int rr = runsBefore; rr < HedgeRuns.Count && clear; rr++)
                            {
                                var rp = HedgeRuns[rr].Pts;
                                for (int k = 1; k < rp.Count && clear; k++)
                                {
                                    var ab = new Vector2(rp[k].X - rp[k - 1].X, rp[k].Z - rp[k - 1].Z); float l2 = Mathf.Max(1e-6f, ab.LengthSquared());
                                    var aq = new Vector2(q.X - rp[k - 1].X, q.Z - rp[k - 1].Z);
                                    if ((aq - ab * Mathf.Clamp(aq.Dot(ab) / l2, 0f, 1f)).Length() < HedgeRuns[rr].Depth * 0.5f + 2f) clear = false;
                                }
                            }
                            if (!clear) continue;
                            LoneTrees.Add(q); placed++;
                        }
                        if (placed > 0) what += $", {placed} tree{(placed > 1 ? "s" : "")}";
                    }
                    // ...and bushes in the spots a bush fits (3.6 m across), round any lone tree and on their own, clear of hedges
                    if (what != "park" && halfMax >= 1.8f && !what.StartsWith("left: along lot fronts"))
                    {
                        float br = BushRadius * BushScale;
                        var spots = new List<(int, int)>();
                        foreach (var (i, j) in cells) if (dt[i, j] - 0.5f >= Mathf.Max(1.8f, br + 0.3f)) spots.Add((i, j));
                        spots.Sort((x, y) => dt[y.Item1, y.Item2].CompareTo(dt[x.Item1, x.Item2]));
                        int placed = 0;
                        foreach (var (i, j) in spots)
                        {
                            if (placed >= 3) break;
                            var q = W(i, j);
                            bool clear = true;
                            foreach (var (bq, _, _) in Bushes) if (Flat(bq, q) < 2f * br + 1.5f) { clear = false; break; }
                            foreach (var t in LoneTrees) if (Flat(t, q) < 2.2f) { clear = false; break; }
                            for (int rr = runsBefore; rr < HedgeRuns.Count && clear; rr++)
                            {
                                var rp = HedgeRuns[rr].Pts;
                                for (int k = 1; k < rp.Count && clear; k++)
                                {
                                    var ab = new Vector2(rp[k].X - rp[k - 1].X, rp[k].Z - rp[k - 1].Z); float l2 = Mathf.Max(1e-6f, ab.LengthSquared());
                                    var aq = new Vector2(q.X - rp[k - 1].X, q.Z - rp[k - 1].Z);
                                    if ((aq - ab * Mathf.Clamp(aq.Dot(ab) / l2, 0f, 1f)).Length() < HedgeRuns[rr].Depth * 0.5f + br + 0.3f) clear = false;
                                }
                            }
                            if (!clear) continue;
                            seedH = Mix(seedH + 0xB0B0UL);
                            Bushes.Add((q, BushProps[(int)(seedH % (ulong)BushProps.Length)], BushScale * (0.85f + 0.3f * Unit(seedH, 20))));
                            placed++;
                        }
                        if (placed > 0) what += $", {placed} bush{(placed > 1 ? "es" : "")}";
                    }
                    if (ProbeEverything)
                    {
                        var pm = Vector3.Zero; foreach (var (i, j) in cells) pm += W(i, j); pm /= cells.Count;
                        Log.Print($"[towngrid]   dead space at ({pm.X:0},{pm.Z:0}): {cells.Count} m2, {w - 2}x{h - 2} m box, up to {2f * halfMax:0.0} m wide -- {what}"
                                + $" ({HedgeRuns.Count - runsBefore} runs, {PocketParks.Count - parksBefore} parks)");
                    }
                }
            long tPieces = clock.ElapsedMilliseconds;
            // 3b. EMPTY LOTS: a lot nothing on the 3 m lattice fits in (a small triangle lot) is a pocket park of its own -- its
            // ground the lot's outline, its hedge in from its edges, open toward its street
            foreach (var pl in Plots)
            {
                if (pl.Outline == null || pl.Width > 0f) continue;
                var ol = pl.Outline;
                float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
                foreach (var v in ol) { var uv = UV(v); a0 = Mathf.Min(a0, uv.X); a1 = Mathf.Max(a1, uv.X); b0 = Mathf.Min(b0, uv.Y); b1 = Mathf.Max(b1, uv.Y); }
                int i0 = Mathf.Max(0, Mathf.FloorToInt((a0 - U0) / Cell) - 1), j0 = Mathf.Max(0, Mathf.FloorToInt((b0 - V0) / Cell) - 1);
                int w = Mathf.Min(nu - 1, Mathf.CeilToInt((a1 - U0) / Cell) + 1) - i0 + 1, h = Mathf.Min(nv - 1, Mathf.CeilToInt((b1 - V0) / Cell) + 1) - j0 + 1;
                if (w < 3 || h < 3) continue;
                var on = new bool[w, h]; var ld = new float[w, h]; int area = 0;
                for (int i = 0; i < w; i++)
                    for (int j = 0; j < h; j++)
                    {
                        on[i, j] = i > 0 && j > 0 && i < w - 1 && j < h - 1 && InsidePoly(ol, W(i0 + i, j0 + j));
                        ld[i, j] = on[i, j] ? 1e6f : 0f;
                        if (on[i, j]) area++;
                    }
                Chamfer(ld, w, h);
                int before = PocketParks.Count;
                PlanPocketPark(roads, on, w, h, (li, lj) => W(i0 + li, j0 + lj), (li, lj) => ld[li, lj], area, ref seedH, inset: 1.5f, lot: pl);
                if (ProbeEverything) Log.Print($"[towngrid]   empty lot at {pl.Front}: {area} m2 -- {(PocketParks.Count > before ? "a pocket park" : "too small for one")}");
            }
            // 4. the hedges, piece by piece, and how much of the dead space they and the parks now take
            long tLay = clock.ElapsedMilliseconds;
            foreach (var run in HedgeRuns) LayHedge(run, ref seedH);
            PlanBenches();
            // the trees, last: ParkTreeSpacing apart, and out of every bench's lawn -- its pad and BenchTreeClear round it
            foreach (var pk in PocketParks)
                foreach (var q in pk.TreeSpots)
                {
                    if (pk.Trees.Count >= pk.TreeWant) break;
                    bool clear = true;
                    foreach (var t in pk.Trees) if (Flat(t, q) < ParkTreeSpacing) { clear = false; break; }
                    foreach (var (bq, faces) in pk.Benches)
                    {
                        var padMid = bq + faces * (BenchPadDepth * 0.5f - 0.9f);
                        if (Flat(padMid, q) < Mathf.Max(BenchPadWidth, BenchPadDepth) * 0.5f + BenchTreeClear) { clear = false; break; }
                    }
                    if (clear) pk.Trees.Add(q);
                }
            foreach (var hp in HedgePieces)
            {
                var uv = UV(hp.At);
                float r = Mathf.Max(hp.Length, hp.Depth) * 0.5f + 0.5f;
                for (int i = Mathf.Max(0, Mathf.FloorToInt((uv.X - r - U0) / Cell)); i <= Mathf.Min(nu - 1, Mathf.CeilToInt((uv.X + r - U0) / Cell)); i++)
                    for (int j = Mathf.Max(0, Mathf.FloorToInt((uv.Y - r - V0) / Cell)); j <= Mathf.Min(nv - 1, Mathf.CeilToInt((uv.Y + r - V0) / Cell)); j++)
                        if (!shut[i, j] && !greened[i, j]) { greened[i, j] = true; DeadSpaceGreened++; }
            }
            foreach (var pk in PocketParks)
            {
                var uv = UV(pk.Centre);
                float r = Mathf.Sqrt(pk.Area / Mathf.Pi) + 2f;
                for (int i = Mathf.Max(0, Mathf.FloorToInt((uv.X - r - U0) / Cell)); i <= Mathf.Min(nu - 1, Mathf.CeilToInt((uv.X + r - U0) / Cell)); i++)
                    for (int j = Mathf.Max(0, Mathf.FloorToInt((uv.Y - r - V0) / Cell)); j <= Mathf.Min(nv - 1, Mathf.CeilToInt((uv.Y + r - V0) / Cell)); j++)
                        if (!shut[i, j] && !greened[i, j] && InsidePark(pk, W(i, j))) { greened[i, j] = true; DeadSpaceGreened++; }
            }
            foreach (var t in LoneTrees)
            {
                var uv = UV(t);
                for (int i = Mathf.Max(0, Mathf.FloorToInt((uv.X - 3f - U0) / Cell)); i <= Mathf.Min(nu - 1, Mathf.CeilToInt((uv.X + 3f - U0) / Cell)); i++)
                    for (int j = Mathf.Max(0, Mathf.FloorToInt((uv.Y - 3f - V0) / Cell)); j <= Mathf.Min(nv - 1, Mathf.CeilToInt((uv.Y + 3f - V0) / Cell)); j++)
                        if (!shut[i, j] && !greened[i, j] && Flat(W(i, j), t) <= 3f) { greened[i, j] = true; DeadSpaceGreened++; }
            }
            tLay = clock.ElapsedMilliseconds - tLay;
            int rows = 0; float rowLen = 0f;
            foreach (var run in HedgeRuns) if (run.Kind == "row") { rows++; rowLen += RunLength(run); }
            int trees = 0, benchN = 0, lotParks = 0; foreach (var pk in PocketParks) { trees += pk.Trees.Count; benchN += pk.Benches.Count; if (pk.Lot != null) lotParks++; }
            int loneN = LoneTrees.Count;
            Log.Print($"[towngrid] greens: {DeadSpace} m2 of dead space in {DeadSpacePieces} pieces -- {rows} hedgerows ({rowLen:0} m), "
                    + $"{PocketParks.Count} pocket parks ({lotParks} on empty lots; {trees} trees, {benchN} benches), {loneN} lone trees, {Bushes.Count} bushes, {HedgePieces.Count} hedge pieces; {DeadSpaceGreened} m2 of it greened "
                    + $"({100f * DeadSpaceGreened / Mathf.Max(1, DeadSpace):0}%) ({clock.ElapsedMilliseconds} ms: streets {tStreets}, slabs {tSlab}, lots {tPlots}, "
                    + $"pieces {tPieces - t0 - tStreets - tSlab - tPlots}, laying {tLay})");
        }

        const byte WhyStreet = 1, WhyLane = 2, WhySlab = 3, WhyLot = 4;
        /// <summary>Is this spot (a cell of the lot) along the lot's FRONT -- within 1.5 m of its front edge, away from its
        /// corners? A triangle lot's front is the side its zone faces (Facing), the avenue's.</summary>
        static bool AtLotFront(Plot pl, Vector3 q)
        {
            if (pl.Outline != null)
            {
                var ol = pl.Outline;
                float best = float.MaxValue; bool front = false;
                for (int e = 0; e < ol.Length; e++)
                {
                    var a = ol[e]; var b = ol[(e + 1) % ol.Length];
                    var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = Mathf.Max(1e-6f, ab.LengthSquared());
                    float u = Mathf.Clamp(((q.X - a.X) * ab.X + (q.Z - a.Z) * ab.Y) / l2, 0f, 1f);
                    float d = new Vector2(a.X + ab.X * u - q.X, a.Z + ab.Y * u - q.Z).Length();
                    if (d >= best) continue;
                    best = d;
                    var n = new Vector2(ab.Y, -ab.X).Normalized();
                    var mid = new Vector2((a.X + b.X) * 0.5f, (a.Z + b.Z) * 0.5f);
                    var cen = Vector2.Zero; foreach (var v in ol) cen += new Vector2(v.X, v.Z); cen /= ol.Length;
                    if (n.Dot(mid - cen) < 0f) n = -n;
                    front = n.Dot(new Vector2(pl.Facing.X, pl.Facing.Z)) > 0.7f;
                }
                return front && best < 1.5f;
            }
            var rel = q - pl.Front;
            float along = rel.X * pl.Along.X + rel.Z * pl.Along.Z, f = -(rel.X * pl.Facing.X + rel.Z * pl.Facing.Z);
            return f < 1.5f && Mathf.Abs(along) < pl.Width * 0.5f - 0.75f;
        }

        /// <summary>A chamfer distance transform in place: each cell's distance (cells, 1 and 1.414 steps) to the nearest cell
        /// that starts at 0, two passes.</summary>
        static void Chamfer(float[,] dt, int nu, int nv)
        {
            const float D1 = 1f, D2 = 1.41421f;
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    if (dt[i, j] == 0f) continue;
                    float m = dt[i, j];
                    if (i > 0) m = Mathf.Min(m, dt[i - 1, j] + D1);
                    if (j > 0) m = Mathf.Min(m, dt[i, j - 1] + D1);
                    if (i > 0 && j > 0) m = Mathf.Min(m, dt[i - 1, j - 1] + D2);
                    if (i + 1 < nu && j > 0) m = Mathf.Min(m, dt[i + 1, j - 1] + D2);
                    dt[i, j] = m;
                }
            for (int i = nu - 1; i >= 0; i--)
                for (int j = nv - 1; j >= 0; j--)
                {
                    if (dt[i, j] == 0f) continue;
                    float m = dt[i, j];
                    if (i + 1 < nu) m = Mathf.Min(m, dt[i + 1, j] + D1);
                    if (j + 1 < nv) m = Mathf.Min(m, dt[i, j + 1] + D1);
                    if (i + 1 < nu && j + 1 < nv) m = Mathf.Min(m, dt[i + 1, j + 1] + D2);
                    if (i > 0 && j + 1 < nv) m = Mathf.Min(m, dt[i - 1, j + 1] + D2);
                    dt[i, j] = m;
                }
        }

        static float RunLength(HedgeRun run)
        {
            float l = 0f;
            for (int k = 1; k < run.Pts.Count; k++) l += Flat(run.Pts[k - 1], run.Pts[k]);
            if (run.Closed && run.Pts.Count > 2) l += Flat(run.Pts[^1], run.Pts[0]);
            return l;
        }
        /// <summary>Inside a park's hedge line (the hedge run's points as a polygon; an open run closed across its gate).</summary>
        static bool InsidePark(PocketPark pk, Vector3 q)
        {
            var p = pk.Hedge?.Pts;
            if (p == null || p.Count < 3) return false;
            bool inside = false;
            for (int a = 0, b = p.Count - 1; a < p.Count; b = a++)
                if ((p[a].Z > q.Z) != (p[b].Z > q.Z) && q.X < (p[b].X - p[a].X) * (q.Z - p[a].Z) / (p[b].Z - p[a].Z) + p[a].X) inside = !inside;
            return inside;
        }

        /// <summary>⭐ HEDGEROWS down a long thin piece of dead space: its SKELETON (Zhang-Suen thinning of the piece), the spurs
        /// thinning leaves at every corner pruned, and what is left traced into lines from end to end and junction to
        /// junction -- each line, simplified to the few points that hold its shape, one hedge run, as deep as the strip
        /// is wide along it.</summary>
        string PlanHedgerows(bool[,] on, int w, int h, Func<int, int, Vector3> Wl, Func<int, int, float> Dl, ref ulong seedH)
        {
            var sk = (bool[,])on.Clone();
            Thin(sk, w, h);
            // ...and the right-angle stairs thinning leaves taken down to diagonal steps: a cell whose only neighbours are two
            // at right angles to each other (N and E, say) is not needed to join them, and left in, every one of them reads
            // as a junction and cuts a line into pieces too short to hedge
            for (int i = 1; i < w - 1; i++)
                for (int j = 1; j < h - 1; j++)
                {
                    if (!sk[i, j]) continue;
                    bool n = sk[i, j - 1], e = sk[i + 1, j], so = sk[i, j + 1], we = sk[i - 1, j];
                    bool ne = sk[i + 1, j - 1], se = sk[i + 1, j + 1], sw = sk[i - 1, j + 1], nw = sk[i - 1, j - 1];
                    int cnt = (n ? 1 : 0) + (e ? 1 : 0) + (so ? 1 : 0) + (we ? 1 : 0) + (ne ? 1 : 0) + (se ? 1 : 0) + (sw ? 1 : 0) + (nw ? 1 : 0);
                    if (cnt != 2) continue;
                    if (n && e || e && so || so && we || we && n) sk[i, j] = false;
                }
            int Deg(int i, int j)
            {
                int n = 0;
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                    if ((di != 0 || dj != 0) && i + di >= 0 && j + dj >= 0 && i + di < w && j + dj < h && sk[i + di, j + dj]) n++;
                return n;
            }
            // spurs: an end that reaches a junction within a few metres is a corner's whisker, not a run
            for (int pass = 0; pass < 3; pass++)
            {
                bool any = false;
                for (int i = 0; i < w; i++)
                    for (int j = 0; j < h; j++)
                    {
                        if (!sk[i, j] || Deg(i, j) != 1) continue;
                        var path = new List<(int, int)> { (i, j) };
                        int pi = i, pj = j, li = -1, lj = -1; bool hitJunction = false;
                        while (path.Count < 6)
                        {
                            int ni = -1, nj = -1;
                            for (int di = -1; di <= 1 && ni < 0; di++) for (int dj = -1; dj <= 1; dj++)
                            {
                                int a = pi + di, b = pj + dj;
                                if ((di == 0 && dj == 0) || a < 0 || b < 0 || a >= w || b >= h || !sk[a, b] || (a == li && b == lj) || path.Contains((a, b))) continue;
                                ni = a; nj = b; break;
                            }
                            if (ni < 0) break;
                            if (Deg(ni, nj) >= 3) { hitJunction = true; break; }
                            li = pi; lj = pj; pi = ni; pj = nj; path.Add((pi, pj));
                        }
                        if (hitJunction && path.Count < 5) { foreach (var (a, b) in path) sk[a, b] = false; any = true; }
                    }
                if (!any) break;
            }
            // trace: from each end or junction out along each arm to the next end or junction
            var used = new HashSet<((int, int), (int, int))>();
            bool Node(int i, int j) { int d = Deg(i, j); return d != 2; }
            var lines = new List<List<(int, int)>>();
            for (int i = 0; i < w; i++)
                for (int j = 0; j < h; j++)
                {
                    if (!sk[i, j] || !Node(i, j) || Deg(i, j) == 0) continue;
                    for (int di = -1; di <= 1; di++)
                        for (int dj = -1; dj <= 1; dj++)
                        {
                            int a = i + di, b = j + dj;
                            if ((di == 0 && dj == 0) || a < 0 || b < 0 || a >= w || b >= h || !sk[a, b]) continue;
                            if (used.Contains(((i, j), (a, b)))) continue;
                            var line = new List<(int, int)> { (i, j) };
                            int pi = i, pj = j, ci = a, cj = b;
                            used.Add(((pi, pj), (ci, cj))); used.Add(((ci, cj), (pi, pj)));
                            while (true)
                            {
                                line.Add((ci, cj));
                                if (Node(ci, cj)) break;
                                int ni = -1, nj = -1;
                                for (int ei = -1; ei <= 1 && ni < 0; ei++) for (int ej = -1; ej <= 1; ej++)
                                {
                                    int x = ci + ei, y = cj + ej;
                                    if ((ei == 0 && ej == 0) || x < 0 || y < 0 || x >= w || y >= h || !sk[x, y] || (x == pi && y == pj)) continue;
                                    if (used.Contains(((ci, cj), (x, y)))) continue;
                                    ni = x; nj = y; break;
                                }
                                if (ni < 0) break;
                                used.Add(((ci, cj), (ni, nj))); used.Add(((ni, nj), (ci, cj)));
                                pi = ci; pj = cj; ci = ni; cj = nj;
                            }
                            lines.Add(line);
                        }
                }
            var why = new System.Text.StringBuilder($"skeleton {lines.Count} lines:");
            foreach (var line in lines)
            {
                var pts = new List<Vector3>(); var widths = new List<float>();
                foreach (var (i, j) in line) { pts.Add(Wl(i, j)); widths.Add(2f * Dl(i, j) - 1f); }
                float len = 0f; for (int k = 1; k < pts.Count; k++) len += Flat(pts[k - 1], pts[k]);
                widths.Sort();
                float width = widths[widths.Count / 2];
                if (ProbeEverything && why.Length < 300) why.Append($" {len:0.0}m/{width:0.0}w");
                if (len < RowMinLength || width < RowMinWidth) continue;
                float depth = Mathf.Clamp(width - 0.5f, HedgeDepthMin, HedgeDepthMax);
                var sp = Simplify(pts, 0.6f);
                // its ends: into a junction, on by half a hedge so the arms meeting there close up; at the strip's end, on to
                // within half a hedge and a little of it -- the skeleton stops a half-width short of the ground's end
                void Reach(int endIdx, int prevIdx, int pixel)
                {
                    var d = new Vector3(sp[endIdx].X - sp[prevIdx].X, 0f, sp[endIdx].Z - sp[prevIdx].Z);
                    if (d.LengthSquared() < 1e-6f) return;
                    d = d.Normalized();
                    var (pi, pj) = line[pixel];
                    float ext = Deg(pi, pj) >= 3 ? depth * 0.5f : Mathf.Clamp(Dl(pi, pj) - 0.5f - depth * 0.5f - 0.3f, 0f, 2f);
                    sp[endIdx] += d * ext;
                }
                Reach(0, 1, 0);
                Reach(sp.Count - 1, sp.Count - 2, line.Count - 1);
                // straight, with hard corners square to the grid (strawberry 2026-10-14: "try to have them do 90s instead of
                // curves, since the hedges are square")
                sp = Rectify(sp, false);
                seedH = Mix(seedH + 0x4ED6E5UL);
                if (Unit(seedH, 8) < RowHedgeChance) { HedgeRuns.Add(new HedgeRun { Pts = sp, Closed = false, Kind = "row", Depth = depth }); continue; }
                // ...or, as often, a line of bushes down it instead -- as big as the strip lets them be
                float bs = Mathf.Clamp((width - 0.4f) / (2f * BushRadius), 0.25f, BushScale);
                if (bs * BushRadius * 2f < 1f) continue;
                BushesAlong(sp, false, bs, bs * BushRadius * 2f + 2.5f, ref seedH);
                if (ProbeEverything) why.Append(" (bushes)");
            }
            return why.ToString();
        }

        /// <summary>Bushes down a line, `spacing` apart from half a spacing in, clear of the bushes and trees already stood.</summary>
        void BushesAlong(List<Vector3> line, bool closed, float scale, float spacing, ref ulong seedH)
        {
            var pts = new List<Vector3>(line); if (closed && line.Count > 2) pts.Add(line[0]);
            float total = 0f; for (int k = 1; k < pts.Count; k++) total += Flat(pts[k - 1], pts[k]);
            for (float t = spacing * 0.5f; t <= total + 1e-3f; t += spacing)
            {
                float left = t; var q = pts[^1];
                for (int k = 1; k < pts.Count; k++)
                {
                    float l = Flat(pts[k - 1], pts[k]);
                    if (left <= l) { q = pts[k - 1].Lerp(pts[k], left / Mathf.Max(1e-4f, l)); break; }
                    left -= l;
                }
                bool clear = true;
                foreach (var (bq, _, sc) in Bushes) if (Flat(bq, q) < (sc + scale) * BushRadius + 0.5f) { clear = false; break; }
                foreach (var t2 in LoneTrees) if (Flat(t2, q) < 2.5f) { clear = false; break; }
                if (!clear) continue;
                seedH = Mix(seedH + 0xB0B0UL);
                Bushes.Add((q, BushProps[(int)(seedH % (ulong)BushProps.Length)], scale * (0.85f + 0.3f * Unit(seedH, 20))));
            }
        }

        /// <summary>A plan direction's angle in the GRID's frame (radians, 0 along its streets' u axis), and back.</summary>
        float GridAngle(Vector3 d) { float c = Mathf.Cos(Bearing), s = Mathf.Sin(Bearing); return Mathf.Atan2(-d.X * s + d.Z * c, d.X * c + d.Z * s); }
        Vector3 FromGridAngle(float a) { float c = Mathf.Cos(Bearing), s = Mathf.Sin(Bearing), u = Mathf.Cos(a), v = Mathf.Sin(a); return new Vector3(u * c - v * s, 0f, u * s + v * c); }

        /// <summary>⭐ A LINE SQUARED UP (strawberry 2026-10-14: "try to have them do 90s instead of curves, since the hedges are
        /// square"). Simplified to its few straight sides; each side within 15 degrees of the grid's streets or the 45-degree
        /// avenue turned onto it; sides that end up parallel and touching merged into one; jogs under 1.5 m taken out; and
        /// the corners where the squared sides meet -- 90 degrees between the grid's, 45 where the avenue's come in. The
        /// raster's line back as it was if squaring would move a corner more than 2.5 m off it.</summary>
        List<Vector3> Rectify(List<Vector3> pts, bool closed)
        {
            var sp = Simplify(pts, 0.8f, closed);
            // (a side under 1.5 m is a stair, not a side: its two ends become one point)
            for (int pass = 0; pass < 3 && sp.Count > (closed ? 3 : 2); pass++)
            {
                bool any = false;
                int segs = closed ? sp.Count : sp.Count - 1;
                for (int k = 0; k < segs && sp.Count > (closed ? 3 : 2); k++)
                {
                    int k2 = (k + 1) % sp.Count;
                    if (Flat(sp[k], sp[k2]) >= 1.5f) continue;
                    if (!closed && (k == 0 || k2 == sp.Count - 1)) continue;     // (a line's ends stay where they are)
                    sp[k] = (sp[k] + sp[k2]) * 0.5f; sp.RemoveAt(k2); any = true; break;
                }
                if (!any) break;
            }
            int m = sp.Count;
            if (m < 2 || closed && m < 3) return sp;
            var lines = new List<(Vector3 p, Vector3 d, float len)>();
            int n = closed ? m : m - 1;
            for (int k = 0; k < n; k++)
            {
                var a = sp[k]; var b = sp[(k + 1) % m];
                var d = new Vector3(b.X - a.X, 0f, b.Z - a.Z); float len = d.Length();
                if (len < 1e-3f) continue;
                float ang = GridAngle(d), snapped = Mathf.Round(ang / (Mathf.Pi * 0.25f)) * (Mathf.Pi * 0.25f);
                var dir = Mathf.Abs(Mathf.AngleDifference(ang, snapped)) < Mathf.DegToRad(15f) ? FromGridAngle(snapped) : d / len;
                var mid = (a + b) * 0.5f; mid.Y = 0f;
                if (lines.Count > 0 && lines[^1].d.Dot(dir) > 0.999f)
                {
                    var pv = lines[^1]; var nrm = new Vector3(-dir.Z, 0f, dir.X);
                    float off = (pv.p.Dot(nrm) * pv.len + mid.Dot(nrm) * len) / (pv.len + len);
                    lines[^1] = (pv.p + nrm * (off - pv.p.Dot(nrm)), dir, pv.len + len);
                }
                else lines.Add((mid, dir, len));
            }
            if (closed && lines.Count > 2 && lines[^1].d.Dot(lines[0].d) > 0.999f)
            {
                var pv = lines[^1]; var f = lines[0]; var nrm = new Vector3(-f.d.Z, 0f, f.d.X);
                float off = (pv.p.Dot(nrm) * pv.len + f.p.Dot(nrm) * f.len) / (pv.len + f.len);
                lines[0] = (f.p + nrm * (off - f.p.Dot(nrm)), f.d, pv.len + f.len);
                lines.RemoveAt(lines.Count - 1);
            }
            if (lines.Count < (closed ? 3 : 1)) return sp;
            Vector3 Onto(Vector3 q, (Vector3 p, Vector3 d, float len) l) => l.p + l.d * (q - l.p).Dot(l.d);
            var outp = new List<Vector3>();
            if (!closed) outp.Add(Onto(sp[0], lines[0]));
            int corners = closed ? lines.Count : lines.Count - 1;
            for (int k = 0; k < corners; k++)
            {
                var l1 = lines[k]; var l2 = lines[(k + 1) % lines.Count];
                float den = l1.d.X * l2.d.Z - l1.d.Z * l2.d.X;
                if (Mathf.Abs(den) < 0.05f) { outp.Add(Onto(l2.p - l2.d * (l2.len * 0.5f), l1)); continue; }
                var wv = l2.p - l1.p;
                outp.Add(l1.p + l1.d * ((wv.X * l2.d.Z - wv.Z * l2.d.X) / den));
            }
            if (!closed) outp.Add(Onto(sp[^1], lines[^1]));
            // (squaring must not wander off the ground it was traced on)
            foreach (var v in outp)
            {
                float best = float.MaxValue;
                for (int k = 0; k < (closed ? sp.Count : sp.Count - 1); k++)
                {
                    var a = sp[k]; var b = sp[(k + 1) % sp.Count];
                    var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2s = Mathf.Max(1e-6f, ab.LengthSquared());
                    var av = new Vector2(v.X - a.X, v.Z - a.Z);
                    best = Mathf.Min(best, (av - ab * Mathf.Clamp(av.Dot(ab) / l2s, 0f, 1f)).Length());
                }
                if (best > 2.5f) return sp;
            }
            return outp;
        }

        /// <summary>⭐ A POCKET PARK in a piece of dead space big enough to stand in: a hedge round it ParkHedgeInset in from its
        /// edge (the outline of the ground at least that far from anything a lot could not have, traced and simplified),
        /// open for ParkGateWidth where it comes nearest a street, and trees in its middle, ParkTreeSpacing apart.</summary>
        void PlanPocketPark(RoadField roads, bool[,] on, int w, int h, Func<int, int, Vector3> Wl, Func<int, int, float> Dl, int area, ref ulong seedH,
                            float inset = -1f, Plot lot = null)
        {
            if (inset < 0f) inset = ParkHedgeInset;
            // the ground the hedge line goes round: far enough in, the biggest piece of it
            var inner = new bool[w, h];
            for (int i = 0; i < w; i++) for (int j = 0; j < h; j++) inner[i, j] = on[i, j] && Dl(i, j) >= inset + 0.5f;
            var best = new List<(int, int)>();
            var seen = new bool[w, h];
            for (int i = 0; i < w; i++)
                for (int j = 0; j < h; j++)
                {
                    if (!inner[i, j] || seen[i, j]) continue;
                    var piece = new List<(int, int)>(); var st = new Stack<(int, int)>(); st.Push((i, j)); seen[i, j] = true;
                    while (st.Count > 0)
                    {
                        var (a, b) = st.Pop(); piece.Add((a, b));
                        foreach (var (x, y) in new[] { (a + 1, b), (a - 1, b), (a, b + 1), (a, b - 1) })
                            if (x >= 0 && y >= 0 && x < w && y < h && inner[x, y] && !seen[x, y]) { seen[x, y] = true; st.Push((x, y)); }
                    }
                    if (piece.Count > best.Count) best = piece;
                }
            if (best.Count < 12) return;
            var keepMask = new bool[w, h]; foreach (var (a, b) in best) keepMask[a, b] = true;
            var loop = TraceOutline(keepMask, w, h);
            if (loop == null || loop.Count < 8) return;
            var pts = new List<Vector3>(); foreach (var (a, b) in loop) pts.Add(Wl(a, b));
            pts = Rectify(pts, true);
            if (pts.Count < 3) return;                     // (a triangle lot's park line is a triangle)
            var mid = Vector3.Zero; foreach (var (a, b) in best) mid += Wl(a, b); mid /= best.Count;
            float twice = 0f; for (int k = 0; k < pts.Count; k++) { var a = pts[k]; var b = pts[(k + 1) % pts.Count]; twice += a.X * b.Z - b.X * a.Z; }
            // which sides are its STREET side: along a street's frontage, within ParkStreetReach of it
            float FrontageGap(Vector3 q)
            {
                float g = float.MaxValue;
                foreach (int r in Built)
                {
                    RoadNear(roads, r, q, out _, out float off);
                    g = Mathf.Min(g, off - (roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback));
                }
                return g;
            }
            int sides = pts.Count;
            var street = new bool[sides];
            int longest = -1; float longestLen = 0f;
            for (int k = 0; k < sides; k++)
            {
                var a = pts[k]; var b = pts[(k + 1) % sides];
                var g = new[] { FrontageGap(a.Lerp(b, 0.25f)), FrontageGap(a.Lerp(b, 0.5f)), FrontageGap(a.Lerp(b, 0.75f)) };
                System.Array.Sort(g);
                street[k] = g[1] <= ParkStreetReach;
                if (street[k] && Flat(a, b) > longestLen) { longestLen = Flat(a, b); longest = k; }
            }
            // its way in: the middle of its longest street side (no street side: the side nearest one)
            if (longest < 0)
            {
                float bestGap = float.MaxValue;
                for (int k = 0; k < sides; k++) { float g = FrontageGap(pts[k].Lerp(pts[(k + 1) % sides], 0.5f)); if (g < bestGap) { bestGap = g; longest = k; } }
            }
            var ga = pts[longest]; var gb = pts[(longest + 1) % sides];
            var gateAt = ga.Lerp(gb, 0.5f);
            seedH = Mix(seedH + 0x09E7UL);
            bool open = Unit(seedH, 12) < ParkOpenChance;
            float depthHere = lot != null ? HedgeDepthMin + 0.2f : HedgeCoreDepth;
            if (!open)
            {
                // each run of street sides, one hedge, cut open across the way in
                var runs = new List<List<Vector3>>();
                bool all = true; for (int k = 0; k < sides; k++) if (!street[k]) all = false;
                var dirG = (gb - ga).Normalized();
                float hw = Mathf.Max(0.5f, Mathf.Min(ParkGateWidth * 0.5f, Flat(ga, gb) * 0.5f - 0.5f));
                if (all)
                {
                    // (street all round: one hedge from just past the way in, round every corner, to just before it)
                    var c = new List<Vector3> { gateAt + dirG * hw };
                    for (int q = 1; q <= sides; q++) c.Add(pts[(longest + q) % sides]);
                    c.Add(gateAt - dirG * hw);
                    runs.Add(c);
                }
                else
                {
                    int s0 = 0; while (street[s0]) s0++;
                    List<Vector3> cur = null;
                    for (int q = 1; q <= sides; q++)
                    {
                        int k = (s0 + q) % sides;
                        if (!street[k]) { if (cur != null) { runs.Add(cur); cur = null; } continue; }
                        if (k == longest)
                        {
                            // the way in: this chain ends just before it, the next begins just past it
                            cur ??= new List<Vector3> { pts[k] };
                            cur.Add(gateAt - dirG * hw); runs.Add(cur);
                            cur = new List<Vector3> { gateAt + dirG * hw, pts[(k + 1) % sides] };
                            continue;
                        }
                        cur ??= new List<Vector3> { pts[k] };
                        cur.Add(pts[(k + 1) % sides]);
                    }
                    if (cur != null) runs.Add(cur);
                }
                foreach (var c in runs)
                    if (c.Count >= 2 && RunLength(new HedgeRun { Pts = c }) >= 2f) HedgeRuns.Add(new HedgeRun { Pts = c, Kind = "park", Depth = depthHere });
            }
            var pk = new PocketPark { Centre = mid, Entrance = gateAt, Area = area, Lot = lot, Hedge = new HedgeRun { Pts = pts, Closed = true, Kind = "park-line" } };
            // bushes round its edge, a little in from it: in front of the hedge where there is one, along the open sides
            // where there is not -- the way in left clear
            {
                float bsc = lot != null ? BushScale * 0.8f : BushScale;
                float br = BushRadius * bsc;
                int before = Bushes.Count, cap = lot != null ? Mathf.Clamp(area / 80, 1, 4) : Mathf.Clamp(area / 60, 2, 16);
                for (int k = 0; k < sides && Bushes.Count - before < cap; k++)
                {
                    var a = pts[k]; var b = pts[(k + 1) % sides];
                    var d = (b - a); float len = d.Length(); if (len < 2f) continue;
                    d /= len;
                    var inN = twice > 0f ? new Vector3(-d.Z, 0f, d.X) : new Vector3(d.Z, 0f, -d.X);
                    float inBy = (street[k] && !open ? depthHere * 0.5f + 0.4f : 0f) + br + 0.3f;
                    var line = new List<Vector3> { a + d * Mathf.Min(2.5f, len * 0.3f) + inN * inBy, b - d * Mathf.Min(2.5f, len * 0.3f) + inN * inBy };
                    int got0 = Bushes.Count;
                    BushesAlong(line, false, bsc, 2f * br + (street[k] && !open ? 5f : 3.5f), ref seedH);
                    // (none in the way in, none outside the park line)
                    for (int q = Bushes.Count - 1; q >= got0; q--)
                        if (Flat(Bushes[q].at, gateAt) < ParkGateWidth * 0.5f + br + 1.5f || !InsidePark(new PocketPark { Hedge = new HedgeRun { Pts = pts } }, Bushes[q].at)) Bushes.RemoveAt(q);
                    while (Bushes.Count - before > cap) Bushes.RemoveAt(Bushes.Count - 1);
                }
            }
            // trees: well inside the hedge, ParkTreeSpacing apart, in an order the seed picks
            var cand = new List<(int, int)>();
            foreach (var (a, b) in best) if (Dl(a, b) >= ParkTreeInset + inset * 0.5f) cand.Add((a, b));
            for (int k = cand.Count - 1; k > 0; k--)
            {
                seedH = Mix(seedH + 0x9E3779B97F4A7C15UL);
                int m = (int)(seedH % (ulong)(k + 1));
                (cand[k], cand[m]) = (cand[m], cand[k]);
            }
            pk.TreeWant = lot != null ? Mathf.Clamp(area / 70, 1, 6) : Mathf.Clamp(area / 90, 1, 12);
            foreach (var (a, b) in cand)
            {
                var q = Wl(a, b);
                if (Flat(q, gateAt) < ParkTreeSpacing * 0.6f) continue;           // the way in stays clear
                bool clear = true;
                foreach (var (bq, _, _) in Bushes) if (Flat(bq, q) < BushRadius * BushScale + 2f) { clear = false; break; }
                if (clear) pk.TreeSpots.Add(q);
            }
            PocketParks.Add(pk);
        }

        /// <summary>⭐ EDGE TO EDGE (strawberry 2026-10-14, of the first hedges: "make sure the spline hedges meet perfectly edge
        /// to edge with no gap" -- then "try to have them do 90s instead of curves, since the hedges are square"). One run,
        /// side by side: each straight side cut into whole pieces as near the hedge's 4 m core as fits, butted face to face
        /// -- and at every corner the pieces either side run on past it by half their depth times tan(half the turn): the
        /// SplineTiling joint rule, which at a square corner is half the depth each way, so the two cores between them fill
        /// the corner's square exactly instead of leaving its outer notch open.</summary>
        void LayHedge(HedgeRun run, ref ulong seedH)
        {
            foreach (var hp in PiecesAlong(run.Pts, run.Closed, run.Depth, ref seedH)) { hp.Run = run; HedgePieces.Add(hp); }
        }
        List<HedgePiece> PiecesAlong(List<Vector3> line, bool closed, float depth, ref ulong seedH)
        {
            var outp = new List<HedgePiece>();
            var pts = new List<Vector3>();
            foreach (var v in line) if (pts.Count == 0 || Flat(pts[^1], v) > 0.05f) pts.Add(new Vector3(v.X, 0f, v.Z));
            if (closed && pts.Count > 2 && Flat(pts[0], pts[^1]) < 0.05f) pts.RemoveAt(pts.Count - 1);
            int m = pts.Count;
            if (m < 2) return outp;
            int sides = closed ? m : m - 1;
            var dir = new Vector3[sides];
            for (int k = 0; k < sides; k++) dir[k] = (pts[(k + 1) % m] - pts[k]).Normalized();
            float half = depth * 0.5f;
            // how far the pieces run on past corner k (the start of side k)
            float Ext(int k)
            {
                if (!closed && (k == 0 || k == sides)) return 0f;
                var a = dir[(k - 1 + sides) % sides]; var b = dir[k % sides];
                return half * Mathf.Tan(Mathf.Min(a.AngleTo(b), Mathf.DegToRad(100f)) * 0.5f);
            }
            float yAt(float x, float z) => _heightAt != null ? _heightAt(x, z) : Centre.Y;
            for (int k = 0; k < sides; k++)
            {
                var a = pts[k]; var b = pts[(k + 1) % m];
                float len = Flat(a, b);
                if (len < 0.3f) continue;
                int n = Mathf.Max(1, Mathf.RoundToInt(len / HedgeCoreLength));
                float step = len / n, e0 = Ext(k), e1 = Ext(k + 1);
                for (int q = 0; q < n; q++)
                {
                    float x0 = q * step - (q == 0 ? e0 : 0f), x1 = (q + 1) * step + (q == n - 1 ? e1 : 0f);
                    var mid = a + dir[k] * ((x0 + x1) * 0.5f); mid.Y = yAt(mid.X, mid.Z);
                    seedH = Mix(seedH + 0xED6EUL);
                    outp.Add(new HedgePiece { At = mid, Dir = dir[k], Length = x1 - x0, Depth = depth, Flip = (seedH & 1UL) != 0 });
                }
            }
            return outp;
        }

        /// <summary>The basis a hedge piece stands at: Hedge_0 stood up the way retail stands it (270 degrees about X: it is a
        /// Z-up mesh), its core's length -- local X -- along `dir`, stretched to `lengthScale` of its 4 m and `depthScale` of
        /// its 1 m; `flip` turns it end for end, as retail's rows are (yaws 90 and 270 both).</summary>
        public static Basis HedgeBasis(Vector3 dir, float lengthScale, float depthScale, bool flip)
        {
            // FromEuler = Y(180 - ey) * X(ex) * Z(-ez): with ex 270, local X lands at (cos p, 0, -sin p), p = 180 - ey
            float p = Mathf.Atan2(-dir.Z, dir.X) + (flip ? Mathf.Pi : 0f);
            return EditorObjects.FromEuler(270f, 180f - Mathf.RadToDeg(p), 0f) * Basis.FromScale(new Vector3(lengthScale, depthScale, 1f));
        }

        /// <summary>⭐ BENCHES ONLY WHERE THERE IS ROOM FOR ONE (strawberry 2026-10-14: "make sure benches have actual space instead
        /// of just shoving them. most dead space doesnt need benches"). Only a real park gets them -- BenchMinParkArea and up,
        /// not an empty lot's -- and each needs its whole pad: BenchPadWidth along it, BenchPadDepth from just behind its back to
        /// the end of the legroom in front, inside the hedge line and BenchClear off every hedge core, tree trunk and bush.
        /// The nearest such spot to the way in, facing the park's middle; a second only in a park twice that size, 8 m off.</summary>
        void PlanBenches()
        {
            // ⚠ BEFORE THE TREES: a bench placed after them could only squeeze in among the trunks, under the crowns -- the
            // "shoved" one. Placed first it takes its patch of open lawn by the way in, and the trees keep out of it.
            foreach (var pk in PocketParks)
            {
                if (pk.Lot != null || pk.Area < BenchMinParkArea || pk.Hedge == null) continue;
                int want = pk.Area >= 2f * BenchMinParkArea ? 2 : 1;
                var line = pk.Hedge.Pts;
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                foreach (var v in line) { x0 = Mathf.Min(x0, v.X); x1 = Mathf.Max(x1, v.X); z0 = Mathf.Min(z0, v.Z); z1 = Mathf.Max(z1, v.Z); }
                var cand = new List<Vector3>();
                for (float x = x0; x <= x1; x += 0.5f)
                    for (float z = z0; z <= z1; z += 0.5f)
                    {
                        var q = new Vector3(x, 0f, z);
                        float g = Flat(q, pk.Entrance);
                        if (g < 5f || g > 15f || !InsidePark(pk, q)) continue;      // (in from the way in, not across it)
                        cand.Add(q);
                    }
                cand.Sort((a, b) => Flat(a, pk.Entrance).CompareTo(Flat(b, pk.Entrance)));
                foreach (var q in cand)
                {
                    if (pk.Benches.Count >= want) break;
                    var faces = new Vector3(pk.Centre.X - q.X, 0f, pk.Centre.Z - q.Z);
                    if (faces.LengthSquared() < 1f) continue;
                    faces = faces.Normalized();
                    var along = new Vector3(-faces.Z, 0f, faces.X);
                    bool ok = true;
                    foreach (var (bq, _) in pk.Benches) if (Flat(bq, q) < 8f) { ok = false; break; }
                    // the pad, sampled every half metre: inside the hedge line, clear of everything standing in the park
                    for (float u = -BenchPadWidth * 0.5f; u <= BenchPadWidth * 0.5f + 1e-3f && ok; u += 0.5f)
                        for (float v = -0.9f; v <= BenchPadDepth - 0.9f + 1e-3f && ok; v += 0.5f)
                        {
                            var s = q + along * u + faces * v;
                            if (!InsidePark(pk, s)) { ok = false; break; }
                            foreach (var hp in HedgePieces)
                            {
                                if (Flat(hp.At, s) > hp.Length * 0.5f + hp.Depth + BenchClear + 1f) continue;
                                var rel = new Vector3(s.X - hp.At.X, 0f, s.Z - hp.At.Z);
                                float a = Mathf.Abs(rel.Dot(hp.Dir)) - hp.Length * 0.5f, c = Mathf.Abs(rel.Dot(new Vector3(-hp.Dir.Z, 0f, hp.Dir.X))) - hp.Depth * 0.5f;
                                float dd = new Vector2(Mathf.Max(a, 0f), Mathf.Max(c, 0f)).Length();
                                if (dd < BenchClear + 0.4f) { ok = false; break; }      // (+ its leaves, 0.4 m past the core)
                            }
                            if (!ok) break;

                            foreach (var (bq, _, sc) in Bushes) if (Flat(bq, s) < BushRadius * sc + BenchClear) { ok = false; break; }
                        }
                    if (ok) pk.Benches.Add((q, faces));
                }
                if (ProbeEverything)
                    Log.Print($"[towngrid]   park at ({pk.Centre.X:0},{pk.Centre.Z:0}), {pk.Area:0} m2, way in at ({pk.Entrance.X:0},{pk.Entrance.Z:0}): {pk.Benches.Count} of {want} benches"
                            + string.Concat(pk.Benches.ConvertAll(b => $" ({b.at.X:0.0},{b.at.Z:0.0}) facing ({b.faces.X:0.00},{b.faces.Z:0.00})")));
            }
        }

        /// <summary>Plant what the last PlanGreens laid: each hedge piece (the hedge prop stretched to its length and depth,
        /// stood on the ground's slope up to 8 degrees) and each park's trees. Returns the props placed.</summary>
        /// <summary>Paint the town's dead space DIRT (layer 0) -- a poor town's weedy, unkept gaps (strawberry 2026-10-19). Over
        /// the town's footprint, a splat texel takes dirt when a dead-space cell sits within ~2.5 m of it. Call after PlantGreens
        /// (its cells are gathered there) and after PaintFootprints (so the roads' own dirt is already down and this adds to it).</summary>
        public int PaintDeadSpaceDirt(Terrain terr)
        {
            if (terr == null || _deadCells.Count == 0) return 0;
            bool Dead(float x, float z)
            {
                int cx = Mathf.RoundToInt(x), cz = Mathf.RoundToInt(z);
                for (int dx = -2; dx <= 2; dx++)
                    for (int dz = -2; dz <= 2; dz++)
                        if (_deadCells.Contains((cx + dx, cz + dz))) return true;
                return false;
            }
            float r = HalfSpan + 20f;
            int n = terr.PaintWhere(Centre.X - r, Centre.Z - r, Centre.X + r, Centre.Z + r, Dead, 0);
            Log.Print($"[towngrid] dead-space dirt: {n} texels painted over {_deadCells.Count} cells (poor town)");
            return n;
        }

        public int PlantGreens(RoadField roads, EditorObjects objs, List<Node3D> into = null)
        {
            if (objs == null) return 0;
            int made = 0;
            var terr = Terrain.Active;
            // onto the ground's slope, up to 8 degrees
            Basis Tilt(Vector3 at)
            {
                if (terr == null) return Basis.Identity;
                var nrm = terr.NormalAt(at.X, at.Z);
                var axis = Vector3.Up.Cross(nrm);
                if (axis.LengthSquared() < 1e-8f) return Basis.Identity;
                return new Basis(axis.Normalized(), Mathf.Min(Vector3.Up.AngleTo(nrm), Mathf.DegToRad(8f)));
            }
            void Put(string prop, Vector3 at, Basis b)
            {
                var node = objs.Place(prop, at, b);
                if (node == null) return;
                into?.Add(node); made++;
            }
            foreach (var hp in HedgePieces)
            {
                var at = hp.At;
                if (terr != null) at.Y = terr.SampleHeight(at.X, at.Z);
                Put(HedgeProp, at, Tilt(at) * HedgeBasis(hp.Dir, hp.Length / HedgeCoreLength, hp.Depth / HedgeCoreDepth, hp.Flip));
            }
            ulong h = Mix(((ulong)(uint)Seed << 32) ^ 0x7EE5BEEFUL);
            // (a small park's tree is a smaller one: a full-size maple over a 170 m2 lot roofs all of it)
            var treesAt = new List<(Vector3 at, float scale)>();
            foreach (var pk in PocketParks) foreach (var t in pk.Trees) treesAt.Add((t, pk.Area < 400f ? ParkTreeScale * 0.7f : ParkTreeScale));
            foreach (var t in LoneTrees) treesAt.Add((t, ParkTreeScale * 0.8f));
            foreach (var (t, scale) in treesAt)
            {
                h = Mix(h + 0x9E3779B97F4A7C15UL);
                var at = t; at.Y = terr != null ? terr.SampleHeight(t.X, t.Z) : t.Y;
                string tree = ParkTrees[(int)(h % (ulong)ParkTrees.Length)];
                float ts = scale * (0.85f + 0.3f * Unit(h, 32));
                // (foliage, not a prop: PlantFoliage -- let into the ground by the resource field, as the world's trees are)
                var tn = PlantFoliage(objs, tree, at, Unit(h, 16) * Mathf.Tau, ts);
                if (tn != null) into?.Add(tn);
                if (tn != null || !FoliageAsProps) made++;
            }
            // the bushes: Y-up meshes, like the trees (retail's resource bushes stand at ~0 about X)
            foreach (var (bq, prop, scale) in Bushes)
            {
                h = Mix(h + 0xB05EUL);
                var at = bq; at.Y = terr != null ? terr.SampleHeight(bq.X, bq.Z) : bq.Y;
                // (foliage too: the world's Bush_0/Bush_1 resources, which stand upright and drag a car driven through them)
                var bn = PlantFoliage(objs, prop, at, Unit(h, 8) * Mathf.Tau, scale);
                if (bn != null) into?.Add(bn);
                if (bn != null || !FoliageAsProps) made++;
            }
            foreach (var pk in PocketParks)
                foreach (var (bq, faces) in pk.Benches)
                {
                    var at = bq; at.Y = terr != null ? terr.SampleHeight(bq.X, bq.Z) : bq.Y;
                    // Bench_Wood_0: a Z-up mesh stood up at 270 about X; its back is local -Y and its sitter looks down +Y,
                    // which FromEuler(270, ey, 0) turns to (-sin p, 0, -cos p), p = 180 - ey -- so ey = 180 - YawForDir(faces)
                    Put(ParkBench, at, Tilt(at) * EditorObjects.FromEuler(270f, 180f - ProcIsland.YawForDir(faces.X, faces.Z), 0f));
                }
            if (made > 0) Log.Print($"[towngrid] greens planted: {made} props ({HedgePieces.Count} hedge pieces, {Bushes.Count} bushes)");
            return made;
        }

        /// <summary>Zhang-Suen thinning, in place: a binary mask down to its one-cell-wide skeleton, connectivity kept.</summary>
        static void Thin(bool[,] m, int w, int h)
        {
            var del = new List<(int, int)>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int step = 0; step < 2; step++)
                {
                    del.Clear();
                    for (int i = 1; i < w - 1; i++)
                        for (int j = 1; j < h - 1; j++)
                        {
                            if (!m[i, j]) continue;
                            // P2..P9 clockwise from north (j-1 is "north")
                            bool p2 = m[i, j - 1], p3 = m[i + 1, j - 1], p4 = m[i + 1, j], p5 = m[i + 1, j + 1];
                            bool p6 = m[i, j + 1], p7 = m[i - 1, j + 1], p8 = m[i - 1, j], p9 = m[i - 1, j - 1];
                            int B = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                            if (B < 2 || B > 6) continue;
                            var seq = new[] { p2, p3, p4, p5, p6, p7, p8, p9, p2 };
                            int A = 0; for (int k = 0; k < 8; k++) if (!seq[k] && seq[k + 1]) A++;
                            if (A != 1) continue;
                            if (step == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8)) continue;
                            del.Add((i, j));
                        }
                    foreach (var (i, j) in del) m[i, j] = false;
                    if (del.Count > 0) changed = true;
                }
            }
        }

        /// <summary>The outer boundary of a mask's one piece, as the cells along it in order: Moore-neighbour tracing, clockwise,
        /// stopped on coming back into the first cell the way it first left it (Jacob's criterion).</summary>
        static List<(int, int)> TraceOutline(bool[,] m, int w, int h)
        {
            int si = -1, sj = -1;
            for (int j = 0; j < h && si < 0; j++) for (int i = 0; i < w; i++) if (m[i, j]) { si = i; sj = j; break; }
            if (si < 0) return null;
            // the 8 neighbours clockwise from the west (j - 1 is north)
            int[] di = { -1, -1, 0, 1, 1, 1, 0, -1 };
            int[] dj = { 0, -1, -1, -1, 0, 1, 1, 1 };
            int DirOf(int x, int y) { for (int k = 0; k < 8; k++) if (di[k] == x && dj[k] == y) return k; return 0; }
            bool At(int i, int j) => i >= 0 && j >= 0 && i < w && j < h && m[i, j];
            var outp = new List<(int, int)> { (si, sj) };
            int ci = si, cj = sj, back = 0;                 // the start was found scanning from the west: that side is empty
            int firstBack = -1;
            for (int guard = 0; guard < 8 * w * h; guard++)
            {
                int found = -1, lastWhite = back;
                for (int k = 0; k < 8; k++)
                {
                    int d = (back + k) % 8;
                    if (At(ci + di[d], cj + dj[d])) { found = d; break; }
                    lastWhite = d;
                }
                if (found < 0) break;                       // a lone cell
                int ni = ci + di[found], nj = cj + dj[found];
                // the empty neighbour examined last, seen from the new cell, is where the next search starts
                int nb = DirOf(ci + di[lastWhite] - ni, cj + dj[lastWhite] - nj);
                if (ci == si && cj == sj)
                {
                    if (firstBack < 0) firstBack = nb * 8 + found;
                    else if (firstBack == nb * 8 + found) break;
                }
                ci = ni; cj = nj; back = nb;
                if (ci == si && cj == sj) continue;
                outp.Add((ci, cj));
            }
            return outp;
        }

        /// <summary>Douglas-Peucker in plan: the few points that hold a line's shape to within `tol` metres.</summary>
        static List<Vector3> Simplify(List<Vector3> pts, float tol, bool closed = false)
        {
            if (pts.Count < 3) return new List<Vector3>(pts);
            if (closed)
            {
                // split the loop at its two most distant points and simplify each half
                int a = 0, b = 0; float far = -1f;
                for (int k = 0; k < pts.Count; k++) { float d = Flat(pts[0], pts[k]); if (d > far) { far = d; b = k; } }
                var h1 = pts.GetRange(a, b - a + 1);
                var h2 = new List<Vector3>(pts.GetRange(b, pts.Count - b)); h2.Add(pts[0]);
                var s1 = Simplify(h1, tol); var s2 = Simplify(h2, tol);
                s1.RemoveAt(s1.Count - 1); s2.RemoveAt(s2.Count - 1);
                s1.AddRange(s2);
                return s1;
            }
            var keep = new bool[pts.Count]; keep[0] = keep[^1] = true;
            var st = new Stack<(int, int)>(); st.Push((0, pts.Count - 1));
            while (st.Count > 0)
            {
                var (i0, i1) = st.Pop();
                if (i1 - i0 < 2) continue;
                var p = pts[i0]; var q = pts[i1];
                var pq = new Vector2(q.X - p.X, q.Z - p.Z); float l2 = Mathf.Max(1e-8f, pq.LengthSquared());
                int worst = -1; float wd = tol;
                for (int k = i0 + 1; k < i1; k++)
                {
                    var r = new Vector2(pts[k].X - p.X, pts[k].Z - p.Z);
                    float t = Mathf.Clamp(r.Dot(pq) / l2, 0f, 1f);
                    float d = (r - pq * t).Length();
                    if (d > wd) { wd = d; worst = k; }
                }
                if (worst < 0) continue;
                keep[worst] = true; st.Push((i0, worst)); st.Push((worst, i1));
            }
            var outp = new List<Vector3>();
            for (int k = 0; k < pts.Count; k++) if (keep[k]) outp.Add(pts[k]);
            return outp;
        }
    }
}
