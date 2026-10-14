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
        /// <summary>One piece of hedge as it stands: its middle on the ground, its heading (degrees, YawForDir), and how long
        /// and deep it is laid -- the prop stretched to fit, so a run is whole pieces end to end, a little overlapped.</summary>
        public sealed class HedgePiece { public Vector3 At; public float Yaw; public float Length, Depth; public HedgeRun Run; }
        /// <summary>A pocket park in a piece of dead space too big for a hedgerow, or on an EMPTY LOT (one nothing on the 3 m
        /// lattice fits in, `Lot`): its hedge line (closed; the hedge itself is a run open where the way in is), the way in,
        /// its trees and benches, its area and its middle.</summary>
        public sealed class PocketPark
        {
            public Vector3 Centre, Entrance; public float Area; public HedgeRun Hedge; public Plot Lot;
            public readonly List<Vector3> Trees = new();
            public readonly List<(Vector3 at, float yaw)> Benches = new();
        }
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
        /// <summary>A piece of dead space this big (m2) and this wide somewhere (half-width, m) is a park; a strip this wide
        /// (m) and a run this long (m) gets a hedgerow; a park's hedge stands this far in from its edge (m), its trees this
        /// far apart (m) and this far in (m).</summary>
        public float ParkMinArea = 250f, ParkMinHalfWidth = 5f, RowMinWidth = 1.2f, RowMinLength = 4.5f;
        public float ParkHedgeInset = 2f, ParkTreeSpacing = 7f, ParkTreeInset = 4.5f, ParkGateWidth = 3.5f;
        /// <summary>How deep a hedge may be laid (m): the prop's own depth caps it, a narrow strip squeezes it.</summary>
        public float HedgeDepthMax = 2f, HedgeDepthMin = 0.9f;
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
            HedgeRuns.Clear(); HedgePieces.Clear(); PocketParks.Clear(); DeadSpaceCells.Clear(); LoneTrees.Clear();
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
                    if (ProbeEverything) foreach (var (i, j) in cells) DeadSpaceCells.Add(W(i, j));
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
                    else if (2f * halfMax >= RowMinWidth) { what = PlanHedgerows(on, w, h, Wl, Dl); }
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
            float hedgeLen = HedgeLength();
            foreach (var run in HedgeRuns) LayHedge(run, hedgeLen);
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
                    + $"{PocketParks.Count} pocket parks ({lotParks} on empty lots; {trees} trees, {benchN} benches), {loneN} lone trees, {HedgePieces.Count} hedge pieces; {DeadSpaceGreened} m2 of it greened "
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

        /// <summary>The hedge prop's own length (its mesh's long side), so another hedge still lays end to end.</summary>
        float HedgeLength()
        {
            var hm = ContentProvider.ParseObj($"res://content/objects/{HedgeProp}.obj");
            return hm != null ? Mathf.Max(1f, hm.GetAabb().Size.X) : 4.5f;
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
        string PlanHedgerows(bool[,] on, int w, int h, Func<int, int, Vector3> Wl, Func<int, int, float> Dl)
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
                HedgeRuns.Add(new HedgeRun { Pts = Simplify(pts, 0.6f), Closed = false, Kind = "row",
                                             Depth = Mathf.Clamp(width - 0.3f, HedgeDepthMin, HedgeDepthMax) });
            }
            return why.ToString();
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
            pts = Simplify(pts, 0.7f, closed: true);
            if (pts.Count < 3) return;                     // (a triangle lot's park line is a triangle)
            var mid = Vector3.Zero; foreach (var (a, b) in best) mid += Wl(a, b); mid /= best.Count;
            // its way in: the point of the hedge line nearest a street's frontage
            int gate = 0; float gateOff = float.MaxValue;
            for (int k = 0; k < pts.Count; k++)
                foreach (int r in Built)
                {
                    RoadNear(roads, r, pts[k], out _, out float off);
                    float o = off - (roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback);
                    if (o < gateOff) { gateOff = o; gate = k; }
                }
            var gateAt = pts[gate];
            // the hedge, opened ParkGateWidth across the gate: walk the loop from just past the gate round to just before it
            var open = OpenLoopAt(pts, gate, ParkGateWidth);
            var run = new HedgeRun { Pts = open, Closed = false, Kind = "park", Depth = HedgeDepthMax };
            HedgeRuns.Add(run);
            var pk = new PocketPark { Centre = mid, Entrance = gateAt, Area = area, Lot = lot, Hedge = new HedgeRun { Pts = pts, Closed = true, Kind = "park-line" } };
            // a bench or two just inside the way in, facing the middle
            int benches = area >= 600 ? 2 : 1;
            var bcand = new List<(int, int)>();
            foreach (var (a, b) in best)
            {
                float d = Dl(a, b), g = Flat(Wl(a, b), gateAt);
                if (d >= inset + 2f && d <= inset + 3f && g >= 2.5f && g <= 9f) bcand.Add((a, b));
            }
            bcand.Sort((x, y) => Flat(Wl(x.Item1, x.Item2), gateAt).CompareTo(Flat(Wl(y.Item1, y.Item2), gateAt)));
            foreach (var (a, b) in bcand)
            {
                if (pk.Benches.Count >= benches) break;
                var q = Wl(a, b);
                bool clear = true; foreach (var (bq, _) in pk.Benches) if (Flat(bq, q) < 4f) { clear = false; break; }
                if (!clear) continue;
                var toMid = new Vector3(mid.X - q.X, 0f, mid.Z - q.Z);
                pk.Benches.Add((q, ProcIsland.YawForDir(toMid.X, toMid.Z)));
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
            int maxTrees = Mathf.Clamp(area / 90, 1, 12);
            if (lot != null) maxTrees = Mathf.Clamp(area / 70, 1, 6);
            foreach (var (a, b) in cand)
            {
                if (pk.Trees.Count >= maxTrees) break;
                var q = Wl(a, b);
                if (Flat(q, gateAt) < ParkTreeSpacing * 0.6f) continue;           // the way in stays clear
                bool clear = true; foreach (var t in pk.Trees) if (Flat(t, q) < ParkTreeSpacing) { clear = false; break; }
                foreach (var (bq, _) in pk.Benches) if (Flat(bq, q) < 3f) { clear = false; break; }
                if (clear) pk.Trees.Add(q);
            }
            PocketParks.Add(pk);
        }

        /// <summary>A closed loop opened `gap` metres across point `at`: the points from just past the gap round to just before it.</summary>
        static List<Vector3> OpenLoopAt(List<Vector3> loop, int at, float gap)
        {
            int n = loop.Count;
            // arc length round the loop, starting at `at`
            var outp = new List<Vector3>();
            float half = gap * 0.5f;
            // forward from `at` until half the gap is used up: the run starts there
            Vector3 Cut(int from, int dir, out int next)
            {
                float left = half; int k = from;
                while (true)
                {
                    int k2 = ((k + dir) % n + n) % n;
                    float seg = Flat(loop[k], loop[k2]);
                    if (seg >= left) { next = k2; return loop[k].Lerp(loop[k2], left / Mathf.Max(1e-4f, seg)); }
                    left -= seg; k = k2;
                    if (k == from) { next = k2; return loop[k]; }
                }
            }
            var start = Cut(at, 1, out int sNext);
            var end = Cut(at, -1, out int eNext);
            outp.Add(start);
            for (int k = sNext; ; k = (k + 1) % n)
            {
                outp.Add(loop[k]);
                if (k == eNext) break;
                if (outp.Count > n + 2) break;
            }
            outp.Add(end);
            return outp;
        }

        /// <summary>Lay one hedge run piece by piece: its points smoothed into a spline (SplineTiling.CurveThrough, the curve the
        /// fence and barrier tools walk), cut into whole pieces as near the prop's own length as fits, each standing on the
        /// chord of its stretch of curve, headed along it, and stretched 6% so neighbours overlap at a bend instead of opening.</summary>
        void LayHedge(HedgeRun run, float hedgeLen)
        {
            if (run.Pts.Count < 2) return;
            var pts = new List<Vector3>(run.Pts);
            if (run.Closed) pts.Add(run.Pts[0]);
            for (int k = 0; k < pts.Count; k++) pts[k] = new Vector3(pts[k].X, 0f, pts[k].Z);
            var curve = SplineTiling.CurveThrough(pts, smooth: true);
            curve.BakeInterval = 0.25f;
            float L = curve.GetBakedLength();
            if (L < hedgeLen * 0.6f) return;
            int n = Mathf.Max(1, Mathf.RoundToInt(L / hedgeLen));
            float step = L / n;
            float yAt(float x, float z) => _heightAt != null ? _heightAt(x, z) : Centre.Y;
            for (int k = 0; k < n; k++)
            {
                var p0 = curve.SampleBaked(k * step); var p1 = curve.SampleBaked(Mathf.Min(L, (k + 1) * step));
                var d = new Vector3(p1.X - p0.X, 0f, p1.Z - p0.Z);
                float chord = d.Length();
                if (chord < 0.2f) continue;
                var at = (p0 + p1) * 0.5f; at.Y = yAt(at.X, at.Z);
                HedgePieces.Add(new HedgePiece { At = at, Yaw = ProcIsland.YawForDir(d.X, d.Z), Length = chord * 1.06f, Depth = run.Depth, Run = run });
            }
        }

        /// <summary>Plant what the last PlanGreens laid: each hedge piece (the hedge prop stretched to its length and depth,
        /// stood on the ground's slope up to 8 degrees) and each park's trees. Returns the props placed.</summary>
        public int PlantGreens(RoadField roads, EditorObjects objs, List<Node3D> into = null)
        {
            if (objs == null) return 0;
            int made = 0;
            var hm = ContentProvider.ParseObj($"res://content/objects/{HedgeProp}.obj");
            var box = hm != null ? hm.GetAabb() : new Aabb(Vector3.Zero, new Vector3(4.5f, 2f, 2f));
            float len0 = Mathf.Max(0.5f, box.Size.X), dep0 = Mathf.Max(0.5f, box.Size.Z);
            var terr = Terrain.Active;
            Basis Stand(Vector3 at, float yawDeg)
            {
                var b = new Basis(Vector3.Up, Mathf.DegToRad(yawDeg));
                if (terr == null) return b;
                var nrm = terr.NormalAt(at.X, at.Z);
                var axis = Vector3.Up.Cross(nrm);
                if (axis.LengthSquared() < 1e-8f) return b;
                return new Basis(axis.Normalized(), Mathf.Min(Vector3.Up.AngleTo(nrm), Mathf.DegToRad(8f))) * b;
            }
            foreach (var hp in HedgePieces)
            {
                var at = hp.At;
                if (terr != null) at.Y = terr.SampleHeight(at.X, at.Z);
                var b = Stand(at, hp.Yaw + HedgeYaw) * Basis.FromScale(new Vector3(hp.Length / len0, 1f, hp.Depth / dep0));
                var node = objs.Place(HedgeProp, at, b);
                if (node == null) continue;
                into?.Add(node); made++;
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
                    var b = new Basis(Vector3.Up, Unit(h, 16) * Mathf.Tau).Scaled(Vector3.One * scale * (0.85f + 0.3f * Unit(h, 32)));
                    var node = objs.Place(tree, at, b);
                    if (node == null) continue;
                    into?.Add(node); made++;
                }
            foreach (var pk in PocketParks)
                foreach (var (bq, yaw) in pk.Benches)
                {
                    var at = bq; at.Y = terr != null ? terr.SampleHeight(bq.X, bq.Z) : bq.Y;
                    // Bench_Wood_0: its back is local -Y and the sitter looks down +Y, the way YawForDir points a prop (see
                    // ProcIslandSpawn.SpawnLandmarks) -- stood up like the island's viewpoint benches
                    var node = objs.Place(ParkBench, at, ProcIslandSpawn.RotFor(yaw));
                    if (node == null) continue;
                    into?.Add(node); made++;
                }
            if (made > 0) Log.Print($"[towngrid] greens planted: {made} props ({HedgePieces.Count} hedge pieces)");
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
