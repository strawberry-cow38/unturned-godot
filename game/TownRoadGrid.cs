using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>A town's street network, generated as RoadField splines with real junction nodes (strawberry
    /// 2026-10-11: "work on making a proper road grid system with ur new splines", following "apply the same
    /// logic tiny's branches/junctions use for making a town road grid").
    ///
    /// ⭐ WHAT IS TAKEN FROM InfiniteRoads, and why each piece earns its place:
    ///   - A DETERMINISTIC HASHED SEED. Never a Random instance, and never GetHashCode -- .NET salts that per
    ///     PROCESS, so a town seeded off it would come back different on the next launch. Everything that will
    ///     ever sit on these streets (buildings, props, a saved game, a render you are comparing against
    ///     yesterday's) needs them reproduced exactly. ⚠ This is not hypothetical here: the editor ROADS
    ///     harness is NOT reproducible between launches, which cost a before/after A/B earlier today.
    ///   - TERRAIN GATES THAT TRUNCATE INSTEAD OF FAILING. A street that would run into the sea, climb past
    ///     the grade its class allows, or put a crossroads on a cliff face simply does not get built there --
    ///     the rest of the grid still does. A branch does exactly this, stopping "where the land says stop".
    ///   - ENDS PINNED TO THE SHARED NODE. Two streets meeting at a junction take their endpoint from the one
    ///     node, so they cannot disagree about where the junction is; a branch pins its first point to its
    ///     parent's surface height for the same reason.
    ///   - SMOOTHED PROFILE WITH PINNED ENDS. A road is graded, not draped over every bump -- but smoothing
    ///     that moved an end would unstitch the junction, so both ends are pinned back after the smooth.
    ///
    /// ⚠⚠ ONE ROAD PER SPAN, NOT ONE PER STREET. RoadField binds junctions by road END, so a street crossing
    /// three others as a single road could only ever be joined at its two tips -- the interior crossings would
    /// be two surfaces overlapping, with nothing a router could read. Splitting at every node is what makes an
    /// interior crossing a real 4-way node. The cost is more roads and a tangent that is independent either
    /// side of a junction, which is correct anyway: a crossroads IS a kink, not a smooth curve.</summary>
    public sealed partial class TownRoadGrid
    {
        public int Seed = 1;
        public Vector3 Centre;
        /// <summary>Half the town's span, metres.</summary>
        public float Extent = 240f;
        /// <summary>Nominal block size. The node spacing before jitter.</summary>
        /// <summary>Nominal block size. ⚠ IT HAS TO CLEAR THE JUNCTIONS. tiny's node geometry cuts each arm
        /// back past its fillets, which on an 18 m street costs about 18 m per end -- so 64 m blocks came out
        /// as 19 m of street between two junctions, a town that is mostly intersection. 110 m is a town block
        /// that still looks like one once its corners are rounded.</summary>
        public float Spacing = 110f;
        /// <summary>How far a LINE may wander from its lattice position, as a fraction of Spacing.
        ///
        /// ⚠⚠ PER LINE, NOT PER NODE, and that is the whole difference between an irregular grid and a wiggly
        /// one (strawberry 2026-10-11: "why are they so wiggly"). Jittering each node independently moves it
        /// off BOTH the lines it belongs to, so a street kinks at every junction it passes through -- the
        /// streets were never straight, they were polylines through 7 randomly nudged points. Offsetting the
        /// whole LINE instead keeps u a function of i alone and v of j alone, so every street is dead straight
        /// and the blocks are different sizes, which is what an irregular town actually looks like.</summary>
        public float Jitter = 0.16f;
        /// <summary>The grid's bearing, radians. Towns are rarely axis-aligned, and an axis-aligned one here
        /// would also hide any frame bug by lining up with the world axes.</summary>
        public float Bearing;
        /// <summary>Every Nth line (counting from the centre) is a main street: wider material, wider pavement.</summary>
        public int MainEvery = 3;
        /// <summary>⚠⚠ READ THE WIDTH TABLE BEFORE PICKING A MATERIAL. "Road" (3) sounds like the town-street
        /// one and is 18.4 m of HALF width -- a 37 m carriageway, the same as the Racetrack -- which is where
        /// a 40 m blob of asphalt at every junction came from. Measured off the live field, half-widths are
        /// 0:9.2 (Highway_0) 1:6.9 (Highway_1) 2:18.4 (Racetrack) 3:18.4 (Road) 4:2.6 (Tracks) 5:4.6 (Trail).
        /// A main street is Highway_0 at 18 m across and a side street Highway_1 at 14 m, which keeps an
        /// intersection the size of an intersection.</summary>
        /// <summary>⚠ BOTH ARE Highway_0 (0), THE YELLOW-LINE ROAD (strawberry: "should be the yellow line
        /// highway"). Checked against the textures rather than the names: road_0 is dashes plus a solid yellow
        /// centre line, road_1 is dashes only (a one-way carriageway), road_3 "Road" is 18.4 m of HALF width --
        /// the same as the Racetrack -- which is where a 40 m blob of asphalt at every junction came from.
        /// Half-widths measured off the live field: 0:9.2 1:6.9 2:18.4 3:18.4 4:2.6 5:4.6.</summary>
        /// <summary>⚠ A TOWN HAS TWO ROAD CLASSES. Main streets are Highway_0 (9.2 m half, the four-lane
        /// yellow-line road) and side streets are White_0 (4.6 m half, the small white-dotted two-lane one),
        /// measured off the live field: 0:9.2 1:6.9 2:18.4 3:18.4 4:2.6 5:4.6 6:4.6. Setting both to the same
        /// value gives a town of one class, which is what IsMain tests for.</summary>
        public int MainMaterial = 0;
        public int StreetMaterial = 6;
        /// <summary>Steepest a span may be before it is left unbuilt, rise over run.</summary>
        public float MaxGrade = 0.16f;
        /// <summary>Steepest ground a NODE may sit on. A junction is flat ground by definition -- four roads
        /// have to meet there -- so this is tighter than the span grade.</summary>
        public float MaxNodeSlope = 0.22f;
        /// <summary>Clearance above sea level a node needs. Dry land at exactly sea level is still beach.</summary>
        public float SeaMargin = 2.0f;
        public float SeaLevel = Terrain.SeaLevelY;
        /// <summary>Spline point spacing along a span.</summary>
        public float PointStep = 12f;
        /// <summary>Smoothing passes over a span's height profile (ends pinned).</summary>
        public int Grade = 2;
        /// <summary>Whether the drawn surface re-samples the ground under it.
        ///
        /// ⚠ READ THIS BEFORE TRUSTING THE GRADED PROFILE. With FollowTerrain (the default) RoadField snaps
        /// every surface sample to the heightmap, so the graded heights below shape the SPLINE's control
        /// points and tangents, and the asphalt still rides every bump -- which is correct on terrain nobody
        /// has carved, and is why the node and span gates refuse ground the grading could not rescue. Set it
        /// false and the street holds its own profile, which looks right only once something carves the
        /// terrain underneath to match (what InfiniteRoads does for its raised and cut stretches).</summary>
        public bool FollowTerrain = true;

        /// <summary>What came out. ⭐ Checksum folds every emitted point: determinism is then a MEASUREMENT,
        /// not an intention -- a test can assert two runs agree instead of hoping they do.</summary>
        public struct Report
        {
            public int Nodes, NodesRejected, Roads, SpansRejected;
            public int Junctions, Crossroads, Tees, Corners, DeadEnds;
            /// <summary>Nodes paved with one of tiny's junction patches, and spans a junction ate entirely.</summary>
            public int Patched, CutAway;
            /// <summary>Corners rounded into one curving road instead of slabbed as a two-arm junction.</summary>
            public int Bends;
            /// <summary>Junctions the 45-degree diagonal made where it crosses the grid.</summary>
            public int DiagonalCrossings;
            /// <summary>A varied town: blocks joined, spans pruned as loose, what the seed chose, and the rules checked.</summary>
            public int Superblocks, Pruned;
            public string Layout, Rules;
            public float Metres;
            public ulong Checksum;
            public override string ToString() =>
                $"{Roads} roads ({Metres:0} m), {Junctions} junctions = {Crossroads} crossroads / {Tees} tees / "
              + $"{Corners} corners / {DeadEnds} dead ends; {Nodes} nodes ({NodesRejected} rejected), "
              + $"{SpansRejected} spans rejected ({CutAway} eaten by their junctions); {Patched} nodes patched; {Bends} corners rounded; {DiagonalCrossings} diagonal crossings; checksum {Checksum:X16}";
        }

        /// <summary>splitmix64. ⚠ NOT GetHashCode (salted per process) and NOT System.Random (no cross-version
        /// guarantee): a town must come back bit-identical, and the only way to be sure of that is arithmetic
        /// written down here.</summary>
        static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        ulong NodeHash(int i, int j) => Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)i << 20) ^ (uint)j ^ 0x5151UL));
        /// <summary>One offset per LINE, so every node on that line shares it and the line stays straight.</summary>
        ulong LineHash(int axis, int line) => Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)axis << 40) ^ (uint)line ^ 0x7A3BUL));
        static float Unit(ulong h, int shift) => ((h >> shift) & 0xFFFF) / 65535f;    // 0..1

        // The grid's own frame. Local (u, v) -> world, with the town's bearing applied.
        Vector3 ToWorld(float u, float v, float y)
        {
            float c = Mathf.Cos(Bearing), s = Mathf.Sin(Bearing);
            return new Vector3(Centre.X + u * c - v * s, y, Centre.Z + u * s + v * c);
        }

        struct Node
        {
            public Vector3 P; public bool Ok; public int Junction;
            /// <summary>A crossing the diagonal made, not a grid node -- and the half width of the grid road
            /// it sits on, which is what a diagonal arm has to clear.</summary>
            public bool Extra; public float CrossHalf;
        }

        /// <summary>A planned street before it is cut: the two nodes it joins, its full polyline, and how much
        /// each end gives up to the junction there.</summary>
        struct Span
        {
            public int AI, AJ, BI, BJ, Axis; public bool Main;
            /// <summary>A main with a median down its middle: wider, and its own material (see BoulevardMaterial).</summary>
            public bool Boulevard;
            public List<Vector3> Pts; public float Len, TrimA, TrimB;
            /// <summary>The axis each END runs along. Equal to Axis for a straight span; a span fused round a
            /// corner arrives along one axis and leaves along the other, and a junction at either end has to
            /// see the right one -- pairing arms by a single Axis would call a bend's far end collinear with
            /// whatever it happened to share a number with.</summary>
            public int AxisA, AxisB;
            public bool Bent; public int BendId;
            /// <summary>This end is half of a main running THROUGH its node: nobody stops it, so it gets no
            /// crossing and no stop line. Decided by the node pass, read by the crossings.</summary>
            public bool ThroughA, ThroughB;
            /// <summary>The direction of the line this end YIELDS to at its node, or zero where it is that line
            /// (or there is none). Its crossing runs parallel to it.</summary>
            public Vector3 AlongA, AlongB;
        }
        static int AxisAt(Span sp, bool atEnd) => atEnd ? sp.AxisB : sp.AxisA;

        /// <summary>UG_TOWNCUTMAIN=1 puts the main back to stopping at every junction -- the control, so the
        /// through-main change can be shown rather than asserted.</summary>
        public static bool CutMain = System.Environment.GetEnvironmentVariable("UG_TOWNCUTMAIN") == "1";

        /// <summary>Cut the through road's corridor out of a node's patch, leaving only the pieces either
        /// side of it -- the corner fillets that join each side street to the main's edge.
        ///
        /// ⭐⭐ THIS IS WHAT THE JUNCTION SHOULD HAVE BEEN ALL ALONG (strawberry 2026-10-12: "the idea is
        /// that the spline runs through - no patch, and the side roads join the sides of the spline??").
        /// tiny's node geometry lays ONE SLAB over the whole intersection and cuts every arm back under it,
        /// which means the slab -- plain tarmac, by design, so four lane textures do not collide in the
        /// middle -- is what you see. Running the main through underneath changed nothing visible: the slab
        /// still covered it and the box went BLANK, because the main's lines were buried and the painted
        /// ones ride on the slab.
        /// ⭐ With the corridor cut out, the main's OWN RIBBON is the surface through the junction, so its
        /// lines continue in the road's own texture. That retires three separate complaints at once -- the
        /// painted lines being a different colour, being misaligned, and being scaled wrong -- because
        /// there is no longer a second thing drawing them to disagree with the first.
        /// ⚠ The patch is in InfiniteRoads' frame (z negated); so are cx/cz and the normal.</summary>
        /// <summary>Every slab ClipCorridor cut, with the corridor it was cut along (InfiniteRoads' frame).</summary>
        readonly List<(SDG.Unturned.RoadPatch patch, double cx, double cz, double nx, double nz, double half)> _clips = new();

        /// <summary>⚠⚠ A CUT SLAB MEETS ITS MAIN AT THE MAIN'S HEIGHT (strawberry 2026-10-13, "follow gentle hills and slopes").
        /// The cut leaves the slab's edge along the main wherever tiny's surface had it -- and that surface is the whole
        /// junction's, bending between every arm's height at once, not the main's profile. On flat ground the two are one
        /// height. On a hill the side of a junction a main runs through stood up to half a metre off the main's own edge: a
        /// dark crease the length of the junction, and corner asphalt coming up through it (the fill probe, 37 cm). Each
        /// point of the cut edge now takes the main's surface beside it, and the slab tilts to meet it.</summary>
        void SnapClippedEdges(RoadField roads)
        {
            ClipSnapped = 0; ClipSnapMost = 0f;
            if (_clips.Count == 0 || FollowTerrain) return;
            _roadSamples.Clear();
            var fit = FitShifts(roads, roads.RoadSurfaceOffsetOfMaterial(MainMaterial));
            foreach (var (pp, cx, cz, nx, nz, half) in _clips)
            {
                if (pp?.X == null || !Patches.Contains(pp)) continue;
                float shift = fit.TryGetValue(pp, out float f) ? f : roads.RoadSurfaceOffsetOfMaterial(MainMaterial);
                var along = new Vector3((float)nz, 0f, (float)nx);                 // the main's way, in world (the frame negates z)
                int onCut = 0, missed = 0;
                for (int i = 0; i < pp.X.Length; i++)
                {
                    double lat = (pp.X[i] - cx) * nx + (pp.Z[i] - cz) * nz;
                    float e = (float)(Math.Abs(lat) - half);                        // how far out from the main's edge
                    if (e > ClipBlend || e < -0.05f) continue;
                    if (e < 0.02f) onCut++;
                    var w = new Vector3((float)pp.X[i], 0f, -(float)pp.Z[i]);
                    // the main: a street running the corridor's way, as far off as this point is
                    float bestMiss = 0.3f, top = float.NaN;
                    foreach (int r in Built)
                    {
                        float y = RoadNear(roads, r, w, out float at, out float off);
                        if (Mathf.Abs(off - (float)Math.Abs(lat)) >= bestMiss) continue;
                        if (!roads.EvaluateAlong(r, at, out _, out var tn, snapTerrain: false)) continue;
                        var th = new Vector3(tn.X, 0f, tn.Z);
                        if (th.LengthSquared() < 1e-8f || Mathf.Abs(th.Normalized().Dot(along)) < 0.95f) continue;
                        bestMiss = Mathf.Abs(off - (float)Math.Abs(lat)); top = y + roads.RoadSurfaceOffset(r);
                    }
                    if (float.IsNaN(top)) { if (e < 0.02f) missed++; continue; }
                    // on the cut, the main's surface; out across the slab, back to the slab's own over ClipBlend
                    float want = top - shift - PatchLift;
                    float keep = Mathf.SmoothStep(0f, ClipBlend, Mathf.Max(0f, e));
                    float y2 = Mathf.Lerp(want, pp.Y[i], keep);
                    ClipSnapMost = Mathf.Max(ClipSnapMost, Mathf.Abs(y2 - pp.Y[i]));
                    pp.Y[i] = y2; ClipSnapped++;
                }
                if (ProbeEverything)
                    Log.Print($"[towngrid] cut slab at ({cx:0.0}, {-cz:0.0}): {onCut} points on the cut, {missed} with no main beside them, "
                            + $"half {half:0.00}, {pp.X.Length} points");
            }
            Log.Print($"[towngrid] cut slabs: {ClipSnapped} points onto the main beside them (blended over {ClipBlend:0.0} m), moved by up to {ClipSnapMost * 100f:0.0} cm");
        }
        /// <summary>How far out from the main's edge a cut slab is bent from the main's height back to its own: the cut
        /// edge alone was not enough, the slab's next ring in (2 m) kept the junction's height and the step became a ramp
        /// half a metre high over a few centimetres.</summary>
        public float ClipBlend = 4f;
        public int ClipSnapped; public float ClipSnapMost;

        static SDG.Unturned.RoadPatch ClipCorridor(SDG.Unturned.RoadPatch p, double cx, double cz,
                                                   double nx, double nz, double half)
        {
            if (p?.Tris == null || p.X == null || p.Tris.Length < 3) return p;
            var X = new List<double>(p.X); var Z = new List<double>(p.Z);
            var Y = new List<float>(p.Y);
            var L = p.Lift != null ? new List<float>(p.Lift) : null;
            var U = p.U != null ? new List<float>(p.U) : null;
            var Vv = p.V != null ? new List<float>(p.V) : null;
            double Lat(int i) => (X[i] - cx) * nx + (Z[i] - cz) * nz;

            var cuts = new Dictionary<(int, int, bool), int>();
            int Mid(int a, int b, double plane, bool hi)
            {
                var key = a < b ? (a, b, hi) : (b, a, hi);
                if (cuts.TryGetValue(key, out int got)) return got;
                double la = Lat(a), lb = Lat(b);
                double t = Math.Abs(lb - la) < 1e-12 ? 0.5 : (plane - la) / (lb - la);
                t = Math.Clamp(t, 0.0, 1.0);
                X.Add(X[a] + (X[b] - X[a]) * t); Z.Add(Z[a] + (Z[b] - Z[a]) * t);
                Y.Add((float)(Y[a] + (Y[b] - Y[a]) * t));
                L?.Add((float)(L[a] + (L[b] - L[a]) * t));
                U?.Add((float)(U[a] + (U[b] - U[a]) * t));
                Vv?.Add((float)(Vv[a] + (Vv[b] - Vv[a]) * t));
                int id = X.Count - 1; cuts[key] = id; return id;
            }

            var tris = new List<int>();
            var poly = new List<int>(4); var clipped = new List<int>(5);
            void ClipTri(int a, int b, int c, double plane, int sign, bool hi)
            {
                poly.Clear(); poly.Add(a); poly.Add(b); poly.Add(c);
                clipped.Clear();
                for (int i = 0; i < poly.Count; i++)
                {
                    int cur = poly[i], nxt = poly[(i + 1) % poly.Count];
                    double dc = sign * (Lat(cur) - plane), dn = sign * (Lat(nxt) - plane);
                    if (dc >= 0) clipped.Add(cur);
                    if ((dc > 1e-9 && dn < -1e-9) || (dc < -1e-9 && dn > 1e-9)) clipped.Add(Mid(cur, nxt, plane, hi));
                }
                for (int i = 2; i < clipped.Count; i++)
                {
                    int v0 = clipped[0], v1 = clipped[i - 1], v2 = clipped[i];
                    // ⚠ DROP THE SLIVERS A CLIP ALWAYS MAKES. A triangle grazing the plane produces a
                    // zero-area offcut whose normal is noise, and those are what shade as dark specks.
                    double ar = Math.Abs((X[v1] - X[v0]) * (Z[v2] - Z[v0]) - (X[v2] - X[v0]) * (Z[v1] - Z[v0]));
                    if (ar < 1e-4) continue;
                    tris.Add(v0); tris.Add(v1); tris.Add(v2);
                }
            }
            for (int t = 0; t + 2 < p.Tris.Length; t += 3)
            {
                int a = p.Tris[t], b = p.Tris[t + 1], c = p.Tris[t + 2];
                if (a >= p.X.Length || b >= p.X.Length || c >= p.X.Length) continue;
                ClipTri(a, b, c, half, +1, true);
                ClipTri(a, b, c, -half, -1, false);
            }
            if (tris.Count < 3) return null;        // the corridor swallowed the whole patch

            // ---- compact to the vertices the surviving triangles actually use
            var map = new Dictionary<int, int>();
            var fx = new List<double>(); var fz = new List<double>();
            var fy = new List<float>(); var fl = new List<float>();
            var fu = new List<float>(); var fv = new List<float>();
            int Keep(int i)
            {
                if (map.TryGetValue(i, out int got)) return got;
                fx.Add(X[i]); fz.Add(Z[i]); fy.Add(Y[i]);
                fl.Add(L != null ? L[i] : 0f); fu.Add(U != null ? U[i] : 0f); fv.Add(Vv != null ? Vv[i] : 0f);
                int id = fx.Count - 1; map[i] = id; return id;
            }
            for (int i = 0; i < tris.Count; i++) tris[i] = Keep(tris[i]);

            // ---- the bevel runs round the OUTSIDE only. An edge lying on a cut plane faces the main's
            // ribbon, which is the same asphalt at the same height: bevelling it would hang a 9 cm cliff
            // down the middle of a junction that has no drop in it.
            var seen = new Dictionary<(int, int), (int count, int opp)>();
            for (int t = 0; t + 2 < tris.Count; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[t + e], b = tris[t + (e + 1) % 3], o = tris[t + (e + 2) % 3];
                    var k = a < b ? (a, b) : (b, a);
                    seen[k] = seen.TryGetValue(k, out var had) ? (had.count + 1, had.opp) : (1, o);
                }
            var skirts = new List<int[]>(); var snx = new List<float[]>(); var snz = new List<float[]>();
            foreach (var kv in seen)
            {
                if (kv.Value.count != 1) continue;                       // interior edge
                var (a, b) = kv.Key;
                double la = (fx[a] - cx) * nx + (fz[a] - cz) * nz, lb = (fx[b] - cx) * nx + (fz[b] - cz) * nz;
                if (Math.Abs(Math.Abs(la) - half) < 1e-3 && Math.Abs(Math.Abs(lb) - half) < 1e-3) continue;  // the cut
                double ex = fx[b] - fx[a], ez = fz[b] - fz[a];
                double len = Math.Sqrt(ex * ex + ez * ez);
                if (len < 1e-6) continue;
                double ox = -ez / len, oz = ex / len;                    // perpendicular...
                int o2 = kv.Value.opp;                                   // ...pointing AWAY from the triangle
                if ((fx[o2] - fx[a]) * ox + (fz[o2] - fz[a]) * oz > 0) { ox = -ox; oz = -oz; }
                skirts.Add(new[] { a, b });
                snx.Add(new[] { (float)ox, (float)ox }); snz.Add(new[] { (float)oz, (float)oz });
            }

            return new SDG.Unturned.RoadPatch
            {
                Kind = p.Kind, Ramp = p.Ramp, AnchorX = p.AnchorX, AnchorZ = p.AnchorZ,
                X = fx.ToArray(), Z = fz.ToArray(), Y = fy.ToArray(),
                Lift = fl.ToArray(), U = fu.ToArray(), V = fv.ToArray(),
                Tris = tris.ToArray(),
                Skirts = skirts.ToArray(), SkirtNX = snx.ToArray(), SkirtNZ = snz.ToArray(),
                // ⚠ NO INTERIOR VERTICES. Interior marks the ones free to ride up onto the ground beneath;
                // the indices it referred to are gone, and a fillet is small enough that none should.
                Interior = int.MaxValue,
            };
        }

        /// <summary>Which pair of arms, if any, RUNS THROUGH this node uncut -- a MAIN line, only.
        ///
        /// ⚠ EXACTLY ONE PAIR, AND THEY MUST BE COLLINEAR. Two arms on the same axis are two halves of one
        /// line, so leaving both uncut makes one continuous carriageway that meets itself at the node. Doing
        /// it to the crossing pair as well would lay two coplanar ribbons at right angles over each other --
        /// a z-fight, not a fix.
        /// ⚠⚠ ONLY A MAIN, NEVER A STREET -- master's spec, not a preference (2026-10-12: "the bigger roads
        /// should be the 'main' roads, their lines continue through the junction without crossings on their
        /// parts"). A through ribbon carries its OWN baked lines across the node, which is exactly right for
        /// a main and exactly wrong for a street, because a street HAS crossings: I generalised this to any
        /// collinear pair to push the backing number from 58% to 89%, and every side street's centre dashes
        /// then ran straight through its own zebras. The number went up and the junctions got worse.
        /// ⚠⚠ AND ONLY WHERE NOTHING ELSE AT THE NODE IS A MAIN (strawberry 2026-10-12: "where u have 2 main
        /// roads crossing, the splines just continue through, should have the 4 crossings like it did before.
        /// the spline continuing rule is just for small roads joining"). Where two mains crossed I made one
        /// of them "the priority road" and ran it through -- so a crossing of two four-lane roads lost all
        /// four zebras and had one road's lines painted straight over the other's. Two mains meeting is a
        /// full junction: every arm cut, the slab, a crossing on each. A main STEM at a tee counts too: it is
        /// a main joining, not a small road.</summary>
        static bool ThroughMain(List<Span> plan, List<(int, bool)> here, out int a, out int b)
        {
            a = b = -1;
            for (int axis = 0; axis < 3; axis++)          // 2 = the diagonal
            {
                int p = -1, q = -1, count = 0;
                bool allMain = true;
                for (int i = 0; i < here.Count; i++)
                {
                    if (AxisAt(plan[here[i].Item1], here[i].Item2) != axis) continue;
                    count++;
                    if (p < 0) p = i; else if (q < 0) q = i;
                    allMain &= plan[here[i].Item1].Main;
                }
                if (count != 2 || q < 0 || !allMain) continue;   // a stub, a fan, or a street
                for (int i = 0; i < here.Count; i++)
                    if (i != p && i != q && plan[here[i].Item1].Main) return false;   // a main meets a main
                a = p; b = q;
                return true;
            }
            return false;
        }

        /// <summary>The line every other arm at this node YIELDS to, and so the one their crossings run parallel
        /// to (strawberry 2026-10-12: "the 45° joins should have the crosswalk parallel to the main road's
        /// direction"). A main over a street; between equals the GRID over the diagonal, since the avenue is
        /// what joins the grid; and only a collinear pair is a line at all, so a tee's through road beats its
        /// stem. -1 where no two arms make a line.
        /// ⭐ AT A RIGHT ANGLE THIS CHANGES NOTHING: a crossing parallel to the road you are crossing into is
        /// a square one. It only moves the crossings at 45 degrees, which is the point.</summary>
        static int PriorityAxis(List<Span> plan, List<(int, bool)> here)
        {
            int best = -1, bestScore = int.MinValue;
            for (int axis = 0; axis < 3; axis++)
            {
                int count = 0; bool allMain = true;
                foreach (var (si, atEnd) in here)
                    if (AxisAt(plan[si], atEnd) == axis) { count++; allMain &= plan[si].Main; }
                if (count != 2) continue;
                int score = (allMain ? 4 : 0) - axis;    // main first, then the lower axis: the grid before the diagonal
                if (score > bestScore) { bestScore = score; best = axis; }
            }
            return best;
        }

        /// <summary>An end's direction leaving its node, flat, from the planned polyline.</summary>
        static Vector3 PlanDir(Span sp, bool atEnd)
        {
            var p = sp.Pts;
            if (p == null || p.Count < 2) return Vector3.Zero;
            var d = atEnd ? p[p.Count - 2] - p[p.Count - 1] : p[1] - p[0];
            d.Y = 0f;
            return d.LengthSquared() < 1e-8f ? Vector3.Zero : d.Normalized();
        }

        static IEnumerable<(Span sp, int idx)> Enumerate(List<Span> l) { for (int i = 0; i < l.Count; i++) yield return (l[i], i); }
        static void Add<TK, TV>(Dictionary<TK, List<TV>> d, TK k, TV v)
        { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<TV>(); l.Add(v); }

        /// <summary>Whether to use tiny's node geometry (fillet-aware cuts + a patch paving the middle) rather
        /// than a plain kerb-line setback. UG_TOWNNOPATCH=1 turns it off, which is the control: the same town
        /// with overlapping arms, for a before/after at one camera.</summary>
        public bool UseNodePatches = System.Environment.GetEnvironmentVariable("UG_TOWNNOPATCH") != "1";
        /// <summary>What the corridor clip did, so "the fillets are gone" and "the fillets are fine" are
        /// distinguishable without opening a render.</summary>
        public int ClipsTried, ClipsEmptied, ClipTrisBefore, ClipTrisAfter;
        /// <summary>Where the arm walk found the widest bare run, so a camera can be aimed at the thing the
        /// probe actually measured instead of at where I guessed the defect was.</summary>
        public Vector3 LastBareAt = Vector3.Zero;
        public float LastBareRun = 0f;
        /// <summary>The junction patches this grid produced, for the caller to build meshes from.</summary>
        public readonly List<SDG.Unturned.RoadPatch> Patches = new();

        /// <summary>Each patch's bounding box in InfiniteRoads' frame, built on first use after generation.
        ///
        /// ⚠⚠ WHERE A TOWN RENDER'S TEN MINUTES WENT. Timed line by line: the whole town generates in 2.7 s,
        /// and 627 of 633 seconds were two of the probes in this file -- each sample asked EVERY patch for
        /// its distance, and RoadPatch.DistanceTo walks every triangle and edge. Half a million samples times
        /// every triangle of every junction. A point cannot be on, or nearer than d to, a patch whose box it
        /// is further than d outside, so skipping those patches changes no answer.</summary>
        readonly List<(double x0, double x1, double z0, double z1)> _patchBox = new();
        void EnsurePatchBoxes()
        {
            if (_patchBox.Count == Patches.Count) return;
            _patchBox.Clear();
            foreach (var pp in Patches)
            {
                double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
                if (pp?.X != null)
                    for (int i = 0; i < pp.X.Length; i++)
                    { x0 = System.Math.Min(x0, pp.X[i]); x1 = System.Math.Max(x1, pp.X[i]); z0 = System.Math.Min(z0, pp.Z[i]); z1 = System.Math.Max(z1, pp.Z[i]); }
                _patchBox.Add((x0, x1, z0, z1));
            }
        }
        /// <summary>Is (x, zFrame) on any junction patch, to within <paramref name="tol"/>?</summary>
        bool OnAnyPatch(double x, double zf, double tol)
        {
            EnsurePatchBoxes();
            for (int k = 0; k < Patches.Count; k++)
            {
                var b = _patchBox[k];
                if (x < b.x0 - tol || x > b.x1 + tol || zf < b.z0 - tol || zf > b.z1 + tol) continue;
                if (Patches[k].DistanceTo(x, zf) <= tol) return true;
            }
            return false;
        }
        /// <summary>Distance from (x, zFrame) to the nearest junction patch.</summary>
        double NearestPatch(double x, double zf)
        {
            EnsurePatchBoxes();
            double best = double.MaxValue;
            for (int k = 0; k < Patches.Count; k++)
            {
                var b = _patchBox[k];
                double dx = System.Math.Max(0, System.Math.Max(b.x0 - x, x - b.x1)), dz = System.Math.Max(0, System.Math.Max(b.z0 - zf, zf - b.z1));
                if (dx * dx + dz * dz >= best * best) continue;          // its box is already further than the best
                best = System.Math.Min(best, Patches[k].DistanceTo(x, zf));
            }
            return best;
        }
        /// <summary>The node PAINT: each arm's lines carried into the junction, and the stop lines.</summary>
        public readonly List<SDG.Unturned.RoadMark> Marks = new();

        /// <summary>A polyline with `a` metres taken off its start and `b` off its end, cut ON the line rather
        /// than at the nearest existing point -- a 12 m point spacing would otherwise round a 9 m cut to 12 or
        /// to 0, and at a junction that is the difference between meeting the kerb and crossing it.</summary>
        List<Vector3> CutBack(List<Vector3> pts, float a, float b, float step = -1f)
        {
            if (pts == null || pts.Count < 2) return null;
            float total = 0f;
            for (int i = 0; i + 1 < pts.Count; i++) total += Flat(pts[i], pts[i + 1]);
            if (total - a - b < 4f) return null;            // all intersection: this span does not exist
            var cut = new List<Vector3>();
            float want0 = Mathf.Max(0f, a), want1 = total - Mathf.Max(0f, b), run = 0f;
            Vector3 At(Vector3 p, Vector3 q, float f) => new Vector3(Mathf.Lerp(p.X, q.X, f), Mathf.Lerp(p.Y, q.Y, f), Mathf.Lerp(p.Z, q.Z, f));
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float segLen = Flat(pts[i], pts[i + 1]);
                if (segLen < 1e-5f) continue;
                float s0 = run, s1 = run + segLen;
                if (s1 >= want0 && s0 <= want1)
                {
                    float f0 = Mathf.Clamp((want0 - s0) / segLen, 0f, 1f), f1 = Mathf.Clamp((want1 - s0) / segLen, 0f, 1f);
                    if (cut.Count == 0) cut.Add(At(pts[i], pts[i + 1], f0));
                    cut.Add(At(pts[i], pts[i + 1], f1));
                }
                run = s1;
            }
            // ⚠ NO RESPACING HERE, AND THAT IS A MEASUREMENT not an omission. The cut leaves a stub
            // segment, and Catmull-Rom over uneven spacing is a known source of subtle waviness -- so
            // respacing looked like the explanation for "sidewalks seem to wiggle". It was not: with the
            // respacing in and out, the streets' worst bow off their own chord measured 0.0 cm either way.
            // The wiggle was faceting on the CORNER ARCS (11.3 degrees per segment). Code that fixes nothing
            // measurable does not stay.
            // ⚠⚠ DROP THE STUB AT EACH END. Cutting at an arbitrary arc leaves a first segment that can be a
            // few centimetres against a 12 m neighbour, and RoadField tangents its joints with Catmull-Rom:
            // over spacing that uneven the curve OVERSHOOTS and the ribbon folds back on itself, right at the
            // junction mouth. strawberry saw it immediately -- "its like the spline loops back on itself or
            // somethinf" -- which is literally what it is.
            // ⚠ I HAD THIS AND THREW IT AWAY. An earlier respace was removed because the streets' bow off
            // their chord measured 0.0 cm. True, and the wrong instrument: a cusp in the last half metre
            // barely moves a 24-sample chord deviation, so the probe called a road with a fold in its end
            // "straight" and six explanations were then spent elsewhere.
            // ⚠⚠ RESPACE THE WHOLE RUN, not just the stubs. Dropping end segments under 2 m fixed the folds
            // on a single-class town and left them on a mixed one: a 2.5 m first segment against a 12 m
            // neighbour is still 5:1, and Catmull-Rom overshoots on the RATIO, not on an absolute length.
            // Even spacing has no ratio to overshoot on. (This is the third time round on the same bug --
            // written, deleted on a measurement that asked the wrong question, reinstated as a stub trim,
            // and only now in the form that actually holds.)
            if (step == 0f) return cut;      // the caller's spacing is already right (a rounded corner's exact arc)
            return Respace(cut, step > 0f ? step : Mathf.Max(2f, PointStep));
        }
        /// <summary>A diagonal avenue across the grid at 45 degrees -- the 45-degree intersections (strawberry
        /// 2026-10-12: "work on 45° intersections").
        ///
        /// ⭐ HALF A BLOCK OFF EVERY NODE, ON PURPOSE. In grid space the line is v = u + S/2, so it meets each
        /// span it crosses near the MIDDLE and never passes through a node -- a node it hit would become a
        /// six-way. Every crossing is therefore a clean four-way at 45 and 135 degrees, and its two ends are
        /// 45-degree tees on the town's edge: both shapes the junction code has never been asked to make.
        /// ⚠ A CROSSING IS A NODE LIKE ANY OTHER. The span it lands on is split there, the diagonal's pieces
        /// run node to node, and from here on the node pass, the patches, pavements, crossings and signals
        /// see an ordinary junction whose arms happen not to be square -- which is the whole test of them.
        /// Returns the number of crossings made.</summary>
        int AddDiagonal(List<Span> plan, Node[,] grid, int w, RoadField roads, Func<float, float, float> heightAt)
        {
            DiagonalNodes.Clear();
            var P0 = ToWorld(0f, Spacing * 0.5f, 0f);
            var P1 = ToWorld(Spacing, Spacing * 1.5f, 0f);
            var dir = new Vector3(P1.X - P0.X, 0f, P1.Z - P0.Z).Normalized();
            static float Cross(Vector3 a, Vector3 b) => a.X * b.Z - a.Z * b.X;
            var hits = new List<(int span, float t, float s, Vector3 x)>();
            for (int k = 0; k < plan.Count; k++)
            {
                var sp = plan[k];
                if (sp.Axis > 1 || sp.Pts == null || sp.Pts.Count < 2) continue;
                var A = sp.Pts[0]; var B = sp.Pts[sp.Pts.Count - 1];
                var ab = new Vector3(B.X - A.X, 0f, B.Z - A.Z);
                float den = Cross(ab, dir);
                if (Mathf.Abs(den) < 1e-6f) continue;                       // parallel to it
                var ap = new Vector3(P0.X - A.X, 0f, P0.Z - A.Z);
                float t = Cross(ap, dir) / den;                             // along the span
                if (t < 0.2f || t > 0.8f) continue;                         // too near a node to be its own junction
                hits.Add((k, t, Cross(ap, ab) / den, new Vector3(A.X + ab.X * t, 0f, A.Z + ab.Z * t)));
            }
            if (hits.Count < 2) return 0;
            hits.Sort((a, b) => a.s.CompareTo(b.s));

            var keys = new List<(int, int)>(hits.Count);
            var splitAt = new Dictionary<int, (float t, (int, int) key)>();
            for (int h = 0; h < hits.Count; h++)
            {
                int gi = w + h / w, gj = h % w;
                if (gi >= grid.GetLength(0)) break;                        // out of spare rows
                var (k, t, _, x) = hits[h];
                var sp = plan[k];
                var on = PointAlong(sp.Pts, t * PolyLen(sp.Pts));
                grid[gi, gj] = new Node { P = new Vector3(x.X, on.Y, x.Z), Ok = true, Junction = -1, Extra = true,
                                          CrossHalf = roads.RoadHalfWidthOfMaterial(MatOf(sp)) };
                keys.Add((gi, gj));
                splitAt[k] = (t, (gi, gj));
                DiagonalNodes.Add(grid[gi, gj].P);
            }

            // split every span the diagonal crosses, pinning both halves exactly onto the new node
            var outPlan = new List<Span>(plan.Count + keys.Count * 2);
            for (int k = 0; k < plan.Count; k++)
            {
                var sp = plan[k];
                if (!splitAt.TryGetValue(k, out var cut)) { outPlan.Add(sp); continue; }
                float len = PolyLen(sp.Pts), sCut = cut.t * len;
                var first = CutBack(sp.Pts, 0f, len - sCut);
                var second = CutBack(sp.Pts, sCut, 0f);
                if (first == null || second == null) { outPlan.Add(sp); continue; }
                var xp = grid[cut.key.Item1, cut.key.Item2].P;
                first[first.Count - 1] = xp; second[0] = xp;
                var a = sp; a.BI = cut.key.Item1; a.BJ = cut.key.Item2; a.Pts = first; a.Len = PolyLen(first);
                var b = sp; b.AI = cut.key.Item1; b.AJ = cut.key.Item2; b.Pts = second; b.Len = PolyLen(second);
                outPlan.Add(a); outPlan.Add(b);
            }
            // ...and lay the diagonal itself, crossing to crossing
            for (int h = 0; h + 1 < keys.Count; h++)
            {
                var na = grid[keys[h].Item1, keys[h].Item2];
                var nb = grid[keys[h + 1].Item1, keys[h + 1].Item2];
                var pts = SpanPoints(na.P, nb.P, heightAt, out float len);
                if (pts == null) continue;                                  // too steep or wet: the avenue breaks here
                outPlan.Add(new Span { AI = keys[h].Item1, AJ = keys[h].Item2, BI = keys[h + 1].Item1, BJ = keys[h + 1].Item2,
                                       Axis = 2, AxisA = 2, AxisB = 2, Main = DiagonalMain, Pts = pts, Len = len });
            }
            plan.Clear(); plan.AddRange(outPlan);
            return keys.Count;
        }
        /// <summary>Lay a 45-degree avenue across the grid, and whether it is a main.</summary>
        public bool Diagonal, DiagonalMain;
        /// <summary>The roads the diagonal avenue was built as.</summary>
        public readonly HashSet<int> DiagonalRoads = new();
        /// <summary>Where the diagonal crosses the grid, so a camera can be put on one.</summary>
        public readonly List<Vector3> DiagonalNodes = new();

        /// <summary>Round the corners: every node with exactly two arms that TURN becomes one road curving
        /// round it on a circular arc, instead of two roads meeting at a point on a junction slab (strawberry
        /// 2026-10-12: "work on 45° intersections. as well as curved corner pieces").
        ///
        /// ⭐ A BEND IS NOT A JUNCTION. Two roads meeting at a corner were given everything a junction gets --
        /// a slab, fillet kerbs, cut ends -- and came out as a sharp point, because nothing in a junction
        /// knows how to TURN a road. Fusing the two spans into one polyline makes the corner the road's own
        /// geometry: its lane lines follow the curve, its pavements are offset round it, and there is no
        /// seam to fit because there is no second piece.
        /// ⚠ RESPACED EVENLY ONCE THE ARC IS IN. Catmull-Rom overshoots on the RATIO between neighbouring
        /// segments, and an arc sampled every couple of metres stitched to 12 m straights is exactly that
        /// ratio: the road would wiggle at both ends of every bend. One spacing, end to end.
        /// ⚠ NEVER FUSES TWO WIDTHS. A main turning into a street stays a junction; one ribbon is one width.</summary>
        int MergeBends(List<Span> plan, RoadField roads)
        {
            int made = 0;
            BendPoints.Clear(); _bendGeom.Clear(); _bentRoads.Clear();
            for (int guard = 0; guard < 256; guard++)
            {
                var at = new Dictionary<(int, int), List<(int span, bool atEnd)>>();
                for (int k = 0; k < plan.Count; k++)
                {
                    Add(at, (plan[k].AI, plan[k].AJ), (k, false));
                    Add(at, (plan[k].BI, plan[k].BJ), (k, true));
                }
                bool any = false;
                foreach (var kv in at)
                {
                    if (kv.Value.Count != 2) continue;
                    var (s1, e1) = kv.Value[0]; var (s2, e2) = kv.Value[1];
                    if (s1 == s2) continue;                                  // a ring closing on itself
                    var A = plan[s1]; var B = plan[s2];
                    if (A.Main != B.Main || A.Boulevard != B.Boulevard) continue;
                    // A oriented to END at the node, B to START at it
                    var pA = new List<Vector3>(A.Pts); if (!e1) pA.Reverse();
                    var pB = new List<Vector3>(B.Pts); if (e2) pB.Reverse();
                    if (pA.Count < 2 || pB.Count < 2) continue;
                    var dIn = new Vector3(pA[^1].X - pA[^2].X, 0f, pA[^1].Z - pA[^2].Z).Normalized();
                    var dOut = new Vector3(pB[1].X - pB[0].X, 0f, pB[1].Z - pB[0].Z).Normalized();
                    float turn = Mathf.Acos(Mathf.Clamp(dIn.Dot(dOut), -1f, 1f));
                    if (turn < Mathf.DegToRad(15f) || turn > Mathf.DegToRad(150f)) continue;   // straight on, or a hairpin
                    float half = roads.RoadHalfWidthOfMaterial(MatOf(A));
                    // the inside pavement must keep a positive radius: carriageway + pavement + some room
                    // ⚠ TIGHTEN RATHER THAN GIVE UP. A side the diagonal split is half as long, and a fixed radius
                    // that no longer fits left that corner SHARP. Shrink R to the room there is -- but never below
                    // what keeps the inside pavement a real curve.
                    float rMin = 2f * half + 6f, tanHalf = Mathf.Tan(turn * 0.5f);
                    float room = Mathf.Min(PolyLen(pA), PolyLen(pB)) * 0.45f;
                    float R = Mathf.Max(BendRadius, rMin);
                    if (R * tanHalf > room) R = Mathf.Max(rMin, room / tanHalf);
                    float T = R * tanHalf;
                    if (T > room) continue;                                  // not even the tightest corner fits
                    var cutA = CutBack(pA, 0f, T); var cutB = CutBack(pB, T, 0f);
                    if (cutA == null || cutB == null) continue;
                    var S = cutA[cutA.Count - 1]; var E = cutB[0];
                    // centre: inward from the start tangent point by R, inward = dOut minus its part along dIn
                    var nIn = (dOut - dIn * dIn.Dot(dOut)).Normalized();
                    var C = new Vector3(S.X, 0f, S.Z) + nIn * R;
                    float a0 = Mathf.Atan2(S.Z - C.Z, S.X - C.X), a1 = Mathf.Atan2(E.Z - C.Z, E.X - C.X);
                    float da = Mathf.Wrap(a1 - a0, -Mathf.Pi, Mathf.Pi);
                    // ⚠⚠ SAMPLED ON THE IDEAL PATH, NOT RESPACED ONTO CHORDS. Respace interpolates LINEARLY, so
                    // every respacing of an arc moves its points onto chords -- inside the circle by the sagitta
                    // -- and each pass compounds it: measured 4.4 cm off the true arc mid-curve, getting WORSE as
                    // the step shrank, which is how it showed itself to be a second error and not the first.
                    // Walk the straight-arc-straight by arc length instead and put every point exactly on it.
                    float lenA = PolyLen(cutA), lenB = PolyLen(cutB), arcLen = Mathf.Abs(da) * R;
                    float total = lenA + arcLen + lenB;
                    int nPts = Mathf.Max(2, Mathf.RoundToInt(total / BendStep));
                    var fusedPts = new List<Vector3>(nPts + 1);
                    for (int k = 0; k <= nPts; k++)
                    {
                        float sAt = total * k / nPts;
                        if (sAt <= lenA) fusedPts.Add(PointAlong(cutA, sAt));
                        else if (sAt >= lenA + arcLen) fusedPts.Add(PointAlong(cutB, sAt - lenA - arcLen));
                        else
                        {
                            float t = (sAt - lenA) / arcLen, ang = a0 + da * t;
                            fusedPts.Add(new Vector3(C.X + Mathf.Cos(ang) * R, Mathf.Lerp(S.Y, E.Y, t), C.Z + Mathf.Sin(ang) * R));
                        }
                    }
                    // the fused span runs from A's far node to B's far node, each end keeping its own axis
                    var far1 = e1 ? (A.AI, A.AJ) : (A.BI, A.BJ);
                    var far2 = e2 ? (B.AI, B.AJ) : (B.BI, B.BJ);
                    int axA = e1 ? A.AxisA : A.AxisB, axB = e2 ? B.AxisA : B.AxisB;
                    _bendGeom.Add((S, E, C, R, dIn, dOut));
                    var fused = new Span { AI = far1.Item1, AJ = far1.Item2, BI = far2.Item1, BJ = far2.Item2,
                                           Axis = axA, AxisA = axA, AxisB = axB, Main = A.Main, Boulevard = A.Boulevard, Bent = true,
                                           BendId = _bendGeom.Count - 1,
                                           Pts = fusedPts, Len = PolyLen(fusedPts), TrimA = 0f, TrimB = 0f };
                    plan.RemoveAt(Mathf.Max(s1, s2)); plan.RemoveAt(Mathf.Min(s1, s2));
                    plan.Add(fused);
                    float am = a0 + da * 0.5f;
                    BendPoints.Add(new Vector3(C.X + Mathf.Cos(am) * R, Mathf.Lerp(S.Y, E.Y, 0.5f), C.Z + Mathf.Sin(am) * R));
                    made++; any = true;
                    break;                                   // indices moved: rebuild the map
                }
                if (!any) break;
            }
            return made;
        }
        /// <summary>Each rounded corner's INTENDED shape -- tangent points, centre, radius, the two directions
        /// -- and which built road carries it, so the road that was actually built can be held to it.</summary>
        readonly List<(Vector3 S, Vector3 E, Vector3 C, float R, Vector3 dIn, Vector3 dOut)> _bendGeom = new();
        readonly List<(int road, int bend)> _bentRoads = new();
        /// <summary>Built road ends that are half of a main running through their node: not crossed, not stopped.</summary>
        readonly HashSet<(int road, bool atEnd)> _throughEnds = new();
        /// <summary>Built road ends whose crossing runs parallel to the line they yield to, and that line's direction.</summary>
        readonly Dictionary<(int road, bool atEnd), Vector3> _crossAlong = new();
        /// <summary>Is this road end half of a main that runs through its junction? Nothing stops it there.</summary>
        public bool RunsThrough(int road, bool atEnd) => _throughEnds.Contains((road, atEnd));
        /// <summary>The middle of every rounded corner's arc, so a camera can be put on one.</summary>
        public readonly List<Vector3> BendPoints = new();
        /// <summary>The last bend-fidelity measurement, for the test to hold the built roads to.</summary>
        public float BendWorstOffCm, BendWorstReverseDegPerM;
        /// <summary>UG_TOWNSHARPCORNERS=1 puts the corners back to two roads on a slab -- the control.</summary>
        public bool CurveCorners = System.Environment.GetEnvironmentVariable("UG_TOWNSHARPCORNERS") != "1";
        /// <summary>Centreline radius of a rounded corner, and the point spacing a fused road is respaced to.</summary>
        public float BendRadius = 22f, BendStep = 2f;
        /// <summary>The point at arc length <paramref name="sAt"/> along a polyline (clamped to its ends).</summary>
        static Vector3 PointAlong(List<Vector3> pts, float sAt)
        {
            float run = 0f;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float seg = Flat(pts[i], pts[i + 1]);
                if (seg > 1e-6f && sAt <= run + seg)
                {
                    float f = Mathf.Clamp((sAt - run) / seg, 0f, 1f);
                    return pts[i].Lerp(pts[i + 1], f);
                }
                run += seg;
            }
            return pts[pts.Count - 1];
        }
        static float PolyLen(List<Vector3> pts)
        {
            float t = 0f;
            for (int i = 0; i + 1 < pts.Count; i++) t += Flat(pts[i], pts[i + 1]);
            return t;
        }

        /// <summary>The same polyline, its points evenly spaced, with both ends exactly where they were --
        /// the far end must stay on the junction node it is about to be bound to.</summary>
        static List<Vector3> Respace(List<Vector3> pts, float step)
        {
            if (pts == null || pts.Count < 2) return null;
            float total = 0f;
            for (int i = 0; i + 1 < pts.Count; i++) total += Flat(pts[i], pts[i + 1]);
            if (total < 1e-3f) return null;
            int n = Mathf.Max(1, Mathf.RoundToInt(total / step));
            var outp = new List<Vector3>(n + 1) { pts[0] };
            float seg = total / n, run = 0f, want = seg;
            for (int i = 0; i + 1 < pts.Count && outp.Count <= n; i++)
            {
                float len = Flat(pts[i], pts[i + 1]);
                if (len < 1e-6f) continue;
                while (want <= run + len + 1e-4f && outp.Count <= n)
                {
                    float f = Mathf.Clamp((want - run) / len, 0f, 1f);
                    outp.Add(new Vector3(Mathf.Lerp(pts[i].X, pts[i + 1].X, f),
                                         Mathf.Lerp(pts[i].Y, pts[i + 1].Y, f),
                                         Mathf.Lerp(pts[i].Z, pts[i + 1].Z, f)));
                    want += seg;
                }
                run += len;
            }
            if (outp.Count < 2) return null;
            outp[^1] = pts[^1];
            return outp;
        }

        static float Flat(Vector3 p, Vector3 q) => new Vector2(q.X - p.X, q.Z - p.Z).Length();

        // ⚠ ALL THREE TAKE LATTICE-CENTRED INDICES (i, j already offset by -n, so the middle line is 0).
        // The first cut passed raw grid indices to one and centred ones to another, which is the sort of
        // mismatch that silently makes every third street a main one in the wrong place.
        /// <summary>Is this line a main street? axis 0 = a row (indexed by j), 1 = a column (by i). Every MainEvery-th line,
        /// counted from the line its axis's phase picks -- 0 for both on a fixed lattice, the seed's choice on a varied town
        /// (ChooseLayout), so the main streets are not always the two through the middle.</summary>
        bool MainLine(int axis, int line)
        {
            int e = Mathf.Max(1, MainEvery);
            return (((line + _mainPhase[axis & 1]) % e) + e) % e == 0;
        }
        readonly int[] _mainPhase = new int[2];
        /// <summary>The main line on this axis nearest the middle: where the boulevard runs.</summary>
        int CentreMain(int axis)
        {
            int e = Mathf.Max(1, MainEvery), ph = _mainPhase[axis & 1] % e;
            return ph <= e - ph ? -ph : e - ph;
        }

        /// <summary>Which axis OWNS a node: 0 = the row runs through it, 1 = the column does. The through
        /// street keeps its centreline to the node; every other arm stops at that street's kerb.
        /// ⚠ A tie goes to the row, always. Any rule works as long as it is the SAME rule for both spans
        /// arriving on an axis -- a dominance that disagreed with itself would pull one arm back and leave
        /// its opposite number running across the intersection.</summary>
        int DominantAxis(int i, int j)
        {
            bool rowMain = MainLine(0, j), colMain = MainLine(1, i);
            if (rowMain != colMain) return rowMain ? 0 : 1;
            return 0;
        }

        /// <summary>The road indices THIS grid created, in order. ⚠ Without it LaySidewalks paves every road
        /// in the field: the first run of the harness laid 80 ribbons for 17 streets, because the field also
        /// held the map's own 23 roads and a loop over RoadCount cannot tell whose they are.</summary>
        public readonly List<int> Built = new();
        readonly List<List<Vector3>> _profiles = new();   // what each built street's surface should be

        /// <summary>Grade the site: pull the ground up to (and down to) the streets once they are laid.
        ///
        /// ⭐ THIS IS WHAT MAKES IT A TOWN RATHER THAN A PATH NETWORK. Real streets are cut and filled, and so
        /// are InfiniteRoads' raised and cut stretches; a grid merely draped over a heightmap bucks over every
        /// bump and its junctions sit at whatever angle the hill was. ⚠ Use it WITH FollowTerrain = false --
        /// grading the ground while the surface re-samples that same ground is a feedback loop with no point:
        /// the road would chase the terrain it just moved.</summary>
        public bool GradeSite;
        public float GradeReach = 6f;     // full-weight levelling this far beyond the carriageway edge
        public float GradeFeather = 8f;   // ...then blended out over this much more (ConformToPolylines: outer = radius + feather)
        /// <summary>How far BELOW the street the ground is conformed.
        /// ⚠ NOT ZERO. Conforming the ground to exactly the road's own profile leaves two coplanar surfaces,
        /// and the depth buffer then picks between them per pixel: the first graded render came back with
        /// flattened corridors and no asphalt on them, because the terrain was fighting the tarmac for every
        /// pixel of it. ProcIslandSpawn sinks its conform target for the same reason, and 0.12 m is its
        /// number -- a heightmap approximates a sloping segment rather than reproducing it, and that error
        /// alone is enough to poke ground back through the road.</summary>
        public float GradeDrop = 0.12f;
        /// <summary>How far the pavement's top stands over the lawn behind it on a graded town (strawberry 2026-10-19: "move the
        /// road height (and sidewalk height) lower, and thus the grassy sides too"). UG_TOWNPROUD=<metres> overrides it.
        ///
        /// ⭐ THE STREETS USED TO STAND ON A BANK. Each street was profiled off the ground at its own level, the asphalt rode
        /// 0.22 m over that, the pavement 0.4 m, and the grading then cut the ground 0.12 m BELOW the street for a whole cell
        /// past the kerb -- so every pavement stood half a metre over the lawn behind it, and a kerb-only street's grass fell
        /// all the way down from the kerb stone. Now the street is laid StreetSink below the ground it was profiled off, the
        /// ground beside it is graded to the pavement's level less this, and only the carriageway's own ground is cut down
        /// (CeilCarriageways). UG_TOWNOLDGRADE=1 is the control: no sink, the old corridor cut.</summary>
        public float PavementProud = float.TryParse(System.Environment.GetEnvironmentVariable("UG_TOWNPROUD"), System.Globalization.NumberStyles.Float,
                                                    System.Globalization.CultureInfo.InvariantCulture, out float tproud) ? tproud : 0.05f;
        public static bool OldGrade = System.Environment.GetEnvironmentVariable("UG_TOWNOLDGRADE") == "1";
        /// <summary>How far below the ground it was profiled off a street is laid: the pavement's height less PavementProud.
        /// None on a draped town (its streets ride the ground) or under the control.</summary>
        public float StreetSink => OldGrade || FollowTerrain ? 0f : Mathf.Max(0f, PavementKerb - PavementProud);

        // ================== LAYOUT: a different town per seed, inside a set of rules ==================
        // strawberry 2026-10-19: "add a bunch of rules about how roads connect, min distances between junctions, areas where 45s
        // arent allowed. then shake up the shape/layout of roads within each different town, as well as support for clamped
        // sizes for towns". Until then every seed laid the same 5 x 5 lattice with one diagonal -- 51 roads, 29 junctions --
        // and only the jitter changed.

        /// <summary>Lay out a different town per seed (ChooseLayout): its size, block, bearing, outline, main streets,
        /// superblocks and diagonal avenues. Off, it is the fixed lattice every test was written against. The game turns it on.</summary>
        public bool Vary;
        /// <summary>A town's width and depth, metres, each picked per seed between these -- and a RequestedSize is clamped into
        /// them. Measured centreline to centreline of the outer streets, so it is the lattice that is held to it.</summary>
        public float SizeMin = 220f, SizeMax = 760f;
        /// <summary>A whole width asked for (both ways), or 0 to let the seed pick. Clamped to SizeMin..SizeMax.</summary>
        public float RequestedSize;
        /// <summary>The block (node spacing) a varied town picks between. ⚠ Not much under 100 m: a junction's fillets eat ~18 m
        /// of every street end (see Spacing).</summary>
        public float SpacingMin = 100f, SpacingMax = 125f;
        /// <summary>Leave Bearing as it was set; otherwise a varied town picks its own.</summary>
        public bool KeepBearing;
        /// <summary>Per corner of the outline, the chance a corner of blocks is left out: an L, a T, a stepped edge.</summary>
        public float BiteChance = 0.35f;
        /// <summary>Per interior side-street span, the chance it is left out and the two blocks either side become one.</summary>
        public float SuperblockChance = 0.12f;

        // ================== SETTLEMENT CLASS + WEALTH (strawberry 2026-10-19) ==================
        // "give towngen awareness of the differences between a small town, vs bigger towns, vs cities. as well as the wealth,
        // since a poorer town may not have all of the nice boulevards, sidewalks, or pedestrian infrastructure" + "town wealth
        // may also effect the buildings, businesses, as well as what appears in dead space. dirt patches -> nice parks".
        //
        // Two axes, each rolled per seed (or forced). They do NOT add new machinery -- they TUNE the dials the rest of the
        // generator already reads, before ChooseLayout runs: size, block, main-street frequency and the plot-kind MIX (class);
        // boulevards, street profiles, pavement width, street trees, crossings and the parks (wealth). ApplyClassAndWealth is
        // the one place the two axes become numbers, so a render of "a poor city" vs "an affluent hamlet" is a set of knobs,
        // not a special case scattered through the file.

        /// <summary>How big the place is. Hamlet: a few blocks, nearly all houses. City: wide, small blocks, shops and flats.</summary>
        public enum SettlementSize { Hamlet, SmallTown, Town, City }
        /// <summary>How well-off it is. Matches tinyclaw's BuildingGenSpec.Wealth 1:1 so SpecFor can pass it straight through.</summary>
        public enum Wealth { Poor, Working, Affluent }
        /// <summary>This town's class and wealth. ApplyClassAndWealth sets them from the seed unless forced (ClassForced /
        /// WealthForced, or the env/menu). Read by SpecFor (buildings), the greens (parks) and the strip tenant mix.</summary>
        public SettlementSize Size = SettlementSize.Town;
        public Wealth Money = Wealth.Working;
        public bool ClassForced, WealthForced, SizeForced, SpacingForced;
        /// <summary>Whether a varied town tunes its dials to its class and wealth. Off = the plain Vary town (every test's).</summary>
        public bool ClassAware = true;
        /// <summary>One line naming the class and wealth and what they set, for the log.</summary>
        public string ClassSummary = "";

        /// <summary>Turn the two axes into the dials ChooseLayout and the builders read. Called once, at the top of ChooseLayout,
        /// BEFORE it reads SizeMin/Spacing/MainEvery. Deterministic from the seed; a forced axis is left as set.</summary>
        void ApplyClassAndWealth()
        {
            if (!ClassAware) return;
            ulong hc = Mix(((ulong)(uint)Seed << 32) ^ 0xC1A55UL), hw = Mix(hc ^ 0x3EA17UL);
            // ---- which class: hamlets and towns common, cities rare (a map is mostly small places with one city)
            if (!ClassForced)
            {
                float u = Unit(hc, 0);
                Size = u < 0.30f ? SettlementSize.Hamlet : u < 0.62f ? SettlementSize.SmallTown : u < 0.88f ? SettlementSize.Town : SettlementSize.City;
            }
            // ---- which wealth: working common, poor and affluent the tails
            if (!WealthForced)
            {
                float u = Unit(hw, 0);
                Money = u < 0.33f ? Wealth.Poor : u < 0.78f ? Wealth.Working : Wealth.Affluent;
            }

            // ---- SIZE DIALS (the footprint, the block, how often a main street) ----
            // ⚠ an explicit UG_TOWNSIZE*/SPACING override wins -- those set *Forced and this leaves them.
            var (szMin, szMax, spMin, spMax, me) = Size switch
            {
                SettlementSize.Hamlet    => (140f, 260f, 110f, 135f, 5),   // a cluster, big rural blocks, a main through it
                SettlementSize.SmallTown => (260f, 430f, 105f, 125f, 4),
                SettlementSize.Town      => (420f, 650f, 100f, 120f, 3),
                SettlementSize.City      => (640f, 920f,  92f, 108f, 2),   // wide, tight blocks, a main every other line
                _                        => (SizeMin, SizeMax, SpacingMin, SpacingMax, MainEvery),
            };
            if (!SizeForced) { SizeMin = szMin; SizeMax = szMax; }
            if (!SpacingForced) { SpacingMin = spMin; SpacingMax = spMax; }
            MainEvery = me;
            // ⚠ THE MIX IS NOT SET HERE -- it comes from DENSITY, per block (strawberry 2026-10-19: "a separate density heatmap.
            // ie the downtown isnt magically giant plots, space is still a premium downtown"). Class shapes the density FIELD
            // (a city has a broad dense core, a hamlet almost none, Density()); KindsFor reads the density where each plot sits
            // and picks flats/shops/small lots downtown, roomy houses on the cheap edge -- the same for a poor or a rich town.

            // ---- WEALTH DIALS (the nice stuff: boulevards, pavements, trees, crossings, parks) ----
            // A boulevard is a well-off flourish: never in a poor town, a city always, the middle only if it is affluent.
            Boulevards = Money != Wealth.Poor && (Size == SettlementSize.City || Money == Wealth.Affluent)
                         && System.Environment.GetEnvironmentVariable("UG_TOWNNOBOULEVARD") != "1";
            // street profiles: poor = mostly kerb-only or no pavement at all, no verges; affluent = wide paved walks and grass
            // verges; working in between. (shares of the NON-main, non-business streets; a main is always paved.)
            (VergeShare, KerbOnlyShare, NoPavementShare, PavementWidth) = Money switch
            {
                Wealth.Poor     => (0.00f, 0.82f, 0.00f, 2.0f),   // kerbs and weedy grass, almost no footway
                Wealth.Working  => (0.35f, 0.25f, 0.00f, 3.0f),
                Wealth.Affluent => (0.60f, 0.05f, 0.00f, 4.5f),   // verges and broad walks
                _               => (VergeShare, KerbOnlyShare, NoPavementShare, PavementWidth),
            };
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_TOWNWALK"), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float tw) && tw > 0.5f) PavementWidth = tw;
            // pedestrian crossings: a poor town has few, an affluent one has them at most side junctions
            SideCrossingShare = Money switch { Wealth.Poor => 0.08f, Wealth.Working => 0.40f, Wealth.Affluent => 0.75f, _ => SideCrossingShare };
            // street trees: poor plants almost none (sparse + small), affluent lines the streets
            (StreetTreeEvery, GreenRichness) = Money switch
            {
                Wealth.Poor     => (0.20f, 0.15f),
                Wealth.Working  => (0.75f, 0.55f),
                Wealth.Affluent => (1.00f, 1.00f),
                _               => (StreetTreeEvery, GreenRichness),
            };
            // ⭐ DEAD SPACE: a poor town's is bare dirt and weeds, a rich town's is a hedged park with benches (strawberry: "dirt
            // patches -> nice parks"). GreenRichness scales the thresholds the greens read -- a high ParkMinArea means few dead
            // patches are big enough to become a park, a high BenchMinParkArea means none get benches. DirtDeadSpace paints the
            // rest as dirt. (The knobs live in TownGreens, this partial class, so setting them here is enough.)
            float gr = Mathf.Clamp(GreenRichness, 0.05f, 1f);
            ParkMinArea = 250f / gr;                 // poor (0.15): parks need ~1670 m2, so almost none form
            BenchMinParkArea = 600f / gr;            // and essentially no benches
            ParkOpenChance = Mathf.Lerp(0.15f, 0.45f, gr);
            RowHedgeChance = Mathf.Lerp(0.10f, 0.70f, gr);   // poor: strips are bushes/weeds, not clipped hedgerows
            DirtDeadSpace = GreenRichness < 0.35f;   // the leftover dead space is painted dirt, not left as grass
            BushScale = Mathf.Lerp(0.35f, 0.5f, gr);

            ClassSummary = $"{Size} / {Money}: {SizeMin:0}-{SizeMax:0} m, blocks {SpacingMin:0}-{SpacingMax:0}, a main every {MainEvery}, "
                         + $"boulevard {(Boulevards ? "yes" : "no")}, walks {PavementWidth:0.0} m (verge {VergeShare:0.00}/kerb {KerbOnlyShare:0.00}/"
                         + $"none {NoPavementShare:0.00}), crossings {SideCrossingShare:0.00}, trees x{StreetTreeEvery:0.00}, greens x{GreenRichness:0.00}";
        }
        /// <summary>Share of non-main streets with NO kerb and NO pavement at all -- just the road and grass to the plots
        /// (a poor or rural street). The remainder after VergeShare + KerbOnlyShare that is not plain pavement.</summary>
        public float NoPavementShare = 0.0f;
        /// <summary>How much of the normal street-tree run a town plants (0 = none, 1 = the full spacing). Wealth sets it.</summary>
        public float StreetTreeEvery = 1.0f;
        /// <summary>How nice the dead space is: 0 = bare dirt and weeds (a poor town), 1 = full hedged parks with benches and
        /// trees. Scales park frequency, benches and planting in TownGreens.</summary>
        public float GreenRichness = 1.0f;

        // ---- the rules ----
        /// <summary>No two junctions closer than this, metres. A diagonal's crossing has to stand this far from both ends of the
        /// street it crosses, or the avenue is not built across that street.</summary>
        public float MinJunctionGap = 35f;
        /// <summary>No junction with more arms than this. The lattice gives four, and a diagonal runs half a block off every
        /// node -- so it only ever makes a new four-way, never a five- or six-way.</summary>
        public int MaxArms = 4;
        /// <summary>No loose ends: a span left with an end on nothing (the outline, the gates, a superblock) is taken out, again
        /// and again, and only the biggest connected piece of the network is kept.</summary>
        public bool PruneDeadEnds = true;
        /// <summary>A diagonal avenue shorter than this many blocks is not built: one block of 45-degree road reads as a slip
        /// road, not an avenue.</summary>
        public int MinDiagonalBlocks = 2;
        /// <summary>Where a 45-degree avenue may not go: rectangles in the lattice's own metres (u, v, before the jitter). The
        /// core -- the four blocks round where the middle main streets cross -- always; the seed may add another.</summary>
        public readonly List<Rect2> No45 = new();
        /// <summary>The diagonals the seed picked: the line v = slope * u + c in the lattice's metres.</summary>
        readonly List<(int slope, float c)> _diagonals = new();
        /// <summary>Lattice nodes the outline leaves out (lattice-centred i, j), and the half-counts the size allows each way.</summary>
        readonly HashSet<(int, int)> _outside = new();
        int _nU = int.MaxValue, _nV = int.MaxValue;
        bool _layoutChosen;
        /// <summary>One line saying what the seed chose and what the rules did to it.</summary>
        public string LayoutSummary = "";
        /// <summary>The half-span of the varied town, metres: how far from its centre its outer streets' kerbs reach.</summary>
        public float HalfSpan => (Mathf.Max(_nU == int.MaxValue ? 0 : _nU, _nV == int.MaxValue ? 0 : _nV) * Spacing) + 15f;

        bool InShape(int i, int j) => !Vary || (Mathf.Abs(i) <= _nU && Mathf.Abs(j) <= _nV && !_outside.Contains((i, j)));

        /// <summary>Pick this seed's town. Deterministic from Seed alone (the site comes later), and only once.</summary>
        public void ChooseLayout()
        {
            if (!Vary || _layoutChosen) return;
            _layoutChosen = true;
            ApplyClassAndWealth();          // ⭐ class + wealth set the dials below BEFORE the seed reads them
            ulong h0 = Mix(((ulong)(uint)Seed << 32) ^ 0x7A40C5UL), h1 = Mix(h0 ^ 0x51DE5UL), h2 = Mix(h1 ^ 0xD1A6UL);
            if (!KeepBearing) Bearing = Unit(h0, 0) * Mathf.Pi * 0.5f;
            Spacing = Mathf.Lerp(Mathf.Min(SpacingMin, SpacingMax), Mathf.Max(SpacingMin, SpacingMax), Unit(h0, 16));
            // ---- SIZE, clamped: each way a whole number of blocks whose width falls inside SizeMin..SizeMax where one does
            float lo = Mathf.Min(SizeMin, SizeMax), hi = Mathf.Max(SizeMin, SizeMax);
            int Half(float size)
            {
                int nLo = Mathf.Max(1, Mathf.CeilToInt(lo * 0.5f / Spacing - 1e-3f)), nHi = Mathf.Max(1, Mathf.FloorToInt(hi * 0.5f / Spacing + 1e-3f));
                if (nLo > nHi) return nHi;                     // no whole block fits the range: the largest under its top
                return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp(size, lo, hi) * 0.5f / Spacing), nLo, nHi);
            }
            _nU = Half(RequestedSize > 0f ? RequestedSize : Mathf.Lerp(lo, hi, Unit(h0, 32)));
            _nV = Half(RequestedSize > 0f ? RequestedSize : Mathf.Lerp(lo, hi, Unit(h0, 48)));
            int n = Mathf.Max(_nU, _nV);
            Extent = n * Spacing + Spacing * 0.5f;             // the lattice's own reach; the outline masks it down from there
            // ---- OUTLINE: a corner of blocks bitten out here and there, never down to under two blocks either way
            _outside.Clear();
            var bites = new List<string>();
            for (int corner = 0; corner < 4; corner++)
            {
                int sx = (corner & 1) == 0 ? 1 : -1, sy = (corner & 2) == 0 ? 1 : -1;
                ulong hc = Mix(h1 ^ ((ulong)(corner + 1) * 0x9E3779B97F4A7C15UL));
                if (_nU < 2 || _nV < 2 || Unit(hc, 0) >= BiteChance) continue;
                int bu = Mathf.Clamp(1 + (int)(Unit(hc, 16) * (_nU - 1)), 1, _nU - 1);
                int bv = Mathf.Clamp(1 + (int)(Unit(hc, 32) * (_nV - 1)), 1, _nV - 1);
                for (int i = -_nU; i <= _nU; i++)
                    for (int j = -_nV; j <= _nV; j++)
                        if (sx * i > _nU - bu && sy * j > _nV - bv) _outside.Add((i, j));
                bites.Add($"{(sy > 0 ? "N" : "S")}{(sx > 0 ? "E" : "W")} {bu}x{bv}");
            }
            // ---- MAIN STREETS: which lines, each way
            int e = Mathf.Max(1, MainEvery);
            _mainPhase[0] = (int)(Unit(h1, 48) * e) % e; _mainPhase[1] = (int)(Unit(h2, 48) * e) % e;
            // ---- WHERE A 45 MAY NOT GO: the core, round where the middle mains cross, and maybe one more patch of blocks
            No45.Clear();
            int iM = CentreMain(1), jM = CentreMain(0);
            No45.Add(new Rect2((iM - 1) * Spacing, (jM - 1) * Spacing, 2f * Spacing, 2f * Spacing));
            if (Unit(h2, 0) < 0.4f && n >= 2)
            {
                int zi = -n + (int)(Unit(h2, 8) * (2 * n - 1)), zj = -n + (int)(Unit(h2, 24) * (2 * n - 1));
                No45.Add(new Rect2(zi * Spacing, zj * Spacing, 2f * Spacing, 2f * Spacing));
            }
            // ---- DIAGONAL AVENUES: none, one, or two parallel ones at least two blocks apart (two that crossed each other
            // would meet ON a lattice line -- a six-way -- whichever offsets they had)
            _diagonals.Clear();
            float pick = Unit(h1, 32);
            // ⭐ a 45-degree AVENUE is a grand gesture: a poor town never gets one, a hamlet rarely, a city can have two.
            int maxDiag = !ClassAware ? 2 : Money == Wealth.Poor ? 0 : Size switch { SettlementSize.Hamlet => 1, SettlementSize.City => 2, _ => (Money == Wealth.Affluent ? 2 : 1) };
            int count = !Diagonal ? 0 : Mathf.Min(maxDiag, pick < 0.3f ? 0 : pick < 0.8f ? 1 : 2);
            int slope = Unit(h2, 16) < 0.5f ? 1 : -1;
            int k1 = -n + (int)(Unit(h2, 32) * (2 * n));
            for (int d = 0; d < count; d++)
            {
                int k = d == 0 ? k1 : k1 + (k1 > 0 ? -1 : 1) * (2 + (int)(Unit(h2, 40) * 2f));
                _diagonals.Add((slope, (k + 0.5f) * Spacing));
            }
            LayoutSummary = $"seed {Seed}: {2 * _nU * Spacing:0} x {2 * _nV * Spacing:0} m ({2 * _nU} x {2 * _nV} blocks of {Spacing:0} m, "
                          + $"clamped {lo:0}..{hi:0}), bearing {Mathf.RadToDeg(Bearing):0} deg, "
                          + $"outline {(bites.Count > 0 ? string.Join(", ", bites) + " bitten out" : "whole")}, "
                          + $"mains on rows {_mainPhase[0]}/cols {_mainPhase[1]} of {e}, {_diagonals.Count} diagonal(s) planned, "
                          + $"{No45.Count} no-45 zone(s)";
        }

        /// <summary>Leave out a few interior side-street spans, joining the blocks either side into one. Only where both ends
        /// keep three arms (no corners or loose ends made) and neither block has been joined already.</summary>
        int DropSpans(List<Span> plan, int n)
        {
            var degree = new Dictionary<(int, int), int>();
            foreach (var sp in plan) { Bump(degree, (sp.AI, sp.AJ)); Bump(degree, (sp.BI, sp.BJ)); }
            static void Bump(Dictionary<(int, int), int> d, (int, int) k) => d[k] = d.TryGetValue(k, out int c) ? c + 1 : 1;
            bool Whole(int gi, int gj) => gi >= 0 && gj >= 0 && gi + 1 < _nodeOk.GetLength(0) && gj + 1 < _nodeOk.GetLength(1)
                                          && _nodeOk[gi, gj] && _nodeOk[gi + 1, gj] && _nodeOk[gi, gj + 1] && _nodeOk[gi + 1, gj + 1];
            var joined = new HashSet<(int, int)>();
            int dropped = 0;
            for (int k = plan.Count - 1; k >= 0; k--)
            {
                var sp = plan[k];
                if (sp.Main || sp.Axis > 1) continue;
                // the two blocks either side of it
                (int, int) b0, b1;
                if (sp.Axis == 0) { b0 = (Mathf.Min(sp.AI, sp.BI), sp.AJ - 1); b1 = (Mathf.Min(sp.AI, sp.BI), sp.AJ); }
                else { b0 = (sp.AI - 1, Mathf.Min(sp.AJ, sp.BJ)); b1 = (sp.AI, Mathf.Min(sp.AJ, sp.BJ)); }
                if (!Whole(b0.Item1, b0.Item2) || !Whole(b1.Item1, b1.Item2) || joined.Contains(b0) || joined.Contains(b1)) continue;
                if (degree[(sp.AI, sp.AJ)] < 4 || degree[(sp.BI, sp.BJ)] < 4) continue;   // both ends stay at least tees
                ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)sp.Axis << 40) ^ ((ulong)(uint)sp.AI << 20) ^ (uint)sp.AJ ^ 0x5B10CUL));
                if (Unit(h, 0) >= SuperblockChance) continue;
                plan.RemoveAt(k);
                degree[(sp.AI, sp.AJ)]--; degree[(sp.BI, sp.BJ)]--;
                joined.Add(b0); joined.Add(b1);
                dropped++;
            }
            return dropped;
        }

        /// <summary>Take out every span with a loose end, until none is left, then keep only the biggest connected piece.
        /// Returns how many spans went.</summary>
        int PruneLoose(List<Span> plan)
        {
            int removed = 0;
            for (bool any = true; any;)
            {
                any = false;
                var degree = new Dictionary<(int, int), int>();
                foreach (var sp in plan)
                {
                    degree[(sp.AI, sp.AJ)] = degree.TryGetValue((sp.AI, sp.AJ), out int a) ? a + 1 : 1;
                    degree[(sp.BI, sp.BJ)] = degree.TryGetValue((sp.BI, sp.BJ), out int b) ? b + 1 : 1;
                }
                for (int k = plan.Count - 1; k >= 0; k--)
                    if (degree[(plan[k].AI, plan[k].AJ)] < 2 || degree[(plan[k].BI, plan[k].BJ)] < 2) { plan.RemoveAt(k); removed++; any = true; }
            }
            // the biggest piece: union-find over the nodes
            var parent = new Dictionary<(int, int), (int, int)>();
            (int, int) Find((int, int) x)
            {
                if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
                if (p.Equals(x)) return x;
                var r = Find(p); parent[x] = r; return r;
            }
            foreach (var sp in plan) { var ra = Find((sp.AI, sp.AJ)); var rb = Find((sp.BI, sp.BJ)); if (!ra.Equals(rb)) parent[ra] = rb; }
            var size = new Dictionary<(int, int), int>();
            foreach (var sp in plan) { var r = Find((sp.AI, sp.AJ)); size[r] = size.TryGetValue(r, out int c) ? c + 1 : 1; }
            if (size.Count > 1)
            {
                var keep = default((int, int)); int best = -1;
                foreach (var kv in size) if (kv.Value > best) { best = kv.Value; keep = kv.Key; }
                for (int k = plan.Count - 1; k >= 0; k--)
                    if (!Find((plan[k].AI, plan[k].AJ)).Equals(keep)) { plan.RemoveAt(k); removed++; }
            }
            return removed;
        }

        /// <summary>A diagonal avenue by the rules (a varied town's; AddDiagonal is the fixed lattice's): the line v = slope u + c
        /// across whatever of the grid is there, a crossing wherever it meets a street -- but only a crossing that stands
        /// MinJunctionGap from both ends of that street, and only a block's worth of avenue whose middle is inside the town and
        /// outside every No45 zone. What is left is cut into runs; a run shorter than MinDiagonalBlocks is not built.
        /// Returns the crossings made, and adds the runs it cut (with why) to `why`.</summary>
        int AddDiagonalRuled(List<Span> plan, Node[,] grid, int w, int baseRow, RoadField roads, Func<float, float, float> heightAt,
                             int slope, float c, List<string> why)
        {
            var (hits, lay, gapCut, zoneCut, outCut, shortCut) = PlanDiagonal(plan, slope, c);
            return LayDiagonal(plan, grid, w, baseRow, roads, heightAt, slope, c, why, hits, lay, gapCut, zoneCut, outCut, shortCut);
        }

        /// <summary>Where a diagonal on v = slope u + c would cross the planned streets, and which blocks of it the rules allow
        /// -- without building or splitting anything, so a town can try every offset and keep the best.</summary>
        (List<(int span, float t, float s, Vector3 x, bool ok)> hits, bool[] lay, int gapCut, int zoneCut, int outCut, int shortCut)
            PlanDiagonal(List<Span> plan, int slope, float c)
        {
            var P0 = ToWorld(0f, c, 0f);
            var P1 = ToWorld(Spacing, c + slope * Spacing, 0f);
            var dir = new Vector3(P1.X - P0.X, 0f, P1.Z - P0.Z).Normalized();
            static float Cross(Vector3 a, Vector3 b) => a.X * b.Z - a.Z * b.X;
            var hits = new List<(int span, float t, float s, Vector3 x, bool ok)>();
            for (int k = 0; k < plan.Count; k++)
            {
                var sp = plan[k];
                if (sp.Axis > 1 || sp.Bent || sp.Pts == null || sp.Pts.Count < 2) continue;
                var A = sp.Pts[0]; var B = sp.Pts[sp.Pts.Count - 1];
                var ab = new Vector3(B.X - A.X, 0f, B.Z - A.Z);
                float den = Cross(ab, dir);
                if (Mathf.Abs(den) < 1e-6f) continue;
                var ap = new Vector3(P0.X - A.X, 0f, P0.Z - A.Z);
                float t = Cross(ap, dir) / den;
                if (t <= 0f || t >= 1f) continue;
                float len = ab.Length();
                bool ok = t * len >= MinJunctionGap && (1f - t) * len >= MinJunctionGap;     // ⚠ THE GAP RULE
                hits.Add((k, t, Cross(ap, ab) / den, new Vector3(A.X + ab.X * t, 0f, A.Z + ab.Z * t), ok));
            }
            hits.Sort((a, b) => a.s.CompareTo(b.s));
            // which blocks of avenue survive the rules
            float cs = Mathf.Cos(Bearing), sn = Mathf.Sin(Bearing);
            (float u, float v) Local(Vector3 p) { float dx = p.X - Centre.X, dz = p.Z - Centre.Z; return (dx * cs + dz * sn, -dx * sn + dz * cs); }
            var lay = new bool[Mathf.Max(0, hits.Count - 1)];
            int gapCut = 0, zoneCut = 0, outCut = 0;
            for (int h = 0; h + 1 < hits.Count; h++)
            {
                if (!hits[h].ok || !hits[h + 1].ok) { gapCut++; continue; }
                var mid = (hits[h].x + hits[h + 1].x) * 0.5f;
                // one block's step, and over a block that is really there (not across the outline's bite or the void past it)
                if (Flat(hits[h].x, hits[h + 1].x) > Spacing * 1.05f) { outCut++; continue; }
                var blk = BlockOf(mid);
                if (blk is not { } b || !(_nodeOk[b.Item1, b.Item2] && _nodeOk[b.Item1 + 1, b.Item2] && _nodeOk[b.Item1, b.Item2 + 1] && _nodeOk[b.Item1 + 1, b.Item2 + 1]))
                { outCut++; continue; }
                var (mu, mv) = Local(mid);
                bool inZone = false;
                foreach (var z in No45) if (z.HasPoint(new Vector2(mu, mv))) { inZone = true; break; }
                if (inZone) { zoneCut++; continue; }
                lay[h] = true;
            }
            // runs: a run shorter than MinDiagonalBlocks is not built
            int shortCut = 0;
            for (int h = 0; h < lay.Length;)
            {
                if (!lay[h]) { h++; continue; }
                int e2 = h; while (e2 < lay.Length && lay[e2]) e2++;
                if (e2 - h < MinDiagonalBlocks) { for (int q = h; q < e2; q++) lay[q] = false; shortCut++; }
                h = e2;
            }
            return (hits, lay, gapCut, zoneCut, outCut, shortCut);
        }

        int LayDiagonal(List<Span> plan, Node[,] grid, int w, int baseRow, RoadField roads, Func<float, float, float> heightAt,
                        int slope, float c, List<string> why, List<(int span, float t, float s, Vector3 x, bool ok)> hits, bool[] lay,
                        int gapCut, int zoneCut, int outCut, int shortCut)
        {
            // the crossings the surviving runs need, one node each, split into the spans they cross
            var key = new (int, int)[hits.Count];
            var splitAt = new Dictionary<int, (float t, (int, int) key)>();
            int made = 0;
            for (int h = 0; h < hits.Count; h++)
            {
                bool used = (h > 0 && lay[h - 1]) || (h < lay.Length && lay[h]);
                if (!used) continue;
                int gi = baseRow + made / w, gj = made % w;
                if (gi >= grid.GetLength(0)) break;
                var (k, t, _, x, _) = hits[h];
                var sp = plan[k];
                var on = PointAlong(sp.Pts, t * PolyLen(sp.Pts));
                grid[gi, gj] = new Node { P = new Vector3(x.X, on.Y, x.Z), Ok = true, Junction = -1, Extra = true,
                                          CrossHalf = roads.RoadHalfWidthOfMaterial(MatOf(sp)) };
                key[h] = (gi, gj);
                splitAt[k] = (t, (gi, gj));
                DiagonalNodes.Add(grid[gi, gj].P);
                made++;
            }
            var outPlan = new List<Span>(plan.Count + made * 2);
            for (int k = 0; k < plan.Count; k++)
            {
                var sp = plan[k];
                if (!splitAt.TryGetValue(k, out var cut)) { outPlan.Add(sp); continue; }
                float len = PolyLen(sp.Pts), sCut = cut.t * len;
                var first = CutBack(sp.Pts, 0f, len - sCut);
                var second = CutBack(sp.Pts, sCut, 0f);
                if (first == null || second == null) { outPlan.Add(sp); continue; }
                var xp = grid[cut.key.Item1, cut.key.Item2].P;
                first[first.Count - 1] = xp; second[0] = xp;
                var a = sp; a.BI = cut.key.Item1; a.BJ = cut.key.Item2; a.Pts = first; a.Len = PolyLen(first);
                var b = sp; b.AI = cut.key.Item1; b.AJ = cut.key.Item2; b.Pts = second; b.Len = PolyLen(second);
                outPlan.Add(a); outPlan.Add(b);
            }
            int pieces = 0;
            for (int h = 0; h < lay.Length; h++)
            {
                if (!lay[h]) continue;
                var na = grid[key[h].Item1, key[h].Item2]; var nb = grid[key[h + 1].Item1, key[h + 1].Item2];
                var pts = SpanPoints(na.P, nb.P, heightAt, out float len);
                if (pts == null) continue;                                  // too steep or wet: the avenue breaks here
                outPlan.Add(new Span { AI = key[h].Item1, AJ = key[h].Item2, BI = key[h + 1].Item1, BJ = key[h + 1].Item2,
                                       Axis = 2, AxisA = 2, AxisB = 2, Main = DiagonalMain, Pts = pts, Len = len });
                pieces++;
            }
            plan.Clear(); plan.AddRange(outPlan);
            why.Add($"v = {(slope > 0 ? "+" : "-")}u{(c >= 0 ? "+" : "-")}{Mathf.Abs(c):0}: {pieces} blocks built; cut {gapCut} at a crossing under "
                  + $"{MinJunctionGap:0} m from a junction, {zoneCut} in a no-45 zone, {outCut} outside the town, {shortCut} run(s) too short");
            return made;
        }

        /// <summary>The rules, measured on what was planned (not taken on trust): the most arms at any junction, and the closest
        /// two junctions come -- with how many pairs are under MinJunctionGap.</summary>
        string CheckRules(List<Span> plan, Node[,] grid)
        {
            var degree = new Dictionary<(int, int), int>();
            foreach (var sp in plan)
            {
                degree[(sp.AI, sp.AJ)] = degree.TryGetValue((sp.AI, sp.AJ), out int a) ? a + 1 : 1;
                degree[(sp.BI, sp.BJ)] = degree.TryGetValue((sp.BI, sp.BJ), out int b) ? b + 1 : 1;
            }
            int most = 0, loose = 0;
            var junctions = new List<Vector3>();
            foreach (var kv in degree)
            {
                most = Mathf.Max(most, kv.Value);
                if (kv.Value == 1) loose++;
                if (kv.Value >= 3) junctions.Add(grid[kv.Key.Item1, kv.Key.Item2].P);
            }
            float closest = float.MaxValue; int under = 0;
            for (int a = 0; a < junctions.Count; a++)
                for (int b = a + 1; b < junctions.Count; b++)
                {
                    float d = Flat(junctions[a], junctions[b]);
                    closest = Mathf.Min(closest, d);
                    if (d < MinJunctionGap) under++;
                }
            string verdict = most <= MaxArms && under == 0 && (!PruneDeadEnds || loose == 0) ? "all held" : "BROKEN";
            return $"rules {verdict}: at most {most} arms (max {MaxArms}), {loose} loose end(s), closest junctions {closest:0.0} m apart "
                 + $"({under} pair(s) under {MinJunctionGap:0} m) over {junctions.Count} junctions";
        }

        /// <summary>Lay the grid into <paramref name="roads"/>. <paramref name="heightAt"/> is the ground
        /// sampler (Terrain.Active.SampleHeight in game; a lambda in a test, which is the only way to test the
        /// gates at all -- you cannot arrange a cliff in the real map on demand). <paramref name="siteOk"/>
        /// optionally vetoes ground for reasons the heightfield does not know about.</summary>
        public Report Generate(RoadField roads, Func<float, float, float> heightAt, Func<float, float, bool> siteOk = null)
        {
            var rep = new Report();
            _heightAt = heightAt;
            Built.Clear(); _profiles.Clear(); Marks.Clear(); _throughEnds.Clear(); _crossAlong.Clear(); _clips.Clear(); DiagonalRoads.Clear();
            _wideTrimRoads = null; Foliage.Clear();
            if (Vary) ChooseLayout();          // (once per town: Main asks first, to size the site it looks for)
            if (roads == null || heightAt == null || Spacing < 4f || Extent < Spacing) return rep;
            // the kerb return follows the road's width, so everything measured off it does too
            CornerKerbRadius = Mathf.Clamp(roads.RoadHalfWidthOfMaterial(MainMaterial) * CornerKerbFraction, 2f, 9f);
            MakeBoulevardMaterial(roads);

            int n = Mathf.FloorToInt(Extent / Spacing);
            if (n < 1) return rep;
            int w = 2 * n + 1;
            // ⚠ SPARE ROWS for the diagonal's crossings. They are nodes like any other, so they live in the
            // same array and every grid[i, j] in here keeps working; the loops over the grid stop at w.
            var grid = new Node[w + 4, w];

            // ---- 1. NODES. Each one is a potential junction, so the gates here are the strict ones.
            _frameU0 = _frameV0 = float.MaxValue; _frameU1 = _frameV1 = float.MinValue;
            _lineN = n; _lineU = new float[w]; _lineV = new float[w]; _nodeOk = new bool[w, w];
            for (int gi = 0; gi < w; gi++)
                for (int gj = 0; gj < w; gj++)
                {
                    int i = gi - n, j = gj - n;
                    // u depends on i ONLY and v on j ONLY -- that is what keeps every line straight.
                    float u = i * Spacing + (Unit(LineHash(1, i), 0) - 0.5f) * Jitter * Spacing;
                    float v = j * Spacing + (Unit(LineHash(0, j), 0) - 0.5f) * Jitter * Spacing;
                    var flat = ToWorld(u, v, 0f);
                    float y = heightAt(flat.X, flat.Z);
                    // (laid StreetSink under the ground, as every point of its spans is; the gates below read the ground itself)
                    var node = new Node { P = new Vector3(flat.X, y - StreetSink, flat.Z), Junction = -1 };

                    bool dry = y > SeaLevel + SeaMargin;
                    // Local steepness from four probes. One probe pair cannot see a ridge running diagonally.
                    const float probe = 8f;
                    float sx = Mathf.Abs(heightAt(flat.X + probe, flat.Z) - heightAt(flat.X - probe, flat.Z)) / (2f * probe);
                    float sz = Mathf.Abs(heightAt(flat.X, flat.Z + probe) - heightAt(flat.X, flat.Z - probe)) / (2f * probe);
                    bool flatEnough = Mathf.Max(sx, sz) <= MaxNodeSlope;
                    // (a node the outline leaves out is not refused by the ground, so it is not counted as refused)
                    bool inShape = InShape(i, j);
                    node.Ok = inShape && dry && flatEnough && (siteOk == null || siteOk(flat.X, flat.Z));
                    if (node.Ok) rep.Nodes++; else if (inShape) rep.NodesRejected++;
                    if (node.Ok)
                    {
                        _frameU0 = Mathf.Min(_frameU0, u); _frameU1 = Mathf.Max(_frameU1, u);
                        _frameV0 = Mathf.Min(_frameV0, v); _frameV1 = Mathf.Max(_frameV1, v);
                    }
                    _lineU[gi] = u; _lineV[gj] = v; _nodeOk[gi, gj] = node.Ok;
                    grid[gi, gj] = node;
                }

            // ---- 2. SPANS, IN TWO PASSES. ⚠ THE ORDER IS FORCED BY tiny's NODE GEOMETRY: it is handed whole
            // arms and tells you how far to cut each one, so every span's full polyline has to exist BEFORE
            // any road is built. The old single pass built each road as it went and could only ever apply a
            // setback it had guessed itself.
            var plan = new List<Span>();
            for (int axis = 0; axis < 2; axis++)
                for (int line = 0; line < w; line++)
                {
                    bool main = MainLine(axis, line - n);
                    bool boulevard = main && BoulevardLine(axis, line - n);
                    for (int k = 0; k + 1 < w; k++)
                    {
                        int ai = axis == 0 ? k : line, aj = axis == 0 ? line : k;
                        int bi = axis == 0 ? k + 1 : line, bj = axis == 0 ? line : k + 1;
                        if (!InShape(ai - n, aj - n) || !InShape(bi - n, bj - n)) continue;     // past the outline: never planned
                        if (!grid[ai, aj].Ok || !grid[bi, bj].Ok) { rep.SpansRejected++; continue; }
                        var pts = SpanPoints(grid[ai, aj].P, grid[bi, bj].P, heightAt, out float len);
                        if (pts == null) { rep.SpansRejected++; continue; }
                        plan.Add(new Span { AI = ai, AJ = aj, BI = bi, BJ = bj, Axis = axis, Main = main, Boulevard = boulevard,
                                            AxisA = axis, AxisB = axis,
                                            Pts = pts, Len = len, TrimA = 0f, TrimB = 0f });
                    }
                }

            // ---- 2b. THE NODES. Every span touching a node becomes an arm; tiny's geometry cuts them back
            // past their fillets and hands back the patch that paves the middle. Where it declines -- too few
            // arms, one too short to cut, a shape the fillets cannot close -- the span keeps the simple
            // kerb-line setback, which is worse-looking but never leaves a hole.
            // ---- 2a. THE DIAGONAL, before the bends: its crossings split grid spans, and a split span next to
            // a corner is what the bend pass then has to round.
            if (Vary)
            {
                // ---- a varied town: its superblocks, its avenues by the rules, then no loose ends and one network
                rep.Superblocks = SuperblockChance > 0f ? DropSpans(plan, n) : 0;
                DiagonalNodes.Clear();
                var why = new List<string>();
                // ⭐ EACH AVENUE GOES WHERE THE RULES LEAVE IT MOST ROOM: every half-block offset is tried, the seed's slope first,
                // and the longest run of 45-degree road clear of the zones, the outline and the junction gap wins (ties: the seed).
                // A fixed offset ran straight through the core on most seeds and the zone cut it to nothing.
                // A second avenue runs parallel to the first and at least two blocks off it: two that crossed would meet ON a
                // lattice line, a six-way, whatever their offsets.
                int slope0 = _diagonals.Count > 0 ? _diagonals[0].slope : 1;
                var usedK = new List<int>();
                for (int d = 0; d < _diagonals.Count && d < 2; d++)
                {
                    int bestK = int.MinValue, bestBlocks = 0, bestSlope = slope0; ulong bestTie = 0;
                    foreach (int sl in new[] { slope0, -slope0 })
                    {
                        if (d > 0 && sl != slope0) continue;
                        for (int k = -n; k < n; k++)
                        {
                            bool near = false; foreach (int u in usedK) if (Math.Abs(u - k) < 2) near = true;
                            if (near) continue;
                            var pd = PlanDiagonal(plan, sl, (k + 0.5f) * Spacing);
                            int blocks = 0; foreach (bool b in pd.lay) if (b) blocks++;
                            ulong tie = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)(k + 64) << 8) ^ (ulong)(uint)(d * 2 + (sl > 0 ? 1 : 0)) ^ 0xD1A9UL));
                            if (blocks > bestBlocks || (blocks == bestBlocks && blocks > 0 && tie > bestTie))
                            { bestBlocks = blocks; bestK = k; bestSlope = sl; bestTie = tie; }
                        }
                        if (bestBlocks >= MinDiagonalBlocks) break;          // the seed's own slope found room
                    }
                    if (bestBlocks < MinDiagonalBlocks) { why.Add($"#{d + 1}: no offset leaves {MinDiagonalBlocks} blocks clear of the rules"); break; }
                    usedK.Add(bestK); slope0 = bestSlope;
                    rep.DiagonalCrossings += AddDiagonalRuled(plan, grid, w, w + 2 * d, roads, heightAt, bestSlope, (bestK + 0.5f) * Spacing, why);
                }
                if (PruneDeadEnds) rep.Pruned = PruneLoose(plan);
                // a lattice node nothing reaches any more is no node: the plots and their lanes ask _nodeOk
                var reached = new HashSet<(int, int)>();
                foreach (var sp in plan) { reached.Add((sp.AI, sp.AJ)); reached.Add((sp.BI, sp.BJ)); }
                for (int gi = 0; gi < w; gi++)
                    for (int gj = 0; gj < w; gj++)
                        if (_nodeOk[gi, gj] && !reached.Contains((gi, gj))) _nodeOk[gi, gj] = false;
                rep.Layout = LayoutSummary + $"; {rep.Superblocks} superblock(s); " + (why.Count > 0 ? "diagonal " + string.Join(" / ", why) : "no diagonal")
                           + $"; {rep.Pruned} span(s) pruned as loose";
            }
            else if (Diagonal) rep.DiagonalCrossings = AddDiagonal(plan, grid, w, roads, heightAt);

            // ---- 2b. BENDS, BEFORE THE NODES: a corner fused here is not a node any more, so the node pass
            // must never see it -- it would otherwise slab it as a two-arm junction, which is the sharp point.
            if (CurveCorners) rep.Bends = MergeBends(plan, roads);
            if (Vary) rep.Rules = CheckRules(plan, grid);

            var touching = new Dictionary<(int, int), List<(int span, bool atEnd)>>();
            foreach (var (sp, idx) in Enumerate(plan))
            {
                Add(touching, (sp.AI, sp.AJ), (idx, false));
                Add(touching, (sp.BI, sp.BJ), (idx, true));
            }
            Patches.Clear(); _patchBox.Clear();
            foreach (var kv in touching)
            {
                var here = kv.Value;
                var node = grid[kv.Key.Item1, kv.Key.Item2];
                bool done = false;
                if (UseNodePatches && here.Count >= 2)
                {
                    var spokes = new List<SDG.Unturned.InfiniteRoads.TownArm>(here.Count);
                    foreach (var (si, atEnd) in here)
                    {
                        var sp = plan[si];
                        int m = sp.Pts.Count;
                        var xs = new double[m]; var zs = new double[m]; var hs = new float[m];
                        // ⚠ INTO InfiniteRoads' FRAME: its z is the NEGATED world z. Handing it world
                        // coordinates mirrors the node, and a mirror swaps which side is counter-clockwise --
                        // the ordering every fillet in there is built on.
                        for (int q = 0; q < m; q++) { xs[q] = sp.Pts[q].X; zs[q] = -sp.Pts[q].Z; hs[q] = sp.Pts[q].Y; }
                        spokes.Add(new SDG.Unturned.InfiniteRoads.TownArm(xs, zs, hs, atEnd,
                                       roads.RoadHalfWidthOfMaterial(MatOf(sp))));
                    }
                    if (SDG.Unturned.InfiniteRoads.TownNode(spokes, node.P.X, -node.P.Z, out var cuts, out var patch, out var marks,
                                                           stopPastEdge: StopPastEdge,
                                                           lines: LineTableFor(roads.RoadHalfWidthOfMaterial(MainMaterial)),
                                                           acuteRun: CornerKerbRadius)   // the slab turns in R, like the kerb
                        && cuts != null && cuts.Length == here.Count)
                    {
                        for (int q = 0; q < here.Count; q++)
                        {
                            var (si, atEnd) = here[q];
                            var sp = plan[si];
                            if (atEnd) sp.TrimB = (float)cuts[q]; else sp.TrimA = (float)cuts[q];
                            plan[si] = sp;
                        }
                        Patches.Add(patch);
                        if (marks != null) Marks.AddRange(marks);
                        rep.Patched++;
                        done = true;
                    }
                }
                if (!done)
                    foreach (var (si, atEnd) in here)
                    {
                        var sp = plan[si];
                        float back = node.Extra
                            // a diagonal crossing: the grid road runs on, the diagonal stops clear of it -- and at
                            // 45 degrees "clear" is half the width over sin 45, not half the width
                            ? (AxisAt(sp, atEnd) == 2 ? node.CrossHalf / Mathf.Sin(Mathf.Pi * 0.25f) : 0f)
                            : DominantAxis(kv.Key.Item1 - n, kv.Key.Item2 - n) == AxisAt(sp, atEnd)
                                   ? 0f : HalfAt(roads, kv.Key.Item1 - n, kv.Key.Item2 - n);
                        if (atEnd) sp.TrimB = back; else sp.TrimA = back;
                        plan[si] = sp;
                    }

                // ⚠⚠ A MAIN RUNS THROUGH A JUNCTION, it does not stop at it and start again on the far side
                // (strawberry 2026-10-12: "couldnt we just have the main road spline continue through this
                // intersection instead?"). Every arm used to be cut back behind its fillet and the patch
                // left to pave the middle, so a main crossing five junctions had TEN cut ends -- and a cut
                // end is exactly where the slab can fall short of the asphalt and show ground through. Left
                // uncut, the two halves of a through main MEET at the node: collinear, sharing the node
                // point, so the carriageway is continuous underneath and there is real road wherever the
                // patch is a few millimetres shy. The patch still paves the top, so nothing looks different
                // where it already fitted -- this only removes what the gaps were made of.
                // ⚠ ONLY A MAIN, AND ONLY STRAIGHT THROUGH. Leaving CROSSING arms uncut would overlap two
                // coplanar ribbons at right angles in the middle of the node, which is a z-fight, not a fix.
                // ⚠ WHO IS CROSSED, AND WHICH WAY, is decided HERE -- the plan still knows each arm's axis and
                // class -- and carried to the crossings on the span. The UG_TOWNCUTMAIN control still flags
                // the through pair: that control is about the geometry of the cut, not about the markings.
                bool runsThrough = ThroughMain(plan, here, out int ta, out int tb);
                if (runsThrough)
                    foreach (int q in new[] { ta, tb })
                    {
                        var sp = plan[here[q].Item1];
                        if (here[q].Item2) sp.ThroughB = true; else sp.ThroughA = true;
                        plan[here[q].Item1] = sp;
                    }
                int priAxis = here.Count >= 3 ? PriorityAxis(plan, here) : -1;
                if (priAxis >= 0)
                {
                    var priDir = Vector3.Zero;
                    foreach (var (si, atEnd) in here)
                        if (AxisAt(plan[si], atEnd) == priAxis) { priDir = PlanDir(plan[si], atEnd); break; }
                    if (priDir != Vector3.Zero)
                        for (int q = 0; q < here.Count; q++)
                        {
                            var (si, atEnd) = here[q];
                            if (AxisAt(plan[si], atEnd) == priAxis) continue;    // the line itself is crossed square
                            var sp = plan[si];
                            if (atEnd) sp.AlongB = priDir; else sp.AlongA = priDir;
                            plan[si] = sp;
                        }
                }
                if (!CutMain && runsThrough)
                {
                    var spT = plan[here[ta].Item1];
                    foreach (int q in new[] { ta, tb })
                    {
                        var sp = plan[here[q].Item1];
                        if (here[q].Item2) sp.TrimB = 0f; else sp.TrimA = 0f;
                        plan[here[q].Item1] = sp;
                    }
                    // ⚠⚠ AND TAKE THE SLAB OFF IT. Running the main through is only half the change: the
                    // patch still covers the corridor, and the patch is PLAIN TARMAC, so the junction went
                    // blank. Cut the corridor out and the main's own ribbon is the surface, lines and all.
                    if (done && Patches.Count > 0 && spT.Pts != null && spT.Pts.Count >= 2)
                    {
                        var ptsT = spT.Pts;
                        var tipT = here[ta].Item2 ? ptsT[ptsT.Count - 1] : ptsT[0];
                        var prvT = here[ta].Item2 ? ptsT[ptsT.Count - 2] : ptsT[1];
                        double ddx = tipT.X - prvT.X, ddz = (-tipT.Z) - (-prvT.Z);
                        double dlen = Math.Sqrt(ddx * ddx + ddz * ddz);
                        if (dlen > 1e-6)
                        {
                            ddx /= dlen; ddz /= dlen;
                            double hwT = roads.RoadHalfWidthOfMaterial(MatOf(spT));
                            var oldP = Patches[Patches.Count - 1];
                            var cut = ClipCorridor(oldP, node.P.X, -node.P.Z, -ddz, ddx, hwT);
                            // ⚠ THE PAINT IS KEYED ON THE PATCH OBJECT. Every mark remembers which patch it
                            // rides on and BuildMarkMeshes looks its height shift up by that reference -- so
                            // swapping the patch without re-pointing them drops every line at this node to a
                            // zero shift, which is the paint sunk under the asphalt rather than on it.
                            foreach (var mk in Marks) if (mk != null && ReferenceEquals(mk.On, oldP)) mk.On = cut;
                            ClipsTried++;
                            if (cut != null)
                            {
                                Patches[Patches.Count - 1] = cut;
                                ClipTrisBefore += oldP.Tris.Length / 3; ClipTrisAfter += cut.Tris.Length / 3;
                                _clips.Add((cut, node.P.X, -node.P.Z, -ddz, ddx, hwT));
                            }
                            else { Patches.RemoveAt(Patches.Count - 1); ClipsEmptied++; }
                        }
                    }
                }
            }

            // ---- 2c. EMIT. Each span cut by whatever its two ends asked for, then bound at the NODE while
            // its geometry stops at the cut.
            var endsAt = new Dictionary<int, int>();   // junction -> bound ends, for the topology census
            void Bind(ref Node nd, int road, bool atEnd, Vector3 geomAt)
            {
                if (nd.Junction < 0) nd.Junction = roads.AddJunction(nd.P);
                roads.BindRoadEndAt(road, atEnd, nd.Junction, geomAt);   // bound at the NODE, drawn to the KERB
                endsAt[nd.Junction] = endsAt.TryGetValue(nd.Junction, out int c) ? c + 1 : 1;
            }
            foreach (var sp in plan)
            {
                // ⚠⚠ RUN EACH ARM A LITTLE INTO THE PATCH. Its end cap is suppressed (the ramp dipped under
                // the junction), which leaves the slab OPEN -- and an open end is a 22 cm hole you see into
                // at any grazing angle: a thin dark line across every join. master, with the heights already
                // matching to 2 mm and the surfaces meeting to 7 mm in plan: "the seams are still there".
                // Overlapping by a handful of centimetres hides the opening under the patch, and the patch
                // sits PatchLift above, so the two cannot z-fight over the shared strip.
                // ⚠⚠ A ROUNDED CORNER KEEPS ITS OWN SPACING HERE. CutBack respaces to PointStep (12 m), and this
                // is the LAST stage before the road is built -- so it silently undid the 4 m respacing MergeBends
                // gave the arc, leaving a 35 m quarter-circle on three 12 m chords. Catmull-Rom across that
                // overshot by 33 cm and turned against the bend at the joins: exactly master's "are the curves
                // overshooting and correcting as they connect". The stage that runs last wins.
                var pts = CutBack(sp.Pts, Mathf.Max(0f, sp.TrimA - PatchOverlap), Mathf.Max(0f, sp.TrimB - PatchOverlap),
                                  sp.Bent ? 0f : -1f);
                if (pts == null) { rep.SpansRejected++; rep.CutAway++; continue; }
                int road = roads.AddRoadFromPolyline(pts, MatOf(sp),
                                                    loop: false, ignoreTerrain: !FollowTerrain);
                if (road < 0) { rep.SpansRejected++; continue; }
                ref Node a = ref grid[sp.AI, sp.AJ];
                ref Node b = ref grid[sp.BI, sp.BJ];
                Bind(ref a, road, false, pts[0]);
                Bind(ref b, road, true, pts[^1]);
                Built.Add(road); _profiles.Add(pts);
                if (sp.Axis == 2) DiagonalRoads.Add(road);
                if (sp.Bent) _bentRoads.Add((road, sp.BendId));
                if (sp.ThroughA) _throughEnds.Add((road, false));
                if (sp.ThroughB) _throughEnds.Add((road, true));
                if (sp.AlongA != Vector3.Zero) _crossAlong[(road, false)] = sp.AlongA;
                if (sp.AlongB != Vector3.Zero) _crossAlong[(road, true)] = sp.AlongB;
                // ⚠ THE LENGTH THAT WAS BUILT, not the length that was planned. Reporting sp.Len counted the
                // whole span including the part the junction ate, so the total went UP when the cuts came in.
                float built = 0f;
                for (int q = 0; q + 1 < pts.Count; q++) built += Flat(pts[q], pts[q + 1]);
                rep.Roads++; rep.Metres += built;
                foreach (var p in pts) rep.Checksum = Mix(rep.Checksum ^ (ulong)BitConverter.SingleToInt32Bits(p.X)
                                                ^ ((ulong)BitConverter.SingleToInt32Bits(p.Y) << 21)
                                                ^ ((ulong)BitConverter.SingleToInt32Bits(p.Z) << 42));
            }

            // ⚠ REBUILD ONCE EVERY BIND IS IN. Whether an end ramps depends on how many roads share its node,
            // and the first road bound to a node sees a count of one -- its mesh was built before its
            // neighbours existed. Without this pass the first arm at every junction keeps a ramp nobody can
            // see the cause of.
            foreach (int r in Built) roads.RebuildRoad(r);

            // ⚠⚠ BEND FIDELITY: the BUILT road against the INTENDED curve. master: "are the curves
            // overshooting and correcting as they connect lol?" -- and the town test could not answer, because
            // it only bounded the heading change over 2 m, which catches a KINK. An overshoot is a different
            // shape: the road turns the WRONG way for a few metres, by small amounts at every step, then swings
            // back. So this asks the two questions that shape answers to: how far does the built centreline
            // stray from the true straight-arc-straight path, and does it ever turn against the bend.
            if (_bentRoads.Count > 0)
            {
                float worstOff = 0f, worstRev = 0f; string offAt = "", revAt = "";
                foreach (var (road, bid) in _bentRoads)
                {
                    var g = _bendGeom[bid];
                    float L = roads.RoadLength(road);
                    const float Step = 0.25f;
                    float prevH = float.NaN, total = 0f;
                    var dth = new List<(float d, float v)>();
                    for (float d = 0f; d <= L; d += Step)
                    {
                        if (!roads.EvaluateAlong(road, d, out var p, out var tg, snapTerrain: false)) continue;
                        var q = new Vector3(p.X, 0f, p.Z);
                        float off;
                        var S0 = new Vector3(g.S.X, 0f, g.S.Z); var E0 = new Vector3(g.E.X, 0f, g.E.Z);
                        if ((q - S0).Dot(g.dIn) < 0f)            // still on the incoming straight
                        { var rel = q - S0; off = (rel - g.dIn * rel.Dot(g.dIn)).Length(); }
                        else if ((q - E0).Dot(g.dOut) > 0f)      // already on the outgoing straight
                        { var rel = q - E0; off = (rel - g.dOut * rel.Dot(g.dOut)).Length(); }
                        else off = Mathf.Abs((q - g.C).Length() - g.R);   // on the arc
                        if (off > worstOff) { worstOff = off; offAt = $"road {road} at {d:0.0}/{L:0} m"; }
                        float h = Mathf.Atan2(tg.Z, tg.X);
                        if (!float.IsNaN(prevH)) { float dh = Mathf.Wrap(h - prevH, -Mathf.Pi, Mathf.Pi); dth.Add((d, dh)); total += dh; }
                        prevH = h;
                    }
                    float sign = Mathf.Sign(total);
                    foreach (var (d, dh) in dth)
                    {
                        float rev = -sign * dh / Step;            // rad per metre turned AGAINST the bend
                        if (rev > worstRev) { worstRev = rev; revAt = $"road {road} at {d:0.0}/{L:0} m"; }
                    }
                }
                Log.Print($"[towngrid] bend fidelity: {_bentRoads.Count} rounded corners; worst stray from the true arc "
                        + $"{worstOff * 100f:0.0} cm ({offAt}); worst turn AGAINST the bend {Mathf.RadToDeg(worstRev):0.00} deg/m"
                        + (worstRev > 0f ? $" ({revAt})" : ""));
                BendWorstOffCm = worstOff * 100f; BendWorstReverseDegPerM = Mathf.RadToDeg(worstRev);
            }

            SnapClippedEdges(roads);

            // ---- 3. CENSUS. Reported by DEGREE, because "47 junctions" says nothing about whether the thing
            // is a grid: a lattice that failed to bind would report the same count as one that worked, all of
            // it dead ends.
            foreach (var kv in endsAt)
            {
                rep.Junctions++;
                switch (kv.Value)
                {
                    case >= 4: rep.Crossroads++; break;
                    case 3: rep.Tees++; break;
                    case 2: rep.Corners++; break;
                    default: rep.DeadEnds++; break;
                }
            }
            return rep;
        }

        /// <summary>Half the carriageway of the street that OWNS this node -- how far back the other arms
        /// have to stop.</summary>
        float HalfAt(RoadField roads, int i, int j)
        {
            int ax = DominantAxis(i, j);
            bool mainHere = ax == 0 ? MainLine(0, j) : MainLine(1, i);
            bool boulHere = ax == 0 ? BoulevardLine(0, j) : BoulevardLine(1, i);
            return roads.RoadHalfWidthOfMaterial(boulHere ? BoulevardMaterial : mainHere ? MainMaterial : StreetMaterial);
        }

        /// <summary>One span's spline points: terrain-following, graded, ends pinned to the node positions.
        /// Returns null when the span is too steep for a street -- the caller leaves those two nodes
        /// unconnected rather than building a road nobody could drive.</summary>
        List<Vector3> SpanPoints(Vector3 a, Vector3 b, Func<float, float, float> heightAt, out float len)
        {
            len = new Vector2(b.X - a.X, b.Z - a.Z).Length();
            if (len < 4f) return null;
            if (Mathf.Abs(b.Y - a.Y) / len > MaxGrade) return null;

            int steps = Mathf.Max(1, Mathf.RoundToInt(len / Mathf.Max(2f, PointStep)));
            var ys = new float[steps + 1];
            var pts = new List<Vector3>(steps + 1);
            float sink = StreetSink;      // the nodes it is pinned to are laid this far under the ground too
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float x = Mathf.Lerp(a.X, b.X, t), z = Mathf.Lerp(a.Z, b.Z, t);
                float ground = heightAt(x, z);
                ys[i] = ground - sink;
                // ⚠ THE WATER GATE BELONGS ON THE SPAN, NOT ONLY ON ITS ENDS. Two nodes on dry ground either
                // side of a creek both pass, and the street between them runs along the creek bed. Checking
                // the ends is the cheap test that looks like the real one.
                if (ground <= SeaLevel + SeaMargin) return null;
                pts.Add(new Vector3(x, 0f, z));
            }
            // Grade it, then PIN BOTH ENDS BACK. A smoothing pass that moved an end would pull the road off
            // the junction node it is about to be bound to, and the two streets meeting there would part by
            // however much the filter happened to shift them.
            for (int pass = 0; pass < Grade; pass++)
            {
                var src = (float[])ys.Clone();
                for (int i = 1; i < ys.Length - 1; i++) ys[i] = (src[i - 1] + src[i] * 2f + src[i + 1]) * 0.25f;
            }
            ys[0] = a.Y; ys[^1] = b.Y;
            // ...and re-check the graded profile: smoothing can leave a step at the pinned ends that the raw
            // samples did not have.
            for (int i = 1; i < ys.Length; i++)
            {
                float dx = new Vector2(pts[i].X - pts[i - 1].X, pts[i].Z - pts[i - 1].Z).Length();
                if (dx > 0.01f && Mathf.Abs(ys[i] - ys[i - 1]) / dx > MaxGrade * 1.5f) return null;
            }
            for (int i = 0; i < pts.Count; i++) pts[i] = new Vector3(pts[i].X, ys[i], pts[i].Z);
            return pts;
        }

        /// <summary>Lay a mesh for every junction patch -- the asphalt that paves the middle of a node now
        /// that the arms stop at its fillets. ⚠ WITHOUT THIS THE CUTS ARE A REGRESSION: trimmed arms and no
        /// patch is a hole at every junction, which is worse than the overlap it replaced.</summary>
        public int BuildPatchMeshes(Node3D parent, RoadField roads)
        {
            if (parent == null || roads == null || Patches.Count == 0) return 0;
            var mat = roads.MaterialOfMaterial(MainMaterial);
            float half = roads.RoadHalfWidthOfMaterial(MainMaterial);
            // ⚠⚠ THE PATCH SITS AT THE ASPHALT, AND THE SHIFT IS MEASURED, NOT ASSUMED (strawberry: "theres
            // a slight seam between the combined mesh and the road splines").
            //
            // Two lifts are in play and they are not the same number. RoadField's drivable surface is
            // RoadSurfaceOffset (0.220 m here) above the joints the spline was built from -- and tiny's
            // NodePatch ALREADY raises its own surface above the arm heights it was handed, by 0.130 m.
            // Adding RoadField's on top double-counted, which measured as a 13.2 cm lip; the first guess
            // (add the whole offset) and the second (blame Lift) were both wrong, and only printing the two
            // heights side by side said why.
            //
            // So each patch is FITTED to the asphalt it actually meets: find a road end bound into it, and
            // shift the patch by whatever makes the two agree there. That survives either side changing its
            // mind about its own lift, which a hardcoded 0.09 would not.
            float surf = roads.RoadSurfaceOffsetOfMaterial(MainMaterial);
            var fit = FitShifts(roads, surf);
            // ⚠ THE PLAIN-ASPHALT BAND: road_0 is dashes and a yellow centre line running ALONG it, so
            // tiling the texture whole over an intersection paints lane markings across the junction.
            // RegionStreamer samples only u 4/256..56/256 -- the asphalt between the left edge and the first
            // dash -- and MIRRORS it so it tiles with no seam. Same band, same reason.
            const float U0 = 4f / 256f, U1 = 56f / 256f;
            float tile = (U1 - U0) * 2f * 2f * Mathf.Max(1f, half);   // metres across one mirrored tile
            int made = 0;
            foreach (var p in Patches)
            {
                if (p?.Tris == null || p.Tris.Length < 3 || p.X == null) continue;
                float shift = fit.TryGetValue(p, out float f) ? f : surf;   // no joined end found: fall back to the slab offset
                var st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                var V = new Vector3[p.X.Length];
                var UV = new Vector2[p.X.Length];
                for (int i = 0; i < V.Length; i++)
                {
                    // back out of InfiniteRoads' frame: its z is the negated world z.
                    // ⚠ Lift IS NOT AN ADDEND. RegionStreamer uses it as a FLOOR against the ground --
                    // max(Y, terrainHere + 0.02 + Lift) -- for the interior vertices that are free to ride up
                    // over whatever the terrain does. Adding it instead pushed the middle of every patch 15 cm
                    // above the asphalt feeding it, which is the seam measured rather than guessed at.
                    V[i] = new Vector3((float)p.X[i], p.Y[i] + shift + PatchLift, -(float)p.Z[i]);
                    float tu = ((float)(p.X[i] - p.AnchorX)) / tile, tv = ((float)(-p.Z[i] + p.AnchorZ)) / tile;
                    UV[i] = new Vector2(U0 + PingPong(tu) * (U1 - U0), tv);
                }
                for (int t = 0; t + 2 < p.Tris.Length; t += 3)
                {
                    int a = p.Tris[t], b = p.Tris[t + 1], c = p.Tris[t + 2];
                    if (a >= V.Length || b >= V.Length || c >= V.Length) continue;
                    // ⚠ ORIENT BY THE WANTED NORMAL, never by the index order as given: the frame flip above
                    // MIRRORS the patch, and a mirror reverses winding -- the surface would be back-facing and
                    // simply not drawn, which is the same failure the left-hand pavement had.
                    bool keep = (V[c] - V[a]).Cross(V[b] - V[a]).Y >= 0f;
                    int i1 = keep ? b : c, i2 = keep ? c : b;
                    // ⚠⚠ NORMAL UP, EXPLICITLY -- not GenerateNormals. A node's patch is a FAN of triangles
                    // radiating from the middle, and per-face normals over vertices that are not exactly
                    // coplanar light each wedge of that fan slightly differently: dark wedges spreading from
                    // the junction, which read as holes beside every mouth. They are not holes -- a coverage
                    // sweep of the junctions found 0 enclosed bare points in 52,800 -- they are the fan's own
                    // shading. RegionStreamer sets Vector3.Up on its patches for the same reason.
                    st.SetNormal(Vector3.Up); st.SetUV(UV[a]); st.AddVertex(V[a]);
                    st.SetNormal(Vector3.Up); st.SetUV(UV[i1]); st.AddVertex(V[i1]);
                    st.SetNormal(Vector3.Up); st.SetUV(UV[i2]); st.AddVertex(V[i2]);
                }
                // ⚠⚠ THE PATCH'S EDGE BEVEL, which I skipped when first building these and which is what
                // master kept seeing: "theres big gaps between the road spline and the merged mesh area".
                // They are not gaps -- a coverage sweep found 0 enclosed bare points in 52,800 -- and they
                // are not shadows either, since they survive with every prop removed. A patch's open edge is
                // a 9 cm cliff standing on ground that GradeTerrain never conformed (it conforms to the
                // ROADS), so you see the drop and the dark band under it all the way round each mouth.
                // RegionStreamer hangs the same bevel on its patches; the data for it has been sitting in
                // p.Skirts the whole time.
                if (p.Skirts != null)
                {
                    float bev = 2f * SDG.Unturned.InfiniteRoads.Thickness(SDG.Unturned.RoadKind.Main);
                    float run = SDG.Unturned.InfiniteRoads.BevelRun(SDG.Unturned.RoadKind.Main);
                    var down = new Vector3(0f, -bev, 0f);
                    for (int sk = 0; sk < p.Skirts.Length; sk++)
                    {
                        var ids = p.Skirts[sk];
                        if (ids == null) continue;
                        for (int q = 0; q + 1 < ids.Length; q++)
                        {
                            if (ids[q] >= V.Length || ids[q + 1] >= V.Length) continue;
                            // ⚠ the outward normals are in InfiniteRoads' frame: z negates coming out of it
                            var na = new Vector3(p.SkirtNX[sk][q], 0f, -p.SkirtNZ[sk][q]);
                            var nb = new Vector3(p.SkirtNX[sk][q + 1], 0f, -p.SkirtNZ[sk][q + 1]);
                            Vector3 a2 = V[ids[q]], b2 = V[ids[q + 1]];
                            Vector3 fa = a2 + na * run + down, fb = b2 + nb * run + down;
                            var la = (na * (bev / run) + Vector3.Up).Normalized();
                            var lb = (nb * (bev / run) + Vector3.Up).Normalized();
                            void Face(Vector3 x, Vector3 y2, Vector3 z2, Vector3 nx, Vector3 ny, Vector3 nz)
                            {
                                bool keep2 = (z2 - x).Cross(y2 - x).Dot(nx) >= 0f;
                                st.SetNormal(nx); st.SetUV(UV[ids[q]]); st.AddVertex(x);
                                st.SetNormal(keep2 ? ny : nz); st.SetUV(UV[ids[q]]); st.AddVertex(keep2 ? y2 : z2);
                                st.SetNormal(keep2 ? nz : ny); st.SetUV(UV[ids[q]]); st.AddVertex(keep2 ? z2 : y2);
                            }
                            Face(fa, a2, fb, la, la, lb);
                            Face(a2, b2, fb, la, lb, lb);
                        }
                    }
                }
                var mesh = st.Commit();
                if (mesh == null || mesh.GetSurfaceCount() == 0) continue;
                var mi = new MeshInstance3D { Name = $"Junction_Patch_{made}", Mesh = mesh };
                if (mat != null) mi.MaterialOverride = mat;
                parent.AddChild(mi);
                // Drivable, so it needs a collider and the road's own surface tag -- an untagged junction
                // would give tyres and footsteps the default while the asphalt either side gives concrete.
                var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
                mi.AddChild(body);
                made++;
            }
            // ⚠ MEASURE THE SEAM, because "slight" is not a number and a 2 cm lip and a 22 cm one look the
            // same from 30 m up. For every street end that is bound into a junction, compare the asphalt's
            // height there with the patch's surface at the same spot.
            float worst = 0f; int probed = 0; string worstWhy = "";
            foreach (int r in Built)
                for (int e = 0; e < 2; e++)
                {
                    bool atEnd = e == 1;
                    if (roads.RoadEndJunction(r, atEnd) < 0) continue;
                    // snapTerrain:false for clarity -- a GRADED town's joints are ignoreTerrain anyway, so
                    // PosAlong skips the snap either way and both calls agree. (Checked: changing it moved
                    // the number by nothing, which is how that was established rather than assumed.)
                    if (!roads.EvaluateAlong(r, atEnd ? roads.RoadLength(r) : 0f, out var tip, out _, snapTerrain: false)) continue;
                    float road = tip.Y + roads.RoadSurfaceOffset(r);
                    foreach (var p in Patches)
                    {
                        if (p.DistanceTo(tip.X, -tip.Z) > 0.5) continue;
                        float h = p.HeightAt(tip.X, -tip.Z);
                        if (float.IsNaN(h)) continue;
                        float d = Mathf.Abs(h + (fit.TryGetValue(p, out float pf) ? pf : surf) + PatchLift - road);
                        if (d > worst) { worst = d; worstWhy = $"road {r} asphalt {road:0.000}; patch {h:0.000} fitted by {(fit.TryGetValue(p, out float pg) ? pg : surf):0.000}"; }
                        probed++;
                        break;
                    }
                }
            // ⚠⚠ AND THE SEAM IN PLAN, which the height probe above cannot see. Two surfaces can agree to a
            // millimetre in Y and still leave a stripe of ground between them, or overlap into a double
            // edge. master: "the seams are still there" with the vertical mismatch already at 2 mm -- so the
            // gap that is left is a HORIZONTAL one. Sample across the road's full width at its cut and ask
            // the patch whether it reaches that far.
            float worstGap = 0f; int gapProbes = 0; string gapWhy = "";
            foreach (int r in Built)
                for (int e = 0; e < 2; e++)
                {
                    bool atEnd = e == 1;
                    int jg = roads.RoadEndJunction(r, atEnd);
                    if (jg < 0) continue;
                    // ⚠ A LOOSE END IS NOT A SEAM. Every span end gets a junction node even where the town
                    // simply stops, and a degree-1 node has no junction paving for the arm to meet -- so
                    // this probe was measuring the distance from the edge of town to the nearest patch and
                    // calling 9.2 m a gap. The seam it exists to find is between an arm and what PAVES its
                    // node; where nothing does, there is nothing to be flush with.
                    int deg = roads.JunctionEdges(jg).Count;
                    if (deg < 2) continue;
                    float L = roads.RoadLength(r);
                    if (!roads.EvaluateAlong(r, atEnd ? L : 0f, out var tip, out var tan, snapTerrain: false)) continue;
                    // ⚠ AND A THROUGH ARM HAS NO CUT. Its end sits ON the node and its carriageway simply
                    // carries on as the next block, with the slab deliberately cut away along it -- so
                    // "how far is the nearest patch" answers a corridor half-width (9.2 m) and means
                    // nothing. The seam this probe is for is where an arm STOPS and paving takes over.
                    var jpg = roads.JunctionPos(jg);
                    if (new Vector2(tip.X - jpg.X, tip.Z - jpg.Z).Length() < 0.5f) continue;
                    var right = new Vector3(tan.Z, 0f, -tan.X).Normalized();
                    float hw = roads.RoadHalfWidth(r);
                    for (int k = -2; k <= 2; k++)
                    {
                        var q = tip + right * (hw * k / 2f);
                        double best = double.MaxValue;
                        best = NearestPatch(q.X, -q.Z);
                        // ⚠⚠ OR BY ANOTHER ROAD. A THROUGH arm's cut meets its own CONTINUATION, not a
                        // patch -- the slab is cut away along the main on purpose now -- so asking only the
                        // patches reported a 9.2 m "gap" straight across a junction that is solid asphalt.
                        // The question this probe exists to ask is whether the cut meets PAVING, of any kind.
                        if (best > 0 && roads.DeepestRoadOverlap(q, 0f, out _, out _, skipRoad: r)) best = 0;
                        if (best > worstGap) { worstGap = (float)best; gapWhy = $"road {r} {(atEnd ? "end" : "start")} at lateral {hw * k / 2f:0.0} m, node degree {deg}"; }
                        gapProbes++;
                    }
                }
            PaintPieces = BuildMarkMeshes(parent, roads, fit);   // counted separately: paint is not pavement
            CrossingPieces = BuildCrossings(parent, roads) + BuildMainThroughLines(parent, roads);
            // ⚠⚠ COVERAGE, NOT JUST THE SEAM AT THE CUT. The seam probe samples the road's END cross-section
            // and reported 0.0 cm while master could see holes -- because a hole BESIDE the mouth, between a
            // fillet and a road's flank, is nowhere near that line.
            // ⚠ AND "BARE" IS NOT "A HOLE": most of a junction's surroundings is grass by design, and a first
            // version of this called 20% of the area a defect. A hole is bare ground ENCLOSED by paving, so
            // the test is whether a bare point's neighbours are covered -- which is what you can see.
            // the slab-alone pass is diagnostic only (UG_TOWNPROBES=1): the pass that matters runs once the
            // corner asphalt is down, from BuildTownGrid
            if (ProbeEverything) ProbeCoverage(roads, "slab alone");
            // ⚠ THE THROUGH-MAIN CHANGE IS INVISIBLE BY DESIGN -- it puts real carriageway UNDER the patch,
            // so a render of a junction whose patch already fitted looks identical (master, of exactly that
            // render: "nothing changed in the pic"). What it changes is whether there is anything beneath
            // the slab, which only a measurement can show. Walk each through main's centreline across its
            // node and ask how much of it is backed by ROAD rather than by the patch alone.
            {
                int nodes = 0; float backed = 0f, total = 0f;
                for (int j5 = 0; j5 < roads.JunctionCount; j5++)
                {
                    var e5 = roads.JunctionEdges(j5);
                    if (e5.Count < 3) continue;
                    int mainRoad = -1; float mhw = 0f;
                    foreach (var (r5, x5) in e5) if (_throughEnds.Contains((r5, x5))) { mainRoad = r5; mhw = roads.RoadHalfWidth(r5); }
                    if (mainRoad < 0) continue;
                    var jp5 = roads.JunctionPos(j5);
                    if (!roads.EvaluateAlong(mainRoad, roads.RoadLength(mainRoad) * 0.5f, out _, out var mt, snapTerrain: false)) continue;
                    var dir = new Vector3(mt.X, 0f, mt.Z).Normalized();
                    nodes++;
                    for (float d5 = -mhw; d5 <= mhw; d5 += 0.25f)
                    {
                        total += 1f;
                        if (roads.DeepestRoadOverlap(jp5 + dir * d5, 0f, out _, out _)) backed += 1f;
                    }
                }
                // ⚠ AND WHICH SURFACE ACTUALLY WINS AT THE NODE. Running the main through only helps if its
                // ribbon is the thing you see; if the patch sits over it the box goes blank, because the
                // patch is plain tarmac and the painted lines ride on the PATCH's height, not the road's.
                for (int j6 = 0; j6 < roads.JunctionCount && j6 < 60; j6++)
                {
                    var e6 = roads.JunctionEdges(j6);
                    if (e6.Count < 3) continue;
                    int mr = -1;
                    foreach (var (r6, x6) in e6) if (_throughEnds.Contains((r6, x6))) mr = r6;
                    if (mr < 0) continue;
                    var jp6 = roads.JunctionPos(j6);
                    float ribbon = float.NaN;
                    if (roads.NearestPointOnSpline(jp6, 60f, out int nr6, out _, out _, out var np6)) ribbon = np6.Y;
                    float patchY = float.NaN; float shift6 = 0f;
                    foreach (var pp in Patches)
                        if (pp.DistanceTo(jp6.X, -jp6.Z) <= 0.01)
                        { patchY = pp.HeightAt(jp6.X, -jp6.Z); fit.TryGetValue(pp, out shift6); break; }
                    Log.Print($"[towngrid] node {j6} surfaces: nearest ribbon (road {nr6}) {ribbon:0.000}, "
                            + $"patch {patchY + shift6 + PatchLift:0.000} (raw {patchY:0.000} + fit {shift6:0.000} + lift {PatchLift:0.000}), "
                            + $"paint {patchY + shift6 + PatchLift + MarkLift:0.000} -- "
                            + (patchY + shift6 + PatchLift > ribbon ? "PATCH WINS" : "RIBBON WINS"));
                    break;
                }
                if (nodes > 0)
                    Log.Print($"[towngrid] through-main backing: {backed / total * 100f:0}% of the main's centreline "
                            + $"across {nodes} junctions is REAL CARRIAGEWAY{(CutMain ? "  (UG_TOWNCUTMAIN control)" : "")}");
            }
            Log.Print($"[towngrid] corridor clip: {ClipsTried} nodes cut, {ClipsEmptied} left with NO patch at all, "
                    + $"{ClipTrisBefore} tris -> {ClipTrisAfter}");
            Log.Print($"[towngrid] patch/road seam: height {worst * 100f:0.0} cm, IN-PLAN GAP {worstGap * 100f:0.0} cm "
                    + $"over {probed}/{gapProbes} probes (surf {surf:0.000}, lift {PatchLift:0.000}) -- {worstWhy}; widest gap {gapWhy}");
            return made;
        }

        /// <summary>Where does the GROUND come up through the paving? Every street and back lane is walked a metre at a
        /// time and across its carriageway a metre at a time, and every junction slab at each triangle's corners and
        /// middle; at each point <paramref name="ground"/> is compared with the surface laid there. A point counts as a
        /// poke when the ground is within <paramref name="clear"/> of the surface or above it -- the depth buffer cannot
        /// keep two surfaces apart much closer than that at a distance. Worst is the most the ground stands proud of the
        /// paving (negative: everything clears it), and where, and over what.
        /// ⚠ The SAME probe drives the hills test and the render's log, so the number the test holds is the number a
        /// render prints.</summary>
        public (int samples, int pokes, float worst, Vector3 at, string what) GroundPokes(RoadField roads, Func<float, float, float> ground,
                                                                                         float clear = 0.03f)
        {
            int samples = 0, pokes = 0; float worst = float.NegativeInfinity; var at = Vector3.Zero; string what = "nothing";
            void Probe(float x, float z, float surf, string over)
            {
                float g = ground(x, z);
                if (float.IsNaN(g) || float.IsNaN(surf)) return;
                samples++;
                float proud = g - surf;
                if (proud > -clear) pokes++;
                if (proud > worst) { worst = proud; at = new Vector3(x, surf, z); what = over; }
            }
            foreach (var (list, kind) in new[] { (Built, "street"), (LaneRoads, "lane") })
                foreach (int r in list)
                {
                    float L = roads.RoadLength(r), hw = roads.RoadHalfWidth(r) - 0.25f, lift = roads.RoadSurfaceOffset(r);
                    for (float s = 0f; s <= L; s += 1f)
                    {
                        if (!roads.EvaluateAlong(r, s, out var p, out var t, snapTerrain: false)) continue;
                        var side = new Vector3(t.Z, 0f, -t.X).Normalized();
                        int across = Mathf.Max(1, Mathf.CeilToInt(2f * hw));
                        for (int c = 0; c <= across; c++)
                        {
                            float lat = -hw + 2f * hw * c / across;
                            var q = p + side * lat;
                            Probe(q.X, q.Z, p.Y + lift, $"{kind} {r} at {s:0} m, {lat:+0.0;-0.0} m across");
                        }
                    }
                }
            var fit = FitShifts(roads, roads.RoadSurfaceOffsetOfMaterial(MainMaterial));
            for (int k = 0; k < Patches.Count; k++)
            {
                var pp = Patches[k];
                if (pp?.Tris == null || pp.X == null) continue;
                float shift = fit.TryGetValue(pp, out float f) ? f : 0f;
                for (int t = 0; t + 2 < pp.Tris.Length; t += 3)
                {
                    int i0 = pp.Tris[t], i1 = pp.Tris[t + 1], i2 = pp.Tris[t + 2];
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= pp.X.Length || i1 >= pp.X.Length || i2 >= pp.X.Length) continue;
                    // the corners pulled a few centimetres in, so a corner the slab shares with its arm reads the slab
                    float cx = (float)(pp.X[i0] + pp.X[i1] + pp.X[i2]) / 3f, cz = (float)(pp.Z[i0] + pp.Z[i1] + pp.Z[i2]) / 3f;
                    float cy = (pp.Y[i0] + pp.Y[i1] + pp.Y[i2]) / 3f;
                    Probe(cx, -cz, cy + shift + PatchLift, $"slab {k} triangle {t / 3} middle");
                    foreach (int ix in new[] { i0, i1, i2 })
                    {
                        float x = Mathf.Lerp((float)pp.X[ix], cx, 0.1f), z = Mathf.Lerp((float)pp.Z[ix], cz, 0.1f);
                        Probe(x, -z, Mathf.Lerp(pp.Y[ix], cy, 0.1f) + shift + PatchLift, $"slab {k} triangle {t / 3} corner");
                    }
                }
            }
            return (samples, pokes, worst, at, what);
        }

        /// <summary>DIAGNOSTIC: what is around a point -- the streets within 25 m (how far off, their surface at the nearest
        /// point, and the grading target their profile line gives there), and the ground.</summary>
        public string ExplainPoint(RoadField roads, Vector3 at, Func<float, float, float> ground)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"ground {ground(at.X, at.Z):0.000} under a surface at {at.Y:0.000};");
            var near = new List<(float d, string s)>();
            for (int i = 0; i < Built.Count; i++)
            {
                int r = Built[i];
                float L = roads.RoadLength(r), bestD = float.MaxValue, bestS = 0f; var bestP = Vector3.Zero;
                for (float s = 0f; s <= L; s += 0.25f)
                {
                    if (!roads.EvaluateAlong(r, s, out var p, out _, snapTerrain: false)) continue;
                    float d = new Vector2(p.X - at.X, p.Z - at.Z).Length();
                    if (d < bestD) { bestD = d; bestS = s; bestP = p; }
                }
                if (bestD > 25f) continue;
                float pd = float.MaxValue, py = 0f;
                var line = _profiles[i];
                for (int k = 1; k < line.Count; k++)
                {
                    var a = line[k - 1]; var b = line[k];
                    var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = ab.LengthSquared();
                    if (l2 < 1e-6f) continue;
                    float t = Mathf.Clamp(((at.X - a.X) * ab.X + (at.Z - a.Z) * ab.Y) / l2, 0f, 1f);
                    float d = new Vector2(a.X + ab.X * t - at.X, a.Z + ab.Y * t - at.Z).Length();
                    if (d < pd) { pd = d; py = Mathf.Lerp(a.Y, b.Y, t); }
                }
                near.Add((bestD, $" street {r} {bestD:0.0} m off at {bestS:0.0}/{L:0.0} m, surface {bestP.Y + roads.RoadSurfaceOffset(r):0.000}, "
                               + $"half-width {roads.RoadHalfWidth(r):0.00}, grade target {py - GradeDrop:0.000} from {pd:0.0} m;"));
            }
            near.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var n in near) sb.Append(n.s);
            return sb.ToString();
        }

        /// <summary>Is any PAINT under the paving it was painted on? Every upward-facing vertex of the town's markings --
        /// lines, crossings and their aprons, junction paint, bay paint -- against the highest paving top under it (the
        /// slab, or any carriageway it lies on). Under it by more than a millimetre, that piece of marking cannot be seen.
        /// The ground probes cannot catch this: paint under the asphalt is still well clear of the ground.</summary>
        public List<(string kind, int samples, int buried, float worst, Vector3 at)> PaintUnderPaving(Godot.Node root, RoadField roads)
        {
            var paving = PavingTops(roads);
            var by = new Dictionary<string, (int samples, int buried, float worst, Vector3 at)>();
            void Walk(Godot.Node n)
            {
                if (n is MeshInstance3D mi && mi.Mesh != null && mi.Visible)
                {
                    string name = mi.Name.ToString();
                    if (name.StartsWith("Road_Lines") || name.StartsWith("Crossing_") || name.StartsWith("Junction_Paint")
                        || name.StartsWith("Parking_Bays_Paint"))
                    {
                        string kind = System.Text.RegularExpressions.Regex.Replace(name, @"_?\d+", "");
                        var xf = mi.GlobalTransform;
                        by.TryGetValue(kind, out var st);
                        if (st.samples == 0) st.worst = float.NegativeInfinity;
                        for (int sfc = 0; sfc < mi.Mesh.GetSurfaceCount(); sfc++)
                        {
                            var arr = mi.Mesh.SurfaceGetArrays(sfc);
                            if (arr == null || arr.Count <= (int)Mesh.ArrayType.Normal) continue;
                            var vs = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                            var ns = arr[(int)Mesh.ArrayType.Normal].AsVector3Array();
                            if (vs == null || ns == null || ns.Length != vs.Length) continue;
                            for (int i = 0; i < vs.Length; i++)
                            {
                                if ((xf.Basis * ns[i]).Normalized().Y < 0.7f) continue;
                                var w = xf * vs[i];
                                float hi = paving(w).hi;
                                if (float.IsNaN(hi)) continue;
                                st.samples++;
                                float under = hi - w.Y;
                                if (under > 0.001f) st.buried++;
                                if (under > st.worst) { st.worst = under; st.at = w; }
                            }
                        }
                        by[kind] = st;
                    }
                }
                foreach (var c in n.GetChildren()) Walk(c);
            }
            Walk(root);
            var list = new List<(string, int, int, float, Vector3)>();
            foreach (var kv in by) if (kv.Value.samples > 0) list.Add((kv.Key, kv.Value.samples, kv.Value.buried, kv.Value.worst, kv.Value.at));
            return list;
        }

        /// <summary>Over a HOLE in the paving (the corner fill's whole reason to exist): the paving NEAREST it -- the first ring
        /// out (a quarter metre, then wider) that finds any, the mean of its lows and of its highs. NaN for both if none
        /// within 2 m. ⚠ The nearest, not the lowest anywhere near: a hole's fill held at the lowest paving within a metre
        /// and a half dropped a pit 30 cm deep beside a kerb on a hill.</summary>
        static (float lo, float hi) PavingNear(Func<Vector3, (float lo, float hi, float roadHi)> paving, Vector3 v)
        {
            // (the MEAN of what the ring finds: on a slope its highest or lowest is the slope times its radius off the
            // surface the hole sits in -- 8 cm, an apron over a hole stood over the next crossing's bars by that)
            foreach (float rr in new[] { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f })
            {
                float lo = 0f, hi = 0f; int n = 0;
                for (int k8 = 0; k8 < 8; k8++)
                {
                    float ang = k8 * Mathf.Pi / 4f;
                    var (l, h, _) = paving(v + new Vector3(Mathf.Cos(ang) * rr, 0f, Mathf.Sin(ang) * rr));
                    if (float.IsNaN(l)) continue;
                    lo += l; hi += h; n++;
                }
                if (n > 0) return (lo / n, hi / n);
            }
            return (float.NaN, float.NaN);
        }

        /// <summary>The corner fill where it is what you SEE -- no slab or carriageway over it, the holes it is laid for --
        /// against the ground: the fill under the ground there is the ground showing through the junction. Under paving the
        /// fill may sit where it likes, the ground included: nothing shows it. Returns the visible points tested, how many
        /// are within 3 cm of the ground or under it, and the worst.</summary>
        public (int samples, int under, float worst, Vector3 at) VisibleFillOverGround(RoadField roads, Func<float, float, float> ground)
        {
            var paving = PavingTops(roads);
            int samples = 0, under = 0; float worst = float.NegativeInfinity; var at = Vector3.Zero;
            var seen = new HashSet<(long, long)>();
            foreach (var (a, b, c) in _cornerFill)
                foreach (var v in new[] { a, b, c, (a + b + c) / 3f })
                {
                    if (!seen.Add(((long)Mathf.Round(v.X * 100f), (long)Mathf.Round(v.Z * 100f)))) continue;
                    if (!float.IsNaN(paving(v).hi)) continue;                 // covered: not what you see
                    float g = ground(v.X, v.Z);
                    if (float.IsNaN(g)) continue;
                    samples++;
                    float proud = g - v.Y;
                    if (proud > -0.03f) under++;
                    if (proud > worst) { worst = proud; at = v; }
                }
            return (samples, under, worst, at);
        }

        /// <summary>How far does each piece of PAINT stand over whatever is under it -- across its whole face, not just at its
        /// corners? Every marking triangle (lines, zebra bars, stop lines, junction and bay paint) is sampled inside, and at
        /// each sample the highest surface under it is found: a road's mesh, a slab, or a crossing's plain apron. Under
        /// 3 mm the depth buffer cannot keep them apart at any distance, and paint flickers in and out ("z fighting with
        /// some road markings", strawberry 2026-10-13, on hills); below zero it is simply hidden. Returns the samples, how
        /// many are closer than 3 mm, and the thinnest gap with where it is.</summary>
        public (int samples, int thin, float least, Vector3 at, string what) PaintClearance(Godot.Node root, RoadField roads)
            => PaintClearance(root, roads, null);
        public (int samples, int thin, float least, Vector3 at, string what) PaintClearance(Godot.Node root, RoadField roads,
            Dictionary<string, (int thin, float least, Vector3 at)> byKind)
        {
            var paving = PavingTops(roads);
            var aprons = new List<(Vector3 a, Vector3 b, Vector3 c)>();
            var paint = new List<(Vector3 a, Vector3 b, Vector3 c, string what)>();
            var uvPlain = new Vector2(30f / 256f, 0.5f);
            void Walk(Godot.Node n)
            {
                if (n is MeshInstance3D mi && mi.Mesh != null && mi.Visible)
                {
                    string name = mi.Name.ToString();
                    bool crossing = name.StartsWith("Crossing_");
                    if (crossing || name.StartsWith("Road_Lines") || name.StartsWith("Junction_Paint") || name.StartsWith("Parking_Bays_Paint"))
                    {
                        var xf = mi.GlobalTransform;
                        for (int sfc = 0; sfc < mi.Mesh.GetSurfaceCount(); sfc++)
                        {
                            var arr = mi.Mesh.SurfaceGetArrays(sfc);
                            var vs = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                            var uvs = arr.Count > (int)Mesh.ArrayType.TexUV ? arr[(int)Mesh.ArrayType.TexUV].AsVector2Array() : null;
                            var ix = arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                            bool indexed = ix != null && ix.Length >= 3;
                            int nt = indexed ? ix.Length / 3 : vs.Length / 3;
                            for (int t = 0; t < nt; t++)
                            {
                                int ia = indexed ? ix[3 * t] : 3 * t, ib = indexed ? ix[3 * t + 1] : 3 * t + 1, ic = indexed ? ix[3 * t + 2] : 3 * t + 2;
                                var tri = (xf * vs[ia], xf * vs[ib], xf * vs[ic]);
                                bool apron = crossing && uvs != null && ia < uvs.Length && (uvs[ia] - uvPlain).LengthSquared() < 1e-6f;
                                if (apron) aprons.Add(tri); else paint.Add((tri.Item1, tri.Item2, tri.Item3, name));
                            }
                        }
                    }
                }
                foreach (var c in n.GetChildren()) Walk(c);
            }
            Walk(root);
            // the aprons, bucketed by 4 m cell so a sample asks only the few near it
            var cells = new Dictionary<(int, int), List<int>>();
            for (int k = 0; k < aprons.Count; k++)
            {
                var (a, b, c) = aprons[k];
                int x0 = Mathf.FloorToInt(Mathf.Min(a.X, Mathf.Min(b.X, c.X)) / 4f), x1 = Mathf.FloorToInt(Mathf.Max(a.X, Mathf.Max(b.X, c.X)) / 4f);
                int z0 = Mathf.FloorToInt(Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) / 4f), z1 = Mathf.FloorToInt(Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)) / 4f);
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) { if (!cells.TryGetValue((x, z), out var l)) cells[(x, z)] = l = new List<int>(); l.Add(k); }
            }
            float ApronAt(Vector3 v)
            {
                if (!cells.TryGetValue((Mathf.FloorToInt(v.X / 4f), Mathf.FloorToInt(v.Z / 4f)), out var l)) return float.NaN;
                float best = float.NaN;
                foreach (int k in l)
                {
                    var (a, b, c) = aprons[k];
                    float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                    if (Mathf.Abs(d) < 1e-9f) continue;
                    float w0 = ((b.Z - c.Z) * (v.X - c.X) + (c.X - b.X) * (v.Z - c.Z)) / d, w1 = ((c.Z - a.Z) * (v.X - c.X) + (a.X - c.X) * (v.Z - c.Z)) / d;
                    if (w0 < -1e-4f || w1 < -1e-4f || 1f - w0 - w1 < -1e-4f) continue;
                    float y = w0 * a.Y + w1 * b.Y + (1f - w0 - w1) * c.Y;
                    best = float.IsNaN(best) ? y : Mathf.Max(best, y);
                }
                return best;
            }
            int samples = 0, thin = 0; float least = float.PositiveInfinity; var at = Vector3.Zero; string what = "";
            foreach (var (a, b, c, name) in paint)
                for (int u = 0; u <= 4; u++)
                    for (int w = 0; u + w <= 4; w++)
                    {
                        var v = a + (b - a) * (u / 4f) + (c - a) * (w / 4f);
                        float under = paving(v).hi, ap = ApronAt(v);
                        if (!float.IsNaN(ap)) under = float.IsNaN(under) ? ap : Mathf.Max(under, ap);
                        if (float.IsNaN(under)) continue;
                        samples++;
                        float gap = v.Y - under;
                        if (gap < 0.003f) thin++;
                        if (gap < least) { least = gap; at = v; what = name; }
                        if (byKind != null)
                        {
                            string kind = System.Text.RegularExpressions.Regex.Replace(name, @"_?\d+", "");
                            byKind.TryGetValue(kind, out var bk);
                            if (bk.thin == 0 && bk.least == 0f) bk.least = float.PositiveInfinity;
                            if (gap < 0.003f) bk.thin++;
                            if (gap < bk.least) { bk.least = gap; bk.at = v; }
                            byKind[kind] = bk;
                        }
                    }
            if (ProbeEverything && samples > 0)
            {
                var pv = paving(at);
                Log.Print($"[towngrid] thinnest paint at {at} ({what}): paving lo/hi/roads {pv.lo:0.000}/{pv.hi:0.000}/{pv.roadHi:0.000}, apron {ApronAt(at):0.000}, slab {SlabTops(roads)(at.X, at.Z):0.000}");
                var fitD = FitShifts(roads, roads.RoadSurfaceOffsetOfMaterial(MainMaterial));
                for (int k = 0; k < Patches.Count; k++)
                {
                    var pp = Patches[k];
                    if (pp?.X == null || pp.DistanceTo(at.X, -at.Z) > 1.5) continue;
                    float sh = fitD.TryGetValue(pp, out float f2) ? f2 : 0f;
                    var sbv = new System.Text.StringBuilder($"[towngrid]   slab {k} (clipped {_clips.Exists(c => ReferenceEquals(c.patch, pp))}) vertices within 1.5 m:");
                    for (int i = 0; i < pp.X.Length; i++)
                    {
                        double dx = pp.X[i] - at.X, dz = -pp.Z[i] - at.Z;
                        if (dx * dx + dz * dz < 2.25) sbv.Append($" ({pp.X[i]:0.00},{-pp.Z[i]:0.00}) top {pp.Y[i] + sh + PatchLift:0.000};");
                    }
                    Log.Print(sbv.ToString());
                }
            }
            return (samples, thin, least, at, what);
        }

        /// <summary>Where two pieces of pavement MEET, do they meet at one height? Every walking-surface vertex of every
        /// pavement mesh under <paramref name="root"/>, against every other pavement mesh's within 5 cm of it in plan: a
        /// height difference there is a step in the walk, and its face shows as a dark line along the join. Returns how
        /// many such meetings there are, how many step by more than 1 cm, and the worst.</summary>
        public static (int joins, int steps, float worst, Vector3 at, string between) PavementSteps(Godot.Node root)
            => PavementSteps(root, null);
        public static (int joins, int steps, float worst, Vector3 at, string between) PavementSteps(Godot.Node root,
            Dictionary<string, (int steps, float worst, Vector3 at, string between)> byKind)
        {
            var pts = new List<(Vector3 p, int mesh)>(); var names = new List<string>();
            void Walk(Godot.Node n)
            {
                if (n is MeshInstance3D mi && mi.Mesh != null && mi.Visible && mi.Name.ToString().StartsWith("Sidewalk"))
                {
                    int id = names.Count; names.Add(mi.Name);
                    var xf = mi.GlobalTransform;
                    for (int sfc = 0; sfc < mi.Mesh.GetSurfaceCount(); sfc++)
                    {
                        var arr = mi.Mesh.SurfaceGetArrays(sfc);
                        if (arr == null || arr.Count <= (int)Mesh.ArrayType.Normal) continue;
                        var vs = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var ns = arr[(int)Mesh.ArrayType.Normal].AsVector3Array();
                        if (vs == null || ns == null || ns.Length != vs.Length) continue;
                        for (int i = 0; i < vs.Length; i++)
                            if ((xf.Basis * ns[i]).Normalized().Y >= 0.7f) pts.Add((xf * vs[i], id));
                    }
                }
                foreach (var c in n.GetChildren()) Walk(c);
            }
            Walk(root);
            const float R = 0.05f;
            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < pts.Count; i++)
            {
                var key = (Mathf.FloorToInt(pts[i].p.X / R), Mathf.FloorToInt(pts[i].p.Z / R));
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
                l.Add(i);
            }
            int joins = 0, steps = 0; float worst = 0f; var at = Vector3.Zero; string between = "";
            for (int i = 0; i < pts.Count; i++)
            {
                var (p, m) = pts[i];
                int gx = Mathf.FloorToInt(p.X / R), gz = Mathf.FloorToInt(p.Z / R);
                for (int ox = -1; ox <= 1; ox++)
                    for (int oz = -1; oz <= 1; oz++)
                    {
                        if (!grid.TryGetValue((gx + ox, gz + oz), out var l)) continue;
                        foreach (int k in l)
                        {
                            var (q, mq) = pts[k];
                            if (mq == m || k <= i || new Vector2(q.X - p.X, q.Z - p.Z).Length() > R) continue;
                            joins++;
                            float d = Mathf.Abs(q.Y - p.Y);
                            if (d > 0.01f) steps++;
                            if (d > worst) { worst = d; at = p; between = $"{names[m]} / {names[mq]}"; }
                            if (byKind != null && d > 0.01f)
                            {
                                string ka = System.Text.RegularExpressions.Regex.Replace(names[m], @"_p\d+$|_?[LRAB]?\d+", "");
                                string kb = System.Text.RegularExpressions.Regex.Replace(names[mq], @"_p\d+$|_?[LRAB]?\d+", "");
                                string kind = string.CompareOrdinal(ka, kb) <= 0 ? $"{ka} / {kb}" : $"{kb} / {ka}";
                                byKind.TryGetValue(kind, out var bk);
                                bk.steps++;
                                if (d > bk.worst) { bk.worst = d; bk.at = p; bk.between = $"{names[m]} / {names[mq]}"; }
                                byKind[kind] = bk;
                            }
                        }
                    }
            }
            return (joins, steps, worst, at, between);
        }

        /// <summary>The same question of everything ELSE the town laid: every upward-facing vertex of every mesh under
        /// <paramref name="root"/> -- pavements, kerb corners, medians, bays, plot marks -- against the ground under it,
        /// grouped by what the mesh is (its name without the numbers). GroundPokes walks only the carriageways and the
        /// slabs, and a pavement buried by a hillside is as much the ground coming through as a street is.</summary>
        public static List<(string kind, int samples, int pokes, float worst, Vector3 at, string mesh)> GroundOverMeshes(
            Godot.Node root, Func<float, float, float> ground, float clear = 0.03f)
        {
            var by = new Dictionary<string, (int samples, int pokes, float worst, Vector3 at, string mesh)>();
            void Walk(Godot.Node n)
            {
                if (n is MeshInstance3D mi && mi.Mesh != null && mi.Visible && mi.IsInsideTree())
                {
                    string kind = System.Text.RegularExpressions.Regex.Replace(mi.Name.ToString(), @"_p\d+$|_?[LR]?\d+", "");
                    var xf = mi.GlobalTransform;
                    var basis = xf.Basis;
                    for (int sfc = 0; sfc < mi.Mesh.GetSurfaceCount(); sfc++)
                    {
                        var arr = mi.Mesh.SurfaceGetArrays(sfc);
                        if (arr == null || arr.Count <= (int)Mesh.ArrayType.Normal) continue;
                        var vs = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var ns = arr[(int)Mesh.ArrayType.Normal].AsVector3Array();
                        if (vs == null || ns == null || ns.Length != vs.Length) continue;
                        by.TryGetValue(kind, out var st);
                        if (st.samples == 0) st.worst = float.NegativeInfinity;
                        for (int i = 0; i < vs.Length; i++)
                        {
                            if ((basis * ns[i]).Normalized().Y < 0.7f) continue;     // tops only: kerb faces and skirts meet the ground by design
                            var w = xf * vs[i];
                            float g = ground(w.X, w.Z);
                            if (float.IsNaN(g)) continue;
                            st.samples++;
                            float proud = g - w.Y;
                            if (proud > -clear) st.pokes++;
                            if (proud > st.worst) { st.worst = proud; st.at = w; st.mesh = mi.Name; }
                        }
                        by[kind] = st;
                    }
                }
                foreach (var c in n.GetChildren()) Walk(c);
            }
            Walk(root);
            var list = new List<(string, int, int, float, Vector3, string)>();
            foreach (var kv in by) if (kv.Value.samples > 0) list.Add((kv.Key, kv.Value.samples, kv.Value.pokes, kv.Value.worst, kv.Value.at, kv.Value.mesh));
            list.Sort((a, b) => b.Item4.CompareTo(a.Item4));
            return list;
        }
        /// <summary>The two junction-coverage probes, as one method so they can be asked twice: once over the
        /// slab and roads alone, and again once the corner asphalt is down -- the second answer is the one
        /// the player sees, the first says what the slab alone would have left.</summary>
        public void ProbeCoverage(RoadField roads, string stage)
        {
            {
                int holes = 0, tested = 0; var worstHole = Vector3.Zero; float worstR = 0f;
                for (int j = 0; j < roads.JunctionCount; j++)
                {
                    var es = roads.JunctionEdges(j);
                    if (es.Count < 2) continue;
                    var jp = roads.JunctionPos(j);
                    float reach = 0f;
                    foreach (var (rr, atEnd) in es)
                    {
                        if (!roads.EvaluateAlong(rr, atEnd ? roads.RoadLength(rr) : 0f, out var tp, out _, snapTerrain: false)) continue;
                        reach = Mathf.Max(reach, new Vector2(tp.X - jp.X, tp.Z - jp.Z).Length());
                    }
                    if (reach < 2f) continue;
                    const int A = 96; float dr = 0.5f;
                    int R = Mathf.Max(2, Mathf.CeilToInt(reach / dr));
                    var cov = new bool[A, R + 1];
                    for (int ai = 0; ai < A; ai++)
                        for (int ri = 0; ri <= R; ri++)
                        {
                            float a = Mathf.Tau * ai / A, rad = ri * dr;
                            var q = new Vector3(jp.X + Mathf.Cos(a) * rad, 0f, jp.Z + Mathf.Sin(a) * rad);
                            bool c = false;
                            c = OnAnyPatch(q.X, -q.Z, 0.01);
                            // ⚠ EVERY ROAD, NOT THE NEAREST -- the third probe in this file to have had the
                            // bug it was built to find. A point at a main's edge inside a junction is often
                            // NEAREST to the narrow side street beside it, which it really is outside, so
                            // the nearest-only test called solid carriageway bare and invented 136 holes.
                            if (!c) c = roads.DeepestRoadOverlap(q, 0.05f, out _, out _);
                            if (!c) c = InCornerFill(q);
                            cov[ai, ri] = c;
                        }
                    // ⚠⚠ A BARE RUN WITH PAVING ON BOTH SIDES, not a bare point with covered neighbours.
                    // The neighbour test could only ever see a gap ONE probe wide (0.5 m); a 2 m crescent
                    // between a patch edge and a road's flank has bare neighbours of its own and was
                    // reported as open grass. That is why this said "no holes" while master kept seeing
                    // them, three fixes running.
                    for (int ai = 0; ai < A; ai++)
                    {
                        int ri = 1;
                        while (ri < R)
                        {
                            if (cov[ai, ri]) { ri++; continue; }
                            int start = ri;
                            while (ri < R && !cov[ai, ri]) ri++;
                            tested++;
                            // paved before the run and paved after it: that is a hole, whatever its width
                            if (start - 1 < 0 || !cov[ai, start - 1] || ri >= R || !cov[ai, ri]) continue;
                            holes++;
                            float width = (ri - start) * dr;
                            if (width > worstR)
                            {
                                worstR = width;
                                float rad = (start + (ri - start) * 0.5f) * dr;
                                worstHole = new Vector3(jp.X + Mathf.Cos(Mathf.Tau * ai / A) * rad, jp.Y,
                                                        jp.Z + Mathf.Sin(Mathf.Tau * ai / A) * rad);
                            }
                        }
                    }
                }
                Log.Print($"[towngrid] junction coverage ({stage}): {holes} GAPS (bare runs with paving both sides) of {tested} runs"
                        + (holes > 0 ? $", widest {worstR:0.0} m at {worstHole}" : " -- none"));

                // ⚠⚠ AND A 5 cm WALK DOWN EACH ARM, because the polar sweep CANNOT SEE A SLIVER: its rays
                // are 2*pi*r/96 apart -- 0.65 m at 10 m out -- so a 20 cm strip of grass crossing a mouth
                // falls between two rays and reads as nothing. A dense 2-D grid finds it but costs
                // cells x patches x triangles, which ran for ten minutes and had to be killed. One dimension
                // is enough: a sliver ACROSS a mouth is crossed by a line ALONG the arm.
                // ⭐ Resolution is part of an instrument's answer, and neither of the first two stated theirs.
                {
                    const float Step5 = 0.05f;
                    float worstSliver = 0f; var sliverAt = Vector3.Zero; int slivers = 0;
                    for (int j3 = 0; j3 < roads.JunctionCount; j3++)
                    {
                        var e3 = roads.JunctionEdges(j3);
                        if (e3.Count < 2) continue;
                        var jp3 = roads.JunctionPos(j3);
                        foreach (var (road3, atEnd3) in e3)
                        {
                            float L3 = roads.RoadLength(road3);
                            if (!roads.EvaluateAlong(road3, atEnd3 ? L3 : 0f, out var tip3, out var tan3, snapTerrain: false)) continue;
                            var outw = new Vector3(tip3.X - jp3.X, 0f, tip3.Z - jp3.Z);
                            if (outw.Length() < 0.5f) continue;
                            outw = outw.Normalized();
                            var rt = RightOf(outw);
                            float hw3 = roads.RoadHalfWidth(road3);
                            foreach (float latf in new[] { -0.85f, -0.4f, 0f, 0.4f, 0.85f })
                            {
                                float run3 = 0f; bool prev3 = false;
                                for (float d3 = 0.2f; d3 <= outw.Length() * 0f + 40f; d3 += Step5)
                                {
                                    var q4 = jp3 + outw * d3 + rt * (latf * hw3);
                                    bool c4 = false;
                                    c4 = OnAnyPatch(q4.X, -q4.Z, 0.001);
                                    // ⚠ EVERY ROAD, NOT THE NEAREST. The nearest spline to a point in a wide
                                    // road's mouth is often the narrow side street beside it, which the point
                                    // really is outside -- so a nearest-only test calls covered asphalt bare.
                                    if (!c4) c4 = roads.DeepestRoadOverlap(q4, 0f, out _, out _);
                                    if (!c4) c4 = InCornerFill(q4);
                                    if (!c4) { run3 += Step5; }
                                    else
                                    {
                                        if (prev3 == false && run3 > 0f && run3 < 3f)
                                        {
                                            slivers++;
                                            if (run3 > worstSliver) { worstSliver = run3; sliverAt = q4 - outw * (run3 * 0.5f); }
                                            if (run3 > LastBareRun) { LastBareRun = run3; LastBareAt = q4 - outw * (run3 * 0.5f); }
                                        }
                                        run3 = 0f;
                                    }
                                    prev3 = c4;
                                }
                            }
                        }
                    }
                    Log.Print($"[towngrid] arm walk ({stage}, 5 cm): {slivers} bare slivers crossing a mouth"
                            + (slivers > 0 ? $", widest {worstSliver * 100f:0} cm at {sliverAt}" : " -- none"));
                }
            }
        }

        /// <summary>The junction's painted lines: each arm's own markings carried on in from its cut, and a
        /// stop line across every approach of a three-or-more-way (strawberry 2026-10-12: "add stop lines,
        /// continue the road texture up to the stop lines").
        ///
        /// ⭐ PAINT, NOT MORE ASPHALT. The obvious reading of "continue the road texture" is to stretch each
        /// arm's textured ribbon further in -- but the ribbon carries ITS OWN lane lines in its texture, and
        /// four of them run into one another in the middle of an intersection. tiny's junctions already solve
        /// it the way a road crew does: plain tarmac through the box, with the lines PAINTED on, bent through
        /// the fillets and stopped at a stop line.</summary>
        int BuildMarkMeshes(Node3D parent, RoadField roads, Dictionary<SDG.Unturned.RoadPatch, float> fit)
        {
            if (Marks.Count == 0) return 0;
            int made = 0;
            // ⚠⚠ THE PAINT WEARS THE ROAD'S OWN MATERIAL, with every vertex pinned to ONE TEXEL of the road
            // texture -- the white dash at (60,8) and the yellow centre line at (124,0), read out of road_0
            // rather than guessed. Matching the colour by NUMBER does not work and I tried it twice: first
            // unshaded (full bright, right only at noon), then a lit StandardMaterial3D with the same RGB,
            // which came out DARKER than the road because the asphalt is drawn through the wet-surface
            // shader and a stock material is not the same light. The same texel through the same shader
            // cannot disagree with itself -- in sun, at dusk, or soaked.
            var paintMat = roads.MaterialOfMaterial(MainMaterial);
            var uvWhite = new Vector2(0.2363f, 0.0664f);
            var uvYellow = new Vector2(0.4863f, 0.0039f);
            foreach (var mk in Marks)
            {
                if (mk == null || mk.X == null || mk.Quads == 0) continue;
                float shift = mk.On != null && fit.TryGetValue(mk.On, out float f) ? f : 0f;
                var st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                var uv = mk.Colour == 1 ? uvYellow : uvWhite;
                for (int q = 0; q + 3 < mk.X.Count; q += 4)
                {
                    var v = new Vector3[4];
                    for (int c = 0; c < 4; c++)
                        v[c] = new Vector3((float)mk.X[q + c], mk.Y[q + c] + shift + PatchLift + MarkLift, -(float)mk.Z[q + c]);
                    // ⚠ ORIENT BY THE WANTED NORMAL: the frame flip out of InfiniteRoads' negated z mirrors
                    // these quads exactly as it mirrors the patch, and a mirror reverses winding.
                    void Tri(Vector3 a, Vector3 b, Vector3 c)
                    {
                        bool keep = (c - a).Cross(b - a).Y >= 0f;
                        // ⚠⚠ A NORMAL PER VERTEX. BuildPatchMeshes calls GenerateNormals and this did not, so
                        // every painted quad reached the wet-surface shader with no normal at all and lit as
                        // though it faced nowhere -- which is why the paint stayed DARKER than the road even
                        // once it wore the road's own material and texel. Paint lies flat: the normal is up.
                        st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(a);
                        st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(keep ? b : c);
                        st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(keep ? c : b);
                    }
                    Tri(v[0], v[1], v[3]); Tri(v[0], v[3], v[2]);
                }
                var mesh = st.Commit();
                if (mesh == null || mesh.GetSurfaceCount() == 0) continue;
                var mi = new MeshInstance3D { Name = $"Junction_Paint_{made}", Mesh = mesh };
                mi.MaterialOverride = paintMat;
                parent.AddChild(mi);
                made++;
            }
            return made;
        }
        /// <summary>How far paint stands over the asphalt it is laid on: enough not to fight it.</summary>
        public float MarkLift = 0.015f;
        /// <summary>Painted pieces laid by the last BuildPatchMeshes -- lane lines carried in, and stop lines.</summary>
        public int PaintPieces;
        public int CrossingPieces;

        /// <summary>How far the patch rides over the asphalt it overlaps.
        ///
        /// ⚠⚠ 2 mm WAS RIGHT AND THEN STOPPED BEING RIGHT. It was chosen when the patch met the road EDGE TO
        /// EDGE -- nothing to z-fight, and every millimetre a visible lip. PatchOverlap came later, to bury
        /// the road's open slab end under the patch, and that turned the join into a genuine OVERLAP: 35 cm
        /// of two surfaces 2 mm apart, seen at a grazing angle, which the depth buffer resolves per pixel.
        /// The serrated dark bands along every mouth were that fight -- not a hole (coverage: 0 enclosed bare
        /// points in 52,800), not the ground (the paving clears it by 12 cm), not shadows, normals, the
        /// patch's bevel or the pavements' ends, all of which were measured and ruled out first.
        /// ⚠ The lesson is the one that cost five renders: a constant justified by a condition has to be
        /// revisited when that condition changes. "There is nothing to z-fight with" stopped being true the
        /// day the overlap was added, and nothing pointed at the number.</summary>
        public float PatchLift = 0.012f;
        static float PingPong(float t) { t = Mathf.Abs(t) % 2f; return t <= 1f ? t : 2f - t; }

        /// <summary>How far each patch must be shifted to meet the asphalt it joins, measured per patch.
        /// ⚠ SHARED BY THE MESH AND THE CONFORM. They used different numbers for one frame -- the mesh used
        /// this fit (0.090) and the conform used the raw surface offset (0.220) -- and the ground came out
        /// 13 cm too high, poking through the paving it was meant to sit under.</summary>
        Dictionary<SDG.Unturned.RoadPatch, float> FitShifts(RoadField roads, float fallback)
        {
            var fit = new Dictionary<SDG.Unturned.RoadPatch, float>();
            foreach (int r in Built)
                for (int e = 0; e < 2; e++)
                {
                    bool atEnd = e == 1;
                    if (roads.RoadEndJunction(r, atEnd) < 0) continue;
                    if (!roads.EvaluateAlong(r, atEnd ? roads.RoadLength(r) : 0f, out var tip, out _, snapTerrain: false)) continue;
                    float asphalt = tip.Y + roads.RoadSurfaceOffset(r);
                    foreach (var pp in Patches)
                    {
                        if (fit.ContainsKey(pp) || pp.DistanceTo(tip.X, -tip.Z) > 0.5) continue;
                        float h = pp.HeightAt(tip.X, -tip.Z);
                        if (!float.IsNaN(h)) fit[pp] = asphalt - h;
                        break;
                    }
                }
            foreach (var pp in Patches) if (!fit.ContainsKey(pp)) fit[pp] = fallback;
            return fit;
        }

        /// <summary>Conform the ground to the streets. Call it AFTER Generate and BEFORE LaySidewalks: the
        /// pavement's skirt is cut to the ground it finds, so grading afterwards would leave every ribbon
        /// hanging over its own freshly-lowered verge -- the floating kerb again, by a different route.</summary>
        public bool GradeTerrain(Terrain terr, RoadField roads)
        {
            if (!GradeSite || terr == null || roads == null || _profiles.Count == 0) return false;
            float widest = 0f;
            foreach (int r in Built) widest = Mathf.Max(widest, roads.RoadHalfWidth(r));
            var sunk = new List<List<Vector3>>(_profiles.Count + Patches.Count);
            // ⚠⚠ AND HOW FAR EACH ONE'S TARMAC REACHES, for the conform's ceiling (strawberry 2026-10-13, "follow gentle hills
            // and slopes"). Each ground point takes the height of the NEAREST line, and beside a junction on a slope the
            // nearest can be the OTHER road, higher up -- so the ceiling (no point above a road that covers it) is what keeps
            // the ground out of this one. It reached the island's road, 9.2 m, for every line; the town's mains are 10.75 m,
            // and the ground came up through the last metre and a half of them, 24 cm proud of the asphalt, measured
            // (GroundPokes). Each line now covers its own half-width -- plus one grid cell, because the ground under the very
            // edge is interpolated from points up to a cell's diagonal outside it. UG_TOWNOLDCOVER=1 is the control.
            float cell = Terrain.GridUnit * 1.42f;
            var covers = new List<float>(_profiles.Count + Patches.Count);
            // ⭐ TO THE PAVEMENT'S LEVEL, NOT UNDER THE STREET (strawberry 2026-10-19: "move the road height (and sidewalk height)
            // lower, and thus the grassy sides too"). The corridor used to be cut GradeDrop under the street out to GradeReach
            // past the widest carriageway, which left every pavement standing on a bank half a metre over the lawn. Now the
            // corridor is graded to just under the pavement's top, and CeilCarriageways cuts only what the asphalt needs.
            float lift = OldGrade ? -GradeDrop : PavementKerb - PavementProud;
            for (int li = 0; li < _profiles.Count; li++)
            {
                var line = _profiles[li];
                var one = new List<Vector3>(line.Count);
                foreach (var q in line) one.Add(new Vector3(q.X, q.Y + lift, q.Z));
                sunk.Add(one);
                covers.Add((li < Built.Count ? roads.RoadHalfWidth(Built[li]) : widest) + cell);
            }
            // ⚠⚠ AND THE PATCHES, which were never graded at all. GradeTerrain conformed to the road
            // CENTRELINES only, so the ground around every junction kept whatever height it had -- measured,
            // up to 28 cm BELOW the paving. That is not a hole and nothing pokes through (a coverage sweep
            // found 0 enclosed bare points in 52,800, and a poke sweep says the paving clears its ground
            // everywhere); it is the slab's own EDGE, on show all the way round each mouth because the land
            // falls away from it. Four guesses missed this -- shadows, patch normals, the patch's bevel, the
            // pavements' open ends -- and the measurement found it.
            // The boundary loops are already in the patch: Skirts is exactly the list of its open edges.
            float fallbackLift = roads.RoadSurfaceOffsetOfMaterial(MainMaterial);
            var pfit = FitShifts(roads, fallbackLift);
            // (the junctions' slabs are carriageway: CeilCarriageways cuts under them now, not the corridor's conform)
            if (OldGrade)
            foreach (var pp in Patches)
            {
                if (pp?.Skirts == null || pp.X == null) continue;
                float shift = pfit.TryGetValue(pp, out float pf) ? pf : fallbackLift;
                foreach (var ids in pp.Skirts)
                {
                    if (ids == null || ids.Length < 2 || System.Environment.GetEnvironmentVariable("UG_TOWNNOSKIRTCONFORM") == "1") continue;
                    var loop = new List<Vector3>(ids.Length);
                    foreach (int ix in ids)
                        if (ix >= 0 && ix < pp.X.Length)
                            loop.Add(new Vector3((float)pp.X[ix], pp.Y[ix] + shift - GradeDrop, -(float)pp.Z[ix]));
                    if (loop.Count >= 2) { sunk.Add(loop); covers.Add(cell); }
                }
                // ⚠⚠ AND ITS INSIDE (strawberry 2026-10-13, "follow gentle hills and slopes"). With only its outline to go by,
                // each cell under a slab took the height of the NEAREST piece of outline (or street), which on flat ground is
                // the slab's height and on a hill is not: a cell by the slab's uphill edge took the uphill height while the
                // slab there runs down to its other edges. Every triangle's edges go in too, so a cell takes the slab's own
                // surface over it. MEASURED, A/B on the same hills (GroundPokes, before the per-line ceiling below): 52 points
                // of ground through the paving with these, 384 without (UG_TOWNNOTRICONFORM=1), 8846 without the outline too
                // (UG_TOWNNOSKIRTCONFORM=1). ⚠ It was NOT the dark crease across sloped junctions it was first written for --
                // that was the mouth fill (AddMouthFill), found by turning the fill off.
                if (pp.Tris != null && System.Environment.GetEnvironmentVariable("UG_TOWNNOTRICONFORM") != "1")
                    for (int t = 0; t + 2 < pp.Tris.Length; t += 3)
                    {
                        var tri = new List<Vector3>(4);
                        foreach (int ix in new[] { pp.Tris[t], pp.Tris[t + 1], pp.Tris[t + 2], pp.Tris[t] })
                            if (ix >= 0 && ix < pp.X.Length) tri.Add(new Vector3((float)pp.X[ix], pp.Y[ix] + shift - GradeDrop, -(float)pp.Z[ix]));
                        if (tri.Count == 4) { sunk.Add(tri); covers.Add(cell); }
                    }
            }
            terr.ConformToPolylines(sunk, widest + GradeReach, GradeFeather,
                                    System.Environment.GetEnvironmentVariable("UG_TOWNOLDCOVER") == "1" ? null : covers);
            // ...and under the back lanes, as under the streets (planned before this runs: Main), out to their pavements' backs
            if (LanePlans.Count > 0)
            {
                // (each width its own pass: the back lanes', and a parking lot's wider ways in)
                var byHalf = new Dictionary<float, List<List<Vector3>>>();
                foreach (var lp in LanePlans)
                {
                    var one = new List<Vector3>(lp.Pts.Count);
                    // (to its pavements' level as the streets are; its carriageway is cut under in CeilCarriageways)
                    foreach (var q in lp.Pts) one.Add(new Vector3(q.X, q.Y + lift, q.Z));
                    float h = lp.Half > 0f ? lp.Half : LaneHalf;
                    if (!byHalf.TryGetValue(h, out var lanes)) byHalf[h] = lanes = new List<List<Vector3>>();
                    lanes.Add(one);
                }
                foreach (var (h, lanes) in byHalf) terr.ConformToPolylines(lanes, h + LaneWalkWidth + 0.5f, GradeFeather * 0.5f);
            }
            // ...and under each parking lot, one smooth surface (TownParking)
            GradeLots(terr, roads);
            if (!OldGrade) CeilCarriageways(terr, roads);
            return true;
        }

        /// <summary>Cut the ground down under everything a car drives on -- the streets (and their parking bays), the
        /// junctions' slabs and the corners between their arms, the back lanes -- to CarriagewayClear under the asphalt,
        /// and nowhere else. ⭐ The corridor around it was graded to the pavement's level first; this cuts only
        /// as far as each piece of asphalt needs (Terrain.CeilTo), so the ground behind a pavement stays up at the lawn's
        /// level instead of in the road's trench.</summary>
        void CeilCarriageways(Terrain terr, RoadField roads)
        {
            var samples = new List<Vector3>(1 << 20);
            // (half a metre apart, so a ground point can be called under the asphalt from samples within 0.36 m of it: see CeilTo)
            const float Step = 0.5f;
            // a road's carriageway, a little past each edge (the kerb laps it)
            void Strip(int r, float half)
            {
                float L = roads.RoadLength(r), top = roads.RoadSurfaceOffset(r) - CarriagewayClear;
                int n = Mathf.Max(1, Mathf.CeilToInt(L / Step));
                for (int i = 0; i <= n; i++)
                {
                    float d = L * i / n;
                    if (!roads.EvaluateAlong(r, d, out var p, out var t, snapTerrain: false)) continue;
                    var right = new Vector3(t.Z, 0f, -t.X).Normalized();
                    for (int side = -1; side <= 1; side += 2)
                    {
                        // round a parking bay the asphalt reaches a bay's depth further out
                        float edge = half + BayExtra(r, side, d) + CeilPastEdge;
                        int m = Mathf.Max(1, Mathf.CeilToInt(edge / Step));
                        for (int k = 0; k <= m; k++)
                        {
                            var q = p + right * (side * edge * k / m);
                            samples.Add(new Vector3(q.X, p.Y + top, q.Z));
                        }
                        // ⚠ NOT THE PAVEMENTS. They were sampled here too (held a few cm under their tops), and a sample marks
                        // the ground points round it as under-the-road, the ones Terrain.CeilTo cuts first -- so the points along
                        // each pavement's BACK edge were cut like carriageway and the lawn behind it stayed 26 cm down (median,
                        // measured). The corridor's own grade already holds them PavementProud under the top.
                    }
                }
            }
            foreach (int r in Built) Strip(r, roads.RoadHalfWidth(r));
            // the junctions' slabs, inside and a metre past every edge (the corner asphalt fills out to the kerb's curve)
            float fallbackLift = roads.RoadSurfaceOffsetOfMaterial(MainMaterial);
            var pfit = FitShifts(roads, fallbackLift);
            foreach (var pp in Patches)
            {
                if (pp?.Tris == null || pp.X == null) continue;
                float shift = pfit.TryGetValue(pp, out float pf) ? pf : fallbackLift;
                for (int t = 0; t + 2 < pp.Tris.Length; t += 3)
                {
                    int ia = pp.Tris[t], ib = pp.Tris[t + 1], ic = pp.Tris[t + 2];
                    if (ia >= pp.X.Length || ib >= pp.X.Length || ic >= pp.X.Length) continue;
                    var A = new Vector3((float)pp.X[ia], pp.Y[ia] + shift - CarriagewayClear, -(float)pp.Z[ia]);
                    var B = new Vector3((float)pp.X[ib], pp.Y[ib] + shift - CarriagewayClear, -(float)pp.Z[ib]);
                    var C = new Vector3((float)pp.X[ic], pp.Y[ic] + shift - CarriagewayClear, -(float)pp.Z[ic]);
                    int n = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Flat(A, B), Mathf.Max(Flat(B, C), Flat(C, A))) / Step));
                    for (int i = 0; i <= n; i++)
                        for (int k = 0; i + k <= n; k++)
                        {
                            float u = (float)i / n, v = (float)k / n;
                            samples.Add(A + (B - A) * u + (C - A) * v);
                        }
                }
                if (pp.Skirts == null) continue;
                foreach (var ids in pp.Skirts)
                {
                    if (ids == null || ids.Length < 2) continue;
                    // its centre, to push each edge point out from
                    double sx = 0, sz = 0; int cnt = 0;
                    foreach (int ix in ids) if (ix >= 0 && ix < pp.X.Length) { sx += pp.X[ix]; sz += pp.Z[ix]; cnt++; }
                    if (cnt == 0) continue;
                    var mid = new Vector3((float)(sx / cnt), 0f, -(float)(sz / cnt));
                    foreach (int ix in ids)
                    {
                        if (ix < 0 || ix >= pp.X.Length) continue;
                        var e = new Vector3((float)pp.X[ix], pp.Y[ix] + shift - CarriagewayClear, -(float)pp.Z[ix]);
                        var outw = new Vector3(e.X - mid.X, 0f, e.Z - mid.Z);
                        if (outw.LengthSquared() < 1e-6f) continue;
                        outw = outw.Normalized();
                        for (float o = 0.5f; o <= SlabCeilReach + 1e-3f; o += 0.5f) samples.Add(e + outw * o);
                    }
                }
            }
            // the back lanes and the ways into the parking lots, and a little round each end (its kerb returns' asphalt)
            foreach (var lp in LanePlans)
            {
                if (lp.Pts == null || lp.Pts.Count < 2) continue;
                float h = (lp.Half > 0f ? lp.Half : LaneHalf) + CeilPastEdge;
                float top = roads.RoadSurfaceOffsetOfMaterial(lp.Material >= 0 ? lp.Material : LaneMaterial >= 0 ? LaneMaterial : StreetMaterial) - CarriagewayClear;
                for (int i = 0; i + 1 < lp.Pts.Count; i++)
                {
                    var a = lp.Pts[i]; var b = lp.Pts[i + 1];
                    float len = Flat(a, b);
                    if (len < 1e-3f) continue;
                    var dir = new Vector3(b.X - a.X, 0f, b.Z - a.Z) / len;
                    var right = new Vector3(dir.Z, 0f, -dir.X);
                    int n = Mathf.Max(1, Mathf.CeilToInt(len / Step)), m = Mathf.Max(1, Mathf.CeilToInt(2f * h / Step));
                    for (int s = 0; s <= n; s++)
                    {
                        var p = a.Lerp(b, (float)s / n);
                        for (int k = 0; k <= m; k++)
                        {
                            var q = p + right * (-h + 2f * h * k / m);
                            samples.Add(new Vector3(q.X, p.Y + top, q.Z));
                        }
                    }
                }
                for (int e = 0; e < 2; e++)
                {
                    // only where it opens onto a street (the kerb returns' asphalt) or turns a hard corner (the knee's
                    // square); a closed end stops at its kerb, and a lot's end is the lot's own grading
                    var end = e == 0 ? lp.Pts[0] : lp.Pts[^1];
                    bool mouth = e == 0 ? lp.A != null : lp.B != null, knee = e == 0 ? lp.KneeA : lp.KneeB;
                    if (!mouth && !knee) continue;
                    float rr = mouth ? h + LaneKerbRadius : h * 1.5f;
                    for (float x = -rr; x <= rr; x += Step)
                        for (float z = -rr; z <= rr; z += Step)
                            if (x * x + z * z <= rr * rr) samples.Add(new Vector3(end.X + x, end.Y + top, end.Z + z));
                }
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int moved = terr.CeilTo(samples, out float left, out float deepest, underReach: Step * 0.72f);
            Log.Print($"[towngrid] carriageway cut: {samples.Count} samples, {moved} ground points lowered (deepest {deepest:0.00} m), "
                    + $"{left * 100f:0.0} cm still over after the passes, {watch.Elapsed.TotalSeconds:0.00} s");
            if (Probes) ReportPavementBacks(terr, roads);
        }
        /// <summary>How far past a carriageway's edge its ground is held down (the kerb laps the asphalt by 3 cm), and how far past
        /// a junction slab's outline (the corner asphalt fills out to the kerb's curve).</summary>
        public float CeilPastEdge = 0.1f, SlabCeilReach = 1.5f;
        /// <summary>How far under the asphalt's own surface CeilCarriageways holds the ground. ⚠ NOT GradeDrop's 0.34 m (0.12 under a
        /// spline that is itself 0.22 under its asphalt). That margin was a conform's: one target for a whole 4 m cell, guessing at
        /// what the triangles would do between points. CeilTo checks the rendered triangles at every sample, a metre apart, so a
        /// few centimetres is the real margin -- and every centimetre of it is ground that has to climb back up behind the kerb
        /// within one grid cell. A 1 m lane walk could not get it back at 0.34.</summary>
        public float CarriagewayClear = 0.06f;
        /// <summary>Where the last ReportPavementBacks found a pavement standing highest over the ground behind it.</summary>
        public Vector3 WorstPavementBackAt;

        /// <summary>⭐ THE NUMBER THE CHANGE IS FOR: how far each pavement's top stands over the ground just behind it, read off
        /// every street side every 4 m. Before the sink it was the whole bank (~0.5 m); it should now be about PavementProud.</summary>
        void ReportPavementBacks(Terrain terr, RoadField roads)
        {
            var drops = new List<float>();
            float worst = float.MinValue; Vector3 worstAt = Vector3.Zero; int deep = 0;
            foreach (int r in Built)
            {
                float L = roads.RoadLength(r), half = roads.RoadHalfWidth(r);
                for (float d = 6f; d <= L - 6f; d += 4f)
                {
                    if (!roads.EvaluateAlong(r, d, out var p, out var t, snapTerrain: false)) continue;
                    var right = new Vector3(t.Z, 0f, -t.X).Normalized();
                    for (int side = -1; side <= 1; side += 2)
                    {
                        if (BayExtra(r, side, d) > 0.01f) continue;
                        var q = p + right * (side * (half + PavementGap + _walkWidth + 0.3f));
                        // (behind a pavement means LAWN: near a junction the point lands on the other road's asphalt, where the
                        // ground is meant to be cut deep -- that read as a 2 m drop the first time)
                        if (!ClearOfRoads(roads, q, 0.5f) || NearLanePlan(q, LaneHalf + LaneWalkWidth + 0.5f)) continue;
                        float drop = p.Y + PavementKerb - terr.SampleHeight(q.X, q.Z);
                        drops.Add(drop);
                        if (drop > 0.3f) deep++;
                        if (drop > worst) { worst = drop; worstAt = new Vector3(q.X, p.Y + PavementKerb, q.Z); }
                    }
                }
            }
            if (drops.Count == 0) return;
            drops.Sort();
            float mean = 0f; foreach (float v in drops) mean += v; mean /= drops.Count;
            WorstPavementBackAt = worstAt;
            Log.Print($"[towngrid] pavement over the ground behind it: mean {mean * 100f:0} cm, median {drops[drops.Count / 2] * 100f:0} cm, "
                    + $"90% {drops[(int)(drops.Count * 0.9f)] * 100f:0} cm, {deep} over 30 cm, worst {worst * 100f:0} cm at "
                    + $"({worstAt.X:0.0}, {worstAt.Y:0.00}, {worstAt.Z:0.0}), over {drops.Count} points");
            // ...and the back lanes' walks, a metre wide: the narrowest climb the ground has to make
            var lane = new List<float>();
            float lworst = float.MinValue; Vector3 lworstAt = Vector3.Zero;
            foreach (var lp in LanePlans)
            {
                if (lp.OpenB || lp.Pts == null || lp.Pts.Count < 2) continue;
                float h = lp.Half > 0f ? lp.Half : LaneHalf, run = 0f, total = PolyLen(lp.Pts);
                for (int i = 0; i + 1 < lp.Pts.Count; i++)
                {
                    var a = lp.Pts[i]; var b = lp.Pts[i + 1];
                    float len = Flat(a, b);
                    if (len < 1e-3f) continue;
                    var right = new Vector3(b.Z - a.Z, 0f, -(b.X - a.X)) / len;
                    for (float t = 2f; t < len; t += 4f)
                    {
                        if (run + t < 6f || run + t > total - 6f) continue;     // clear of the mouths
                        var p = a.Lerp(b, t / len);
                        for (int side = -1; side <= 1; side += 2)
                        {
                            var q = p + right * (side * (h + PavementGap + LaneWalkWidth + 0.3f));
                            if (!ClearOfRoads(roads, q, 0.5f)) continue;
                            float drop = p.Y + PavementKerb - terr.SampleHeight(q.X, q.Z);
                            lane.Add(drop);
                            if (drop > lworst) { lworst = drop; lworstAt = new Vector3(q.X, p.Y + PavementKerb, q.Z); }
                        }
                    }
                    run += len;
                }
            }
            if (lane.Count == 0) return;
            lane.Sort();
            Log.Print($"[towngrid] lane walk over the ground behind it: median {lane[lane.Count / 2] * 100f:0} cm, 90% "
                    + $"{lane[(int)(lane.Count * 0.9f)] * 100f:0} cm, worst {lworst * 100f:0} cm at ({lworstAt.X:0.0}, {lworstAt.Z:0.0}), over {lane.Count} points");
        }
        /// <summary>Is this point within `reach` of a planned back lane's centreline? (The lanes are built after the grading, so
        /// the road field cannot be asked yet.)</summary>
        bool NearLanePlan(Vector3 q, float reach)
        {
            foreach (var lp in LanePlans)
            {
                if (lp.Pts == null) continue;
                for (int i = 0; i + 1 < lp.Pts.Count; i++)
                {
                    var a = lp.Pts[i]; var b = lp.Pts[i + 1];
                    var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = ab.LengthSquared();
                    float t = l2 < 1e-8f ? 0f : Mathf.Clamp(((q.X - a.X) * ab.X + (q.Z - a.Z) * ab.Y) / l2, 0f, 1f);
                    if (new Vector2(a.X + ab.X * t - q.X, a.Z + ab.Y * t - q.Z).Length() < reach) return true;
                }
            }
            return false;
        }

        /// <summary>Pavement down both sides of every street the grid just built, held back from the junctions.
        /// ⚠ THE TRIM IS THE WHOLE POINT. Ribbons run to the road's ends, and four of them arriving at one
        /// crossroads would pile into the middle of the intersection and into each other. Each end is held back
        /// by the widest road meeting it plus a corner radius, which is also what leaves room for the corner
        /// itself.</summary>
        public int LaySidewalks(Node3D parent, RoadField roads, EditorSidewalk proto = null, float cornerRadius = 2.5f)
        {
            if (parent == null || roads == null) return 0;
            var walk = proto ?? new EditorSidewalk();
            walk.KerbHeight = PavementKerb;   // ⚠ ONE number for the pavement and for whatever stands on it
            walk.RoadGap = PavementGap;       // ...and one for how it meets the asphalt
            walk.Width = PavementWidth;       // ...and one for how wide it is, which the corners read too
            if (walk.GetParent() == null) parent.AddChild(walk);
            int made = 0;
            foreach (int r in Built)          // ⚠ OUR streets, not every road in the field
            {
                float t0 = TrimFor(roads, r, false, cornerRadius);
                float t1 = TrimFor(roads, r, true, cornerRadius);
                // DROPPED KERBS AT THE CROSSINGS. A junction approach is where people step into the road, so
                // that is where the kerb comes down -- the same places a real street has them.
                // ⚠ IN THE ROAD'S ARC SPACE, not the trimmed ribbon's: KerbAt is asked about `from + i*Step`,
                // so a dip measured from the ribbon's own start would sit a trim's length out of place.
                walk.Dips.Clear();
                if (CrossingDips)
                {
                    float L = roads.RoadLength(r);
                    if (L - t0 - t1 > CrossingDipLength * 3f)
                    {
                        // ⚠⚠ THE DROP RUNS OUT THROUGH THE CORNER, it does not sit just inside it. Dipping a
                        // metre short of the end gave kerb-down, kerb-up, then a full-height corner -- a
                        // visible jog exactly where the straight meets the curve, which is what master saw:
                        // "sidewalks seem to wiggle before connecting to the curved ones". Starting the dip
                        // BEFORE the ribbon begins means it starts already dropped and ramps up once, away
                        // from the junction -- and the corner piece is laid dropped to match, so the whole
                        // radius is a crossing, which is how a real one is built.
                        walk.AddDip(t0 - walk.DipRamp - 2f, CrossingDipLength + walk.DipRamp + 2f);
                        walk.AddDip(L - t1 - CrossingDipLength, CrossingDipLength + walk.DipRamp + 2f);
                    }
                }
                // ⭐ STEPPED OUT ROUND A PARKING BAY, its kerb's corners sampled exactly so the angled ends stay
                // straight lines rather than the nearest 1.5 m station's guess at one
                // ...and its back edge a band of its own width round the angled kerb, clipped in front of the next plot
                List<float> breaks = null;
                foreach (var bay in Bays)
                {
                    if (bay.Road != r) continue;
                    float lead = BayBackLead(bay);
                    (breaks ??= new List<float>()).AddRange(new[] { bay.A, bay.A + bay.Taper, bay.B - bay.Taper, bay.B,
                                                                   bay.Front0 - 0.005f, bay.Front0 + 0.005f, bay.Front1 - 0.005f, bay.Front1 + 0.005f });
                    foreach (float bk in new[] { bay.A - lead, bay.A + bay.Taper - lead, bay.B - bay.Taper + lead, bay.B + lead })
                        if (bk > bay.Front0 + 0.01f && bk < bay.Front1 - 0.01f) breaks.Add(bk);
                }
                int road = r;
                bool kerbOnly = ProfileOf(r) == StreetProfile.KerbOnly;     // (TownVerges: a kerb stone, landings at its ends)
                int Lay(EditorSidewalk.Side side, float from, float toEnd, string piece = null, float min = -1f) => kerbOnly
                    ? LayKerbOnly(walk, roads, road, side, from, toEnd, piece, min, Mathf.Abs(from - t0) < 1e-3f, Mathf.Abs(toEnd - t1) < 1e-3f)
                    : breaks == null
                    ? walk.LayAlongRoad(walk, roads, road, side, null, from, toEnd, piece: piece, minLength: min)
                    : walk.LayAlongRoad(walk, roads, road, side, null, from, toEnd,
                                        d => BayExtra(road, 1, d), d => BayExtra(road, -1, d), breaks,
                                        d => BayBack(road, 1, d), d => BayBack(road, -1, d), piece: piece, minLength: min);
                // ⭐ OPEN WHERE A LANE COMES IN (BuildLanes): that side is laid in pieces round each mouth, whose kerb
                // returns pick the pavement up where it stops
                bool anyMouth = false;
                foreach (var m in LaneMouths) if (m.Road == r) anyMouth = true;
                // (a parking lot's way in is one of these too: a short lane of its own -- TownParking.PlanParkingEntrances)
                if (!anyMouth) { made += Lay(EditorSidewalk.Side.Both, t0, t1); continue; }
                float Lr = roads.RoadLength(r);
                foreach (var (sd, sideEnum) in new[] { (1, EditorSidewalk.Side.Right), (-1, EditorSidewalk.Side.Left) })
                {
                    var cuts = new List<LaneMouth>();
                    foreach (var m in LaneMouths) if (m.Road == r && m.Side == sd) cuts.Add(m);
                    cuts.Sort((x, y) => x.From.CompareTo(y.From));
                    // ⚠ HOWEVER SHORT: a piece between a junction's corner and a mouth, or between two mouths, is pavement a
                    // kerb return picks up -- a parking lot's two-lane way in, opening 1.5 m nearer the corner than a back
                    // lane, left 2.6 m of it, under the two stations a run needs, and that showed as a square of grass
                    float at = t0; int k = 0;
                    foreach (var m in cuts) { made += Lay(sideEnum, at, Lr - m.From, $"p{k++}", 0.05f); at = m.To; }
                    made += Lay(sideEnum, at, t1, cuts.Count > 0 ? $"p{k}" : null, cuts.Count > 0 ? 0.05f : -1f);
                }
            }
            walk.Dips.Clear();
            return made;
        }

        /// <summary>Pavement around the corners of each junction (strawberry 2026-10-12: "can u make the
        /// sidewalks fill the missing curves").
        ///
        /// Each street's pavement now stops where its asphalt does, short of the node -- so every junction is
        /// ringed by pairs of loose ends with a gap between them where the corner should be. This walks the
        /// ends around each node BY ANGLE and arcs the pavement from one to the next.
        /// ⚠ ONLY BETWEEN DIFFERENT STREETS. Sorted by angle, a street's own two pavements are neighbours
        /// too -- the gap between them is its CARRIAGEWAY, and bridging that would pave straight across the
        /// road people drive on.</summary>
        /// <summary>Each corner's centreline and the junction it belongs to, so a test can measure the SHAPE
        /// rather than count meshes: a corner that curved the wrong way built just as many.</summary>
        public readonly List<(Vector3 node, List<Vector3> pts)> LastCorners = new();

        /// <param name="gap">Unused; the kerb's offset comes from the walk's own RoadGap. ⚠ It was a second
        /// copy of that number and they drifted the moment RoadGap changed: the straight runs moved to a 3 cm
        /// LAP over the asphalt while every corner stayed on a 5 cm SETBACK, so each corner met its street
        /// 8 cm out of line (strawberry: "slight misalignment with the sidewalk curves and the sidewalks").
        /// One number, read from one place.</param>
        public int LaySidewalkCorners(Node3D parent, RoadField roads, EditorSidewalk walk, float gap = 0.05f)
        {
            if (parent == null || roads == null || walk == null) return 0;
            LastCorners.Clear(); _cornerFill.Clear(); _roadSamples.Clear();
            var slabTop = SlabTops(roads);
            walk.Width = PavementWidth;       // the corners are exactly as wide as the streets' pavements they join
            _walkWidth = walk.Width;          // plots start behind this pavement
            int made = 0;
            // ⚠ TALLY WHY A PAIR IS DROPPED. "We lose the 3 way junction's straight side sidewalk in some
            // pics" -- and the count already said so: 12 tees and 4 bends produced only 8 extra pieces. A
            // count of what was BUILT cannot say which rule ate the rest.
            int nSame = 0, nLong = 0, nThrough = 0, nNoCorner = 0, nFlatAngle = 0, nShortRad = 0, nFillet = 0;
            int nWide = 0, nBackCross = 0; WideCrossAt = Vector3.Zero;
            for (int j = 0; j < roads.JunctionCount; j++)
            {
                var edges = roads.JunctionEdges(j);
                if (edges.Count < 2) continue;
                var node = roads.JunctionPos(j);
                var ring = new List<(float ang, Vector3 p, Vector3 tan, int road, float r)>();
                foreach (var (road, atEnd) in edges)
                {
                    float L = roads.RoadLength(road);
                    float t = atEnd ? Mathf.Max(0f, L - TrimFor(roads, road, true, CornerRadius))
                                    : Mathf.Min(L, TrimFor(roads, road, false, CornerRadius));
                    if (!roads.EvaluateAlong(road, t, out var pos, out var tan, snapTerrain: false)) continue;
                    if (ProbeEverything && TrimFor(roads, road, atEnd, CornerRadius) > L * 0.5f)
                        Log.Print($"[towngrid] corner ring: junction {j} road {road} {(atEnd ? "end" : "start")} trimmed "
                                + $"{TrimFor(roads, road, atEnd, CornerRadius):0.0} m of {L:0.0} -> sampled at {t:0.0} m, y {pos.Y:0.000} "
                                + $"(node y {node.Y:0.000})");
                    var right = new Vector3(tan.Z, 0f, -tan.X).Normalized();
                    float off = Mathf.Max(0.1f, roads.RoadHalfWidth(road) + walk.RoadGap);   // exactly what LayAlongRoad uses
                    foreach (int sgn in new[] { 1, -1 })
                    {
                        var e = pos + right * (off * sgn);
                        var d = new Vector3(e.X - node.X, 0f, e.Z - node.Z);
                        if (d.Length() < 0.5f) continue;
                        ring.Add((Mathf.Atan2(d.Z, d.X), new Vector3(e.X, pos.Y, e.Z), tan.Normalized(), road, d.Length()));
                    }
                }
                if (ring.Count < 2) continue;
                ring.Sort((a, b) => a.ang.CompareTo(b.ang));
                if (DumpJunction == j)
                {
                    Log.Print($"[towngrid] junction {j} at {node}: {edges.Count} edges");
                    foreach (var (road, atEnd) in edges)
                        Log.Print($"[towngrid]   road {road} {(atEnd ? "end" : "start")}: length {roads.RoadLength(road):0.0}, trim {TrimFor(roads, road, atEnd, CornerRadius):0.0}, half-width {roads.RoadHalfWidth(road):0.0}");
                    foreach (var rr in ring) Log.Print($"[towngrid]   ring {Mathf.RadToDeg(rr.ang):0} deg: road {rr.road} at {rr.p}");
                }
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    if (a.road == b.road) { nSame++; continue; }       // that gap is the carriageway
                    float sweep = b.ang - a.ang; while (sweep <= 0f) sweep += Mathf.Tau;
                    // ⚠⚠ EVERY ADJACENT PAIR GETS SOMETHING, or the kerb ring has a hole in it. The "long way
                    // round" guard dropped its pair outright, and Through() can bail too -- between them that
                    // left 6 missing pieces across 25 junctions, each one a visible break in the pavement
                    // right where it meets the road. master, pointing at the pic: "stilllll got gaps between
                    // the road spline and the merged mesh". They were in the PAVEMENT, not the asphalt, and
                    // three rounds of probing the road/patch seam could never have found them.
                    if (sweep > Mathf.Pi * 1.05f) { nLong++; Through(); continue; }
                    // ⚠ A TEE'S FAR SIDE IS NOT A CORNER, AND SKIPPING IT LEFT A HOLE (strawberry: "add the
                    // straight sidewalks to the 3 way junctions"). Where two arms run THROUGH a node -- the
                    // side of a tee opposite its stem, or a street simply bending -- their kerbs are parallel
                    // and there is nothing to round: the pavement carries straight across the mouth. The old
                    // rule threw that pair away for being too straight to fillet, which is exactly backwards.
                    // ⚠ THE TEST IS "ARE THE KERBS PARALLEL", NOT "IS THE ANGLE BIG". The first cut used the
                    // sweep around the node and missed every tee: the two far-side pavement ends of a
                    // straight-through street subtend only ~126 degrees there, because each sits a half
                    // width off the centreline well out along its arm. Two collinear arms have ANTI-PARALLEL
                    // tangents, which is exactly the case KerbCorner already reports as "no corner".
                    // ⚠⚠ "CANNOT BE FILLETED" AND "IS A THROUGH SIDE" ARE THE SAME CASE, and splitting them
                    // is what lost master the tee pavements: "we lose the 3 way junction's straight side
                    // sidewalk in some pics". KerbCorner only reports parallel when the two tangents are
                    // EXACTLY anti-parallel -- measured, that was 4 pairs -- while 7 more were a hair off,
                    // succeeded, and were then dropped by the fillet path's own "too straight to round"
                    // guard. Both mean the same thing on the ground: there is no corner here, carry the
                    // pavement straight across the mouth.
                    bool Through()
                    {
                        var dir = new Vector3(b.p.X - a.p.X, 0f, b.p.Z - a.p.Z);
                        float run2 = dir.Length();
                        if (run2 < 0.5f) return false;
                        dir /= run2;
                        var nrm = new Vector3(-dir.Z, 0f, dir.X);
                        // outward = away from the junction, the side the pavement is on
                        if (nrm.Dot(new Vector3(a.p.X - node.X, 0f, a.p.Z - node.Z)) < 0f) nrm = -nrm;
                        int sn = Mathf.Max(1, Mathf.CeilToInt(run2 / 1.5f));
                        var sp2 = new List<Vector3>(sn + 1); var so = new List<Vector3>(sn + 1);
                        for (int k = 0; k <= sn; k++)
                        {
                            float f = (float)k / sn;
                            sp2.Add(new Vector3(Mathf.Lerp(a.p.X, b.p.X, f), Mathf.Lerp(a.p.Y, b.p.Y, f), Mathf.Lerp(a.p.Z, b.p.Z, f)));
                            so.Add(nrm);
                        }
                        var smi = EditorSidewalk.BuildRibbon(sp2, so, walk.Width, walk.KerbHeight, walk.Skirt,
                                                             $"Sidewalk_Through_{j}_{i}",
                                                             Terrain.Active != null && !EditorSidewalk.NoGround
                                                                 ? (x, z) => Terrain.Active.SampleHeight(x, z) : null);
                        if (smi == null) return false;
                        nThrough++;
                        LastCorners.Add((node, sp2));
                        parent.AddChild(smi);
                        var sbody = new StaticBody3D { Name = smi.Name + "_body", CollisionLayer = 1u << 0 };
                        sbody.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                        sbody.AddChild(new CollisionShape3D { Shape = smi.Mesh.CreateTrimeshShape() });
                        smi.AddChild(sbody);
                        made++;
                        return true;
                    }
                    // ⚠⚠ A CORNER IS A TANGENT FILLET OF FIXED RADIUS, not an arc through the two pavement
                    // ends (master: "your corners go the opposite way, they go outside, should go inside").
                    // Two wrong versions got here first, and each looked like a plausible reading of it:
                    //   - centred on the NODE: the pavement curves AROUND the junction, concave toward it and
                    //     bulging out into the block. That is a street corner turned inside out.
                    //   - centred on the kerb corner but running A->B directly: the ends are ~18 m out along
                    //     each arm (tiny's fillet cut), so the arc subtends almost a full turn and closes
                    //     into a loop enclosing a disc of grass.
                    // The real thing: the two kerb lines cross at P, the block's sharp corner; the kerb runs
                    // STRAIGHT to a tangent point a fixed distance short of P, turns through one arc of
                    // radius R about a centre on the bisector, and runs straight out the other side.
                    if (!KerbCorner(a.p, a.tan, b.p, b.tan, out var P)) { nNoCorner++; Through(); continue; }
                    var dA = new Vector3(a.p.X - P.X, 0f, a.p.Z - P.Z);
                    var dB = new Vector3(b.p.X - P.X, 0f, b.p.Z - P.Z);
                    float lA = dA.Length(), lB = dB.Length();
                    if (lA < 1f || lB < 1f) { nShortRad++; Through(); continue; }
                    dA /= lA; dB /= lB;
                    float theta = Mathf.Acos(Mathf.Clamp(dA.Dot(dB), -1f, 1f));
                    // ⚠ too straight to round is NOT a reason to leave a hole -- it is the through case.
                    if (theta > Mathf.Pi - 0.25f) { nFlatAngle++; Through(); continue; }
                    if (theta < 0.25f) { nFlatAngle++; Through(); continue; }    // a hairpin: still bridge it
                    float half2 = theta * 0.5f;
                    float rad = Mathf.Min(CornerKerbRadius, Mathf.Min(lA, lB) * 0.8f * Mathf.Tan(half2));
                    // ⚠⚠ AN ACUTE CORNER TURNS IN THE LENGTH A RIGHT ANGLE DOES (strawberry 2026-10-12: "the
                    // crossing + stop line + traffic light/stop sign on angled joins should be way closer to the
                    // road its joining"). A fillet of radius r needs r / tan(θ/2) of kerb either side, so at 45
                    // degrees this corner's ~5 m radius ran 12 m up each kerb -- and a crossing has to stand clear
                    // of the curve, so it stood up there too. Capped at R·tan(θ/2) the curve is R long, as at a
                    // right angle, and the corner is a tighter nose. Square and blunt corners are untouched.
                    if (theta < Mathf.Pi * 0.5f - Mathf.DegToRad(0.5f)) rad = Mathf.Min(rad, CornerKerbRadius * Mathf.Tan(half2));
                    if (rad < 0.4f) { nShortRad++; Through(); continue; }
                    float tdist = rad / Mathf.Tan(half2);
                    var T1 = P + dA * tdist; var T2 = P + dB * tdist;
                    var bis = (dA + dB).Normalized();
                    var C = P + bis * (rad / Mathf.Sin(half2));
                    // ⚠ THE OUTWARD IS PERPENDICULAR ON THE STRAIGHTS AND RADIAL ONLY ON THE ARC. Using the
                    // radial everywhere skews the first cross-section away from the street's own, and the two
                    // ribbons meet at a visible notch even though they share an endpoint. They agree exactly
                    // at the tangent point, because that is what tangent means -- so the two rules join
                    // without a step.
                    Vector3 Radial(Vector3 at) => new Vector3(C.X - at.X, 0f, C.Z - at.Z).Normalized();
                    Vector3 Perp(Vector3 d, Vector3 at)
                    {
                        var nn = new Vector3(-d.Z, 0f, d.X);
                        return nn.Dot(new Vector3(C.X - at.X, 0f, C.Z - at.Z)) < 0f ? -nn : nn;
                    }
                    // ⚠⚠ EVERY STATION AT ITS OWN STREET'S HEIGHT (strawberry 2026-10-13, "follow gentle hills and slopes").
                    // Each straight was held at ONE height, its street's where the ring sampled it -- and at an acute corner
                    // that sample is a long way out: at a 45-degree crossing the wide pavements' back edges meet 30 m up each
                    // street, and on a short street the trim runs off its far end altogether. On flat ground one height is
                    // every height. On a hill the corner by the 45-degree crossing came out 1.4 m under its own streets and
                    // the graded ground buried it, with its asphalt (the probe: 97 and 104 cm proud). Each straight is now
                    // stationed every couple of metres, each station at its street's spline where it passes nearest, and
                    // the arc runs between its two ends' heights.
                    // ⚠ AND BESIDE THE JUNCTION, THE SLAB'S. Past its street's end a station stands beside the junction slab,
                    // which on a hill is not its street's end height: tiny's slab takes every arm's height at once, and a
                    // 45-degree corner by a junction whose arms climb 1.8 m from one to the next stood 58 cm under it with the
                    // ground over it. So: beside its street, the street's spline where it passes; past the street's end, the
                    // slab it borders (found walking in from the kerb); neither, what it would have had.
                    bool beside = false;
                    float FootAt(int road, Vector3 at, Vector3 outw, float fallback)
                    {
                        float y = RoadNear(roads, road, at, out float along, out _);
                        beside = along <= 0.05f || along >= roads.RoadLength(road) - 0.05f;
                        if (!beside) return y;
                        for (float inw = 0.3f; inw <= 6f; inw += 0.3f)
                        {
                            var q = at - outw * inw;
                            float top = slabTop(q.X, q.Z);
                            if (!float.IsNaN(top)) return top - PatchLift - roads.RoadSurfaceOffset(road);
                        }
                        return float.IsNaN(fallback) ? y : fallback;
                    }
                    var cp = new List<Vector3>(); var co = new List<Vector3>(); var byJ = new List<bool>();
                    void Push(Vector3 at, float y, Vector3 outw) { cp.Add(new Vector3(at.X, y, at.Z)); co.Add(outw); byJ.Add(beside); }
                    int stA = Mathf.Max(1, Mathf.CeilToInt(Flat(a.p, T1) / 2f));
                    for (int k = 0; k < stA; k++)
                    {
                        var at = a.p.Lerp(T1, (float)k / stA);
                        Push(at, FootAt(a.road, at, Perp(dA, at), float.NaN), Perp(dA, at));
                    }
                    int onACount = stA + 1;                                     // ...and T1
                    float yT1 = FootAt(a.road, T1, Perp(dA, T1), float.NaN);
                    Push(T1, yT1, Perp(dA, T1));
                    float yT2 = FootAt(b.road, T2, Perp(dB, T2), float.NaN);
                    float s0 = Mathf.Atan2(T1.Z - C.Z, T1.X - C.X), s1 = Mathf.Atan2(T2.Z - C.Z, T2.X - C.X);
                    float sweepArc = Mathf.Wrap(s1 - s0, -Mathf.Pi, Mathf.Pi);
                    // ⚠⚠ TESSELLATE BY ANGLE, NOT BY CHORD LENGTH. A fixed 1.2 m chord on a 6 m radius is
                    // 11.3 degrees per facet -- measured -- and BuildRibbon shades per face, so each facet
                    // reads as a jog and the run of them reads as a WIGGLE (strawberry: "sidewalks seem to
                    // wiggle before connecting to the curved ones"). It is not the road: the streets' own
                    // bow measures 0.0 cm. Capping the turn per segment keeps a tight corner smooth and
                    // stops a wide one from being tessellated for nothing.
                    int steps = Mathf.Max(4, Mathf.CeilToInt(Mathf.Abs(sweepArc) / Mathf.DegToRad(ArcFacetDegrees)));
                    for (int k = 1; k < steps; k++)
                    {
                        float ang = s0 + sweepArc * k / steps;
                        var at = new Vector3(C.X + Mathf.Cos(ang) * rad, 0f, C.Z + Mathf.Sin(ang) * rad);
                        float yk = FootAt(k * 2 < steps ? a.road : b.road, at, Radial(at), Mathf.Lerp(yT1, yT2, (float)k / steps));
                        beside = true;                                          // the arc is the junction's, always
                        Push(at, yk, Radial(at));
                    }
                    int stB = Mathf.Max(1, Mathf.CeilToInt(Flat(T2, b.p) / 2f)), firstOnB = cp.Count;
                    for (int k = 0; k <= stB; k++)
                    {
                        var at = T2.Lerp(b.p, (float)k / stB);
                        float yk = FootAt(b.road, at, Perp(dB, at), float.NaN);
                        Push(at, k == 0 ? yT2 : yk, Perp(dB, at));
                    }

                    // ⚠⚠ A PAVEMENT WIDER THAN ITS CORNER'S RADIUS MEETS ITSELF IN A POINT (strawberry 2026-10-12: "make
                    // them twice as wide everywhere"). Each section's back edge is its kerb pushed Width in toward the
                    // arc's centre, and where the arc is tighter than that -- every 45-degree corner, whose kerb turns
                    // in 2.5 m -- the back edge passes the centre and folds back over the pavement. What the back edge
                    // does instead is what a property line does: the two straights' back edges run on until they meet,
                    // at Q on the bisector, and every section between them ends there.
                    List<float> cw = null, cs = null;
                    float wide = walk.Width;
                    if (wide > rad - 0.01f)
                    {
                        nWide++;
                        var Q = P + bis * (wide / Mathf.Sin(half2));
                        float sQ = wide / Mathf.Tan(half2);         // how far from P, along either kerb, the back edges meet
                        if (lA < sQ || lB < sQ) { if (nBackCross++ == 0) WideCrossAt = P; }   // ...beyond where the street's own pavement stops
                        cw = new List<float>(cp.Count); cs = new List<float>(cp.Count);
                        // ⚠ ONE HEIGHT FOR Q (strawberry 2026-10-13, "follow gentle hills and slopes"). Each section's back point
                        // is Q, but each section stands at its own height -- the corner's two streets climb or fall away from
                        // each other on a hill -- so the fan onto Q had its apex at a different height for every triangle and
                        // the corner twisted into a hump. Every section that ends at Q now ends at Q's one height, the mean
                        // of its two streets'; the walking surface leans from each kerb to it.
                        bool PastQ(int q)
                        {
                            var rel = new Vector3(cp[q].X - P.X, 0f, cp[q].Z - P.Z);
                            return q < onACount && rel.Dot(dA) > sQ || q >= firstOnB && rel.Dot(dB) > sQ;
                        }
                        float qSum = 0f; int qN = 0;
                        for (int q = 0; q < cp.Count; q++) if (!PastQ(q)) { qSum += cp[q].Y; qN++; }
                        float qY = (qN > 0 ? qSum / qN : (yT1 + yT2) * 0.5f) + walk.KerbHeight;
                        for (int q = 0; q < cp.Count; q++)
                        {
                            var at = cp[q];
                            if (PastQ(q)) { cw.Add(wide); cs.Add(walk.KerbHeight); continue; }
                            var toQ = new Vector3(Q.X - at.X, 0f, Q.Z - at.Z);
                            float lq = toQ.Length();
                            if (lq > 1e-4f) co[q] = toQ / lq;
                            cw.Add(lq);
                            cs.Add(qY - at.Y);
                        }
                    }
                    // the corner IS the crossing, so its kerb is the dropped one all the way round
                    var ck = new List<float>(cp.Count);
                    for (int q = 0; q < cp.Count; q++) ck.Add(CrossingDips ? walk.DipKerb : walk.KerbHeight);
                    var mi = EditorSidewalk.BuildRibbon(cp, co, walk.Width, walk.KerbHeight, walk.Skirt,
                                                        $"Sidewalk_Corner_{j}_{i}",
                                                        Terrain.Active != null && !EditorSidewalk.NoGround
                                                            ? (x, z) => Terrain.Active.SampleHeight(x, z) : null, ck, cw, cs);
                    if (mi == null) continue;
                    LastCorners.Add((node, cp));
                    // ⚠⚠ AND THE ASPHALT UNDER IT, from the node out to this exact kerb. At 45 degrees the
                    // corner between two kerb lines is long and thin, and tiny's junction slab rounds it with
                    // its OWN fillet -- which stops short of this one, leaving a wedge of grass inside the
                    // junction (the arm walk measured slivers to 110 cm; the clip was ruled out by turning it
                    // off and getting the identical numbers). Paving from the SAME geometry the kerb is laid
                    // from is the only way the two can agree at every angle. It rides 1 cm under the road, so
                    // wherever a ribbon or the slab already is, they win and nothing changes; only a hole
                    // ever shows it.
                    // ⚠ ON A HILL, ONLY WHERE IT IS NEEDED, AND IN RINGS (strawberry 2026-10-13, "follow gentle hills and
                    // slopes"). Beside a street its own ribbon is the surface, and a fan triangle from the node to a kerb
                    // station 30 m up the street is a plane the street's profile sags under; so the fan runs only to the
                    // stations the junction borders, and out from the node in rings a couple of metres apart, so the clamp
                    // under the paving above it (BuildCornerFill) has points to hold down.
                    {
                        float surf = roads.RoadSurfaceOffsetOfMaterial(StreetMaterial) - CornerFillDrop;
                        float far = 0f;
                        for (int q = 0; q < cp.Count; q++) if (byJ[q]) far = Mathf.Max(far, Flat(node, cp[q]));
                        int rings = Mathf.Max(1, Mathf.CeilToInt(far / 2f));
                        Vector3 R(int q, int band)
                        {
                            float f = (float)band / rings;
                            return new Vector3(Mathf.Lerp(node.X, cp[q].X, f), Mathf.Lerp(node.Y, cp[q].Y, f) + surf, Mathf.Lerp(node.Z, cp[q].Z, f));
                        }
                        for (int q = 0; q + 1 < cp.Count; q++)
                        {
                            if (!byJ[q] && !byJ[q + 1]) continue;
                            for (int band = 0; band < rings; band++)
                            {
                                var i0 = R(q, band); var i1 = R(q + 1, band); var o0 = R(q, band + 1); var o1 = R(q + 1, band + 1);
                                if (band > 0) _cornerFill.Add((i0, i1, o1));
                                _cornerFill.Add((i0, o1, o0));
                            }
                        }
                    }
                    nFillet++;
                    parent.AddChild(mi);
                    var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                    body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                    body.AddChild(new CollisionShape3D { Shape = mi.Mesh.CreateTrimeshShape() });
                    mi.AddChild(body);
                    made++;
                }
            }
            int attempted = nLong + nNoCorner + nFlatAngle + nShortRad;
            int unpaved = attempted - nThrough;
            Log.Print($"[towngrid] kerb ring: {unpaved} ADJACENT PAIRS LEFT UNPAVED"
                    + (unpaved > 0 ? "  <-- each one is a visible break in the pavement" : "  (ring closed)"));
            Log.Print($"[towngrid] kerb pieces: {nFillet} fillets, {nThrough} straight-through; dropped "
                    + $"{nSame} same-road, {nLong} long-way, {nNoCorner} no-corner, {nFlatAngle} too-straight, {nShortRad} tiny-radius; "
                    + $"{nWide} corners tighter than the {walk.Width:0.0} m pavement (back edge meets in a point), "
                    + $"{nBackCross} of them where the streets' own back edges cross first{(nBackCross > 0 ? $" (first at {WideCrossAt})" : "")}");
            WideCrossCount = nBackCross;
            return made;
        }
        /// <summary>The last LaySidewalkCorners' count of corners where two streets' wide pavements still cross before
        /// their corner piece begins -- a road too short to hold them back -- and where the first one is.</summary>
        public int WideCrossCount; public Vector3 WideCrossAt;
        /// <summary>How far each street runs UNDER the junction patch, hiding its open slab end.</summary>
        public float PatchOverlap = 0.35f;

        /// <summary>Drop the kerb where each pavement meets a junction -- the crossing points.
        ///
        /// ⚠ OFF BY DEFAULT, OPT IN WITH UG_TOWNDIPS=1 (strawberry 2026-10-12, on recognising what the
        /// "wiggle" actually was: "ohhh those are the ramps lol, my bad. cut the ramps out for now"). The
        /// dipped-kerb SUPPORT stays in EditorSidewalk, which is what was asked for -- this is only the
        /// town's automatic placement of them at every junction approach.</summary>
        public bool CrossingDips = System.Environment.GetEnvironmentVariable("UG_TOWNDIPS") == "1";
        public float CrossingDipLength = 3f;

        /// <summary>Street lights down the pavements and traffic signals at the junctions (strawberry
        /// 2026-10-12: "add traffic lights around junctions and street lights populating the sidewalks").
        ///
        /// ⚠ PLACED THROUGH EditorObjects, not as bare meshes, because that is what makes them PROPS: the
        /// catalog row, the collider, the surface tag, and the placements save all come from there. A
        /// hand-built MeshInstance would look identical in a render and be invisible to everything else.
        /// ⚠ AND SEATED WITH SplineTiling.SeatedBasis, which is FromEuler(270, yaw, 0) plus a terrain tilt:
        /// props are authored Z-UP and lie flat without it. Street_Light_0's pole runs along its local +Z.</summary>
        public int PlaceLights(RoadField roads, EditorObjects objs, List<Node3D> into = null)
        {
            if (roads == null || objs == null) return 0;
            int made = 0;
            var terr = Terrain.Active;

            // ⚠ KNOWN-ANSWER CHECK BEFORE THE CENSUS BELOW IS WORTH READING. A junction centre and a road's
            // own centreline are inside a carriageway by definition; a point a kilometre away is not. If
            // these three do not read inside/inside/clear the census is measuring nothing, whatever it says.
            if (roads.RoadCount > 0 && roads.JunctionCount > 0)
            {
                var jc = roads.JunctionPos(0);
                bool a = roads.DeepestRoadOverlap(jc, 0.1f, out int ar, out float ad);
                roads.EvaluateAlong(Built.Count > 0 ? Built[0] : 0, roads.RoadLength(Built.Count > 0 ? Built[0] : 0) * 0.5f,
                                    out var mid, out _, snapTerrain: false);
                bool b = roads.DeepestRoadOverlap(mid, 0.1f, out int br, out float bd);
                bool c = roads.DeepestRoadOverlap(jc + new Vector3(1000f, 0f, 1000f), 0.1f, out _, out _);
                GD.Print($"[towngrid] overlap calibration: junction centre road-only {(a ? $"inside {ar} by {ad:F2} m" : "clear")} " +
                         $"+ patches {(ClearOfRoads(roads, jc) ? "CLEAR (WRONG)" : "on asphalt")}, " +
                         $"road mid {(b ? $"INSIDE road {br} by {bd:F2} m" : "clear (WRONG)")}, " +
                         $"far point {(c || !ClearOfRoads(roads, jc + new Vector3(1000f, 0f, 1000f)) ? "inside (WRONG)" : "clear")}");
            }

            // ---- PARKING METERS FIRST, one to a space along every paid zone. ⭐ THEY OUTRANK THE LAMPS (strawberry 2026-10-15:
            // "meters should override lamp posts, by shoving the lamppost away"): a space's meter stands where its space says,
            // and the lamps below are shoved along the pavement to keep MeterClear off every one of them. (The meters used to
            // be placed last and give way -- a space without its meter, and still a lamp 1.33 m from the next one.)
            MetersPlaced = MetersCrowded = PaidZones = FreeZones = 0; LampsShoved = LampsLost = HydrantsSlid = HydrantsOffBays = 0;
            foreach (var bay in Bays) { if (bay.Paid) PaidZones++; else FreeZones++; }
            var meters = new List<(Vector3 at, Vector3 outw)>();
            if (ParkingMeters)
                foreach (var bay in Bays)
                {
                    if (!bay.Paid) continue;
                    for (int k = 0; k < bay.Spaces; k++)
                    {
                        float d = bay.First + (k + 0.5f) * bay.SpaceLength;
                        if (!roads.EvaluateAlong(bay.Road, d, out var pos, out var tan, snapTerrain: false)) continue;
                        var outw = new Vector3(tan.Z, 0f, -tan.X).Normalized() * bay.Side;     // from the road out to this pavement
                        var at = pos + outw * (roads.RoadHalfWidth(bay.Road) + BayExtra(bay.Road, bay.Side, d) + RoadGapForLights + MeterSetback);
                        at.Y = PavementTopAt(pos, at, terr);
                        meters.Add((at, outw));
                    }
                }
            bool NearMeter(Vector3 q) { foreach (var (m, _) in meters) if (Flat(m, q) < MeterClear) return true; return false; }

            var standing = new List<Vector3>();     // the lamps and hydrants up so far
            // ---- STREET LIGHTS, down each street's pavement.
            if (StreetLights)
                foreach (int r in Built)
                {
                    float L = roads.RoadLength(r);
                    float t0 = TrimFor(roads, r, false, CornerRadius), t1 = TrimFor(roads, r, true, CornerRadius);
                    float from = t0 + LampSpacing * 0.5f, to = L - t1;
                    if (to - from < 1f) continue;
                    int n = Mathf.Max(1, Mathf.FloorToInt((to - from) / LampSpacing));
                    float half = roads.RoadHalfWidth(r);
                    // where a lamp at arc d on `side` stands, or false where it may not (a mouth, a lot's way in, off the paving)
                    bool LampAt(float d, int side, out Vector3 at, out Vector3 right)
                    {
                        at = right = Vector3.Zero;
                        if (MouthAt(r, side, d - 1f, d + 1f) != null) return false;      // the lane's way in
                        if (EntranceAt(r, side, d - 1.5f, d + 1.5f) != null) return false;   // a parking lot's
                        if (!roads.EvaluateAlong(r, d, out var pos, out var tan, snapTerrain: false)) return false;
                        right = new Vector3(tan.Z, 0f, -tan.X).Normalized();
                        // ...on the pavement wherever it is, which round a parking bay is a bay's depth further out
                        at = pos + right * (side * (half + BayExtra(r, side, d) + RoadGapForLights + LampSetback));
                        if (!PushClear(roads, ref at, right * side)) return false;
                        at.Y = PavementTopAt(pos, at, terr);
                        return true;
                    }
                    for (int k = 0; k <= n; k++)
                    {
                        float d0 = from + k * LampSpacing;
                        if (d0 > to) break;
                        // ⚠ ALTERNATE SIDES. Lamps down one kerb only light one half of a street and look like
                        // a runway; staggering them is what a real street does and halves the count.
                        int side = (k % 2 == 0) ? 1 : -1;
                        if (!LampAt(d0, side, out var at, out var right)) continue;
                        if (NearMeter(at))
                        {
                            // shoved along its pavement to the nearest spot either way clear of every meter -- never past the
                            // street's own trimmed ends, never into a mouth or a lot's way in
                            bool found = false;
                            for (float shove = 0.5f; shove <= LampSpacing * 0.5f && !found; shove += 0.5f)
                                foreach (float sg in new[] { 1f, -1f })
                                {
                                    float d = d0 + sg * shove;
                                    if (d < t0 + 1f || d > to) continue;
                                    if (LampAt(d, side, out var a2, out var r2) && !NearMeter(a2)) { at = a2; right = r2; found = true; break; }
                                }
                            if (!found) { LampsLost++; continue; }
                            LampsShoved++;
                        }
                        // the arm has to reach OVER the carriageway, so the lamp faces across the road
                        float yaw = ProcIsland.YawForDir(-right.X * side, right.Z * side);
                        var basis = SplineTiling.SeatedBasis(PavementGround(terr), at, yaw, 6f, out _);
                        var node = objs.Place(StreetLightProp, at, basis);
                        if (node != null) { into?.Add(node); made++; standing.Add(at); }
                    }
                }

            // ---- HYDRANTS, sparse, on the pavement (strawberry 2026-10-12: "add hydrants to the sidewalks,
            // somewhat uncommon"). ⚠ DETERMINISTIC, like everything else the grid places: a hydrant that
            // moved between launches would be a prop appearing and disappearing in a saved town.
            // ⭐ NEVER BESIDE A PARKING BAY (strawberry 2026-10-15: "parking cubbies shouldnt spawn hydrants bc.. parking in front of
            // a hydrant"): a hydrant keeps HydrantBayClear off either end of every bay on its side of the street, sliding along
            // the kerb -- or across to the other side -- to the nearest spot that does; none, and the street has no hydrant.
            if (Hydrants)
                foreach (int r in Built)
                {
                    ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix((ulong)(uint)r ^ 0x4879UL));
                    if (Unit(h, 0) > HydrantChance) continue;
                    float L = roads.RoadLength(r);
                    float t0 = TrimFor(roads, r, false, CornerRadius), t1 = TrimFor(roads, r, true, CornerRadius);
                    if (L - t0 - t1 < 12f) continue;
                    float lo = t0 + 4f, hi = L - t1 - 4f;
                    float want = Mathf.Lerp(lo, hi, Unit(h, 16));
                    var side0 = ((h >> 40) & 1UL) == 0 ? 1 : -1;
                    bool oldRule = System.Environment.GetEnvironmentVariable("UG_TOWNHYDRANTBAYS") == "1";   // the control: as before
                    bool BesideBay(int side, float d)
                    {
                        if (oldRule) return false;
                        foreach (var bay in Bays)
                            if (bay.Road == r && bay.Side == side && d > Mathf.Min(bay.A, bay.B) - HydrantBayClear && d < Mathf.Max(bay.A, bay.B) + HydrantBayClear) return true;
                        return false;
                    }
                    bool placedH = false;
                    bool wantedBay = BesideBay(side0, want);
                    if (wantedBay && HydrantsOffBays++ == 0 && roads.EvaluateAlong(r, want, out var wbp, out _, snapTerrain: false)) FirstHydrantOffBay = wbp;
                    for (float slide = 0f; slide <= hi - lo && !placedH; slide += 1f)
                        foreach (float sg in slide == 0f ? new[] { 1f } : new[] { 1f, -1f })
                        {
                            float d = want + sg * slide;
                            if (d < lo || d > hi) continue;
                            foreach (int side in new[] { side0, -side0 })
                            {
                                if (BesideBay(side, d) || MouthAt(r, side, d - 1.5f, d + 1.5f) != null || EntranceAt(r, side, d - 1.5f, d + 1.5f) != null) continue;
                                if (!roads.EvaluateAlong(r, d, out var pos, out var tan, snapTerrain: false)) continue;
                                var rr = new Vector3(tan.Z, 0f, -tan.X).Normalized();       // symmetric use: either kerb will do
                                var at = pos + rr * (side * (roads.RoadHalfWidth(r) + BayExtra(r, side, d) + RoadGapForLights + HydrantSetback));
                                if (!PushClear(roads, ref at, rr * side)) continue;
                                bool crowd = !oldRule && NearMeter(at);
                                if (!oldRule) foreach (var q in standing) if (Flat(q, at) < MeterClear) { crowd = true; break; }
                                if (crowd) continue;
                                at.Y = PavementTopAt(pos, at, terr);
                                var hb = SplineTiling.SeatedBasis(PavementGround(terr), at, Unit(h, 32) * 360f, 6f, out _);
                                var hn = objs.Place(HydrantProp, at, hb);
                                if (hn != null) { into?.Add(hn); made++; standing.Add(at); }
                                if (slide > 0f || side != side0)
                                {
                                    if (HydrantsSlid++ == 0 && roads.EvaluateAlong(r, want, out var wp, out _, snapTerrain: false)) FirstHydrantSlid = (wp, at);
                                }
                                placedH = true;
                                break;
                            }
                            if (placedH) break;
                        }
                }

            // ...and the meters themselves, now that nothing else stands where they do
            foreach (var (at, outw) in meters)
            {
                // facing its space: like the lamps' arms, back over the road (its front is local +Y, Z-up as they are)
                var mb = SplineTiling.SeatedBasis(PavementGround(terr), at, ProcIsland.YawForDir(-outw.X, outw.Z), 6f, out _);
                var mn = objs.Place(ParkingMeterProp, at, mb);
                if (mn != null) { into?.Add(mn); made++; if (MetersPlaced++ == 0) FirstMeterAt = at; }
            }
            if (Bays.Count > 0)
                GD.Print($"[towngrid] street parking: {PaidZones} zones marked paid, {FreeZones} free; {MetersPlaced} meters, one a space; "
                        + $"{LampsShoved} lamp(s) shoved along to clear one{(LampsLost > 0 ? $", {LampsLost} with nowhere to go" : "")}; "
                        + $"{HydrantsSlid} hydrant(s) slid along (off a bay, a meter or a lamp){(HydrantsSlid > 0 ? $", the first from ({FirstHydrantSlid.wanted.X:0},{FirstHydrantSlid.wanted.Z:0}) to ({FirstHydrantSlid.went.X:0.0},{FirstHydrantSlid.went.Z:0.0})" : "")}"
                        + $"{(HydrantsOffBays > 0 ? $"; {HydrantsOffBays} of them had wanted a spot beside a bay, the first at ({FirstHydrantOffBay.X:0},{FirstHydrantOffBay.Z:0})" : "")}"
                        + $"{(MetersPlaced > 0 ? $"; the first meter at ({FirstMeterAt.X:0.0},{FirstMeterAt.Z:0.0})" : "")}");

            // ---- TRAFFIC SIGNALS, one per approach at a junction that has three or more.
            if (TrafficLights && Junctions != Control.None)
                for (int j = 0; j < roads.JunctionCount; j++)
                {
                    var edges = roads.JunctionEdges(j);
                    if (edges.Count < 3) continue;          // a bend or a dead end gets neither
                    bool anyMain = false;
                    foreach (var (rr, _) in edges) if (IsMain(roads, rr)) anyMain = true;
                    var mode = Junctions == Control.Auto ? (anyMain ? Control.Signals : Control.StopSigns) : Junctions;
                    var node0 = roads.JunctionPos(j);
                    foreach (var (road, atEnd) in edges)
                    {
                        // a main running through is not stopped: no sign on its own approach (two mains
                        // crossing is a full junction, and both are)
                        if (mode == Control.StopSigns && _throughEnds.Contains((road, atEnd))) continue;
                        float L = roads.RoadLength(road);
                        // ⚠⚠ POSITIONED OFF THE NODE, NOT OFF THE ROAD'S CUT (strawberry 2026-10-12: "bring
                        // the traffic lights closer, actually on the intersection, a little before the curve
                        // actually starts"). The cut is wherever tiny's fillet trim happened to land -- about
                        // 18 m out -- so measuring back from it put the signals a further 2.5 m up the street,
                        // nowhere near the intersection. What a signal is actually placed against is the
                        // CORNER: the kerb runs straight to a tangent point at (half width + kerb radius)
                        // from the node and turns there, so the mast stands a metre before that.
                        float arc = atEnd ? Mathf.Max(0f, L - 1f) : Mathf.Min(L, 1f);
                        if (!roads.EvaluateAlong(road, arc, out var pos, out var tan, snapTerrain: false)) continue;
                        var fwd = (atEnd ? tan : -tan).Normalized();      // the way a driver arrives
                        var right = RightOf(fwd);
                        float half = roads.RoadHalfWidth(road);
                        // ⚠ THE WIDEST ARM AT THIS NODE, not this one: a mast set back by a 9 m street's
                        // own half width stands in the 18 m road it is signalling.
                        float widest = half;
                        foreach (var (rr2, _) in edges) widest = Mathf.Max(widest, roads.RoadHalfWidth(rr2));
                        float back = SignalBack(roads, j, road, atEnd, widest, half + RoadGapForLights + 0.6f);
                        // ⚠ ON THE DRIVER'S RIGHT, which is the kerb a mast stands on; its arm then reaches
                        // left over the lanes the drivers approaching here are in.
                        var at = node0 - fwd * back + right * (half + RoadGapForLights + 0.6f);
                        if (!PushClear(roads, ref at, right)) continue;      // never stand it in a carriageway
                        at.Y = PavementTopAt(pos, at, terr);
                        // ⚠ THE MAST REACHES ACROSS THE ROAD, NOT BACK ALONG IT. Measured off the props:
                        // both run their POLE along local +Z and their ARM along local +Y, which the 270
                        // degree stand turns into world -Z -- so the yaw is choosing where the ARM points.
                        // Aiming it down the approach laid the mast parallel to the kerb with its heads side
                        // on. It belongs perpendicular, over the lanes the approaching driver is in.
                        var armDir = -right;                                   // to the driver's left
                        float yaw = ProcIsland.YawForDir(armDir.X, -armDir.Z);
                        var basis = SplineTiling.SeatedBasis(PavementGround(terr), at, yaw, 6f, out _);
                        if (mode == Control.StopSigns)
                        {
                            // ⚠ A SIGN FACES THE DRIVER, so its face looks back ALONG the approach -- the
                            // opposite of the mast's arm, which reaches across it. And it stands at the kerb,
                            // not out over the road.
                            float signBack = SignalBack(roads, j, road, atEnd, widest, half + RoadGapForLights + 0.7f) - 0.5f;
                            var signAt = node0 - fwd * signBack + right * (half + RoadGapForLights + 0.7f);
                            if (!PushClear(roads, ref signAt, right)) continue;
                            signAt.Y = PavementTopAt(pos, signAt, terr);
                            float syaw = ProcIsland.YawForDir(-fwd.X, fwd.Z);
                            var sb = SplineTiling.SeatedBasis(PavementGround(terr), signAt, syaw, 6f, out _);
                            var sn = objs.Place(StopSignProp, signAt, sb);
                            if (sn != null) { into?.Add(sn); made++; }
                            continue;
                        }
                        var tnode = objs.Place(TrafficLightProp, at, basis);
                        if (tnode != null) { into?.Add(tnode); made++; }
                    }
                }
            GD.Print($"[towngrid] prop clearance: {_propTested} sited, {_propPushed} pushed out ({_pushByRoad} off a road, {_pushBySlab} off a junction slab; furthest {_propPushedMax:0.0} m, at {_propPushedAt}), " +
                     $"{_propInRoad} LEFT STANDING IN A CARRIAGEWAY{(NoClear ? "  (UG_TOWNNOCLEAR control)" : "")}");
            return made;
        }
        /// <summary>UG_TOWNNOPROPS=1 drops every pole and hydrant: the control for "is that dark shape a
        /// hole in the mesh, or a mast's shadow lying in it".</summary>
        public static bool NoProps = System.Environment.GetEnvironmentVariable("UG_TOWNNOPROPS") == "1";
        public bool StreetLights = !NoProps, TrafficLights = !NoProps;
        /// <summary>⭐ PARKING METERS (strawberry 2026-10-14: "yes add them, 1 per street parking bay, if the street-parking zone is
        /// marked paid parking"): astraclaw's Parking_Meter_0 (approved 2026-10-14), one at every space of a paid zone, on the
        /// pavement this far in from the kerb's face, level with the middle of its space and facing it.</summary>
        public bool ParkingMeters = !NoProps;
        public string ParkingMeterProp = "Parking_Meter_0";
        public float MeterSetback = 0.45f;
        public int MetersPlaced, PaidZones, FreeZones, MetersCrowded; public Vector3 FirstMeterAt;
        /// <summary>What the last PlaceLights moved for the meters: lamps shoved along their pavement to clear one, lamps that had
        /// nowhere to go within half a lamp spacing, and hydrants slid along the kerb (or across) to keep off the bays.</summary>
        public int LampsShoved, LampsLost, HydrantsSlid, HydrantsOffBays;
        /// <summary>Where the first hydrant that wanted a spot beside a bay wanted it (its street's centreline there).</summary>
        public Vector3 FirstHydrantOffBay;
        /// <summary>The first slid hydrant: where its street's centreline was at the spot it wanted, and where it went.</summary>
        public (Vector3 wanted, Vector3 went) FirstHydrantSlid;
        /// <summary>⭐ PAID OR FREE: a zone on a main street, or in front of a shop or an apartment block, is marked paid -- the
        /// town centre's kerbside; one in front of houses only, on a side street, parks free.</summary>
        bool PaidZone(RoadField roads, int r, List<(Plot pl, Vector3 was)> fronting)
        {
            if (IsMain(roads, r)) return true;
            foreach (var (pl, _) in fronting) if (pl.Kind == PlotKind.Shop || pl.Kind == PlotKind.Apartments) return true;
            return false;
        }
        /// <summary>Hydrants, and how often a street gets one. Uncommon by default.</summary>
        public bool Hydrants = !NoProps;
        public float HydrantChance = 0.35f;
        public float HydrantSetback = 0.8f;
        /// <summary>How far past either end of a parking bay a hydrant keeps -- nobody may park in front of one.</summary>
        public float HydrantBayClear = 3f;
        public string HydrantProp = "Fire_Hydrant_0";
        public string StreetLightProp = "Street_Light_0", TrafficLightProp = "Traffic_Light_0";
        /// <summary>Metres between lamps; they alternate sides, so one side sees twice this.</summary>
        public float LampSpacing = 32f;
        /// <summary>How far back from the kerb line a lamp's pole stands, onto the pavement.</summary>
        public float LampSetback = 0.9f;
        /// <summary>How far outside the kerb's tangent point -- where the corner's curve begins -- a signal
        /// stands. Small: the mast belongs at the intersection, not up the street.</summary>
        public float SignalBeforeCurve = 1f;
        /// <summary>How far behind a stop line with no crossing its sign or mast stands.</summary>
        public float SignBehindStop = 0.3f;
        /// <summary>⚠ THE SAME OFFSET THE PAVEMENT USES, not a third copy of it. Props stand on the kerb, so
        /// if this and EditorSidewalk.RoadGap disagree they stand off the edge of it.</summary>
        float RoadGapForLights => PavementGap;
        /// <summary>Mirrors EditorSidewalk.RoadGap; negative means the kerb laps the asphalt.</summary>
        public float PavementGap = -0.03f;

        /// <summary>Pedestrian crossings on every approach of a junction (strawberry 2026-10-12: "add
        /// crossings at junctions and intersections, pushing the roads lines back to meet the crossing").
        ///
        /// ⚠ THE ORDER OUTWARD FROM A JUNCTION IS: box, CROSSING, stop line, lane lines. A driver stops
        /// BEHIND the crossing, not on it, so the stop line is pushed out past it rather than the crossing
        /// being squeezed in behind the line -- which is why StopPastEdge is derived from the crossing's own
        /// extent below instead of being a number of its own.
        /// ⚠ ZEBRA STRIPES RUN ALONG THE DIRECTION OF TRAVEL, not across it. Bars across the road is a stop
        /// line repeated, which is a different marking entirely.</summary>
        public int BuildCrossings(Node3D parent, RoadField roads)
        {
            if (!Crossings || roads == null) return 0;
            var mat = roads.MaterialOfMaterial(MainMaterial);
            var uvWhite = new Vector2(0.2363f, 0.0664f);
            int made = 0, slanted = 0, bare = 0;
            var plans = new Dictionary<(int road, bool atEnd), CrossingLayout>();
            for (int j = 0; j < roads.JunctionCount; j++)
            {
                var edges = roads.JunctionEdges(j);
                if (edges.Count < 3) continue;
                foreach (var (road, atEnd) in edges)
                    if (LayCrossing(roads, j, road, atEnd, out var cl)) plans[(road, atEnd)] = cl;
            }
            int dropped = DropCrowdedCrossings(roads, plans);
            _builtCrossings.Clear(); _roadSamples.Clear();
            var slabTop = SlabTops(roads);
            var paving = PavingTops(roads);
            for (int j = 0; j < roads.JunctionCount; j++)
            {
                var edges = roads.JunctionEdges(j);
                if (edges.Count < 3) continue;
                foreach (var (road, atEnd) in edges)
                {
                    if (!plans.TryGetValue((road, atEnd), out var cl)) continue;
                    float half = cl.Half;
                    // ⚠⚠ ON THE SURFACE UNDER EACH POINT, NOT AT ONE HEIGHT (strawberry 2026-10-13, "follow gentle hills and
                    // slopes"). The whole crossing -- apron, bars, stop line -- was painted at the height of one point of its
                    // street, and on a slope the street climbs out from under a few metres of it and falls away from the rest:
                    // half a zebra under the asphalt, the other half hovering (the probe: paint 8 cm under the GROUND at one).
                    // Each corner of each piece now sits on what is under it -- its street's ribbon, or the slab where the
                    // crossing runs onto it, whichever is higher -- by the same lifts as before.
                    // ⚠ AND OVER WHATEVER ELSE IS THERE: a crossing slanted along the road its street joins reaches out over that
                    // road's carriageway too, and on a hill that road is not this one's height -- a bar 34 cm under its asphalt
                    // (the paint-clearance probe). The highest paving under the point wins, a slab's lift over a ribbon kept.
                    // (its own street counts only ON its carriageway: carried out sideways, level, over the other road's, it
                    // stood a crossing's apron over the other's bars on a hill -- the same 33 cm, from the other side)
                    // ⚠ ONE RULE FOR EVERY CROSSING: the same point gets the same height whichever crossing asks. With each
                    // preferring its own street, two crossings over one corner's hole sat 6.5 cm apart and the higher one's
                    // apron covered the other's bar.
                    float Paved(Vector3 q)
                    {
                        float sl = slabTop(q.X, q.Z), anyRoad = paving(q).roadHi;
                        float rd = float.IsNaN(anyRoad) ? float.NaN : anyRoad + PatchLift;
                        return float.IsNaN(sl) ? rd : float.IsNaN(rd) ? sl : Mathf.Max(sl, rd);
                    }
                    float Top(Vector3 q)
                    {
                        float top = Paved(q);
                        if (!float.IsNaN(top)) return top;
                        // over nothing (the corner fill, a kerb's foot): what it would be on the paving nearest -- the fill
                        // there takes that paving too, and two crossings over one hole then agree with each other and with it
                        foreach (float rr in new[] { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f })
                        {
                            float sum = 0f; int n = 0;
                            for (int k8 = 0; k8 < 8; k8++)
                            {
                                float ang = k8 * Mathf.Pi / 4f, t = Paved(q + new Vector3(Mathf.Cos(ang) * rr, 0f, Mathf.Sin(ang) * rr));
                                if (!float.IsNaN(t)) { sum += t; n++; }
                            }
                            if (n > 0) return sum / n;
                        }
                        return cl.PaintY - MarkLift;
                    }
                    var st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    // the bars, across the crossing (the apron goes round them)
                    var painted = new List<(float l0, float l1)>();
                    if (cl.D1 - cl.D0 < 0.01f) { if (bare++ == 0) FirstBareJunction = cl.Node; }   // no crossing here: its stop line alone
                    else if (Zebra)
                    {
                        float pitch = CrossingBar + CrossingGap;
                        int n = Mathf.Max(1, Mathf.FloorToInt((2f * half - CrossingGap) / pitch));
                        float span = n * pitch - CrossingGap, start = -span * 0.5f;
                        for (int k = 0; k < n; k++) { float a = start + k * pitch; painted.Add((a, a + CrossingBar)); }
                    }
                    else
                    {   // the quieter option: just the two outlines of the walkway
                        painted.Add((-half + 0.2f, -half + 0.2f + CrossingBar));
                        painted.Add((half - 0.2f - CrossingBar, half - 0.2f));
                    }
                    // ⚠⚠ A PLAIN-TARMAC APRON UNDER THE CROSSING AND THE STOP LINE. Both sit out on the street's
                    // own RIBBON, past the junction slab, and the ribbon's lane lines are baked into its texture
                    // -- so wherever a centre dash happened to fall, it ran straight through the zebra. It never
                    // showed on one render and always on the next, because it depends on the dash's phase.
                    // Blanking the ribbon from the crossing's near edge to the stop line's far edge is what a
                    // road crew does, and what master asked for: "continue the road texture up to the stop lines".
                    // It rides between the slab and the paint (ApronLift), so it neither fights the slab where
                    // they overlap nor covers the bars painted on it.
                    // ⚠ ONLY WHERE THERE ARE BAKED LINES TO BLANK (strawberry 2026-10-13, on hills). The town's own roads are
                    // plain tarmac with their lines painted on (PaintOwnLines), so there is nothing under a crossing for an
                    // apron to hide -- and the apron was the one surface in the town laid OVER other paint: where two
                    // crossings overlap at an acute corner, on a hill one's apron stood over the other's bars by 6.5 cm.
                    if (!(PaintOwnLines && _plainMain >= 0))
                    {
                        float a0 = cl.D0 - 0.3f, a1 = cl.SD1 + 0.3f, w = half + 0.02f;
                        Vector3 A(float dist, float lat) { var q = cl.At(dist, lat); return new Vector3(q.X, Top(q) + ApronLift, q.Z); }
                        var uvPlain = new Vector2(30f / 256f, 0.5f);   // the plain band between the edge and the first dash
                        void TA(Vector3 a, Vector3 b, Vector3 c)
                        {
                            bool keep = (c - a).Cross(b - a).Y >= 0f;
                            st.SetNormal(Vector3.Up); st.SetUV(uvPlain); st.AddVertex(a);
                            st.SetNormal(Vector3.Up); st.SetUV(uvPlain); st.AddVertex(keep ? b : c);
                            st.SetNormal(Vector3.Up); st.SetUV(uvPlain); st.AddVertex(keep ? c : b);
                        }
                        // ⚠⚠ AROUND THE PAINT, NOT UNDER IT (strawberry 2026-10-13, on hills: "z fighting with some road
                        // markings"). Laid whole under the bars and the stop line, the apron and the paint were two surfaces
                        // 8 mm apart, each bent to the road its own way between its own points -- and where the road bends
                        // under them the apron came up through the paint. Now the apron is everything the paint is not, so
                        // nothing lies over anything; each piece is a grid, a metre along and three quarters across, so it
                        // bends with what it lies on.
                        void Grid(float d0, float d1, float l0, float l1)
                        {
                            if (d1 - d0 < 1e-3f || l1 - l0 < 1e-3f) return;
                            int na = Mathf.Max(1, Mathf.CeilToInt((d1 - d0) / 1f)), nw = Mathf.Max(1, Mathf.CeilToInt((l1 - l0) / 0.75f));
                            for (int ia = 0; ia < na; ia++)
                                for (int iw = 0; iw < nw; iw++)
                                {
                                    float da = Mathf.Lerp(d0, d1, (float)ia / na), db = Mathf.Lerp(d0, d1, (float)(ia + 1) / na);
                                    float la = Mathf.Lerp(l0, l1, (float)iw / nw), lb = Mathf.Lerp(l0, l1, (float)(iw + 1) / nw);
                                    TA(A(da, la), A(da, lb), A(db, lb)); TA(A(da, la), A(db, lb), A(db, la));
                                }
                        }
                        var cutsD = new List<float> { a0, a1 };
                        foreach (float c in new[] { cl.D0, cl.D1, cl.SD0, cl.SD1 }) if (c > a0 && c < a1) cutsD.Add(c);
                        cutsD.Sort();
                        for (int b = 0; b + 1 < cutsD.Count; b++)
                        {
                            float u0 = cutsD[b], u1 = cutsD[b + 1];
                            if (u1 - u0 < 1e-3f) continue;
                            var holes = new List<(float l0, float l1)>();
                            if (u0 >= cl.D0 - 1e-3f && u1 <= cl.D1 + 1e-3f) holes.AddRange(painted);
                            if (u0 >= cl.SD0 - 1e-3f && u1 <= cl.SD1 + 1e-3f) holes.Add((0.1f, half - 0.2f));
                            holes.Sort((x, y) => x.l0.CompareTo(y.l0));
                            float from = -w;
                            foreach (var (h0, h1) in holes) { Grid(u0, u1, from, Mathf.Max(from, h0)); from = Mathf.Max(from, h1); }
                            Grid(u0, u1, from, w);
                        }
                    }
                    // ⚠ A BAR STILL RUNS ALONG THE DIRECTION OF TRAVEL when the crossing is laid at a slant, so
                    // tyres keep rolling between the bars; only its two ENDS follow the band, which makes each
                    // bar a parallelogram rather than a rotated rectangle.
                    // ⚠ PAINT IN A GRID TOO, as the apron under it is: one quad per bar, its corners on the surface, ran
                    // straight between them while the apron bent with the slab, and on a hill the apron came up through the
                    // middle of the bars and the stop lines -- the flicker strawberry saw ("z fighting with some road markings")
                    void PaintQuad(float d0, float d1, float latA, float latB, float alongStep, float acrossStep)
                    {
                        Vector3 V(float d, float lat) { var q = cl.At(d, lat); return new Vector3(q.X, Top(q) + MarkLift, q.Z); }
                        void Tri(Vector3 a, Vector3 b, Vector3 c)
                        {
                            bool keep = (c - a).Cross(b - a).Y >= 0f;
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(a);
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(keep ? b : c);
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(keep ? c : b);
                        }
                        int nd = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(d1 - d0) / alongStep)), nl = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(latB - latA) / acrossStep));
                        for (int id = 0; id < nd; id++)
                            for (int il = 0; il < nl; il++)
                            {
                                float da = Mathf.Lerp(d0, d1, (float)id / nd), db = Mathf.Lerp(d0, d1, (float)(id + 1) / nd);
                                float la = Mathf.Lerp(latA, latB, (float)il / nl), lb = Mathf.Lerp(latA, latB, (float)(il + 1) / nl);
                                Tri(V(da, la), V(da, lb), V(db, lb)); Tri(V(da, la), V(db, lb), V(db, la));
                            }
                    }
                    // (half a metre along and three-eighths across: a slab bends at its own triangles' edges, and a metre
                    // of paint between two points on it ran up to 3.5 cm under the slab's kink in between)
                    void Bar(float latA, float latB) => PaintQuad(cl.D0, cl.D1, latA, latB, 0.5f, 0.375f);
                    foreach (var (l0, l1) in painted) Bar(l0, l1);
                    // ⚠ AND ITS OWN STOP LINE, because this is now past the junction patch and PaintNode
                    // can only paint within it. A driver stops BEHIND the crossing, so the line goes outside.
                    // Across the approach half only -- we drive on the right. Parallel to the crossing, the way
                    // a give-way line follows the edge of the road it gives way to.
                    {
                        PaintQuad(cl.SD0, cl.SD1, 0.1f, half - 0.2f, 0.5f, 0.375f);
                    }
                    var mesh = st.Commit();
                    if (mesh == null || mesh.GetSurfaceCount() == 0) continue;
                    var mi = new MeshInstance3D { Name = $"Crossing_{j}_{made}", Mesh = mesh };
                    if (mat != null) mi.MaterialOverride = mat;
                    parent.AddChild(mi);
                    made++;
                    if (cl.Shear != 0f) slanted++;
                    _builtCrossings.Add((road, atEnd));
                }
            }
            Log.Print($"[towngrid] crossings: {made - bare} laid ({slanted} slanted parallel to the road they cross into), "
                    + $"{bare} approaches with a stop line and no crossing ({CrossingsAt}{(bare > 0 ? $", the first at ({FirstBareJunction.X:0.0}, {FirstBareJunction.Z:0.0})" : "")}), "
                    + $"{dropped} left off where a short road could not hold them");
            return made;
        }

        /// <summary>The road ends the last BuildCrossings actually crossed.</summary>
        readonly HashSet<(int road, bool atEnd)> _builtCrossings = new();
        public bool CrossingBuilt(int road, bool atEnd) => _builtCrossings.Contains((road, atEnd));

        /// <summary>A road's length NODE TO NODE: the built ribbon plus what each junction cut off it.</summary>
        public static float NodeSpan(RoadField roads, int road)
        {
            float L = roads.RoadLength(road), span = L;
            for (int e = 0; e < 2; e++)
            {
                bool atEnd = e == 1;
                int j = roads.RoadEndJunction(road, atEnd);
                if (j < 0 || !roads.EvaluateAlong(road, atEnd ? L : 0f, out var tip, out _, snapTerrain: false)) continue;
                var jp = roads.JunctionPos(j);
                span += new Vector2(tip.X - jp.X, tip.Z - jp.Z).Length();
            }
            return span;
        }

        /// <summary>Do the crossings at a road's two ends overlap? Each reaches from its node out to the far
        /// side of its stop line's apron, and a slanted one reaches further at one kerb than the other -- so
        /// both kerbs are asked. The same kerb is lat +1 at one end and -1 at the other.</summary>
        public static bool CrossingsCollide(CrossingLayout a, CrossingLayout b, float span)
        {
            for (int s = -1; s <= 1; s += 2)
                if ((a.SD1 + s * a.Half * a.Shear + 0.3f) + (b.SD1 - s * b.Half * b.Shear + 0.3f) > span) return true;
            return false;
        }

        /// <summary>Take one crossing off each road too short to hold both of its ends'; returns how many.
        ///
        /// ⚠⚠ ONE SHORT ROAD CANNOT ALWAYS HOLD TWO CROSSINGS. Each has to stand clear of its own kerb's curve,
        /// and where a 9.2 m main meets at 45 degrees its far kerb end is still ~30 m out -- so on the short links
        /// the diagonal cuts (the long side of a triangle island) the two ends' crossings met in the middle, and
        /// once each was laid parallel to ITS main they crossed in an X. The end crossing the bigger road keeps
        /// its crossing, the start end on a tie.
        /// ⚠ ONLY CROSSING AGAINST CROSSING. A first version also made each crossing clear the FAR junction's
        /// kerb curve, and it took two crossings off the very four-way master had just asked to have four --
        /// on a sum that said they reached a triangle island's curve, where the render showed them clear of it.</summary>
        int DropCrowdedCrossings(RoadField roads, Dictionary<(int road, bool atEnd), CrossingLayout> plans)
        {
            int dropped = 0;
            foreach (var key in new List<(int road, bool atEnd)>(plans.Keys))
            {
                if (key.atEnd || !plans.TryGetValue((key.road, true), out var cb)) continue;   // each road once
                var ca = plans[key];
                if (!CrossingsCollide(ca, cb, NodeSpan(roads, key.road))) continue;
                plans.Remove((key.road, !(cb.Crossed > ca.Crossed)));
                dropped++;
            }
            return dropped;
        }

        /// <summary>Where one approach's crossing and stop line lie, in the arm's own frame: At(d, lat) is the
        /// point d metres out from the node along the arm and lat metres to the arriving driver's right.
        /// ⚠ BuildCrossings DRAWS FROM THIS and the tests measure it -- one copy of the rule, not two.</summary>
        public struct CrossingLayout
        {
            public Vector3 Node, Fwd, Right;
            /// <summary>This arm's half width, and the widest road it crosses into.</summary>
            public float Half, Crossed, PaintY, ApronY;
            /// <summary>Where the crossing's near and far edges, and the stop line's, cross the arm's centreline.</summary>
            public float D0, D1, SD0, SD1;
            /// <summary>Metres the band moves out along the arm per metre across it: 0 for a square crossing,
            /// cot θ for one laid parallel to a road that meets the arm at θ.</summary>
            public float Shear;
            public Vector3 At(float d, float lat) => Node - Fwd * (d + lat * Shear) + Right * lat;
        }

        /// <summary>Lay out the crossing on one approach -- or say there is none, for a main running through.
        ///
        /// ⭐ PARALLEL TO THE ROAD IT CROSSES INTO (strawberry 2026-10-12: "the 45° joins should have the
        /// crosswalk parallel to the main road's direction"). A crossing carries the pavement of the road the
        /// arm joins across the arm's mouth, so it lies along THAT road, not square to the arm: at 45 degrees
        /// a square one cut across the mouth on a slant to the kerb its pedestrians were walking along. Where
        /// the arm meets that road square, parallel to it IS square, and the old numbers come back bit for bit.
        /// ⚠ SO ITS DISTANCE IS MEASURED OFF THAT ROAD'S CENTRELINE, not along the arm. The kerb on the arm's
        /// sharp side stops turning R past the corner (LaySidewalkCorners holds an acute curve to R), which is
        /// h + R sin θ from that centreline (h the crossed road's half width); the near edge goes CrossingInset
        /// beyond that, and the blunt side's curve is shorter, so it is clear too. At 90 degrees that is
        /// h + R + inset -- the square rule exactly. Widths stay true widths: a 3 m walkway is 3 m across for
        /// the person on it, which along a 45-degree arm is 4.2 m.
        /// ⚠⚠ AND CLOSE TO THAT ROAD (strawberry 2026-10-12: "the crossing + stop line + traffic light/stop
        /// sign on angled joins should be way closer to the road its joining"). The first version cleared a
        /// fillet of radius R, h + R(1 + cos θ) out: at 45 degrees that curve runs R / tan 22.5 = 14.5 m up
        /// the arm, and the corner really was laid near that big. Pulling the crossing in alone would have
        /// stood it on the curve -- the built-kerb test caught exactly that -- so the corner turns tighter.</summary>
        public bool LayCrossing(RoadField roads, int junction, int road, bool atEnd, out CrossingLayout c)
        {
            c = default;
            // ⚠ A MAIN RUNNING THROUGH IS NOT STOPPED AND NOT CROSSED. Traffic on it runs through; its lines
            // carry on across the box and nothing is painted over them (strawberry: "the bigger roads should be
            // the 'main' roads, their lines continue through the junction without crossings on their parts").
            // ⚠⚠ RUNNING THROUGH, NOT MERELY MAIN. This asked IsMain, so where two four-lane roads crossed
            // there was no crossing on any arm (strawberry: "should have the 4 crossings like it did before").
            if (_throughEnds.Contains((road, atEnd))) return false;
            float L = roads.RoadLength(road);
            float arc = atEnd ? Mathf.Max(0f, L - 1f) : Mathf.Min(L, 1f);
            if (!roads.EvaluateAlong(road, arc, out var pos, out var tan, snapTerrain: false)) return false;
            var fwd = (atEnd ? tan : -tan).Normalized();         // the way a driver arrives
            var right = RightOf(fwd);
            float half = roads.RoadHalfWidth(road);
            // ⚠⚠ OUTSIDE THE KERB'S CURVE (strawberry: "bring them back before the curve"). The corner's
            // fillet begins at half + CornerKerbRadius from the node; inside that the kerbs either side are
            // still turning, so a crossing there meets them at an angle and its dropped kerbs cannot line up.
            // ⚠⚠ ...MEASURED FROM THE ROAD IT CROSSES, NOT FROM ITSELF. The kerb turns at the CROSSING road's
            // half width plus the radius, so a side street meeting a 9.2 m main is still curving until 15.2 m
            // out -- and `half + radius` put its zebra at 11.8-14.8 m, squarely inside the curve it was moved
            // out of. Same bug, same fix as the signals: master's "traffic lights (and other stuff) should
            // consider both roads' widths when placing".
            float crossed = half;
            foreach (var (other, _) in roads.JunctionEdges(junction)) if (other != road) crossed = Mathf.Max(crossed, roads.RoadHalfWidth(other));
            float curve = ApproachClear(roads, junction, road, atEnd, crossed);
            float d0 = curve + CrossingInset, d1 = d0 + CrossingWidth;
            float sd0 = d1 + StopGap, sd1 = sd0 + StopLineWidth;
            float shear = 0f, stretch = 1f;      // (metres along the arm per metre square to it: 1/sin of a slanted band)
            if (_crossAlong.TryGetValue((road, atEnd), out var along))
            {
                var outw = new Vector3(-fwd.X, 0f, -fwd.Z).Normalized();
                var m = new Vector3(along.X, 0f, along.Z).Normalized();
                float mu = m.Dot(outw), mr = m.Dot(right);
                float sinT = Mathf.Abs(mr), cosT = Mathf.Abs(mu);
                // square to half a degree keeps the square numbers exactly; a line nearly along the arm is no
                // road to lay a crossing parallel to
                if (cosT > Mathf.Sin(Mathf.DegToRad(0.5f)) && sinT > Mathf.Sin(Mathf.DegToRad(20f)))
                {
                    shear = mu / mr;
                    // ⚠ THE PULL-IN SCALES WITH THE CORNER: an acute corner's kerb turns in R tan(theta/2), not R (see
                    // LaySidewalkCorners), so 1.2 m into it is a third of its whole curve -- 61 cm off straight at 45
                    // degrees, measured. The same fraction of ITS radius keeps it as square as a right angle's.
                    float pull = CrossingInset < 0f ? CrossingInset * sinT / (1f + cosT) : CrossingInset;
                    curve = (crossed + CornerKerbRadius * sinT) / sinT;
                    d0 = curve + pull / sinT;
                    d1 = d0 + CrossingWidth / sinT;
                    sd0 = d1 + StopGap / sinT; sd1 = sd0 + StopLineWidth / sinT;
                    stretch = 1f / sinT;
                }
            }
            // ⭐ NO CROSSING, STILL A STOP LINE (strawberry 2026-10-19: "support for junctions to not have crossings (but keeping
            // stop lines)"). The band collapses to nothing where the kerb straightens out, and the stop line comes forward to
            // just past it -- so everything measured off D1 and SD1 (the lane lines, a median's nose, the stop sign) follows
            // it in instead of stopping short of a crossing that is not there.
            if (!HasCrossings(roads, junction))
            {
                sd0 = curve + BareStopPast * stretch; sd1 = sd0 + StopLineWidth * stretch;
                d0 = d1 = sd0;
            }
            c = new CrossingLayout
            {
                Node = roads.JunctionPos(junction), Fwd = fwd, Right = right, Half = half, Crossed = crossed,
                PaintY = pos.Y + roads.RoadSurfaceOffset(road) + PatchLift + MarkLift,
                ApronY = pos.Y + roads.RoadSurfaceOffset(road) + PatchLift + ApronLift,
                D0 = d0, D1 = d1, SD0 = sd0, SD1 = sd1, Shear = shear,
            };
            return true;
        }
        /// <summary>How far under the road surface the corner asphalt rides: enough that a ribbon or the slab
        /// always wins where they overlap it, little enough that the step at a filled hole cannot be seen.</summary>
        public float CornerFillDrop = float.TryParse(System.Environment.GetEnvironmentVariable("UG_TOWNFILLDROP"), System.Globalization.NumberStyles.Float,
                                                     System.Globalization.CultureInfo.InvariantCulture, out float fdrop) ? fdrop : 0.01f;
        readonly List<(Vector3 a, Vector3 b, Vector3 c)> _cornerFill = new();
        /// <summary>Triangles of corner asphalt laid by the last LaySidewalkCorners.</summary>
        public int CornerFillTris => _cornerFill.Count;

        /// <summary>Is this point (in plan) inside the corner asphalt?</summary>
        bool InCornerFill(Vector3 q)
        {
            foreach (var (a, b, c) in _cornerFill)
            {
                float minX = Mathf.Min(a.X, Mathf.Min(b.X, c.X)), maxX = Mathf.Max(a.X, Mathf.Max(b.X, c.X));
                float minZ = Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)), maxZ = Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z));
                if (q.X < minX || q.X > maxX || q.Z < minZ || q.Z > maxZ) continue;
                float d1 = (q.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (q.Z - b.Z);
                float d2 = (q.X - c.X) * (b.Z - c.Z) - (b.X - c.X) * (q.Z - c.Z);
                float d3 = (q.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (q.Z - a.Z);
                bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
                if (!(neg && pos)) return true;
            }
            return false;
        }

        /// <summary>Each arm's own carriageway, full width, from its node out to where its ribbon starts.
        ///
        /// ⚠ THE CORNER FANS ALONE LEFT A TRIANGLE. A fan runs from the node DIAGONALLY to the kerb point, so
        /// it covers the corner but not the strip of an arm's carriageway beside its centreline between the
        /// node and the arm's cut -- and at 45 degrees the junction slab does not reach all of that either.
        /// The node-to-cut strip of every arm is road by definition, so it is paved as such, under
        /// everything, the same 1 cm down. UG_TOWNNOMOUTHFILL=1 is the control.</summary>
        void AddMouthFill(RoadField roads)
        {
            float surf = roads.RoadSurfaceOffsetOfMaterial(StreetMaterial) - CornerFillDrop;
            // ⚠⚠ A GRID, NOT ONE QUAD (strawberry 2026-10-13, "follow gentle hills and slopes"). Each arm's fill was one flat
            // quad from the node's height to the arm's, a centimetre under both -- under everything on flat ground. On a hill
            // the junction's slab is not that plane: it takes every arm's height at once and bends between them, and where it
            // dipped under the quad the fill came up through it as a dark crease corner to corner across the junction (gone
            // with UG_TOWNNOMOUTHFILL=1, which is how it was found). So the plane is laid as a grid, and BuildCornerFill holds
            // every point of it under whatever paving is over it.
            for (int j = 0; j < roads.JunctionCount; j++)
            {
                var es = roads.JunctionEdges(j);
                if (es.Count < 3) continue;
                var node = roads.JunctionPos(j);
                foreach (var (road, atEnd) in es)
                {
                    float L = roads.RoadLength(road);
                    if (!roads.EvaluateAlong(road, atEnd ? L : 0f, out var tip, out _, snapTerrain: false)) continue;
                    var u = new Vector3(tip.X - node.X, 0f, tip.Z - node.Z);
                    float have = u.Length();
                    if (have < 0.5f) continue;                    // uncut: its own ribbon already reaches the node
                    u /= have;
                    var nrm = new Vector3(-u.Z, 0f, u.X);
                    float h = roads.RoadHalfWidth(road) + 0.05f, reach = have + 0.5f;     // a little under the ribbon's own start
                    int na = Mathf.Max(1, Mathf.CeilToInt(reach / 1.5f)), nc = Mathf.Max(1, Mathf.CeilToInt(2f * h / 1.5f));
                    Vector3 V(int ia, int ic)
                    {
                        float fa = (float)ia / na, lat = Mathf.Lerp(-h, h, (float)ic / nc);
                        var at = node + u * (reach * fa) + nrm * lat;
                        return new Vector3(at.X, Mathf.Lerp(node.Y, tip.Y, fa) + surf, at.Z);
                    }
                    for (int ia = 0; ia < na; ia++)
                        for (int ic = 0; ic < nc; ic++)
                        {
                            var a0 = V(ia, ic); var a1 = V(ia, ic + 1); var b0 = V(ia + 1, ic); var b1 = V(ia + 1, ic + 1);
                            _cornerFill.Add((a0, a1, b1)); _cornerFill.Add((a0, b1, b0));
                        }
                }
            }
        }
        public static bool NoMouthFill = System.Environment.GetEnvironmentVariable("UG_TOWNNOMOUTHFILL") == "1";
        /// <summary>How many corner/mouth fill points the last BuildCornerFill had to pull under the paving over them, and
        /// the most any one moved -- the fill that WOULD have come up through the asphalt.</summary>
        public int FillPulled; public float FillPulledMost;
        /// <summary>...and how many points INSIDE its triangles still came within 2 mm of the paving over them, the worst.</summary>
        public int FillThrough; public float FillThroughMost;
        /// <summary>UG_TOWNNOCORNERFILL=1 lays no corner asphalt at all (nor mouth fill): the control for "is that
        /// surface the fill, or something under it".</summary>
        public static bool NoCornerFill = System.Environment.GetEnvironmentVariable("UG_TOWNNOCORNERFILL") == "1";
        /// <summary>UG_TOWNPROBES=1 also runs the diagnostic passes nobody needs on every render.</summary>
        public static bool ProbeEverything = System.Environment.GetEnvironmentVariable("UG_TOWNPROBES") == "1";
        /// <summary>The measurements a stage takes of its own work as it goes (the corner fill's 800,000-point check of itself):
        /// on for the renders and the tests, off for a town built to PLAY, where they were half a minute of a load.</summary>
        public bool Probes = true;


        /// <summary>Lay the corner asphalt LaySidewalkCorners collected: one mesh, the slab's own material and
        /// plain-band UVs, normals up -- the same three choices BuildPatchMeshes makes, for the same reasons.</summary>
        public int BuildCornerFill(Node3D parent, RoadField roads)
        {
            if (parent == null || roads == null) return 0;
            if (NoCornerFill) { _cornerFill.Clear(); return 0; }
            if (!NoMouthFill) AddMouthFill(roads);
            if (_cornerFill.Count == 0) return 0;
            // ⚠⚠ EVERY POINT UNDER THE PAVING OVER IT (strawberry 2026-10-13, "follow gentle hills and slopes"). The fill is
            // laid as planes a centimetre under the road surface at the points it was given -- the node, the kerb, a mouth's
            // edge -- and on flat ground that is under everything. On a hill the slab bends and a street's profile sags
            // between those points, and the fill came up through both as dark creases. So each point is held a centimetre
            // under the lowest paving over it: the slab where there is one, any street's or lane's carriageway it lies on.
            // Where there is none -- the holes the fill is for -- it keeps its plane.
            var paving = PavingTops(roads);
            var held = new Dictionary<(long, long), float>();
            int pulled = 0, points = 0; float pulledMost = 0f;
            Vector3 Hold(Vector3 v)
            {
                var key = ((long)Mathf.Round(v.X * 1000f), (long)Mathf.Round(v.Z * 1000f));
                if (!held.TryGetValue(key, out float under))
                {
                    under = paving(v).lo;
                    // ⚠ IN A HOLE, NO HIGHER THAN THE PAVING NEAREST IT. A point with no paving over it keeps its plane -- but
                    // its triangles reach under the paving beside the hole, and on a hill a hole's plane can stand over the
                    // slab's or a street's edge next to it, so the triangle between came up through that edge (36.8 cm, the
                    // interior probe below). Held no higher than the nearest paving, a triangle running from it under that
                    // paving stays under it.
                    if (float.IsNaN(under)) under = PavingNear(paving, v).lo;
                    held[key] = under;
                    points++;
                    if (!float.IsNaN(under) && v.Y > under - CornerFillDrop) { pulled++; pulledMost = Mathf.Max(pulledMost, v.Y - (under - CornerFillDrop)); }
                }
                return float.IsNaN(under) ? v : new Vector3(v.X, Mathf.Min(v.Y, under - CornerFillDrop), v.Z);
            }
            // ⚠ AND SPLIT WHERE ITS MIDDLE STILL COMES UP. Held at its corners, a triangle is still a plane between them,
            // and a slab bent to meet a main (SnapClippedEdges) dips between a fill triangle's corners: 5.3 cm of it came
            // up through, the interior probe below. A triangle any point of which comes within 2 mm of the paving over it
            // is split in four, its new corners held too, down to a thirty-second of its size.
            bool Pokes((Vector3 a, Vector3 b, Vector3 c) t)
            {
                for (int u = 0; u <= 4; u++)
                    for (int w = 0; u + w <= 4; w++)
                    {
                        if (u == 0 && w == 0 || u == 4 || w == 4) continue;          // its corners are held already
                        var v = t.a + (t.b - t.a) * (u / 4f) + (t.c - t.a) * (w / 4f);
                        float lo = paving(v).lo;
                        if (!float.IsNaN(lo) && v.Y > lo - 0.002f) return true;
                    }
                return false;
            }
            var held3 = new List<(Vector3 a, Vector3 b, Vector3 c)>(_cornerFill.Count);
            int split = 0;
            void Lay((Vector3 a, Vector3 b, Vector3 c) t, int depth)
            {
                var h = (Hold(t.a), Hold(t.b), Hold(t.c));
                if (depth >= 5 || !Pokes(h)) { held3.Add(h); return; }
                split++;
                var ab = (t.a + t.b) * 0.5f; var bc = (t.b + t.c) * 0.5f; var ca = (t.c + t.a) * 0.5f;
                Lay((t.a, ab, ca), depth + 1); Lay((ab, t.b, bc), depth + 1); Lay((ca, bc, t.c), depth + 1); Lay((ab, bc, ca), depth + 1);
            }
            foreach (var t in _cornerFill) Lay(t, 0);
            _cornerFill.Clear(); _cornerFill.AddRange(held3);
            FillPulled = pulled; FillPulledMost = pulledMost;
            Log.Print($"[towngrid] corner asphalt: {split} triangles split where their middle came up through the paving");
            Log.Print($"[towngrid] corner asphalt: {pulled}/{points} points pulled under the paving over them, by up to {pulledMost * 100f:0.0} cm");
            // ...and between its points: a triangle can still come up through paving its corners are all under
            if (Probes)
            {
                float worstUp = float.NegativeInfinity; var upAt = Vector3.Zero; int upN = 0, tested = 0; string upTri = "";
                foreach (var (fa, fb, fc) in _cornerFill)
                    for (int u = 0; u <= 4; u++)
                        for (int w = 0; u + w <= 4; w++)
                        {
                            var v = fa + (fb - fa) * (u / 4f) + (fc - fa) * (w / 4f);
                            float lo = paving(v).lo;
                            if (float.IsNaN(lo)) continue;
                            tested++;
                            float up = v.Y - (lo - 0.002f);
                            if (up > 0f) upN++;
                            if (up > worstUp) { worstUp = up; upAt = v; upTri = $"{fa} {fb} {fc}"; }
                        }
                FillThrough = upN; FillThroughMost = worstUp;
                if (ProbeEverything && upN > 0)
                {
                    var slabTop2 = SlabTops(roads);
                    foreach (var q in new[] { upAt, upAt + new Vector3(0.25f, 0f, 0f), upAt - new Vector3(0.25f, 0f, 0f), upAt + new Vector3(0f, 0f, 0.25f), upAt - new Vector3(0f, 0f, 0.25f) })
                    {
                        var sb = new System.Text.StringBuilder($"[towngrid] paving at {q}: slab {slabTop2(q.X, q.Z):0.000}");
                        foreach (var list in new[] { Built, LaneRoads })
                            foreach (int r in list)
                            {
                                float y = RoadNear(roads, r, q, out float along, out float off);
                                if (off < roads.RoadHalfWidth(r) + 1f) sb.Append($"; road {r} off {off:0.00}/{roads.RoadHalfWidth(r):0.00} at {along:0.0}/{roads.RoadLength(r):0.0} spline+lift {y + roads.RoadSurfaceOffset(r):0.000}");
                            }
                        sb.Append($"; paving lo/hi {paving(q).lo:0.000}/{paving(q).hi:0.000}");
                        Log.Print(sb.ToString());
                    }
                }
                Log.Print($"[towngrid] corner asphalt: {upN}/{tested} points inside its triangles within 2 mm of the paving over them or through it; "
                        + $"worst {worstUp * 100f:0.0} cm at {upAt} (triangle {upTri})");
            }
            const float U0 = 4f / 256f, U1 = 56f / 256f;
            float tile = (U1 - U0) * 2f * 2f * Mathf.Max(1f, roads.RoadHalfWidthOfMaterial(MainMaterial));
            Vector2 UV(Vector3 q) => new Vector2(U0 + PingPong(q.X / tile) * (U1 - U0), q.Z / tile);
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var (a, b, c) in _cornerFill)
            {
                // orient by the wanted normal (up), never by the order given -- a corner swept the other way
                // round would otherwise come out back-facing and simply not draw
                bool keep = (c - a).Cross(b - a).Y >= 0f;
                var i1 = keep ? b : c; var i2 = keep ? c : b;
                st.SetNormal(Vector3.Up); st.SetUV(UV(a)); st.AddVertex(a);
                st.SetNormal(Vector3.Up); st.SetUV(UV(i1)); st.AddVertex(i1);
                st.SetNormal(Vector3.Up); st.SetUV(UV(i2)); st.AddVertex(i2);
            }
            var mesh = st.Commit();
            if (mesh == null || mesh.GetSurfaceCount() == 0) return 0;
            var mi = new MeshInstance3D { Name = "Junction_Corner_Asphalt", Mesh = mesh };
            var mat = roads.MaterialOfMaterial(MainMaterial);
            if (mat != null) mi.MaterialOverride = mat;
            parent.AddChild(mi);
            return _cornerFill.Count;
        }

        /// <summary>The DRIVER'S right for a heading, which is forward x up.
        ///
        /// ⚠⚠ `(f.Z, 0, -f.X)` IS THE LEFT, and it is written in this file as "right" more than once because
        /// a sidewalk lays both sides and never notices which is which. It matters the moment something is
        /// one-sided: a stop line painted across the lanes LEAVING the junction, and a signal mast on the far
        /// kerb reaching back over the oncoming traffic -- strawberry, twice: "the stop lines are assuming
        /// left side driving". Check it against Godot's own axes rather than by rotating a vector in your
        /// head: forward is -Z and right is +X, and (0,0,-1) x (0,1,0) = (1,0,0).</summary>
        static Vector3 RightOf(Vector3 fwd) => new Vector3(-fwd.Z, 0f, fwd.X).Normalized();

        /// <summary>Is this point clear of EVERY carriageway, not just the one it was measured from?
        ///
        /// ⚠⚠ A PROP SITED OFF ITS OWN ROAD'S WIDTH LANDS IN THE CROSSING ROAD. Each lamp, sign and mast was
        /// placed at its own street's half width plus a setback -- fine in the middle of a block, and wrong
        /// near a junction where the road it crosses is twice as wide: the pole ends up standing in the
        /// other carriageway. bitvox: "some of the streetlight poles are on the street not the siding";
        /// master: "traffic lights (and other stuff) should consider both roads' widths when placing".
        /// One test, asked of every road, is the only way to be sure -- there is no arithmetic on one road's
        /// width that can know about another's. See RoadField.DeepestRoadOverlap for why it must be EVERY
        /// road and not the nearest one.</summary>
        bool ClearOfRoads(RoadField roads, Vector3 at, float margin = 0.1f)
        {
            if (roads.DeepestRoadOverlap(at, margin, out _, out _)) return false;
            // ⚠⚠ AND THE JUNCTION PATCHES, which are asphalt that NO ROAD SPLINE COVERS. Every arm is
            // trimmed back from the node and the merged slab fills the middle, so a point in the middle of
            // an intersection reads "clear of every road" -- the calibration proved it, junction centre
            // "clear". That hole is exactly where a pole goes wrong: the pavement TURNS INWARD at the
            // corner while the last lamp keeps the straight's lateral offset, so it carries on into the
            // slab. Asking only about roads could never see it.
            if (OnAnyPatch(at.X, -at.Z, margin)) return false;
            return true;
        }

        /// <summary>Push a point away from the node until it is clear of every carriageway, or give up.</summary>
        bool PushClear(RoadField roads, ref Vector3 at, Vector3 away, float margin = 0.1f)
        {
            // ⚠ THE CONTROL. UG_TOWNNOCLEAR=1 leaves the point exactly where its own road's width put it,
            // so the census below MUST then report poles standing in the asphalt. A probe that reads zero
            // both with and without the fix is measuring nothing -- it has to be able to fail.
            if (NoClear) { _propTested++; if (!ClearOfRoads(roads, at, margin)) _propInRoad++; return true; }
            _propTested++;
            var from = at;
            if (!ClearOfRoads(roads, at, margin))
            { if (roads.DeepestRoadOverlap(at, margin, out _, out _)) _pushByRoad++; else _pushBySlab++; }
            for (int i = 0; i < 24; i++)
            {
                if (ClearOfRoads(roads, at, margin))
                {
                    if (i > 0)
                    {
                        _propPushed++;
                        // ⚠ HOW FAR, not just how many: a count cannot tell a mast nudged off a slab's skirt from
                        // one shoved across the pavement into the verge
                        if (i * 0.5f > _propPushedMax) { _propPushedMax = i * 0.5f; _propPushedAt = from; }
                    }
                    return true;
                }
                at += away * 0.5f;
            }
            _propInRoad++;                 // gave up: do not place it, it would stand in the road
            return false;
        }
        public static bool NoClear = System.Environment.GetEnvironmentVariable("UG_TOWNNOCLEAR") == "1";
        int _propTested, _propInRoad, _propPushed;
        float _propPushedMax; Vector3 _propPushedAt; int _pushByRoad, _pushBySlab;
        /// <summary>Where the prop pushed furthest off paving was first sited, and how far it went, so a camera
        /// can be put on the worst one rather than on the one I guess it is.</summary>
        public Vector3 FurthestPushAt => _propPushedAt;
        public float FurthestPush => _propPushedMax;

        /// <summary>How far the pavement stands proud of the road.
        ///
        /// ⚠⚠ ANYTHING ON THE PAVEMENT IS PLACED AT THIS HEIGHT, NOT AT THE ROAD'S. A hydrant seated on the
        /// terrain stands 0.4 m inside the slab it is supposed to be bolted to -- a 1.15 m prop with 0.75 m
        /// showing, which is exactly what "hydrants look sunk into the ground" is. The lamps and the signal
        /// masts stand on the same pavement and had the same error; their poles are tall enough that nobody
        /// had noticed yet.</summary>
        public float PavementKerb = 0.4f;
        /// <summary>The top of the pavement at `at`, beside the street point `pos`: what a lamp, a meter, a sign or a mast stands on.
        /// ⚠ NOT max(street, ground) + kerb ON A GRADED TOWN. The ground beside a street is graded up to just under the pavement's
        /// top now (PavementProud), so under a pavement's back half it can be within centimetres of it, and every prop seated on
        /// the higher of the two stood up to 35 cm over its slab. A draped town's street rides the ground, so there the ground
        /// can still be the higher and wins.</summary>
        float PavementTopAt(Vector3 pos, Vector3 at, Terrain terr) =>
            (FollowTerrain && terr != null ? Mathf.Max(pos.Y, terr.SampleHeight(at.X, at.Z)) : pos.Y) + PavementKerb;
        /// <summary>The ground a pavement prop leans with, or null to stand it plumb. ⚠ PLUMB ON A GRADED TOWN: the ground under a
        /// pavement now climbs from the carriageway's cut up to the lawn's level across the pavement's width, so a lamp tilted to
        /// the terrain's normal there leaned out over the road by up to the 6 degree cap. The slab it stands on is level across.</summary>
        Terrain PavementGround(Terrain terr) => FollowTerrain ? terr : null;
        /// <summary>How wide the town's pavements are, straight runs and corners alike -- one number, read from one
        /// place, so the corners always meet the streets they join. UG_TOWNWALK=<metres> overrides it.
        /// ⚠ 4 m (strawberry 2026-10-12: "add support for wider sidewalks ... just make them twice as wide
        /// everywhere"); the kit's own pavement is 2.</summary>
        public float PavementWidth = float.TryParse(System.Environment.GetEnvironmentVariable("UG_TOWNWALK"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tw) && tw > 0.5f ? tw : 4f;

        /// <summary>What governs a junction. ⚠ A four-lane crossing of two-lane streets gets signals; a
        /// corner of two small streets gets a stop sign. Auto picks per junction on whether any arm is a MAIN
        /// street, which is what decides it in a real town too (strawberry 2026-10-12: "generally these
        /// smaller streets should use stop signs instead of traffic lights, have it as a setting").</summary>
        public enum Control { Auto, Signals, StopSigns, None }
        public Control Junctions = Control.Auto;
        public string StopSignProp = "Stop_0";

        /// <summary>Is this road one of the grid's MAIN streets? ⚠ Asked by material, because that is what
        /// the generator actually varies between a main and a side street -- and a main road's junction
        /// treatment differs from a side street's in every respect that follows.</summary>
        /// <summary>Is this road a main -- the plain one, the boulevard, or the textured original? Asked of the material
        /// it was DERIVED from, since the town draws every road from a material of its own.</summary>
        public bool IsMain(RoadField roads, int road)
        {
            int m = roads.RoadMaterialOf(road);
            return MainMaterial != StreetMaterial && m >= 0 && roads.MaterialSource(m) == MainMaterial;
        }
        /// <summary>Is this road the boulevard -- a main with a median down its middle?</summary>
        public bool IsBoulevard(RoadField roads, int road) => BoulevardMaterial >= 0 && roads.RoadMaterialOf(road) == BoulevardMaterial;

        /// <summary>The main road's own lane lines, carried straight across the junction box.
        ///
        /// ⚠ ONLY FOR A THROUGH PAIR. Traffic on the main road is not stopped, so its markings do not stop
        /// either -- but that is only meaningful where the main road actually passes THROUGH the node, i.e.
        /// two main arms roughly opposite each other. A single main arm ending at a junction is a tee's stem
        /// and its lines stop like anyone else's.</summary>
        int BuildMainThroughLines(Node3D parent, RoadField roads)
        {
            if (MainMaterial == StreetMaterial) return 0;      // one class of road: nothing is "the main one"
            // ⚠⚠ NOT WHEN THE TOWN PAINTS ITS OWN LINES (strawberry 2026-10-13: "theres a duplicate line in the middle of
            // the intersection"). A through main is uncut: its two halves meet AT the node, and their own lines run to
            // it from both sides (LineLimit is 0 at a through end) -- so the box is already painted, and this painted
            // it a second time along a chord a hair off the roads' own centrelines: a doubled line down the middle.
            if (PaintOwnLines && _plainMain >= 0) return 0;
            var mat = roads.MaterialOfMaterial(MainMaterial);
            var uvWhite = new Vector2(0.2363f, 0.0664f);
            var uvYellow = new Vector2(0.4863f, 0.0039f);
            float half = roads.RoadHalfWidthOfMaterial(MainMaterial);
            var table = LineTableFor(half);
            int made = 0;
            for (int j = 0; j < roads.JunctionCount; j++)
            {
                var edges = roads.JunctionEdges(j);
                if (edges.Count < 2) continue;
                var node = roads.JunctionPos(j);
                var arms = new List<(Vector3 tip, Vector3 outDir, int road)>();
                foreach (var (road, atEnd) in edges)
                {
                    // ⚠ A MAIN THAT RUNS THROUGH, not any main: where two mains cross, nobody's lines carry on
                    // across the box -- this painted one of them straight over the other's crossings
                    if (!_throughEnds.Contains((road, atEnd))) continue;
                    float L = roads.RoadLength(road);
                    if (!roads.EvaluateAlong(road, atEnd ? L : 0f, out var tip, out var tan, snapTerrain: false)) continue;
                    var outDir = new Vector3(tip.X - node.X, 0f, tip.Z - node.Z);
                    if (outDir.Length() < 0.5f) continue;
                    arms.Add((tip, outDir.Normalized(), road));
                }
                if (arms.Count < 2) continue;
                // the most opposed pair is the through one
                int bi = -1, bj = -1; float worst = 0f;
                for (int a = 0; a < arms.Count; a++)
                    for (int b = a + 1; b < arms.Count; b++)
                    {
                        float opp = -arms[a].outDir.Dot(arms[b].outDir);
                        if (opp > worst) { worst = opp; bi = a; bj = b; }
                    }
                if (bi < 0 || worst < 0.8f) continue;          // not a straight-through main road
                // ⚠ A BOULEVARD CARRIES ITS OWN LINES ACROSS: each half of the main's, pushed out by half the median.
                // Painting its own lines, the town carries ITS table across -- the centre and the lanes, never the edges,
                // which belong to kerbs the box does not have.
                if (PaintOwnLines && _plainMain >= 0)
                {
                    table = new List<(double lat, double width, byte colour, bool dashed)>();
                    foreach (var (ol, ow, oc, od, oe) in OwnLines(roads, arms[bi].road, withEdges: false)) table.Add((ol, ow, oc, od));
                }
                else table = LineTableFor(half, MedianHalfOf(roads, arms[bi].road));
                var p0 = arms[bi].tip; var p1 = arms[bj].tip;
                var axis = new Vector3(p1.X - p0.X, 0f, p1.Z - p0.Z);
                float span = axis.Length();
                if (span < 1f) continue;
                axis /= span;
                var lat = RightOf(axis);
                float y = node.Y + roads.RoadSurfaceOffsetOfMaterial(MainMaterial) + PatchLift + MarkLift;
                var st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                int quads = 0;
                void Bar(float s0, float s1, float off, float w, Vector2 uv)
                {
                    var a0 = p0 + axis * s0 + lat * (off - w * 0.5f); var a1 = p0 + axis * s0 + lat * (off + w * 0.5f);
                    var b0 = p0 + axis * s1 + lat * (off - w * 0.5f); var b1 = p0 + axis * s1 + lat * (off + w * 0.5f);
                    Vector3 V(Vector3 q) => new Vector3(q.X, y, q.Z);
                    void Tri(Vector3 x, Vector3 yy, Vector3 z)
                    {
                        bool keep = (z - x).Cross(yy - x).Y >= 0f;
                        st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(x);
                        st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(keep ? yy : z);
                        st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(keep ? z : yy);
                    }
                    Tri(V(a0), V(a1), V(b1)); Tri(V(a0), V(b1), V(b0)); quads++;
                }
                foreach (var (off, w, colour, dashed) in table)
                {
                    var uv = colour == 1 ? uvYellow : uvWhite;
                    if (!dashed) { Bar(0f, span, (float)off, (float)w, uv); continue; }
                    // ⚠ dashes keep a rhythm rather than becoming one long line, or the box reads as solid -- the
                    // roads' own rhythm, where the town paints its own lines
                    float dash = PaintOwnLines && _plainMain >= 0 ? LineDash : 3f, gap = PaintOwnLines && _plainMain >= 0 ? LineGap : 6f;
                    for (float t = 0f; t < span; t += dash + gap)
                        Bar(t, Mathf.Min(span, t + dash), (float)off, (float)w, uv);
                }
                if (quads == 0) continue;
                var mesh = st.Commit();
                if (mesh == null || mesh.GetSurfaceCount() == 0) continue;
                var mi = new MeshInstance3D { Name = $"Junction_Through_{j}", Mesh = mesh };
                if (mat != null) mi.MaterialOverride = mat;
                parent.AddChild(mi);
                made++;
            }
            return made;
        }

        public bool Crossings = true;
        /// <summary>Which junctions get crossings (strawberry 2026-10-19: "support for junctions to not have crossings (but keeping
        /// stop lines)"). All: every one. None: none, stop lines only. Auto: a junction with a main street on it always (it has
        /// signals, and the people), one of side streets only by SideCrossingShare -- the four-way stop with no zebra is
        /// most of a real suburb. The class keeps All, the game asks for Auto (Main); UG_TOWNCROSSINGS=all|none|auto.</summary>
        public enum CrossingRule { All, Auto, None }
        public CrossingRule CrossingsAt = CrossingRule.All;
        public float SideCrossingShare = 0.4f;
        /// <summary>Where a stop line with no crossing goes: this far past where the kerb straightens (so it meets the kerbs square).</summary>
        public float BareStopPast = 0.3f;
        /// <summary>A junction the last BuildCrossings left without crossings, for a camera.</summary>
        public Vector3 FirstBareJunction;
        /// <summary>Does this junction get crossings? Deterministic per junction (its position and the seed).</summary>
        public bool HasCrossings(RoadField roads, int junction)
        {
            if (CrossingsAt == CrossingRule.All) return true;
            if (CrossingsAt == CrossingRule.None) return false;
            foreach (var (r, _) in roads.JunctionEdges(junction)) if (IsMain(roads, r)) return true;
            var p = roads.JunctionPos(junction);
            ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)Mathf.RoundToInt(p.X * 10f) << 24) ^ (uint)Mathf.RoundToInt(p.Z * 10f) ^ 0xC305UL));
            return Unit(h, 0) < SideCrossingShare;
        }
        /// <summary>Zebra bars, or just the two outlines of the walkway.</summary>
        public bool Zebra = true;
        /// <summary>How far the crossing sits outside the carriageway's own edge, and how deep it is.</summary>
        /// ⚠ NEGATIVE: the crossing starts INSIDE the corner's curve (strawberry 2026-10-13: "bring the crossings (and
        /// stop lines) closer to the intersection"), 1.2 m in from where the kerb stops turning -- where a 6 m kerb
        /// return has turned only 12 cm off straight, so the crossing still meets its kerbs square. It was 1.2 m OUT
        /// past it (strawberry, earlier: "bring them back before the curve"); this is that, pulled in by 2.4 m.
        public float CrossingInset = -1.2f, CrossingWidth = 3f;
        public float CrossingBar = 0.5f, CrossingGap = 0.5f;
        /// <summary>Gap between the crossing and the stop line behind it, and the line's own depth.</summary>
        public float StopGap = 0.8f, StopLineWidth = 0.5f;
        /// <summary>How far the plain apron under a crossing rides over the slab: between the slab (0) and the
        /// paint (MarkLift), so it fights neither.</summary>
        public float ApronLift = 0.007f;
        /// <summary>⚠ NEGATIVE: the crossing and its stop line now sit OUTSIDE the junction patch, where
        /// PaintNode cannot paint, so this tells it to skip its own stop line and carry the arm's lines the
        /// whole way in instead of starting them past a line that is not there.</summary>
        public float StopPastEdge => -1f;

        /// <summary>Where this road's markings actually are, DECODED FROM road_0.png rather than taken from a
        /// class table (strawberry 2026-10-12: "some of the lines are scaled/aligned wrong?").
        ///
        /// ⚠⚠ THE TEXTURE IS THE ONLY TRUTH ABOUT WHERE A ROAD'S LINES ARE, because it is literally what is
        /// drawn. InfiniteJunctions' LinesOf says a Main road's dashes sit at +-4.82 m, symmetrically; the
        /// actual texture has them at columns 60..63 and 188..191 of 256, which against a centre at 127.5 is
        /// 66 px one side and 62 the other -- NOT symmetric. Scaling the class table lands both at the same
        /// distance and one of them is then a quarter of a metre out, which is exactly the misalignment.
        /// RoadField's cross-section gives u = 0 and 1 to the carriageway's own edges (uC = {0,0,1,1}; the
        /// tapers share them), so u maps to lateral as (u - 0.5) * 2 * halfWidth with nothing else to allow for.</summary>
        /// <summary>The same table for a road carrying a median `medianHalf` either side of its centre: road_0's two
        /// halves pulled apart, as the boulevard's texture is built -- every line moved out by the median's half, and
        /// the centre line split into one half-width line along each side of the median.</summary>
        static List<(double lat, double width, byte colour, bool dashed)> LineTableFor(float half, float medianHalf)
        {
            var table = LineTableFor(half);
            if (medianHalf <= 0f) return table;
            var outp = new List<(double, double, byte, bool)>(table.Count + 1);
            foreach (var (lat, w, colour, dashed) in table)
            {
                if (Math.Abs(lat) < 1e-3)
                {
                    outp.Add((-(medianHalf + w * 0.25), w * 0.5, colour, dashed));
                    outp.Add((medianHalf + w * 0.25, w * 0.5, colour, dashed));
                }
                else outp.Add((lat + Math.Sign(lat) * medianHalf, w, colour, dashed));
            }
            return outp;
        }
        float MedianHalfOf(RoadField roads, int road) => IsBoulevard(roads, road) ? roads.MedianHalfOfMaterial(BoulevardMaterial) : 0f;

        static List<(double lat, double width, byte colour, bool dashed)> LineTableFor(float half)
        {
            // (u centre, u width, colour, dashed) measured off road_0.png: white 60..63, yellow 124..131,
            // white 188..191, all of 256 across.
            var src = new (double u, double w, byte colour, bool dashed)[]
            {
                (0.2422, 0.0156, 0, true),
                (0.5000, 0.0312, 1, false),
                (0.7422, 0.0156, 0, true),
            };
            var outp = new List<(double, double, byte, bool)>(src.Length);
            foreach (var (u, w, colour, dashed) in src)
                outp.Add(((u - 0.5) * 2.0 * half, w * 2.0 * half, colour, dashed));
            return outp;
        }

        /// <summary>Where two kerb lines would cross: the sharp corner of the block, which is the centre the
        /// rounded corner turns about. False when the two run parallel and there is no corner to round.</summary>
        static bool KerbCorner(Vector3 a, Vector3 ta, Vector3 b, Vector3 tb, out Vector3 p)
        {
            p = Vector3.Zero;
            float den = ta.X * tb.Z - ta.Z * tb.X;
            if (Mathf.Abs(den) < 1e-4f) return false;
            float s = ((b.X - a.X) * tb.Z - (b.Z - a.Z) * tb.X) / den;
            p = new Vector3(a.X + ta.X * s, (a.Y + b.Y) * 0.5f, a.Z + ta.Z * s);
            return true;
        }

        /// <summary>Lay corners against an EXISTING field (one another grid already populated) and hand back
        /// their centrelines. The test seam: a town built by one instance, cornered by another.</summary>
        public int LaySidewalkCornersOn(RoadField roads, Node3D parent, EditorSidewalk walk,
                                        out List<(Vector3 node, List<Vector3> pts)> shapes)
        {
            int n = LaySidewalkCorners(parent, roads, walk);
            shapes = new List<(Vector3, List<Vector3>)>(LastCorners);
            return n;
        }

        /// <summary>The most a kerb's corner may turn between two of its segments. 4 degrees is about 22
        /// facets in a right-angle turn, which stops reading as flats.</summary>
        public float ArcFacetDegrees = 4f;

        /// <summary>The radius a kerb turns through at a corner, as a fraction of the carriageway's half
        /// width. ⚠ NOT A FIXED NUMBER: 6 m is an ordinary urban return off an 18 m street and a sweeping
        /// curve off a 9 m one. 0.65 reproduces the old 6 m at Highway_0 and scales down with the road.</summary>
        public float CornerKerbFraction = 0.65f;
        public float CornerKerbRadius = 6f;

        /// <summary>How far past the widest road at a junction a pavement is held back, and so how wide the
        /// corner it leaves is.</summary>
        public float CornerRadius = 2.5f;

        /// <summary>How far along an arm, measured from its node, the kerb is still TURNING -- at any angle.
        ///
        /// ⭐ THE 90-DEGREE RULE WAS HARDCODED FOUR TIMES. Pavement trims, crossings, stop lines and signals
        /// each placed themselves at `widest + radius` from the node, which is exactly right when the roads
        /// meet square and badly wrong when they do not. Two kerbs meeting at interior angle θ intersect at
        /// (h_b + h_a·cos θ) / sin θ along this arm, and a fillet of radius R in that corner touches them a
        /// further R / tan(θ/2) out. At 90° that is h_b + R -- the old number. At 45° (strawberry 2026-10-12:
        /// "work on 45° intersections") it is about 25.6 m against the 10.6 m everything assumed, so every
        /// one of the four would have stood inside the curve.
        /// ⚠ BIT-IDENTICAL AT A RIGHT ANGLE, ON PURPOSE. Each caller passes the width it always used (hw), and
        /// at 90° this returns hw + R exactly, so the square town master has already signed off is untouched
        /// and only the new angles move. The sharpest corner beside the arm decides it, since the sharper
        /// side is the one whose kerb runs out furthest.</summary>
        public float KerbCurveEnd(RoadField roads, int junction, int road, bool atEnd, float hw, float radius)
        {
            float theta = SharpestCorner(roads, junction, road, atEnd);
            if (Mathf.Abs(theta - Mathf.Pi * 0.5f) < Mathf.DegToRad(0.5f)) return hw + radius;   // the old rule, exactly
            if (theta > Mathf.DegToRad(150f)) return hw + radius;    // effectively straight through: no sharp side
            theta = Mathf.Max(theta, Mathf.DegToRad(20f));            // a sliver of an angle is not a corner to fillet
            float ha = roads.RoadHalfWidth(road);
            return (hw + ha * Mathf.Cos(theta)) / Mathf.Sin(theta) + radius / Mathf.Tan(theta * 0.5f);
        }

        /// <summary>How far out along an arm its approach's furniture -- crossing, stop line, signal or sign --
        /// stands clear of the junction. At a square or blunt corner exactly KerbCurveEnd at CornerKerbRadius,
        /// the rule master signed off. An ACUTE corner's kerb is laid to turn in R (LaySidewalkCorners caps its
        /// radius at R·tan(θ/2)), so its curve ends R past the corner point, not R / tan(θ/2) past it -- 6 m
        /// at 45 degrees where it was 14.5.</summary>
        public float ApproachClear(RoadField roads, int junction, int road, bool atEnd, float hw)
        {
            float theta = SharpestCorner(roads, junction, road, atEnd);
            if (theta > Mathf.Pi * 0.5f - Mathf.DegToRad(0.5f)) return KerbCurveEnd(roads, junction, road, atEnd, hw, CornerKerbRadius);
            theta = Mathf.Max(theta, Mathf.DegToRad(20f));
            float ha = roads.RoadHalfWidth(road);
            return (hw + ha * Mathf.Cos(theta)) / Mathf.Sin(theta) + CornerKerbRadius;
        }

        /// <summary>How far out along an arm a signal mast or sign stands, at `lat` metres to the arriving
        /// driver's right. ⚠ BESIDE ITS CROSSING: the mast stands SignalBeforeCurve past the clear point, a
        /// fifth of a metre short of where the crossing starts -- and where the crossing is laid on a slant,
        /// that start is a different distance at the kerb than on the centreline, so it is read off the
        /// crossing at the mast's own offset. Square everywhere else, and then the old number bit for bit.</summary>
        public float SignalBack(RoadField roads, int junction, int road, bool atEnd, float widest, float lat)
        {
            if (LayCrossing(roads, junction, road, atEnd, out var cl))
            {
                // no crossing: just behind its stop line, which is where a driver stops to read it
                if (cl.D1 - cl.D0 < 0.01f) return cl.SD1 + lat * cl.Shear + SignBehindStop;
                if (cl.Shear != 0f) return cl.D0 + lat * cl.Shear - (CrossingInset - SignalBeforeCurve);
            }
            return ApproachClear(roads, junction, road, atEnd, widest) + SignalBeforeCurve;
        }

        /// <summary>The smallest angle between this arm and any other arm at its node, in radians.</summary>
        public static float SharpestCorner(RoadField roads, int junction, int road, bool atEnd)
        {
            var me = ArmDir(roads, road, atEnd);
            if (me == Vector3.Zero) return Mathf.Pi * 0.5f;
            float best = Mathf.Pi;
            foreach (var (o, oe) in roads.JunctionEdges(junction))
            {
                if (o == road && oe == atEnd) continue;
                var d = ArmDir(roads, o, oe);
                if (d != Vector3.Zero) best = Mathf.Min(best, me.AngleTo(d));
            }
            return best;
        }

        /// <summary>An arm's direction leaving its node, flat.</summary>
        static Vector3 ArmDir(RoadField roads, int road, bool atEnd)
        {
            float L = roads.RoadLength(road);
            if (!roads.EvaluateAlong(road, atEnd ? L : 0f, out _, out var tg, snapTerrain: false)) return Vector3.Zero;
            var t = new Vector3(tg.X, 0f, tg.Z);
            if (t.LengthSquared() < 1e-8f) return Vector3.Zero;
            t = t.Normalized();
            return atEnd ? -t : t;
        }

        // ==============================================================================================
        // BUILDING PLOTS (strawberry 2026-10-12: "have the grid system mark plots for buildings, with fixed sizes
        // and shapes").
        // ==============================================================================================

        /// <summary>What a plot is for.</summary>
        public enum PlotKind { HouseSmall, HouseMedium, HouseLarge, Corner, Shop, Apartments, Triangle, Parking }

        /// <summary>One entry of the plot catalogue: a kind, its frontage and depth, its yards, and the colour it is
        /// marked in. The building stands in what the yards leave: (Width - 2 SideYard) x (Depth - FrontYard -
        /// BackYard), a whole number of 3 m cells, so a generated footprint fills it exactly.</summary>
        public readonly struct PlotSize
        {
            public readonly PlotKind Kind; public readonly float Width, Depth, FrontYard, BackYard, SideYard; public readonly Color Colour;
            public PlotSize(PlotKind kind, float width, float depth, float front, float back, float side, Color colour)
            { Kind = kind; Width = width; Depth = depth; FrontYard = front; BackYard = back; SideYard = side; Colour = colour; }
        }

        /// <summary>The catalogue: frontage x depth, then front, back and side yards, in metres.
        /// ⚠ FIXED. A plot is one of these exactly and is never stretched or trimmed to fill a gap; what is left
        /// over stays open ground. Every plot and every building zone in it is a whole number of tiny's 3 m
        /// building cells (BuildingGen.Cell), so a building generated for a plot lands on its lattice.
        /// ⭐ THE YARDS ARE PART OF THE PLOT (strawberry 2026-10-12: "consider space for alleys, backyards, front
        /// yards"). A front yard deep enough for a porch and a car (tinyclaw: "porches stick 3 m out the front and
        /// the garage door wants a drive"), a backyard behind, side yards so neighbours' walls do not touch -- a
        /// shop's are none, it fronts the pavement shoulder to shoulder like a high street. A corner plot is
        /// SQUARE and fronts two streets, so its street side gets a front yard too.
        /// ⚠ THE ZONES ARE SIZED FOR THE GENERATOR, NOT THE OTHER WAY ROUND. tiny's fit-the-plot houses (2026-10-12):
        /// a 4 x 5 cell small house had no room for a kitchen table in 39 of 40 seeds, and a 4 x 6 shop came out a
        /// thin floor with few shelves in 25. One cell more each -- the small house's out of its backyard, which
        /// leaves the plot and so the layout as it was; the shop's in frontage -- and the small one can go two
        /// storeys (16/40 complete, from 1) and the shop fits 40/40.
        /// ⚠ AND AN APARTMENT BLOCK IS A CORRIDOR WITH FLATS BOTH SIDES (tiny, 2026-10-13): a flat is at least four
        /// rooms of two cells side by side, plus the stairwell, and the double-loaded rows need nine deep -- 11 x 9
        /// fits 60/60 seeds, the old 8 x 7 none. The room comes out of its yards (front 3, back 6, sides 1.5: a block
        /// fronts its street closely), so the plot is 36 deep like the deepest houses' and no block or lane moves.</summary>
        public static readonly PlotSize[] PlotSizes =
        {
            new(PlotKind.HouseSmall,  15f, 30f, 6f, 6f, 1.5f, new Color(0.98f, 0.86f, 0.30f)),   // house 12 x 18
            new(PlotKind.HouseMedium, 18f, 33f, 6f, 9f, 1.5f, new Color(0.98f, 0.58f, 0.20f)),   // house 15 x 18
            new(PlotKind.HouseLarge,  24f, 36f, 6f, 9f, 1.5f, new Color(0.92f, 0.30f, 0.28f)),   // house 21 x 21
            new(PlotKind.Corner,      30f, 30f, 6f, 3f, 3f,   new Color(0.35f, 0.82f, 0.42f)),   // house 21 x 21
            new(PlotKind.Shop,        15f, 27f, 3f, 6f, 0f,   new Color(0.72f, 0.45f, 0.98f)),   // shop 15 x 18
            new(PlotKind.Apartments,  36f, 36f, 3f, 6f, 1.5f, new Color(0.30f, 0.62f, 0.98f)),   // block 33 x 27
            // (a parking lot: solved to what its building asked for -- TownParking -- so this size is only a nominal one; no
            // catalogue fill offers it)
            new(PlotKind.Parking,     17f, 20f, 0f, 0f, 0f,   new Color(0.55f, 0.55f, 0.58f)),
        };
        public static PlotSize SizeOf(PlotKind k) { foreach (var p in PlotSizes) if (p.Kind == k) return p; return PlotSizes[0]; }

        /// <summary>A building plot: a rectangle whose FRONT edge runs along a street, just behind its pavement.</summary>
        public sealed class Plot
        {
            public PlotKind Kind; public float Width, Depth;
            /// <summary>The middle of the front edge, in plan (Y is the ground there).</summary>
            public Vector3 Front;
            /// <summary>Unit vectors in plan: toward the street it fronts, and along that street.</summary>
            public Vector3 Facing, Along;
            /// <summary>The road it fronts; a corner plot also fronts Road2.</summary>
            public int Road, Road2 = -1;
            /// <summary>How much further back than its street's frontage line it stands: 0, except a corner lot
            /// nudged out along its diagonal to clear the corner's pavement.</summary>
            public float Setback;
            /// <summary>Its yards: in from the front, the back, and the left and right sides (left = the
            /// front-left corner's side; a corner lot's left side is its second street).</summary>
            public float YardFront, YardBack, YardLeft, YardRight;
            /// <summary>A frontage plot's place on its street: the arc metres of its front edge's middle along Road
            /// (-1 for a corner lot), the side it stands on (+1 = the spline's (t.z, 0, -t.x) side, the way the
            /// pavement and the lamps count sides), and whether its run of frontage is straight.</summary>
            public float Arc = -1f; public int RoadSide; public bool Straight;
            /// <summary>How far it grew into the ground round it after it was laid (GrowPlots): its backyard and its two side
            /// yards, metres. Its building zone did not move; the catalogue lot is what is left without them.</summary>
            public float GrowBack, GrowLeft, GrowRight;
            /// <summary>A lot that is not a rectangle -- a TRIANGLE lot, in what the diagonal leaves (PlaceTriangleLots): its
            /// boundary, a convex polygon in plan. Its building zone is then the rectangle Corners() gives (no yards: the
            /// rest of the polygon is its yard), and null if nothing on the 3 m lattice fits in it.</summary>
            public Vector3[] Outline;
            /// <summary>The lot's boundary whatever its shape: the outline of a triangle lot, the corners of any other.</summary>
            public Vector3[] Boundary() => Outline ?? Corners();
            public float BaseWidth => Width - GrowLeft - GrowRight;
            public float BaseDepth => Depth - GrowBack;
            /// <summary>Where its building goes: the plot less its yards, corners in the same order.</summary>
            public Vector3[] BuildArea()
            {
                float x0 = -Width * 0.5f + YardLeft, x1 = Width * 0.5f - YardRight, y0 = YardFront, y1 = Depth - YardBack;
                Vector3 P(float x, float y) => Front + Along * x - Facing * y;
                return new[] { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };
            }
            public float BuildWidth => Width - YardLeft - YardRight;
            public float BuildDepth => Depth - YardFront - YardBack;
            public Vector3 Centre => Front - Facing * (Depth * 0.5f);
            /// <summary>Its corners in plan: front-left, front-right, back-right, back-left.</summary>
            public Vector3[] Corners()
            {
                var h = Along * (Width * 0.5f); var back = -Facing * Depth;
                return new[] { Front - h, Front + h, Front + h + back, Front - h + back };
            }
        }

        /// <summary>The plots the last MarkPlots laid.</summary>
        public readonly List<Plot> Plots = new();
        /// <summary>What fronts a main, and what fronts a street. The DEFAULTS; ApplyClassAndWealth swaps the instance
        /// fields below per class (a city fronts its mains with flats, a hamlet with houses). A town with ClassAware off keeps
        /// these.</summary>
        static readonly PlotKind[] DefaultMainKinds = { PlotKind.Shop, PlotKind.Apartments, PlotKind.HouseLarge };
        static readonly PlotKind[] DefaultStreetKinds = { PlotKind.HouseSmall, PlotKind.HouseMedium, PlotKind.HouseLarge };
        public PlotKind[] MainKinds = DefaultMainKinds;
        public PlotKind[] StreetKinds = DefaultStreetKinds;
        /// <summary>The kinds a main's frontage is offered, or a street's.</summary>
        public IReadOnlyList<PlotKind> FrontageKinds(bool main) => main ? MainKinds : StreetKinds;

        /// <summary>⭐ LAND VALUE at a world point, 0 (cheap edge) .. 1 (prime downtown) -- the DENSITY heatmap (strawberry
        /// 2026-10-19: "a separate density heatmap. ie the downtown isnt magically giant plots, space is still a premium
        /// downtown ... poorer places can still have big motels, strip malls, parking lots. where land is cheap").
        ///
        /// A radial falloff from the town centre, its peak and reach set by the CLASS (a city has a broad high core, a hamlet
        /// almost none), plus a little per-block noise so the core is not a perfect disc. It is NOT wealth: a poor downtown is
        /// still dense and a rich town still has cheap roomy edges. Drives the plot MIX (KindsFor) and is read by the buildings
        /// (tall where it is high). Flat 0.5 when the town is not class-aware, so nothing changes for the plain Vary town.</summary>
        public float Density(Vector3 world)
        {
            if (!ClassAware) return 0.5f;
            float c = Mathf.Cos(Bearing), s = Mathf.Sin(Bearing);
            float dx = world.X - Centre.X, dz = world.Z - Centre.Z;
            float u = dx * c + dz * s, v = -dx * s + dz * c;
            float span = Mathf.Max(1f, Mathf.Max(_nU == int.MaxValue ? 2 : _nU, _nV == int.MaxValue ? 2 : _nV) * Spacing);
            float r = Mathf.Max(Mathf.Abs(u), Mathf.Abs(v)) / span;            // 0 at the centre, ~1 at the outer streets
            (float peak, float coreR, float edgeR) = Size switch
            {
                SettlementSize.City      => (1.00f, 0.22f, 0.95f),
                SettlementSize.Town      => (0.80f, 0.14f, 0.82f),
                SettlementSize.SmallTown => (0.55f, 0.08f, 0.72f),
                SettlementSize.Hamlet    => (0.32f, 0.00f, 0.60f),
                _                        => (0.65f, 0.12f, 0.82f),
            };
            float d = peak * (1f - Mathf.SmoothStep(coreR, edgeR, r));
            // a little block-scale noise (deterministic), so the downtown edge is ragged, not a circle
            ulong hn = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)(Mathf.RoundToInt(u / Spacing) + 128) << 10) ^ (uint)(Mathf.RoundToInt(v / Spacing) + 128) ^ 0xDE5A7UL));
            d += (Unit(hn, 0) - 0.5f) * 0.18f;
            return Mathf.Clamp(d, 0f, 1f);
        }

        /// <summary>The plot kinds a street's frontage is offered, by the DENSITY where it runs -- flats and shops and small
        /// lots downtown, roomy houses on the cheap edge. A main frontage carries the bigger/commercial kinds; a side street
        /// the homes. The plain (not class-aware) town keeps the flat MainKinds/StreetKinds.</summary>
        PlotKind[] KindsFor(RoadField roads, int r)
        {
            bool main = IsMain(roads, r);
            if (!ClassAware) return main ? MainKinds : StreetKinds;
            float L = roads.RoadLength(r);
            float d = roads.EvaluateAlong(r, L * 0.5f, out var mid, out _, snapTerrain: false) ? Density(new Vector3(mid.X, 0f, mid.Z)) : 0.4f;
            if (main)
                return d >= 0.70f ? new[] { PlotKind.Apartments, PlotKind.Shop, PlotKind.Apartments }
                     : d >= 0.45f ? new[] { PlotKind.Shop, PlotKind.Apartments, PlotKind.HouseLarge }
                     : d >= 0.22f ? new[] { PlotKind.Shop, PlotKind.HouseLarge, PlotKind.HouseMedium }
                     :              new[] { PlotKind.HouseLarge, PlotKind.HouseMedium, PlotKind.Shop };   // cheap edge main: roomy + a shop
            return d >= 0.70f ? new[] { PlotKind.HouseSmall, PlotKind.HouseSmall, PlotKind.Apartments }    // terraces + the odd block
                 : d >= 0.45f ? new[] { PlotKind.HouseSmall, PlotKind.HouseMedium }
                 : d >= 0.22f ? new[] { PlotKind.HouseMedium, PlotKind.HouseLarge }
                 :              new[] { PlotKind.HouseLarge, PlotKind.HouseMedium };                       // cheap edge: room to spread
        }

        /// <summary>A back lane through a block, from the pavement of one street to the pavement of the next.</summary>
        public sealed class Alley
        {
            public Vector3 A, B; public float Width;
            /// <summary>A starts at the knee of an L inside its block, not at a street.</summary>
            public bool FromKnee;
            /// <summary>A is where it stops on purpose, just past the last building that asked for it (TrimAlleysToRequests):
            /// closed with a kerb, not a street end that could not open.</summary>
            public bool Stub;
            public Vector3[] Corners()
            {
                var d = B - A; d.Y = 0f;
                var n = new Vector3(-d.Z, 0f, d.X).Normalized() * (Width * 0.5f);
                return new[] { A - n, B - n, B + n, A + n };
            }
        }
        /// <summary>The alleys the last MarkPlots laid.</summary>
        public readonly List<Alley> Alleys = new();
        /// <summary>⭐ AN ALLEY ONLY AS FAR AS THE BUILDING THAT ASKED FOR IT (strawberry 2026-10-14: "have alleys only extend to the
        /// building that requested them"). A block with a shop or an apartment block in it asks for a lane, and the layout is
        /// done again with one laid right across the block, street to street (or an L) -- the plots fill round it. Then it is
        /// cut back: each shop or apartment block beside it is reached from the street end nearer it, out to a pavement's
        /// width past its far edge, and the lane runs only that far, its inner end closed with a kerb (Alley.Stub). Lanes
        /// nobody beside them asked for go altogether, and what they leave is ground the plots' yards grow into (GrowPlots).
        /// UG_TOWNFULLALLEYS=1 keeps them street to street.</summary>
        public bool AlleysToRequester = System.Environment.GetEnvironmentVariable("UG_TOWNFULLALLEYS") != "1";
        public float AlleyStubMin = 8f;
        public int AlleysTrimmed, AlleysDropped; public float AlleyLengthFreed;
        void TrimAlleysToRequests(RoadField roads)
        {
            AlleysTrimmed = AlleysDropped = 0; AlleyLengthFreed = 0f;
            if (!AlleysOnRequest || !AlleysToRequester || Alleys.Count == 0) return;
            // each shop or apartment block is the lane's in its own block -- the nearest leg of it, where it is an L. (Not
            // only those backing straight onto it: the lane runs a deep plot's depth in from the frontages it parallels, so
            // a shop's yard lies between.)
            var mine = new List<Plot>[Alleys.Count];
            for (int i = 0; i < Alleys.Count; i++) mine[i] = new List<Plot>();
            var blockOf = new (int, int)?[Alleys.Count];
            for (int i = 0; i < Alleys.Count; i++) blockOf[i] = BlockOf((Alleys[i].A + Alleys[i].B) * 0.5f);
            foreach (var pl in Plots)
            {
                if (pl.Kind != PlotKind.Shop && pl.Kind != PlotKind.Apartments) continue;
                var blk = BlockOf(pl.Centre);
                if (!blk.HasValue) continue;
                int best = -1; float bestD = float.MaxValue;
                for (int i = 0; i < Alleys.Count; i++)
                {
                    if (!blockOf[i].HasValue || blockOf[i].Value != blk.Value) continue;
                    var a = Alleys[i].A; var b = Alleys[i].B;
                    var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = Mathf.Max(1e-6f, ab.LengthSquared());
                    float u = Mathf.Clamp(((pl.Centre.X - a.X) * ab.X + (pl.Centre.Z - a.Z) * ab.Y) / l2, 0f, 1f);
                    float d = new Vector2(a.X + ab.X * u - pl.Centre.X, a.Z + ab.Y * u - pl.Centre.Z).Length();
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best >= 0) mine[best].Add(pl);
            }
            var keep = new List<Alley>();
            for (int ai = 0; ai < Alleys.Count; ai++)
            {
                var al = Alleys[ai];
                var d = al.B - al.A; d.Y = 0f; float L = d.Length();
                if (L < 1f) continue;
                d /= L;
                var nrm = new Vector3(-d.Z, 0f, d.X);
                // how far in from A a stub from A has to run, and from where (as arc from A) a stub from B has to start
                float reachA = -1f, reachB = L + 1f;
                // ⚠ ONLY FROM AN END THAT OPENS. An end on a diagonal street, or into a junction's corner, closes with a kerb
                // (PlanLanes); a stub kept from there and cut short of the other was shut at both ends, a lane going nowhere.
                // So each end is tried as PlanLanes will try it (the counters it bumps put back).
                var dead = (DeadNoStreet, DeadGlancing, DeadNoCorner, DeadOffPavement, DeadOnMouth);
                bool Opens(Vector3 end, Vector3 from) { var w = end - from; w.Y = 0f; return roads != null && OpenMouth(roads, end, w.Normalized()) != null; }
                bool aOpens = !al.FromKnee && !al.Stub && Opens(al.A, al.B), bOpens = Opens(al.B, al.A);
                (DeadNoStreet, DeadGlancing, DeadNoCorner, DeadOffPavement, DeadOnMouth) = dead;
                if (!aOpens && !bOpens) { keep.Add(al); continue; }          // (neither opens: left as PlanLanes finds it)
                bool aIsStreet = aOpens;
                foreach (var pl in mine[ai])
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (var q in pl.Boundary())
                    {
                        var r = q - al.A; r.Y = 0f;
                        float sAt = r.Dot(d);
                        lo = Mathf.Min(lo, sAt); hi = Mathf.Max(hi, sAt);
                    }
                    if (hi < 0f || lo > L) continue;
                    lo = Mathf.Clamp(lo, 0f, L); hi = Mathf.Clamp(hi, 0f, L);
                    bool fromA = aIsStreet && (!bOpens || (lo + hi) * 0.5f < L * 0.5f);
                    if (fromA) reachA = Mathf.Max(reachA, hi + LaneWalkWidth);
                    else reachB = Mathf.Min(reachB, lo - LaneWalkWidth);
                }
                if (reachA < 0f && reachB > L)
                {
                    AlleysDropped++; AlleyLengthFreed += L;                    // nobody beside it asked
                    if (ProbeEverything) Log.Print($"[towngrid]   alley ({al.A.X:0},{al.A.Z:0})-({al.B.X:0},{al.B.Z:0}): {L:0} m, no shop or apartments along it -- dropped");
                    continue;
                }
                if (reachA >= 0f) reachA = Mathf.Max(reachA, AlleyStubMin);
                if (reachB <= L) reachB = Mathf.Min(reachB, L - AlleyStubMin);
                if (reachA >= reachB - 1e-3f || (reachB <= 0f && !aIsStreet)) { keep.Add(al); continue; }   // (the stubs meet: all of it)
                float kept = 0f;
                if (reachA >= 0f) { keep.Add(new Alley { A = al.A + d * reachA, B = al.A, Width = al.Width, Stub = true }); kept += reachA; }
                if (reachB <= L) { keep.Add(new Alley { A = al.A + d * Mathf.Max(0f, reachB), B = al.B, Width = al.Width, Stub = true }); kept += L - Mathf.Max(0f, reachB); }
                AlleysTrimmed++; AlleyLengthFreed += L - kept;
                if (ProbeEverything)
                    Log.Print($"[towngrid]   alley ({al.A.X:0},{al.A.Z:0})-({al.B.X:0},{al.B.Z:0}): {L:0} m cut to {kept:0} m -- "
                            + $"{(reachA >= 0f ? $"{reachA:0} m in from ({al.A.X:0},{al.A.Z:0})" : "")}{(reachA >= 0f && reachB <= L ? " and " : "")}{(reachB <= L ? $"{L - Mathf.Max(0f, reachB):0} m in from ({al.B.X:0},{al.B.Z:0})" : "")}");
            }
            Alleys.Clear(); Alleys.AddRange(keep);
            Log.Print($"[towngrid] alleys to the buildings that asked: {AlleysTrimmed} cut back, {AlleysDropped} dropped (no shop or apartments along them), {AlleyLengthFreed:0} m of lane given back to the yards");
        }
        /// <summary>How wide a back lane is, kerb to kerb of its two pavements: two 3 m cells. ⚠ DERIVED from the lane
        /// that is built in it (LaneHalf, LaneWalkWidth), so the strip the plots are kept off and the road laid down it
        /// cannot drift apart.</summary>
        public float AlleyWidth => 2f * (LaneHalf + LaneWalkWidth);
        float[] _lineU, _lineV; int _lineN; bool[,] _nodeOk;

        /// <summary>Do two convex quads (four corners in order, in plan) overlap? Shared edges do not count.</summary>
        public static bool QuadsOverlap(Vector3[] a, Vector3[] b, float tol = 0.001f)
        {
            foreach (var poly in new[] { a, b })
                for (int e = 0; e < poly.Length; e++)              // (any convex polygon: a triangle lot's outline too)
                {
                    var d = poly[(e + 1) % poly.Length] - poly[e];
                    float l = Mathf.Sqrt(d.X * d.X + d.Z * d.Z);
                    if (l < 1e-6f) continue;
                    float ax = -d.Z / l, az = d.X / l;
                    float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
                    foreach (var q in a) { float t = q.X * ax + q.Z * az; a0 = Mathf.Min(a0, t); a1 = Mathf.Max(a1, t); }
                    foreach (var q in b) { float t = q.X * ax + q.Z * az; b0 = Mathf.Min(b0, t); b1 = Mathf.Max(b1, t); }
                    if (a1 <= b0 + tol || b1 <= a0 + tol) return false;
                }
            return true;
        }
        /// <summary>How far behind the back of the pavement a plot's front line stands.</summary>
        public float PlotSetback = 0.5f;
        /// <summary>How close to a road's pavement, a slab or a corner pavement any part of a plot may come.</summary>
        public float PlotClearance = 0.25f;
        float _walkWidth = 2f;
        float _frameU0, _frameU1, _frameV0, _frameV1;

        /// <summary>Can something be built at this plan point: off every carriageway AND its pavement, off every
        /// junction slab by the same margin, and inside the town.
        /// ⚠ NO SEPARATE TEST FOR THE CORNER PAVEMENTS, and that is a measurement. A corner's kerb cuts the
        /// block's corner, so the back of its pavement can stand further than (half width + pavement) from both
        /// roads -- and I wrote a test against the built kerb outlines for it. Probed at the back of every corner
        /// pavement in a town, the junction slab runs under each one to within 0.03-2 m of that back edge, so the
        /// slab's margin already refuses all of them, 45-degree noses included; the extra test never decided a
        /// single point. The town test holds the INVARIANT (no corner pavement is buildable) instead, so if the
        /// slabs ever stop reaching, it says so.</summary>
        public bool Buildable(RoadField roads, Vector3 p)
        {
            float keep = PavementGap + _walkWidth + PlotClearance;
            if (roads.DeepestRoadOverlap(p, keep, out _, out _)) return false;
            if (OnAnyPatch(p.X, -p.Z, keep)) return false;
            // inside the town: a plot on the far side of the outermost street is in open country
            float c = Mathf.Cos(Bearing), s = Mathf.Sin(Bearing);
            float dx = p.X - Centre.X, dz = p.Z - Centre.Z;
            float u = dx * c + dz * s, v = -dx * s + dz * c;
            return u >= _frameU0 && u <= _frameU1 && v >= _frameV0 && v <= _frameV1;
        }

        /// <summary>Do two plots overlap? Edges shared exactly are not an overlap -- neighbouring plots share their
        /// side lines, as real lots do. ⚠ A MILLIMETRE, NOT FIVE CENTIMETRES: at 5 cm a frontage run that began
        /// on a slice just touching a corner lot laid its first plot 2 cm INTO it, and the test's own overlap
        /// check found all four.</summary>
        public static bool PlotsOverlap(Plot a, Plot b, float tol = 0.001f)
        {
            var ca = a.Corners(); var cb = b.Corners();
            foreach (var axis in new[] { a.Along, a.Facing, b.Along, b.Facing })
            {
                float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
                foreach (var q in ca) { float d = q.X * axis.X + q.Z * axis.Z; a0 = Mathf.Min(a0, d); a1 = Mathf.Max(a1, d); }
                foreach (var q in cb) { float d = q.X * axis.X + q.Z * axis.Z; b0 = Mathf.Min(b0, d); b1 = Mathf.Max(b1, d); }
                if (a1 <= b0 + tol || b1 <= a0 + tol) return false;     // a separating axis
            }
            return true;
        }

        /// <summary>Does this plot fit: every point of its edge buildable, and no plot already laid overlapped.
        /// ⚠ THE EDGE IS ENOUGH. A road, a slab or a pavement that reaches into a plot has to cross its edge to get
        /// there -- none of them ends in the middle of a block -- so sampling the edge every 1.5 m answers for the
        /// inside too, at a fraction of the cost. The corners go first: that is where almost every misfit is.</summary>
        bool PlotFits(RoadField roads, Plot pl)
        {
            var c = pl.Corners();
            foreach (var q in c) if (!Buildable(roads, q)) return false;
            foreach (var q in Plots) if (PlotsOverlap(pl, q)) return false;
            foreach (var al in Alleys) if (QuadsOverlap(c, al.Corners())) return false;
            for (int e = 0; e < 4; e++)
            {
                var a = c[e]; var b = c[(e + 1) % 4];
                float len = new Vector2(b.X - a.X, b.Z - a.Z).Length();
                int n = Mathf.Max(1, Mathf.CeilToInt(len / 1.5f));
                for (int k = 1; k < n; k++)
                    if (!Buildable(roads, a.Lerp(b, (float)k / n))) return false;
            }
            return true;
        }

        /// <summary>Lay out the building plots and mark them on the ground; returns how many. Call after
        /// LaySidewalkCorners, which sets the pavement width the plots stand behind.
        ///
        /// ⭐ CORNERS FIRST, THEN THE FRONTAGES. Every square corner of a block gets a square corner lot, nudged
        /// out along its diagonal until it clears the corner's pavement. Then each street is walked down both
        /// sides to find the free RUNS of frontage between them, and each run is filled from the catalogue --
        /// shops, apartments and big houses along a main, houses along a street -- as fully as the fixed sizes
        /// allow, in an order the seed picks. So blocks fill from their corners in, and an odd corner (a
        /// 45-degree wedge, a triangle island) simply gets none.
        /// ⚠ `plan: false` marks what PlanPlots already laid. The town plans its plots BEFORE its pavement, because
        /// a parking bay moves the kerb, the pavement and the plots behind it -- and the pavement and the lamps
        /// have to be laid where the bay put them, not moved after.</summary>
        public int MarkPlots(Node3D parent, RoadField roads, bool plan = true)
        {
            if (roads == null) return 0;
            if (plan) PlanPlots(roads);
            if (parent != null) { if (MarkLots) BuildPlotMarks(parent); BuildParking(parent, roads); BuildParkingLots(parent, roads); }
            return Plots.Count;
        }
        /// <summary>The plan drawn on the ground -- each lot outlined in its kind's colour, its yard tinted, its building zone
        /// and building line -- for the renders. Off for a town to PLAY: there the buildings and the fences are the lots.</summary>
        public bool MarkLots = true;

        /// <summary>Lay out the plots, the alleys, the lanes down them and the parking bays without building anything;
        /// returns how many plots. `walkWidth` is the pavement the plots stand behind (LaySidewalkCorners sets it too).</summary>
        public int PlanPlots(RoadField roads, float walkWidth = -1f)
        {
            if (walkWidth > 0f) _walkWidth = walkWidth;
            Plots.Clear(); Alleys.Clear(); Bays.Clear();
            if (roads == null) return 0;
            // (where the time goes, as one line of laps -- the plan was 15.7 s of a 101 s Generate Town)
            var planLaps = new System.Text.StringBuilder(); var planWatch = System.Diagnostics.Stopwatch.StartNew();
            void Lap(string what) { planLaps.Append($" {what} {planWatch.Elapsed.TotalSeconds:0.0},"); planWatch.Restart(); }
            float Front(int r) => roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback;

            // ⭐⭐ A LANE ONLY WHERE A BUILDING ASKS FOR ONE (strawberry 2026-10-14: "alleys should only be placed at the request
            // of a building. ie a shop wants an alley for delivery, house doesnt need it, they can have a bigger yard etc.").
            // Every block used to get a back lane, laid before any plot. Now the town is laid out first with none; a block
            // that then has a SHOP or an APARTMENT BLOCK in it asks for one -- deliveries, bins, the service side of a
            // building -- and the layout is done again with lanes in just those blocks. A block of houses keeps no lane, and
            // its backyards grow into the middle instead (GrowPlots). UG_TOWNALLALLEYS=1: a lane in every block, as before.
            HashSet<(int, int)> laneBlocks = AlleysOnRequest ? new HashSet<(int, int)>() : null;
            List<(Plot pl, int n)> parkingAsks = null;
            ParkingLotsLaid.Clear(); ParkingAsked = ParkingSpacesAsked = ParkingSpacesLaid = ParkingRefused = 0;
            int corners = 0;
            for (int layout = 0; layout < 2; layout++)
            {
                Plots.Clear(); Alleys.Clear(); corners = 0;
                // ---- alleys first: a back lane through the middle of every block, laid before any plot so the plots
                // fill round it. It runs between the block's two NON-main sides, so it comes out onto side streets
                // and a main's frontage -- its shops -- stays unbroken; between equals the seed decides; a block with
                // mains on two sides gets an L out to its other two. Walked in half metres, every stretch whose full
                // width is buildable is a lane: a block the diagonal cuts gets one each side of it.
                // ⚠ CENTRED BETWEEN THE PAVEMENTS, NOT THE ROADS. Halfway between two centrelines is 2.3 m nearer a
                // main's frontage than a street's, and on a block the jitter had narrowed, that cost the main's 36 m
                // plots their last metre: whole main frontages came out empty.
                if (_lineU != null)
                {
                    int wl = _lineU.Length;
                    float mainOff = roads.RoadHalfWidthOfMaterial(MainMaterial) + PavementGap + _walkWidth + PlotSetback;
                    float streetOff = roads.RoadHalfWidthOfMaterial(StreetMaterial) + PavementGap + _walkWidth + PlotSetback;
                    float OffOf(int axis, int line) => MainLine(axis, line - _lineN) ? mainOff : streetOff;
                    // the boulevard is a main as wide again as its median: a u line is an axis-1 line, a v line an axis-0 one
                    float boulOff = BoulevardMaterial >= 0 ? roads.RoadHalfWidthOfMaterial(BoulevardMaterial) + PavementGap + _walkWidth + PlotSetback : mainOff;
                    float OffU(int line) => BoulevardLine(1, line - _lineN) ? boulOff : OffOf(1, line);
                    float OffV(int line) => BoulevardLine(0, line - _lineN) ? boulOff : OffOf(0, line);
                    void Lane(Vector3 p0, Vector3 p1, bool knee = false)
                    {
                        var dir = p1 - p0; dir.Y = 0f; float len = dir.Length(); if (len < 1f) return;
                        dir /= len;
                        var nrm = new Vector3(-dir.Z, 0f, dir.X) * (AlleyWidth * 0.5f);
                        // ⚠ THE CENTRE RUNS TO THE PAVEMENT; THE FULL WIDTH ONLY HAS TO BE CLEAR AWAY FROM THE ENDS.
                        // Asking the whole width at every step stopped a lane meeting the diagonal at 45 degrees -- or
                        // passing a junction's slab -- metres short, its near corner having reached the clearance
                        // first: six lanes in the test town dead-ended in the grass. Within a lane's width of an end
                        // a corner may run under the pavement or the slab, both of which stand over it.
                        void Keep(float s0, float s1)
                        {
                            float zone = AlleyWidth, a = s0; bool open = true;
                            for (float t = s0; t <= s1 + 1e-3f; t += 0.5f)
                            {
                                bool sideOk = t - s0 < zone || s1 - t < zone
                                              || Buildable(roads, p0 + dir * t + nrm) && Buildable(roads, p0 + dir * t - nrm);
                                if (!sideOk && open) { Add(a, t - 0.5f); open = false; }
                                if (sideOk && !open) { a = t; open = true; }
                            }
                            if (open) Add(a, s1);
                        }
                        void Add(float s0, float s1)
                        {
                            if (s1 - s0 >= 15f)
                                Alleys.Add(new Alley { A = p0 + dir * s0, B = p0 + dir * s1, Width = AlleyWidth, FromKnee = knee && s0 == 0f });
                        }
                        float start = -1f;
                        int steps = Mathf.FloorToInt(len / 0.5f);
                        for (int k = 0; k <= steps + 1; k++)          // one past the end, which closes any open stretch
                        {
                            float tt = k * 0.5f;
                            bool ok = k <= steps && Buildable(roads, p0 + dir * tt);
                            if (ok && start < 0f) start = tt;
                            if (!ok && start >= 0f)
                            {
                                // out to the pavement: the stretch ends where its clearance began
                                Keep(start > 0f ? Mathf.Max(0f, start - PlotClearance) : 0f, Mathf.Min(len, tt - 0.5f + PlotClearance));
                                start = -1f;
                            }
                        }
                    }
                    // ⚠ WHERE A LANE GOES ACROSS ITS BLOCK is a fit, not the middle. It has to stay a deep plot plus
                    // half its width off the two frontages it runs PARALLEL to, or their 36 m plots run into it (put
                    // beside a corner lot, it emptied both of them); within that window it goes where the frontages
                    // it comes OUT on fill best -- down the middle it split ~30 m between two corner lots into two
                    // halves just short of the smallest plot. The seed chooses between equally good places.
                    float deepest = 0f; foreach (var ps in PlotSizes) deepest = Mathf.Max(deepest, ps.Depth);
                    float cornerTake = SizeOf(PlotKind.Corner).Width + 1f;
                    float FillOf(float len, PlotKind[] kinds)
                    {
                        float sum = 0f;
                        foreach (var k in FillRun((int)(len * 10f + 1e-3f), kinds, 0UL)) sum += SizeOf(k).Width;
                        return sum;
                    }
                    float BestLane(float lo, float hi, PlotKind[] exitKinds, ulong hash)
                    {
                        float half = AlleyWidth * 0.5f, w0 = lo + deepest + half + 0.5f, w1 = hi - deepest - half - 0.5f;
                        if (w1 < w0) return (lo + hi) * 0.5f;
                        float best = -1f, at = (w0 + w1) * 0.5f; int ties = 0;
                        for (float a = w0; a <= w1 + 1e-3f; a += 0.5f)
                        {
                            float f = FillOf((a - half) - (lo + cornerTake), exitKinds) + FillOf((hi - cornerTake) - (a + half), exitKinds);
                            if (f > best + 1e-3f) { best = f; at = a; ties = 1; }
                            else if (Mathf.Abs(f - best) <= 1e-3f && Unit(Mix(hash ^ (ulong)(uint)ties * 0x9E37UL), 0) * ++ties < 1f) at = a;
                        }
                        return at;
                    }
                    PlotKind[] KindsOn(int axis, int line) => MainLine(axis, line - _lineN) ? MainKinds : StreetKinds;
                    for (int gi = 0; gi + 1 < wl; gi++)
                        for (int gj = 0; gj + 1 < wl; gj++)
                        {
                            if (!_nodeOk[gi, gj] || !_nodeOk[gi + 1, gj] || !_nodeOk[gi, gj + 1] || !_nodeOk[gi + 1, gj + 1]) continue;
                            if (laneBlocks != null && !laneBlocks.Contains((gi, gj))) continue;     // nothing here asked for a lane
                            bool row0 = MainLine(0, gj - _lineN), row1 = MainLine(0, gj + 1 - _lineN);
                            bool col0 = MainLine(1, gi - _lineN), col1 = MainLine(1, gi + 1 - _lineN);
                            bool rowsMain = row0 || row1, colsMain = col0 || col1;
                            float uLo = _lineU[gi] + OffU(gi), uHi = _lineU[gi + 1] - OffU(gi + 1);
                            float vLo = _lineV[gj] + OffV(gj), vHi = _lineV[gj + 1] - OffV(gj + 1);
                            ulong ch = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)gi << 16) ^ (ulong)(uint)gj ^ 0xA11E7UL));
                            if (rowsMain && colsMain)
                            {
                                // mains on two sides: an L from inside the block out to the other two
                                int rowOut = row0 ? gj + 1 : gj, colOut = col0 ? gi + 1 : gi;
                                float ua = BestLane(uLo, uHi, KindsOn(0, rowOut), ch), va = BestLane(vLo, vHi, KindsOn(1, colOut), ch ^ 0x5EEDUL);
                                var knee = ToWorld(ua, va, 0f);
                                Lane(knee, ToWorld(ua, _lineV[rowOut], 0f), knee: true);
                                Lane(knee, ToWorld(_lineU[colOut], va, 0f), knee: true);
                                continue;
                            }
                            // along v it comes out onto the rows; along u, onto the columns
                            bool alongV = rowsMain == colsMain ? Unit(ch, 0) < 0.5f : colsMain;
                            if (alongV)
                            {
                                float at = BestLane(uLo, uHi, row0 || row1 ? MainKinds : StreetKinds, ch);
                                Lane(ToWorld(at, _lineV[gj], 0f), ToWorld(at, _lineV[gj + 1], 0f));
                            }
                            else
                            {
                                float at = BestLane(vLo, vHi, col0 || col1 ? MainKinds : StreetKinds, ch);
                                Lane(ToWorld(_lineU[gi], at, 0f), ToWorld(_lineU[gi + 1], at, 0f));
                            }
                        }
                }

                // ---- corner lots: at every square corner of every junction
                var corner = SizeOf(PlotKind.Corner);
                for (int j = 0; j < roads.JunctionCount; j++)
                {
                    var es = roads.JunctionEdges(j);
                    if (es.Count < 3) continue;
                    var node = roads.JunctionPos(j);
                    var arms = new List<(float ang, Vector3 d, int road)>();
                    foreach (var (r, atEnd) in es)
                    {
                        var d = ArmDir(roads, r, atEnd);
                        if (d != Vector3.Zero) arms.Add((Mathf.Atan2(d.Z, d.X), d, r));
                    }
                    arms.Sort((x, y) => x.ang.CompareTo(y.ang));
                    for (int i = 0; i < arms.Count; i++)
                    {
                        var A = arms[i]; var B = arms[(i + 1) % arms.Count];
                        float sweep = B.ang - A.ang; if (sweep <= 0f) sweep += Mathf.Tau;
                        if (Mathf.Abs(sweep - Mathf.Pi * 0.5f) > Mathf.DegToRad(8f)) continue;   // only a square corner
                        // the front is the bigger road; the plot runs along it and back along the other
                        var (F, S) = roads.RoadHalfWidth(B.road) > roads.RoadHalfWidth(A.road) ? (B, A) : (A, B);
                        var inward = (S.d - F.d * S.d.Dot(F.d)).Normalized();          // away from F, toward S's side
                        float offF = Front(F.road), offS = Front(S.road);
                        for (int k = 0; k < 12; k++)
                        {
                            float nudge = k * 0.5f;
                            // the corner where F's frontage line meets S's, then out along both roads by the nudge
                            var at = node + F.d * (offS + nudge) + inward * (offF + nudge);
                            var pl = new Plot { Kind = PlotKind.Corner, Width = corner.Width, Depth = corner.Depth,
                                                Along = F.d, Facing = -inward, Road = F.road, Road2 = S.road,
                                                Front = at + F.d * (corner.Width * 0.5f), Setback = nudge,
                                                // its left side is its second street: a front yard there too
                                                YardFront = corner.FrontYard, YardBack = corner.BackYard,
                                                YardLeft = corner.FrontYard, YardRight = corner.SideYard };
                            if (PlotFits(roads, pl)) { Plots.Add(pl); corners++; break; }
                        }
                    }
                }

                // ---- the buildings that asked for a parking lot on the first pass, back where they stood, and each one's lot
                // solved and laid beside it -- before the frontages fill, which then fill round them (TownParking)
                if (layout == 1 && parkingAsks != null) PlaceRequestedParking(roads, parkingAsks);

                // ---- frontages: mains first, then streets, each side walked from end to end
                var order = new List<int>(Built);
                order.Sort((x, y) => { int mx = IsMain(roads, x) ? 0 : 1, my = IsMain(roads, y) ? 0 : 1; return mx != my ? mx.CompareTo(my) : x.CompareTo(y); });
                Plot PlotAt(int r, int side, float off, float mid, float w, float d, PlotKind kind, Vector3? runDir = null)
                {
                    if (!roads.EvaluateAlong(r, mid, out var pos, out var tan, snapTerrain: false)) return null;
                    var along = runDir ?? new Vector3(tan.X, 0f, tan.Z).Normalized();
                    var outw = new Vector3(-along.Z, 0f, along.X) * side;            // from the road to this side
                    var ps = SizeOf(kind);
                    return new Plot { Kind = kind, Width = w, Depth = d, Along = along, Facing = -outw, Road = r, Front = pos + outw * off,
                                      YardFront = ps.FrontYard, YardBack = ps.BackYard, YardLeft = ps.SideYard, YardRight = ps.SideYard,
                                      Arc = mid, RoadSide = outw.Dot(new Vector3(tan.Z, 0f, -tan.X)) > 0f ? 1 : -1 };
                }
                // ⭐⭐ DEEPEST FIRST, THEN WHATEVER STILL FITS (strawberry 2026-10-13: "fill gaps in zoning with more
                // buildings"). Each side used to be walked in slices as deep as its DEEPEST kind -- 36 m -- and a block whose
                // back lane runs down its middle is only ~35 m deep either side of it, so not one slice fitted and the whole
                // frontage between its corner lots came out empty, where a 30 m or 33 m house stands with room to spare.
                // Measured on the demo town (CensusZoning): 2133 of its 5 m cells were buildable and zoned for nothing,
                // against 2677 zoned -- 1688 against 3122 since. So every side is walked once per depth the catalogue has,
                // deepest first, each pass offering only the kinds that shallow and filling only what the passes before it
                // left. What is still open is the odd ground: the diagonal's triangles, the rounded outer corners, strips
                // too thin for any plot.
                // ⚠ PASS BY PASS ACROSS THE WHOLE TOWN, not road by road: a shallow plot laid on one street before the
                // next street's deep pass could take ground the old layout gave that street. Done this way the first
                // pass IS the old layout -- same slices, same seeds, same order -- and every later pass only adds.
                // UG_TOWNDEEPONLY=1 stops after the first pass: the control.
                var depthsOf = new Dictionary<int, List<float>>();
                int passes = 0;
                foreach (int r in order)
                {
                    var ds = new List<float>();
                    foreach (var k in KindsFor(roads, r)) if (!ds.Contains(SizeOf(k).Depth)) ds.Add(SizeOf(k).Depth);
                    ds.Sort((x, y) => y.CompareTo(x));
                    if (!FillShallower) ds.RemoveRange(1, ds.Count - 1);
                    depthsOf[r] = ds; passes = Mathf.Max(passes, ds.Count);
                }
                ShallowPlots = 0;
                for (int pass = 0; pass < passes; pass++)
                foreach (int r in order)
                {
                    if (pass >= depthsOf[r].Count) continue;
                    float depth = depthsOf[r][pass];
                    var fitting = new List<PlotKind>();
                    foreach (var k in KindsFor(roads, r)) if (SizeOf(k).Depth <= depth + 1e-3f) fitting.Add(k);
                    var kinds = fitting.ToArray();
                    float L = roads.RoadLength(r), off = Front(r);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        // 1. WHERE a plot could stand at all along this side: 1 m slices, as deep as this pass's kinds.
                        //    The corner lots, the junctions and the plots already laid all show up as gaps.
                        var runs = new List<(float a, float b)>();
                        float runA = -1f;
                        for (float s0 = 0f; s0 + 1f <= L; s0 += 1f)
                        {
                            var sl = PlotAt(r, side, off, s0 + 0.5f, 1f, depth, kinds[0]);
                            bool ok = sl != null && PlotFits(roads, sl);
                            if (ok && runA < 0f) runA = s0;
                            if (!ok && runA >= 0f) { runs.Add((runA, s0)); runA = -1f; }
                        }
                        if (runA >= 0f) runs.Add((runA, Mathf.Floor(L)));
                        // ...and each run's ends found to 10 cm, not to the metre. A run is usually the stretch between
                        // two corner lots, and the catalogue fills it EXACTLY or not at all (18 + 18 = 36): a whole-
                        // metre slice that straddles a lot's edge throws away up to a metre at each end -- the town lost
                        // 11 of its 138 plots that way once the overlap test was made strict.
                        for (int ri = 0; ri < runs.Count; ri++)
                        {
                            var (ra, rb) = runs[ri];
                            for (int k = 0; k < 10 && ra - 0.1f >= 0f; k++)
                            {
                                var sl = PlotAt(r, side, off, ra - 0.05f, 0.1f, depth, kinds[0]);
                                if (sl == null || !PlotFits(roads, sl)) break;
                                ra -= 0.1f;
                            }
                            for (int k = 0; k < 10 && rb + 0.1f <= L; k++)
                            {
                                var sl = PlotAt(r, side, off, rb + 0.05f, 0.1f, depth, kinds[0]);
                                if (sl == null || !PlotFits(roads, sl)) break;
                                rb += 0.1f;
                            }
                            runs[ri] = (ra, rb);
                        }
                        // 2. each run filled from the catalogue as fully as its kinds can be, in an order the seed
                        //    picks, the leftover split between the run's two ends so the plots stay shoulder to shoulder
                        for (int ri = 0; ri < runs.Count; ri++)
                        {
                            var (a, b) = runs[ri];
                            ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)r << 20) ^ ((ulong)(uint)(side + 1) << 16) ^ (ulong)(uint)ri));
                            if (pass > 0) h = Mix(h ^ ((ulong)(uint)pass * 0xD3E95UL));
                            var fill = FillRun((int)((b - a) * 10f + 1e-3f), kinds, h);
                            float used = 0f; foreach (var k in fill) used += SizeOf(k).Width;
                            float at = a + ((b - a) - used) * 0.5f;
                            // ⚠ ONE DIRECTION FOR A STRAIGHT RUN. Each plot took the road's tangent at its own middle,
                            // and a straight road's tangent still wanders by ~1e-4 rad from one point to the next -- over
                            // a 36 m depth that is a few millimetres of overlap between neighbours that should share an
                            // edge, enough for the strict overlap test to refuse every other plot in a run.
                            Vector3? runDir = null;
                            if (roads.EvaluateAlong(r, a, out var pa, out var ta, snapTerrain: false)
                                && roads.EvaluateAlong(r, b, out var pb, out var tb, snapTerrain: false)
                                && roads.EvaluateAlong(r, (a + b) * 0.5f, out _, out var tm, snapTerrain: false))
                            {
                                var chord = new Vector3(pb.X - pa.X, 0f, pb.Z - pa.Z).Normalized();
                                float straight = Mathf.Cos(Mathf.DegToRad(0.5f));
                                if (new Vector3(ta.X, 0f, ta.Z).Normalized().Dot(chord) > straight
                                    && new Vector3(tb.X, 0f, tb.Z).Normalized().Dot(chord) > straight
                                    && new Vector3(tm.X, 0f, tm.Z).Normalized().Dot(chord) > straight) runDir = chord;
                            }
                            foreach (var k in fill)
                            {
                                var sz = SizeOf(k);
                                var pl = PlotAt(r, side, off, at + sz.Width * 0.5f, sz.Width, sz.Depth, k, runDir);
                                if (pl != null) pl.Straight = runDir != null;
                                if (pl != null && PlotFits(roads, pl)) { Plots.Add(pl); if (pass > 0) ShallowPlots++; }
                                at += sz.Width;
                            }
                        }
                    }
                }
                if (layout == 1) break;
                // what the first layout's buildings ask for: a lane (a block with a shop or apartments in it), a parking lot
                bool again = false;
                if (laneBlocks != null)
                {
                    LaneRequests = 0;
                    foreach (var pl in Plots)
                    {
                        if (pl.Kind != PlotKind.Shop && pl.Kind != PlotKind.Apartments) continue;
                        var blk = BlockOf(pl.Centre);
                        if (blk.HasValue && laneBlocks.Add(blk.Value)) LaneRequests++;
                    }
                    again |= laneBlocks.Count > 0;
                }
                parkingAsks = new List<(Plot, int)>();
                if (ParkingLots) foreach (var pl in Plots) { int n = ParkingFor(pl); if (n > 0) parkingAsks.Add((pl, n)); }
                again |= parkingAsks.Count > 0;
                if (!again) break;                // nobody asked: the first layout stands
            }
            Lap("layout");
            TrimAlleysToRequests(roads);
            PlanLanes(roads);
            Lap("lanes");
            PlanProfiles(roads);  // each street's kerb, verge or pavement (TownVerges) -- the plots decide it, the bays need it
            int stepped = LayParkingBays(roads);
            Lap("bays");
            GrowPlots(roads);
            Lap("grow");
            MeetPlots(roads);     // ...and the slivers growth left between neighbours, closed: no dead grass with a fence down each side
            Lap("meet");
            // the parking lots over whatever room the plots' yards left beside them, and only then their ways in -- a lot laid
            // again may come in somewhere else (TownParking)
            StretchLots(roads);
            PlanParkingEntrances(roads);
            Lap("lots");
            PlaceTriangleLots(roads);
            Lap("triangles");
            PlanFences(roads);
            Lap("fences");
            CensusZoning(roads);
            Lap("zoning");
            PlanGreens(roads);
            Lap("greens");
            BuildContext(roads);   // what each plot knows of what is round it, now everything round it is planned
            Lap("context");
            Log.Print($"[towngrid] plan timing (s):{planLaps.ToString().TrimEnd(',')}");
            var counts = new System.Text.StringBuilder();
            foreach (var ps in PlotSizes)
            {
                int n = 0; foreach (var pl in Plots) if (pl.Kind == ps.Kind) n++;
                counts.Append($" {ps.Kind} {n} ({ps.Width:0}x{ps.Depth:0});");
            }
            counts.Append($" Triangle {TrianglesLaid} ({TrianglesZoned} zoned);");
            if (ProbeEverything)
                foreach (var lot in ParkingLotsLaid)
                    foreach (var o in Plots)
                        if (!ReferenceEquals(o, lot.Lot) && (o.Outline != null ? QuadsOverlap(lot.Lot.Corners(), o.Outline, 0.01f) : PlotsOverlap(lot.Lot, o, 0.01f)))
                            Log.Print($"[towngrid]   ⚠ parking lot at ({lot.Lot.Front.X:0},{lot.Lot.Front.Z:0}) overlaps a {o.Kind} at ({o.Front.X:0},{o.Front.Z:0})");
            if (ParkingAsked > 0)
                Log.Print($"[towngrid] parking lots: {ParkingAsked} buildings asked ({ParkingSpacesAsked} spaces), {ParkingLotsLaid.Count} lots laid beside them "
                        + $"({ParkingSpacesLaid} stalls, {ParkingWaysIn} ways in), {ParkingCornersTaken} corner lots given way to them, {ParkingRefused} refused (no room on the frontage)");
            float area = 0f;
            foreach (var pl in Plots)
            {
                if (pl.Outline == null) { area += pl.Width * pl.Depth; continue; }
                float twice = 0f; var ol = pl.Outline;
                for (int k = 0; k < ol.Length; k++) { var a = ol[k]; var b = ol[(k + 1) % ol.Length]; twice += a.X * b.Z - b.X * a.Z; }
                area += Mathf.Abs(twice) * 0.5f;
            }
            float lanes = 0f; foreach (var al in Alleys) lanes += (al.B - al.A).Length();
            Log.Print($"[towngrid] plots: {Plots.Count} marked ({ShallowPlots} of them where only a shallower kind fitted), "
                    + $"{corners} on square corners, {area / 10000f:0.00} ha; {Alleys.Count} alleys, {lanes:0} m"
                    + (AlleysOnRequest ? $" ({LaneRequests} blocks asked for a lane: a shop or apartments in them)" : " (a lane in every block)") + $" --{counts}");
            int spaces = 0, paired = 0;
            foreach (var bay in Bays) spaces += bay.Spaces;
            foreach (var bay in Bays) if (bay.Side > 0 && BayOpposite(bay) != null) paired++;
            Log.Print($"[towngrid] parking: {Bays.Count} bays ({Bays.FindAll(b => b.Taper <= SquareTaper + 1e-4f).Count} cut square), {spaces} spaces; {paired} stretches widened on both sides; "
                    + $"{stepped} plots stepped back {BayDepth:0.0} m behind them, of {_bayOffered} fronting a side chosen for parking "
                    + $"({_bayNoRoom} no room behind, {_bayNearJunction} too near a junction, {_bayAtLane} beside a lane's mouth, {_bayTooShort} in runs too short for a car)");
            return Plots.Count;
        }

        /// <summary>The plots, by kind, that fill a run of `runDm` DECIMETRES of frontage as fully as the kinds
        /// allow, in an order the seed picks among the fullest. ⚠ A FILL, NOT A PICK-THEN-HOPE: choosing each plot
        /// on its own left a 24 m house in a 36 m run between two corner lots, with 12 m nothing could take, where
        /// 18 + 18 fills it exactly. An unbounded knapsack in decimetres -- whole metres threw away up to one
        /// at every run, and a run that only just holds two plots then holds one.</summary>
        static List<PlotKind> FillRun(int runDm, PlotKind[] kinds, ulong seed)
        {
            var list = new List<PlotKind>();
            if (runDm <= 0) return list;
            static int Dm(PlotKind k) => (int)Mathf.Round(SizeOf(k).Width * 10f);
            var reach = new bool[runDm + 1]; reach[0] = true;
            for (int x = 1; x <= runDm; x++)
                foreach (var k in kinds) { int w = Dm(k); if (w <= x && reach[x - w]) { reach[x] = true; break; } }
            int at = runDm; while (at > 0 && !reach[at]) at--;
            for (int n = 0; at > 0; n++)
            {
                var ok = new List<PlotKind>();
                foreach (var k in kinds) { int w = Dm(k); if (w <= at && reach[at - w]) ok.Add(k); }
                var pick = ok[(int)(Unit(Mix(seed ^ ((ulong)(uint)n * 0x9E3779B97F4A7C15UL)), 0) * ok.Count) % ok.Count];
                list.Add(pick); at -= Dm(pick);
            }
            return list;
        }

        /// <summary>The plots and alleys marked on the ground. Each plot: its lot line in its kind's colour, the
        /// whole lot lightly tinted, and the building zone the yards leave tinted strongly with a pale outline --
        /// so front yard, backyard and side yards read as the lighter ring round it. Each alley: a gravel lane.</summary>
        int BuildPlotMarks(Node3D parent)
        {
            const float Lift = 0.05f;
            float Ground(float x, float z, float fallback) => Terrain.Active != null ? Terrain.Active.SampleHeight(x, z) : fallback;
            Vector3 G(Vector3 q, float lift, float fallback) => new Vector3(q.X, Ground(q.X, q.Z, fallback) + lift, q.Z);
            // a closed outline, as flat quads in 3 m pieces so it follows the ground
            void Outline(SurfaceTool st, Vector3[] c, float width, float lift, float fallback)
            {
                for (int e = 0; e < 4; e++)
                {
                    var a = c[e]; var b = c[(e + 1) % 4];
                    var dir = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
                    float len = dir.Length();
                    if (len < 0.01f) continue;
                    dir /= len;
                    var n = new Vector3(-dir.Z, 0f, dir.X) * (width * 0.5f);
                    a -= dir * (width * 0.5f); b += dir * (width * 0.5f);   // overlap at the corners: no notch
                    int pieces = Mathf.Max(1, Mathf.CeilToInt((len + width) / 3f));
                    for (int k = 0; k < pieces; k++)
                    {
                        var p0 = a.Lerp(b, (float)k / pieces); var p1 = a.Lerp(b, (float)(k + 1) / pieces);
                        var v00 = G(p0 - n, lift, fallback); var v01 = G(p0 + n, lift, fallback);
                        var v10 = G(p1 - n, lift, fallback); var v11 = G(p1 + n, lift, fallback);
                        foreach (var v in new[] { v00, v01, v11, v00, v11, v10 }) { st.SetNormal(Vector3.Up); st.AddVertex(v); }
                    }
                }
            }
            // a closed outline of any polygon (a triangle lot's), in 3 m pieces
            void OutlinePoly(SurfaceTool st, Vector3[] c, float width, float lift, float fallback)
            {
                for (int e = 0; e < c.Length; e++)
                {
                    var a = c[e]; var b = c[(e + 1) % c.Length];
                    var dir = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
                    float len = dir.Length();
                    if (len < 0.01f) continue;
                    dir /= len;
                    var n = new Vector3(-dir.Z, 0f, dir.X) * (width * 0.5f);
                    a -= dir * (width * 0.5f); b += dir * (width * 0.5f);
                    int pieces = Mathf.Max(1, Mathf.CeilToInt((len + width) / 3f));
                    for (int k = 0; k < pieces; k++)
                    {
                        var p0 = a.Lerp(b, (float)k / pieces); var p1 = a.Lerp(b, (float)(k + 1) / pieces);
                        var v00 = G(p0 - n, lift, fallback); var v01 = G(p0 + n, lift, fallback);
                        var v10 = G(p1 - n, lift, fallback); var v11 = G(p1 + n, lift, fallback);
                        foreach (var v in new[] { v00, v01, v11, v00, v11, v10 }) { st.SetNormal(Vector3.Up); st.AddVertex(v); }
                    }
                }
            }
            // a filled convex polygon, fanned from its middle and each fan triangle split into a ~3 m grid on the ground
            void FillPoly(SurfaceTool st, Vector3[] c, float lift, float fallback)
            {
                var m = Vector3.Zero; foreach (var v in c) m += v; m /= c.Length;
                for (int e = 0; e < c.Length; e++)
                {
                    var a = c[e]; var b = c[(e + 1) % c.Length];
                    float longest = Mathf.Max(Flat(m, a), Mathf.Max(Flat(m, b), Flat(a, b)));
                    int n = Mathf.Max(1, Mathf.CeilToInt(longest / 3f));
                    Vector3 P(int i, int k) => G(m + (a - m) * ((float)i / n) + (b - m) * ((float)k / n), lift, fallback);
                    for (int i = 0; i < n; i++)
                        for (int k = 0; i + k < n; k++)
                        {
                            foreach (var v in new[] { P(i, k), P(i + 1, k), P(i, k + 1) }) { st.SetNormal(Vector3.Up); st.AddVertex(v); }
                            if (i + k + 1 < n)
                                foreach (var v in new[] { P(i + 1, k), P(i + 1, k + 1), P(i, k + 1) }) { st.SetNormal(Vector3.Up); st.AddVertex(v); }
                        }
                }
            }
            // a filled quad, as a 3 m grid so it lies on the ground
            void Fill(SurfaceTool st, Vector3[] c, float lift, float fallback)
            {
                float wu = (c[1] - c[0]).Length(), wv = (c[3] - c[0]).Length();
                int nu = Mathf.Max(1, Mathf.CeilToInt(wu / 3f)), nv = Mathf.Max(1, Mathf.CeilToInt(wv / 3f));
                Vector3 P(int i, int k) => G(c[0].Lerp(c[1], (float)i / nu).Lerp(c[3].Lerp(c[2], (float)i / nu), (float)k / nv), lift, fallback);
                for (int i = 0; i < nu; i++)
                    for (int k = 0; k < nv; k++)
                        foreach (var v in new[] { P(i, k), P(i + 1, k), P(i + 1, k + 1), P(i, k), P(i + 1, k + 1), P(i, k + 1) })
                        { st.SetNormal(Vector3.Up); st.AddVertex(v); }
            }
            MeshInstance3D Commit(SurfaceTool st, string name, Color colour, bool unshaded = true)
            {
                var mesh = st.Commit();
                if (mesh == null || mesh.GetSurfaceCount() == 0) return null;
                var mat = new StandardMaterial3D
                {
                    AlbedoColor = colour, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    ShadingMode = unshaded ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
                    Transparency = colour.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
                };
                var mi = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = mat };
                parent.AddChild(mi);
                return mi;
            }
            int made = 0;
            // the alleys: gravel, lit like the ground it is -- only where no lane was built down them (UG_TOWNNOLANES=1)
            if (Alleys.Count > 0 && LaneRoads.Count == 0)
            {
                var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
                foreach (var al in Alleys) Fill(st, al.Corners(), Lift * 0.4f, al.A.Y);
                if (Commit(st, "Plots_Alleys", new Color(0.66f, 0.60f, 0.50f), unshaded: false) != null) made++;
            }
            foreach (var ps in PlotSizes)
            {
                var lots = new SurfaceTool(); lots.Begin(Mesh.PrimitiveType.Triangles);
                var tint = new SurfaceTool(); tint.Begin(Mesh.PrimitiveType.Triangles);
                var zone = new SurfaceTool(); zone.Begin(Mesh.PrimitiveType.Triangles);
                var zoneLine = new SurfaceTool(); zoneLine.Begin(Mesh.PrimitiveType.Triangles);
                int any = 0;
                foreach (var pl in Plots)
                {
                    if (pl.Kind != ps.Kind) continue;
                    any++;
                    Outline(lots, pl.Corners(), 0.45f, Lift, pl.Front.Y);
                    if (pl.Kind == PlotKind.Parking) continue;      // (its asphalt and stripes are its marks: BuildParkingLots)
                    Fill(tint, pl.Corners(), Lift * 0.5f, pl.Front.Y);
                    Fill(zone, pl.BuildArea(), Lift * 0.7f, pl.Front.Y);
                    Outline(zoneLine, pl.BuildArea(), 0.25f, Lift * 0.9f, pl.Front.Y);
                }
                if (any == 0) continue;
                if (Commit(lots, $"Plots_{ps.Kind}", ps.Colour) != null) made++;
                Commit(tint, $"Plots_{ps.Kind}_Yards", new Color(ps.Colour.R, ps.Colour.G, ps.Colour.B, 0.16f));
                Commit(zone, $"Plots_{ps.Kind}_Building", new Color(ps.Colour.R, ps.Colour.G, ps.Colour.B, 0.45f));
                Commit(zoneLine, $"Plots_{ps.Kind}_BuildingLine", new Color(1f, 1f, 0.95f, 0.85f));
            }
            // the triangle lots: their outline, lightly tinted, and the building zone in them as any lot's
            {
                var lots = new SurfaceTool(); lots.Begin(Mesh.PrimitiveType.Triangles);
                var tint = new SurfaceTool(); tint.Begin(Mesh.PrimitiveType.Triangles);
                var zone = new SurfaceTool(); zone.Begin(Mesh.PrimitiveType.Triangles);
                var zoneLine = new SurfaceTool(); zoneLine.Begin(Mesh.PrimitiveType.Triangles);
                int any = 0;
                foreach (var pl in Plots)
                {
                    if (pl.Outline == null) continue;
                    any++;
                    OutlinePoly(lots, pl.Outline, 0.45f, Lift, pl.Front.Y);
                    FillPoly(tint, pl.Outline, Lift * 0.5f, pl.Front.Y);
                    if (pl.Width > 0f && pl.Depth > 0f)
                    {
                        Fill(zone, pl.BuildArea(), Lift * 0.7f, pl.Front.Y);
                        Outline(zoneLine, pl.BuildArea(), 0.25f, Lift * 0.9f, pl.Front.Y);
                    }
                }
                if (any > 0)
                {
                    if (Commit(lots, "Plots_Triangle", TriangleColour) != null) made++;
                    Commit(tint, "Plots_Triangle_Yards", new Color(TriangleColour.R, TriangleColour.G, TriangleColour.B, 0.16f));
                    Commit(zone, "Plots_Triangle_Building", new Color(TriangleColour.R, TriangleColour.G, TriangleColour.B, 0.45f));
                    Commit(zoneLine, "Plots_Triangle_BuildingLine", new Color(1f, 1f, 0.95f, 0.85f));
                }
            }
            // (UG_TOWNPROBES=1: the ground the triangle lots left near them, red)
            if (TriangleGapCells.Count > 0)
            {
                var gs = new SurfaceTool(); gs.Begin(Mesh.PrimitiveType.Triangles);
                foreach (var c in TriangleGapCells)
                    Fill(gs, new[] { c + new Vector3(-0.45f, 0f, -0.45f), c + new Vector3(0.45f, 0f, -0.45f), c + new Vector3(0.45f, 0f, 0.45f), c + new Vector3(-0.45f, 0f, 0.45f) }, Lift * 1.5f, Centre.Y);
                Commit(gs, "Plots_TriangleGaps", new Color(0.95f, 0.15f, 0.10f, 0.75f));
            }
            // (UG_TOWNPROBES=1: the dead space PlanGreens found, magenta)
            if (DeadSpaceCells.Count > 0)
            {
                var ds = new SurfaceTool(); ds.Begin(Mesh.PrimitiveType.Triangles);
                foreach (var c in DeadSpaceCells)
                    Fill(ds, new[] { c + new Vector3(-0.4f, 0f, -0.4f), c + new Vector3(0.4f, 0f, -0.4f), c + new Vector3(0.4f, 0f, 0.4f), c + new Vector3(-0.4f, 0f, 0.4f) }, Lift * 1.6f, Centre.Y);
                Commit(ds, "Plots_DeadSpace", new Color(0.9f, 0.2f, 0.9f, 0.7f));
            }
            // the yard fences, designated: a dark line along each run, over the lot lines -- only where the real ones are not
            // being built (UG_TOWNFENCES=0; BuildFences lays them)
            if (Fences.Count > 0 && !BuildRealFences)
            {
                var fs = new SurfaceTool(); fs.Begin(Mesh.PrimitiveType.Triangles);
                foreach (var f in Fences)
                {
                    var dir = new Vector3(f.B.X - f.A.X, 0f, f.B.Z - f.A.Z);
                    float len = dir.Length();
                    if (len < 0.05f) continue;
                    dir /= len;
                    var n = new Vector3(-dir.Z, 0f, dir.X) * 0.18f;
                    int pieces = Mathf.Max(1, Mathf.CeilToInt(len / 3f));
                    for (int k = 0; k < pieces; k++)
                    {
                        var p0 = f.A.Lerp(f.B, (float)k / pieces); var p1 = f.A.Lerp(f.B, (float)(k + 1) / pieces);
                        var v00 = G(p0 - n, Lift * 1.3f, f.A.Y); var v01 = G(p0 + n, Lift * 1.3f, f.A.Y);
                        var v10 = G(p1 - n, Lift * 1.3f, f.A.Y); var v11 = G(p1 + n, Lift * 1.3f, f.A.Y);
                        foreach (var v in new[] { v00, v01, v11, v00, v11, v10 }) { fs.SetNormal(Vector3.Up); fs.AddVertex(v); }
                    }
                }
                if (Commit(fs, "Plots_Fences", FenceMarkColour) != null) made++;
            }
            return made;
        }
        /// <summary>The colour a designated yard fence is marked in: dark timber, against every plot colour.</summary>
        public static readonly Color FenceMarkColour = new Color(0.30f, 0.18f, 0.10f);

        // ==============================================================================================
        // BACK LANES (strawberry 2026-10-13: "replace placeholder alleys with single lane roads with short
        // sidewalks/kerbs and no markings").
        // ==============================================================================================

        /// <summary>UG_TOWNNOLANES=1: the alleys stay gravel strips -- the control.</summary>
        public bool BuildBackLanes = System.Environment.GetEnvironmentVariable("UG_TOWNNOLANES") != "1";
        /// <summary>UG_TOWNDEEPONLY=1: each frontage offers its deepest kind only, as before the shallower passes -- the
        /// control for them.</summary>
        public bool FillShallower = System.Environment.GetEnvironmentVariable("UG_TOWNDEEPONLY") != "1";
        /// <summary>Plots the shallower passes laid, on ground the deepest kind could not use.</summary>
        public int ShallowPlots;
        /// <summary>UG_TOWNALLALLEYS=1: a back lane through every block, asked for or not -- the old way, and the control.</summary>
        public bool AlleysOnRequest = System.Environment.GetEnvironmentVariable("UG_TOWNALLALLEYS") != "1";
        /// <summary>How many blocks the last PlanPlots gave a lane because a shop or an apartment block in them asked.</summary>
        public int LaneRequests;
        /// <summary>The grid block (column, row) a plan point stands in, or null outside the grid.</summary>
        public (int, int)? BlockOf(Vector3 p)
        {
            if (_lineU == null || _lineV == null) return null;
            float c = Mathf.Cos(Bearing), s = Mathf.Sin(Bearing);
            float dx = p.X - Centre.X, dz = p.Z - Centre.Z;
            float u = dx * c + dz * s, v = -dx * s + dz * c;
            int gi = -1, gj = -1;
            for (int k = 0; k + 1 < _lineU.Length; k++) if (u >= _lineU[k] && u < _lineU[k + 1]) { gi = k; break; }
            for (int k = 0; k + 1 < _lineV.Length; k++) if (v >= _lineV[k] && v < _lineV[k + 1]) { gj = k; break; }
            return gi >= 0 && gj >= 0 ? (gi, gj) : null;
        }

        // ==============================================================================================
        // GROWING THE LOTS INTO WHAT IS LEFT (strawberry 2026-10-14: "work on filling spaces with more/expanding
        // existing plots").
        // ==============================================================================================

        /// <summary>UG_TOWNNOEXPAND=1: the lots stay their catalogue size -- the control.</summary>
        public bool ExpandPlots = System.Environment.GetEnvironmentVariable("UG_TOWNNOEXPAND") != "1";
        /// <summary>How far a lot may grow: back, and out to each side, and in what steps.</summary>
        public float ExpandMaxBack = 30f, ExpandMaxSide = 15f, ExpandStep = 0.5f;
        /// <summary>What the last GrowPlots did: how many lots grew, and by how many square metres between them.</summary>
        public int PlotsGrown; public float GrownArea;

        /// <summary>⭐ EVERY LOT GROWS ITS YARDS INTO THE GROUND ROUND IT. The catalogue is fixed and a block is not, so the
        /// frontage passes leave strips no lot fits: between a row's backs and the lane (up to 20 m where the lane was
        /// placed for the deepest kind and a shallower one stood), the ends of rows short of a corner, the odd triangle a
        /// diagonal leaves. 35% of the demo town's buildable ground was zoned for nothing. Now each lot pushes its
        /// backyard and its two side yards out, half a metre at a time and all lots in turn -- so two lots facing across
        /// a strip meet in its middle rather than the first taking all of it -- until a street's pavement, a slab, a
        /// lane, another lot or the edge of town stops it.
        /// ⚠ ONLY THE YARDS GROW. Its front stays on its street and its building zone stays exactly the catalogue's, on
        /// the 3 m lattice, where it was: a lot is its building plus more garden, never a bigger building.</summary>
        /// <summary>⚠ IN FRONT OF A PARKING BAY'S STEPPED-BACK PLOTS: the bay pushes its pavement's back edge out over
        /// [Front0, Front1], which Buildable (a street's own width) cannot know -- a lot beside one grew its front corner onto
        /// the bay's pavement (the town test's plots-on-built-pavement check).</summary>
        bool BayFrontAt(RoadField roads, Vector3 q)
        {
            foreach (var bay in Bays)
            {
                RoadNear(roads, bay.Road, q, out float along, out float off);
                if (along < bay.Front0 - 0.05f || along > bay.Front1 + 0.05f) continue;
                if (!roads.EvaluateAlong(bay.Road, along, out var bp, out var bt, snapTerrain: false)) continue;
                if (((q - bp).Dot(new Vector3(bt.Z, 0f, -bt.X)) > 0f ? 1 : -1) != bay.Side) continue;
                if (off < roads.RoadHalfWidth(bay.Road) + PavementGap + _walkWidth + PlotSetback + bay.Depth - 0.01f) return true;
            }
            return false;
        }
        /// <summary>On a back lane as BUILT, not just its strip: at a hard 90 the two legs' strips each stop at the knee and
        /// leave its outer corner open, where the two pavements meet square -- a lot grew a metre into one.</summary>
        bool LaneAt(Vector3 q)
        {
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
                    if (k == 1 && lp.KneeA) a -= d * laneReach;              // out past the knee, to its square corner
                    if (k == pts.Count - 1 && lp.KneeB) b += d * laneReach;
                    var ab = new Vector2(b.X - a.X, b.Z - a.Z);
                    float t = ((q.X - a.X) * ab.X + (q.Z - a.Z) * ab.Y) / Mathf.Max(1e-6f, ab.LengthSquared());
                    // ⚠ ...and ends SQUARE there. Clamped like the rest, the run past the knee ended in a half-disc reaching
                    // laneReach beyond the square corner it is there to cover -- 3 m out over grass no pavement is on, which
                    // a triangle lot's corner came up against and stopped
                    if (t < 0f && k == 1 && lp.KneeA || t > 1f && k == pts.Count - 1 && lp.KneeB) continue;
                    t = Mathf.Clamp(t, 0f, 1f);
                    if (new Vector2(a.X + ab.X * t - q.X, a.Z + ab.Y * t - q.Z).Length() < laneReach) return true;
                }
            }
            return false;
        }

        void GrowPlots(RoadField roads)
        {
            PlotsGrown = 0; GrownArea = 0f;
            if (!ExpandPlots || Plots.Count == 0) return;
            float step = Mathf.Max(0.1f, ExpandStep);
            var open = new bool[Plots.Count, 3];                          // back, left, right
            for (int i = 0; i < Plots.Count; i++) for (int s = 0; s < 3; s++) open[i, s] = true;
            _roadSamples.Clear();
            bool EdgeClear(Vector3 a, Vector3 b)
            {
                float len = new Vector2(b.X - a.X, b.Z - a.Z).Length();
                int n = Mathf.Max(1, Mathf.CeilToInt(len / 1.5f));
                for (int k = 0; k <= n; k++)
                {
                    var q = a.Lerp(b, (float)k / n);
                    if (!Buildable(roads, q) || BayFrontAt(roads, q) || LaneAt(q)) return false;
                }
                return true;
            }
            bool Clear(Plot pl, Vector3 a, Vector3 b)
            {
                if (!EdgeClear(a, b)) return false;
                var c = pl.Corners();
                foreach (var q in Plots)
                    if (!ReferenceEquals(q, pl) && (q.Outline != null ? QuadsOverlap(c, q.Outline) : PlotsOverlap(pl, q))) return false;
                foreach (var al in Alleys) if (QuadsOverlap(c, al.Corners())) return false;
                return true;
            }
            var before = new float[Plots.Count];
            for (int i = 0; i < Plots.Count; i++) before[i] = Plots[i].Width * Plots[i].Depth;
            for (int round = 0; round < 400; round++)
            {
                bool any = false;
                for (int n = 0; n < Plots.Count; n++)
                {
                    int i = round % 2 == 0 ? n : Plots.Count - 1 - n;            // each way in turn: no lot always first
                    var pl = Plots[i];
                    if (pl.Outline != null || pl.Kind == PlotKind.Parking) continue;   // a triangle lot is already all it can be; a lot is solved
                    for (int s = 0; s < 3; s++)
                    {
                        if (!open[i, s]) continue;
                        if (s == 0 && pl.GrowBack + step > ExpandMaxBack + 1e-3f || s == 1 && pl.GrowLeft + step > ExpandMaxSide + 1e-3f
                            || s == 2 && pl.GrowRight + step > ExpandMaxSide + 1e-3f) { open[i, s] = false; continue; }
                        // grow, then look at the new edge and at everything the bigger lot now touches
                        if (s == 0) { pl.Depth += step; pl.YardBack += step; pl.GrowBack += step; }
                        else if (s == 1) { pl.Width += step; pl.Front -= pl.Along * (step * 0.5f); pl.YardLeft += step; pl.GrowLeft += step; }
                        else { pl.Width += step; pl.Front += pl.Along * (step * 0.5f); pl.YardRight += step; pl.GrowRight += step; }
                        var c = pl.Corners();          // front-left, front-right, back-right, back-left
                        var (ea, eb) = s == 0 ? (c[2], c[3]) : s == 1 ? (c[3], c[0]) : (c[1], c[2]);
                        if (Clear(pl, ea, eb)) { any = true; continue; }
                        if (s == 0) { pl.Depth -= step; pl.YardBack -= step; pl.GrowBack -= step; }
                        else if (s == 1) { pl.Width -= step; pl.Front += pl.Along * (step * 0.5f); pl.YardLeft -= step; pl.GrowLeft -= step; }
                        else { pl.Width -= step; pl.Front -= pl.Along * (step * 0.5f); pl.YardRight -= step; pl.GrowRight -= step; }
                        open[i, s] = false;
                    }
                }
                if (!any) break;
            }
            for (int i = 0; i < Plots.Count; i++)
            {
                float gained = Plots[i].Width * Plots[i].Depth - before[i];
                if (gained > 0.01f) { PlotsGrown++; GrownArea += gained; }
            }
            Log.Print($"[towngrid] lots grown: {PlotsGrown} of {Plots.Count} pushed their yards into the ground round them, "
                    + $"{GrownArea / 10000f:0.00} ha more garden (backs up to {ExpandMaxBack:0} m, sides up to {ExpandMaxSide:0} m)");
        }

        // ==============================================================================================
        // TRIANGLE LOTS (strawberry 2026-10-14: "add support for stuff to be built in triangular plots").
        // ==============================================================================================

        /// <summary>UG_TOWNNOTRIANGLES=1: the diagonal's leftover wedges stay grass -- the control.</summary>
        public bool TriangleLots = System.Environment.GetEnvironmentVariable("UG_TOWNNOTRIANGLES") != "1";
        /// <summary>The smallest wedge worth a lot (square metres), how far its building keeps off every edge, and the
        /// smallest building zone, in 3 m cells a side.
        /// ⚠ FOUR CELLS, the smallest the building generator lays a plan in (strawberry 2026-10-15: "the teal plots arent scaled
        /// properly to the building size"): it said so of every smaller zone -- "a depth of 2 cells is too shallow for two rows
        /// of rooms: 4 at least" -- and built 4x4 anyway, over the pavement. A wedge with no zone that big has no building zone,
        /// and an empty lot is a pocket park (PlanGreens).</summary>
        public float TriangleMinArea = 120f, TriangleYard = 1.5f; public int TriangleMinCells = 4;
        /// <summary>What the last PlaceTriangleLots did: lots laid, how many of them with a building zone, and the
        /// leftovers it passed over (too small, or not a wedge: an L round a lot's corner).</summary>
        public int TrianglesLaid, TrianglesZoned, TrianglesPassed;
        /// <summary>UG_TOWNTRICORNERS=0: wedge outlines as they come off the raster, their points cut off -- the control.</summary>
        public bool FillCorners = System.Environment.GetEnvironmentVariable("UG_TOWNTRICORNERS") != "0";
        /// <summary>After the last PlaceTriangleLots: square metres of ground a lot could still have (inside every frontage
        /// line, off the paving, in no lot) within 4 m of a triangle lot; and the triangle lots' sides that stop short of what
        /// bounds them -- a point cut off square, ground a lot could have just past it (SidesShort).</summary>
        public int TriangleCornerGap, TriangleSidesShort;
        /// <summary>(UG_TOWNPROBES=1) that ground, a metre square at a time: drawn red over the plots.</summary>
        public readonly List<Vector3> TriangleGapCells = new();
        public static readonly Color TriangleColour = new Color(0.20f, 0.78f, 0.78f);

        /// <summary>⭐ THE DIAGONAL'S WEDGES BECOME LOTS. A 45-degree avenue across a square grid cuts every block it crosses
        /// into a rectangle-ish piece and a TRIANGLE, and no catalogue lot -- a rectangle squared to its street -- fits a
        /// triangle: they stayed grass. Now the ground still free near the avenue is found a metre at a time, each piece
        /// of it that fronts the avenue and is a wedge (convex: nearly all of its own outline) becomes a TRIANGLE LOT with
        /// that outline, and inside it the biggest building zone of whole 3 m cells that keeps TriangleYard off every edge --
        /// squared to the avenue or to the grid, whichever holds the bigger one -- so the generator that builds the town's
        /// rectangles builds here too, and whatever wants the whole wedge (a flatiron, a pocket park) has its shape.
        /// ⚠ LAID AFTER THE OTHER LOTS GROW. Before, the free ground near the avenue was the wedges AND the strips between
        /// rows of lots that reach them -- a house block has no lane now, so 20 m of ground runs down its middle -- and
        /// most pieces came out L-shaped (10 of 26 laid). Grown first, the lots take the strips, and stop where their
        /// edges meet the avenue's pavement: what they leave by the avenue is the wedge.</summary>
        void PlaceTriangleLots(RoadField roads)
        {
            TrianglesLaid = TrianglesZoned = TrianglesPassed = 0;
            if (!TriangleLots || DiagonalRoads.Count == 0 || roads == null) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _roadSamples.Clear();
            // the avenue as one straight line: its pieces lie along it
            var all = new List<Vector3>();
            float diagFront = 0f;
            foreach (int r in DiagonalRoads)
            {
                all.AddRange(RoadSamples(roads, r).p);
                diagFront = Mathf.Max(diagFront, roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback);
            }
            if (all.Count < 2) return;
            var o = all[0];
            Vector3 far = o; float fd = 0f;
            foreach (var q in all) { float d2 = Flat(o, q); if (d2 > fd) { fd = d2; far = q; } }
            var D = new Vector3(far.X - o.X, 0f, far.Z - o.Z).Normalized();
            var Nd = new Vector3(-D.Z, 0f, D.X);
            float tLo = float.MaxValue, tHi = float.MinValue;
            foreach (var q in all) { float t = (q - o).Dot(D); tLo = Mathf.Min(tLo, t); tHi = Mathf.Max(tHi, t); }
            const float Reach = 80f;
            bool InRect(Plot pl, Vector3 q)
            {
                var rel = q - pl.Front;
                float a = rel.X * pl.Along.X + rel.Z * pl.Along.Z, f = -(rel.X * pl.Facing.X + rel.Z * pl.Facing.Z);
                return Mathf.Abs(a) <= pl.Width * 0.5f && f >= 0f && f <= pl.Depth;
            }
            bool Taken(Vector3 q)
            {
                foreach (var pl in Plots) if (pl.Outline != null ? InsidePoly(pl.Outline, q) : InRect(pl, q)) return true;
                foreach (var al in Alleys) if (InsidePoly(al.Corners(), q)) return true;
                return false;
            }
            // 1. the free ground within reach of the avenue, a metre square at a time
            var free = new HashSet<(int, int)>();
            int x0 = int.MaxValue, x1 = int.MinValue, z0 = int.MaxValue, z1 = int.MinValue;
            foreach (var q in all)
            {
                x0 = Mathf.Min(x0, Mathf.FloorToInt(q.X - Reach)); x1 = Mathf.Max(x1, Mathf.CeilToInt(q.X + Reach));
                z0 = Mathf.Min(z0, Mathf.FloorToInt(q.Z - Reach)); z1 = Mathf.Max(z1, Mathf.CeilToInt(q.Z + Reach));
            }
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    var q = new Vector3(x + 0.5f, 0f, z + 0.5f);
                    var rel = q - o; float t = rel.Dot(D), n = Mathf.Abs(rel.Dot(Nd));
                    if (n > Reach || t < tLo - Reach || t > tHi + Reach) continue;
                    if (Taken(q) || !Buildable(roads, q) || BayFrontAt(roads, q) || LaneAt(q)) continue;
                    free.Add((x, z));
                }
            // ...less every cell with a taken one beside it: a strip a metre or two wide between two rows of lots is how the
            // ground behind a row reaches a wedge, and it is not part of the wedge
            var core = new HashSet<(int, int)>();
            foreach (var c in free)
            {
                bool inner = true;
                for (int dx = -1; dx <= 1 && inner; dx++) for (int dz = -1; dz <= 1 && inner; dz++) if (!free.Contains((c.Item1 + dx, c.Item2 + dz))) inner = false;
                if (inner) core.Add(c);
            }
            // 2. each piece of it, 4-connected
            var seen = new HashSet<(int, int)>();
            foreach (var start in core)
            {
                if (seen.Contains(start)) continue;
                var piece = new List<(int, int)>(); var stack = new Stack<(int, int)>();
                stack.Push(start); seen.Add(start);
                while (stack.Count > 0)
                {
                    var c = stack.Pop(); piece.Add(c);
                    foreach (var nb in new[] { (c.Item1 + 1, c.Item2), (c.Item1 - 1, c.Item2), (c.Item1, c.Item2 + 1), (c.Item1, c.Item2 - 1) })
                        if (core.Contains(nb) && seen.Add(nb)) stack.Push(nb);
                }
                // grown back by the cell the erosion took, where that ground is free
                var cells = new HashSet<(int, int)>(piece);
                foreach (var c in piece)
                    for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
                        if (free.Contains((c.Item1 + dx, c.Item2 + dz))) cells.Add((c.Item1 + dx, c.Item2 + dz));
                Vector3 PieceMid() { float sx = 0f, sz = 0f; foreach (var c in cells) { sx += c.Item1 + 0.5f; sz += c.Item2 + 0.5f; } return new Vector3(sx / cells.Count, 0f, sz / cells.Count); }
                void Passed(string why) { TrianglesPassed++; if (ProbeEverything) Log.Print($"[towngrid]   leftover at {PieceMid()}: {cells.Count} m2, passed over -- {why}"); }
                if (cells.Count < TriangleMinArea) { if (cells.Count > 20) Passed("too small"); continue; }
                // ...fronting the avenue -- measured off its pieces as built, not off the straight line through them, which
                // its jittered crossings leave by a metre or two near its ends
                bool fronts = false;
                foreach (var c in cells)
                {
                    var q = new Vector3(c.Item1 + 0.5f, 0f, c.Item2 + 0.5f);
                    if (Mathf.Abs((q - o).Dot(Nd)) > diagFront + 6f) continue;     // (the line first: cheap, and generous)
                    foreach (int r in DiagonalRoads)
                    {
                        RoadNear(roads, r, q, out _, out float off);
                        if (off <= roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback + 1.5f) { fronts = true; break; }
                    }
                    if (fronts) break;
                }
                if (!fronts) { Passed("not on the avenue"); continue; }
                // 3. its outline: the hull of its cells' MIDDLES -- inside the free ground by half a cell, so it cannot reach
                //    into the lot next door; SnapOutline then takes each side out onto the line it stands short of -- and a
                //    wedge only if the cells fill nearly all of it
                var pts = new List<Vector2>();
                foreach (var c in cells) pts.Add(new Vector2(c.Item1 + 0.5f, c.Item2 + 0.5f));
                var hull = Hull2(pts);
                float hullArea = 0f, perim = 0f;
                for (int k = 0; k < hull.Count; k++) { var a = hull[k]; var b = hull[(k + 1) % hull.Count]; hullArea += a.X * b.Y - b.X * a.Y; perim += (b - a).Length(); }
                hullArea = Mathf.Abs(hullArea) * 0.5f;
                float cellsArea = hullArea + perim * 0.5f + 1f;          // its hull grown by the half cell round it
                if (hullArea < 1f || cells.Count / cellsArea < 0.85f) { Passed($"not a wedge: fills {cells.Count / Mathf.Max(1f, cellsArea) * 100f:0}% of its hull"); continue; }
                hull = SimplifyHull(hull, 15f);
                // 4. the biggest building zone of whole 3 m cells in it, squared to the avenue or to the grid
                var best = (area: 0f, A: Vector3.Zero, N: Vector3.Zero, s0: 0f, t0: 0f, w: 0f, h: 0f);
                var oc = new Vector3(piece[0].Item1 + 0.5f, 0f, piece[0].Item2 + 0.5f);
                foreach (var A in new[] { D, new Vector3(Mathf.Cos(Bearing), 0f, Mathf.Sin(Bearing)) })
                {
                    var N = new Vector3(-A.Z, 0f, A.X);
                    float sMin = float.MaxValue, sMax = float.MinValue, tMin = float.MaxValue, tMax = float.MinValue;
                    foreach (var c in cells)
                        foreach (var cx in new[] { 0f, 1f }) foreach (var cz in new[] { 0f, 1f })
                        {
                            var w = new Vector3(c.Item1 + cx, 0f, c.Item2 + cz) - oc;
                            float sv = w.Dot(A), tv = w.Dot(N);
                            sMin = Mathf.Min(sMin, sv); sMax = Mathf.Max(sMax, sv); tMin = Mathf.Min(tMin, tv); tMax = Mathf.Max(tMax, tv);
                        }
                    int ns = Mathf.CeilToInt(sMax - sMin), nt = Mathf.CeilToInt(tMax - tMin);
                    if (ns < 2 || nt < 2) continue;
                    var grid = new bool[ns, nt];
                    for (int i = 0; i < ns; i++)
                        for (int k = 0; k < nt; k++)
                        {
                            var w = oc + A * (sMin + i + 0.5f) + N * (tMin + k + 0.5f);
                            grid[i, k] = cells.Contains((Mathf.FloorToInt(w.X), Mathf.FloorToInt(w.Z)));
                        }
                    // kept TriangleYard off every edge: eroded, a square at a time
                    int er = Mathf.CeilToInt(TriangleYard);
                    var ok = new bool[ns, nt];
                    for (int i = 0; i < ns; i++)
                        for (int k = 0; k < nt; k++)
                        {
                            bool all2 = true;
                            for (int di = -er; di <= er && all2; di++)
                                for (int dk = -er; dk <= er && all2; dk++)
                                {
                                    int ii = i + di, kk = k + dk;
                                    if (ii < 0 || kk < 0 || ii >= ns || kk >= nt || !grid[ii, kk]) all2 = false;
                                }
                            ok[i, k] = all2;
                        }
                    // the largest rectangle of it whose sides snap to whole 3 m cells: every height, the histogram way
                    var heights = new int[nt];
                    for (int i = 0; i < ns; i++)
                    {
                        for (int k = 0; k < nt; k++) heights[k] = ok[i, k] ? heights[k] + 1 : 0;
                        for (int k0 = 0; k0 < nt; k0++)
                        {
                            int minH = int.MaxValue;
                            for (int k1 = k0; k1 < nt; k1++)
                            {
                                minH = Mathf.Min(minH, heights[k1]);
                                if (minH == 0) break;
                                float wS = Mathf.Floor(minH / 3f) * 3f, hT = Mathf.Floor((k1 - k0 + 1) / 3f) * 3f;
                                if (wS < TriangleMinCells * 3f || hT < TriangleMinCells * 3f) continue;
                                if (wS * hT > best.area + 1e-3f)
                                    best = (wS * hT, A, N, sMin + (i + 1 - minH) + (minH - wS) * 0.5f, tMin + k0 + ((k1 - k0 + 1) - hT) * 0.5f, wS, hT);
                            }
                        }
                    }
                    // (best's origin is oc, its s and t the zone's low corner)
                }
                float yAt(float x, float z) => _heightAt != null ? _heightAt(x, z) : Centre.Y;
                var outline = new Vector3[hull.Count];
                for (int k = 0; k < hull.Count; k++) outline[k] = new Vector3(hull[k].X, 0f, hull[k].Y);
                var snapped = SnapOutline(roads, outline);
                if (!IsConvex(snapped))
                {
                    // a short side squeezed between two long ones that snapped to different lines can fold in: take the hull
                    // of the snapped corners, and keep it only if it still stands clear of every other lot
                    var sp = new List<Vector2>(); foreach (var v in snapped) sp.Add(new Vector2(v.X, v.Z));
                    var sh = SimplifyHull(Hull2(sp), 5f);
                    snapped = new Vector3[sh.Count];
                    for (int k = 0; k < sh.Count; k++) snapped[k] = new Vector3(sh[k].X, 0f, sh[k].Y);
                }
                // ...and clear of every other lot: a lot whose corner pokes into the wedge's notch, or a side snapped a hair past
                // its neighbour's, is cut back along that lot's side that faces it -- the snapped outline first, keeping its
                // front on the avenue's frontage line; the raw one if that cannot be made clear
                Vector3[] ClearOf(Vector3[] poly)
                {
                    for (int pass = 0; pass < 3 && poly.Length >= 3; pass++)
                    {
                        Plot hit = null;
                        foreach (var other in Plots) if (QuadsOverlap(poly, other.Boundary(), 0.01f)) { hit = other; break; }
                        if (hit == null) return poly;
                        poly = ClipAway(poly, hit.Boundary());
                    }
                    if (poly.Length < 3) return null;
                    foreach (var other in Plots) if (QuadsOverlap(poly, other.Boundary(), 0.01f)) return null;
                    return poly;
                }
                // fronting the avenue: some point of its edges within reach of the avenue's frontage line
                bool Fronts(Vector3[] poly)
                {
                    for (int e = 0; e < poly.Length; e++)
                    {
                        var a = poly[e]; var b = poly[(e + 1) % poly.Length];
                        int nn = Mathf.Max(1, Mathf.CeilToInt(Flat(a, b)));
                        for (int k = 0; k <= nn; k++)
                        {
                            var v = a.Lerp(b, (float)k / nn);
                            foreach (int r in DiagonalRoads)
                            {
                                RoadNear(roads, r, v, out _, out float off);
                                if (off <= roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback + 1.5f) return true;
                            }
                        }
                    }
                    return false;
                }
                float areaBefore = PolyArea(outline), areaAfter = PolyArea(snapped);
                bool snapOk = IsConvex(snapped) && areaAfter > areaBefore * 0.8f && areaAfter < areaBefore * 1.25f;
                Vector3[] chosen = null; string how = "";
                if (snapOk) { chosen = ClearOf(snapped); how = "snapped"; }
                if (chosen == null || PolyArea(chosen) < TriangleMinArea * 0.75f || !Fronts(chosen)) { chosen = ClearOf(outline); how = "raw"; }
                if (ProbeEverything) Log.Print($"[towngrid]   wedge outline {areaBefore:0} m2 -> snapped {areaAfter:0} m2, {(snapOk ? "convex" : "refused")}; using the {how} one{(chosen == null ? " -- NONE clear of the lots" : $", {PolyArea(chosen):0} m2")}");
                if (chosen == null) { Passed("its outline would take in a corner of another lot"); continue; }
                if (PolyArea(chosen) < TriangleMinArea * 0.75f) { Passed("too small once clear of the lot beside it"); continue; }
                if (!Fronts(chosen)) { Passed("cut clear of the lot beside it, it no longer reaches the avenue"); continue; }
                outline = chosen;
                // ⭐ RIGHT INTO ITS CORNERS (strawberry 2026-10-14, of the first triangle lots: "make sure it fills right to the
                // corners" -- and of a building zone that then did: "i meant the plot, not the actual building"). Off the metre
                // raster every point of a wedge came out CUT OFF SQUARE, by a side 2-5 m long that lies on nothing (measured:
                // all 15 lots, all their points); the lines that really bound it -- the avenue's frontage, the street's, the
                // next lot's side -- meet metres further on. ⚠ The first try kept every side 2.5 m and longer as a line, and
                // those cuts ARE 2.5 m and longer: it moved no corner of 11 lots by more than a hair. So the outline is rebuilt
                // from the sides that lie ON such a line, and each corner that lands on ground a lot cannot have (a corner
                // pavement, a slab's margin) is cut back only as far as that ground comes, the cut following its edge.
                if (FillCorners)
                {
                    var mid0 = Vector3.Zero; foreach (var v in outline) mid0 += v; mid0 /= outline.Length;
                    float rad0 = 0f; foreach (var v in outline) rad0 = Mathf.Max(rad0, Flat(v, mid0));
                    var streets = StreetsNear(roads, mid0, rad0 + 12f);
                    var full = FillToCorners(outline, q => LotCouldHave(roads, q, streets),
                                             (Vector3 a, Vector3 b, out Vector3 lp, out Vector3 ld) => BoundingLine(roads, a, b, out lp, out ld));
                    if (full != null)
                        foreach (var other in Plots)
                            if (QuadsOverlap(full, other.Boundary(), 0.01f)) { _fillWhy = "it would overlap another lot"; full = null; break; }
                    if (ProbeEverything) Log.Print($"[towngrid]   wedge corners: {(full != null ? $"filled, {PolyArea(outline):0} -> {PolyArea(full):0} m2{(_fillWhy.Length > 0 ? " (" + _fillWhy + ")" : "")}" : "left -- " + _fillWhy)}");
                    if (full != null) outline = full;
                }
                for (int k = 0; k < outline.Length; k++) outline[k].Y = yAt(outline[k].X, outline[k].Z);
                // the avenue it fronts
                int road = -1; float rBest = float.MaxValue;
                var mid = Vector3.Zero; foreach (var v in outline) mid += v; mid /= outline.Length;
                foreach (int r in DiagonalRoads) { RoadNear(roads, r, mid, out _, out float off); if (off < rBest) { rBest = off; road = r; } }
                var toward = -Nd * Mathf.Sign((mid - o).Dot(Nd));
                var pl = new Plot { Kind = PlotKind.Triangle, Outline = outline, Road = road };
                if (best.area > 0f)
                {
                    var zc0 = oc + best.A * (best.s0 + best.w * 0.5f) + best.N * (best.t0 + best.h * 0.5f);
                    foreach (var (ds, dt) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                        if (!InsidePoly(outline, zc0 + best.A * (best.w * 0.5f * ds) + best.N * (best.h * 0.5f * dt))) { best.area = 0f; break; }
                }
                if (best.area > 0f)
                {
                    // its front: whichever side of the zone faces the avenue most squarely
                    var zc = oc + best.A * (best.s0 + best.w * 0.5f) + best.N * (best.t0 + best.h * 0.5f);
                    Vector3 facing = best.N; float halfF = best.h * 0.5f, halfA = best.w * 0.5f;
                    foreach (var (f, hf, ha) in new[] { (best.N, best.h * 0.5f, best.w * 0.5f), (-best.N, best.h * 0.5f, best.w * 0.5f),
                                                        (best.A, best.w * 0.5f, best.h * 0.5f), (-best.A, best.w * 0.5f, best.h * 0.5f) })
                        if (f.Dot(toward) > facing.Dot(toward) + 1e-4f) { facing = f; halfF = hf; halfA = ha; }
                    pl.Facing = facing; pl.Along = new Vector3(-facing.Z, 0f, facing.X);
                    pl.Width = halfA * 2f; pl.Depth = halfF * 2f;
                    pl.Front = zc + facing * halfF; pl.Front.Y = yAt(pl.Front.X, pl.Front.Z);
                    TrianglesZoned++;
                }
                else
                {
                    pl.Facing = toward; pl.Along = new Vector3(-toward.Z, 0f, toward.X); pl.Front = mid; pl.Front.Y = yAt(mid.X, mid.Z);
                }
                Plots.Add(pl);
                TrianglesLaid++;
                if (ProbeEverything)
                    Log.Print($"[towngrid]   triangle lot at {mid}: {cells.Count} m2, {outline.Length} corners, zone {(best.area > 0f ? $"{pl.Width:0}x{pl.Depth:0} m" : "none")}");
            }
            // MEASURED: the triangle lots' sides that stop short of what bounds them, and (UG_TOWNPROBES=1) the ground a
            // lot could still have within 4 m of one, patch by patch
            TriangleCornerGap = 0; TriangleSidesShort = 0; TriangleGapCells.Clear();
            var gapSeen = new HashSet<(int, int)>();
            foreach (var t in Plots)
            {
                if (t.Kind != PlotKind.Triangle || t.Outline == null) continue;
                var ol = t.Outline;
                var shortAt = new List<Vector3>();
                TriangleSidesShort += SidesShort(roads, t, shortAt);
                if (!ProbeEverything) continue;
                var tm = Vector3.Zero; foreach (var v in ol) tm += v; tm /= ol.Length;
                float trad = 0f, bx0 = float.MaxValue, bx1 = float.MinValue, bz0 = float.MaxValue, bz1 = float.MinValue;
                foreach (var v in ol) { trad = Mathf.Max(trad, Flat(v, tm)); bx0 = Mathf.Min(bx0, v.X); bx1 = Mathf.Max(bx1, v.X); bz0 = Mathf.Min(bz0, v.Z); bz1 = Mathf.Max(bz1, v.Z); }
                var streets = StreetsNear(roads, tm, trad + 6f);
                var near = new HashSet<(int, int)>();
                for (int x = Mathf.FloorToInt(bx0 - 4f); x <= Mathf.CeilToInt(bx1 + 4f); x++)
                    for (int z = Mathf.FloorToInt(bz0 - 4f); z <= Mathf.CeilToInt(bz1 + 4f); z++)
                    {
                        var q = new Vector3(x + 0.5f, 0f, z + 0.5f);
                        if (InsidePoly(ol, q)) continue;
                        float nearest = float.MaxValue;
                        for (int e = 0; e < ol.Length; e++)
                        {
                            var a = ol[e]; var b = ol[(e + 1) % ol.Length];
                            var ab = new Vector2(b.X - a.X, b.Z - a.Z);
                            float u = Mathf.Clamp(((q.X - a.X) * ab.X + (q.Z - a.Z) * ab.Y) / Mathf.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
                            nearest = Mathf.Min(nearest, new Vector2(a.X + ab.X * u - q.X, a.Z + ab.Y * u - q.Z).Length());
                        }
                        if (nearest >= 4f || !LotCouldHave(roads, q, streets)) continue;
                        near.Add((x, z));
                        if (gapSeen.Add((x, z))) { TriangleCornerGap++; TriangleGapCells.Add(q); }
                    }
                var sb = new System.Text.StringBuilder();
                sb.Append($"[towngrid]   triangle lot at ({tm.X:0},{tm.Z:0}), {PolyArea(ol):0} m2, corners");
                foreach (var v in ol) sb.Append($" ({v.X:0.0},{v.Z:0.0})");
                sb.Append($": {near.Count} m2 a lot could have within 4 m; {shortAt.Count} sides short of what bounds it, at");
                foreach (var v in shortAt) sb.Append($" ({v.X:0.0},{v.Z:0.0})");
                var seenN = new HashSet<(int, int)>();
                foreach (var s0 in near)
                {
                    if (!seenN.Add(s0)) continue;
                    var patch = new List<(int, int)>(); var st = new Stack<(int, int)>(); st.Push(s0);
                    while (st.Count > 0)
                    {
                        var c = st.Pop(); patch.Add(c);
                        foreach (var nb in new[] { (c.Item1 + 1, c.Item2), (c.Item1 - 1, c.Item2), (c.Item1, c.Item2 + 1), (c.Item1, c.Item2 - 1) })
                            if (near.Contains(nb) && seenN.Add(nb)) st.Push(nb);
                    }
                    if (patch.Count < 2) continue;
                    float px = 0f, pz = 0f; foreach (var c in patch) { px += c.Item1 + 0.5f; pz += c.Item2 + 0.5f; }
                    var pm = new Vector3(px / patch.Count, 0f, pz / patch.Count);
                    int vk = 0; float vd = float.MaxValue;
                    for (int k = 0; k < ol.Length; k++) { float d2 = Flat(ol[k], pm); if (d2 < vd) { vd = d2; vk = k; } }
                    sb.Append($"\n[towngrid]     patch {patch.Count} m2 at ({pm.X:0.0},{pm.Z:0.0}), {vd:0.0} m from corner {vk}");
                }
                Log.Print(sb.ToString());
            }
            Log.Print($"[towngrid] triangle lots: {TrianglesLaid} laid in the wedges the diagonal leaves ({TrianglesZoned} with a building zone of whole 3 m cells), "
                    + $"{TrianglesPassed} leftovers passed over (too small, not a wedge, or not on the avenue); "
                    + $"{TriangleSidesShort} of their sides stop short of what bounds them{(FillCorners ? "" : " (UG_TOWNTRICORNERS=0)")}"
                    + (ProbeEverything ? $", {TriangleCornerGap} m2 a lot could have left within 4 m of them" : "") + $" ({clock.ElapsedMilliseconds} ms)");
        }
        // (a point ON another lot's line is not in it: a wedge snapped onto its neighbour's side shares that line)
        static bool InRectStrict(Plot pl, Vector3 q, float inset)
        {
            var rel = q - pl.Front;
            float a = rel.X * pl.Along.X + rel.Z * pl.Along.Z, f = -(rel.X * pl.Facing.X + rel.Z * pl.Facing.Z);
            return Mathf.Abs(a) < pl.Width * 0.5f - inset && f > inset && f < pl.Depth - inset;
        }
        static bool InPolyStrict(Vector3[] c, Vector3 q, float inset)
        {
            if (!InsidePoly(c, q)) return false;
            for (int e = 0; e < c.Length; e++)
            {
                var a = c[e]; var b = c[(e + 1) % c.Length];
                var ab = new Vector2(b.X - a.X, b.Z - a.Z);
                float u = Mathf.Clamp(((q.X - a.X) * ab.X + (q.Z - a.Z) * ab.Y) / Mathf.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
                if (new Vector2(a.X + ab.X * u - q.X, a.Z + ab.Y * u - q.Z).Length() < inset) return false;
            }
            return true;
        }
        bool TakenStrict(Vector3 q)
        {
            foreach (var pl in Plots) if (pl.Outline != null ? InPolyStrict(pl.Outline, q, 0.03f) : InRectStrict(pl, q, 0.03f)) return true;
            foreach (var al in Alleys) if (InPolyStrict(al.Corners(), q, 0.03f)) return true;
            return false;
        }
        /// <summary>The streets that pass within `reach` of a spot's frontage, each with how far from its centreline a lot's
        /// front stands -- the lines a lot near there may not cross.</summary>
        List<(int r, float front)> StreetsNear(RoadField roads, Vector3 at, float reach)
        {
            var list = new List<(int, float)>();
            foreach (int r in Built)
            {
                RoadNear(roads, r, at, out _, out float off);
                float front = roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback;
                if (off < reach + front + 1f) list.Add((r, front));
            }
            return list;
        }
        /// <summary>⭐ Could a lot have this spot? Off the paving and every slab's margin (Buildable), BEHIND every street's
        /// frontage line (to 5 cm: a lot leaves the strip between it and the pavement), off a parking bay's front and a back
        /// lane, and in no lot -- a spot on another lot's line is not in it. `streets` (StreetsNear) narrows the frontage
        /// test to the streets near; null asks every street.</summary>
        public bool LotCouldHave(RoadField roads, Vector3 q, List<(int r, float front)> streets = null)
        {
            if (TakenStrict(q)) return false;
            if (streets != null)
            {
                foreach (var (r, front) in streets) { RoadNear(roads, r, q, out _, out float off); if (off < front - 0.05f) return false; }
            }
            else
                foreach (int r in Built)
                {
                    RoadNear(roads, r, q, out _, out float off);
                    if (off < roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback - 0.05f) return false;
                }
            return Buildable(roads, q) && !BayFrontAt(roads, q) && !LaneAt(q);
        }
        /// <summary>The line that really bounds a lot's side a-b, if there is one: a street's frontage line or the side of another
        /// lot it runs beside. 1: the side lies ON it (to 30 cm and 3.5 degrees); 2: it lies just OUTSIDE the side (out to
        /// 2.5 m, within 15 degrees) -- the raster's side, standing short of what bounds it -- and `p`, `d` are that line,
        /// `d` the side's way; 0: neither, a side the raster made (the square cut across a wedge's point, a stair on its
        /// slant, a chord across a pocket). `self`: the lot whose side it is, if it is laid already -- not a line to itself.</summary>
        int BoundingLine(RoadField roads, Vector3 a, Vector3 b, out Vector3 p, out Vector3 d, Plot self = null)
        {
            p = a; d = Vector3.Zero;
            var sd = new Vector3(b.X - a.X, 0f, b.Z - a.Z); float len = sd.Length();
            if (len < 0.5f) return 0;
            sd /= len; d = sd;
            var m = (a + b) * 0.5f;
            const float Tol = 0.3f, Par = 0.998f, Near = 2.5f, Loose = 0.966f;
            int kind = 0; float bestOut = float.MaxValue;
            foreach (int r in Built)
            {
                RoadNear(roads, r, m, out float along, out float off);
                float front = roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback;
                float stand = off - front;                                     // how far the side stands back from the line
                if (stand < -Tol || stand > Near) continue;
                if (!roads.EvaluateAlong(r, along, out var rp, out var rt, snapTerrain: false)) continue;
                var rd = new Vector3(rt.X, 0f, rt.Z).Normalized();
                float par = Mathf.Abs(rd.Dot(sd));
                if (Mathf.Abs(stand) <= Tol && par >= Par) { p = a; d = sd; return 1; }
                if (stand <= Tol || par < Loose || stand >= bestOut || len < 3f) continue;
                var right = new Vector3(rd.Z, 0f, -rd.X);
                float sign = (m - rp).Dot(right) > 0f ? 1f : -1f;
                bestOut = stand; kind = 2; p = rp + right * (front * sign); p.Y = 0f; d = rd.Dot(sd) > 0f ? rd : -rd;
            }
            foreach (var pl in Plots)
            {
                if (ReferenceEquals(pl, self)) continue;
                var c = pl.Boundary();
                var pc = Vector3.Zero; foreach (var v in c) pc += v; pc /= c.Length;
                for (int k = 0; k < c.Length; k++)
                {
                    var p0 = c[k]; var p1 = c[(k + 1) % c.Length];
                    var e = new Vector3(p1.X - p0.X, 0f, p1.Z - p0.Z); float el = e.Length();
                    if (el < 0.5f) continue;
                    e /= el;
                    float par = Mathf.Abs(e.Dot(sd));
                    if (par < Loose) continue;
                    float t0 = (a - p0).Dot(e), t1 = (b - p0).Dot(e);
                    if (Mathf.Max(t0, t1) < 0.5f || Mathf.Min(t0, t1) > el - 0.5f) continue;     // only a side it runs beside
                    var nrm = new Vector3(-e.Z, 0f, e.X);
                    float sm = (m - p0).Dot(nrm), sc = (pc - p0).Dot(nrm);
                    float stand = Mathf.Abs(sm);
                    if (stand <= Tol && par >= Par) { p = a; d = sd; return 1; }
                    if (stand <= Tol || stand > Near || stand >= bestOut || sm * sc > 0f || len < 3f) continue;   // (that lot beyond it)
                    bestOut = stand; kind = 2; p = new Vector3(p0.X, 0f, p0.Z); d = e.Dot(sd) > 0f ? e : -e;
                }
            }
            return kind;
        }
        delegate int LineOfSide(Vector3 a, Vector3 b, out Vector3 p, out Vector3 d);
        /// <summary>⭐ The sides of a triangle lot that STOP SHORT of what bounds it -- what a point cut off square is: a side on
        /// no line that bounds it (a street's frontage, the side of a lot it runs beside: BoundingLine) with ground a lot could
        /// have (LotCouldHave) just outside it, 60 cm out anywhere along it. A side that reaches is on such a line, or has
        /// nothing a lot could have past it. Each one's middle into `at`, if given.</summary>
        public int SidesShort(RoadField roads, Plot t, List<Vector3> at = null)
        {
            var ol = t?.Outline;
            if (ol == null || ol.Length < 3) return 0;
            float twice = 0f;
            for (int k = 0; k < ol.Length; k++) { var a = ol[k]; var b = ol[(k + 1) % ol.Length]; twice += a.X * b.Z - b.X * a.Z; }
            var m = Vector3.Zero; foreach (var v in ol) m += v; m /= ol.Length;
            float rad = 0f; foreach (var v in ol) rad = Mathf.Max(rad, Flat(v, m));
            var streets = StreetsNear(roads, m, rad + 2f);
            int n = 0;
            for (int k = 0; k < ol.Length; k++)
            {
                var a = new Vector3(ol[k].X, 0f, ol[k].Z); var b = new Vector3(ol[(k + 1) % ol.Length].X, 0f, ol[(k + 1) % ol.Length].Z);
                float len = Flat(a, b);
                if (len < 0.05f) continue;
                var d = (b - a) / len;
                var outN = twice > 0f ? new Vector3(d.Z, 0f, -d.X) : new Vector3(-d.Z, 0f, d.X);    // (the outside: right of a counter-clockwise side)
                bool open = false;
                int ns = Mathf.Max(2, Mathf.CeilToInt(len / 0.5f));
                for (int u = 1; u < ns && !open; u++) open = LotCouldHave(roads, a + d * (len * u / ns) + outN * 0.6f, streets);
                // ⚠ NOT ITS OWN SIDE: laid, the lot is in Plots, and every side of it lies exactly on a side of a lot -- itself.
                // Asked without `self` this said 0 for the control too, whose points are all cut off.
                if (!open || BoundingLine(roads, a, b, out _, out _, t) == 1) continue;
                n++; at?.Add((a + b) * 0.5f);
            }
            return n;
        }

        /// <summary>⚠ A WEDGE'S EDGES ON THE LINES ROUND IT. Its outline comes off a metre raster, so each side stands up to a
        /// metre short of what bounds it -- a sliver of grass between it and the next lot, two fences side by side, a front
        /// not on its street's frontage. Each side lying along a street's frontage line or a neighbouring lot's edge, near
        /// enough and parallel, is moved onto it, and the corners are where the moved sides meet.</summary>
        Vector3[] SnapOutline(RoadField roads, Vector3[] poly)
        {
            int n = poly.Length;
            if (n < 3) return poly;
            var mid = Vector3.Zero; foreach (var v in poly) mid += v; mid /= n;
            // each side as a line: a point on it and its direction
            var lp = new Vector3[n]; var ld = new Vector3[n];
            for (int e = 0; e < n; e++)
            {
                var a = poly[e]; var b = poly[(e + 1) % n];
                lp[e] = (a + b) * 0.5f; ld[e] = new Vector3(b.X - a.X, 0f, b.Z - a.Z).Normalized();
            }
            const float Reach = 2f, Parallel = 0.99f;       // within 2 m, within ~8 degrees
            // the candidate lines: every street's frontage line, and every other lot's sides
            for (int e = 0; e < n; e++)
            {
                var a = poly[e]; var b = poly[(e + 1) % n];
                float len = Flat(a, b);
                if (len < 1f) continue;
                float bestD = Reach; Vector3 bestP = lp[e], bestDir = ld[e];
                void Offer(Vector3 p0, Vector3 dir)
                {
                    if (Mathf.Abs(dir.Dot(ld[e])) < Parallel) return;
                    var nrm = new Vector3(-dir.Z, 0f, dir.X);
                    float dist = Mathf.Abs((lp[e] - p0).Dot(nrm));
                    if (dist < bestD) { bestD = dist; bestP = p0; bestDir = dir.Dot(ld[e]) > 0f ? dir : -dir; }
                }
                foreach (int r in Built)
                {
                    RoadNear(roads, r, lp[e], out float along, out _);
                    if (!roads.EvaluateAlong(r, along, out var rp, out var rt, snapTerrain: false)) continue;
                    var dir = new Vector3(rt.X, 0f, rt.Z).Normalized();
                    var right = new Vector3(dir.Z, 0f, -dir.X);
                    float front = roads.RoadHalfWidth(r) + PavementGap + _walkWidth + PlotSetback;
                    int side = (lp[e] - rp).Dot(right) > 0f ? 1 : -1;
                    Offer(rp + right * (front * side), dir);
                }
                foreach (var pl in Plots)
                {
                    var c = pl.Boundary();
                    for (int k = 0; k < c.Length; k++)
                    {
                        var p0 = c[k]; var p1 = c[(k + 1) % c.Length];
                        var dir = new Vector3(p1.X - p0.X, 0f, p1.Z - p0.Z);
                        float dl = dir.Length(); if (dl < 0.5f) continue;
                        dir /= dl;
                        // only a side it actually runs beside
                        float t0 = (a - p0).Dot(dir), t1 = (b - p0).Dot(dir);
                        if (Mathf.Max(t0, t1) < 0.5f || Mathf.Min(t0, t1) > dl - 0.5f) continue;
                        Offer(p0, dir);
                    }
                }
                lp[e] = bestP; ld[e] = bestDir;
            }
            // the corners where consecutive sides meet; a pair too near parallel keeps its old corner
            var outl = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                int e0 = (k + n - 1) % n, e1 = k;                 // corner k joins side k-1 to side k
                float den = ld[e0].X * ld[e1].Z - ld[e0].Z * ld[e1].X;
                if (Mathf.Abs(den) < 0.05f) { outl[k] = poly[k]; continue; }
                var w = lp[e1] - lp[e0];
                float t = (w.X * ld[e1].Z - w.Z * ld[e1].X) / den;
                var q = lp[e0] + ld[e0] * t;
                // a snap that throws a corner far off (a side jumped to the wrong line) is not taken
                outl[k] = Flat(q, poly[k]) < 4f ? new Vector3(q.X, 0f, q.Z) : poly[k];
            }
            return outl;
        }

        static float PolyArea(Vector3[] c)
        {
            float twice = 0f;
            for (int k = 0; k < c.Length; k++) { var a = c[k]; var b = c[(k + 1) % c.Length]; twice += a.X * b.Z - b.X * a.Z; }
            return Mathf.Abs(twice) * 0.5f;
        }
        static bool IsConvex(Vector3[] c)
        {
            bool neg = false, pos = false;
            for (int k = 0; k < c.Length; k++)
            {
                var a = c[k]; var b = c[(k + 1) % c.Length]; var d = c[(k + 2) % c.Length];
                float x = (b.X - a.X) * (d.Z - b.Z) - (b.Z - a.Z) * (d.X - b.X);
                if (x < -1e-4f) neg = true; else if (x > 1e-4f) pos = true;
            }
            return !(neg && pos);
        }
        /// <summary>A wedge's outline taken out to its TRUE CORNERS. Its LINES are the lines that really bound it: each side on
        /// one, each standing just short of one (moved out onto it), and each long side on nothing with no ground a lot could
        /// have outside it (the edge of a curved pavement's back, a lane); the rest of its sides are the raster's. Each two
        /// lines that follow one another meet in a corner, and the outline goes out to it -- cut back, where it lands on
        /// ground `free` says no to, just as far as that ground comes, the cut following its edge. Corner by corner: one that
        /// cannot be had cleanly (its lines meet far off, or every pull-back still leaves an edge across ground it cannot
        /// have) keeps the outline it had there, and costs the others nothing. Null if no corner could be filled.</summary>
        static string _fillWhy = "";
        static Vector3[] FillToCorners(Vector3[] poly, Func<Vector3, bool> free, LineOfSide bounding)
        {
            _fillWhy = "";
            if (poly == null || poly.Length < 3) { _fillWhy = "no outline"; return null; }
            // counter-clockwise in plan (x, z), so a side's inside is on its left
            float twice = 0f;
            for (int k = 0; k < poly.Length; k++) { var a = poly[k]; var b = poly[(k + 1) % poly.Length]; twice += a.X * b.Z - b.X * a.Z; }
            var pg = new List<Vector3>(poly);
            if (twice < 0f) pg.Reverse();
            int n = pg.Count;
            for (int k = 0; k < n; k++) pg[k] = new Vector3(pg[k].X, 0f, pg[k].Z);
            var lines = new List<(Vector3 p, Vector3 d, int side, int kind)>();
            int onLine = 0, moved = 0, gone = 0;
            for (int k = 0; k < n; k++)
            {
                var a = pg[k]; var b = pg[(k + 1) % n];
                float len = Flat(a, b);
                if (len < 0.05f) continue;
                var d0 = new Vector3(b.X - a.X, 0f, b.Z - a.Z) / len;
                int kind = bounding(a, b, out var lp, out var ld);
                if (kind == 1) { lines.Add((a, d0, k, 1)); onLine++; continue; }
                if (kind == 2) { lines.Add((lp, ld, k, 2)); onLine++; moved++; continue; }
                if (len >= 3f)
                {
                    var outN = new Vector3(d0.Z, 0f, -d0.X);                 // (counter-clockwise: the outside is on the right)
                    bool open = false;
                    int ns = Mathf.Max(2, Mathf.CeilToInt(len));
                    for (int u = 1; u < ns && !open; u++) open = free(a + d0 * (len * u / ns) + outN * 0.6f);
                    if (!open) { lines.Add((a, d0, k, 0)); continue; }
                }
                gone++;
            }
            if (onLine < 2 || lines.Count < 3) { _fillWhy = $"only {onLine} of its sides by a line that bounds it"; return null; }
            if (gone == 0 && moved == 0) { _fillWhy = "no raster side: its corners are where its lines meet"; return null; }
            // on or inside the outline it had (to 10 cm): ground already its own, which the edge tests need not ask about again
            bool Within(Vector3 q)
            {
                for (int k = 0; k < n; k++)
                {
                    var a = pg[k]; var b = pg[(k + 1) % n];
                    float l = Flat(a, b); if (l < 1e-4f) continue;
                    if (((b.X - a.X) * (q.Z - a.Z) - (b.Z - a.Z) * (q.X - a.X)) / l < -0.1f) return false;
                }
                return true;
            }
            List<Vector2> HullWith(IEnumerable<Vector3> extra)
            {
                var pts = new List<Vector2>(); foreach (var v in pg) pts.Add(new Vector2(v.X, v.Z)); foreach (var v in extra) pts.Add(new Vector2(v.X, v.Z));
                return Hull2(pts);
            }
            // every edge of hull(outline + extra), where it leaves the outline, on ground a lot can have
            bool Clean(IEnumerable<Vector3> extra, out Vector3 badAt)
            {
                badAt = Vector3.Zero;
                var h = HullWith(extra);
                for (int k = 0; k < h.Count; k++)
                {
                    var a = new Vector3(h[k].X, 0f, h[k].Y); var b = new Vector3(h[(k + 1) % h.Count].X, 0f, h[(k + 1) % h.Count].Y);
                    int ns = Mathf.Max(1, Mathf.CeilToInt(Flat(a, b) / 0.2f));
                    for (int u = 0; u <= ns; u++) { var q = a.Lerp(b, (float)u / ns); if (!Within(q) && !free(q)) { badAt = q; return false; } }
                }
                return true;
            }
            // one corner, where line I meets line J
            string why = "";
            List<Vector3> Corner((Vector3 p, Vector3 d, int side, int kind) I, (Vector3 p, Vector3 d, int side, int kind) J)
            {
                float den = I.d.X * J.d.Z - I.d.Z * J.d.X;
                if (Mathf.Abs(den) < 0.05f) { why = "its lines there run near parallel"; return null; }
                var w = J.p - I.p;
                var c = I.p + I.d * ((w.X * J.d.Z - w.Z * J.d.X) / den); c.Y = 0f;
                // (the outline it stands for: from where side I ends to where side J begins)
                float near = float.MaxValue;
                for (int k = (I.side + 1) % n; ; k = (k + 1) % n) { near = Mathf.Min(near, Flat(c, pg[k])); if (k == J.side) break; }
                if (near > 12f) { why = $"its lines meet {near:0} m off"; return null; }
                for (float back = 0f; back <= 6f; back += 0.4f)
                {
                    var got = new List<Vector3>();
                    if (back <= 0f && free(c)) got.Add(c);
                    else
                    {
                        // each of its two lines walked in from the corner to the first spot a lot can have -- each on its own, so a
                        // corner pavement bulging across one does not cost the other
                        Vector3? A = null, B = null;
                        for (float s0 = back; s0 < 12f; s0 += 0.2f) { var q = c - I.d * s0; if (free(q)) { A = q; break; } }
                        for (float s0 = back; s0 < 12f; s0 += 0.2f) { var q = c + J.d * s0; if (free(q)) { B = q; break; } }
                        if (A != null) got.Add(A.Value);
                        if (B != null) got.Add(B.Value);
                        // ...and between those two, out toward the corner as far as the ground goes: the cut follows a curved
                        // pavement's back instead of squaring it off
                        if (A != null && B != null)
                            for (int f = 1; f < 4; f++)
                            {
                                var s1 = A.Value.Lerp(B.Value, f / 4f);
                                if (!free(s1)) continue;
                                var toC = new Vector3(c.X - s1.X, 0f, c.Z - s1.Z); float lc = toC.Length();
                                if (lc < 0.3f) continue;
                                toC /= lc;
                                float reach = 0f;
                                for (float s2 = 0.2f; s2 < lc - back; s2 += 0.2f) { if (!free(s1 + toC * s2)) break; reach = s2; }
                                if (reach > 0f) got.Add(s1 + toC * reach);
                            }
                    }
                    if (got.Count == 0) { why = "no ground a lot can have toward its corner"; return null; }
                    if (Clean(got, out var bad)) return got;
                    // (round ground bulging INTO the corner -- a lane's end -- is crossed by the chords between the spots that
                    // follow its edge: the two walked in along its lines alone, before giving any of that up)
                    if (got.Count > 2 && Clean(got.GetRange(0, 2), out _)) return got.GetRange(0, 2);
                    why = $"an edge crosses ground it cannot have at ({bad.X:0.0},{bad.Z:0.0})";
                }
                return null;
            }
            var perCorner = new List<List<Vector3>>();
            int kept = 0;
            for (int q = 0; q < lines.Count; q++)
            {
                var I = lines[q]; var J = lines[(q + 1) % lines.Count];
                if ((I.side + 1) % n == J.side && I.kind != 2 && J.kind != 2) continue;     // its own corner: its two sides meet there
                var got = Corner(I, J);
                if (got != null) perCorner.Add(got);
                else { kept++; _fillWhy += (_fillWhy.Length > 0 ? "; " : "") + why; }
            }
            // ...and all of them at once: where two together still cross ground it cannot have, the one nearest gives way
            while (perCorner.Count > 0)
            {
                var all = new List<Vector3>(); foreach (var g in perCorner) all.AddRange(g);
                if (Clean(all, out var badAll)) break;
                int worst = 0; float wd = float.MaxValue;
                for (int g = 0; g < perCorner.Count; g++) foreach (var v in perCorner[g]) { float d2 = Flat(v, badAll); if (d2 < wd) { wd = d2; worst = g; } }
                perCorner.RemoveAt(worst); kept++;
                _fillWhy += (_fillWhy.Length > 0 ? "; " : "") + $"two corners together cross ground it cannot have at ({badAll.X:0.0},{badAll.Z:0.0})";
            }
            if (perCorner.Count == 0) { if (_fillWhy.Length == 0) _fillWhy = "no corner to fill"; return null; }
            var extraAll = new List<Vector3>(); foreach (var g in perCorner) extraAll.AddRange(g);
            var hullAll = SimplifyHull(HullWith(extraAll), 1f);
            var outl = new Vector3[hullAll.Count];
            for (int k = 0; k < hullAll.Count; k++) outl[k] = new Vector3(hullAll[k].X, 0f, hullAll[k].Y);
            float before = PolyArea(poly), after = PolyArea(outl);
            if (after > before * 2.5f) { _fillWhy = $"area {before:0} -> {after:0}"; return null; }
            if (kept > 0) _fillWhy = $"{kept} corner(s) left -- " + _fillWhy;
            return outl;
        }

        /// <summary>A convex polygon brought in by `d` on every side: clipped to each side's line moved in by `d`. Null if
        /// nothing is left.</summary>
        static Vector3[] InsetPoly(Vector3[] poly, float d)
        {
            if (poly == null || poly.Length < 3) return null;
            float twice = 0f;
            for (int k = 0; k < poly.Length; k++) { var a = poly[k]; var b = poly[(k + 1) % poly.Length]; twice += a.X * b.Z - b.X * a.Z; }
            var pg = new List<Vector3>(poly);
            if (twice < 0f) pg.Reverse();                         // counter-clockwise: each side's inside on its left
            var P = new List<Vector3>(pg);
            for (int e = 0; e < pg.Count && P.Count >= 3; e++)
            {
                var a = pg[e]; var b = pg[(e + 1) % pg.Count];
                var dir = new Vector3(b.X - a.X, 0f, b.Z - a.Z); float len = dir.Length();
                if (len < 1e-4f) continue;
                dir /= len;
                var nIn = new Vector3(-dir.Z, 0f, dir.X);
                var p0 = a + nIn * d;
                var outp = new List<Vector3>();
                for (int k = 0; k < P.Count; k++)
                {
                    var p = P[k]; var q = P[(k + 1) % P.Count];
                    float dp = (p - p0).Dot(nIn), dq = (q - p0).Dot(nIn);
                    if (dp >= 0f) outp.Add(p);
                    if (dp >= 0f != dq >= 0f) outp.Add(p + (q - p) * (dp / (dp - dq)));
                }
                P = outp;
            }
            return P.Count >= 3 ? P.ToArray() : null;
        }

        /// <summary>A convex polygon cut back to the outside of one of a convex lot's sides: the side that faces it most
        /// directly (its outward normal pointing at the polygon's middle). Sutherland-Hodgman against that one line.</summary>
        static Vector3[] ClipAway(Vector3[] poly, Vector3[] lot)
        {
            var pm = Vector3.Zero; foreach (var v in poly) pm += v; pm /= poly.Length;
            var lm = Vector3.Zero; foreach (var v in lot) lm += v; lm /= lot.Length;
            Vector3 bestA = Vector3.Zero, bestN = Vector3.Zero; float bestDot = float.MinValue;
            for (int e = 0; e < lot.Length; e++)
            {
                var a = lot[e]; var b = lot[(e + 1) % lot.Length];
                var n = new Vector3(-(b.Z - a.Z), 0f, b.X - a.X).Normalized();
                if (n.Dot((a + b) * 0.5f - lm) < 0f) n = -n;                       // outward
                float d = n.Dot((pm - (a + b) * 0.5f).Normalized());
                if (d > bestDot) { bestDot = d; bestA = a; bestN = n; }
            }
            var outp = new List<Vector3>();
            for (int k = 0; k < poly.Length; k++)
            {
                var p = poly[k]; var q = poly[(k + 1) % poly.Length];
                float dp = (p - bestA).Dot(bestN), dq = (q - bestA).Dot(bestN);
                if (dp >= 0f) outp.Add(p);
                if (dp >= 0f != dq >= 0f) outp.Add(p + (q - p) * (dp / (dp - dq)));
            }
            return outp.ToArray();
        }

        /// <summary>Is a plan point inside a convex polygon (either winding)?</summary>
        public static bool InsidePoly(Vector3[] c, Vector3 q)
        {
            bool neg = false, pos = false;
            for (int e = 0; e < c.Length; e++)
            {
                var a = c[e]; var b = c[(e + 1) % c.Length];
                float x = (b.X - a.X) * (q.Z - a.Z) - (b.Z - a.Z) * (q.X - a.X);
                if (x < 0f) neg = true; else if (x > 0f) pos = true;
                if (neg && pos) return false;
            }
            return true;
        }
        /// <summary>The convex hull of points in plan (x, z as X, Y), counter-clockwise.</summary>
        static List<Vector2> Hull2(List<Vector2> pts)
        {
            var p = new List<Vector2>(new HashSet<Vector2>(pts));
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            if (p.Count < 3) return p;
            static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new List<Vector2>();
            foreach (var q in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0f) h.RemoveAt(h.Count - 1); h.Add(q); }
            int lower = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--) { var q = p[i]; while (h.Count >= lower && Cross(h[^2], h[^1], q) <= 0f) h.RemoveAt(h.Count - 1); h.Add(q); }
            h.RemoveAt(h.Count - 1);
            return h;
        }
        /// <summary>A hull with its near-straight corners taken out: a wedge's hull off a raster is a triangle with a
        /// staircase of tiny turns along its slanted side.</summary>
        static List<Vector2> SimplifyHull(List<Vector2> h, float minTurnDeg)
        {
            var list = new List<Vector2>(h);
            bool changed = true;
            while (changed && list.Count > 3)
            {
                changed = false;
                for (int i = 0; i < list.Count && list.Count > 3; i++)
                {
                    var a = list[(i + list.Count - 1) % list.Count]; var b = list[i]; var c = list[(i + 1) % list.Count];
                    var u = (b - a).Normalized(); var v = (c - b).Normalized();
                    float turn = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(u.Dot(v), -1f, 1f)));
                    if (turn < minTurnDeg) { list.RemoveAt(i); changed = true; i--; }
                }
            }
            return list;
        }

        // ==============================================================================================
        // YARD FENCES (strawberry 2026-10-14: "designating where yard fences should be (not placing anything just yet)").
        // ==============================================================================================

        /// <summary>A run of yard fence, on a lot line: where one goes, not a fence. `Plot` is the lot it was laid for (the
        /// first, where two share it).</summary>
        public sealed class YardFence { public Vector3 A, B; public Plot Plot; public string Edge; }
        /// <summary>The yard fences the last PlanPlots designated.</summary>
        public readonly List<YardFence> Fences = new();

        /// <summary>⭐ WHERE THE YARD FENCES GO: round each lot's backyard and side yards, never across its front. A house's
        /// back and both sides; a corner lot's back and its inner side (its other side is its second street, a front);
        /// a shop's back only (shops stand shoulder to shoulder on the pavement); an apartment block's back and sides.
        /// ⚠ A LINE TWO LOTS SHARE IS ONE FENCE. Neighbours share their side lines, and lots that grew to meet across a
        /// strip share a back line: each line is merged with every run on it, so a fence is never laid twice.</summary>
        void PlanFences(RoadField roads)
        {
            Fences.Clear();
            var segs = new List<(Vector3 a, Vector3 b, Plot pl, string edge)>();
            foreach (var pl in Plots)
            {
                if (pl.Outline != null)
                {
                    // a triangle lot: fenced where it meets other ground, open where it meets a street -- each edge asks
                    // a metre outside its middle
                    var ol = pl.Outline; var mid = Vector3.Zero; foreach (var v in ol) mid += v; mid /= ol.Length;
                    for (int e = 0; e < ol.Length; e++)
                    {
                        var a = ol[e]; var b = ol[(e + 1) % ol.Length];
                        var m = (a + b) * 0.5f; var n = new Vector3(-(b.Z - a.Z), 0f, b.X - a.X).Normalized();
                        if (n.Dot(m - mid) < 0f) n = -n;
                        if (roads != null && Buildable(roads, m + n * 1f)) segs.Add((a, b, pl, "side"));
                    }
                    continue;
                }
                if (pl.Kind == PlotKind.Parking) continue;                 // (a car park is open ground)
                var c = pl.Corners();                   // front-left, front-right, back-right, back-left
                segs.Add((c[2], c[3], pl, "back"));
                if (pl.Kind == PlotKind.Shop) continue;
                segs.Add((c[1], c[2], pl, "side"));
                if (pl.Kind != PlotKind.Corner) segs.Add((c[3], c[0], pl, "side"));
            }
            // group the runs that lie on one line -- pairwise, within 3 cm and half a degree: keys rounded to a grid split one
            // line in two wherever it happened to fall on a rounding edge, and a fence was laid twice -- then merge each line
            var segN = segs.Count;
            var parent = new int[segN];
            for (int k = 0; k < segN; k++) parent[k] = k;
            int Find(int k) { while (parent[k] != k) { parent[k] = parent[parent[k]]; k = parent[k]; } return k; }
            var dirs = new Vector3[segN];
            for (int k = 0; k < segN; k++)
            {
                var d = new Vector3(segs[k].b.X - segs[k].a.X, 0f, segs[k].b.Z - segs[k].a.Z);
                dirs[k] = d.LengthSquared() > 1e-8f ? d.Normalized() : Vector3.Zero;
            }
            float cosTol = Mathf.Cos(Mathf.DegToRad(0.5f));
            for (int x = 0; x < segN; x++)
                for (int y = x + 1; y < segN; y++)
                {
                    if (dirs[x] == Vector3.Zero || dirs[y] == Vector3.Zero || Mathf.Abs(dirs[x].Dot(dirs[y])) < cosTol) continue;
                    var nx = new Vector3(-dirs[x].Z, 0f, dirs[x].X);
                    if (Mathf.Abs((segs[y].a - segs[x].a).Dot(nx)) > 0.03f || Mathf.Abs((segs[y].b - segs[x].a).Dot(nx)) > 0.03f) continue;
                    parent[Find(y)] = Find(x);
                }
            var groups = new Dictionary<int, List<int>>();
            for (int k = 0; k < segN; k++) { if (dirs[k] == Vector3.Zero) continue; int root = Find(k); if (!groups.TryGetValue(root, out var g)) groups[root] = g = new List<int>(); g.Add(k); }
            foreach (var g in groups.Values)
            {
                var d = dirs[g[0]]; var org = segs[g[0]].a; org.Y = 0f;
                var runs = new List<(float t0, float t1, int k)>();
                foreach (int k in g)
                {
                    float ta = (segs[k].a - org).Dot(d), tb = (segs[k].b - org).Dot(d);
                    runs.Add((Mathf.Min(ta, tb), Mathf.Max(ta, tb), k));
                }
                runs.Sort((x, y) => x.t0.CompareTo(y.t0));
                var cur = runs[0];
                void Emit((float t0, float t1, int k) r)
                {
                    if (r.t1 - r.t0 < 0.05f) return;
                    Fences.Add(new YardFence { A = org + d * r.t0, B = org + d * r.t1, Plot = segs[r.k].pl, Edge = segs[r.k].edge });
                }
                for (int k = 1; k < runs.Count; k++)
                {
                    if (runs[k].t0 <= cur.t1 + 0.05f) { if (runs[k].t1 > cur.t1) cur.t1 = runs[k].t1; continue; }
                    Emit(cur); cur = runs[k];
                }
                Emit(cur);
            }
            // the Y of each end: the ground the lot was laid on (marks drape it to the terrain where there is one)
            foreach (var f in Fences) { f.A.Y = f.Plot.Front.Y; f.B.Y = f.Plot.Front.Y; }
            DropDoubleFences();
            float total = 0f; foreach (var f in Fences) total += Flat(f.A, f.B);
            Log.Print($"[towngrid] yard fences: {Fences.Count} runs, {total:0} m, round {Plots.Count} lots");
        }

        /// <summary>A back lane's cross-section: half its carriageway, each pavement's width and kerb, and the radius its
        /// kerb turns through into the street's.
        /// ⚠ ONE LANE, SIZED TO THIS GAME'S CARS. They are 3 m across their tyres (see BayDepth), so a 4 m single track
        /// leaves half a metre a side -- tight, as a back lane is. With a 1 m pavement each side the whole lane is exactly
        /// the 6 m strip the alleys were always planned as (AlleyWidth is derived from these), so not one plot moves.
        /// ⚠ THE KERB RETURN IS THE PAVEMENT'S OWN 1 m. The opening it cuts in the street's pavement is then the lane's
        /// own 6 m and no wider, so the plots either side, and any parking bay in front of them, start at its edge; a
        /// junction's 6 m return here would have run into both.
        /// ⚠⚠ THE KERB IS THE STREETS', NOT A SHORT ONE (strawberry 2026-10-13: "kerb height should stay the same as the
        /// other roads too"). "Short sidewalks/kerbs" was the 1 m pavement; read as a low kerb it went in at 0.2 m from
        /// the kerb's foot -- 2 cm UNDER the lane's asphalt, which stands 0.22 m over that foot ("make sure the alleys and
        /// their kerbs arent sinking below the road level") -- then at 0.1 m over the asphalt, still not the streets'.
        /// A lane's kerb now stands exactly as far over its asphalt as a street's does over its own: KerbProud.
        /// LaneRamp: where a kerb return's height and the lane's own kerb foot differ, the difference fades over this.</summary>
        public float LaneHalf = 2f, LaneWalkWidth = 1f, LaneKerbRadius = 1f, LaneRamp = 2.5f;
        /// <summary>How much further up the lane a mouth's kerb return runs past its back corner, to twist from the street's
        /// level-across to the lane's (OpenMouth).</summary>
        public float LaneTwist = 1f;
        /// <summary>How far the town's kerbs stand over their own asphalt -- a street's, and so a lane's: PavementKerb is
        /// counted from the kerb's foot, and the carriageway's surface stands its material's surface offset over that.</summary>
        public float KerbProud(RoadField roads) => PavementKerb - roads.RoadSurfaceOffsetOfMaterial(_plainStreet >= 0 ? _plainStreet : StreetMaterial);
        /// <summary>The centreline radius an L-shaped lane turns its corner on, inside its block, when it curves.</summary>
        public float LaneKneeRadius = 8f;
        /// <summary>⭐ HARD 90s FOR THE ALLEYS (strawberry 2026-10-13: "make hard 90s with both the road and the sidewalks
        /// possible ... for alleys ... as an option you can toggle on/off"). On: an L-shaped lane is two straight lanes
        /// meeting at its knee, the asphalt mitred corner to corner (RoadField.CornerMitre) and each pavement ending square
        /// on the line its kerbs and back edges cross at (EditorSidewalk's mitre). Off -- UG_TOWNLANECURVES=1 -- it turns
        /// on LaneKneeRadius as before.</summary>
        public bool LaneHardCorners = System.Environment.GetEnvironmentVariable("UG_TOWNLANECURVES") != "1";
        /// <summary>The lanes' material: the street's, plain tarmac -- no markings at all -- and LaneHalf wide. -1: none.</summary>
        public int LaneMaterial = -1;

        /// <summary>Where a lane opens off a street.</summary>
        public sealed class LaneMouth
        {
            /// <summary>The street, and the side of it the lane opens off (+1 = the spline's (t.z, 0, -t.x) side, the way
            /// the pavements count sides).</summary>
            public int Road, Side;
            /// <summary>The stretch of that side's pavement the opening takes, in the street's arc metres.</summary>
            public float From, To;
            /// <summary>Where the lane's centreline meets the street's asphalt edge, and the way up the lane from there.</summary>
            public Vector3 Edge, Into;
            /// <summary>The kerb round each corner, in plan -- [0] on the left of Into, [1] on its right -- with each point's
            /// outward direction and pavement width; and how far up the lane from Edge that side's own pavement starts.</summary>
            public readonly List<Vector3>[] Kerb = new List<Vector3>[2], Out = new List<Vector3>[2];
            public readonly List<float>[] Width = new List<float>[2];
            public readonly float[] LaneFrom = new float[2];
            /// <summary>Where each corner's kerb leaves the street's, in the street's arc metres.</summary>
            public readonly float[] StreetArc = new float[2];
            /// <summary>Where the lane's centreline meets the street's edge, in the street's arc metres.</summary>
            public float EdgeArc;
        }
        /// <summary>A lane to build: its centreline end to end, and the mouth at each end -- null where it could not open
        /// onto a straight run of a street's pavement and stops short (LaneDeadEnds). The road, once built.</summary>
        public sealed class LanePlan
        {
            public List<Vector3> Pts; public LaneMouth A, B; public int Road = -1;
            /// <summary>End B opens into a parking lot's asphalt (TownParking): neither a mouth nor closed -- its pavements run
            /// to the end and meet the lot's own.</summary>
            public bool OpenB;
            /// <summary>The plane an open end B's ring lies in (a surface point, its rise per metre in plan): a parking lot's
            /// surface where a way comes through its side -- level across, the end met the lot in a step either side.</summary>
            public (Vector3 origin, Vector3 grad)? EndPlane;
            /// <summary>Its own carriageway, where it is not a back lane's single track (a parking lot's two-lane way in):
            /// the material it is built from, and its half-width. -1 / 0: LaneMaterial, LaneHalf.</summary>
            public int Material = -1; public float Half;
            /// <summary>An end that meets the other half of an L at a hard corner (LaneHardCorners): neither a mouth nor
            /// closed. Partner is that other half, which meets it with its own opposite end.</summary>
            public bool KneeA, KneeB; public LanePlan Partner;
        }
        /// <summary>The kerbed ends BuildLanes laid across the lanes that stop short.</summary>
        public int LaneEndsClosed;
        public readonly List<LanePlan> LanePlans = new();
        public readonly List<LaneMouth> LaneMouths = new();
        /// <summary>The roads BuildLanes laid, one per lane. ⚠ NOT IN Built: everything that walks Built -- the lines, the
        /// street pavements, the lamps, the plots' frontages -- is about the streets.</summary>
        public readonly List<int> LaneRoads = new();
        public static bool NoLaneBank = System.Environment.GetEnvironmentVariable("UG_TOWNNOLANEBANK") == "1";
        /// <summary>The widest a lane-mouth return's kerb and its lane pavement's kerb miss each other where they meet, in plan.</summary>
        public float ReturnJog; public Vector3 ReturnJogAt;
        /// <summary>UG_TOWNJDUMP=<j>: print junction j's arms and kerb ring as LaySidewalkCorners sees them.</summary>
        public static int DumpJunction = int.TryParse(System.Environment.GetEnvironmentVariable("UG_TOWNJDUMP"), out int dj) ? dj : -1;
        /// <summary>The worst the last BuildLanes found a lane's level end standing over (+) or under (-) the street's edge
        /// at either corner of a mouth.</summary>
        public float MouthLip;
        public int LaneDeadEnds;
        /// <summary>Why each dead end could not open: no street beyond it, met one glancing, no corner to round, its
        /// opening off the pavement the street lays (into a junction's corner), or onto another lane's -- and where the
        /// first of them is, for a camera.</summary>
        public int DeadNoStreet, DeadGlancing, DeadNoCorner, DeadOffPavement, DeadOnMouth;
        /// <summary>Mouths refused for opening within their clearOfCorner of a junction's corner (a parking lot's ways in).</summary>
        public int MouthsNearCorner;
        public Vector3 FirstDeadEnd;
        Func<float, float, float> _heightAt;

        /// <summary>The mouth on this side of this road that takes any of arc [a, b] of its pavement, or null.</summary>
        public LaneMouth MouthAt(int road, int side, float a, float b)
        {
            foreach (var m in LaneMouths) if (m.Road == road && m.Side == side && m.From < b && a < m.To) return m;
            return null;
        }

        /// <summary>Lay out a lane down every alley PlanPlots marked: its centreline -- an L's two legs joined round one
        /// curve -- run out at each end to the street's asphalt, and the mouth it opens there. Builds nothing.</summary>
        void PlanLanes(RoadField roads)
        {
            LanePlans.Clear(); LaneMouths.Clear(); LaneDeadEnds = 0; OrphanAlleys = 0;
            DeadNoStreet = DeadGlancing = DeadNoCorner = DeadOffPavement = DeadOnMouth = MouthsNearCorner = 0; FirstDeadEnd = Vector3.Zero;
            if (!BuildBackLanes || roads == null || Alleys.Count == 0) return;
            float Y(Vector3 q) => _heightAt != null ? _heightAt(q.X, q.Z) : q.Y;
            var taken = new bool[Alleys.Count];
            var orphans = new List<Alley>();
            for (int i = 0; i < Alleys.Count; i++)
            {
                if (taken[i]) continue;
                taken[i] = true;
                var al = Alleys[i];
                // an L's two legs both start at its knee: walk one in to it and the other out
                int partner = -1;
                if (al.FromKnee)
                    for (int k = i + 1; k < Alleys.Count; k++)
                        if (!taken[k] && Alleys[k].FromKnee && Flat(Alleys[k].A, al.A) < 0.1f) { partner = k; break; }
                Vector3 endA, endB, kneeAt = Vector3.Zero; bool streetA, knee = partner >= 0;
                if (knee) { taken[partner] = true; endA = al.B; kneeAt = al.A; endB = Alleys[partner].B; streetA = true; }
                else { endA = al.A; endB = al.B; streetA = !al.FromKnee && !al.Stub; }
                Vector3 OutAt(Vector3 end, Vector3 from) { var d = end - from; d.Y = 0f; return d.Normalized(); }
                var outA = OutAt(endA, knee ? kneeAt : endB);
                var outB = OutAt(endB, knee ? kneeAt : endA);
                var mA = streetA ? OpenMouth(roads, endA, outA) : null;
                var mB = OpenMouth(roads, endB, outB);
                // ⭐ AN ALLEY NEITHER END OF WHICH OPENS IS NO ALLEY (strawberry 2026-10-15, of a lane piece kerbed shut at both
                // ends between a triangle lot and a fenced one: "i see an orphaned alley... fix dat"). Nobody can drive into it,
                // so it is not laid at all, and its strip goes back to the lots round it -- this runs before GrowPlots.
                if (mA == null && mB == null)
                {
                    orphans.Add(al); if (knee) orphans.Add(Alleys[partner]);
                    if (OrphanAlleys++ == 0) FirstOrphanAlley = (endA + endB) * 0.5f;
                    continue;
                }
                if (streetA && mA == null && LaneDeadEnds++ == 0) FirstDeadEnd = endA;
                if (mB == null && LaneDeadEnds++ == 0) FirstDeadEnd = endB;
                // ⚠ AN END THAT CANNOT OPEN IS CLOSED: pulled back far enough for the kerb BuildLanes lays across it to stand
                // clear -- left as it was, the road just stopped, its end ramping down into the grass
                var a = mA != null ? mA.Edge : endA - outA * CloseBack(roads, endA, outA);
                var b = mB != null ? mB.Edge : endB - outB * CloseBack(roads, endB, outB);
                // a hard 90: the two straights, the knee a corner of the line (split below); otherwise one curve round it
                bool hard = knee && LaneHardCorners;
                int kneeIx = -1;
                List<Vector3> line;
                if (hard)
                {
                    var l1 = RoundedPath(a, a, kneeAt, 0f, 3f); var l2 = RoundedPath(kneeAt, kneeAt, b, 0f, 3f);
                    line = l1 == null || l2 == null ? null : new List<Vector3>(l1);
                    if (line != null) { kneeIx = line.Count - 1; line.AddRange(l2.GetRange(1, l2.Count - 1)); }
                }
                else line = knee ? RoundedPath(a, kneeAt, b, LaneKneeRadius, 2f) : RoundedPath(a, a, b, 0f, 3f);
                if (line == null || line.Count < 2) continue;
                // ⚠⚠ THE STREET'S HEIGHT, NOT THE GROUND'S (strawberry 2026-10-13, twice: "make sure the alleys and their
                // kerbs arent sinking below the road level", then "they are still sinking below street road level??"). The
                // town's streets hold their own graded profile and the ground was cut 0.12 m BELOW it (GradeTerrain, before
                // this runs) -- so a lane laid on that ground came off its street 12 cm low, its asphalt a step down at the
                // mouth and its kerbs below the street's asphalt. So each end stands at the height of the street it opens
                // onto, and the lane is graded straight between them, as a street span is between its nodes. A closed end
                // takes the nearest street's height; a draped town's lanes follow the ground as its streets do.
                // ⚠⚠ AND THE GROUND BETWEEN THEM (strawberry 2026-10-13: "add support for the grid to follow gentle hills and
                // slopes"). Graded straight from one street to the other, a lane on a hill ran under the crest between them --
                // out of sight. So it is profiled as a street span is: the ground along it, smoothed, and pinned to its streets
                // at both ends, the difference taken out evenly along it. (The plots and lanes are planned BEFORE the town
                // grades its ground -- Main -- so this is the ground the streets were profiled off; GradeTerrain then cuts it
                // under the lanes too.)
                if (!FollowTerrain)
                {
                    float ya = mA != null ? mA.Edge.Y : StreetHeightNear(roads, line[0]);
                    float yb = mB != null ? mB.Edge.Y : StreetHeightNear(roads, line[^1]);
                    var g = new float[line.Count];
                    for (int q = 0; q < line.Count; q++) g[q] = Y(line[q]);
                    for (int pass = 0; pass < Grade + 1; pass++)
                    {
                        var src = (float[])g.Clone();
                        for (int q = 1; q + 1 < g.Length; q++) g[q] = (src[q - 1] + src[q] * 2f + src[q + 1]) * 0.25f;
                    }
                    float total = 0f; for (int q = 0; q + 1 < line.Count; q++) total += Flat(line[q], line[q + 1]);
                    float run = 0f;
                    for (int q = 0; q < line.Count; q++)
                    {
                        if (q > 0) run += Flat(line[q - 1], line[q]);
                        float f = total > 1e-3f ? run / total : 0f;
                        line[q] = new Vector3(line[q].X, g[q] + Mathf.Lerp(ya - g[0], yb - g[^1], f), line[q].Z);
                    }
                }
                else for (int q = 0; q < line.Count; q++) line[q] = new Vector3(line[q].X, Y(line[q]), line[q].Z);
                if (mA != null) LaneMouths.Add(mA);
                if (mB != null) LaneMouths.Add(mB);
                if (hard)
                {
                    // the two halves, each straight, sharing the knee's point -- and its height, the profile having been run
                    // over the whole L first
                    var h1 = new LanePlan { Pts = line.GetRange(0, kneeIx + 1), A = mA, KneeB = true };
                    var h2 = new LanePlan { Pts = line.GetRange(kneeIx, line.Count - kneeIx), B = mB, KneeA = true };
                    h1.Partner = h2; h2.Partner = h1;
                    LanePlans.Add(h1); LanePlans.Add(h2);
                }
                else LanePlans.Add(new LanePlan { Pts = line, A = mA, B = mB });
            }
            if (orphans.Count > 0)
            {
                foreach (var al in orphans) Alleys.Remove(al);
                Log.Print($"[towngrid] orphaned alleys: {OrphanAlleys} could open onto no street at either end -- not laid, their ground "
                        + $"given back to the lots (the first at {FirstOrphanAlley.X:0},{FirstOrphanAlley.Z:0})");
            }
        }
        /// <summary>What the last PlanLanes dropped: alleys neither end of which could open onto a street.</summary>
        public int OrphanAlleys; public Vector3 FirstOrphanAlley;

        /// <summary>The height of the nearest of our streets' own profiles to a point: where a closed lane end stands.</summary>
        /// <summary>Each road's centreline, a sample a metre, for "where does it pass nearest" -- asked of every kerb station
        /// and every fill vertex, which is too often to walk the spline each time. Cleared by whoever is about to ask after
        /// roads may have been rebuilt.</summary>
        readonly Dictionary<int, (Vector3[] p, float[] s)> _roadSamples = new();
        (Vector3[] p, float[] s) RoadSamples(RoadField roads, int road)
        {
            if (_roadSamples.TryGetValue(road, out var c)) return c;
            float L = roads.RoadLength(road);
            int n = Mathf.Max(1, Mathf.CeilToInt(L));
            var ps = new List<Vector3>(n + 1); var ss = new List<float>(n + 1);
            for (int k = 0; k <= n; k++)
            {
                float at = L * k / n;
                if (roads.EvaluateAlong(road, at, out var q, out _, snapTerrain: false)) { ps.Add(q); ss.Add(at); }
            }
            c = (ps.ToArray(), ss.ToArray());
            _roadSamples[road] = c;
            return c;
        }
        /// <summary>Where ONE road passes nearest <paramref name="p"/> in plan: its spline's height there, how far along it
        /// that is, and how far off. Past either end it is the end.</summary>
        float RoadNear(RoadField roads, int road, Vector3 p, out float along, out float off)
        {
            var (ps, ss) = RoadSamples(roads, road);
            along = 0f; off = float.MaxValue; float y = p.Y;
            if (ps.Length == 0) return y;
            if (ps.Length == 1) { along = ss[0]; off = Flat(ps[0], p); return ps[0].Y; }
            for (int k = 1; k < ps.Length; k++)
            {
                var a = ps[k - 1]; var b = ps[k];
                var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = ab.LengthSquared();
                float t = l2 < 1e-8f ? 0f : Mathf.Clamp(((p.X - a.X) * ab.X + (p.Z - a.Z) * ab.Y) / l2, 0f, 1f);
                float d = new Vector2(a.X + ab.X * t - p.X, a.Z + ab.Y * t - p.Z).Length();
                if (d < off) { off = d; along = Mathf.Lerp(ss[k - 1], ss[k], t); y = Mathf.Lerp(a.Y, b.Y, t); }
            }
            return y;
        }
        /// <summary>The junction slabs' TOP at (x, z) -- exactly as BuildPatchMeshes lays them, fitted shift and lift
        /// included -- or NaN off every slab.</summary>
        Func<float, float, float> SlabTops(RoadField roads)
        {
            EnsurePatchBoxes();
            var fit = FitShifts(roads, roads.RoadSurfaceOffsetOfMaterial(MainMaterial));
            return (x, z) =>
            {
                double zf = -z;
                for (int k = 0; k < Patches.Count; k++)
                {
                    var b = _patchBox[k];
                    if (x < b.x0 || x > b.x1 || zf < b.z0 || zf > b.z1) continue;
                    float hh = Patches[k].HeightAt(x, zf);
                    if (!float.IsNaN(hh)) return hh + (fit.TryGetValue(Patches[k], out float sh) ? sh : 0f) + PatchLift;
                }
                return float.NaN;
            };
        }
        /// <summary>The paving over (x, z): the LOWEST and the HIGHEST top of everything there -- the junction slabs, and
        /// every street's and lane's carriageway the point lies on -- or NaN for both where there is none. Fill has to be
        /// under the lowest; paint has to be over the highest.
        /// ⚠ A CARRIAGEWAY'S TOP IS ITS MESH, NOT ITS SPLINE. RoadField lays a ring every 5 m and the surface between two
        /// is flat, so over a crest it runs a few millimetres under the curve it was cut from -- and fill held a centimetre
        /// under the CURVE came up through the mesh on a hill as a thin dark line in the street (gone with the fill 4 cm
        /// down, which is how it was found). So the top is read off the road's own surface triangles.</summary>
        Func<Vector3, (float lo, float hi, float roadHi)> PavingTops(RoadField roads)
        {
            _roadSamples.Clear();
            var slabTop = SlabTops(roads);
            var cand = new List<(float x0, float x1, float z0, float z1, List<(Vector3 a, Vector3 b, Vector3 c)> tris)>();
            foreach (var list in new[] { Built, LaneRoads })
                foreach (int r in list)
                {
                    var mesh = roads.RoadMeshOf(r);
                    if (mesh == null) continue;
                    var tris = new List<(Vector3, Vector3, Vector3)>();
                    float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                    for (int sfc = 0; sfc < mesh.GetSurfaceCount(); sfc++)
                    {
                        var arrs = mesh.SurfaceGetArrays(sfc);
                        var vs = arrs[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var ix = arrs[(int)Mesh.ArrayType.Index].AsInt32Array();
                        bool indexed = ix != null && ix.Length >= 3;
                        int nt = indexed ? ix.Length / 3 : vs.Length / 3;
                        for (int t = 0; t < nt; t++)
                        {
                            int ia = indexed ? ix[3 * t] : 3 * t, ib = indexed ? ix[3 * t + 1] : 3 * t + 1, ic = indexed ? ix[3 * t + 2] : 3 * t + 2;
                            // ⚠ THE TOP BY WHERE ITS CORNERS ARE IN THE RING, not by which way it faces: RoadField lays four
                            // vertices a ring (bevel, surface, surface, bevel), and where a bevel meets an end ramp's bevel the
                            // quad between lies FLAT at the bevels' foot, 0.44 m down -- a "top" by its normal, which held the
                            // fill in every hole beside a road's end 44 cm under the street on flat ground
                            if (ia % 4 is 0 or 3 || ib % 4 is 0 or 3 || ic % 4 is 0 or 3) continue;
                            var a = vs[ia]; var b = vs[ib]; var c = vs[ic];
                            var n = (b - a).Cross(c - a);
                            if (n.LengthSquared() < 1e-10f || Mathf.Abs(n.Normalized().Y) < 0.95f) continue;   // (and not its end ramp)
                            tris.Add((a, b, c));
                            foreach (var q in new[] { a, b, c }) { x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X); z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z); }
                        }
                    }
                    if (tris.Count > 0) cand.Add((x0, x1, z0, z1, tris));
                }
            // ⚡ BUCKETED, NOT WALKED (a whole town's corner asphalt asks this a million and a half times, and each ask walked
            // every top triangle of every road whose box it fell in -- most of 16 s). The same triangles in a 4 m grid, each
            // cell's list in the walk's own order (road by road, triangle by triangle), so the first one a point is in, road by
            // road, is the one the walk found: the same answer.
            const float Cell = 4f;
            var grid = new Dictionary<(int, int), List<(int road, Vector3 a, Vector3 b, Vector3 c)>>();
            for (int ci = 0; ci < cand.Count; ci++)
                foreach (var (a, b, c) in cand[ci].tris)
                {
                    int gx0 = Mathf.FloorToInt(Mathf.Min(a.X, Mathf.Min(b.X, c.X)) / Cell), gx1 = Mathf.FloorToInt(Mathf.Max(a.X, Mathf.Max(b.X, c.X)) / Cell);
                    int gz0 = Mathf.FloorToInt(Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) / Cell), gz1 = Mathf.FloorToInt(Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)) / Cell);
                    for (int gx = gx0; gx <= gx1; gx++)
                        for (int gz = gz0; gz <= gz1; gz++)
                        {
                            if (!grid.TryGetValue((gx, gz), out var l)) grid[(gx, gz)] = l = new List<(int, Vector3, Vector3, Vector3)>();
                            l.Add((ci, a, b, c));
                        }
                }
            return v =>
            {
                float lo = slabTop(v.X, v.Z), hi = lo, roadHi = float.NaN;
                if (!grid.TryGetValue((Mathf.FloorToInt(v.X / Cell), Mathf.FloorToInt(v.Z / Cell)), out var inCell)) return (lo, hi, roadHi);
                int done = -1;                        // the road a point was last found on: its other triangles are passed over
                foreach (var (ci, a, b, cc) in inCell)
                {
                    if (ci == done) continue;
                    if (v.X < Mathf.Min(a.X, Mathf.Min(b.X, cc.X)) || v.X > Mathf.Max(a.X, Mathf.Max(b.X, cc.X))
                        || v.Z < Mathf.Min(a.Z, Mathf.Min(b.Z, cc.Z)) || v.Z > Mathf.Max(a.Z, Mathf.Max(b.Z, cc.Z))) continue;
                    float d = (b.Z - cc.Z) * (a.X - cc.X) + (cc.X - b.X) * (a.Z - cc.Z);
                    if (Mathf.Abs(d) < 1e-9f) continue;
                    float w0 = ((b.Z - cc.Z) * (v.X - cc.X) + (cc.X - b.X) * (v.Z - cc.Z)) / d;
                    float w1 = ((cc.Z - a.Z) * (v.X - cc.X) + (a.X - cc.X) * (v.Z - cc.Z)) / d;
                    float w2 = 1f - w0 - w1;
                    if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                    float top = w0 * a.Y + w1 * b.Y + w2 * cc.Y;
                    lo = float.IsNaN(lo) ? top : Mathf.Min(lo, top);
                    hi = float.IsNaN(hi) ? top : Mathf.Max(hi, top);
                    roadHi = float.IsNaN(roadHi) ? top : Mathf.Max(roadHi, top);
                    done = ci;
                }
                return (lo, hi, roadHi);
            };
        }

        float StreetHeightNear(RoadField roads, Vector3 p)
        {
            float best = float.MaxValue, y = _heightAt != null ? _heightAt(p.X, p.Z) : p.Y;
            foreach (int r in Built)
            {
                float L = roads.RoadLength(r);
                for (float d = 0f; d <= L; d += 2f)
                    if (roads.EvaluateAlong(r, d, out var q, out _, snapTerrain: false))
                    { float dd = Flat(q, p); if (dd < best) { best = dd; y = q.Y; } }
            }
            return y;
        }

        /// <summary>How far back from `end`, against `w`, a lane that cannot open must stop for the kerb across its end -- the
        /// lane and both its pavements wide, and the pavements' last few metres behind it -- to stand clear of every road,
        /// pavement and slab. At least a pavement's width, which is what the kerb takes.
        /// ⚠ NOT JUST ITS CENTRELINE. An alley's strip only has to be clear of the pavement along its middle near an end
        /// (PlanPlots lets its corners run under the pavement there, or no lane could meet a 45-degree street), so the
        /// first closed ends, pulled back a fixed metre, put a corner of their kerb onto the street's pavement wherever
        /// the lane met it at an angle -- the town test's overlap check found one.</summary>
        float CloseBack(RoadField roads, Vector3 end, Vector3 w)
        {
            var across = new Vector3(-w.Z, 0f, w.X);
            float reach = LaneHalf + PavementGap + LaneWalkWidth;
            for (float back = LaneWalkWidth; back <= 12f; back += 0.25f)
            {
                var e = end - w * back;                                   // where the road will stop
                bool clear = true;
                for (float t = -LaneWalkWidth - 4f; t <= 0.6f + 1e-3f && clear; t += 0.5f)
                    foreach (float s in new[] { -reach, 0f, reach })
                        if (!Buildable(roads, e + w * t + across * s)) { clear = false; break; }
                if (clear) return back;
            }
            return 12f;
        }

        /// <summary>A path from a to b by way of a corner at k, the corner rounded on radius R and every point
        /// on the true straight-arc-straight (MergeBends' lesson: respacing an arc onto chords pulls it inside), about
        /// `step` apart. k == a with R = 0 is a straight line.</summary>
        static List<Vector3> RoundedPath(Vector3 a, Vector3 k, Vector3 b, float R, float step)
        {
            var d1 = k - a; d1.Y = 0f; var d2 = b - k; d2.Y = 0f;
            float l1 = d1.Length(), l2 = d2.Length();
            var outp = new List<Vector3>();
            if (l1 < 1e-3f || R <= 0f)
            {
                var ab = b - a; ab.Y = 0f; float lab = ab.Length();
                if (lab < 1f) return null;
                int n = Mathf.Max(1, Mathf.RoundToInt(lab / step));
                for (int i = 0; i <= n; i++) outp.Add(a.Lerp(b, (float)i / n));
                return outp;
            }
            if (l2 < 1e-3f) return null;
            d1 /= l1; d2 /= l2;
            float turn = Mathf.Acos(Mathf.Clamp(d1.Dot(d2), -1f, 1f));
            float tanHalf = Mathf.Tan(turn * 0.5f);
            float r = R, T = r * tanHalf;
            if (T > Mathf.Min(l1, l2) * 0.45f) { T = Mathf.Min(l1, l2) * 0.45f; r = T / Mathf.Max(1e-4f, tanHalf); }
            var S = k - d1 * T; var E = k + d2 * T;
            var nIn = (d2 - d1 * d1.Dot(d2)).Normalized();
            var C = new Vector3(S.X, 0f, S.Z) + nIn * r;
            float a0 = Mathf.Atan2(S.Z - C.Z, S.X - C.X), a1 = Mathf.Atan2(E.Z - C.Z, E.X - C.X);
            float da = Mathf.Wrap(a1 - a0, -Mathf.Pi, Mathf.Pi);
            float lenA = l1 - T, arc = Mathf.Abs(da) * r, lenB = l2 - T, total = lenA + arc + lenB;
            int m = Mathf.Max(2, Mathf.RoundToInt(total / step));
            for (int i = 0; i <= m; i++)
            {
                float s = total * i / m;
                if (s <= lenA) outp.Add(a.Lerp(S, lenA > 1e-4f ? s / lenA : 1f));
                else if (s >= lenA + arc) outp.Add(E.Lerp(b, lenB > 1e-4f ? (s - lenA - arc) / lenB : 1f));
                else
                {
                    float f = (s - lenA) / arc, ang = a0 + da * f;
                    outp.Add(new Vector3(C.X + Mathf.Cos(ang) * r, Mathf.Lerp(S.Y, E.Y, f), C.Z + Mathf.Sin(ang) * r));
                }
            }
            return outp;
        }

        /// <summary>Open a lane's mouth onto the street its end comes out at: `end` is where the lane's strip stops
        /// (the back of the street's pavement), `w` the way out of it. Null where there is no straight run of a
        /// street's pavement to open -- a junction's corner, a slab, another lane's mouth.</summary>
        LaneMouth OpenMouth(RoadField roads, Vector3 end, Vector3 w, float laneHalf = 0f, float clearOfCorner = 0f)
        {
            float lh = laneHalf > 0f ? laneHalf : LaneHalf;
            // the street: the first of OUR carriageways the lane's line runs into, walking on out of its end
            int r = -1;
            for (float t = 0.25f; t <= 16f && r < 0; t += 0.25f)
                if (roads.DeepestRoadOverlap(end + w * t, 0f, out int hit, out _) && Built.Contains(hit)) r = hit;
            if (r < 0) { DeadNoStreet++; return null; }
            float L = roads.RoadLength(r), sNear = 0f, best = float.MaxValue;
            for (float d = 0f; d <= L; d += 0.5f)
                if (roads.EvaluateAlong(r, d, out var q, out _, snapTerrain: false))
                { float dd = Flat(q, end); if (dd < best) { best = dd; sNear = d; } }
            for (float d = Mathf.Max(0f, sNear - 0.5f); d <= Mathf.Min(L, sNear + 0.5f); d += 0.02f)
                if (roads.EvaluateAlong(r, d, out var q, out _, snapTerrain: false))
                { float dd = Flat(q, end); if (dd < best) { best = dd; sNear = d; } }
            if (!roads.EvaluateAlong(r, sNear, out var pos, out var tan, snapTerrain: false)) return null;
            var t0 = new Vector3(tan.X, 0f, tan.Z).Normalized();
            var right = new Vector3(t0.Z, 0f, -t0.X);
            int side = (end - pos).Dot(right) > 0f ? 1 : -1;
            var n = right * side;                                    // from the street out to the lane's side
            if (w.Dot(n) > -0.5f) { DeadGlancing++; return null; }   // glancing along the street, not into it
            float half = roads.RoadHalfWidth(r);
            // where the centreline crosses the asphalt's edge
            var edgeLine = pos + n * half;
            float mu = (edgeLine - end).Dot(n) / w.Dot(n);
            var edge = end + w * mu;
            // ⚠ THE STREET'S HEIGHT WHERE THE LANE MEETS IT, not where the lane's end is nearest it: a lane meeting the
            // street at an angle crosses its edge metres along from there, and on a slope (UG_TOWNHILLS) that put a
            // 45-degree lane's end 6.6 cm off its street's surface
            float edgeArc = sNear + (edge - edgeLine).Dot(t0);
            if (!roads.EvaluateAlong(r, Mathf.Clamp(edgeArc, 0f, L), out var atEdge, out _, snapTerrain: false)) return null;
            edge.Y = atEdge.Y;
            var into = -w;
            var m = new LaneMouth { Road = r, Side = side, Edge = edge, Into = into, EdgeArc = edgeArc };
            var left = new Vector3(-into.Z, 0f, into.X);               // [0]: the left of the way up the lane
            float from = float.MaxValue, to = float.MinValue;
            for (int c = 0; c < 2; c++)
            {
                var lat = c == 0 ? left : -left;
                var b0 = edge + lat * (lh + PavementGap);                // on the lane's kerb line
                var a0 = pos + n * (half + PavementGap);                 // on the street's
                var dA = t0.Dot(lat) > 0f ? t0 : -t0;                    // along the street, away from the lane
                if (!MouthCorner(a0, dA, b0, into, LaneKerbRadius, _walkWidth, LaneWalkWidth,
                                 out var pts, out var outw, out var wid, out float sA, out float sB, out var P)) { DeadNoCorner++; return null; }
                // ⚠ AND A LITTLE FURTHER UP THE LANE, LEVEL (strawberry 2026-10-13, on hills: "slight misalignment with the
                // kerb of the alley and the sidewalk of the main road"). The return's back corner Q is where the street's
                // pavement, the return and the lane's pavement all meet, and each is level across ITSELF -- the street's
                // across the street, the lane's across the lane -- so on a street that climbs, the street's makes Q one
                // height and the lane's another: a 6.4 cm step at the back of the pavement (the pavement-join probe). The
                // return now runs LaneTwist further up the lane: Q takes the street's height, the return's last section
                // is level with the lane's first, and its back edge climbs between the two.
                float laneFrom = (P + into * sB - edge).Dot(into);
                var twistAt = b0 + into * ((P + into * sB - b0).Dot(into) + LaneTwist);
                pts.Add(twistAt); outw.Add(lat); wid.Add(LaneWalkWidth);
                m.Kerb[c] = pts; m.Out[c] = outw; m.Width[c] = wid;
                float arc = sNear + (P + dA * sA - pos).Dot(t0);         // where the street's pavement stops
                from = Mathf.Min(from, arc); to = Mathf.Max(to, arc);
                m.StreetArc[c] = arc;
                m.LaneFrom[c] = laneFrom + LaneTwist;                     // where the lane's starts
            }
            m.From = from; m.To = to;
            // the opening has to fall on pavement the street actually lays, clear of its junctions' corners
            float lo = TrimFor(roads, r, false, CornerRadius) + 0.25f, hi = L - TrimFor(roads, r, true, CornerRadius) - 0.25f;
            if (from < lo || to > hi) { DeadOffPavement++; return null; }
            if (from < lo + clearOfCorner || to > hi - clearOfCorner) { MouthsNearCorner++; return null; }
            if (MouthAt(r, side, from - 0.5f, to + 0.5f) != null) { DeadOnMouth++; return null; }
            return m;
        }

        /// <summary>One corner of a lane's mouth: the kerb from where the street's pavement stops, round a fillet of
        /// radius R, up to where the lane's own pavement starts, with each point's outward direction and width.
        /// `a`/`dA`: a point on the street's kerb line and the way along it AWAY from the lane; `b`/`dB`: a point on the
        /// lane's kerb line and the way up it; wA/wB the two pavements' widths. `sA`/`sB`: how far from the kerbs' corner
        /// P, along each, its pavement's straight run takes over.
        /// ⚠⚠ EACH STRAIGHT TAKES OVER WHERE ITS BACK EDGE MEETS THE OTHER'S, AT Q -- not where the curve starts. The
        /// street's pavement is four times the lane's, so a lane pavement begun at the end of its 1 m kerb return lies
        /// across the street's for the next 3 m: two slabs at one height, the junctions' "wide pavements cross at a
        /// sharp corner" again. Out to Q the corner piece IS the pavement, fanned onto Q, and each straight begins on
        /// it square.</summary>
        bool MouthCorner(Vector3 a, Vector3 dA, Vector3 b, Vector3 dB, float R, float wA, float wB,
                         out List<Vector3> pts, out List<Vector3> outw, out List<float> widths, out float sA, out float sB, out Vector3 P)
        {
            pts = null; outw = null; widths = null; sA = sB = 0f;
            dA.Y = 0f; dB.Y = 0f; dA = dA.Normalized(); dB = dB.Normalized();
            if (!KerbCorner(a, dA, b, dB, out P)) return false;
            P.Y = 0f;
            var Pk = P;                                            // (an out parameter cannot be captured below)
            float theta = Mathf.Acos(Mathf.Clamp(dA.Dot(dB), -1f, 1f)), sin = Mathf.Sin(theta);
            if (sin < 0.3f) return false;                          // a sliver: no corner worth the name
            float halfT = theta * 0.5f, tdist = R / Mathf.Tan(halfT);
            var bis = (dA + dB).Normalized();
            var C = P + bis * (R / Mathf.Sin(halfT));
            var Q = P + dA * (wB / sin) + dB * (wA / sin);
            float qA = (Q - P).Dot(dA), qB = (Q - P).Dot(dB);
            sA = Mathf.Max(tdist, qA); sB = Mathf.Max(tdist, qB);
            Vector3 Toward(Vector3 d) { var nn = new Vector3(-d.Z, 0f, d.X); return nn.Dot(C - Pk) < 0f ? -nn : nn; }
            var nA = Toward(dA); var nB = Toward(dB);
            // where the street's own pavement ends square at the curve, its back corner IS Q: take it exactly from there, so
            // the corner meets that pavement edge to edge (the lane's side then takes it the fan's way, below)
            var T1back = P + dA * tdist + nA * wA;
            if (new Vector2(T1back.X - Q.X, T1back.Z - Q.Z).Length() < 0.05f) Q = T1back;
            var ps = new List<Vector3>(); var os = new List<Vector3>(); var ws = new List<float>();
            void Fan(Vector3 at) { var v = Q - at; v.Y = 0f; float l = v.Length(); ps.Add(at); os.Add(l > 1e-4f ? v / l : nA); ws.Add(Mathf.Max(l, 0.01f)); }
            // ⚠ A straight's full width that lands on Q is handed Q the fan's way, not its own: worked out separately the two
            // differ by a millimetre (the corner is never exactly square), and the hair-thin triangle that leaves between
            // them shaded the whole corner (EditorSidewalk.BuildRibbon drops a triangle only where its corners coincide)
            void Square(Vector3 at, Vector3 o, float w)
            {
                var back = at + o * w; back.Y = 0f;
                if (new Vector2(back.X - Q.X, back.Z - Q.Z).Length() < 0.05f) { Fan(at); return; }
                ps.Add(at); os.Add(o); ws.Add(w);
            }
            var T1 = P + dA * tdist; var T2 = P + dB * tdist;
            if (sA > tdist + 0.01f) Square(P + dA * sA, nA, wA);
            if (tdist >= qA - 1e-3f) Square(T1, nA, wA); else Fan(T1);
            float s0 = Mathf.Atan2(T1.Z - C.Z, T1.X - C.X), s1 = Mathf.Atan2(T2.Z - C.Z, T2.X - C.X);
            float sweep = Mathf.Wrap(s1 - s0, -Mathf.Pi, Mathf.Pi);
            int steps = Mathf.Max(4, Mathf.CeilToInt(Mathf.Abs(sweep) / Mathf.DegToRad(ArcFacetDegrees)));
            for (int k = 1; k < steps; k++)
            {
                float ang = s0 + sweep * k / steps;
                Fan(new Vector3(C.X + Mathf.Cos(ang) * R, 0f, C.Z + Mathf.Sin(ang) * R));
            }
            if (tdist >= qB - 1e-3f) Square(T2, nB, wB); else Fan(T2);
            if (sB > tdist + 0.01f) Square(P + dB * sB, nB, wB);
            pts = ps; outw = os; widths = ws;
            return true;
        }

        /// <summary>Build every lane PlanLanes laid out: the road, plain and LaneHalf wide; its low pavements, climbing to
        /// the street's height where they meet it; and at each mouth the two kerb returns, with the asphalt under them.
        /// ⚠ CALL AFTER LaySidewalkCorners -- the asphalt joins that pass's corner fill, which it starts afresh -- and
        /// before BuildCornerFill, which lays it. LaySidewalks has already left each mouth open.
        /// ⚠⚠ THE STREET'S HEIGHT AT THE MOUTH, THE LANE'S UP IT. Where the lane's pavement starts it stands level with the
        /// street's beside it; only then does it fall to its own 0.2 m, over LaneRamp. Started low, it would have met the
        /// street's in a 0.2 m step across its whole width.</summary>
        public int BuildLanes(Node3D parent, RoadField roads)
        {
            LaneRoads.Clear(); LaneEndsClosed = 0; ReturnJog = 0f;
            if (parent == null || roads == null || LanePlans.Count == 0) return 0;
            var walk = new EditorSidewalk { Name = "LaneWalk", Width = LaneWalkWidth, RoadGap = PavementGap };
            parent.AddChild(walk);
            Func<float, float, float> ground = Terrain.Active != null && !EditorSidewalk.NoGround ? (x, z) => Terrain.Active.SampleHeight(x, z) : null;
            float fillSurf = roads.RoadSurfaceOffsetOfMaterial(StreetMaterial) - CornerFillDrop;
            int walks = 0, corners = 0, knees = 0;
            foreach (var lp in LanePlans)
            {
                int mat = lp.Material >= 0 ? lp.Material : LaneMaterial;
                if (mat < 0) continue;
                lp.Road = roads.AddRoadFromPolyline(lp.Pts, mat, loop: false, ignoreTerrain: !FollowTerrain);
                if (lp.Road >= 0) LaneRoads.Add(lp.Road);
            }
            // ⭐ A HARD 90's TWO HALVES MEET AT A NODE OF THEIR OWN, which is what mitres their asphalt: two road ends, and
            // only two, at one node is a corner to RoadField, and each end ring then runs corner to corner. The first half is
            // rebuilt once the second is bound -- it was built when it was alone there
            foreach (var lp in LanePlans)
            {
                if (!lp.KneeB || lp.Partner == null || lp.Road < 0 || lp.Partner.Road < 0) continue;
                int j = roads.AddJunction(lp.Pts[^1]);
                roads.BindRoadEnd(lp.Road, true, j);
                roads.BindRoadEnd(lp.Partner.Road, false, j);
                roads.RebuildRoad(lp.Road);
                knees++;
            }
            // ⚠ EACH MOUTH'S END RING ON THE STREET'S SLOPE (strawberry 2026-10-13, "follow gentle hills and slopes"): the lane
            // is level from side to side and the street's edge it ends on climbs along it, so one corner of every mouth on a
            // hill stood proud of the street (11 cm, measured below). The ring now takes the street's surface at both corners.
            // UG_TOWNNOLANEBANK=1 is the control. (A parking lot's way in takes its street's plane at its open end too: the
            // lot's front is the street's height along the street -- BuildParkingLots -- so it meets the lot square.)
            foreach (var lp in LanePlans)
            {
                if (lp.Road < 0 || lp.A == null && lp.B == null || NoLaneBank) continue;
                // (a way that climbs to meet a lot through its side or back keeps its own end, level across, at the lot's height)
                bool openOnPlane = lp.OpenB && Mathf.Abs(lp.Pts[^1].Y - lp.Pts[0].Y) < 0.005f;
                if (lp.OpenB && lp.EndPlane.HasValue) roads.SetEndPlane(lp.Road, true, lp.EndPlane.Value.origin, lp.EndPlane.Value.grad);
                foreach (var (m, atEnd) in new[] { (lp.A, false), (lp.OpenB ? (openOnPlane ? lp.A : null) : lp.B, true) })
                {
                    if (m == null || !roads.EvaluateAlong(m.Road, m.EdgeArc, out var sq, out var stn, snapTerrain: false)) continue;
                    var h = new Vector3(stn.X, 0f, stn.Z); float hl = h.Length();
                    if (hl < 1e-5f) continue;
                    var grad = h / hl * (stn.Y / hl);                   // rise per metre along the street, in plan
                    roads.SetEndPlane(lp.Road, atEnd, new Vector3(sq.X, sq.Y + roads.RoadSurfaceOffset(m.Road), sq.Z), grad);
                }
                roads.RebuildRoad(lp.Road);
            }
            foreach (var lp in LanePlans)
            {
                int road = lp.Road;
                if (road < 0) continue;
                float L = roads.RoadLength(road);
                // the kerb's height as the pavement builder counts it, from its foot: the lane's asphalt there plus as far
                // as the streets' kerbs stand over theirs
                float kerb = roads.RoadSurfaceOffset(road) + KerbProud(roads);
                walk.KerbHeight = kerb;
                // the spline runs from end A to end B, so A's Into is its tangent and B's its reverse: the spline's right,
                // (t.z, 0, -t.x), is the right of A's way in and the left of B's
                // ⚠⚠ ONE HEIGHT FOR A CORNER, AND THE LANE'S PAVEMENT MEETS IT THERE. Each point of the first corners took the
                // ground under itself, and the ground under the street's kerb and 4 m up the lane differ by centimetres -- so
                // the fan of the corner's surface onto its back point Q had its apex at a different height for every
                // triangle, and it shaded as a crease from Q down to the kerb. Each corner now stands at the height of the
                // street pavement it carries on, and the lane's pavement starts at that same height and lets the
                // difference go over its ramp.
                float CornerY(LaneMouth m, int c) { roads.EvaluateAlong(m.Road, m.StreetArc[c], out var q, out _); return q.Y; }
                // ⚠⚠ ON A HILL, THE STREET'S SLOPE CARRIED OUT ACROSS THE MOUTH (strawberry 2026-10-13, "follow gentle hills and
                // slopes"). Each return stood at ONE height, its street's at the return's foot, and so did the asphalt under it
                // -- while the street's edge beside them climbs, so the flare between them sat below the street on the
                // downhill side and the street's bevelled edge showed along it as a dark wedge. Every return's kerb point,
                // back point and fill point now takes the street's own surface carried flat across the mouth (its slope
                // along the street, none across): the lane's end ring meets the same plane (SetEndPlane), so all of the
                // mouth lies in it, and a fan onto a return's back point Q is flat because every point of it is on the plane.
                // Its spline height (kerb foot), as CornerY's is. A return's plane runs through the street exactly where the
                // return meets the street's pavement (c), so the two meet with no step at all; c < 0 is the mouth's middle.
                float MouthFoot(LaneMouth m, Vector3 p, int c = -1)
                {
                    float arc = c >= 0 ? m.StreetArc[c] : m.EdgeArc;
                    if (!roads.EvaluateAlong(m.Road, arc, out var sq, out var stn, snapTerrain: false)) return CornerY(m, Mathf.Max(c, 0));
                    var h = new Vector3(stn.X, 0f, stn.Z); float hl = h.Length();
                    if (hl < 1e-5f || FollowTerrain) return sq.Y;
                    var g = h / hl * (stn.Y / hl);
                    return sq.Y + g.X * (p.X - sq.X) + g.Z * (p.Z - sq.Z);
                }
                float FootY(float d) { roads.EvaluateAlong(road, Mathf.Clamp(d, 0f, L), out var q, out _); return q.Y; }
                // a hard knee's end: where this side's kerb and back edge cross the other half's -- the side of the turn this
                // pavement is on picked by where the point falls, not by a sign convention
                (Vector3 kerb, Vector3 back)? KneeMitre(bool atEnd, int sd)
                {
                    if (atEnd ? !lp.KneeB : !lp.KneeA) return null;
                    var pp = lp.Partner.Pts;
                    var knee = atEnd ? lp.Pts[^1] : lp.Pts[0];
                    var into = atEnd ? lp.Pts[^1] - lp.Pts[^2] : lp.Pts[0] - lp.Pts[1]; into.Y = 0f; into = into.Normalized();
                    var outp = atEnd ? pp[1] - pp[0] : pp[^2] - pp[^1]; outp.Y = 0f; outp = outp.Normalized();
                    var t = lp.Pts[^1] - lp.Pts[0]; t.Y = 0f; var right = new Vector3(t.Z, 0f, -t.X).Normalized();
                    float kOff = LaneHalf + PavementGap, bOff = kOff + LaneWalkWidth;
                    foreach (int so in new[] { 1, -1 })
                    {
                        var k = RoadField.CornerPoint(knee, into, outp, kOff, kOff, so);
                        if ((k - knee).Dot(right) * sd <= 0f) continue;
                        return (k, RoadField.CornerPoint(knee, into, outp, bOff, bOff, so));
                    }
                    return null;
                }
                foreach (var (sd, sideEnum) in new[] { (1, EditorSidewalk.Side.Right), (-1, EditorSidewalk.Side.Left) })
                {
                    int cA = sd > 0 ? 1 : 0, cB = sd > 0 ? 0 : 1;
                    // (a closed end's kerb takes the lane's last metre: the pavements stop where it starts; a hard knee's
                    // pavement runs to its mitre)
                    float fromA = lp.A != null ? lp.A.LaneFrom[cA] : lp.KneeA ? 0f : LaneWalkWidth;
                    // (an open end -- a parking lot's way -- stops its pavements a pavement's width short: the lot's kerb curves in
                    // from there)
                    float fromB = lp.B != null ? lp.B.LaneFrom[cB] : lp.KneeB ? 0f : LaneWalkWidth;
                    // (the return's end on the lane side is the lane's kerb at LaneFrom, and its foot is on the mouth's plane)
                    float LaneSideFoot(LaneMouth m, int c)
                    {
                        var kp = m.Kerb[c];
                        if (kp == null || kp.Count == 0) return CornerY(m, c);
                        var end = (kp[0] - m.Edge).Dot(m.Into) > (kp[^1] - m.Edge).Dot(m.Into) ? kp[0] : kp[^1];   // the end further up the lane
                        return MouthFoot(m, end, c);
                    }
                    float liftA = lp.A != null ? LaneSideFoot(lp.A, cA) - FootY(fromA) : 0f;
                    float liftB = lp.B != null ? LaneSideFoot(lp.B, cB) - FootY(L - fromB) : 0f;
                    // MEASURED: where the return's kerb hands over to the lane's own, in plan -- the return was laid off
                    // the PLANNED lane, the pavement off the BUILT one
                    foreach (var (m, c, d) in new[] { (lp.A, cA, fromA), (lp.B, cB, L - fromB) })
                    {
                        if (m == null || m.Kerb[c] == null || m.Kerb[c].Count == 0) continue;
                        if (!roads.EvaluateAlong(road, Mathf.Clamp(d, 0f, L), out var wq, out var wt, snapTerrain: false)) continue;
                        var wr = new Vector3(wt.Z, 0f, -wt.X).Normalized();
                        var kerbAt = wq + wr * (sd * (roads.RoadHalfWidth(road) + PavementGap));
                        var kp = m.Kerb[c];
                        var end = (kp[0] - m.Edge).Dot(m.Into) > (kp[^1] - m.Edge).Dot(m.Into) ? kp[0] : kp[^1];
                        float jog = Flat(kerbAt, end);
                        if (jog > ReturnJog) { ReturnJog = jog; ReturnJogAt = end; }
                    }
                    // ⚠ UP OR DOWN (strawberry 2026-10-13, "follow gentle hills and slopes"). Each end's difference fades out over
                    // the ramp, and the two add. It took the higher of them instead, which meant the pavement could climb to a
                    // return but never come down to one -- and on a hill the mouth's low side is below the lane: its return
                    // stood up to 29 cm over the lane's pavement where they met (the pavement-join probe).
                    float Surface(float d)
                    {
                        float h = kerb;
                        if (lp.A != null)
                        {
                            float f = Mathf.Clamp((d - fromA) / LaneRamp, 0f, 1f);
                            h += Mathf.Lerp(PavementKerb + liftA, kerb, f) - kerb;
                        }
                        if (lp.B != null)
                        {
                            float f = Mathf.Clamp((L - fromB - d) / LaneRamp, 0f, 1f);
                            h += Mathf.Lerp(PavementKerb + liftB, kerb, f) - kerb;
                        }
                        return h;
                    }
                    // ⚠ A PARKING LOT'S WAY: its pavements only where there is way left between the mouth's returns and the lot's
                    // kerb curve (BuildParkingLots) -- none through a lot's front, whose returns run to the back of the street's
                    // pavement and the curve takes the last metre; down a way out through a side, across the grass to the street,
                    // however short (LayAlongRoad would refuse under two stations)
                    walks += walk.LayAlongRoad(walk, roads, road, sideEnum, null, fromA, fromB, surfaceAt: Surface,
                                               mitreStart: KneeMitre(false, sd), mitreEnd: KneeMitre(true, sd), minLength: lp.OpenB ? 0.05f : -1f);
                }
                // ⭐ A LANE THAT STOPS SHORT ENDS IN A KERB, NOT A RAMP: a strip of its own pavement straight across the end,
                // as wide as the lane and both its pavements, standing on the asphalt's last metre -- so the road's own
                // end ramp lies under it and the lane reads as closed off rather than unfinished
                foreach (var (closed, atEnd) in new[] { (lp.A == null && !lp.KneeA, false), (lp.B == null && !lp.KneeB && !lp.OpenB, true) })
                {
                    if (!closed || !roads.EvaluateAlong(road, atEnd ? L : 0f, out var ep, out var et)) continue;
                    var tn = new Vector3(et.X, 0f, et.Z).Normalized();
                    var outE = atEnd ? tn : -tn;
                    var across = new Vector3(tn.Z, 0f, -tn.X);
                    float reach = LaneHalf + PavementGap + LaneWalkWidth;
                    // (at the lane's height where the strip's kerb stands, a pavement's width back from the end -- where the
                    // lane's pavements stop -- not the end's: on a slope the two differ by the slope, and the pavements met the
                    // strip in a step of up to 6.3 cm, the pavement-join probe)
                    var stripFoot = ep - outE * LaneWalkWidth;
                    stripFoot.Y = FootY(atEnd ? L - LaneWalkWidth : LaneWalkWidth);
                    var kerbLine = new List<Vector3> { stripFoot - across * reach, stripFoot + across * reach };
                    var mi = EditorSidewalk.BuildRibbon(kerbLine, new List<Vector3> { outE, outE }, LaneWalkWidth + 0.6f, kerb, walk.Skirt,
                                                        $"Sidewalk_LaneEnd_{road}_{(atEnd ? "B" : "A")}", ground);
                    if (mi == null) continue;
                    parent.AddChild(mi);
                    var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                    body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                    body.AddChild(new CollisionShape3D { Shape = mi.Mesh.CreateTrimeshShape() });
                    mi.AddChild(body);
                    LaneEndsClosed++;
                }
                foreach (var (m, atEnd) in new[] { (lp.A, false), (lp.B, true) })
                {
                    if (m == null) continue;
                    for (int c = 0; c < 2; c++)
                    {
                        var kp = m.Kerb[c];
                        if (kp == null || kp.Count < 2) continue;
                        // the kerb on the plane; the walking surface level across from it, as the street's pavement and the
                        // lane's are where they meet the return -- except where it fans onto the back corner Q, which is one
                        // point at one height (its own on the plane), or the fan creases
                        var backs = new List<Vector3>(kp.Count);
                        for (int q = 0; q < kp.Count; q++) backs.Add(kp[q] + m.Out[c][q] * m.Width[c][q]);
                        Vector3? apex = null;
                        for (int q = 0; q < backs.Count && apex == null; q++)
                        {
                            int shared = 0;
                            for (int o = 0; o < backs.Count; o++) if (Flat(backs[q], backs[o]) < 0.02f) shared++;
                            if (shared >= 2) apex = backs[q];
                        }
                        float apexFoot = apex.HasValue ? MouthFoot(m, apex.Value, c) : 0f;
                        var cp = new List<Vector3>(kp.Count); var surf = new List<float>(kp.Count);
                        for (int q = 0; q < kp.Count; q++)
                        {
                            float foot = MouthFoot(m, kp[q], c);
                            cp.Add(new Vector3(kp[q].X, foot, kp[q].Z));
                            surf.Add(apex.HasValue && Flat(backs[q], apex.Value) < 0.02f ? apexFoot - foot + PavementKerb : PavementKerb);
                        }
                        var kerbs = new List<float>(kp.Count);
                        for (int q = 0; q < kp.Count; q++) kerbs.Add(PavementKerb);     // (the kerb's own top stays over its foot)
                        var mi = EditorSidewalk.BuildRibbon(cp, m.Out[c], _walkWidth, PavementKerb, walk.Skirt,
                                                            $"Sidewalk_LaneMouth_{road}_{(atEnd ? "B" : "A")}{c}", ground, kerbs, m.Width[c], surf);
                        if (mi == null) continue;
                        parent.AddChild(mi);
                        var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                        body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                        body.AddChild(new CollisionShape3D { Shape = mi.Mesh.CreateTrimeshShape() });
                        mi.AddChild(body);
                        corners++;
                        // the asphalt under the return, fanned from the middle of the mouth: under the road surfaces, so
                        // the street's and the lane's own ribbons win wherever they are and only the fillet shows it.
                        // ⚠ AT THE ROADS' OWN HEIGHT: the street's profile where the town holds one, the ground where it is
                        // draped. (A dark sliver across the mouth was this fill standing proud of a lane that had been laid
                        // 12 cm low on the graded ground; putting the fill on the ground hid it and sank the fillet's asphalt
                        // instead. The lane is at the street's height now, and so is this.)
                        Vector3 F(Vector3 q) => new Vector3(q.X, (FollowTerrain && ground != null ? ground(q.X, q.Z) : MouthFoot(m, q)) + fillSurf, q.Z);
                        var e0 = F(m.Edge);
                        for (int q = 0; q + 1 < cp.Count; q++) _cornerFill.Add((e0, F(cp[q]), F(cp[q + 1])));
                    }
                }
            }
            // HEIGHTS, measured: how far each kerb stands proud of its OWN carriageway, and how far below the street's
            // asphalt at its mouth each lane's asphalt sinks anywhere along it
            if (LaneRoads.Count > 0)
            {
                float laneOff = roads.RoadSurfaceOffset(LaneRoads[0]), streetOff = Built.Count > 0 ? roads.RoadSurfaceOffset(Built[0]) : 0f;
                float worstSink = 0f; string sinkAt = "";
                float worstLip = 0f; string lipAt = ""; _roadSamples.Clear();
                foreach (var lp in LanePlans)
                {
                    if (lp.Road < 0) continue;
                    float L = roads.RoadLength(lp.Road);
                    foreach (var (m, d) in new[] { (lp.A, 0f), (lp.B, L) })
                    {
                        if (m == null) continue;
                        // ⚠ THE STREET'S SURFACE, read the way the road builds it: its own profile here (EvaluateAlong only snaps
                        // to the ground for a draped road). The first version of this read the GROUND at the street's edge --
                        // graded 0.12 m below the street -- and reported a lane 12 cm low as "0.003 m".
                        roads.EvaluateAlong(m.Road, m.EdgeArc, out var sp2, out _);
                        roads.EvaluateAlong(lp.Road, d, out var lq, out var lt);
                        float sink = sp2.Y + roads.RoadSurfaceOffset(m.Road) - (lq.Y + roads.RoadSurfaceOffset(lp.Road));
                        if (Mathf.Abs(sink) > Mathf.Abs(worstSink)) { worstSink = sink; sinkAt = $"lane {lp.Road}'s {(d == 0f ? "A" : "B")} end"; }
                    }
                    // ...and across it, off the lane's BUILT mesh: any of it out on the street's carriageway standing over the
                    // street's asphalt there is a lip a wheel hits and an edge the eye sees. (Level from side to side, a lane
                    // ending on a street that climbs along it stood up to 11 cm proud at one corner of its mouth.)
                    // ⚠ OVER ITS TRIANGLES, NOT JUST ITS VERTICES: the end ramp's corners are below the street and its ring's on
                    // the street's very edge, and the lip is the slope between them crossing the street's surface -- a vertex
                    // count of the control read 0.000 m with the sliver in plain sight.
                    var lmesh = roads.RoadMeshOf(lp.Road);
                    for (int sfc = 0; lmesh != null && sfc < lmesh.GetSurfaceCount(); sfc++)
                    {
                        var arrs = lmesh.SurfaceGetArrays(sfc);
                        var vs = arrs[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var ix = arrs[(int)Mesh.ArrayType.Index].AsInt32Array();
                        int nt = ix != null && ix.Length >= 3 ? ix.Length / 3 : vs.Length / 3;
                        for (int t = 0; t < nt; t++)
                        {
                            var a = vs[ix != null && ix.Length >= 3 ? ix[3 * t] : 3 * t];
                            var b = vs[ix != null && ix.Length >= 3 ? ix[3 * t + 1] : 3 * t + 1];
                            var c = vs[ix != null && ix.Length >= 3 ? ix[3 * t + 2] : 3 * t + 2];
                            foreach (var m in new[] { lp.A, lp.B })
                            {
                                if (m == null || Flat(a, m.Edge) > roads.RoadHalfWidth(lp.Road) + 3f) continue;
                                for (int u = 0; u <= 6; u++)
                                    for (int w = 0; u + w <= 6; w++)
                                    {
                                        var v = a + (b - a) * (u / 6f) + (c - a) * (w / 6f);
                                        float sy = RoadNear(roads, m.Road, v, out float along, out float off);
                                        if (off > roads.RoadHalfWidth(m.Road) + 0.02f || along <= 0.01f || along >= roads.RoadLength(m.Road) - 0.01f) continue;
                                        float lip = v.Y - (sy + roads.RoadSurfaceOffset(m.Road));
                                        if (lip > worstLip) { worstLip = lip; lipAt = $"lane {lp.Road} at {v}"; }
                                    }
                            }
                        }
                    }
                }
                Log.Print($"[towngrid] lane heights: carriageway surface {laneOff:0.000} m over its kerb foot, street's {streetOff:0.000}; "
                        + $"lane kerb {walk.KerbHeight - laneOff:0.000} m proud of the lane's asphalt, street kerb {PavementKerb - streetOff:0.000} m proud of the street's; "
                        + $"worst step from a street's asphalt down onto its lane's at the mouth {worstSink:0.000} m{(worstSink != 0f ? $" ({sinkAt})" : "")}; "
                        + $"worst lip of a lane over its street's asphalt at a mouth {worstLip:0.000} m{(worstLip > 0f ? $" ({lipAt})" : "")}");
                MouthLip = worstLip;
                Log.Print($"[towngrid] lane returns: worst miss between a return's kerb and its lane pavement's, in plan, {ReturnJog * 100f:0.0} cm at {ReturnJogAt}");
            }
            Log.Print($"[towngrid] lanes: {LaneRoads.Count} built from {Alleys.Count} alleys ({knees} L-turns as hard 90s), {LaneMouths.Count} mouths opened "
                    + $"({corners} kerb returns), {walks} pavement runs; {LaneDeadEnds} ends could not open onto a street and stop short "
                    + $"({DeadNoStreet} no street beyond, {DeadGlancing} glancing, {DeadNoCorner} no corner, {DeadOffPavement} into a junction's corner, "
                    + $"{DeadOnMouth} onto another lane's mouth{(LaneDeadEnds > 0 ? $"; first at {FirstDeadEnd}" : "")}), {LaneEndsClosed} closed with a kerb");
            return LaneRoads.Count;
        }

        /// <summary>How much of the town's buildable ground the plots took, counted on a 5 m grid: plots, lanes, and
        /// ground a building could stand on that nothing was zoned for -- the gaps. UG_TOWNPROBES=1 also prints the grid
        /// as a map: P plot, a lane, . unzoned, # road or pavement.</summary>
        public int ZonedCells, LaneCells, UnzonedCells;
        void CensusZoning(RoadField roads)
        {
            ZonedCells = LaneCells = UnzonedCells = 0;
            if (_lineU == null || roads == null) return;
            static bool Inside(Vector3[] c, Vector3 q) => InsidePoly(c, q);
            const float Cell = 5f;
            for (float v = _frameV1; v >= _frameV0; v -= Cell)
            {
                var row = ProbeEverything ? new System.Text.StringBuilder() : null;
                for (float u = _frameU0; u <= _frameU1; u += Cell)
                {
                    var q = ToWorld(u, v, 0f);
                    char ch = '#';
                    bool plot = false; foreach (var pl in Plots) if (Inside(pl.Boundary(), q)) { plot = true; break; }
                    if (plot) { ch = 'P'; ZonedCells++; }
                    else
                    {
                        bool lane = false; foreach (var al in Alleys) if (Inside(al.Corners(), q)) { lane = true; break; }
                        if (lane) { ch = 'a'; LaneCells++; }
                        else if (Buildable(roads, q)) { ch = '.'; UnzonedCells++; }
                    }
                    row?.Append(ch);
                }
                if (row != null) Log.Print($"[towngrid] zoning map {row}");
            }
            Log.Print($"[towngrid] zoning: {ZonedCells} 5 m cells are plots, {LaneCells} lanes, {UnzonedCells} buildable but zoned for nothing "
                    + $"({100f * ZonedCells / Mathf.Max(1, ZonedCells + UnzonedCells):0}% of the buildable ground zoned)");
        }

        // ==============================================================================================
        // PARKING BAYS (strawberry 2026-10-12: "work on widened sections of road, with dotted lines for parking
        // on one or both sides of the street within the widened portion").
        // ==============================================================================================

        /// <summary>A stretch of one side of a street where the kerb steps out and the asphalt widens into the
        /// gap: a lay-by for parked cars. Its kerb leaves the carriageway's edge at A, angles out over Taper to
        /// Depth, runs parallel, and angles back in to meet the edge again at B.</summary>
        public sealed class ParkingBay
        {
            public int Road;
            /// <summary>+1 = the spline's (t.z, 0, -t.x) side, the way the pavement and the lamps count sides.</summary>
            public int Side;
            /// <summary>Arc metres along Road where the kerb leaves the carriageway's edge, and meets it again.</summary>
            public float A, B;
            public float Depth, Taper;
            /// <summary>The car spaces marked: Spaces of SpaceLength each, the first starting at arc First.</summary>
            public int Spaces; public float First, SpaceLength;
            /// <summary>Marked paid parking (PaidZone): a meter at every space (PlaceLights).</summary>
            public bool Paid;
            /// <summary>Arc metres where the plots stepped back behind it begin and end: the pavement's back edge
            /// steps out by Depth over exactly this stretch, to stand where they stand.</summary>
            public float Front0, Front1;
            /// <summary>How far past the carriageway's edge the kerb stands at arc `d`.</summary>
            public float Extra(float d)
            {
                if (d <= A || d >= B) return 0f;
                if (d < A + Taper) return Depth * (d - A) / Taper;
                if (d > B - Taper) return Depth * (B - d) / Taper;
                return Depth;
            }
        }

        /// <summary>The bays the last PlanPlots laid.</summary>
        public readonly List<ParkingBay> Bays = new();
        /// <summary>UG_TOWNNOPARKING=1: no bays -- the control for everything they change about a street.</summary>
        public bool Parking = System.Environment.GetEnvironmentVariable("UG_TOWNNOPARKING") != "1";
        /// <summary>How far the kerb steps out, how long each angled end runs along the street, and one parallel
        /// space.
        /// ⚠⚠ SIZED TO THIS GAME'S CARS, WHICH ARE NOT REAL-WORLD CARS (strawberry 2026-10-12: "make sure they're
        /// scaled to fit the vehicles in game"). Measured off the BUILT vehicles, wheels and fittings included:
        /// every car is 3.0 m across its tyres (a real one is ~1.8) and the longest, the sedan, is 6.15 m (a real
        /// one is ~4.7) -- the lanes here are 4.6 m for the same reason. A real-world 2.4 x 6 m bay left a parked
        /// sedan most of a metre out in the traffic lane and hanging off both ends of its space. So: deep enough
        /// for the widest car with 25 cm either side, inside the lane line and the kerb's lap; long enough for the
        /// longest with 1.35 m to pull in and out. The town test builds the cars and holds the bays to them.</summary>
        public float BayDepth = 3.7f, BayTaper = 3.5f, BaySpace = 7.5f;
        /// <summary>The share of street lines whose bays are cut square -- the kerb stepping straight out and straight back in,
        /// the lay-by boxed into the pavement -- rather than angled. UG_TOWNBAYS=square|angled makes it every street.</summary>
        public float SquareBayShare = 0.5f;
        public string BayForce = System.Environment.GetEnvironmentVariable("UG_TOWNBAYS");
        /// <summary>A square bay's "taper": its kerb's step, all but straight across. (Not zero: the bays' arithmetic divides
        /// by it, and the pavement wants a station each side of the step.)</summary>
        public const float SquareTaper = 0.02f;
        bool SquareBays(RoadField roads, int r) => BayForce == "square" || (BayForce != "angled" && Unit(LineSeed(roads, r, 0x5C0A7EUL), 0) < SquareBayShare);
        /// <summary>The LEAST distance inside its first and last plot's frontage a bay's kerb begins to step out. In
        /// practice the pavement band's lead sets it (LayParkingBays): 1.7 m for a 4 m pavement.</summary>
        public float BayInset = 0.25f;
        /// <summary>How far past a junction's crossing and stop line a bay may begin: nobody parks in the queue
        /// for the lights.</summary>
        public float BayClearOfJunction = 6f;
        /// <summary>The share of streets with no bays, and with bays down one side only; the rest have them down
        /// both.</summary>
        public float BayNoneShare = 0.3f, BayOneSideShare = 0.4f;
        /// <summary>The paint: the width of the solid line across each end of a space (and the length of a dot and its
        /// gap, unused since the bays went solid).</summary>
        public float BayLine = 0.2875f, BayDot = 0.5f, BayDotGap = 0.5f;     // as thick as the roads' own lines (strawberry: "match the center lines")
        /// <summary>How far the bay's asphalt tucks under the carriageway's edge, how far it runs on under the
        /// kerb, and how far below the road's surface it lies.</summary>
        public float BayTuck = 0.3f, BayUnderKerb = 0.08f, BayDrop = 0.01f;
        /// <summary>The line along the bay's open side stands this far inside it from the carriageway's edge; the
        /// lines across it stop this short of the kerb.</summary>
        public float BayEdgeGap = 0.05f, BayKerbGap = 0.15f;
        /// <summary>Dots painted by the last BuildParking.</summary>
        public int ParkingPaintPieces;
        /// <summary>The last LayParkingBays' census of the plots fronting a side chosen for parking: all of them,
        /// those with no room behind to step back, those too near a junction, those in a run too short for a car.</summary>
        int _bayOffered, _bayNoRoom, _bayNearJunction, _bayTooShort, _bayAtLane;

        /// <summary>How far past the carriageway's edge the kerb stands at arc `d` on `side` of `road`.</summary>
        public float BayExtra(int road, int side, float d)
        {
            float e = 0f;
            foreach (var bay in Bays) if (bay.Road == road && bay.Side == side) e = Mathf.Max(e, bay.Extra(d));
            return e;
        }

        /// <summary>How far past its usual line the pavement's BACK edge stands at arc `d` on `side` of `road`.
        /// ⚠⚠ THE PAVEMENT'S OWN WIDTH SQUARE TO THE ANGLED KERB, FOLLOWING IT (strawberry 2026-10-13, of a back edge
        /// squared off to the plots: "now theyre square?"). A band W wide round a kerb that turns through phi has to
        /// start turning W tan(phi/2) BEFORE the kerb does -- 1.7 m for a 4 m pavement at a bay's 47 degrees -- which
        /// is in front of the next house. So it follows the kerb at its full width wherever the plots behind have
        /// stepped back to make room, and is clipped to the usual line in front of the next plot: a short square
        /// step there, and the angle everywhere else.</summary>
        /// <summary>⭐ SIDEWALKS MERGE ACROSS A SLIVER (strawberry 2026-10-14: "have sidewalks try to auto-fill small slivers of
        /// deadspace" -- then: "not to the property line, should merge with close other sidewalks"). Where one pavement runs
        /// within SliverMax of another with nothing but bare ground between -- a parking lot's kerb beside a street's
        /// pavement, or backing onto a lane's -- the lot's pavement runs on across the gap and meets it (BuildParkingLots).
        /// Never out to a plot's line: ground between a pavement and somebody's yard stays theirs. UG_TOWNNOSLIVERS=1 is
        /// the control.</summary>
        public bool FillSlivers = System.Environment.GetEnvironmentVariable("UG_TOWNNOSLIVERS") != "1";
        public float SliverMax = 2f;
        public int SliverPoints; public float SliverWidest;
        /// <summary>How far along a ray (in plan, from `o` along unit `n`) it first meets a polygon's edge, within `maxT` --
        /// MaxValue if it does not.</summary>
        static float RayIntoPoly(Vector3 o, Vector3 n, Vector3[] poly, float maxT)
        {
            float best = float.MaxValue;
            for (int e = 0; e < poly.Length; e++)
            {
                var a = poly[e]; var b = poly[(e + 1) % poly.Length];
                float ex = b.X - a.X, ez = b.Z - a.Z;
                float den = n.X * ez - n.Z * ex;
                if (Mathf.Abs(den) < 1e-6f) continue;
                float ax = a.X - o.X, az = a.Z - o.Z;
                float t = (ax * ez - az * ex) / den, u = (ax * n.Z - az * n.X) / den;
                if (t >= -1e-4f && t <= maxT && u >= -1e-4f && u <= 1f + 1e-4f) best = Mathf.Min(best, Mathf.Max(0f, t));
            }
            return best;
        }

        public float BayBack(int road, int side, float d)
        {
            float best = 0f;
            foreach (var bay in Bays)
            {
                if (bay.Road != road || bay.Side != side || d < bay.Front0 || d > bay.Front1) continue;
                float slant = Mathf.Sqrt(bay.Taper * bay.Taper + bay.Depth * bay.Depth);
                float lead = PavementWidth * (slant - bay.Taper) / bay.Depth;     // how far before the kerb turns the band does
                best = Mathf.Max(best, Mathf.Clamp(bay.Depth / bay.Taper * Mathf.Min(d - bay.A + lead, bay.B - d + lead), 0f, bay.Depth));
            }
            return best;
        }
        /// <summary>Where a bay's pavement back edge turns: the band's own corners, which lead the kerb's.</summary>
        float BayBackLead(ParkingBay bay)
        {
            float slant = Mathf.Sqrt(bay.Taper * bay.Taper + bay.Depth * bay.Depth);
            return PavementWidth * (slant - bay.Taper) / bay.Depth;
        }

        /// <summary>The bay across the street overlapping this one, where the street widens on both sides.</summary>
        public ParkingBay BayOpposite(ParkingBay bay)
        {
            foreach (var o in Bays) if (o.Road == bay.Road && o.Side == -bay.Side && o.A < bay.B && bay.A < o.B) return o;
            return null;
        }

        /// <summary>How far from this end of the road, in arc metres, the first bay may begin: BayClearOfJunction
        /// past the crossing's stop line -- past the corner, for a main running through with no crossing -- and
        /// never before the pavement itself begins.</summary>
        float NoParkingFromEnd(RoadField roads, int road, bool atEnd)
        {
            float trim = TrimFor(roads, road, atEnd, CornerRadius);
            int j = roads.RoadEndJunction(road, atEnd);
            if (j < 0) return trim + BayClearOfJunction;
            if (!roads.EvaluateAlong(road, atEnd ? roads.RoadLength(road) : 0f, out var tip, out _, snapTerrain: false))
                return trim + BayClearOfJunction;
            var node = roads.JunctionPos(j);
            float cut = new Vector2(tip.X - node.X, tip.Z - node.Z).Length();      // the node to this ribbon's end
            float stop;
            if (LayCrossing(roads, j, road, atEnd, out var c)) stop = c.SD1 + c.Half * Mathf.Abs(c.Shear);
            else
            {
                float crossed = roads.RoadHalfWidth(road);
                foreach (var (o, _) in roads.JunctionEdges(j)) if (o != road) crossed = Mathf.Max(crossed, roads.RoadHalfWidth(o));
                stop = ApproachClear(roads, j, road, atEnd, crossed);
            }
            return Mathf.Max(trim + BayClearOfJunction, stop + BayClearOfJunction - cut);
        }

        /// <summary>Choose the streets that get parking, lay a bay in front of each run of plots that can stand
        /// back to make room for one, and step those plots back; returns how many were stepped.
        ///
        /// ⭐ THE ROOM COMES OUT OF THE PLOTS, NOT THE PAVEMENT. A bay pushes the kerb out by its depth and the
        /// pavement goes with it at its full width, so whatever stood just behind the pavement has to stand that
        /// much further back. A plot is a fixed size and is never trimmed, so it MOVES: the whole plot steps back,
        /// and a bay goes only in front of plots with that much room behind them -- a 36 m lot already reaching
        /// its block's back lane stays where it is and nothing is laid in front of it. A bay spans whole plots, so
        /// it starts and ends where one plot's frontage meets the next, which is where the building line steps.
        /// ⚠ CLEAR OF THE JUNCTIONS: never within BayClearOfJunction of a crossing's stop line. And never across an
        /// alley's mouth -- that is a gap between plots, and no run of adjoining plots spans one.
        /// ⚠ STRAIGHT FRONTAGE ONLY. A run round a bend has no single line to step its plots back along.</summary>
        int LayParkingBays(RoadField roads)
        {
            Bays.Clear();
            _bayOffered = _bayNoRoom = _bayNearJunction = _bayTooShort = _bayAtLane = 0;
            if (!Parking || BayDepth <= 0f || BayTaper <= 0f) return 0;
            int stepped = 0;
            // ⚠⚠ THE KERB STARTS ITS ANGLE THE BAND'S LEAD IN FROM THE PLOTS' EDGE (strawberry 2026-10-13, of a band
            // clipped square where it met the next plot: "now its like.. notched half sloped half 90 degrees"). The
            // pavement's back edge turns W·(slant - taper)/depth before its kerb does (BayBack); starting the kerb that
            // far inside the stepped frontage puts the back edge's turn exactly at the plots' edge -- sloped the whole
            // way, nothing to clip. It costs length: a single 15 m plot no longer holds a space.
            float InsetFor(float taper) => Mathf.Max(BayInset, PavementWidth * (Mathf.Sqrt(taper * taper + BayDepth * BayDepth) - taper) / BayDepth);
            foreach (int r in Built)
            {
                if (ProfileOf(r) == StreetProfile.KerbOnly) continue;      // no footway to step out onto (a verge is paved along one: TownVerges)
                ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix((ulong)(uint)r ^ 0xBA75EDUL));
                float u = Unit(h, 0);
                if (u < BayNoneShare) continue;
                // angled ends, or cut square (strawberry 2026-10-15: "add support for square cut versions of the street parking
                // bays") -- the same down the whole line, both sides
                float taper = SquareBays(roads, r) ? SquareTaper : BayTaper;
                float inset = InsetFor(taper);
                // then one side, the other, or both
                float mid = BayNoneShare + BayOneSideShare * 0.5f, both = BayNoneShare + BayOneSideShare;
                bool right = u < mid || u >= both, left = u >= mid;
                float L = roads.RoadLength(r);
                float lo = NoParkingFromEnd(roads, r, false), hi = L - NoParkingFromEnd(roads, r, true);
                if (hi - lo < BaySpace + 2f * (taper + inset)) continue;
                foreach (int side in new[] { 1, -1 })
                {
                    if (side == 1 ? !right : !left) continue;
                    var row = new List<Plot>();
                    foreach (var pl in Plots)
                        if (pl.Road == r && pl.Road2 < 0 && pl.Arc >= 0f && pl.RoadSide == side && pl.Straight) row.Add(pl);
                    row.Sort((x, y) => x.Arc.CompareTo(y.Arc));
                    var group = new List<(Plot pl, Vector3 was)>();
                    void Close()
                    {
                        if (group.Count == 0) return;
                        float a = group[0].pl.Arc - group[0].pl.Width * 0.5f + inset;
                        float b = group[^1].pl.Arc + group[^1].pl.Width * 0.5f - inset;
                        float full = b - a - 2f * taper;
                        int n = Mathf.FloorToInt(full / BaySpace + 1e-4f);
                        if (n < 1)
                        {
                            foreach (var (pl, was) in group) { pl.Front = was; pl.Setback = 0f; }      // too short: put them back
                            _bayTooShort += group.Count;
                        }
                        else
                        {
                            Bays.Add(new ParkingBay { Road = r, Side = side, A = a, B = b, Depth = BayDepth, Taper = taper, Paid = PaidZone(roads, r, group),
                                                      Spaces = n, SpaceLength = BaySpace, First = a + taper + (full - n * BaySpace) * 0.5f,
                                                      Front0 = a - inset, Front1 = b + inset });
                            stepped += group.Count;
                        }
                        group.Clear();
                    }
                    foreach (var pl in row)
                    {
                        _bayOffered++;
                        float p0 = pl.Arc - pl.Width * 0.5f, p1 = pl.Arc + pl.Width * 0.5f;
                        if (group.Count > 0 && Mathf.Abs(group[^1].pl.Arc + group[^1].pl.Width * 0.5f - p0) > 0.05f) Close();
                        if (p0 < lo || p1 > hi) { _bayNearJunction++; Close(); continue; }
                        // ...nor in front of a plot whose frontage a lane's mouth takes a piece of (a lane meeting the
                        // street at an angle opens wider than its own strip on the sharp side)
                        if (MouthAt(r, side, p0 + 0.05f, p1 - 0.05f) != null) { _bayAtLane++; Close(); continue; }
                        // ...nor in front of a parking lot, whose ways in cross the kerb there
                        if (pl.Kind == PlotKind.Parking) { Close(); continue; }
                        // step it back, and keep it there only if it still fits
                        var was = pl.Front;
                        int at = Plots.IndexOf(pl);
                        Plots.RemoveAt(at);
                        pl.Front = was - pl.Facing * BayDepth; pl.Setback = BayDepth;
                        bool fits = PlotFits(roads, pl);
                        Plots.Insert(at, pl);
                        if (!fits) { _bayNoRoom++; pl.Front = was; pl.Setback = 0f; Close(); continue; }
                        group.Add((pl, was));
                    }
                    Close();
                }
            }
            return stepped;
        }

        /// <summary>Pave and paint the bays: asphalt from under the carriageway's edge out under the stepped kerb,
        /// and dotted lines -- one down the bay's open side along the traffic lane, and one across it at each end
        /// of each space. The asphalt is the corner fill's (the slab material, its plain band, normals up), the
        /// paint the crossings' (that material's own white texel), for the reasons given there.
        /// ⚠ A HAIR UNDER THE ROAD, NOT OVER IT. The bay tucks under the carriageway's edge, so no ground shows at
        /// the join, and lies a centimetre lower, so the road wins the overlap outright -- riding over it would
        /// draw the bay's own edge as a lip along the lane.</summary>
        public int BuildParking(Node3D parent, RoadField roads)
        {
            ParkingPaintPieces = 0;
            if (parent == null || roads == null || Bays.Count == 0) return 0;
            var mat = roads.MaterialOfMaterial(MainMaterial);
            const float U0 = 4f / 256f, U1 = 56f / 256f;
            float tile = (U1 - U0) * 2f * 2f * Mathf.Max(1f, roads.RoadHalfWidthOfMaterial(MainMaterial));
            Vector2 UV(Vector3 q) => new Vector2(U0 + PingPong(q.X / tile) * (U1 - U0), q.Z / tile);
            var uvWhite = new Vector2(0.2363f, 0.0664f);
            var ground = new SurfaceTool(); ground.Begin(Mesh.PrimitiveType.Triangles);
            var paint = new SurfaceTool(); paint.Begin(Mesh.PrimitiveType.Triangles);
            // orient by the wanted normal (up), never by the order given
            void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector2? uv)
            {
                bool keep = (c - a).Cross(b - a).Y >= 0f;
                var i1 = keep ? b : c; var i2 = keep ? c : b;
                st.SetNormal(Vector3.Up); st.SetUV(uv ?? UV(a)); st.AddVertex(a);
                st.SetNormal(Vector3.Up); st.SetUV(uv ?? UV(i1)); st.AddVertex(i1);
                st.SetNormal(Vector3.Up); st.SetUV(uv ?? UV(i2)); st.AddVertex(i2);
            }
            foreach (var bay in Bays)
            {
                float half = roads.RoadHalfWidth(bay.Road), surface = roads.RoadSurfaceOffset(bay.Road);
                // a station: the centreline at arc d, the bay side's outward unit there, and the bay's height
                bool At(float d, out Vector3 c, out Vector3 o, out float y)
                {
                    c = default; o = default; y = 0f;
                    if (!roads.EvaluateAlong(bay.Road, d, out var pos, out var tan, snapTerrain: false)) return false;
                    c = pos; o = new Vector3(tan.Z, 0f, -tan.X).Normalized() * bay.Side; y = pos.Y + surface - BayDrop;
                    return true;
                }
                static Vector3 P(Vector3 c, Vector3 o, float lat, float y) { var q = c + o * lat; q.Y = y; return q; }
                // the asphalt, in strips across the bay, with a station at every corner of the kerb's line
                var ds = new List<float> { bay.A, bay.A + bay.Taper, bay.B - bay.Taper, bay.B };
                for (float d = bay.A + EditorSidewalk.Step; d < bay.B; d += EditorSidewalk.Step) ds.Add(d);
                ds.Sort();
                Vector3 pi = default, po = default; bool have = false;
                foreach (float d in ds)
                {
                    if (!At(d, out var c, out var o, out float y)) continue;
                    var qi = P(c, o, half - BayTuck, y);
                    var qo = P(c, o, half + bay.Extra(d) + BayUnderKerb, y);
                    if (have) { Tri(ground, pi, po, qo, null); Tri(ground, pi, qo, qi, null); }
                    pi = qi; po = qo; have = true;
                }
                // the paint, a dot at a time, each a quad from arc d0 to d1 and from lat0 to lat1 out
                void Dot(float d0, float d1, float lat0, float lat1)
                {
                    if (!At(d0, out var c0, out var o0, out float y0) || !At(d1, out var c1, out var o1, out float y1)) return;
                    var a0 = P(c0, o0, lat0, y0 + MarkLift); var a1 = P(c0, o0, lat1, y0 + MarkLift);
                    var b0 = P(c1, o1, lat0, y1 + MarkLift); var b1 = P(c1, o1, lat1, y1 + MarkLift);
                    Tri(paint, a0, a1, b1, uvWhite); Tri(paint, a0, b1, b0, uvWhite);
                    ParkingPaintPieces++;
                }
                // ⚠ ONE SOLID LINE ACROSS THE BAY AT EACH END OF EACH SPACE, AND NOTHING ALONG IT (strawberry 2026-10-13:
                // "change the parking bays to not have dotted lines at all, only perpendicular solid ones relative to
                // the road's length"): from the carriageway's edge out to just short of the kerb
                float reach = half + bay.Depth - BayKerbGap;
                for (int k = 0; k <= bay.Spaces; k++)
                {
                    float d = bay.First + k * bay.SpaceLength;
                    Dot(d - BayLine * 0.5f, d + BayLine * 0.5f, half + BayEdgeGap, reach);
                }
            }
            int made = 0;
            var gm = ground.Commit();
            if (gm != null && gm.GetSurfaceCount() > 0)
            {
                var mi = new MeshInstance3D { Name = "Parking_Bays", Mesh = gm };
                if (mat != null) mi.MaterialOverride = mat;
                parent.AddChild(mi);
                // drivable: a collider and the road's own surface tag, as the junction slabs have
                var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                body.AddChild(new CollisionShape3D { Shape = gm.CreateTrimeshShape() });
                mi.AddChild(body);
                made++;
            }
            var pm = paint.Commit();
            if (pm != null && pm.GetSurfaceCount() > 0)
            {
                var mi = new MeshInstance3D { Name = "Parking_Bays_Paint", Mesh = pm };
                if (mat != null) mi.MaterialOverride = mat;
                parent.AddChild(mi);
                made++;
            }
            return made;
        }

        // ==============================================================================================
        // THE BOULEVARD (strawberry 2026-10-13: "work on a new road type, with a central concrete median/grass +
        // concrete median. 2 + 2 lanes").
        // ==============================================================================================

        /// <summary>UG_TOWNNOBOULEVARD=1: no boulevard -- every main a plain one, the control.</summary>
        public bool Boulevards = System.Environment.GetEnvironmentVariable("UG_TOWNNOBOULEVARD") != "1";
        /// <summary>The median: how wide, and how far its kerb stands proud of the road.
        /// ⚠ 3 m because the game's cars are 3 m across their tyres (see BayDepth): a median narrower than a car
        /// reads as a painted island at this scale.</summary>
        public float MedianWidth = 3f, MedianKerb = 0.25f;
        /// <summary>How far past its crossing's stop line a median stops, so the crossing and the queue at the
        /// line are all on asphalt; where the boulevard runs through, it stops where the side street's kerb
        /// does, leaving the mouth open to turn across.</summary>
        public float MedianPastStop = 1f;
        /// <summary>Grass down the middle with a concrete kerb round it, or concrete all over. Null: the seed picks.
        /// UG_TOWNMEDIAN=grass|concrete forces it.</summary>
        public bool? MedianGrass = System.Environment.GetEnvironmentVariable("UG_TOWNMEDIAN") switch { "grass" => true, "concrete" => false, _ => null };
        /// <summary>How wide the concrete kerb round a grass median is.</summary>
        public float MedianEdge = 0.4f;
        /// <summary>Which axis's central main line is the boulevard (0 or 1); -1: the seed picks.</summary>
        public int BoulevardAxis = -1;
        /// <summary>The material the boulevard's spans are built from: derived from MainMaterial by MakeBoulevardMaterial,
        /// -1 when there is none.</summary>
        public int BoulevardMaterial = -1;

        int BoulevardAxisNow => BoulevardAxis >= 0 ? BoulevardAxis : (int)(Unit(Mix(((ulong)(uint)Seed << 32) ^ 0xB0A1EUL), 0) * 2f) % 2;
        /// <summary>Is grid line `line` (counted from the centre) of `axis` the boulevard? The CENTRAL main line of one
        /// axis: the town's high street, crossing a plain main at its middle.</summary>
        bool BoulevardLine(int axis, int line) => BoulevardMaterial >= 0 && axis == BoulevardAxisNow && line == CentreMain(axis);
        /// <summary>The material a span is built from.</summary>
        int MatOf(Span sp) => sp.Boulevard && BoulevardMaterial >= 0 ? BoulevardMaterial
                            : sp.Main ? (_plainMain >= 0 ? _plainMain : MainMaterial)
                            : (_plainStreet >= 0 ? _plainStreet : StreetMaterial);

        /// <summary>Derive the boulevard's material from the main road's: as wide again as its median, and drawn with
        /// road_0 split down its yellow centre line and pulled apart by the median -- so every lane keeps the main
        /// road's painted width and the centre line becomes a yellow edge line along each side of the median.</summary>
        void MakeBoulevardMaterial(RoadField roads)
        {
            BoulevardMaterial = -1; _plainMain = _plainStreet = -1; LaneMaterial = -1; ParkingEntranceMaterial = -1;
            // ⭐ THE TOWN'S ROADS ARE PLAIN TARMAC AND ITS LINES ARE PAINTED SEPARATELY (strawberry 2026-10-13: "it
            // might be better to rip the line etc decals and apply them ourselves instead of adjusting the existing
            // spline thing. the spline becomes just the tarmac road, decals are a separate thing"). Baked into the
            // texture a line runs the length of the ribbon whatever is laid over it -- into the crossings, along the
            // parking bays -- and has no edge lines to give. So each road wears its own material's width and surface
            // with no paint on it at all, and BuildRoadLines lays the lines where they belong.
            Image plain = null;
            {
                string p0 = ProjectSettings.GlobalizePath($"res://content/roads/road_{MainMaterial}.png");
                var s0 = new Image();
                if (System.IO.File.Exists(p0) && ContentProvider.LoadOk(s0, p0) && s0.GetWidth() >= 8)
                {
                    var asphalt0 = s0.GetPixel(Mathf.Min(s0.GetWidth() - 1, s0.GetWidth() / 8), 0);
                    plain = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
                    plain.Fill(asphalt0);
                }
            }
            if (PaintOwnLines && plain != null)
            {
                float mh0 = roads.RoadHalfWidthOfMaterial(MainMaterial), sh0 = roads.RoadHalfWidthOfMaterial(StreetMaterial);
                if (mh0 > 0f) _plainMain = roads.AddDerivedMaterial(MainMaterial, mh0, plain, 0f);
                if (sh0 > 0f && StreetMaterial != MainMaterial) _plainStreet = roads.AddDerivedMaterial(StreetMaterial, sh0, plain, 0f);
                else if (StreetMaterial == MainMaterial) _plainStreet = _plainMain;
            }
            // the back lanes' single track: the street's surface, plain whatever the streets wear -- a lane has no paint
            if (BuildBackLanes && plain != null && LaneHalf > 0f) LaneMaterial = roads.AddDerivedMaterial(StreetMaterial, LaneHalf, plain, 0f);
            // ...and a parking lot's way in, the same tarmac two lanes wide
            if (ParkingLots && plain != null && ParkingEntranceHalf > 0f) ParkingEntranceMaterial = roads.AddDerivedMaterial(StreetMaterial, ParkingEntranceHalf, plain, 0f);
            if (!Boulevards || MainMaterial == StreetMaterial || MedianWidth <= 0f) return;
            float half = roads.RoadHalfWidthOfMaterial(MainMaterial);
            if (half <= 0f) return;
            float medianHalf = MedianWidth * 0.5f, wide = half + medianHalf;
            if (PaintOwnLines && plain != null) { BoulevardMaterial = roads.AddDerivedMaterial(MainMaterial, wide, plain, medianHalf); return; }
            Image tex = null;
            string path = ProjectSettings.GlobalizePath($"res://content/roads/road_{MainMaterial}.png");
            var src = new Image();
            if (System.IO.File.Exists(path) && ContentProvider.LoadOk(src, path) && src.GetWidth() >= 8)
            {
                int sw = src.GetWidth(), sh = src.GetHeight();
                int ow = Mathf.Max(8, Mathf.RoundToInt(sw * wide / half));       // the same texels per metre as road_0
                tex = Image.CreateEmpty(ow, sh, false, Image.Format.Rgba8);
                var asphalt = src.GetPixel(Mathf.Min(sw - 1, sw / 8), 0);
                for (int x = 0; x < ow; x++)
                {
                    float lat = ((x + 0.5f) / ow * 2f - 1f) * wide;              // metres from the centre, as RoadField lays u
                    int col;
                    if (Mathf.Abs(lat) <= medianHalf) col = -1;                  // under the median
                    else
                    {
                        float srcLat = lat - Mathf.Sign(lat) * medianHalf;
                        col = Mathf.Clamp(Mathf.FloorToInt((srcLat / half * 0.5f + 0.5f) * sw), 0, sw - 1);
                    }
                    for (int y = 0; y < sh; y++) tex.SetPixel(x, y, col < 0 ? asphalt : src.GetPixel(col, y));
                }
            }
            BoulevardMaterial = roads.AddDerivedMaterial(MainMaterial, wide, tex, medianHalf);
        }

        /// <summary>The planting down a grass median (strawberry 2026-10-13: "place hedges and trees along the grassy
        /// median"): a tree every MedianTreeSpacing metres, scaled down -- the stock maples and birches are 17-21 m giants
        /// with 18 m crowns, and a row of those over a 3 m median roofs both carriageways -- and hedges end to end
        /// between them, kept MedianTreeClear back from each trunk. Nothing within a median's width of its noses.</summary>
        public float MedianTreeSpacing = 15f, MedianTreeScale = 0.6f, MedianTreeClear = 2f;
        public string HedgeProp = "Hedge_0";
        public string[] MedianTrees = { "Maple_0", "Maple_1", "Birch_0", "Birch_1" };

        /// <summary>The town's trees and bushes as FOLIAGE: the world's own harvestable resources, not props (strawberry
        /// 2026-10-19: "make sure the trees and bushes found in towngen use the actual FOLIAGE versions. instead of custom props or
        /// w/e you did"). The planters, verges, medians, parks and parking lots all plant into this list; Main bakes it into a
        /// resource map (ProcIslandFoliage.BakeResourceList) and loads a ResourceField over it, as a generated island does. So
        /// a town tree sways, has a trunk you collide with, can be chopped for its logs and regrows, and a bush drags a car,
        /// like every other one in the world. The names are the resource species (resources.txt) the props were named after.
        /// UG_TOWNTREEPROPS=1: the old props, the control.</summary>
        public readonly List<(string name, Vector3 at, float yawDeg, float scale)> Foliage = new();
        public static bool FoliageAsProps = System.Environment.GetEnvironmentVariable("UG_TOWNTREEPROPS") == "1";
        /// <summary>Plant a tree or a bush with its foot at `ground` -- NOT sunk: ResourceField lets every tree into the ground
        /// itself (TreeSink), which is what SeatedTree imitated for the props. Returns the prop under the control, else null.</summary>
        Node3D PlantFoliage(EditorObjects objs, string name, Vector3 ground, float yawRad, float scale)
        {
            if (FoliageAsProps)
                return objs?.Place(name, ResourceField.IsTree(name) ? SeatedTree(ground, scale) : ground,
                                   new Basis(Vector3.Up, yawRad).Scaled(Vector3.One * scale));
            Foliage.Add((name, ground, Mathf.RadToDeg(yawRad), scale));
            return null;
        }
        /// <summary>What the last PlantMedians planted, for the tests: prop, where, and which median.</summary>
        public readonly List<(string prop, Vector3 at, int road)> MedianPlants = new();

        /// <summary>Plant every grass median; returns the props placed.</summary>
        public int PlantMedians(RoadField roads, EditorObjects objs, List<Node3D> into = null)
        {
            MedianPlants.Clear();
            if (roads == null) return 0;
            int made = 0;
            foreach (var md in Medians)
            {
                if (!md.Grass) continue;
                float surface = roads.RoadSurfaceOffset(md.Road);
                float s0 = md.A + md.Width + 1f, s1 = md.B - md.Width - 1f;
                if (s1 - s0 < 2f) continue;
                ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix((ulong)(uint)md.Road ^ 0x7EE5UL));
                int nTrees = Mathf.Max(1, Mathf.FloorToInt((s1 - s0) / MedianTreeSpacing) + 1);
                var treeAt = new List<float>();
                for (int k = 0; k < nTrees; k++) treeAt.Add(nTrees > 1 ? Mathf.Lerp(s0, s1, (float)k / (nTrees - 1)) : (s0 + s1) * 0.5f);
                bool At(float d, out Vector3 at, out float yaw)
                {
                    at = default; yaw = 0f;
                    if (!roads.EvaluateAlong(md.Road, d, out var pos, out var tan, snapTerrain: false)) return false;
                    at = new Vector3(pos.X, pos.Y + surface + MedianKerb, pos.Z);
                    yaw = ProcIsland.YawForDir(tan.X, tan.Z);
                    return true;
                }
                void Plant(string prop, Vector3 at, Basis b)
                {
                    Node3D node = objs?.Place(prop, at, b);
                    if (objs != null && node == null) return;
                    if (node != null) into?.Add(node);
                    MedianPlants.Add((prop, at, md.Road));
                    made++;
                }
                for (int k = 0; k < treeAt.Count; k++)
                {
                    if (!At(treeAt[k], out var at, out _)) continue;
                    string tree = MedianTrees[(int)(Unit(Mix(h ^ ((ulong)(uint)k * 0x9E37UL)), 0) * MedianTrees.Length) % MedianTrees.Length];
                    // (foliage, not a prop: PlantFoliage)
                    var tnode = PlantFoliage(objs, tree, at, Unit(Mix(h ^ ((ulong)(uint)k * 0x51UL) ^ 0xA5UL), 0) * Mathf.Tau, MedianTreeScale);
                    if (tnode != null) into?.Add(tnode);
                    if (!(FoliageAsProps && objs != null && tnode == null)) { MedianPlants.Add((tree, at, md.Road)); made++; }
                    if (k + 1 >= treeAt.Count) continue;
                    // a hedge between this tree and the next, its pieces butted core to core along the median (PiecesAlong: the
                    // greens' edge-to-edge rule) and stood the way retail stands a hedge (HedgeBasis: it is a Z-up mesh)
                    float g0 = treeAt[k] + MedianTreeClear, g1 = treeAt[k + 1] - MedianTreeClear;
                    if (g1 - g0 < HedgeCoreLength * 0.75f) continue;
                    // (one straight side, tree to tree: the boulevard runs straight, and a hedge is laid in straight sides)
                    var line = new List<Vector3>();
                    if (At(g0, out var lp0, out _)) line.Add(new Vector3(lp0.X, 0f, lp0.Z));
                    if (At(g1, out var lp1, out _)) line.Add(new Vector3(lp1.X, 0f, lp1.Z));
                    ulong hh = h ^ ((ulong)(uint)k * 0x2545UL);
                    foreach (var hp in PiecesAlong(line, false, HedgeCoreDepth, ref hh))
                    {
                        RoadNear(roads, md.Road, hp.At, out float along, out _);
                        if (!At(along, out var hat, out _)) continue;
                        Plant(HedgeProp, new Vector3(hp.At.X, hat.Y, hp.At.Z), HedgeBasis(hp.Dir, hp.Length / HedgeCoreLength, 1f, hp.Flip));
                    }
                }
            }
            if (made > 0) Log.Print($"[towngrid] median planting: {made} props ({MedianPlants.FindAll(p => p.prop == HedgeProp).Count} hedges) down {Medians.FindAll(m => m.Grass).Count} grass medians");
            return made;
        }

        /// <summary>One median: on which road, from and to which arc metres (nose to nose), how wide, and which kind.</summary>
        public sealed class Median { public int Road; public float A, B, Width; public bool Grass; }
        /// <summary>The medians the last BuildMedians laid.</summary>
        public readonly List<Median> Medians = new();

        /// <summary>How far from this end of a boulevard (arc metres) its median may come: MedianPastStop past the
        /// crossing's stop line, or -- where the boulevard runs through -- to where the side street's kerb begins to
        /// turn, which leaves the side street's mouth open across it.</summary>
        float MedianClear(RoadField roads, int road, bool atEnd)
        {
            int j = roads.RoadEndJunction(road, atEnd);
            if (j < 0) return MedianWidth;
            if (!roads.EvaluateAlong(road, atEnd ? roads.RoadLength(road) : 0f, out var tip, out _, snapTerrain: false)) return MedianWidth;
            var node = roads.JunctionPos(j);
            float cut = new Vector2(tip.X - node.X, tip.Z - node.Z).Length();
            float clear;
            if (LayCrossing(roads, j, road, atEnd, out var c)) clear = c.SD1 + c.Half * Mathf.Abs(c.Shear) + MedianPastStop;
            else
            {
                float crossed = 0f;
                foreach (var (o, _) in roads.JunctionEdges(j)) if (o != road) crossed = Mathf.Max(crossed, roads.RoadHalfWidth(o));
                clear = ApproachClear(roads, j, road, atEnd, crossed > 0f ? crossed : roads.RoadHalfWidth(road));
            }
            return Mathf.Max(0f, clear - cut);
        }

        /// <summary>Lay the boulevard's median down every span of it: a raised strip, rounded at each end, of concrete
        /// or of grass inside a concrete kerb. Solid -- a car that leaves its lane hits it. Returns how many.</summary>
        public int BuildMedians(Node3D parent, RoadField roads)
        {
            Medians.Clear();
            if (roads == null || BoulevardMaterial < 0) return 0;
            float half = MedianWidth * 0.5f;
            bool grass = MedianGrass ?? Unit(Mix(((ulong)(uint)Seed << 32) ^ 0x6A55UL), 0) < 0.5f;
            foreach (int r in Built)
            {
                if (!IsBoulevard(roads, r)) continue;
                float L = roads.RoadLength(r);
                float a = MedianClear(roads, r, false), b = L - MedianClear(roads, r, true);
                if (b - a < MedianWidth * 2f) continue;
                Medians.Add(new Median { Road = r, A = a, B = b, Width = MedianWidth, Grass = grass });
            }
            if (parent == null || Medians.Count == 0) return Medians.Count;
            var kerbSt = new SurfaceTool(); kerbSt.Begin(Mesh.PrimitiveType.Triangles);
            var grassSt = new SurfaceTool(); grassSt.Begin(Mesh.PrimitiveType.Triangles);
            // orient by the wanted normal, never by the order given
            void Tri(SurfaceTool st, Vector3 p, Vector3 q, Vector3 t, Vector3 want)
            {
                bool keep = (t - p).Cross(q - p).Dot(want) >= 0f;
                st.SetNormal(want); st.AddVertex(p);
                st.SetNormal(want); st.AddVertex(keep ? q : t);
                st.SetNormal(want); st.AddVertex(keep ? t : q);
            }
            foreach (var md in Medians)
            {
                float surface = roads.RoadSurfaceOffset(md.Road);
                // the outline, nose to nose: down one side, round the far nose, back up the other, round the near one
                var ring = new List<(Vector3 p, Vector3 o)>();          // point at road level, and its outward normal
                bool Frame(float d, out Vector3 c, out Vector3 t, out Vector3 o)
                {
                    c = default; t = default; o = default;
                    if (!roads.EvaluateAlong(md.Road, d, out var pos, out var tan, snapTerrain: false)) return false;
                    t = new Vector3(tan.X, 0f, tan.Z).Normalized(); o = new Vector3(t.Z, 0f, -t.X);
                    c = new Vector3(pos.X, pos.Y + surface, pos.Z);
                    return true;
                }
                float s0 = md.A + half, s1 = md.B - half;                // where each nose's semicircle is centred
                const int noseSteps = 10;
                var side = new List<float>();
                for (float d = s0; d < s1; d += 1.5f) side.Add(d);
                side.Add(s1);
                foreach (float d in side) if (Frame(d, out var c, out _, out var o)) ring.Add((c + o * half, o));
                if (Frame(s1, out var cB, out var tB, out var oB))
                    for (int k = 1; k < noseSteps; k++)
                    {
                        float ang = Mathf.Pi * k / noseSteps;
                        var dir = oB * Mathf.Cos(ang) + tB * Mathf.Sin(ang);
                        ring.Add((cB + dir * half, dir));
                    }
                for (int k = side.Count - 1; k >= 0; k--) if (Frame(side[k], out var c, out _, out var o)) ring.Add((c - o * half, -o));
                if (Frame(s0, out var cA, out var tA, out var oA))
                    for (int k = 1; k < noseSteps; k++)
                    {
                        float ang = Mathf.Pi * k / noseSteps;
                        var dir = -oA * Mathf.Cos(ang) - tA * Mathf.Sin(ang);
                        ring.Add((cA + dir * half, dir));
                    }
                if (ring.Count < 6) continue;
                // the centre of each section, for the top's fan: the median's own centreline
                var mid = new List<Vector3>();
                foreach (var (p, _) in ring) mid.Add(p);
                var top = Vector3.Up * MedianKerb;
                float inset = md.Grass ? Mathf.Min(MedianEdge, half * 0.8f) : 0f;
                // the kerb face all the way round, from a little under the road to the top
                for (int k = 0; k < ring.Count; k++)
                {
                    var (p0, o0) = ring[k]; var (p1, o1) = ring[(k + 1) % ring.Count];
                    var b0 = p0 - Vector3.Up * 0.05f; var b1 = p1 - Vector3.Up * 0.05f;
                    var want = (o0 + o1).Normalized();
                    Tri(kerbSt, b0, p0 + top, p1 + top, want); Tri(kerbSt, b0, p1 + top, b1, want);
                }
                // the top: a strip from each outline point to its point `inset` in (or to the centreline for
                // concrete), concrete in the kerb band and grass inside it
                Vector3 Centre(Vector3 p, Vector3 o) => p - o * half;   // straight in to the centreline...
                for (int k = 0; k < ring.Count; k++)
                {
                    var (p0, o0) = ring[k]; var (p1, o1) = ring[(k + 1) % ring.Count];
                    var a0 = p0 + top; var a1 = p1 + top;
                    // ...for a nose point that is its circle's centre, which is where the radial points
                    var c0 = Centre(p0, o0) + top; var c1 = Centre(p1, o1) + top;
                    if (inset <= 0f)
                    {
                        Tri(kerbSt, a0, a1, c1, Vector3.Up); Tri(kerbSt, a0, c1, c0, Vector3.Up);
                        continue;
                    }
                    var i0 = p0 - o0 * inset + top; var i1 = p1 - o1 * inset + top;
                    Tri(kerbSt, a0, a1, i1, Vector3.Up); Tri(kerbSt, a0, i1, i0, Vector3.Up);
                    var g0 = i0 - Vector3.Up * 0.02f; var g1 = i1 - Vector3.Up * 0.02f;
                    var gc0 = c0 - Vector3.Up * 0.02f; var gc1 = c1 - Vector3.Up * 0.02f;
                    Tri(grassSt, g0, g1, gc1, Vector3.Up); Tri(grassSt, g0, gc1, gc0, Vector3.Up);
                }
            }
            int made = 0;
            var km = kerbSt.Commit();
            if (km != null && km.GetSurfaceCount() > 0)
            {
                var mi = new MeshInstance3D { Name = "Boulevard_Median", Mesh = km };
                parent.AddChild(mi);
                var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                body.AddChild(new CollisionShape3D { Shape = km.CreateTrimeshShape() });
                mi.AddChild(body);
                made++;
            }
            var gm = grassSt.Commit();
            if (gm != null && gm.GetSurfaceCount() > 0)
            {
                var mi = new MeshInstance3D { Name = "Boulevard_Median_Grass", Mesh = gm, MaterialOverride = GroundGrassMaterial() };   // the terrain's grass (TownVerges)
                parent.AddChild(mi);
                made++;
            }
            Log.Print($"[towngrid] boulevard: {Medians.Count} {(Medians.Count > 0 && Medians[0].Grass ? "grass" : "concrete")} median(s), "
                    + $"{MedianWidth:0.0} m wide, kerb {MedianKerb:0.00} m");
            return made;
        }

        // ==============================================================================================
        // THE LINES (strawberry 2026-10-13: "rip the line etc decals and apply them ourselves ... the spline becomes
        // just the tarmac road, decals are a separate thing"; and "should we add yellow lines to the sides of the
        // roads? ... those double lines extend into the crossing").
        // ==============================================================================================

        /// <summary>UG_TOWNTEXLINES=1: the old way -- each road's lines baked into its texture -- as the control.</summary>
        public bool PaintOwnLines = System.Environment.GetEnvironmentVariable("UG_TOWNTEXLINES") != "1";
        int _plainMain = -1, _plainStreet = -1;
        /// <summary>The paint: line width; how far inside the carriageway's edge the edge line runs, and inside the
        /// median's kerb its yellow line; a dash and the gap after it.
        /// ⚠ THE OLD TEXTURES' OWN SIZES, MEASURED OFF THEM (strawberry 2026-10-13: "whyd the dotted center lines become
        /// thinner? match them to the old spline size"). road_0 paints its dashes 4 texels of 256 across 18.4 m and
        /// road_6 8 of 256 across 9.2 m -- 0.2875 m both -- and both run 4 m on, 4 m off (16 of 32 rows of a texture
        /// that repeats every 32 m on road_0, 32 of 64 on road_6). road_0's centre yellow is 8 texels, 0.575 m: the
        /// double yellow keeps that overall width.</summary>
        public float LineWidth = 0.2875f, EdgeLineInset = 0.35f, MedianLineInset = 0.35f, LineDash = 4f, LineGap = 4f;
        /// <summary>A main's centre line: ONE solid yellow, road_0's own 8 texels -- 0.575 m. It was a double yellow (two
        /// 0.2 m lines 0.175 m apart). ⚠ That swap was made as the fix for strawberry's "i still see the duped lines,
        /// they are spaced slightly apart, dead center of the junction" and it was NOT the cause: the junction in that
        /// picture was a boulevard's, which has no yellow at all. The doubling was the dash rhythm restarting at a
        /// through node -- see BuildRoadLines, and the test that walks each dashed line through one.</summary>
        public float CentreYellowWidth = 0.575f;
        /// <summary>Line pieces laid by the last BuildRoadLines.</summary>
        public int RoadLinePieces;
        /// <summary>White edge lines along the kerbs, and a yellow line along each side of a boulevard's median. Both
        /// OFF (strawberry 2026-10-13: "kill the outside lines and the thin double yellow when we have a median"):
        /// the kerb and the median's own kerb already say where the road ends.</summary>
        public bool EdgeLines = false, MedianYellow = false;

        /// <summary>A road's own lines, in metres off its centreline along the spline's (t.z, 0, -t.x) side: where,
        /// how wide, white (0) or yellow (1), dashed or solid, and whether it is an EDGE line (one that follows a kerb).
        /// Yellow only down a main's middle; white for everything else -- a street's dashed centre included, as the
        /// old street texture painted it (strawberry 2026-10-13 chose white over the Canadian yellow for those).
        ///   main      a solid yellow down the middle, a dashed white between each side's two lanes
        ///   boulevard a dashed white between each side's lanes (the median parts the traffic itself)
        ///   street    a dashed white down the middle, as the old street texture had it
        /// (white edges and the median's yellows exist behind EdgeLines and MedianYellow, both off.)</summary>
        public List<(float lat, float width, byte colour, bool dashed, bool edge)> OwnLines(RoadField roads, int road, bool withEdges)
        {
            var t = new List<(float, float, byte, bool, bool)>();
            float h = roads.RoadHalfWidth(road), mh = roads.MedianHalfOfMaterial(roads.RoadMaterialOf(road));
            if (mh > 0f)
                foreach (int s in new[] { -1, 1 })
                {
                    if (MedianYellow) t.Add((s * (mh + MedianLineInset), LineWidth, 1, false, false));
                    t.Add((s * (mh + RoadField.LaneWidth), LineWidth, 0, true, false));
                }
            else if (IsMain(roads, road))
            {
                t.Add((0f, CentreYellowWidth, 1, false, false));
                foreach (int s in new[] { -1, 1 }) t.Add((s * RoadField.LaneWidth, LineWidth, 0, true, false));
            }
            // a two-lane street: a dashed WHITE centre, as road_6 painted it (strawberry 2026-10-13: "change the 2 lane
            // road to use white dotted center lines")
            else t.Add((0f, LineWidth, 0, true, false));
            if (withEdges && EdgeLines)
                foreach (int s in new[] { -1, 1 }) t.Add((s * (h - EdgeLineInset), LineWidth, 0, false, true));
            return t;
        }

        /// <summary>How far in from this end of a road a line at `lat` may come, in arc metres (0: to the end).
        /// ⚠ TO THE STOP LINE AND NO FURTHER where the end has a crossing: the crossing and the box behind it are bare
        /// on a real junction, and a line run on into them is exactly "those double lines extend into the crossing".
        /// The stop line may be slanted, so each line stops where it meets it. Where a main runs through, its centre and
        /// lane lines run on to the node from both halves, their dashes counted from it so the two keep one step
        /// (BuildRoadLines); its EDGE lines stop where the kerb turns away into the side street's mouth.</summary>
        float LineLimit(RoadField roads, int road, bool atEnd, float lat, bool edge)
        {
            int j = roads.RoadEndJunction(road, atEnd);
            if (j < 0) return 0f;
            float L = roads.RoadLength(road);
            if (!roads.EvaluateAlong(road, atEnd ? L : 0f, out var tip, out _, snapTerrain: false)) return 0f;
            var node = roads.JunctionPos(j);
            float cut = new Vector2(tip.X - node.X, tip.Z - node.Z).Length();
            if (_throughEnds.Contains((road, atEnd)))
            {
                if (!edge) return 0f;
                float crossed = 0f;
                foreach (var (o, _) in roads.JunctionEdges(j)) if (o != road) crossed = Mathf.Max(crossed, roads.RoadHalfWidth(o));
                return Mathf.Max(0f, ApproachClear(roads, j, road, atEnd, crossed > 0f ? crossed : roads.RoadHalfWidth(road)) - cut);
            }
            if (CrossingBuilt(road, atEnd) && LayCrossing(roads, j, road, atEnd, out var c))
            {
                // the arm's lateral is to the ARRIVING driver's right: the spline's side at its start, the other at its end
                float armLat = atEnd ? -lat : lat;
                return Mathf.Max(0f, c.SD1 + armLat * c.Shear - cut);
            }
            return 0f;
        }

        /// <summary>Paint every road's lines: its own table (OwnLines), each line from stop line to stop line, an edge
        /// line broken where a parking bay opens off it (the bay's own dotted line takes over), dashes in a fixed
        /// rhythm along the road. One mesh, the crossings' paint: the main road's material and its own white and
        /// yellow texels. Returns the pieces laid.</summary>
        public int BuildRoadLines(Node3D parent, RoadField roads)
        {
            RoadLinePieces = 0;
            if (!PaintOwnLines || parent == null || roads == null || _plainMain < 0) return 0;
            var paving = PavingTops(roads);
            var mat = roads.MaterialOfMaterial(MainMaterial);
            var uvWhite = new Vector2(0.2363f, 0.0664f); var uvYellow = new Vector2(0.4863f, 0.0039f);
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            void Tri(Vector3 a, Vector3 b, Vector3 c, Vector2 uv)
            {
                bool keep = (c - a).Cross(b - a).Y >= 0f;
                st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(a);
                st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(keep ? b : c);
                st.SetNormal(Vector3.Up); st.SetUV(uv); st.AddVertex(keep ? c : b);
            }
            foreach (int r in Built)
            {
                float L = roads.RoadLength(r), surface = roads.RoadSurfaceOffset(r);
                bool Station(float d, float lat, out Vector3 q)
                {
                    q = default;
                    if (!roads.EvaluateAlong(r, d, out var pos, out var tan, snapTerrain: false)) return false;
                    var o = new Vector3(tan.Z, 0f, -tan.X).Normalized();
                    q = pos + o * lat; q.Y = pos.Y + surface + MarkLift;
                    // ⚠ ON THE ROAD'S MESH WHERE IT IS HIGHER THAN ITS SPLINE: a ring every 5 m, flat between, runs over the
                    // curve through a dip -- and on a hill the lines painted on the curve went under it (up to 3.8 cm, the
                    // paint probe), a dash at a time
                    float hi = paving(q).hi;
                    if (!float.IsNaN(hi)) q.Y = Mathf.Max(q.Y, hi + MarkLift);
                    return true;
                }
                void Run(float a, float b, float lat, float w, Vector2 uv)
                {
                    if (b - a < 0.05f) return;
                    int n = Mathf.Max(1, Mathf.CeilToInt((b - a) / 3f));
                    for (int k = 0; k < n; k++)
                    {
                        float d0 = a + (b - a) * k / n, d1 = a + (b - a) * (k + 1) / n;
                        if (!Station(d0, lat - w * 0.5f, out var a0) || !Station(d0, lat + w * 0.5f, out var a1)
                            || !Station(d1, lat - w * 0.5f, out var b0) || !Station(d1, lat + w * 0.5f, out var b1)) continue;
                        Tri(a0, a1, b1, uv); Tri(a0, b1, b0, uv);
                        RoadLinePieces++;
                    }
                }
                // ⚠⚠ THE RHYTHM IS ANCHORED AT A NODE THE ROAD RUNS THROUGH (strawberry 2026-10-13: "i still see the duped
                // lines, they are spaced slightly apart, dead center of the junction"). A through main's two halves meet at
                // the node and each paints up to it -- so with each counting its dashes from its own start, the two rhythms
                // met anywhere at all, and in most boxes that was a dash, a sliver of a gap and another dash. Counted from
                // the node instead, each half stops half a gap short of it and the pair leave one whole gap there. A road
                // running through at both ends fits a whole number of dashes between them, stretched by a hair to fit.
                bool thruA = _throughEnds.Contains((r, false)), thruB = _throughEnds.Contains((r, true));
                float period = LineDash + LineGap, dashLen = LineDash, phase = 0f;
                if (thruA && thruB)
                {
                    float fit = L / (Mathf.Max(1f, Mathf.Round(L / period)) * period);
                    period *= fit; dashLen *= fit; phase = LineGap * fit * 0.5f;
                }
                else if (thruA) phase = LineGap * 0.5f;
                else if (thruB) phase = L - LineGap * 0.5f - dashLen;
                foreach (var (lat, w, colour, dashed, edge) in OwnLines(roads, r, withEdges: true))
                {
                    var uv = colour == 1 ? uvYellow : uvWhite;
                    float s0 = LineLimit(roads, r, false, lat, edge), s1 = L - LineLimit(roads, r, true, lat, edge);
                    if (s1 - s0 < 0.1f) continue;
                    // the stretches this line runs: all of [s0, s1], less any parking bay opening off an edge line
                    var spans = new List<(float a, float b)> { (s0, s1) };
                    if (edge)
                        foreach (var bay in Bays)
                        {
                            if (bay.Road != r || bay.Side != Mathf.Sign(lat)) continue;
                            var next = new List<(float, float)>();
                            foreach (var (a, b) in spans)
                            {
                                if (bay.B <= a || bay.A >= b) { next.Add((a, b)); continue; }
                                if (bay.A > a) next.Add((a, bay.A));
                                if (bay.B < b) next.Add((bay.B, b));
                            }
                            spans = next;
                        }
                    foreach (var (a, b) in spans)
                    {
                        if (!dashed) { Run(a, b, lat, w, uv); continue; }
                        // a fixed rhythm along the road, so a line broken by a bay picks up in step
                        for (float d = phase + Mathf.Floor((a - phase) / period) * period; d < b; d += period)
                            Run(Mathf.Max(a, d), Mathf.Min(b, d + dashLen), lat, w, uv);
                    }
                }
            }
            var mesh = st.Commit();
            if (mesh == null || mesh.GetSurfaceCount() == 0) return 0;
            var mi = new MeshInstance3D { Name = "Road_Lines", Mesh = mesh };
            if (mat != null) mi.MaterialOverride = mat;
            parent.AddChild(mi);
            Log.Print($"[towngrid] lines: {RoadLinePieces} pieces painted over {Built.Count} roads (plain tarmac underneath)");
            return RoadLinePieces;
        }

        /// <summary>How far short of its end a road's pavement stops: clear of the corner, and -- for a pavement
        /// wider than a sharp corner's radius -- far enough back that its own back edge never crosses the next
        /// street's (see WideCornerTrims).</summary>
        float TrimFor(RoadField roads, int road, bool atEnd, float cornerRadius)
        {
            EnsureWideTrims(roads);
            return BaseTrimFor(roads, road, atEnd, cornerRadius) + (_wideTrim.TryGetValue((road, atEnd), out float x) ? x : 0f);
        }

        /// <summary>⚠⚠ WIDE PAVEMENTS CROSS AT A SHARP CORNER BEFORE THEY GET TO IT (strawberry 2026-10-12: "make them
        /// twice as wide everywhere"). Two pavements meeting at a corner have back edges that run on until they meet,
        /// at Q on the corner's bisector, W / tan(θ/2) along each kerb from the corner point: 4.8 m back for a 2 m
        /// pavement at 45 degrees, but 9.7 m for a 4 m one -- further out than the street's own ribbon stops. So
        /// the last few metres of each ribbon lay over the other's, two surfaces at one height, which is z-fighting
        /// at every acute corner in town. Each street's pavement is held back to just past where its back edge
        /// meets the next one; the corner piece runs from there, its back edge already at Q. Measured per corner,
        /// with exactly LaySidewalkCorners' geometry, and kept per road end.</summary>
        readonly Dictionary<(int road, bool atEnd), float> _wideTrim = new();
        RoadField _wideTrimRoads; float _wideTrimWidth = -1f;
        void EnsureWideTrims(RoadField roads)
        {
            if (roads == _wideTrimRoads && _wideTrimWidth == PavementWidth) return;
            _wideTrimRoads = roads; _wideTrimWidth = PavementWidth; _wideTrim.Clear();
            if (roads == null) return;
            float wide = PavementWidth;
            for (int j = 0; j < roads.JunctionCount; j++)
            {
                var edges = roads.JunctionEdges(j);
                if (edges.Count < 2) continue;
                var node = roads.JunctionPos(j);
                var ring = new List<(float ang, Vector3 p, Vector3 tan, int road, bool atEnd)>();
                foreach (var (road, atEnd) in edges)
                {
                    float L = roads.RoadLength(road);
                    float trim = BaseTrimFor(roads, road, atEnd, CornerRadius);
                    float t = atEnd ? Mathf.Max(0f, L - trim) : Mathf.Min(L, trim);
                    if (!roads.EvaluateAlong(road, t, out var pos, out var tan, snapTerrain: false)) continue;
                    var right = new Vector3(tan.Z, 0f, -tan.X).Normalized();
                    float off = Mathf.Max(0.1f, roads.RoadHalfWidth(road) + PavementGap);
                    foreach (int sgn in new[] { 1, -1 })
                    {
                        var e = pos + right * (off * sgn);
                        var d = new Vector3(e.X - node.X, 0f, e.Z - node.Z);
                        if (d.Length() < 0.5f) continue;
                        ring.Add((Mathf.Atan2(d.Z, d.X), e, tan.Normalized(), road, atEnd));
                    }
                }
                ring.Sort((a, b) => a.ang.CompareTo(b.ang));
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    if (a.road == b.road) continue;
                    float sweep = b.ang - a.ang; while (sweep <= 0f) sweep += Mathf.Tau;
                    if (sweep > Mathf.Pi * 1.05f) continue;
                    if (!KerbCorner(a.p, a.tan, b.p, b.tan, out var P)) continue;
                    var dA = new Vector3(a.p.X - P.X, 0f, a.p.Z - P.Z); var dB = new Vector3(b.p.X - P.X, 0f, b.p.Z - P.Z);
                    float lA = dA.Length(), lB = dB.Length();
                    if (lA < 1f || lB < 1f) continue;
                    float theta = Mathf.Acos(Mathf.Clamp(dA.Dot(dB) / (lA * lB), -1f, 1f));
                    if (theta > Mathf.Pi - 0.25f || theta < 0.25f) continue;
                    float half2 = theta * 0.5f;
                    // the corner's own radius, as LaySidewalkCorners will lay it
                    float rad = Mathf.Min(CornerKerbRadius, Mathf.Min(lA, lB) * 0.8f * Mathf.Tan(half2));
                    if (theta < Mathf.Pi * 0.5f - Mathf.DegToRad(0.5f)) rad = Mathf.Min(rad, CornerKerbRadius * Mathf.Tan(half2));
                    if (wide <= rad - 0.01f) continue;                      // its back edge is an arc: nothing crosses
                    float sQ = wide / Mathf.Tan(half2) + 0.05f;
                    // ⚠⚠ AND ROOM TO CLIMB (strawberry 2026-10-13, "follow gentle hills and slopes"). The corner's sections fan
                    // onto ONE back point Q, at one height -- its two streets' mean -- while each street's pavement is level
                    // across and meets Q at its own street's height. On flat ground those are one number. At a 45-degree
                    // crossing on a hill the two streets beside an acute corner stood 0.9 m apart, and each pavement ended
                    // exactly where its back edge reached Q: a 0.9 m cliff between the two pavements, and 0.45 m between
                    // each and the corner (the pavement-join probe). So each street's pavement now stops short of Q by
                    // enough to let the corner's back edge go from that street's height to Q's at a walkable 8% -- the
                    // corner's own level-across stations carry it -- and by nothing at all on flat ground.
                    float climb = Mathf.Abs(a.p.Y - b.p.Y) * 0.5f / 0.08f;
                    foreach (var (end, l) in new[] { ((a.road, a.atEnd), lA), ((b.road, b.atEnd), lB) })
                        if (l < sQ + climb) _wideTrim[end] = Mathf.Max(_wideTrim.TryGetValue(end, out float had) ? had : 0f, sQ + climb - l);
                }
            }
        }

        float BaseTrimFor(RoadField roads, int road, bool atEnd, float cornerRadius)
        {
            int j = roads.RoadEndJunction(road, atEnd);
            if (j < 0) return 0f;                      // a loose end runs to the tip, as it should
            float widest = 0f;
            foreach (var (other, _) in roads.JunctionEdges(j))
                if (other != road) widest = Mathf.Max(widest, roads.RoadHalfWidth(other));
            // ⚠ SUBTRACT THE SETBACK THE ROAD ALREADY HAS. An arm that stops at the kerb is already clear of
            // the intersection, and trimming it by the full clearance again would strand its pavement a whole
            // carriageway short of the corner.
            float have = 0f;
            if (roads.EvaluateAlong(road, atEnd ? roads.RoadLength(road) : 0f, out var tip, out _, snapTerrain: false))
            {
                var jp = roads.JunctionPos(j);
                have = new Vector2(tip.X - jp.X, tip.Z - jp.Z).Length();
            }
            return Mathf.Max(0f, KerbCurveEnd(roads, j, road, atEnd, widest, cornerRadius) - have);
        }
    }
}
