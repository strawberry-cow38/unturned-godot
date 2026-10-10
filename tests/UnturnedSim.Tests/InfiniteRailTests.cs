using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Railways in the infinite world (strawberry 2026-10-10: "add railways."). A window of 48 rail segments (both axes,
    // four bands, six segments) and the main roads round the origin.
    [TestFixture]
    public class InfiniteRailTests
    {
        static readonly InfiniteTerrain Gen = new InfiniteTerrain(1337);
        static List<InfiniteRoads.Line> _window;
        static List<InfiniteRoads.Line> Window()
        {
            if (_window != null) return _window;
            var w = new List<InfiniteRoads.Line>();
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -3; k <= 2; k++) w.AddRange(Gen.Roads.RailLines(axis, band, k));   // (k -3: 0/-1/-3 meets 0/-1/-2 at a crossing-set height)
            return _window = w;
        }

        /// <summary>Nearest point of a line's centreline: distance, fractional dense index, profile height there.</summary>
        static (double d, double f, float h) Nearest(InfiniteRoads.Line e, double x, double z)
        {
            double best = double.MaxValue, bf = 0; float bh = 0;
            for (int i = 0; i + 1 < e.X.Length; i++)
            {
                double sx = e.X[i + 1] - e.X[i], sz = e.Z[i + 1] - e.Z[i], ss = sx * sx + sz * sz;
                if (ss < 1e-12) continue;
                double t = Math.Clamp(((x - e.X[i]) * sx + (z - e.Z[i]) * sz) / ss, 0, 1);
                double dx = e.X[i] + sx * t - x, dz = e.Z[i] + sz * t - z, d = Math.Sqrt(dx * dx + dz * dz);
                if (d < best) { best = d; bf = i + t; bh = e.H[i] + (e.H[i + 1] - e.H[i]) * (float)t; }
            }
            return (best, bf, bh);
        }

        static bool SegCross(double ax, double az, double bx, double bz, double cx, double cz, double dx, double dz)
        {
            double rx = bx - ax, rz = bz - az, sx = dx - cx, sz = dz - cz, den = rx * sz - rz * sx;
            if (Math.Abs(den) < 1e-12) return false;
            double t = ((cx - ax) * sz - (cz - az) * sx) / den, u = ((cx - ax) * rz - (cz - az) * rx) / den;
            return t >= 0 && t < 1 && u >= 0 && u < 1;
        }
        static int CrossCount(IList<(double x, double z)> a, InfiniteRoads.Line b)
        {
            int n = 0;
            for (int i = 0; i + 1 < a.Count; i++)
            {
                double mnx = Math.Min(a[i].x, a[i + 1].x), mxx = Math.Max(a[i].x, a[i + 1].x), mnz = Math.Min(a[i].z, a[i + 1].z), mxz = Math.Max(a[i].z, a[i + 1].z);
                if (mxx < b.MinX || mnx > b.MaxX || mxz < b.MinZ || mnz > b.MaxZ) continue;
                for (int k = 0; k + 1 < b.X.Length; k++)
                    if (SegCross(a[i].x, a[i].z, a[i + 1].x, a[i + 1].z, b.X[k], b.Z[k], b.X[k + 1], b.Z[k + 1])) n++;
            }
            return n;
        }
        static List<(double x, double z)> Poly(InfiniteRoads.Line e) => Enumerable.Range(0, e.X.Length).Select(i => (e.X[i], e.Z[i])).ToList();
        static List<(double x, double z)> Poly((double x, double z, float h)[] p) => p.Select(q => (q.x, q.z)).ToList();
        static bool Covered(List<InfiniteRoads.DeckSpan> c, double f) => c != null && c.Any(s => f >= s.F0 && f <= s.F1);

        /// <summary>A railway is a straight, gentle thing: every segment keeps to RailGrade and curves no tighter than
        /// 250 m; and its track is ONE continuous run of tiles -- every joint closed at the ballast's feet, its top and
        /// the rail heads (the turn, both ways, is what opens a joint), and each segment's last tile ending exactly
        /// where the next segment's first begins, heading the same way. A closing sleeper only at a true end.</summary>
        [Test]
        public void RailsAreGentleAndTheirTrackIsUnbroken()
        {
            var lines = Window();
            double km = 0, worstGrade = 0, minR = double.MaxValue, worstGap = 0, worstJoin = 0, worstJoinTurn = 0, worstEnd = 0;
            int units = 0, joints = 0, joins = 0, moved = 0, sleepers = 0, openEnds = 0; string where = "";
            var starts = new List<(double x, double z, RailPiece first, InfiniteRoads.Line e)>();
            foreach (var e in lines)
            {
                for (int i = 0; i + 1 < e.X.Length; i++)
                {
                    double dx = e.X[i + 1] - e.X[i], dz = e.Z[i + 1] - e.Z[i], l = Math.Sqrt(dx * dx + dz * dz);
                    km += l / 1000;
                    worstGrade = Math.Max(worstGrade, Math.Abs(e.H[i + 1] - e.H[i]) / l);
                    if (i + 2 < e.X.Length)
                    {
                        double ex = e.X[i + 2] - e.X[i + 1], ez = e.Z[i + 2] - e.Z[i + 1], l2 = Math.Sqrt(ex * ex + ez * ez);
                        double turn = Math.Acos(Math.Clamp((dx * ex + dz * ez) / (l * l2), -1, 1));
                        if (turn > 1e-7) minR = Math.Min(minR, 0.5 * (l + l2) / turn);
                    }
                }
                var tr = Gen.Roads.TrackOf(e);
                var u = tr.Where(p => p.Kind == 0).ToList();
                units += u.Count;
                for (int i = 0; i + 1 < u.Count; i++)
                {
                    joints++;
                    double g = Gap(u[i], u[i + 1]);
                    if (g > worstGap) { worstGap = g; where = $"({u[i].X:0.0}, {u[i].Z:0.0})"; }
                }
                // the last tile ends at the line's end
                var last = u[^1];
                double endX = last.X + last.DX * InfiniteRoads.RailPitch * last.K, endZ = last.Z + last.DZ * InfiniteRoads.RailPitch * last.K;
                double endY = last.Y + last.DY * InfiniteRoads.RailPitch * last.K;
                int n = e.X.Length - 1;
                worstEnd = Math.Max(worstEnd, Math.Sqrt((endX - e.X[n]) * (endX - e.X[n]) + (endZ - e.Z[n]) * (endZ - e.Z[n]) + Math.Pow(endY - InfiniteRoads.RailOriginY(e.H[n]), 2)));
                sleepers += tr.Count(p => p.Kind == 1);
                starts.Add((e.X[0], e.Z[0], u[0], e));
            }
            // across every anchor: one segment's last tile ends where the next one's first starts, on the same heading
            foreach (var e in lines)
            {
                var u = Gen.Roads.TrackOf(e).Where(p => p.Kind == 0).ToList();
                var last = u[^1];
                int n = e.X.Length - 1;
                var nx = starts.Where(s => s.e != e && Math.Abs(s.x - e.X[n]) < 0.5 && Math.Abs(s.z - e.Z[n]) < 0.5).ToList();
                // open: no other rail line -- in the window or past it -- starts or ends here
                bool met = Gen.Roads.LinesIn(e.X[n] - 1, e.Z[n] - 1, e.X[n] + 1, e.Z[n] + 1).Any(o => o.Kind == RoadKind.Rail && o != e
                    && (Math.Abs(o.X[0] - e.X[n]) < 0.5 && Math.Abs(o.Z[0] - e.Z[n]) < 0.5 || Math.Abs(o.X[^1] - e.X[n]) < 0.5 && Math.Abs(o.Z[^1] - e.Z[n]) < 0.5));
                if (!met) openEnds++;
                if (nx.Count == 0) continue;
                // a join the anchor's own height did not set: a crossing's approach reached it, and the two sides met
                // at that height instead (the case a per-segment build left as a 2.4 m step)
                float anchorH = Math.Max(Gen.NaturalHeight(e.X[n], e.Z[n]), InfiniteTerrain.SeaLevel + InfiniteRoads.RailDeckAboveWater);
                if (nx.Count > 0 && (Math.Abs(e.H[n] - anchorH) > 0.01 || nx.Any(s => Math.Abs(s.e.H[0] - anchorH) > 0.01))) moved++;
                foreach (var s in nx)
                {
                    joins++;
                    double endX = last.X + last.DX * InfiniteRoads.RailPitch * last.K, endZ = last.Z + last.DZ * InfiniteRoads.RailPitch * last.K, endY = last.Y + last.DY * InfiniteRoads.RailPitch * last.K;
                    worstJoin = Math.Max(worstJoin, Math.Sqrt((endX - s.first.X) * (endX - s.first.X) + (endY - s.first.Y) * (endY - s.first.Y) + (endZ - s.first.Z) * (endZ - s.first.Z)));
                    double hl = Math.Sqrt(last.DX * last.DX + last.DZ * last.DZ), sl = Math.Sqrt(s.first.DX * s.first.DX + s.first.DZ * s.first.DZ);
                    worstJoinTurn = Math.Max(worstJoinTurn, Math.Acos(Math.Clamp((last.DX * s.first.DX + last.DZ * s.first.DZ) / (hl * sl), -1, 1)) * 180 / Math.PI);
                }
            }
            TestContext.WriteLine($"{lines.Count} rail lines, {km:0.0} km: steepest {worstGrade * 100:0.000}%, tightest radius {minR:0} m; {units} tiles, {joints} joints, " +
                                  $"widest {worstGap * 1000:0.00} mm {where}; last tile to line end {worstEnd * 1000:0.00} mm; {joins} joins across anchors, " +
                                  $"worst {worstJoin * 1000:0.00} mm / {worstJoinTurn:0.000} deg ({moved} at a height a crossing set); {sleepers} closing sleepers, {openEnds} open ends");
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(35)); Assert.That(km, Is.GreaterThan(250));
            Assert.That(worstGrade, Is.LessThanOrEqualTo(InfiniteRoads.RailGrade * 1.0001));
            Assert.That(minR, Is.GreaterThan(250), "a curve too tight for a main line");
            Assert.That(worstGap, Is.LessThan(0.001), "a track joint stands open");
            Assert.That(worstEnd, Is.LessThan(0.001), "the track stops short of (or runs past) its line's end");
            Assert.That(joins, Is.GreaterThan(20));
            Assert.That(worstJoin, Is.LessThan(0.001), "the track steps across an anchor");
            Assert.That(moved, Is.GreaterThanOrEqualTo(1), "no join in the window at a crossing-set height: the step case is untested");
            Assert.That(worstJoinTurn, Is.LessThan(0.5), "the track kinks across an anchor");
            Assert.That(sleepers, Is.EqualTo(openEnds), "a closing sleeper at every open end and nowhere else");
        }

        /// <summary>The widest a joint between two tiles opens at any corner of the unit's cross-section (the ballast's
        /// feet and top edge, the rail heads): how far the end of one falls short of the next one's start plane, and the
        /// next one's start of the first one's end plane.</summary>
        internal static double Gap(RailPiece a, RailPiece b)
        {
            static (double x, double y, double z) Xf(RailPiece p, double lx, double ly, double lz)
            {
                double zx = p.DX, zy = p.DY, zz = p.DZ;
                double xx = zz, xz = -zx, xl = Math.Sqrt(xx * xx + xz * xz); xx /= xl; xz /= xl;
                double yx = zy * xz, yy = zz * xx - zx * xz, yz = -zy * xx;
                return (p.X + xx * lx + yx * ly + zx * lz * p.K, p.Y + yy * ly + zy * lz * p.K, p.Z + xz * lx + yz * ly + zz * lz * p.K);
            }
            double worst = 0;
            var corners = new (double lx, double ly)[] { (-InfiniteRoads.RailHalfWidth, InfiniteRoads.RailBallastFoot), (InfiniteRoads.RailHalfWidth, InfiniteRoads.RailBallastFoot),
                                                         (-InfiniteRoads.RailBallastTopHalf, InfiniteRoads.RailBallastTop), (InfiniteRoads.RailBallastTopHalf, InfiniteRoads.RailBallastTop),
                                                         (-1.6, InfiniteRoads.RailHead), (1.6, InfiniteRoads.RailHead) };
            foreach (var (lx, ly) in corners)
            {
                var endA = Xf(a, lx, ly, InfiniteRoads.RailPitch);
                double into = (endA.x - b.X) * b.DX + (endA.y - b.Y) * b.DY + (endA.z - b.Z) * b.DZ;
                if (into < 0) worst = Math.Max(worst, -into);
                var startB = Xf(b, lx, ly, 0);
                double past = (startB.x - a.X) * a.DX + (startB.y - a.Y) * a.DY + (startB.z - a.Z) * a.DZ - InfiniteRoads.RailPitch * a.K;
                if (past > 0) worst = Math.Max(worst, past);
            }
            return worst;
        }

        /// <summary>A railway never meets a highway, or another railway, on the level. At every crossing (each one
        /// recorded, none skipped): UNDER a highway, the highway's own decks span the rail and stand the loading gauge
        /// clear over its rails; OVER a highway or the east-west line, the rail is on its own deck the whole way across
        /// and that deck's soffit clears the road (5 m) or the train under it; under a highway BRIDGE that was already
        /// there, that bridge's soffit clears the train. Checked every metre along the rail across the other's
        /// asphalt (or formation).</summary>
        [Test]
        public void RailsCrossHighwaysAndEachOtherGradeSeparated()
        {
            int under = 0, over = 0, overRail = 0, existing = 0, overTunnel = 0, samples = 0, unrecorded = 0;
            double worstUnder = double.MaxValue, worstOver = double.MaxValue, worstOverRail = double.MaxValue, worstHill = double.MaxValue;
            int offDeck = 0, undecked = 0;
            foreach (var e in Window())
            {
                var near = Gen.Roads.LinesIn(e.MinX, e.MinZ, e.MaxX, e.MaxZ).Where(o => o != e && (o.Kind == RoadKind.Highway || o.Kind == RoadKind.Rail)).ToList();
                var ups = e.Underpasses ?? new List<InfiniteRoads.Underpass>();
                // every crossing is a recorded one: a highway or an earlier-built rail crossed with no separation is a bug
                foreach (var o in near)
                {
                    if (o.Kind == RoadKind.Rail && !(o.Id >> 60 == ((1L << 1) | 0) && e.Id >> 60 == ((1L << 1) | 1))) continue;   // only N-S over E-W
                    int c = CrossCount(Poly(e), o), rec = ups.Count(u => u.HwId == o.Id);
                    if (c != rec) unrecorded++;
                }
                var arc = new double[e.X.Length];
                for (int i = 1; i < e.X.Length; i++) arc[i] = arc[i - 1] + Math.Sqrt((e.X[i] - e.X[i - 1]) * (e.X[i] - e.X[i - 1]) + (e.Z[i] - e.Z[i - 1]) * (e.Z[i] - e.Z[i - 1]));
                foreach (var u in ups)
                {
                    var o = near.FirstOrDefault(l => l.Id == u.HwId);
                    Assert.That(o, Is.Not.Null, $"the line a rail crosses at ({u.X:0}, {u.Z:0}) is not there");
                    if (u.OverTunnel) overTunnel++; else if (u.Existing) existing++; else if (!u.Over) under++; else if (o.Kind == RoadKind.Rail) overRail++; else over++;
                    // every metre of this rail within 60 m of the crossing that is over the other's asphalt / formation
                    for (int i = 0; i + 1 < e.X.Length; i++)
                    {
                        double seg = arc[i + 1] - arc[i];
                        for (double s = 0; s < seg; s += 1.0)
                        {
                            double t = s / seg, x = e.X[i] + (e.X[i + 1] - e.X[i]) * t, z = e.Z[i] + (e.Z[i + 1] - e.Z[i]) * t;
                            if ((x - u.X) * (x - u.X) + (z - u.Z) * (z - u.Z) > 60 * 60) continue;
                            var (d, of, oh) = Nearest(o, x, z);
                            if (d > InfiniteRoads.PavedHalf(o.Kind)) continue;
                            samples++;
                            float h = e.H[i] + (e.H[i + 1] - e.H[i]) * (float)t, head = InfiniteRoads.RailHeadY(h);
                            if (u.OverTunnel)
                            {
                                // over the hill a tunnel bores (a highway's, or the east-west line's): the rail's formation
                                // stands TunnelCover over its shells
                                float shell = InfiniteRoads.TunnelSurfaceY(o.Kind, oh) + InfiniteRoads.ShellTopOf(o.Kind, InfiniteRoads.TubeOffset(o.Kind, 0));
                                worstHill = Math.Min(worstHill, h - InfiniteRoads.Bed - shell);
                            }
                            else if (!u.Over)
                            {
                                // the highway's deck (its own bridge, or the short span it laid over this rail) over the rails
                                float soffit = InfiniteRoads.SurfaceY(RoadKind.Highway, oh) + InfiniteRoads.DeckSoffit;
                                worstUnder = Math.Min(worstUnder, soffit - head);
                                bool decked = u.Existing ? o.DeckCover != null && (Covered(o.DeckCover[0], of) || Covered(o.DeckCover[1], of))
                                                         : u.Cover.Any(c => c is InfiniteRoads.DeckSpan sp && of >= sp.F0 - 0.5 && of <= sp.F1 + 0.5);
                                if (!decked) undecked++;
                            }
                            else
                            {
                                float soffit = InfiniteRoads.RailOriginY(h) + InfiniteRoads.DeckSoffit, below = InfiniteRoads.SurfaceY(o.Kind, oh);
                                if (o.Kind == RoadKind.Rail) worstOverRail = Math.Min(worstOverRail, soffit - below);
                                else worstOver = Math.Min(worstOver, soffit - below);
                                if (!Covered(e.DeckCover?[0], i + t)) offDeck++;
                            }
                        }
                    }
                }
            }
            TestContext.WriteLine($"crossings: {under} under a highway's own decks, {existing} under a highway bridge, {over} over a highway, {overRail} over a rail, {overTunnel} over a tunnel ({worstHill:0.00} m over its shells); " +
                                  $"{samples} metres checked; clearance over the rails under a deck {worstUnder:0.00} m (loading gauge {InfiniteRoads.RailLoadingHeight}), " +
                                  $"under a rail deck over a highway {worstOver:0.00} m, over a rail {worstOverRail:0.00} m; {undecked} under no deck, {offDeck} over off a deck, {unrecorded} unrecorded");
            Assert.That(under + existing, Is.GreaterThanOrEqualTo(3)); Assert.That(over, Is.GreaterThanOrEqualTo(5)); Assert.That(overRail, Is.GreaterThanOrEqualTo(1));
            Assert.That(unrecorded, Is.Zero, "a rail crosses a highway or rail with no separation");
            if (overTunnel > 0) Assert.That(worstHill, Is.GreaterThanOrEqualTo(InfiniteRoads.TunnelCover), "a rail digs into a tunnel's shell");
            Assert.That(worstUnder, Is.GreaterThanOrEqualTo(InfiniteRoads.RailLoadingHeight), "a train would hit a deck");
            Assert.That(worstOver, Is.GreaterThanOrEqualTo(5f), "a rail deck too low over a highway");
            Assert.That(worstOverRail, Is.GreaterThanOrEqualTo(InfiniteRoads.RailLoadingHeight), "a rail deck too low over the other line's train");
            Assert.That(undecked, Is.Zero, "rail under a highway with no deck over it");
            Assert.That(offDeck, Is.Zero, "rail over a highway off its deck");
        }

        /// <summary>Main roads cross a railway ONCE or not at all, square, and on the level at one of the rail's sites:
        /// there the rail is level across the whole road, and the road level across the whole formation, its asphalt
        /// RailProud under the rail heads. Where no site was in reach the main goes over (its own deck) or under (a
        /// viaduct). Small roads, trails and ramps never cross a railway; no roadside pole stands at a level crossing.</summary>
        [Test]
        public void MainsCrossRailsOnTheLevelOrOverThem()
        {
            int level = 0, separated = 0, overTunnel = 0, links = 0, branchX = 0, rampX = 0, twice = 0, unrecorded = 0, polesNear = 0;
            double worstRailFlat = 0, worstRoadFlat = 0, worstFlush = 0, worstAngle = 90, worstCover = double.MaxValue;
            for (long cx = -6; cx <= 6; cx++)
                for (long cz = -6; cz <= 6; cz++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var m = Gen.Roads.MainCentreline(cx, cz, dir);
                        if (m == null) continue;
                        links++;
                        double mnx = m.Min(p => p.x) - 50, mxx = m.Max(p => p.x) + 50, mnz = m.Min(p => p.z) - 50, mxz = m.Max(p => p.z) + 50;
                        var rails = Gen.Roads.LinesIn(mnx, mnz, mxx, mxz).Where(l => l.Kind == RoadKind.Rail).ToList();
                        var lcs = Gen.Roads.MainLevelCrossings(cx, cz, dir);
                        var ups = Gen.Roads.MainUnderpasses(cx, cz, dir);
                        foreach (var r in rails)
                        {
                            int c = CrossCount(Poly(m), r);
                            if (c > 1) twice++;
                            if (c == 1)
                            {
                                bool lv = lcs.Any(l => Nearest(r, l.X, l.Z).d < 0.5), gs = ups.Any(u => u.HwId == r.Id);
                                if (lv) level++; else if (gs) separated++;
                                else
                                {
                                    // over the hill a rail tunnel bores: the bore's shell stays under the road's own bed
                                    var mline = new InfiniteRoads.Line { X = m.Select(p => p.x).ToArray(), Z = m.Select(p => p.z).ToArray(), H = m.Select(p => p.h).ToArray() };
                                    var tn = r.TunnelSpans?.FirstOrDefault(t => Enumerable.Range(0, t.X.Length).Any(i => Nearest(mline, t.X[i], t.Z[i]).d < InfiniteRoads.PavedHalf(RoadKind.Main)));
                                    if (tn == null) unrecorded++;
                                    else
                                    {
                                        overTunnel++;
                                        for (int i = 0; i < tn.X.Length; i++)
                                        {
                                            var (d, _, mh) = Nearest(mline, tn.X[i], tn.Z[i]);
                                            if (d < InfiniteRoads.PavedHalf(RoadKind.Main)) worstCover = Math.Min(worstCover, mh - InfiniteRoads.Bed - (tn.Y[i] + InfiniteRoads.ShellTopOf(RoadKind.Rail, 0f)));
                                        }
                                    }
                                }
                            }
                            foreach (var br in Gen.Roads.BranchesOf(cx, cz, dir)) if (CrossCount(Poly(br.pts), r) > 0) branchX++;
                            foreach (var rp in Gen.Roads.MainRamps(cx, cz, dir)) if (CrossCount(Poly(rp), r) > 0) rampX++;
                        }
                        foreach (var lc in lcs)
                        {
                            var r = rails.OrderBy(l => Nearest(l, lc.X, lc.Z).d).First();
                            double sin = Math.Sqrt(Math.Max(0, 1 - Math.Pow(lc.RX * lc.MX + lc.RZ * lc.MZ, 2)));
                            worstAngle = Math.Min(worstAngle, Math.Asin(Math.Min(1, sin)) * 180 / Math.PI);
                            // the rail level under the whole road: its rail heads at the crossing's Y for the road's width
                            double reachR = (InfiniteRoads.PavedHalf(RoadKind.Main) + 1) / sin;
                            for (double s = -reachR; s <= reachR; s += 0.5)
                            {
                                var (_, _, h) = Nearest(r, lc.X + lc.RX * s, lc.Z + lc.RZ * s);
                                worstRailFlat = Math.Max(worstRailFlat, Math.Abs(InfiniteRoads.RailHeadY(h) - lc.Y));
                            }
                            // the road level over the whole formation, RailProud under the heads
                            double reachM = (InfiniteRoads.RailFormationHalf + 1) / sin;
                            var mline = new InfiniteRoads.Line { X = m.Select(p => p.x).ToArray(), Z = m.Select(p => p.z).ToArray(), H = m.Select(p => p.h).ToArray() };
                            for (double s = -reachM; s <= reachM; s += 0.5)
                            {
                                var (_, _, h) = Nearest(mline, lc.X + lc.MX * s, lc.Z + lc.MZ * s);
                                float road = InfiniteRoads.SurfaceY(RoadKind.Main, h);
                                worstRoadFlat = Math.Max(worstRoadFlat, Math.Abs(road - (lc.Y - InfiniteRoads.RailProud)));
                                if (Math.Abs(s) < 1) worstFlush = Math.Max(worstFlush, Math.Abs(lc.Y - road - InfiniteRoads.RailProud));
                            }
                            foreach (var p in Gen.Roads.PolesIn(Gen.Roads.LinesIn(lc.X - 60, lc.Z - 60, lc.X + 60, lc.Z + 60), lc.X - 60, lc.Z - 60, lc.X + 60, lc.Z + 60))
                                if (Math.Sqrt((p.X - lc.X) * (p.X - lc.X) + (p.Z - lc.Z) * (p.Z - lc.Z)) < 30) polesNear++;
                        }
                    }
            TestContext.WriteLine($"{links} main links: {level} level crossings, {separated} grade-separated rail crossings, {overTunnel} over a rail tunnel (its shell {worstCover:0.0} m under the road's bed at least); square to {worstAngle:0.0} deg; " +
                                  $"rail heads off level under the road by {worstRailFlat * 1000:0.0} mm, the road off level over the formation by {worstRoadFlat * 1000:0.0} mm, " +
                                  $"flush to {worstFlush * 1000:0.0} mm; {twice} crossed twice, {unrecorded} unrecorded, {branchX} branch and {rampX} ramp crossings, {polesNear} poles at a crossing");
            Assert.That(level, Is.GreaterThanOrEqualTo(10)); Assert.That(separated, Is.GreaterThanOrEqualTo(2));
            Assert.That(twice, Is.Zero); Assert.That(unrecorded, Is.Zero, "a main crosses a railway with neither a level crossing nor a separation");
            if (overTunnel > 0) Assert.That(worstCover, Is.GreaterThanOrEqualTo(InfiniteRoads.TunnelCover), "a main's bed digs into a rail tunnel's shell");
            Assert.That(worstAngle, Is.GreaterThanOrEqualTo(60));
            Assert.That(worstRailFlat, Is.LessThan(0.002), "the rails are not level across the road");
            Assert.That(worstRoadFlat, Is.LessThan(0.002), "the road is not level across the track");
            Assert.That(branchX + rampX, Is.Zero, "a small road, trail or ramp crosses a railway");
            Assert.That(polesNear, Is.Zero, "a pole stands at a level crossing");
        }

        /// <summary>The track lies ON its formation: under every tile off a deck, out of a tunnel and away from a
        /// crossing, the LOD0 ground mesh -- what is drawn -- meets the ballast across its whole width. The ballast's
        /// foot is set 40 mm under the formation (RailBallastFoot + RailSet), so the mesh may sag under the formation
        /// by less than that before daylight shows under it (bar: 30 mm, a centimetre short); and it may rise over the
        /// formation by 60 mm before it covers more than a sliver of the ballast's 0.52 m side. (At a 5 m formation the
        /// mesh sagged 158 mm under the feet on embankments.) The ballast and its cess wear gravel; no tree stands on
        /// the formation.</summary>
        [Test]
        public void TheTrackLiesOnItsFormation()
        {
            var regions = new Dictionary<(int, int), RegionData>();
            RegionData Region(double x, double z) { var rc = RegionCoord.Containing(x, z); if (!regions.TryGetValue((rc.X, rc.Z), out var d)) regions[(rc.X, rc.Z)] = d = Gen.Generate(rc, 0); return d; }
            int feet = 0, gravel = 0, notGravel = 0, trees = 0; double hang = 0, bury = 0; string where = "";
            // two whole segments, one each way, every tile
            foreach (var (axis, band, k) in new[] { (0, 0L, 0L), (1, 0L, 0L) })
                foreach (var e in Gen.Roads.RailLines(axis, band, k))
                {
                    var lcs = Gen.Roads.LinesIn(e.MinX, e.MinZ, e.MaxX, e.MaxZ).Where(l => l.LevelCrossings != null).SelectMany(l => l.LevelCrossings).ToList();
                    foreach (var p in Gen.Roads.TrackOf(e))
                    {
                        if (p.Kind != 0) continue;
                        var (_, f, _) = Nearest(e, p.X, p.Z);
                        if (Covered(e.DeckCover?[0], f - 1) || Covered(e.DeckCover?[0], f + 1)) continue;
                        if (e.TunnelSpans != null && e.TunnelSpans.Any(t => f >= t.F0 - 4 && f <= t.F1 + 4)) continue;
                        if (e.Underpasses != null && e.Underpasses.Any(u => (u.X - p.X) * (u.X - p.X) + (u.Z - p.Z) * (u.Z - p.Z) < 60 * 60)) continue;
                        if (lcs.Any(c => (c.X - p.X) * (c.X - p.X) + (c.Z - p.Z) * (c.Z - p.Z) < 40 * 40)) continue;
                        double lx = p.DZ, lz = -p.DX, ll = Math.Sqrt(lx * lx + lz * lz); lx /= ll; lz /= ll;
                        foreach (double o in new[] { -InfiniteRoads.RailHalfWidth, -InfiniteRoads.RailHalfWidth / 2, 0, InfiniteRoads.RailHalfWidth / 2, InfiniteRoads.RailHalfWidth })
                        {
                            double fx = p.X + lx * o, fz = p.Z + lz * o;
                            var d = Region(fx, fz);
                            float mesh = InfiniteTerrain.MeshHeightAt(d, (float)(fx - d.Coord.MinX), (float)(fz - d.Coord.MinZ));
                            // the ballast's underside: its foot at the edges, the root's set under the middle
                            float bed = p.Y - InfiniteRoads.RailSet;
                            feet++;
                            if (bed - mesh > hang) { hang = bed - mesh; where = $"({fx:0.0}, {fz:0.0})"; }
                            bury = Math.Max(bury, mesh - bed);
                        }
                        var dr = Region(p.X, p.Z);
                        int sp = (int)(InfiniteTerrain.RegionSize / dr.Cells), v = dr.Cells + 1;
                        int gi = (int)Math.Round((p.X - dr.Coord.MinX) / sp), gj = (int)Math.Round((p.Z - dr.Coord.MinZ) / sp);
                        if ((InfiniteTerrain.Layer)dr.Layers[Math.Clamp(gj, 0, v - 1) * v + Math.Clamp(gi, 0, v - 1)] == InfiniteTerrain.Layer.Gravel) gravel++; else notGravel++;
                    }
                }
            foreach (var d in regions.Values)
                foreach (var t in d.Trees)
                    foreach (var e in Window())
                        if (t.X >= e.MinX && t.X <= e.MaxX && t.Z >= e.MinZ && t.Z <= e.MaxZ && Nearest(e, t.X, t.Z).d < InfiniteRoads.RailFormationHalf) trees++;
            TestContext.WriteLine($"{feet} points under the ballast over {regions.Count} LOD0 regions: hangs over the mesh by {hang * 1000:0.0} mm at worst {where}, buried {bury * 1000:0.0} mm; " +
                                  $"{gravel} roots on gravel, {notGravel} not; {trees} trees on a formation");
            Assert.That(feet, Is.GreaterThan(10000));
            float footUnder = -InfiniteRoads.RailBallastFoot - InfiniteRoads.RailSet;   // 0.04
            Assert.That(hang, Is.LessThan(footUnder - 0.01), "daylight under the ballast's foot");
            Assert.That(bury, Is.LessThan(0.06), "the ground buries the ballast");
            Assert.That(notGravel, Is.LessThan(gravel / 100), "the track's bed is not worn to gravel");
            Assert.That(trees, Is.Zero);
        }

        /// <summary>Where a hill is too big to cut, the rail bores it: one tube on the track, narrower and lower than a
        /// highway's, with the hill standing over its whole shell from portal to portal, the floor walkable at the
        /// formation, and room for a train in the bore either side of the centreline.</summary>
        [Test]
        public void RailTunnelsAreBoredThroughTheHills()
        {
            // the train's corners in the bore, from the section alone
            float side = InfiniteRoads.BoreTopOf(RoadKind.Rail, InfiniteRoads.RailLoadingHalf), mid = InfiniteRoads.BoreTopOf(RoadKind.Rail, 0f);
            Assert.That(side - InfiniteRoads.RailHead, Is.GreaterThanOrEqualTo(InfiniteRoads.RailLoadingHeight), "a train's shoulder hits the bore");
            Assert.That(float.IsNaN(InfiniteRoads.BoreTopOf(RoadKind.Rail, InfiniteRoads.BoreReachOf(RoadKind.Rail) + 0.1f)));
            int tunnels = 0, probes = 0, shallow = 0, floorOff = 0; double longest = 0, worstCover = double.MaxValue;
            foreach (var e in Window())
            {
                if (e.TunnelSpans == null) continue;
                foreach (var t in e.TunnelSpans)
                {
                    Assert.That(t.Kind, Is.EqualTo(RoadKind.Rail)); Assert.That(t.SX.Length, Is.EqualTo(1), "a rail tunnel is one tube");
                    tunnels++; longest = Math.Max(longest, t.A1 - t.A0);
                    for (int i = 2; i + 2 < t.X.Length; i += 3)
                    {
                        probes++;
                        // the hill over the shell's crown (the ground mesh may only rise over a shell, never dig into it)
                        float g = Gen.HeightAt(t.X[i], t.Z[i]);
                        double cover = g - (t.Y[i] + InfiniteRoads.ShellTopOf(RoadKind.Rail, 0f));
                        worstCover = Math.Min(worstCover, cover);
                        if (cover < 0) shallow++;
                        // standing in the bore you stand on the floor, the formation
                        float floor = Gen.WalkableHeightAt(t.X[i], t.Z[i]);
                        if (Math.Abs(floor - (t.Y[i] - InfiniteRoads.RailSet)) > 0.01f) floorOff++;
                    }
                }
            }
            TestContext.WriteLine($"{tunnels} rail tunnels, longest {longest:0} m; bore over a train's shoulder {side - InfiniteRoads.RailHead:0.00} m, crown {mid:0.00} m; " +
                                  $"{probes} stations: least ground over the shell {worstCover:0.00} m ({shallow} under it), {floorOff} with the floor off the formation");
            Assert.That(tunnels, Is.GreaterThanOrEqualTo(5));
            Assert.That(shallow, Is.Zero, "the ground dips into a rail tunnel's shell");
            Assert.That(floorOff, Is.Zero);
        }

        /// <summary>strawberry 2026-10-10: "the tunnel tries to follow the terrain when it shouldnt?" -- it did: the
        /// line it follows was shaped to the smoothed land, and humped up inside the hill by up to 2.6 m (highway) and
        /// 6 m (rail) over the straight grade between its portals. Now, in every bore, highway or rail, the datum the
        /// tube is swept along is that straight grade, to a centimetre.</summary>
        [Test]
        public void BoresRunStraightThroughTheHill()
        {
            var spans = new List<InfiniteRoads.TunnelSpan>();
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++) spans.AddRange(Gen.Roads.TunnelsOf(axis, band, k));
            int highway = spans.Count;
            foreach (var e in Window()) if (e.TunnelSpans != null) spans.AddRange(e.TunnelSpans);
            double worstUp = 0, worstDown = 0; string where = "";
            foreach (var t in spans)
            {
                int m = t.Y.Length - 1;
                var h = new double[m + 1];
                for (int i = 1; i <= m; i++) h[i] = h[i - 1] + Math.Sqrt((t.X[i] - t.X[i - 1]) * (t.X[i] - t.X[i - 1]) + (t.Z[i] - t.Z[i - 1]) * (t.Z[i] - t.Z[i - 1]));
                for (int i = 0; i <= m; i++)
                {
                    double lin = t.Y[0] + (t.Y[m] - t.Y[0]) * h[i] / h[m], d = t.Y[i] - lin;
                    if (d > worstUp) { worstUp = d; where = $" ({t.Kind} at {t.X[i]:0}, {t.Z[i]:0})"; }
                    worstDown = Math.Max(worstDown, -d);
                }
            }
            TestContext.WriteLine($"{highway} highway and {spans.Count - highway} rail tunnels: the datum rises {worstUp * 100:0.0} cm over the portal-to-portal grade at worst{where}, dips {worstDown * 100:0.0} cm under it");
            Assert.That(highway, Is.GreaterThanOrEqualTo(3)); Assert.That(spans.Count - highway, Is.GreaterThanOrEqualTo(5));
            Assert.That(worstUp, Is.LessThan(0.01), "a bore humps up with the hill");
            Assert.That(worstDown, Is.LessThan(0.01), "a bore sags");
        }

        [Test]
        public void RailsAreTheSameWhoeverAsks()
        {
            var fresh = new InfiniteTerrain(1337);
            fresh.Roads.RailCentreline(1, 0, 1);   // something else first
            var a = Gen.Roads.RailCentreline(0, 0, 0); var b = fresh.Roads.RailCentreline(0, 0, 0);
            Assert.That(b, Is.EqualTo(a));
            var ta = Gen.Roads.TrackOf(0, 0, 0); var tb = fresh.Roads.TrackOf(0, 0, 0);
            Assert.That(tb.Select(p => (p.X, p.Y, p.Z, p.K)), Is.EqualTo(ta.Select(p => (p.X, p.Y, p.Z, p.K))));
            Assert.That(fresh.Roads.RailSitesOf(0, 0, 0).Select(s => (s.X, s.Z, s.H)), Is.EqualTo(Gen.Roads.RailSitesOf(0, 0, 0).Select(s => (s.X, s.Z, s.H))));
        }
    }
}
