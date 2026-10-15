using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // ==============================================================================================
    // YARD FENCES, BUILT (strawberry 2026-10-14: "work on getting fences as splines, might wanna split the models into
    // the individual pieces first and then solving them in engine?"). PlanFences says where; FenceSpline lays them.
    // ==============================================================================================
    public sealed partial class TownRoadGrid
    {
        /// <summary>Lay the yard fences as real fences. UG_TOWNFENCES=0 keeps the old designation line instead.</summary>
        public static bool BuildRealFences = System.Environment.GetEnvironmentVariable("UG_TOWNFENCES") != "0";

        /// <summary>The chance each stretch of a yard fence that has a broken prop is laid broken (UG_TOWNFENCEWEAR=0..1).
        /// Low: a town where every fifth panel is down reads as a scrapyard, not as one nobody has mended for a while.</summary>
        public static float FenceWear = float.TryParse(System.Environment.GetEnvironmentVariable("UG_TOWNFENCEWEAR"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fw) ? fw : 0.04f;

        /// <summary>The fence a lot's yard gets, steady per lot and seed. A house mostly gets the board privacy fence
        /// (Fence_Wood_0, PEI's commonest: 317 placed), sometimes white pickets or a ranch rail; a shop's back is mostly
        /// chain link, as is half the apartment blocks' (Fence_Metal_0, PEI's next: 206).</summary>
        FenceSpline.Style FenceStyleFor(Plot pl, int index)
        {
            ulong h = Mix(((ulong)(uint)Seed << 32) ^ ((ulong)(uint)index * 0x9E3779B97F4A7C15UL) ^ 0xFE4CE5UL);
            float u = Unit(h, 16);
            return pl.Kind switch
            {
                PlotKind.Shop => u < 0.7f ? FenceSpline.ChainLink : FenceSpline.WoodPrivacy,
                PlotKind.Apartments => u < 0.5f ? FenceSpline.ChainLink : FenceSpline.WoodPrivacy,
                _ => u < 0.55f ? FenceSpline.WoodPrivacy : u < 0.8f ? FenceSpline.WoodPicket
                   : u < 0.9f ? FenceSpline.WoodRanch : FenceSpline.ChainLink,
            };
        }

        /// <summary>Build every yard fence the last PlanPlots designated, each in its lot's style; runs of one style that
        /// meet at a corner share the corner post, and FenceWear of the stretches that have a broken prop are laid broken.
        /// Returns the cells made.</summary>
        public int BuildFences(Node3D parent, EditorObjects objs)
        {
            if (!BuildRealFences || Fences.Count == 0 || parent == null || objs == null) return 0;
            var terr = Terrain.Active;
            var b = new FenceSpline.Builder(terr != null ? (x, z) => terr.SampleHeight(x, z) : null);
            var index = new Dictionary<Plot, int>();
            for (int i = 0; i < Plots.Count; i++) index[Plots[i]] = i;
            for (int k = 0; k < Fences.Count; k++)
            {
                var f = Fences[k];
                int pi = index.TryGetValue(f.Plot, out int ix) ? ix : 0;
                b.AddLine(FenceStyleFor(f.Plot, pi), new[] { f.A, f.B }, wear: FenceWear,
                          seed: Mix(((ulong)(uint)Seed << 32) ^ ((ulong)(uint)k * 0xD1B54A32D192ED03UL) ^ 0xB20CE5UL));
            }
            return b.Build(parent, objs, "Town_Fences");
        }

        // ==============================================================================================
        // THE TOWN'S FOOTPRINT IN THE TERRAIN PAINT (strawberry 2026-10-15: "work on the roads/sidewalks leaving terrain paint
        // dirt footprints").
        // ==============================================================================================

        /// <summary>How far past its paving the town's dirt reaches: the worn verge where a kerb meets the grass.</summary>
        public float FootprintBorder = 1f;
        /// <summary>How far inside a lot's line a texel must be for the lot to keep its own paint.</summary>
        public float YardKeep = 0.5f;
        /// <summary>The splat layer a footprint is painted in: Dirt, as the island generator paints its routes and buildings.</summary>
        public const int FootprintLayer = 0;
        /// <summary>What the last PaintFootprints painted.</summary>
        public int FootprintTexels;

        /// <summary>⭐ EVERYTHING THE TOWN PAVED LEAVES DIRT IN THE TERRAIN PAINT -- the streets and back lanes with their
        /// pavements, the junction patches, the pavements pushed out round the parking bays, the parking lots -- with
        /// FootprintBorder round it, the way ProcIslandSpawn paints its routes, camps and buildings. Under the paving it is what
        /// shows through a seam instead of lawn; past it, a worn verge where a kerb meets the grass instead of a lawn mown to the
        /// kerb's edge; and the grass scatter, which grows only on grass, keeps off the paving. Painted per 4 m splat texel, by
        /// its centre -- the paint's own resolution -- so the verge is as ragged as retail's. UG_TOWNFOOTPRINTS=0: none (control).
        /// Returns the texels painted.</summary>
        public int PaintFootprints(RoadField roads, Terrain terr)
        {
            FootprintTexels = 0;
            if (roads == null || terr == null || System.Environment.GetEnvironmentVariable("UG_TOWNFOOTPRINTS") == "0") return 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // the town's box: every street and lane it built, and every lot
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            void Grow(Vector3 q) { x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X); z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z); }
            foreach (var list in new[] { Built, LaneRoads })
                foreach (int r in list)
                {
                    float len = roads.RoadLength(r);
                    for (float d = 0f; d <= len; d += 8f) if (roads.EvaluateAlong(r, d, out var q, out _, snapTerrain: false)) Grow(q);
                    if (roads.EvaluateAlong(r, len, out var e, out _, snapTerrain: false)) Grow(e);
                }
            foreach (var pl in Plots) foreach (var c in pl.Boundary()) Grow(c);
            if (x0 > x1) return 0;
            float reach = 25f;                           // a street's half width, its pavement and the border, with room to spare
            // ⚠ A LANE'S PAVEMENT IS NOT A STREET'S. Measured off every road at the street's pavement width, a back lane --
            // LaneWalkWidth a side -- left a dirt band twice its width, metres into the yards either side of it.
            float keep = PavementGap + _walkWidth + FootprintBorder, laneKeep = PavementGap + LaneWalkWidth + FootprintBorder;
            var lots = new List<Vector3[]>();
            foreach (var pl in Plots) if (pl.Kind == PlotKind.Parking) lots.Add(pl.Corners());
            // ⚠ AND NOT IN A YARD. A splat texel is 4 m, so a verge measured off a lane's pavement reached metres past the
            // fence on the lot line, over the lawn: a lot's own ground keeps its paint, whatever paving runs along its edge.
            var yards = new List<(Vector3[] q, float x0, float z0, float x1, float z1)>();
            foreach (var pl in Plots)
            {
                if (pl.Kind == PlotKind.Parking) continue;
                var b = pl.Boundary();
                float bx0 = float.MaxValue, bz0 = float.MaxValue, bx1 = float.MinValue, bz1 = float.MinValue;
                foreach (var v in b) { bx0 = Mathf.Min(bx0, v.X); bx1 = Mathf.Max(bx1, v.X); bz0 = Mathf.Min(bz0, v.Z); bz1 = Mathf.Max(bz1, v.Z); }
                yards.Add((b, bx0, bz0, bx1, bz1));
            }
            bool InQuad(Vector3[] q, float x, float z, float grow)
            {
                // inside the convex quad, or within `grow` of it
                for (int i = 0; i < q.Length; i++)
                {
                    var a = q[i]; var b = q[(i + 1) % q.Length];
                    var e = new Vector2(b.X - a.X, b.Z - a.Z); float el = e.Length();
                    if (el < 1e-4f) continue;
                    // the quad's corners run round it one way; whichever, a point outside is beyond SOME edge by more than grow
                    var n = new Vector2(-e.Y, e.X) / el;
                    var c = Vector2.Zero; foreach (var v in q) c += new Vector2(v.X, v.Z); c /= q.Length;
                    if (n.Dot(c - new Vector2(a.X, a.Z)) > 0f) n = -n;                    // outward
                    if (n.Dot(new Vector2(x - a.X, z - a.Z)) > grow) return false;
                }
                return true;
            }
            var why = new Dictionary<string, (int n, Vector3 at)>();
            var builtSet = new HashSet<int>(Built); var laneSet = new HashSet<int>(LaneRoads);
            bool Note(string k, Vector3 at) { why[k] = why.TryGetValue(k, out var w) ? (w.n + 1, w.at) : (1, at); return true; }
            bool Paved(float x, float z)
            {
                var p = new Vector3(x, 0f, z);
                foreach (var y in yards)
                    if (x >= y.x0 && x <= y.x1 && z >= y.z0 && z <= y.z1 && InQuad(y.q, x, z, -YardKeep)) { Note("(a yard: left)", p); return false; }
                // within a lane's reach of any road -- or a street's of a road that is not a lane
                if (roads.DeepestRoadOverlap(p, laneKeep, out int rd, out _))
                    return Note(laneSet.Contains(rd) ? "lane" : builtSet.Contains(rd) ? "street" : $"road {rd} (not the town's)", p);
                if (roads.DeepestRoadOverlap(p, keep, out rd, out _)
                    && (!laneSet.Contains(rd) || roads.DeepestRoadOverlap(p, keep, out rd, out _, skipRoad: rd) && !laneSet.Contains(rd)))
                    return Note(builtSet.Contains(rd) ? "street" : $"road {rd} (not the town's)", p);
                if (OnAnyPatch(x, -z, keep)) return Note("junction", p);
                if (BayFrontAt(roads, p)) return Note("bay", p);
                foreach (var q in lots) if (InQuad(q, x, z, FootprintBorder)) return Note("lot", p);
                return false;
            }
            if (ProbeEverything)
                foreach (var lp in LanePlans)
                    if (lp.Pts != null && lp.Pts.Count >= 2)
                        Log.Print($"[towngrid]   lane plan road {lp.Road}: ({lp.Pts[0].X:0},{lp.Pts[0].Z:0}) -> ({lp.Pts[^1].X:0},{lp.Pts[^1].Z:0}), "
                                + $"{lp.Pts.Count} pts{(lp.OpenB ? ", a lot's way in" : "")}");
            FootprintTexels = terr.PaintWhere(x0 - reach, z0 - reach, x1 + reach, z1 + reach, Paved, FootprintLayer);
            Log.Print($"[towngrid] footprint: {FootprintTexels} splat texel(s) under the town's paving painted dirt "
                    + $"({FootprintBorder:0.#} m past it, {Terrain.DefaultLayerNames[FootprintLayer]}) in {clock.ElapsedMilliseconds} ms -- "
                    + string.Join(", ", System.Linq.Enumerable.Select(why, kv => $"{kv.Key} {kv.Value.n} (e.g. {kv.Value.at.X:0},{kv.Value.at.Z:0})")));
            return FootprintTexels;
        }

        // ==============================================================================================
        // NO DOUBLE FENCES (strawberry 2026-10-14: "try to prevent plots from double fencing. dead space below a certain
        // size should just get eaten up by plots trying to meet").
        // ==============================================================================================

        /// <summary>A gap up to this wide between two lots' facing edges is closed by the lots growing to meet
        /// (UG_TOWNMEETGAP). GrowPlots leaves 0..ExpandStep between neighbours -- it stops the step BEFORE an overlap -- and
        /// each of those slivers was a strip of dead grass with a fence down both sides of it.</summary>
        public float MeetGap = float.TryParse(System.Environment.GetEnvironmentVariable("UG_TOWNMEETGAP"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mg) ? mg : 4f;
        /// <summary>What the last MeetPlots did: lot edges moved, and the ground they took.</summary>
        public int PlotsMet; public float MetArea;
        /// <summary>Two fence runs this close and parallel are one fence too many: PlanFences drops the shorter's overlap.</summary>
        public const float DoubleFenceGap = 1.2f;
        /// <summary>What the last PlanFences dropped as doubles: runs cut, and metres of fence that would have stood beside another.</summary>
        public int FencesDoubled; public float DoubledLength;
        /// <summary>The middle of the longest stretch dropped as a double -- where to look.</summary>
        public Vector3 DoubledAt;

        /// <summary>The edges a lot presents, each with its outward normal and which side it is (0 back, 1 left, 2 right,
        /// 3 front; -1 for an outline lot, which does not grow).</summary>
        static List<(Vector3 a, Vector3 b, Vector3 n, int side)> EdgesOf(Plot q)
        {
            var e = new List<(Vector3, Vector3, Vector3, int)>(4);
            if (q.Outline != null)
            {
                var ol = q.Outline; var mid = Vector3.Zero; foreach (var v in ol) mid += v; mid /= ol.Length;
                for (int i = 0; i < ol.Length; i++)
                {
                    var a = ol[i]; var b = ol[(i + 1) % ol.Length];
                    var n = new Vector3(-(b.Z - a.Z), 0f, b.X - a.X);
                    if (n.LengthSquared() < 1e-8f) continue;
                    n = n.Normalized();
                    if (n.Dot((a + b) * 0.5f - mid) < 0f) n = -n;
                    e.Add((a, b, n, -1));
                }
                return e;
            }
            var c = q.Corners();       // front-left, front-right, back-right, back-left
            var f = new Vector3(q.Facing.X, 0f, q.Facing.Z).Normalized(); var al = new Vector3(q.Along.X, 0f, q.Along.Z).Normalized();
            e.Add((c[2], c[3], -f, 0)); e.Add((c[3], c[0], -al, 1)); e.Add((c[1], c[2], al, 2)); e.Add((c[0], c[1], f, 3));
            return e;
        }

        /// <summary>Move side `s` (0 back, 1 left, 2 right) of a lot out by `d` -- its yard grows -- or back in with -d.</summary>
        static void GrowSide(Plot pl, int s, float d)
        {
            if (s == 0) { pl.Depth += d; pl.YardBack += d; pl.GrowBack += d; }
            else if (s == 1) { pl.Width += d; pl.Front -= pl.Along * (d * 0.5f); pl.YardLeft += d; pl.GrowLeft += d; }
            else { pl.Width += d; pl.Front += pl.Along * (d * 0.5f); pl.YardRight += d; pl.GrowRight += d; }
        }

        /// <summary>⭐ SLIVERS ARE EATEN: every gap up to MeetGap between two lots' facing edges -- parallel, opposite, side by
        /// side for a metre or more -- is closed by the lots growing into it, half each where both can grow, all of it into the
        /// one that can otherwise, until the two edges lie on ONE line. PlanFences then merges what lies on one line into one
        /// fence, so this is what stops the double fencing at its source. Runs after GrowPlots, before anything reads the
        /// lots' final shapes (triangles, fences, greens).</summary>
        void MeetPlots(RoadField roads)
        {
            PlotsMet = 0; MetArea = 0f;
            if (MeetGap <= 0f || Plots.Count < 2 || roads == null) return;
            bool Growable(Plot q) => q.Outline == null && q.Kind != PlotKind.Parking;
            bool Clear(Plot pl, int s)
            {
                var c = pl.Corners();
                var (a, b) = s == 0 ? (c[2], c[3]) : s == 1 ? (c[3], c[0]) : (c[1], c[2]);
                int n = Mathf.Max(1, Mathf.CeilToInt(Flat(a, b) / 1.5f));
                for (int k = 0; k <= n; k++)
                {
                    var q = a.Lerp(b, (float)k / n);
                    if (!Buildable(roads, q) || BayFrontAt(roads, q) || LaneAt(q)) return false;
                }
                foreach (var q in Plots)
                    if (!ReferenceEquals(q, pl) && (q.Outline != null ? QuadsOverlap(c, q.Outline) : PlotsOverlap(pl, q))) return false;
                foreach (var al in Alleys) if (QuadsOverlap(c, al.Corners())) return false;
                return true;
            }
            bool TryGrow(Plot pl, int s, float d)
            {
                if (d <= 1e-3f) return false;
                GrowSide(pl, s, d);
                if (Clear(pl, s)) return true;
                GrowSide(pl, s, -d);
                return false;
            }
            float cosOpp = -Mathf.Cos(Mathf.DegToRad(2f));
            _roadSamples.Clear();
            for (int pass = 0; pass < 4; pass++)
            {
                bool any = false;
                foreach (var pl in Plots)
                {
                    if (!Growable(pl)) continue;
                    for (int s = 0; s < 3; s++)
                    {
                        var (ea, eb, en, _) = EdgesOf(pl)[s];
                        var t = new Vector3(eb.X - ea.X, 0f, eb.Z - ea.Z); float elen = t.Length();
                        if (elen < 1f) continue;
                        t /= elen;
                        // the nearest facing edge of another lot
                        float gap = float.MaxValue; Plot other = null; int otherSide = -1;
                        foreach (var q in Plots)
                        {
                            if (ReferenceEquals(q, pl)) continue;
                            foreach (var (qa, qb, qn, qs) in EdgesOf(q))
                            {
                                if (en.Dot(qn) > cosOpp) continue;
                                float pa = new Vector3(qa.X - ea.X, 0f, qa.Z - ea.Z).Dot(t), pb = new Vector3(qb.X - ea.X, 0f, qb.Z - ea.Z).Dot(t);
                                float lo = Mathf.Max(0f, Mathf.Min(pa, pb)), hi = Mathf.Min(elen, Mathf.Max(pa, pb));
                                if (hi - lo < 1f) continue;
                                // the gap measured across the middle of the stretch the two edges share
                                float w = Mathf.Abs(pb - pa) < 1e-4f ? 0.5f : ((lo + hi) * 0.5f - pa) / (pb - pa);
                                var mid = qa.Lerp(qb, Mathf.Clamp(w, 0f, 1f));
                                float g = new Vector3(mid.X - ea.X, 0f, mid.Z - ea.Z).Dot(en);
                                if (g <= 0.03f || g > MeetGap || g >= gap) continue;
                                gap = g; other = q; otherSide = qs;
                            }
                        }
                        if (other == null) continue;
                        // half each where the other lot can grow its facing side; otherwise this one takes the whole gap
                        bool both = Growable(other) && otherSide >= 0 && otherSide <= 2;
                        float mine = both ? gap * 0.5f : gap - 0.01f;
                        if (!TryGrow(pl, s, mine)) continue;
                        any = true; PlotsMet++; MetArea += mine * elen;
                        if (both)
                        {
                            float theirs = gap * 0.5f - 0.01f;
                            if (TryGrow(other, otherSide, theirs)) { PlotsMet++; MetArea += theirs * elen; }
                            else if (TryGrow(pl, s, theirs)) MetArea += theirs * elen;      // it could not: this one takes the rest
                        }
                    }
                }
                if (!any) break;
            }
            Log.Print($"[towngrid] lots met: {PlotsMet} edge(s) grew to meet a neighbour across a gap up to {MeetGap:0.#} m, "
                    + $"{MetArea:0} m2 of sliver taken");
        }

        /// <summary>⭐ AND NEVER TWO FENCES SIDE BY SIDE, whatever the lots did: two runs parallel to half a degree and under
        /// DoubleFenceGap apart are one fence too many. Longest first, each run loses the stretch an already-kept run covers;
        /// what is left of it under half a metre is dropped.</summary>
        void DropDoubleFences()
        {
            FencesDoubled = 0; DoubledLength = 0f; DoubledAt = Vector3.Zero;
            if (Fences.Count < 2 || System.Environment.GetEnvironmentVariable("UG_TOWNDOUBLEFENCES") == "1") return;   // (the control)
            float longest = 0f;
            var order = new List<YardFence>(Fences);
            order.Sort((x, y) => Flat(y.A, y.B).CompareTo(Flat(x.A, x.B)));
            var kept = new List<YardFence>(order.Count);
            float cosTol = Mathf.Cos(Mathf.DegToRad(0.5f));
            foreach (var f in order)
            {
                var pieces = new List<YardFence> { f };
                foreach (var k in kept)
                {
                    var kd = new Vector3(k.B.X - k.A.X, 0f, k.B.Z - k.A.Z); float kl = kd.Length();
                    if (kl < 1e-3f) continue;
                    kd /= kl;
                    var kn = new Vector3(-kd.Z, 0f, kd.X);
                    var next = new List<YardFence>();
                    foreach (var p in pieces)
                    {
                        var pd = new Vector3(p.B.X - p.A.X, 0f, p.B.Z - p.A.Z); float pl = pd.Length();
                        if (pl < 1e-3f || Mathf.Abs(pd.Normalized().Dot(kd)) < cosTol
                            || Mathf.Abs(new Vector3(p.A.X - k.A.X, 0f, p.A.Z - k.A.Z).Dot(kn)) > DoubleFenceGap
                            || Mathf.Abs(new Vector3(p.B.X - k.A.X, 0f, p.B.Z - k.A.Z).Dot(kn)) > DoubleFenceGap)
                        { next.Add(p); continue; }
                        // along k's direction: the piece covers [ta, tb], k covers [0, kl]; cut the overlap out of the piece
                        float ta = new Vector3(p.A.X - k.A.X, 0f, p.A.Z - k.A.Z).Dot(kd), tb = new Vector3(p.B.X - k.A.X, 0f, p.B.Z - k.A.Z).Dot(kd);
                        float lo = Mathf.Max(Mathf.Min(ta, tb), 0f), hi = Mathf.Min(Mathf.Max(ta, tb), kl);
                        if (hi - lo < 0.05f) { next.Add(p); continue; }
                        FencesDoubled++; DoubledLength += hi - lo;
                        Vector3 At(float t) => p.A.Lerp(p.B, Mathf.Clamp((t - ta) / (tb - ta), 0f, 1f));
                        if (hi - lo > longest) { longest = hi - lo; DoubledAt = At((lo + hi) * 0.5f); }
                        foreach (var (u0, u1) in new[] { (Mathf.Min(ta, tb), lo), (hi, Mathf.Max(ta, tb)) })
                            if (u1 - u0 >= 0.5f) next.Add(new YardFence { A = At(u0), B = At(u1), Plot = p.Plot, Edge = p.Edge });
                    }
                    pieces = next;
                }
                kept.AddRange(pieces);
            }
            if (FencesDoubled > 0)
            {
                Fences.Clear(); Fences.AddRange(kept);
                Log.Print($"[towngrid] double fences: {FencesDoubled} run(s) stood beside another within {DoubleFenceGap:0.#} m -- "
                        + $"{DoubledLength:0.0} m of them dropped (longest {longest:0.0} m at {DoubledAt.X:0},{DoubledAt.Z:0})");
            }
        }
    }
}
