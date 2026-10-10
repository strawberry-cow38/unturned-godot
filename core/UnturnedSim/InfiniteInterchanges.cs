using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>
    /// HIGHWAY INTERCHANGES (strawberry 2026-10-10: "when highways cross over eachother, build on/off ramps too, chosing
    /// one as the high/low highway each crossing"). An east-west highway and a north-south one used to cross at grade --
    /// two carves fighting over one patch of ground and four ribbons laid through each other.
    ///
    /// The east-west lines are built first and know nothing of this. A NORTH-SOUTH segment is built in two stages, the way
    /// a railway is (InfiniteRails): RAW (its route and profile, cached) and FINAL (FinishHighway): every crossing its
    /// shaping can reach is planned -- its own, and a neighbour segment's near the anchor they share -- and the profile is
    /// shaped through each one, level OverRise above the east-west line's (it bridges it on its own decks) or UnderDepth
    /// below (the east-west line carries itself over on short decks, as it does over a main). The plan reads only the two
    /// RAW lines and the east-west one, so both segments that see a crossing make the same one, and the shaping is a
    /// function of distance from the crossing's point, so they agree at their shared anchor to the bit.
    ///
    /// Which goes over: the one the land already holds higher -- the north-south line where its own profile stands above
    /// the east-west line's (that one is in a cut), or where going under would take it below the sea, or onto a deck's end;
    /// under where the east-west line is already on a bridge across it; over the hill where that line is in a tunnel.
    ///
    /// Then RAMPS, one in each quadrant: from one carriageway of the east-west line to the north-south line's carriageway
    /// on the same quadrant's side, leaving each along it (InterchangeRamps). Mains, towns and railways keep clear.
    /// </summary>
    public sealed partial class InfiniteRoads
    {
        /// <summary>UG_INF_INTERCHANGES=0: highways cross at grade, as before.</summary>
        public static bool Interchanges = Environment.GetEnvironmentVariable("UG_INF_INTERCHANGES") != "0";
        /// <summary>How far along each highway from the crossing a ramp may join it: the nearest spot past the minimum that
        /// is on neither a bridge nor near a tunnel.</summary>
        public const double InterchangeRampMin = 200, InterchangeRampMax = 760, InterchangeRampStep = 20;
        /// <summary>A main's crossing of either highway, and a town, keeps this far from an interchange; a main's line this
        /// far from its crossing point; a railway pays dearly to come within InterchangeClear.</summary>
        public const double InterchangeClear = 900, InterchangeKeep = 250;

        readonly ConcurrentDictionary<(long, long), Line> _hwyRaw = new();
        /// <summary>Why a quadrant of an interchange has no ramp (diagnostics), by the crossing's point and the quadrant.</summary>
        public readonly ConcurrentDictionary<(long x, long z, int sa, int sb), string> RampDrops = new();
        /// <summary>A ramp turns no tighter than this: in an acute quadrant it joins both highways further out.</summary>
        public const double RampMinRadius = 110;
        Line HighwayRaw(long band, long k) =>
            _hwyRaw.TryGetValue((band, k), out var hit) ? hit : _hwyRaw.GetOrAdd((band, k), key =>
            {
                Trim();
                var raw = BuildHighway(1, key.Item1, key.Item2, raw: true);
                if (!raw.Exists) return raw;
                var a = Anchor(1, key.Item1, key.Item2); var b = Anchor(1, key.Item1, key.Item2 + 1);
                ToWorld(1, a.u, a.w, out double ax, out double az); ToWorld(1, b.u, b.w, out double bx, out double bz);
                var main = ShortOfCrossings(raw, ax, az, bx, bz);
                if (main == null) return Drop(1, key.Item1, key.Item2, "a shore end at an east-west highway, nothing left");
                if (raw.Branches != null)
                {
                    var kept = new List<Line>();
                    foreach (var br in raw.Branches) if (ShortOfCrossings(br, ax, az, bx, bz) is Line t) kept.Add(t);
                    main.Branches = kept.Count > 0 ? kept : null;
                }
                return main;
            });

        /// <summary>A north-south line whose SHORE end (an end at neither anchor: the land stopped there) lies within
        /// ShoreEndKeep of an east-west highway it crosses would end ON the interchange -- its deck stopping over the other
        /// road, its end ramp and carve filling the ground across it. Such an end is cut back to stop ShoreEndKeep short of
        /// that highway, so it never reaches it. Null if nothing would be left.</summary>
        Line ShortOfCrossings(Line e, double ax, double az, double bx, double bz)
        {
            int n = e.X.Length - 1;
            bool AtAnchor(int i) => Math.Abs(e.X[i] - ax) < 1 && Math.Abs(e.Z[i] - az) < 1 || Math.Abs(e.X[i] - bx) < 1 && Math.Abs(e.Z[i] - bz) < 1;
            bool open0 = !AtAnchor(0), open1 = !AtAnchor(n);
            if (!open0 && !open1) return e;
            var ew = new List<Line>();
            TakeHighways(e.MinX, e.MinZ, e.MaxX, e.MaxZ, ew, 0);
            var arc = Arc(e);
            double total = arc[n], lo = 0, hi = total;
            foreach (var a in ew)
                foreach (var c in Crossings(e.X, e.Z, e.X.Length, a))
                {
                    int fk = Math.Min((int)c.f, e.Segments - 1);
                    double aX = arc[fk] + (arc[fk + 1] - arc[fk]) * (c.f - fk);
                    double keep = ShoreEndKeep / Math.Max(0.5, Math.Sin(c.angle * Math.PI / 180));
                    if (open0 && aX < ShoreEndReach) lo = Math.Max(lo, aX + keep);
                    if (open1 && total - aX < ShoreEndReach) hi = Math.Min(hi, aX - keep);
                }
            if (lo <= 0 && hi >= total) return e;
            if (hi - lo < 200) return null;
            // the dense points strictly inside, plus the two cut points
            var xs = new List<double>(); var zs = new List<double>(); var hs = new List<float>();
            void Cut(double s) { At(e, arc, s, out double x, out double z, out float h, out _, out _); xs.Add(x); zs.Add(z); hs.Add(h); }
            Cut(lo);
            for (int i = 0; i <= n; i++) if (arc[i] > lo + 1e-6 && arc[i] < hi - 1e-6) { xs.Add(e.X[i]); zs.Add(e.Z[i]); hs.Add(e.H[i]); }
            Cut(hi);
            var t = new Line { Kind = e.Kind, X = xs.ToArray(), Z = zs.ToArray(), H = hs.ToArray() };
            Rebox(t);
            return t;
        }
        /// <summary>A shore end within ShoreEndReach of a crossing is cut back to ShoreEndKeep short of the other highway's
        /// centreline (along itself, over the sine of the crossing angle): past its carve and its own end ramp.</summary>
        public const double ShoreEndReach = 400, ShoreEndKeep = 70;

        /// <summary>One crossing of a north-south highway (raw line R, fractional dense index F) with an east-west one (A,
        /// at G): what the north-south line does there. Flat: metres either side of (X, Z) it holds Target.</summary>
        sealed class HxPlan
        {
            public Line R, A; public double F, G, X, Z, Sin, Flat;
            public float Target; public bool Over, Existing, Hill;
        }

        static void Interp(Line e, double f, out float h)
        {
            int k = Math.Min((int)f, e.Segments - 1);
            h = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)(f - k);
        }

        HxPlan PlanFor(Line R, double f, Line A, double g, double angleDeg, double x, double z)
        {
            double sin = Math.Max(0.5, Math.Sin(angleDeg * Math.PI / 180)), cot = Math.Sqrt(Math.Max(0.0, 1 - sin * sin)) / sin;
            // along either line, the stretch over which any of the other's asphalt (and the deck margin) lies across it
            double flat = (PavedHalf(RoadKind.Highway) + UnderMargin) / sin + PavedHalf(RoadKind.Highway) * cot;
            var p = new HxPlan { R = R, A = A, F = f, G = g, X = x, Z = z, Sin = sin, Flat = flat };
            Interp(A, g, out float hA); Interp(R, f, out float hR);
            int jk = Math.Min((int)g, A.Segments - 1);
            var arcA = Arc(A);
            double alongA = arcA[jk] + (arcA[jk + 1] - arcA[jk]) * (g - jk);
            var (lo, hi) = ProfileRange(A, g, flat);
            // A IN A TUNNEL: over its hill, held a shell's height and TunnelCover over the bores; no decks, no ramps. Near a
            // portal (the forecourt's cut) over it on decks, as over any cut
            if (Tunnels && A.TunnelSpans != null)
                foreach (var tn in A.TunnelSpans)
                {
                    if (alongA < tn.A0 - TunnelForecourt - flat || alongA > tn.A1 + TunnelForecourt + flat) continue;
                    p.Over = true;
                    p.Hill = alongA >= tn.A0 + MainMouthKeep && alongA <= tn.A1 - MainMouthKeep;
                    if (p.Hill)
                    {
                        p.Flat = (ShellReachOf(A.Kind) + UnderMargin) / sin + PavedHalf(RoadKind.Highway) * cot;
                        p.Target = TunnelSurfaceY(A.Kind, hi) + ShellTopOf(A.Kind, TubeOffset(A.Kind, 0)) + TunnelCover + Bed + 0.5f;
                    }
                    else p.Target = hi + OverRise;
                    return p;
                }
            // ON ONE OF A's BRIDGES across the whole width (both carriageways): under it, no decks of our own
            int covered = 0;
            if (Bridges && A.DeckCover != null)
            {
                double seg = Math.Max(1e-6, arcA[jk + 1] - arcA[jk]), reach = flat / seg;
                for (int s = 0; s < 2; s++)
                    for (int q = -4; q <= 4; q++) if (Covered(A.DeckCover[s], g + reach * q / 4.0)) covered++;
            }
            bool floods = lo - UnderDepth < InfiniteTerrain.SeaLevel + 1.2f;
            // A's decks over us need A's corridor whole either side: not within a span of A's own ends (its next segment
            // holds the rest), so there we go over
            bool atEnd = alongA < 2 * flat + 2 * BridgePitch || alongA > arcA[arcA.Length - 1] - 2 * flat - 2 * BridgePitch;
            p.Existing = covered == 18 && !floods;
            p.Over = !p.Existing && (covered > 0 || floods || atEnd || hR > hA);
            p.Target = p.Over ? hi + OverRise : lo - UnderDepth;
            return p;
        }

        /// <summary>FINAL stage of a north-south segment: plan its crossings (and its neighbours' within reach of the shared
        /// anchors), shape a copy of the raw profile through them, mark its stretches -- forced raised over every crossing
        /// it bridges, kept open near any, piers off the other highway -- lay the east-west line's decks where it goes under,
        /// and build the ramps.</summary>
        Line FinishHighway(long band, long k)
        {
            var raw = HighwayRaw(band, k);
            if (!raw.Exists) return None;
            Line Copy(Line r) => new Line { Kind = r.Kind, X = r.X, Z = r.Z, H = (float[])r.H.Clone(), CMinX = r.CMinX, CMaxX = r.CMaxX, CMinZ = r.CMinZ, CMaxZ = r.CMaxZ,
                                            MinX = r.MinX, MaxX = r.MaxX, MinZ = r.MinZ, MaxZ = r.MaxZ };
            var own = new List<(Line raw, Line fin)> { (raw, Copy(raw)) };
            if (raw.Branches != null) foreach (var b in raw.Branches) own.Add((b, Copy(b)));
            var main = own[0].fin;
            if (own.Count > 1) { main.Branches = new List<Line>(); for (int i = 1; i < own.Count; i++) main.Branches.Add(own[i].fin); }

            // the raw lines whose crossings can reach us: ours, and a neighbour's where an end of ours is near an
            // east-west highway (both neighbours evaluate the same anchor the same way)
            const double Reach = 600;
            var seen = new List<Line>();
            foreach (var (r, _) in own) seen.Add(r);
            var As = new List<Line>();
            bool NearA(double x, double z)
            {
                var near = new List<Line>();
                TakeHighways(x - Reach, z - Reach, x + Reach, z + Reach, near, 0);
                foreach (var a in near) if (NearestOn(a, x, z).d < Reach) return true;
                return false;
            }
            int n0 = raw.X.Length - 1;
            if (NearA(raw.X[0], raw.Z[0]) || NearA(raw.X[n0], raw.Z[n0]))
                foreach (long nk in new[] { k - 1, k + 1 })
                {
                    var nb = HighwayRaw(band, nk);
                    if (!nb.Exists) continue;
                    seen.Add(nb);
                    if (nb.Branches != null) seen.AddRange(nb.Branches);
                }
            double bx0 = double.MaxValue, bz0 = double.MaxValue, bx1 = double.MinValue, bz1 = double.MinValue;
            foreach (var r in seen) { bx0 = Math.Min(bx0, r.MinX); bz0 = Math.Min(bz0, r.MinZ); bx1 = Math.Max(bx1, r.MaxX); bz1 = Math.Max(bz1, r.MaxZ); }
            TakeHighways(bx0, bz0, bx1, bz1, As, 0);
            var plans = new List<HxPlan>();
            foreach (var r in seen)
                foreach (var a in As)
                    foreach (var c in Crossings(r.X, r.Z, r.X.Length, a))
                        plans.Add(PlanFor(r, c.f, a, c.g, c.angle, c.x, c.z));
            if (plans.Count == 0)
            {
                foreach (var (_, fin) in own) MarkStretches(fin);
                return main;
            }
            float grade = 0.92f * MaxGrade(RoadKind.Highway);
            // one over and one under within each other's reach pull against each other: both go over
            for (int pass = 0; pass < 3; pass++)
                foreach (var pa in plans)
                {
                    if (pa.Over) continue;
                    foreach (var pb in plans)
                    {
                        if (!pb.Over || pb == pa) continue;
                        double d = Math.Sqrt((pa.X - pb.X) * (pa.X - pb.X) + (pa.Z - pb.Z) * (pa.Z - pb.Z));
                        if (d - pa.Flat - pb.Flat < (pb.Target - pa.Target) / grade + 80)
                        {
                            var (_, hi) = ProfileRange(pa.A, pa.G, pa.Flat);
                            pa.Over = true; pa.Existing = false; pa.Target = hi + OverRise;
                            break;
                        }
                    }
                }
            // SHAPE: level at Target within Flat (plus a dense spacing: the profile is linear between points) of the
            // crossing's point, then back to the land at `grade` -- by DISTANCE from the point, so the segments either
            // side of an anchor agree there
            foreach (var (_, fin) in own)
            {
                double spacing = MaxSpacing(Arc(fin));
                foreach (var p in plans)
                    for (int i = 0; i < fin.H.Length; i++)
                    {
                        double d = Math.Sqrt((fin.X[i] - p.X) * (fin.X[i] - p.X) + (fin.Z[i] - p.Z) * (fin.Z[i] - p.Z));
                        float b = (float)Math.Max(0.0, d - p.Flat - spacing) * grade;
                        fin.H[i] = p.Over ? Math.Max(fin.H[i], p.Target - b) : Math.Min(fin.H[i], p.Target + b);
                    }
            }
            // MARK: over every crossing it bridges, the carriageway is raised (a deck) across the other's corridor and a
            // dense spacing more, whatever the fill; no tunnel near any crossing; no pier on the other highway
            foreach (var (r, fin) in own)
            {
                var mine = plans.FindAll(p => p.R == r);
                double spacing = MaxSpacing(Arc(fin));
                bool Force(double x, double z)
                {
                    foreach (var p in mine)
                        if (p.Over && !p.Hill && NearestOn(p.A, x, z).d <= PavedHalf(RoadKind.Highway) + UnderMargin + spacing) return true;
                    return false;
                }
                bool PierOk(double x, double z)
                {
                    foreach (var a in As) if (NearestOn(a, x, z).d < PavedHalf(RoadKind.Highway) + UnderMargin + 9.0) return false;
                    return true;
                }
                fin.HArc = Arc(fin);
                bool Open(double s)
                {
                    At(fin, fin.HArc, s, out double x, out double z, out _, out _, out _);
                    foreach (var p in plans)
                        if (Math.Sqrt((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z)) < p.Flat + TunnelForecourt + 60) return true;
                    return false;
                }
                MarkStretches(fin, Force, Open, PierOk);
                // THE RECORDS, the east-west line's decks where we go under, and the ramps
                foreach (var p in mine)
                {
                    Interp(p.A, p.G, out float hA); Interp(fin, p.F, out float hB);
                    var up = new Underpass { HwId = p.A.Id, X = p.X, Z = p.Z, Over = p.Over, Existing = p.Existing, OverTunnel = p.Hill,
                                             HwSurface = SurfaceY(RoadKind.Highway, hA), MainSurface = SurfaceY(RoadKind.Highway, hB) };
                    if (!p.Over && !p.Existing && !LayUnderpass(fin, p.A, up))
                    {
                        // A's decks would not fit (it should not happen: PlanFor keeps clear of A's ends). Say so in the record
                        // rather than leave a highway buried in an embankment without anyone knowing
                        HighwayDrops[(1, band, k)] = $"interchange at ({p.X:0},{p.Z:0}): east-west decks did not fit";
                    }
                    (fin.Underpasses ??= new List<Underpass>()).Add(up);
                    if (!p.Hill)
                    {
                        var ramps = InterchangeRamps(fin, p.F, p.A, p.G);
                        if (ramps.Count > 0) (fin.Ramps ??= new List<Line>()).AddRange(ramps);
                    }
                }
            }
            return main;
        }

        /// <summary>The ramps of one interchange: in each quadrant, from the east-west carriageway on the quadrant's side to
        /// the north-south one on its side. Each starts inside its carriageway at the carriageway's profile, its outer edge
        /// on the carriageway's (as a main's ramps start), heading along the highway toward the crossing; sweeps round
        /// through the quadrant; and joins the other highway the same way, heading away from the crossing. Where a highway is
        /// on a bridge or near a tunnel at the spot, it joins further out; past InterchangeRampMax, that quadrant has none.</summary>
        List<Line> InterchangeRamps(Line B, double fB, Line A, double gA)
        {
            var list = new List<Line>();
            var arcA = Arc(A); var arcB = Arc(B);
            int ak = Math.Min((int)gA, A.Segments - 1), bk = Math.Min((int)fB, B.Segments - 1);
            double aA = arcA[ak] + (arcA[ak + 1] - arcA[ak]) * (gA - ak), aB = arcB[bk] + (arcB[bk + 1] - arcB[bk]) * (fB - bk);
            At(A, arcA, aA, out _, out _, out _, out double tAx, out double tAz);
            At(B, arcB, aB, out _, out _, out _, out double tBx, out double tBz);
            double nAx = -tAz, nAz = tAx, nBx = -tBz, nBz = tBx;   // each line's left (its +offset carriageway's side)
            double off = HighwayRibbonOffset + HighwayLaneHalf - PavedHalf(RoadKind.Small);
            for (int sa = -1; sa <= 1; sa += 2)
                for (int sb = -1; sb <= 1; sb += 2)
                {
                    // along A toward B's sb side; along B toward A's sa side
                    int wayA = sb * Math.Sign(tAx * nBx + tAz * nBz), wayB = sa * Math.Sign(tBx * nAx + tBz * nAz);
                    var key = ((long)Math.Round(B.X[bk]), (long)Math.Round(B.Z[bk]), sa, sb);
                    void Drop(string why) => RampDrops[key] = why;
                    if (wayA == 0 || wayB == 0) { Drop("parallel"); continue; }
                    // the quadrant's angle: the sharper it is, the further out the ramp must join to turn no tighter than
                    // RampMinRadius (a circle tangent to both rays at distance r from their meeting has radius r tan(angle/2))
                    double cosQ = Math.Clamp(wayA * wayB * (tAx * tBx + tAz * tBz), -1.0, 1.0), half = Math.Acos(cosQ) * 0.5;
                    double rMin = Math.Max(InterchangeRampMin, RampMinRadius / Math.Max(0.2, Math.Tan(half)) + off);
                    double? sA = RampSpot(A, arcA, aA, wayA, sa, rMin), sB = RampSpot(B, arcB, aB, wayB, sb, rMin);
                    if (sA is not double spotA) { Drop("no spot on the east-west highway"); continue; }
                    if (sB is not double spotB) { Drop("no spot on the north-south highway"); continue; }
                    At(A, arcA, spotA, out double hx, out double hz, out float hh, out double htx, out double htz);
                    At(B, arcB, spotB, out double gx, out double gz, out float gh, out double gtx, out double gtz);
                    double sx = hx - htz * sa * off, sz = hz + htx * sa * off;
                    double ex = gx - gtz * sb * off, ez = gz + gtx * sb * off;
                    double t0x = -wayA * htx, t0z = -wayA * htz, t1x = wayB * gtx, t1z = wayB * gtz;
                    // the end tangents' length: a circular arc's for the turn between them (2 tan(turn/4) / sin(turn/2) of
                    // the chord, 1.17 at a right angle). The chord alone bends the curve 2.24/chord at its ends -- a 34 m
                    // radius on a short ramp, its first piece 3 deg off the highway it leaves
                    double turn = Math.Acos(Math.Clamp(t0x * t1x + t0z * t1z, -1.0, 1.0));
                    double chord = Math.Sqrt((ex - sx) * (ex - sx) + (ez - sz) * (ez - sz)) * (turn < 1e-3 ? 1.0 : 2 * Math.Tan(turn / 4) / Math.Sin(turn / 2));
                    var cxs = new double[RampCtrl + 1]; var czs = new double[RampCtrl + 1]; var chs = new float[RampCtrl + 1];
                    bool wet = false;
                    for (int q = 0; q <= RampCtrl; q++)
                    {
                        double t = (double)q / RampCtrl, t2 = t * t, t3 = t2 * t;
                        double h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
                        cxs[q] = h00 * sx + h10 * chord * t0x + h01 * ex + h11 * chord * t1x;
                        czs[q] = h00 * sz + h10 * chord * t0z + h01 * ez + h11 * chord * t1z;
                        if (_t.RawHeight(cxs[q], czs[q]) < InfiniteTerrain.SeaLevel + 1.5f) wet = true;
                    }
                    if (wet) { Drop("over water"); continue; }
                    var arcR = new double[RampCtrl + 1];
                    for (int q = 1; q <= RampCtrl; q++) arcR[q] = arcR[q - 1] + Math.Sqrt((cxs[q] - cxs[q - 1]) * (cxs[q] - cxs[q - 1]) + (czs[q] - czs[q - 1]) * (czs[q] - czs[q - 1]));
                    for (int q = 0; q <= RampCtrl; q++) chs[q] = hh + (gh - hh) * RampEase((float)(arcR[q] / arcR[RampCtrl]));
                    // dense enough that its first and last pieces leave along the highway (a chord across a long ramp's
                    // curve leans off it by half the turn over the piece): pieces of at most ~8 m
                    int subdiv = Math.Max(Sub, (int)Math.Ceiling(arcR[RampCtrl] / (RampCtrl * 8.0)));
                    var ramp = Finish(RoadKind.Small, cxs, czs, chs, (t0x, t0z), (t1x, t1z), subdiv);
                    if (ramp == null) { Drop($"too steep ({gh - hh:+0.0;-0.0} m over {arcR[RampCtrl]:0} m)"); continue; }
                    if (Crossings(ramp.X, ramp.Z, ramp.X.Length, A).Count > 0 || Crossings(ramp.X, ramp.Z, ramp.X.Length, B).Count > 0) { Drop("crosses a highway"); continue; }
                    bool crossesAnother = false;
                    foreach (var o in list) if (Crossings(ramp.X, ramp.Z, ramp.X.Length, o).Count > 0) crossesAnother = true;
                    if (crossesAnother) { Drop("crosses another ramp"); continue; }
                    // ...nor over a tunnel's hill (either highway's run beside the interchange): its carve would dig into
                    // the cover over the shell. ⚠ Only the east-west lines and this one: asking for the north-south lines
                    // (or the railways, which ask for them) from here builds this very segment again -- a recursion
                    // GetOrAdd does not catch. Railways are kept off the ramps from their side (ShapeRail).
                    var near = new List<Line> { B };
                    TakeHighways(ramp.MinX, ramp.MinZ, ramp.MaxX, ramp.MaxZ, near, 0);
                    bool overHill = false;
                    for (int q = 0; q < ramp.X.Length && !overHill; q++) if (Influence(near, ramp.X[q], ramp.Z[q]).Tunnel) overHill = true;
                    if (overHill) { Drop("over a tunnel"); continue; }
                    ramp.Ramp = true;
                    list.Add(ramp);
                }
            return list;
        }

        /// <summary>A ramp's climb from one highway's profile to the other's: level where it leaves and where it joins,
        /// eased over the first and last RampEaseEnds, straight between -- its steepest only 1/(1 - RampEaseEnds) of the
        /// average, where a smoothstep's is 1.5x (and turned a 11% climb into a 17% one, over a small road's 14%).</summary>
        const float RampEaseEnds = 0.15f;
        static float RampEase(float u)
        {
            const float a = RampEaseEnds;
            if (u <= a) return u * u / (2f * a * (1f - a));
            if (u >= 1f - a) return 1f - (1f - u) * (1f - u) / (2f * a * (1f - a));
            return (u - a * 0.5f) / (1f - a);
        }

        /// <summary>Where along a highway (arc length from a0, `way` = +1/-1) a ramp may join carriageway `side`: the nearest
        /// spot from InterchangeRampMin out that is on no deck (within 30 m) and near no tunnel; null past InterchangeRampMax.</summary>
        static double? RampSpot(Line e, double[] arc, double a0, int way, int side, double rMin)
        {
            double total = arc[arc.Length - 1];
            for (double r = rMin; r <= InterchangeRampMax; r += InterchangeRampStep)
            {
                double s = a0 + way * r;
                if (s < 60 || s > total - 60) return null;   // this segment's end: the next one is not ours to join
                double f = FracAt(arc, s);
                int fk = Math.Min((int)f, e.Segments - 1);
                double seg = Math.Max(1e-6, arc[fk + 1] - arc[fk]);
                if (Bridges && e.DeckCover != null && Covered(e.DeckCover[side < 0 ? 0 : 1], f, -30.0 / seg)) continue;
                if (Tunnels && e.TunnelSpans != null && e.HArc != null)
                {
                    bool bored = false;
                    foreach (var tn in e.TunnelSpans) if (s > tn.A0 - TunnelForecourt - 40 && s < tn.A1 + TunnelForecourt + 40) bored = true;
                    if (bored) continue;
                }
                return s;
            }
            return null;
        }

        /// <summary>The interchanges (their crossing points) and their ramps whose north-south highway lies in the rectangle.</summary>
        void InterchangesIn(double x0, double z0, double x1, double z1, List<(double x, double z)> points, List<Line> ramps = null)
        {
            if (!Interchanges) return;
            var hw = new List<Line>();
            TakeHighways(x0, z0, x1, z1, hw, 1);
            foreach (var e in hw)
            {
                if (e.Kind != RoadKind.Highway) continue;
                if (e.Underpasses != null) foreach (var u in e.Underpasses) points.Add((u.X, u.Z));
                if (ramps != null && e.Ramps != null) ramps.AddRange(e.Ramps);
            }
        }

        // ---- for tests and tools
        /// <summary>Test accessor: the crossings of a north-south highway segment (and its shore spurs) with east-west ones.</summary>
        public IReadOnlyList<Underpass> HighwayCrossingsOf(long band, long k)
        {
            var e = Highway(1, band, k);
            var r = new List<Underpass>();
            if (e.Exists && e.Underpasses != null) r.AddRange(e.Underpasses);
            if (e.Exists && e.Branches != null) foreach (var b in e.Branches) if (b.Underpasses != null) r.AddRange(b.Underpasses);
            return r;
        }
        /// <summary>Test accessor: the interchange ramps of a north-south highway segment.</summary>
        public List<(double x, double z, float h)[]> HighwayRampsOf(long band, long k)
        {
            var e = Highway(1, band, k);
            var r = new List<(double x, double z, float h)[]>();
            void Add(Line l) { if (l.Ramps != null) foreach (var rp in l.Ramps) r.Add(Pts(rp)); }
            if (!e.Exists) return r;
            Add(e);
            if (e.Branches != null) foreach (var b in e.Branches) Add(b);
            return r;
        }
        /// <summary>Test accessor: a highway segment's own bridge pieces as the world draws them (BridgesIn's filter applied).</summary>
        public List<BridgePiece> DrawnBridgesNear(double x0, double z0, double x1, double z1) => BridgesIn(LinesIn(x0, z0, x1, z1), x0, z0, x1, z1);
    }
}
