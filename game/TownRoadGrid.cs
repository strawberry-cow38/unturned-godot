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
    public sealed class TownRoadGrid
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
            public float Metres;
            public ulong Checksum;
            public override string ToString() =>
                $"{Roads} roads ({Metres:0} m), {Junctions} junctions = {Crossroads} crossroads / {Tees} tees / "
              + $"{Corners} corners / {DeadEnds} dead ends; {Nodes} nodes ({NodesRejected} rejected), "
              + $"{SpansRejected} spans rejected ({CutAway} eaten by their junctions); {Patched} nodes patched; checksum {Checksum:X16}";
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

        struct Node { public Vector3 P; public bool Ok; public int Junction; }

        /// <summary>A planned street before it is cut: the two nodes it joins, its full polyline, and how much
        /// each end gives up to the junction there.</summary>
        struct Span
        {
            public int AI, AJ, BI, BJ, Axis; public bool Main;
            public List<Vector3> Pts; public float Len, TrimA, TrimB;
        }

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

        /// <summary>Which pair of arms, if any, RUNS THROUGH this node uncut -- the priority road.
        ///
        /// ⚠ EXACTLY ONE PAIR, AND THEY MUST BE COLLINEAR. Two arms on the same axis are two halves of one
        /// line, so leaving both uncut makes one continuous carriageway that meets itself at the node. Doing
        /// it to the crossing pair as well would lay two coplanar ribbons at right angles over each other --
        /// a z-fight, not a fix.
        /// ⚠ A MAIN WINS. With MainEvery = 3 the main lines cross EACH OTHER, so "two main arms and no
        /// third" missed every main crossroads and the backing measured 58%, not 100% -- the first rule was
        /// written for the T-junction I happened to be looking at. Where two mains cross, one has to be the
        /// priority road; where no main is present the bigger-numbered axis is picked, so the choice is
        /// deterministic and a town rebuilds identically.</summary>
        static bool ThroughMain(List<Span> plan, List<(int, bool)> here, out int a, out int b)
        {
            a = b = -1;
            // best pair found so far, preferring a main line over a side street
            bool bestMain = false;
            for (int axis = 0; axis < 2; axis++)
            {
                int p = -1, q = -1, count = 0;
                bool main = false;
                for (int i = 0; i < here.Count; i++)
                {
                    if (plan[here[i].Item1].Axis != axis) continue;
                    count++;
                    if (p < 0) p = i; else if (q < 0) q = i;
                    main |= plan[here[i].Item1].Main;
                }
                if (count != 2 || q < 0) continue;              // a stub or a fan: nothing collinear to join
                if (plan[here[p].Item1].Main != plan[here[q].Item1].Main) continue;   // a main must not fuse to a street
                if (a >= 0 && (bestMain || !main)) continue;    // already have a pair, and this one is no better
                a = p; b = q; bestMain = main;
            }
            return a >= 0;
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
        /// <summary>The node PAINT: each arm's lines carried into the junction, and the stop lines.</summary>
        public readonly List<SDG.Unturned.RoadMark> Marks = new();

        /// <summary>A polyline with `a` metres taken off its start and `b` off its end, cut ON the line rather
        /// than at the nearest existing point -- a 12 m point spacing would otherwise round a 9 m cut to 12 or
        /// to 0, and at a junction that is the difference between meeting the kerb and crossing it.</summary>
        List<Vector3> CutBack(List<Vector3> pts, float a, float b)
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
            return Respace(cut, Mathf.Max(2f, PointStep));
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
        bool MainLine(int line) => (line % Mathf.Max(1, MainEvery)) == 0;

        /// <summary>Which axis OWNS a node: 0 = the row runs through it, 1 = the column does. The through
        /// street keeps its centreline to the node; every other arm stops at that street's kerb.
        /// ⚠ A tie goes to the row, always. Any rule works as long as it is the SAME rule for both spans
        /// arriving on an axis -- a dominance that disagreed with itself would pull one arm back and leave
        /// its opposite number running across the intersection.</summary>
        int DominantAxis(int i, int j)
        {
            bool rowMain = MainLine(j), colMain = MainLine(i);
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

        /// <summary>Lay the grid into <paramref name="roads"/>. <paramref name="heightAt"/> is the ground
        /// sampler (Terrain.Active.SampleHeight in game; a lambda in a test, which is the only way to test the
        /// gates at all -- you cannot arrange a cliff in the real map on demand). <paramref name="siteOk"/>
        /// optionally vetoes ground for reasons the heightfield does not know about.</summary>
        public Report Generate(RoadField roads, Func<float, float, float> heightAt, Func<float, float, bool> siteOk = null)
        {
            var rep = new Report();
            Built.Clear(); _profiles.Clear(); Marks.Clear();
            if (roads == null || heightAt == null || Spacing < 4f || Extent < Spacing) return rep;
            // the kerb return follows the road's width, so everything measured off it does too
            CornerKerbRadius = Mathf.Clamp(roads.RoadHalfWidthOfMaterial(MainMaterial) * CornerKerbFraction, 2f, 9f);

            int n = Mathf.FloorToInt(Extent / Spacing);
            if (n < 1) return rep;
            int w = 2 * n + 1;
            var grid = new Node[w, w];

            // ---- 1. NODES. Each one is a potential junction, so the gates here are the strict ones.
            for (int gi = 0; gi < w; gi++)
                for (int gj = 0; gj < w; gj++)
                {
                    int i = gi - n, j = gj - n;
                    // u depends on i ONLY and v on j ONLY -- that is what keeps every line straight.
                    float u = i * Spacing + (Unit(LineHash(1, i), 0) - 0.5f) * Jitter * Spacing;
                    float v = j * Spacing + (Unit(LineHash(0, j), 0) - 0.5f) * Jitter * Spacing;
                    var flat = ToWorld(u, v, 0f);
                    float y = heightAt(flat.X, flat.Z);
                    var node = new Node { P = new Vector3(flat.X, y, flat.Z), Junction = -1 };

                    bool dry = y > SeaLevel + SeaMargin;
                    // Local steepness from four probes. One probe pair cannot see a ridge running diagonally.
                    const float probe = 8f;
                    float sx = Mathf.Abs(heightAt(flat.X + probe, flat.Z) - heightAt(flat.X - probe, flat.Z)) / (2f * probe);
                    float sz = Mathf.Abs(heightAt(flat.X, flat.Z + probe) - heightAt(flat.X, flat.Z - probe)) / (2f * probe);
                    bool flatEnough = Mathf.Max(sx, sz) <= MaxNodeSlope;
                    node.Ok = dry && flatEnough && (siteOk == null || siteOk(flat.X, flat.Z));
                    if (node.Ok) rep.Nodes++; else rep.NodesRejected++;
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
                    bool main = MainLine(line - n);
                    for (int k = 0; k + 1 < w; k++)
                    {
                        int ai = axis == 0 ? k : line, aj = axis == 0 ? line : k;
                        int bi = axis == 0 ? k + 1 : line, bj = axis == 0 ? line : k + 1;
                        if (!grid[ai, aj].Ok || !grid[bi, bj].Ok) { rep.SpansRejected++; continue; }
                        var pts = SpanPoints(grid[ai, aj].P, grid[bi, bj].P, heightAt, out float len);
                        if (pts == null) { rep.SpansRejected++; continue; }
                        plan.Add(new Span { AI = ai, AJ = aj, BI = bi, BJ = bj, Axis = axis, Main = main,
                                            Pts = pts, Len = len, TrimA = 0f, TrimB = 0f });
                    }
                }

            // ---- 2b. THE NODES. Every span touching a node becomes an arm; tiny's geometry cuts them back
            // past their fillets and hands back the patch that paves the middle. Where it declines -- too few
            // arms, one too short to cut, a shape the fillets cannot close -- the span keeps the simple
            // kerb-line setback, which is worse-looking but never leaves a hole.
            var touching = new Dictionary<(int, int), List<(int span, bool atEnd)>>();
            foreach (var (sp, idx) in Enumerate(plan))
            {
                Add(touching, (sp.AI, sp.AJ), (idx, false));
                Add(touching, (sp.BI, sp.BJ), (idx, true));
            }
            Patches.Clear();
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
                                       roads.RoadHalfWidthOfMaterial(sp.Main ? MainMaterial : StreetMaterial)));
                    }
                    if (SDG.Unturned.InfiniteRoads.TownNode(spokes, node.P.X, -node.P.Z, out var cuts, out var patch, out var marks,
                                                           stopPastEdge: StopPastEdge,
                                                           lines: LineTableFor(roads.RoadHalfWidthOfMaterial(MainMaterial)))
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
                        float back = DominantAxis(kv.Key.Item1 - n, kv.Key.Item2 - n) == sp.Axis
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
                if (!CutMain && ThroughMain(plan, here, out int ta, out int tb))
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
                            double hwT = roads.RoadHalfWidthOfMaterial(spT.Main ? MainMaterial : StreetMaterial);
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
                var pts = CutBack(sp.Pts, Mathf.Max(0f, sp.TrimA - PatchOverlap), Mathf.Max(0f, sp.TrimB - PatchOverlap));
                if (pts == null) { rep.SpansRejected++; rep.CutAway++; continue; }
                int road = roads.AddRoadFromPolyline(pts, sp.Main ? MainMaterial : StreetMaterial,
                                                    loop: false, ignoreTerrain: !FollowTerrain);
                if (road < 0) { rep.SpansRejected++; continue; }
                ref Node a = ref grid[sp.AI, sp.AJ];
                ref Node b = ref grid[sp.BI, sp.BJ];
                Bind(ref a, road, false, pts[0]);
                Bind(ref b, road, true, pts[^1]);
                Built.Add(road); _profiles.Add(pts);
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
            bool mainHere = ax == 0 ? MainLine(j) : MainLine(i);
            return roads.RoadHalfWidthOfMaterial(mainHere ? MainMaterial : StreetMaterial);
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
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float x = Mathf.Lerp(a.X, b.X, t), z = Mathf.Lerp(a.Z, b.Z, t);
                ys[i] = heightAt(x, z);
                // ⚠ THE WATER GATE BELONGS ON THE SPAN, NOT ONLY ON ITS ENDS. Two nodes on dry ground either
                // side of a creek both pass, and the street between them runs along the creek bed. Checking
                // the ends is the cheap test that looks like the real one.
                if (ys[i] <= SeaLevel + SeaMargin) return null;
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
                        foreach (var pp in Patches) best = System.Math.Min(best, pp.DistanceTo(q.X, -q.Z));
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
                            foreach (var pp in Patches) if (pp.DistanceTo(q.X, -q.Z) <= 0.01) { c = true; break; }
                            // ⚠ EVERY ROAD, NOT THE NEAREST -- the third probe in this file to have had the
                            // bug it was built to find. A point at a main's edge inside a junction is often
                            // NEAREST to the narrow side street beside it, which it really is outside, so
                            // the nearest-only test called solid carriageway bare and invented 136 holes.
                            if (!c) c = roads.DeepestRoadOverlap(q, 0.05f, out _, out _);
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
                Log.Print($"[towngrid] junction coverage: {holes} GAPS (bare runs with paving both sides) of {tested} runs"
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
                                    foreach (var pq in Patches) if (pq.DistanceTo(q4.X, -q4.Z) <= 0.001) { c4 = true; break; }
                                    // ⚠ EVERY ROAD, NOT THE NEAREST. The nearest spline to a point in a wide
                                    // road's mouth is often the narrow side street beside it, which the point
                                    // really is outside -- so a nearest-only test calls covered asphalt bare.
                                    if (!c4) c4 = roads.DeepestRoadOverlap(q4, 0f, out _, out _);
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
                    Log.Print($"[towngrid] arm walk (5 cm): {slivers} bare slivers crossing a mouth"
                            + (slivers > 0 ? $", widest {worstSliver * 100f:0} cm at {sliverAt}" : " -- none"));
                }
            }
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
                    foreach (var (r5, _) in e5) if (IsMain(roads, r5)) { mainRoad = r5; mhw = roads.RoadHalfWidth(r5); }
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
                    foreach (var (r6, _) in e6) if (IsMain(roads, r6)) mr = r6;
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
            foreach (var line in _profiles)
            {
                var one = new List<Vector3>(line.Count);
                foreach (var q in line) one.Add(new Vector3(q.X, q.Y - GradeDrop, q.Z));
                sunk.Add(one);
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
            foreach (var pp in Patches)
            {
                if (pp?.Skirts == null || pp.X == null) continue;
                float shift = pfit.TryGetValue(pp, out float pf) ? pf : fallbackLift;
                foreach (var ids in pp.Skirts)
                {
                    if (ids == null || ids.Length < 2) continue;
                    var loop = new List<Vector3>(ids.Length);
                    foreach (int ix in ids)
                        if (ix >= 0 && ix < pp.X.Length)
                            loop.Add(new Vector3((float)pp.X[ix], pp.Y[ix] + shift - GradeDrop, -(float)pp.Z[ix]));
                    if (loop.Count >= 2) sunk.Add(loop);
                }
            }
            terr.ConformToPolylines(sunk, widest + GradeReach, GradeFeather);
            return true;
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
                made += walk.LayAlongRoad(walk, roads, r, EditorSidewalk.Side.Both, null, t0, t1);
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
            LastCorners.Clear();
            int made = 0;
            // ⚠ TALLY WHY A PAIR IS DROPPED. "We lose the 3 way junction's straight side sidewalk in some
            // pics" -- and the count already said so: 12 tees and 4 bends produced only 8 extra pieces. A
            // count of what was BUILT cannot say which rule ate the rest.
            int nSame = 0, nLong = 0, nThrough = 0, nNoCorner = 0, nFlatAngle = 0, nShortRad = 0, nFillet = 0;
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
                    float yA = a.p.Y, yB = b.p.Y;
                    var cp = new List<Vector3>(); var co = new List<Vector3>();
                    void Push(Vector3 at, float y, Vector3 outw) { cp.Add(new Vector3(at.X, y, at.Z)); co.Add(outw); }
                    Push(a.p, yA, Perp(dA, a.p)); Push(T1, yA, Perp(dA, T1));
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
                        Push(at, Mathf.Lerp(yA, yB, (float)k / steps), Radial(at));
                    }
                    Push(T2, yB, Perp(dB, T2)); Push(b.p, yB, Perp(dB, b.p));

                    // the corner IS the crossing, so its kerb is the dropped one all the way round
                    var ck = new List<float>(cp.Count);
                    for (int q = 0; q < cp.Count; q++) ck.Add(CrossingDips ? walk.DipKerb : walk.KerbHeight);
                    var mi = EditorSidewalk.BuildRibbon(cp, co, walk.Width, walk.KerbHeight, walk.Skirt,
                                                        $"Sidewalk_Corner_{j}_{i}",
                                                        Terrain.Active != null && !EditorSidewalk.NoGround
                                                            ? (x, z) => Terrain.Active.SampleHeight(x, z) : null, ck);
                    if (mi == null) continue;
                    LastCorners.Add((node, cp));
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
                    + $"{nSame} same-road, {nLong} long-way, {nNoCorner} no-corner, {nFlatAngle} too-straight, {nShortRad} tiny-radius");
            return made;
        }
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
                    for (int k = 0; k <= n; k++)
                    {
                        float d = from + k * LampSpacing;
                        if (d > to) break;
                        if (!roads.EvaluateAlong(r, d, out var pos, out var tan, snapTerrain: false)) continue;
                        var right = new Vector3(tan.Z, 0f, -tan.X).Normalized();
                        // ⚠ ALTERNATE SIDES. Lamps down one kerb only light one half of a street and look like
                        // a runway; staggering them is what a real street does and halves the count.
                        int side = (k % 2 == 0) ? 1 : -1;
                        var at = pos + right * (side * (half + RoadGapForLights + LampSetback));
                        if (!PushClear(roads, ref at, right * side)) continue;
                        at.Y = Mathf.Max(pos.Y, terr != null ? terr.SampleHeight(at.X, at.Z) : pos.Y) + PavementKerb;
                        // the arm has to reach OVER the carriageway, so the lamp faces across the road
                        float yaw = ProcIsland.YawForDir(-right.X * side, right.Z * side);
                        var basis = SplineTiling.SeatedBasis(terr, at, yaw, 6f, out _);
                        var node = objs.Place(StreetLightProp, at, basis);
                        if (node != null) { into?.Add(node); made++; }
                    }
                }

            // ---- HYDRANTS, sparse, on the pavement (strawberry 2026-10-12: "add hydrants to the sidewalks,
            // somewhat uncommon"). ⚠ DETERMINISTIC, like everything else the grid places: a hydrant that
            // moved between launches would be a prop appearing and disappearing in a saved town.
            if (Hydrants)
                foreach (int r in Built)
                {
                    ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix((ulong)(uint)r ^ 0x4879UL));
                    if (Unit(h, 0) > HydrantChance) continue;
                    float L = roads.RoadLength(r);
                    float t0 = TrimFor(roads, r, false, CornerRadius), t1 = TrimFor(roads, r, true, CornerRadius);
                    if (L - t0 - t1 < 12f) continue;
                    float d = Mathf.Lerp(t0 + 4f, L - t1 - 4f, Unit(h, 16));
                    if (!roads.EvaluateAlong(r, d, out var pos, out var tan, snapTerrain: false)) continue;
                    var side = ((h >> 40) & 1UL) == 0 ? 1f : -1f;
                    var rr = new Vector3(tan.Z, 0f, -tan.X).Normalized();       // symmetric use: either kerb will do
                    var at = pos + rr * (side * (roads.RoadHalfWidth(r) + RoadGapForLights + HydrantSetback));
                    if (!PushClear(roads, ref at, rr * side)) continue;
                    at.Y = Mathf.Max(pos.Y, terr != null ? terr.SampleHeight(at.X, at.Z) : pos.Y) + PavementKerb;
                    var hb = SplineTiling.SeatedBasis(terr, at, Unit(h, 32) * 360f, 6f, out _);
                    var hn = objs.Place(HydrantProp, at, hb);
                    if (hn != null) { into?.Add(hn); made++; }
                }

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
                        // a main road is not stopped: no signal head and no sign on its own approach
                        if (mode == Control.StopSigns && IsMain(roads, road)) continue;
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
                        float back = widest + CornerKerbRadius + SignalBeforeCurve;
                        // ⚠ ON THE DRIVER'S RIGHT, which is the kerb a mast stands on; its arm then reaches
                        // left over the lanes the drivers approaching here are in.
                        var at = node0 - fwd * back + right * (half + RoadGapForLights + 0.6f);
                        if (!PushClear(roads, ref at, right)) continue;      // never stand it in a carriageway
                        at.Y = Mathf.Max(pos.Y, terr != null ? terr.SampleHeight(at.X, at.Z) : pos.Y) + PavementKerb;
                        // ⚠ THE MAST REACHES ACROSS THE ROAD, NOT BACK ALONG IT. Measured off the props:
                        // both run their POLE along local +Z and their ARM along local +Y, which the 270
                        // degree stand turns into world -Z -- so the yaw is choosing where the ARM points.
                        // Aiming it down the approach laid the mast parallel to the kerb with its heads side
                        // on. It belongs perpendicular, over the lanes the approaching driver is in.
                        var armDir = -right;                                   // to the driver's left
                        float yaw = ProcIsland.YawForDir(armDir.X, -armDir.Z);
                        var basis = SplineTiling.SeatedBasis(terr, at, yaw, 6f, out _);
                        if (mode == Control.StopSigns)
                        {
                            // ⚠ A SIGN FACES THE DRIVER, so its face looks back ALONG the approach -- the
                            // opposite of the mast's arm, which reaches across it. And it stands at the kerb,
                            // not out over the road.
                            var signAt = node0 - fwd * (back - 0.5f) + right * (half + RoadGapForLights + 0.7f);
                            if (!PushClear(roads, ref signAt, right)) continue;
                            signAt.Y = Mathf.Max(pos.Y, terr != null ? terr.SampleHeight(signAt.X, signAt.Z) : pos.Y) + PavementKerb;
                            float syaw = ProcIsland.YawForDir(-fwd.X, fwd.Z);
                            var sb = SplineTiling.SeatedBasis(terr, signAt, syaw, 6f, out _);
                            var sn = objs.Place(StopSignProp, signAt, sb);
                            if (sn != null) { into?.Add(sn); made++; }
                            continue;
                        }
                        var tnode = objs.Place(TrafficLightProp, at, basis);
                        if (tnode != null) { into?.Add(tnode); made++; }
                    }
                }
            GD.Print($"[towngrid] prop clearance: {_propTested} sited, {_propPushed} pushed out, " +
                     $"{_propInRoad} LEFT STANDING IN A CARRIAGEWAY{(NoClear ? "  (UG_TOWNNOCLEAR control)" : "")}");
            return made;
        }
        /// <summary>UG_TOWNNOPROPS=1 drops every pole and hydrant: the control for "is that dark shape a
        /// hole in the mesh, or a mast's shadow lying in it".</summary>
        public static bool NoProps = System.Environment.GetEnvironmentVariable("UG_TOWNNOPROPS") == "1";
        public bool StreetLights = !NoProps, TrafficLights = !NoProps;
        /// <summary>Hydrants, and how often a street gets one. Uncommon by default.</summary>
        public bool Hydrants = !NoProps;
        public float HydrantChance = 0.35f;
        public float HydrantSetback = 0.8f;
        public string HydrantProp = "Fire_Hydrant_0";
        public string StreetLightProp = "Street_Light_0", TrafficLightProp = "Traffic_Light_0";
        /// <summary>Metres between lamps; they alternate sides, so one side sees twice this.</summary>
        public float LampSpacing = 32f;
        /// <summary>How far back from the kerb line a lamp's pole stands, onto the pavement.</summary>
        public float LampSetback = 0.9f;
        /// <summary>How far outside the kerb's tangent point -- where the corner's curve begins -- a signal
        /// stands. Small: the mast belongs at the intersection, not up the street.</summary>
        public float SignalBeforeCurve = 1f;
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
        int BuildCrossings(Node3D parent, RoadField roads)
        {
            if (!Crossings || roads == null) return 0;
            var mat = roads.MaterialOfMaterial(MainMaterial);
            var uvWhite = new Vector2(0.2363f, 0.0664f);
            int made = 0;
            for (int j = 0; j < roads.JunctionCount; j++)
            {
                var edges = roads.JunctionEdges(j);
                if (edges.Count < 3) continue;
                var node = roads.JunctionPos(j);
                foreach (var (road, atEnd) in edges)
                {
                    // ⚠ THE MAIN ROAD IS NOT STOPPED AND NOT CROSSED. Traffic on it runs through; its lines
                    // carry on across the box and nothing is painted over them (strawberry: "the bigger roads
                    // should be the 'main' roads, their lines continue through the junction without crossings
                    // on their parts").
                    if (IsMain(roads, road)) continue;
                    float L = roads.RoadLength(road);
                    float arc = atEnd ? Mathf.Max(0f, L - 1f) : Mathf.Min(L, 1f);
                    if (!roads.EvaluateAlong(road, arc, out var pos, out var tan, snapTerrain: false)) continue;
                    var fwd = (atEnd ? tan : -tan).Normalized();         // the way a driver arrives
                    var right = RightOf(fwd);
                    float half = roads.RoadHalfWidth(road);
                    float y = pos.Y + roads.RoadSurfaceOffset(road) + PatchLift + MarkLift;
                    // ⚠⚠ OUTSIDE THE KERB'S CURVE (strawberry: "bring them back before the curve"). The
                    // corner's fillet begins at half + CornerKerbRadius from the node; inside that the kerbs
                    // either side are still turning, so a crossing there meets them at an angle and its
                    // dropped kerbs cannot line up. Clear of the tangent point the two kerbs are straight and
                    // parallel, which is the only place a crossing squares up.
                    float curve = half + CornerKerbRadius;
                    float d0 = curve + CrossingInset, d1 = d0 + CrossingWidth;
                    var st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    void Bar(float latA, float latB)
                    {
                        var p00 = node - fwd * d0 + right * latA; var p01 = node - fwd * d0 + right * latB;
                        var p10 = node - fwd * d1 + right * latA; var p11 = node - fwd * d1 + right * latB;
                        Vector3 V(Vector3 q) => new Vector3(q.X, y, q.Z);
                        void Tri(Vector3 a, Vector3 b, Vector3 c)
                        {
                            bool keep = (c - a).Cross(b - a).Y >= 0f;
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(a);
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(keep ? b : c);
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(keep ? c : b);
                        }
                        Tri(V(p00), V(p01), V(p11)); Tri(V(p00), V(p11), V(p10));
                    }
                    if (Zebra)
                    {
                        float pitch = CrossingBar + CrossingGap;
                        int n = Mathf.Max(1, Mathf.FloorToInt((2f * half - CrossingGap) / pitch));
                        float span = n * pitch - CrossingGap, start = -span * 0.5f;
                        for (int k = 0; k < n; k++) { float a = start + k * pitch; Bar(a, a + CrossingBar); }
                    }
                    else
                    {   // the quieter option: just the two outlines of the walkway
                        Bar(-half + 0.2f, -half + 0.2f + CrossingBar);
                        Bar(half - 0.2f - CrossingBar, half - 0.2f);
                    }
                    // ⚠ AND ITS OWN STOP LINE, because this is now past the junction patch and PaintNode
                    // can only paint within it. A driver stops BEHIND the crossing, so the line goes outside.
                    // Across the approach half only -- we drive on the right.
                    {
                        float sd0 = d1 + StopGap, sd1 = sd0 + StopLineWidth;
                        var q00 = node - fwd * sd0 + right * 0.1f; var q01 = node - fwd * sd0 + right * (half - 0.2f);
                        var q10 = node - fwd * sd1 + right * 0.1f; var q11 = node - fwd * sd1 + right * (half - 0.2f);
                        Vector3 W(Vector3 q) => new Vector3(q.X, y, q.Z);
                        void T2(Vector3 a, Vector3 b, Vector3 c)
                        {
                            bool keep = (c - a).Cross(b - a).Y >= 0f;
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(a);
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(keep ? b : c);
                            st.SetNormal(Vector3.Up); st.SetUV(uvWhite); st.AddVertex(keep ? c : b);
                        }
                        T2(W(q00), W(q01), W(q11)); T2(W(q00), W(q11), W(q10));
                    }
                    var mesh = st.Commit();
                    if (mesh == null || mesh.GetSurfaceCount() == 0) continue;
                    var mi = new MeshInstance3D { Name = $"Crossing_{j}_{made}", Mesh = mesh };
                    if (mat != null) mi.MaterialOverride = mat;
                    parent.AddChild(mi);
                    made++;
                }
            }
            return made;
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
            foreach (var pp in Patches) if (pp.DistanceTo(at.X, -at.Z) <= margin) return false;
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
            for (int i = 0; i < 24; i++)
            {
                if (ClearOfRoads(roads, at, margin)) { if (i > 0) _propPushed++; return true; }
                at += away * 0.5f;
            }
            _propInRoad++;                 // gave up: do not place it, it would stand in the road
            return false;
        }
        public static bool NoClear = System.Environment.GetEnvironmentVariable("UG_TOWNNOCLEAR") == "1";
        int _propTested, _propInRoad, _propPushed;

        /// <summary>How far the pavement stands proud of the road.
        ///
        /// ⚠⚠ ANYTHING ON THE PAVEMENT IS PLACED AT THIS HEIGHT, NOT AT THE ROAD'S. A hydrant seated on the
        /// terrain stands 0.4 m inside the slab it is supposed to be bolted to -- a 1.15 m prop with 0.75 m
        /// showing, which is exactly what "hydrants look sunk into the ground" is. The lamps and the signal
        /// masts stand on the same pavement and had the same error; their poles are tall enough that nobody
        /// had noticed yet.</summary>
        public float PavementKerb = 0.4f;

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
        bool IsMain(RoadField roads, int road) => roads.RoadMaterialOf(road) == MainMaterial && MainMaterial != StreetMaterial;

        /// <summary>The main road's own lane lines, carried straight across the junction box.
        ///
        /// ⚠ ONLY FOR A THROUGH PAIR. Traffic on the main road is not stopped, so its markings do not stop
        /// either -- but that is only meaningful where the main road actually passes THROUGH the node, i.e.
        /// two main arms roughly opposite each other. A single main arm ending at a junction is a tee's stem
        /// and its lines stop like anyone else's.</summary>
        int BuildMainThroughLines(Node3D parent, RoadField roads)
        {
            if (MainMaterial == StreetMaterial) return 0;      // one class of road: nothing is "the main one"
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
                var arms = new List<(Vector3 tip, Vector3 outDir)>();
                foreach (var (road, atEnd) in edges)
                {
                    if (!IsMain(roads, road)) continue;
                    float L = roads.RoadLength(road);
                    if (!roads.EvaluateAlong(road, atEnd ? L : 0f, out var tip, out var tan, snapTerrain: false)) continue;
                    var outDir = new Vector3(tip.X - node.X, 0f, tip.Z - node.Z);
                    if (outDir.Length() < 0.5f) continue;
                    arms.Add((tip, outDir.Normalized()));
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
                    // ⚠ dashes keep a rhythm rather than becoming one long line, or the box reads as solid
                    const float dash = 3f, gap = 6f;
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
        /// <summary>Zebra bars, or just the two outlines of the walkway.</summary>
        public bool Zebra = true;
        /// <summary>How far the crossing sits outside the carriageway's own edge, and how deep it is.</summary>
        public float CrossingInset = 1.2f, CrossingWidth = 3f;
        public float CrossingBar = 0.5f, CrossingGap = 0.5f;
        /// <summary>Gap between the crossing and the stop line behind it, and the line's own depth.</summary>
        public float StopGap = 0.8f, StopLineWidth = 0.5f;
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

        static float TrimFor(RoadField roads, int road, bool atEnd, float cornerRadius)
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
            return Mathf.Max(0f, widest + cornerRadius - have);
        }
    }
}
