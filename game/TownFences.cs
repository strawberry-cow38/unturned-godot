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
