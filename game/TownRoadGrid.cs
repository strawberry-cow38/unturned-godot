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
        public int MainMaterial = 0;
        public int StreetMaterial = 0;
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

        static IEnumerable<(Span sp, int idx)> Enumerate(List<Span> l) { for (int i = 0; i < l.Count; i++) yield return (l[i], i); }
        static void Add<TK, TV>(Dictionary<TK, List<TV>> d, TK k, TV v)
        { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<TV>(); l.Add(v); }

        /// <summary>Whether to use tiny's node geometry (fillet-aware cuts + a patch paving the middle) rather
        /// than a plain kerb-line setback. UG_TOWNNOPATCH=1 turns it off, which is the control: the same town
        /// with overlapping arms, for a before/after at one camera.</summary>
        public bool UseNodePatches = System.Environment.GetEnvironmentVariable("UG_TOWNNOPATCH") != "1";
        /// <summary>The junction patches this grid produced, for the caller to build meshes from.</summary>
        public readonly List<SDG.Unturned.RoadPatch> Patches = new();
        /// <summary>The node PAINT: each arm's lines carried into the junction, and the stop lines.</summary>
        public readonly List<SDG.Unturned.RoadMark> Marks = new();

        /// <summary>A polyline with `a` metres taken off its start and `b` off its end, cut ON the line rather
        /// than at the nearest existing point -- a 12 m point spacing would otherwise round a 9 m cut to 12 or
        /// to 0, and at a junction that is the difference between meeting the kerb and crossing it.</summary>
        static List<Vector3> CutBack(List<Vector3> pts, float a, float b)
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
            return cut.Count >= 2 ? cut : null;
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
            // ⚠ THE PLAIN-ASPHALT BAND, which is why a patch does not wear the road texture whole: road_0 is
            // dashes and a yellow centre line running ALONG it, so tiling it over an intersection paints lane
            // markings straight across the junction. RegionStreamer solves this for the infinite world by
            // sampling only u 4/256..56/256 -- the asphalt between the left edge and the first dash -- and
            // MIRRORING it, so it tiles with no seam. Same band, same reason.
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
                    st.SetUV(UV[a]); st.AddVertex(V[a]);
                    st.SetUV(UV[i1]); st.AddVertex(V[i1]);
                    st.SetUV(UV[i2]); st.AddVertex(V[i2]);
                }
                st.GenerateNormals();
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
                    if (roads.RoadEndJunction(r, atEnd) < 0) continue;
                    float L = roads.RoadLength(r);
                    if (!roads.EvaluateAlong(r, atEnd ? L : 0f, out var tip, out var tan, snapTerrain: false)) continue;
                    var right = new Vector3(tan.Z, 0f, -tan.X).Normalized();
                    float hw = roads.RoadHalfWidth(r);
                    for (int k = -2; k <= 2; k++)
                    {
                        var q = tip + right * (hw * k / 2f);
                        double best = double.MaxValue;
                        foreach (var pp in Patches) best = System.Math.Min(best, pp.DistanceTo(q.X, -q.Z));
                        if (best > worstGap) { worstGap = (float)best; gapWhy = $"road {r} {(atEnd ? "end" : "start")} at lateral {hw * k / 2f:0.0} m"; }
                        gapProbes++;
                    }
                }
            PaintPieces = BuildMarkMeshes(parent, roads, fit);   // counted separately: paint is not pavement
            CrossingPieces = BuildCrossings(parent, roads);
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

        /// <summary>A last hair of clearance over the asphalt. ⚠ SMALL ON PURPOSE: this is a SEAM, not an
        /// overlap -- the patch and the road meet edge to edge, so there is nothing to z-fight with and every
        /// millimetre here is a lip you can see. It exists only to keep the join from landing exactly on the
        /// road's own surface where the two disagree by a float's worth.</summary>
        public float PatchLift = 0.002f;
        static float PingPong(float t) { t = Mathf.Abs(t) % 2f; return t <= 1f ? t : 2f - t; }

        /// <summary>Conform the ground to the streets. Call it AFTER Generate and BEFORE LaySidewalks: the
        /// pavement's skirt is cut to the ground it finds, so grading afterwards would leave every ribbon
        /// hanging over its own freshly-lowered verge -- the floating kerb again, by a different route.</summary>
        public bool GradeTerrain(Terrain terr, RoadField roads)
        {
            if (!GradeSite || terr == null || roads == null || _profiles.Count == 0) return false;
            float widest = 0f;
            foreach (int r in Built) widest = Mathf.Max(widest, roads.RoadHalfWidth(r));
            var sunk = new List<List<Vector3>>(_profiles.Count);
            foreach (var line in _profiles)
            {
                var one = new List<Vector3>(line.Count);
                foreach (var q in line) one.Add(new Vector3(q.X, q.Y - GradeDrop, q.Z));
                sunk.Add(one);
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
                    float off = roads.RoadHalfWidth(road) + gap;
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
                    if (sweep > Mathf.Pi * 1.05f) { nLong++; continue; }   // the long way round: not a mouth at all
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
                    if (lA < 1f || lB < 1f) continue;
                    dA /= lA; dB /= lB;
                    float theta = Mathf.Acos(Mathf.Clamp(dA.Dot(dB), -1f, 1f));
                    // ⚠ too straight to round is NOT a reason to leave a hole -- it is the through case.
                    if (theta > Mathf.Pi - 0.25f) { nFlatAngle++; Through(); continue; }
                    if (theta < 0.25f) { nFlatAngle++; continue; }               // a hairpin: no mouth to pave
                    float half2 = theta * 0.5f;
                    float rad = Mathf.Min(CornerKerbRadius, Mathf.Min(lA, lB) * 0.8f * Mathf.Tan(half2));
                    if (rad < 0.4f) { nShortRad++; continue; }
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
                        if (terr != null) at.Y = Mathf.Max(at.Y, terr.SampleHeight(at.X, at.Z));
                        // the arm has to reach OVER the carriageway, so the lamp faces across the road
                        float yaw = ProcIsland.YawForDir(-right.X * side, right.Z * side);
                        var basis = SplineTiling.SeatedBasis(terr, at, yaw, 6f, out _);
                        var node = objs.Place(StreetLightProp, at, basis);
                        if (node != null) { into?.Add(node); made++; }
                    }
                }

            // ---- TRAFFIC SIGNALS, one per approach at a junction that has three or more.
            if (TrafficLights)
                for (int j = 0; j < roads.JunctionCount; j++)
                {
                    var edges = roads.JunctionEdges(j);
                    if (edges.Count < 3) continue;          // a bend or a dead end gets no signals
                    var node0 = roads.JunctionPos(j);
                    foreach (var (road, atEnd) in edges)
                    {
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
                        var right = new Vector3(fwd.Z, 0f, -fwd.X).Normalized();
                        float half = roads.RoadHalfWidth(road);
                        float back = half + CornerKerbRadius + SignalBeforeCurve;
                        // ⚠ ON THE DRIVER'S RIGHT, which is the kerb a mast stands on; its arm then reaches
                        // left over the lanes the drivers approaching here are in.
                        var at = node0 - fwd * back + right * (half + RoadGapForLights + 0.6f);
                        if (terr != null) at.Y = Mathf.Max(at.Y, terr.SampleHeight(at.X, at.Z));
                        // ⚠ THE MAST REACHES ACROSS THE ROAD, NOT BACK ALONG IT. Measured off the props:
                        // both run their POLE along local +Z and their ARM along local +Y, which the 270
                        // degree stand turns into world -Z -- so the yaw is choosing where the ARM points.
                        // Aiming it down the approach laid the mast parallel to the kerb with its heads side
                        // on. It belongs perpendicular, over the lanes the approaching driver is in.
                        var armDir = -right;                                   // to the driver's left
                        float yaw = ProcIsland.YawForDir(armDir.X, -armDir.Z);
                        var basis = SplineTiling.SeatedBasis(terr, at, yaw, 6f, out _);
                        var tnode = objs.Place(TrafficLightProp, at, basis);
                        if (tnode != null) { into?.Add(tnode); made++; }
                    }
                }
            return made;
        }
        public bool StreetLights = true, TrafficLights = true;
        public string StreetLightProp = "Street_Light_0", TrafficLightProp = "Traffic_Light_0";
        /// <summary>Metres between lamps; they alternate sides, so one side sees twice this.</summary>
        public float LampSpacing = 32f;
        /// <summary>How far back from the kerb line a lamp's pole stands, onto the pavement.</summary>
        public float LampSetback = 0.9f;
        /// <summary>How far outside the kerb's tangent point -- where the corner's curve begins -- a signal
        /// stands. Small: the mast belongs at the intersection, not up the street.</summary>
        public float SignalBeforeCurve = 1f;
        float RoadGapForLights = 0.05f;

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
                    float L = roads.RoadLength(road);
                    float arc = atEnd ? Mathf.Max(0f, L - 1f) : Mathf.Min(L, 1f);
                    if (!roads.EvaluateAlong(road, arc, out var pos, out var tan, snapTerrain: false)) continue;
                    var fwd = (atEnd ? tan : -tan).Normalized();         // the way a driver arrives
                    var right = new Vector3(fwd.Z, 0f, -fwd.X).Normalized();
                    float half = roads.RoadHalfWidth(road);
                    float y = pos.Y + roads.RoadSurfaceOffset(road) + PatchLift + MarkLift;
                    float d0 = half + CrossingInset, d1 = d0 + CrossingWidth;
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
        public bool Crossings = true;
        /// <summary>Zebra bars, or just the two outlines of the walkway.</summary>
        public bool Zebra = true;
        /// <summary>How far the crossing sits outside the carriageway's own edge, and how deep it is.</summary>
        public float CrossingInset = 1.2f, CrossingWidth = 3f;
        public float CrossingBar = 0.5f, CrossingGap = 0.5f;
        /// <summary>The stop line goes OUTSIDE the crossing: a driver stops behind it, not on it.</summary>
        public float StopPastEdge => CrossingInset + CrossingWidth + 1.2f;

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

        /// <summary>The radius a kerb turns through at a corner. 6 m is an ordinary urban one.</summary>
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
