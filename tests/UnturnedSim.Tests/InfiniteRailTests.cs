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
                    for (long k = -3; k <= 2; k++) w.AddRange(Gen.Roads.RailLines(axis, band, k));
            // ...and a pair that meets at a height a crossing set (the case a per-segment build left as a 2.4 m step). It
            // was 0/-1/-3 meeting 0/-1/-2, in the window; double track's wider formation moved that crossing's approach off
            // the anchor, and the nearest such join is now 0/-1/-5 meeting 0/-1/-4, at (-31600, -6778), 69.2 m vs 47.4.
            foreach (long k in new[] { -5L, -4L }) w.AddRange(Gen.Roads.RailLines(0, -1, k));
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

        /// <summary>Horizontal arc length at each dense point.</summary>
        static double[] ArcOf(InfiniteRoads.Line e)
        {
            var a = new double[e.X.Length];
            for (int i = 1; i < a.Length; i++) a[i] = a[i - 1] + Math.Sqrt((e.X[i] - e.X[i - 1]) * (e.X[i] - e.X[i - 1]) + (e.Z[i] - e.Z[i - 1]) * (e.Z[i] - e.Z[i - 1]));
            return a;
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
        /// 250 m; it carries TWO tracks (strawberry 2026-10-11: "double rail lines"), each unit's root RailTrackOffset off the
        /// line's centreline on its own side; and each track is ONE continuous run of tiles -- every joint closed at the
        /// ballast's feet, its top and the rail heads (the turn, both ways, is what opens a joint), and each segment's last
        /// tile ending exactly at its track's end, where the next segment's first tile on the same track begins, heading
        /// the same way. A closing sleeper on each track only at a true end.</summary>
        [Test]
        public void RailsAreGentleAndTheirTrackIsUnbroken()
        {
            var lines = Window();
            double km = 0, worstGrade = 0, minR = double.MaxValue, worstGap = 0, worstJoin = 0, worstJoinTurn = 0, worstEnd = 0, worstOff = 0;
            int units = 0, joints = 0, joins = 0, moved = 0, sleepers = 0, openEnds = 0, wrongSide = 0, single = 0, openChecked = 0; string where = "", joinAt = "";
            var starts = new List<(double x, double z, int track, RailPiece first, InfiniteRoads.Line e)>();
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
                int n = e.X.Length - 1;
                // the line's end heading: its last segment's, the track's offset normal there
                double lx = e.X[n] - e.X[n - 1], lz = e.Z[n] - e.Z[n - 1], ll = Math.Sqrt(lx * lx + lz * lz); lx /= ll; lz /= ll;
                for (int t = 0; t < 2; t++)
                {
                    var u = tr.Where(p => p.Kind == 0 && p.Track == t).ToList();
                    if (u.Count == 0) { single++; continue; }
                    units += u.Count;
                    for (int i = 0; i + 1 < u.Count; i++)
                    {
                        joints++;
                        double g = Gap(u[i], u[i + 1]);
                        if (g > worstGap) { worstGap = g; where = $"({u[i].X:0.0}, {u[i].Z:0.0})"; }
                    }
                    // every root on its own side of the line, RailTrackOffset off it (track 0 on the left of its way) --
                    // the offset only outside a split (RailsSplitAndRejoin's)
                    var arcs = ArcOf(e);
                    foreach (var p in u)
                    {
                        var (d, f, _) = Nearest(e, p.X, p.Z);
                        int a = Math.Clamp((int)Math.Floor(f), 0, n - 1);
                        double along = arcs[a] + (arcs[a + 1] - arcs[a]) * (f - a);
                        if (e.Splits == null || !e.Splits.Any(sp => along >= sp.A0 - 1 && along <= sp.A1 + 1)) worstOff = Math.Max(worstOff, Math.Abs(d - InfiniteRoads.RailTrackOffset));
                        double sx = e.X[a + 1] - e.X[a], sz = e.Z[a + 1] - e.Z[a];
                        bool left = (p.X - e.X[a]) * sz - (p.Z - e.Z[a]) * sx > 0;   // (tz, -tx) is the left of (tx, tz)
                        if (left != (t == 0)) wrongSide++;
                    }
                    // the last tile ends at the track's end -- at an OPEN end, where the line's own last segment sets the
                    // offset's normal (where another line carries on, the two share a heading; the join below checks that)
                    bool carriesOn = Gen.Roads.LinesIn(e.X[n] - 1, e.Z[n] - 1, e.X[n] + 1, e.Z[n] + 1).Any(o2 => o2.Kind == RoadKind.Rail && o2 != e
                        && (Math.Abs(o2.X[0] - e.X[n]) < 0.5 && Math.Abs(o2.Z[0] - e.Z[n]) < 0.5 || Math.Abs(o2.X[^1] - e.X[n]) < 0.5 && Math.Abs(o2.Z[^1] - e.Z[n]) < 0.5));
                    starts.Add((e.X[0], e.Z[0], t, u[0], e));
                    if (carriesOn) continue;
                    openChecked++;
                    double o = t == 0 ? -InfiniteRoads.RailTrackOffset : InfiniteRoads.RailTrackOffset;
                    double tx = e.X[n] - lz * o, tz = e.Z[n] + lx * o;
                    var last = u[^1];
                    double endX = last.X + last.DX * InfiniteRoads.RailPitch * last.K, endZ = last.Z + last.DZ * InfiniteRoads.RailPitch * last.K;
                    double endY = last.Y + last.DY * InfiniteRoads.RailPitch * last.K;
                    worstEnd = Math.Max(worstEnd, Math.Sqrt((endX - tx) * (endX - tx) + (endZ - tz) * (endZ - tz) + Math.Pow(endY - InfiniteRoads.RailOriginY(e.H[n]), 2)));
                }
                sleepers += tr.Count(p => p.Kind == 1);
            }
            // across every anchor: on each track, one segment's last tile ends where the next one's first starts, on the
            // same heading
            foreach (var e in lines)
            {
                var tr = Gen.Roads.TrackOf(e);
                int n = e.X.Length - 1;
                // open: no other rail line -- in the window or past it -- starts or ends here
                bool met = Gen.Roads.LinesIn(e.X[n] - 1, e.Z[n] - 1, e.X[n] + 1, e.Z[n] + 1).Any(o => o.Kind == RoadKind.Rail && o != e
                    && (Math.Abs(o.X[0] - e.X[n]) < 0.5 && Math.Abs(o.Z[0] - e.Z[n]) < 0.5 || Math.Abs(o.X[^1] - e.X[n]) < 0.5 && Math.Abs(o.Z[^1] - e.Z[n]) < 0.5));
                if (!met) openEnds += 2;   // both tracks end there
                var nx = starts.Where(s => s.e != e && Math.Abs(s.x - e.X[n]) < 0.5 && Math.Abs(s.z - e.Z[n]) < 0.5).ToList();
                if (nx.Count == 0) continue;
                // a join the anchor's own height did not set: a crossing's approach reached it, and the two sides met
                // at that height instead (the case a per-segment build left as a 2.4 m step)
                float anchorH = Math.Max(Gen.NaturalHeight(e.X[n], e.Z[n]), InfiniteTerrain.SeaLevel + InfiniteRoads.RailDeckAboveWater);
                if (Math.Abs(e.H[n] - anchorH) > 0.01 || nx.Any(s => Math.Abs(s.e.H[0] - anchorH) > 0.01)) moved++;
                foreach (var s in nx)
                {
                    var last = tr.Where(p => p.Kind == 0 && p.Track == s.track).Last();
                    joins++;
                    double endX = last.X + last.DX * InfiniteRoads.RailPitch * last.K, endZ = last.Z + last.DZ * InfiniteRoads.RailPitch * last.K, endY = last.Y + last.DY * InfiniteRoads.RailPitch * last.K;
                    double j = Math.Sqrt((endX - s.first.X) * (endX - s.first.X) + (endY - s.first.Y) * (endY - s.first.Y) + (endZ - s.first.Z) * (endZ - s.first.Z));
                    if (j > worstJoin) { worstJoin = j; joinAt = $"({e.X[n]:0.0}, {e.Z[n]:0.0}) track {s.track}"; }
                    double hl = Math.Sqrt(last.DX * last.DX + last.DZ * last.DZ), sl = Math.Sqrt(s.first.DX * s.first.DX + s.first.DZ * s.first.DZ);
                    worstJoinTurn = Math.Max(worstJoinTurn, Math.Acos(Math.Clamp((last.DX * s.first.DX + last.DZ * s.first.DZ) / (hl * sl), -1, 1)) * 180 / Math.PI);
                }
            }
            TestContext.WriteLine($"{lines.Count} rail lines, {km:0.0} km: steepest {worstGrade * 100:0.000}%, tightest radius {minR:0} m; {units} tiles on two tracks ({single} tracks with none), " +
                                  $"every root {worstOff * 1000:0.00} mm off its {InfiniteRoads.RailTrackOffset} m offset, {wrongSide} on the wrong side; {joints} joints, " +
                                  $"widest {worstGap * 1000:0.00} mm {where}; last tile to an open track end {worstEnd * 1000:0.00} mm ({openChecked} ends); {joins} joins across anchors, " +
                                  $"worst {worstJoin * 1000:0.00} mm {joinAt} / {worstJoinTurn:0.000} deg ({moved} at a height a crossing set); {sleepers} closing sleepers, {openEnds} open track ends");
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(35)); Assert.That(km, Is.GreaterThan(250));
            Assert.That(worstGrade, Is.LessThanOrEqualTo(InfiniteRoads.RailGrade * 1.0001));
            Assert.That(minR, Is.GreaterThan(250), "a curve too tight for a main line");
            Assert.That(single, Is.Zero, "a line with one track");
            Assert.That(wrongSide, Is.Zero, "a track on the other side than its index says");
            Assert.That(worstOff, Is.LessThan(0.01), "a track's root off its offset from the line");
            Assert.That(worstGap, Is.LessThan(0.001), "a track joint stands open");
            Assert.That(openChecked, Is.GreaterThan(4), "too few open ends to check the last tile against");
            Assert.That(worstEnd, Is.LessThan(0.001), "the track stops short of (or runs past) its end");
            Assert.That(joins, Is.GreaterThan(40));
            Assert.That(worstJoin, Is.LessThan(0.001), "the track steps across an anchor");
            Assert.That(moved, Is.GreaterThanOrEqualTo(1), "no join in the window at a crossing-set height: the step case is untested");
            Assert.That(worstJoinTurn, Is.LessThan(0.5), "the track kinks across an anchor");
            Assert.That(sleepers, Is.EqualTo(openEnds), "a closing sleeper at every open track end and nowhere else");
        }

        /// <summary>SPLITS (strawberry 2026-10-11: "...split parallel lines"): somewhere on a fair share of the lines the two
        /// tracks part, run on parallel 22-30 m apart, and come back. Over the window: each split's tracks stand at least
        /// 22 m apart through its middle and bend no tighter than a main line may (250 m, the line's own bend included)
        /// as they part; none crosses a level
        /// crossing, sits on a deck or goes into a tunnel; the ground between the tracks at the middle is the land's own
        /// grass (clear of the formation, not worn to gravel) while the tracks' own roots stand on gravel; and the ballast's
        /// outer feet lie on the drawn ground as everywhere else.</summary>
        [Test]
        public void RailsSplitAndRejoin()
        {
            var lines = Window();
            int splits = 0, narrow = 0, tight = 0, crossed = 0, decked = 0, bored = 0, middleWorn = 0, middleIn = 0, rootsBare = 0, roots = 0, feet = 0;
            double minApart = double.MaxValue, minR = double.MaxValue, hang = 0, bury = 0; string first = null, buryAt = "", minRAt = "";
            var regions = new Dictionary<(int, int), RegionData>();
            RegionData Region(double x, double z) { var rc = RegionCoord.Containing(x, z); if (!regions.TryGetValue((rc.X, rc.Z), out var d)) regions[(rc.X, rc.Z)] = d = Gen.Generate(rc, 0); return d; }
            InfiniteTerrain.Layer LayerAt(double x, double z)
            {
                var d = Region(x, z); int sp = (int)(InfiniteTerrain.RegionSize / d.Cells), v = d.Cells + 1;
                int gi = (int)Math.Round((x - d.Coord.MinX) / sp), gj = (int)Math.Round((z - d.Coord.MinZ) / sp);
                return (InfiniteTerrain.Layer)d.Layers[Math.Clamp(gj, 0, v - 1) * v + Math.Clamp(gi, 0, v - 1)];
            }
            foreach (var e in lines)
            {
                if (e.Splits == null) continue;
                var arcs = ArcOf(e);
                var tr = Gen.Roads.TrackOf(e);
                var lcs = Gen.Roads.LinesIn(e.MinX, e.MinZ, e.MaxX, e.MaxZ).Where(l => l.LevelCrossings != null).SelectMany(l => l.LevelCrossings).ToList();
                foreach (var sp in e.Splits)
                {
                    splits++;
                    double Along(double x, double z) { var (_, f, _) = Nearest(e, x, z); int a = Math.Clamp((int)Math.Floor(f), 0, e.X.Length - 2); return arcs[a] + (arcs[a + 1] - arcs[a]) * (f - a); }
                    double mid = (sp.A0 + sp.A1) / 2;
                    // the two tracks' roots nearest the middle
                    var t0 = tr.Where(p => p.Kind == 0 && p.Track == 0).OrderBy(p => Math.Abs(Along(p.X, p.Z) - mid)).First();
                    var t1 = tr.Where(p => p.Kind == 0 && p.Track == 1).OrderBy(p => Math.Abs((p.X - t0.X) * t0.DX + (p.Z - t0.Z) * t0.DZ)).First();
                    double apart = Math.Sqrt((t0.X - t1.X) * (t0.X - t1.X) + (t0.Z - t1.Z) * (t0.Z - t1.Z));
                    minApart = Math.Min(minApart, apart);
                    if (apart < 22) { narrow++; first ??= $"tracks {apart:0.0} m apart mid-split at ({t0.X:0}, {t0.Z:0})"; }
                    // each track's own bend through the split: the heading's change over 25 tiles (50 m) against the
                    // ground between -- tile to tile it is the dense line's vertex kinks (12.5 m apart), not the curve
                    const int W = 25;
                    foreach (int t in new[] { 0, 1 })
                    {
                        var u = tr.Where(p => p.Kind == 0 && p.Track == t).ToList();
                        for (int i = 0; i + W < u.Count; i++)
                        {
                            double al = Along(u[i].X, u[i].Z);
                            if (al < sp.A0 - 50 || al > sp.A1 + 50) continue;
                            var v = u[i + W];
                            double hl = Math.Sqrt(u[i].DX * u[i].DX + u[i].DZ * u[i].DZ), nl = Math.Sqrt(v.DX * v.DX + v.DZ * v.DZ);
                            double turn = Math.Acos(Math.Clamp((u[i].DX * v.DX + u[i].DZ * v.DZ) / (hl * nl), -1, 1));
                            double run = Math.Sqrt((v.X - u[i].X) * (v.X - u[i].X) + (v.Z - u[i].Z) * (v.Z - u[i].Z));
                            if (turn > 1e-6 && run / turn < minR) { minR = run / turn; minRAt = $" at ({u[i].X:0.0}, {u[i].Z:0.0}), track {t}, {al - sp.A0:0} m into a split of {sp.A1 - sp.A0:0} m"; }
                        }
                    }
                    // nothing that needs the tracks together inside it
                    foreach (var c in lcs) { double al = Along(c.X, c.Z); if (al >= sp.A0 && al <= sp.A1 && Nearest(e, c.X, c.Z).d < 40) { crossed++; first ??= $"a level crossing in a split at ({c.X:0}, {c.Z:0})"; } }
                    if (e.Raised != null && e.Raised.Any(st => arcs[Math.Min(arcs.Length - 1, st.I1)] >= sp.A0 && arcs[Math.Max(0, st.I0)] <= sp.A1)) { decked++; first ??= "a deck in a split"; }
                    if (e.TunnelSpans != null && e.TunnelSpans.Any(tn => tn.A1 >= sp.A0 && tn.A0 <= sp.A1)) { bored++; first ??= "a tunnel in a split"; }
                    // between the tracks at the middle: grass, clear of the formation
                    double cx = (t0.X + t1.X) / 2, cz = (t0.Z + t1.Z) / 2;
                    var hit = Gen.Roads.Influence(cx, cz);
                    if (hit.Clear <= 0) { middleIn++; first ??= $"the ground between the tracks counts as formation ({hit.Clear:0.0} m) at ({cx:0}, {cz:0})"; }
                    if (LayerAt(cx, cz) == InfiniteTerrain.Layer.Gravel) { middleWorn++; first ??= $"the ground between the tracks is worn to gravel at ({cx:0}, {cz:0})"; }
                    // the tracks themselves: roots on gravel, outer feet on the drawn ground
                    foreach (var p in tr.Where(p => p.Kind == 0))
                    {
                        double al = Along(p.X, p.Z);
                        if (al < sp.A0 || al > sp.A1) continue;
                        roots++;
                        if (LayerAt(p.X, p.Z) != InfiniteTerrain.Layer.Gravel) rootsBare++;
                        double lx = p.DZ, lz = -p.DX, ll = Math.Sqrt(lx * lx + lz * lz); lx /= ll; lz /= ll;
                        foreach (double o in new[] { -InfiniteRoads.RailHalfWidth, InfiniteRoads.RailHalfWidth })
                        {
                            double fx = p.X + lx * o, fz = p.Z + lz * o;
                            var d = Region(fx, fz);
                            float mesh = InfiniteTerrain.MeshHeightAt(d, (float)(fx - d.Coord.MinX), (float)(fz - d.Coord.MinZ));
                            float bed = p.Y - InfiniteRoads.RailSet;
                            feet++; hang = Math.Max(hang, bed - mesh);
                            if (mesh - bed > bury) { bury = mesh - bed; buryAt = $" at ({fx:0.0}, {fz:0.0}), track {p.Track}, {al - sp.A0:0} m into a split of {sp.A1 - sp.A0:0} m, {sp.W:0.0} m out"; }
                        }
                    }
                }
            }
            TestContext.WriteLine($"{splits} splits in {lines.Count} lines: tracks at least {minApart:0.0} m apart mid-split ({narrow} under 22), each track's bend {minR:0} m at the tightest{minRAt}; " +
                                  $"{crossed} level crossings, {decked} decks, {bored} tunnels inside one; between the tracks {middleIn} on the formation, {middleWorn} worn to gravel; " +
                                  $"{roots} roots, {rootsBare} not on gravel; {feet} outer feet hang {hang * 1000:0.0} mm, buried {bury * 1000:0.0} mm{buryAt}{(first == null ? "" : $"; first: {first}")}");
            Assert.That(splits, Is.GreaterThanOrEqualTo(5), "too few splits to mean anything");
            Assert.That(narrow, Is.Zero, first);
            Assert.That(minR, Is.GreaterThan(250), "a track bends tighter than a main line may as it parts");
            Assert.That(crossed + decked + bored, Is.Zero, first);
            Assert.That(middleIn + middleWorn, Is.Zero, first);
            Assert.That(rootsBare, Is.LessThan(roots / 100 + 1), "the parted tracks' bed is not worn to gravel");
            Assert.That(hang, Is.LessThan(-InfiniteRoads.RailBallastFoot - InfiniteRoads.RailSet - 0.01), "daylight under a parted track's ballast");
            Assert.That(bury, Is.LessThan(0.06), "the ground buries a parted track's ballast");
        }

        /// <summary>CHORDS (strawberry 2026-10-11: "rail branching, merging"): where a north-south line crosses an east-west
        /// one, single-track curves in some quadrants leave one line's near track and run onto the other's. Each one: both
        /// ends ON a track of the line it joins (RailTrackOffset off that line's centreline, heading along it, at its
        /// height less the sink), its two ends on two different lines; no bend tighter than ChordMinRadius, no grade over
        /// the ruling one; one unbroken track; crossing no highway and no rail between its ends; and off its decks, its
        /// ballast lies on the drawn ground all the way, through its turnouts (where it takes the other line's profile,
        /// let down ChordSink so its ballast is under the other's).</summary>
        [Test]
        public void ChordsJoinTheLines()
        {
            var chords = new List<InfiniteRoads.Line>();
            for (long band = -2; band <= 1; band++)
                for (long k = -3; k <= 2; k++) chords.AddRange(Gen.Roads.ChordsOf(1, band, k));
            int ends = 0, offTrack = 0, sameLine = 0, steep = 0, tightN = 0, crossing = 0, feet = 0, level = 0; double worstOff = 0, worstTurn = 0, worstH = 0, minR = double.MaxValue, worstGap = 0, hang = 0, bury = 0;
            string first = null, hangAt = "";
            var regions = new Dictionary<(int, int), RegionData>();
            RegionData Region(double x, double z) { var rc = RegionCoord.Containing(x, z); if (!regions.TryGetValue((rc.X, rc.Z), out var d)) regions[(rc.X, rc.Z)] = d = Gen.Generate(rc, 0); return d; }
            foreach (var c in chords)
            {
                int n = c.X.Length - 1;
                var arcs = ArcOf(c);
                // the lines at its two ends: the nearest DOUBLE-track rail centreline to each
                InfiniteRoads.Line[] joined = new InfiniteRoads.Line[2];
                for (int end = 0; end < 2; end++)
                {
                    int i = end == 0 ? 0 : n, j = end == 0 ? 1 : n - 1;
                    double ex = c.X[i], ez = c.Z[i];
                    InfiniteRoads.Line best = null; double bd = double.MaxValue, bf = 0; float bh = 0;
                    foreach (var o in Gen.Roads.LinesIn(ex - 20, ez - 20, ex + 20, ez + 20))
                    {
                        if (o.Kind != RoadKind.Rail || o.SingleTrack) continue;
                        var (d, f, h) = Nearest(o, ex, ez);
                        if (d < bd) { bd = d; best = o; bf = f; bh = h; }
                    }
                    ends++;
                    if (best == null) { offTrack++; first ??= $"a chord end at ({ex:0}, {ez:0}) is on no line"; continue; }
                    joined[end] = best;
                    worstOff = Math.Max(worstOff, Math.Abs(bd - InfiniteRoads.RailTrackOffset));
                    int a = Math.Clamp((int)Math.Floor(bf), 0, best.X.Length - 2);
                    double lx = best.X[a + 1] - best.X[a], lz = best.Z[a + 1] - best.Z[a], cx = c.X[j] - c.X[i], cz = c.Z[j] - c.Z[i];
                    double turn = Math.Acos(Math.Abs(lx * cx + lz * cz) / Math.Sqrt((lx * lx + lz * lz) * (cx * cx + cz * cz))) * 180 / Math.PI;
                    worstTurn = Math.Max(worstTurn, turn);
                    worstH = Math.Max(worstH, Math.Abs(c.H[i] - bh));
                    // (at least half the sink UNDER the line's own: two ballast tops in one plane flicker)
                    if (bh - c.H[i] < InfiniteRoads.ChordSink * 0.5f) { level++; first ??= $"a chord end at ({ex:0}, {ez:0}) is {c.H[i] - bh:+0.000;-0.000} m off the line's height: its ballast is not under the other's"; }
                    if (Math.Abs(bd - InfiniteRoads.RailTrackOffset) > 0.05 || turn > 1.0 || Math.Abs(c.H[i] - bh) > InfiniteRoads.ChordSink + 0.01f) { offTrack++; first ??= $"a chord end at ({ex:0}, {ez:0}) is {bd:0.00} m off the line, {turn:0.00} deg off its heading, {c.H[i] - bh:+0.000;-0.000} m off its height"; }
                }
                if (joined[0] != null && joined[0] == joined[1]) { sameLine++; first ??= "a chord joins a line to itself"; }
                // the curve and the profile
                for (int i = 1; i < n; i++)
                {
                    double ux = c.X[i] - c.X[i - 1], uz = c.Z[i] - c.Z[i - 1], vx = c.X[i + 1] - c.X[i], vz = c.Z[i + 1] - c.Z[i];
                    double lu = Math.Sqrt(ux * ux + uz * uz), lv = Math.Sqrt(vx * vx + vz * vz);
                    double t = Math.Acos(Math.Clamp((ux * vx + uz * vz) / (lu * lv), -1.0, 1.0));
                    if (t > 1e-9) { double r = 0.5 * (lu + lv) / t; minR = Math.Min(minR, r); if (r < InfiniteRoads.ChordMinRadius) tightN++; }
                }
                for (int i = 0; i < n; i++) if (Math.Abs(c.H[i + 1] - c.H[i]) / (arcs[i + 1] - arcs[i]) > InfiniteRoads.RailGrade * 1.0001) steep++;
                // its track
                var u = Gen.Roads.TrackOf(c).Where(p => p.Kind == 0).ToList();
                for (int i = 0; i + 1 < u.Count; i++) worstGap = Math.Max(worstGap, Gap(u[i], u[i + 1]));
                // nothing crossed between its ends
                foreach (var o in Gen.Roads.LinesIn(c.MinX, c.MinZ, c.MaxX, c.MaxZ))
                {
                    if (o == c || o == joined[0] || o == joined[1] || o.Kind == RoadKind.Main || o.Kind == RoadKind.Small || o.Kind == RoadKind.Trail) continue;
                    for (int i = 0; i < n; i++)
                        for (int q = 0; q + 1 < o.X.Length; q++)
                            if (SegCross(c.X[i], c.Z[i], c.X[i + 1], c.Z[i + 1], o.X[q], o.Z[q], o.X[q + 1], o.Z[q + 1])) { crossing++; first ??= $"a chord crosses a {o.Kind} at ({c.X[i]:0}, {c.Z[i]:0})"; }
                }
                // its ballast on the drawn ground, away from its turnouts and decks
                foreach (var p in u)
                {
                    var (_, f, _) = Nearest(c, p.X, p.Z);
                    int a = Math.Clamp((int)Math.Floor(f), 0, n - 1);
                    double al = arcs[a] + (arcs[a + 1] - arcs[a]) * (f - a);
                    if (Covered(c.DeckCover?[0], f - 1) || Covered(c.DeckCover?[0], f + 1)) continue;
                    double lx = p.DZ, lz = -p.DX, ll = Math.Sqrt(lx * lx + lz * lz); lx /= ll; lz /= ll;
                    foreach (double o in new[] { -InfiniteRoads.RailHalfWidth, 0, InfiniteRoads.RailHalfWidth })
                    {
                        double fx = p.X + lx * o, fz = p.Z + lz * o;
                        var d = Region(fx, fz);
                        float mesh = InfiniteTerrain.MeshHeightAt(d, (float)(fx - d.Coord.MinX), (float)(fz - d.Coord.MinZ));
                        float bed = p.Y - InfiniteRoads.RailSet;
                        feet++; bury = Math.Max(bury, mesh - bed);
                        if (bed - mesh > hang) { hang = bed - mesh; hangAt = $" at ({fx:0.0}, {fz:0.0}), {al:0} m along a chord of {arcs[n]:0} m"; }
                    }
                }
            }
            TestContext.WriteLine($"{chords.Count} chords: {ends} ends, {offTrack} off a track (worst {worstOff * 1000:0.0} mm off the offset, {worstTurn:0.000} deg off the heading, {worstH * 1000:0.0} mm off the height), {sameLine} joining a line to itself; " +
                                  $"tightest {minR:0} m ({tightN} points under {InfiniteRoads.ChordMinRadius}), {steep} segments over the grade; joints {worstGap * 1000:0.00} mm; {crossing} crossings; {feet} ballast points hang {hang * 1000:0.0} mm{hangAt}, buried {bury * 1000:0.0} mm" +
                                  $"{(first == null ? "" : $"; first: {first}")}");
            TestContext.WriteLine("not laid: " + string.Join(", ", Gen.Roads.ChordMisses.Values.GroupBy(v => v.Split(' ')[0] + " " + (v.Split(' ').Length > 1 ? v.Split(' ')[1] : "")).Select(g => $"{g.Key} x{g.Count()}")));
            Assert.That(chords.Count, Is.GreaterThanOrEqualTo(4), "too few chords to mean anything");
            Assert.That(offTrack + sameLine + level, Is.Zero, first);
            Assert.That(tightN + steep, Is.Zero, "a chord too tight or too steep");
            Assert.That(worstGap, Is.LessThan(0.001), "a chord's track joint stands open");
            Assert.That(crossing, Is.Zero, first);
            Assert.That(feet, Is.GreaterThan(500));
            Assert.That(hang, Is.LessThan(-InfiniteRoads.RailBallastFoot - InfiniteRoads.RailSet - 0.01), "daylight under a chord's ballast");
            Assert.That(bury, Is.LessThan(0.06), "the ground buries a chord's ballast");
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
        /// <summary>NO MOUNDS (strawberry 2026-10-11: "prevent railways making super-mounds beneath them"): wherever the
        /// track is not on a viaduct, the fill under it (its bed over the natural ground at the centre and both OUTER ballast
        /// feet, the least of the three, as the stretches are marked) is at most 5 m, its 1:2 sides 10 m out each side --
        /// RailRaise and the most a stretch shorter than RaiseMinLength (left an embankment) adds. NOT a multiple of
        /// RailRaise: a bar that moves with the constant cannot catch it raised back. It was 10 m: a mound 40 m across.</summary>
        [Test]
        public void RailEmbankmentsStayLow()
        {
            double total = 0, banked = 0, worst = 0; string where = "";
            foreach (var e in Window())
            {
                var onDeck = new bool[e.X.Length];
                if (e.Raised != null) foreach (var st in e.Raised) for (int i = Math.Max(0, st.I0); i <= Math.Min(e.X.Length - 1, st.I1); i++) onDeck[i] = true;
                for (int i = 1; i + 1 < e.X.Length; i++)
                {
                    double sl = Math.Sqrt((e.X[i + 1] - e.X[i]) * (e.X[i + 1] - e.X[i]) + (e.Z[i + 1] - e.Z[i]) * (e.Z[i + 1] - e.Z[i]));
                    total += sl;
                    if (onDeck[i] || e.TunnelSpans != null && e.TunnelSpans.Any(t => i >= t.F0 - 1 && i <= t.F1 + 1)) continue;
                    double tx = e.X[i + 1] - e.X[i - 1], tz = e.Z[i + 1] - e.Z[i - 1], tl = Math.Sqrt(tx * tx + tz * tz);
                    double fill = double.MaxValue;
                    foreach (int s2 in new[] { -1, 0, 1 })
                        fill = Math.Min(fill, e.H[i] - InfiniteRoads.Bed - Gen.NaturalHeight(e.X[i] - tz / tl * s2 * InfiniteRoads.RailOuterHalf, e.Z[i] + tx / tl * s2 * InfiniteRoads.RailOuterHalf));
                    if (fill > 2) banked += sl;
                    if (fill > worst) { worst = fill; where = $" at ({e.X[i]:0}, {e.Z[i]:0})"; }
                }
            }
            TestContext.WriteLine($"{total / 1000:0.0} km of track: {banked / 1000:0.00} km on an embankment over 2 m, the tallest {worst:0.00} m{where} (viaducts from {InfiniteRoads.RailRaise} m)");
            Assert.That(total, Is.GreaterThan(20000), "too little track to mean anything");
            Assert.That(worst, Is.LessThanOrEqualTo(5.0), $"an embankment {worst:0.0} m tall{where}");
        }

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
            // (the outer shoulder of a train on either track: double track, 2026-10-11)
            float side = InfiniteRoads.BoreTopOf(RoadKind.Rail, InfiniteRoads.RailTrackOffset + InfiniteRoads.RailLoadingHalf), mid = InfiniteRoads.BoreTopOf(RoadKind.Rail, 0f);
            Assert.That(side - InfiniteRoads.RailHead, Is.GreaterThanOrEqualTo(InfiniteRoads.RailLoadingHeight), "a train's shoulder hits the bore");
            Assert.That(InfiniteRoads.BoreReachOf(RoadKind.Rail), Is.GreaterThan(InfiniteRoads.RailOuterHalf), "the outer ballast runs into the wall");
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
