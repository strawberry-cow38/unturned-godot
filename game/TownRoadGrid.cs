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
        public float Spacing = 64f;
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
            public float Metres;
            public ulong Checksum;
            public override string ToString() =>
                $"{Roads} roads ({Metres:0} m), {Junctions} junctions = {Crossroads} crossroads / {Tees} tees / "
              + $"{Corners} corners / {DeadEnds} dead ends; {Nodes} nodes ({NodesRejected} rejected), "
              + $"{SpansRejected} spans rejected; checksum {Checksum:X16}";
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
            Built.Clear(); _profiles.Clear();
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

            // ---- 2. SPANS. Rows first, then columns; identical treatment, so the grid has no favoured axis.
            var endsAt = new Dictionary<int, int>();   // junction -> bound ends, for the topology census
            void Bind(ref Node nd, int road, bool atEnd, Vector3 geomAt)
            {
                if (nd.Junction < 0) nd.Junction = roads.AddJunction(nd.P);
                roads.BindRoadEndAt(road, atEnd, nd.Junction, geomAt);   // bound at the NODE, drawn to the KERB
                endsAt[nd.Junction] = endsAt.TryGetValue(nd.Junction, out int c) ? c + 1 : 1;
            }

            for (int axis = 0; axis < 2; axis++)
                for (int line = 0; line < w; line++)
                {
                    bool main = MainLine(line - n);
                    for (int k = 0; k + 1 < w; k++)
                    {
                        int ai = axis == 0 ? k : line, aj = axis == 0 ? line : k;
                        int bi = axis == 0 ? k + 1 : line, bj = axis == 0 ? line : k + 1;
                        ref Node a = ref grid[ai, aj];
                        ref Node b = ref grid[bi, bj];
                        if (!a.Ok || !b.Ok) { rep.SpansRejected++; continue; }

                        // ⭐ MEET AT THE EDGE, NOT THE MIDDLE. The arm that does NOT own a node stops at the
                        // kerb of the street that does; only the through street keeps its centreline to the
                        // node. Running every arm to the centre lays each one's last half-carriageway on top
                        // of the others, which is the merged blob of tarmac at every junction.
                        // ⚠ AND IT TAKES THE NODE'S HEIGHT, not the ground's at the pulled-back point: the
                        // through street's surface is what the arm has to meet, so sampling raw terrain there
                        // would put a step in the road exactly where the two join.
                        var dir = new Vector3(b.P.X - a.P.X, 0f, b.P.Z - a.P.Z).Normalized();
                        float backA = DominantAxis(ai - n, aj - n) == axis ? 0f : HalfAt(roads, ai - n, aj - n);
                        float backB = DominantAxis(bi - n, bj - n) == axis ? 0f : HalfAt(roads, bi - n, bj - n);
                        var startAt = new Vector3(a.P.X + dir.X * backA, a.P.Y, a.P.Z + dir.Z * backA);
                        var endAt = new Vector3(b.P.X - dir.X * backB, b.P.Y, b.P.Z - dir.Z * backB);

                        var pts = SpanPoints(startAt, endAt, heightAt, out float len);
                        if (pts == null) { rep.SpansRejected++; continue; }

                        int road = roads.AddRoadFromPolyline(pts, main ? MainMaterial : StreetMaterial,
                                                            loop: false, ignoreTerrain: !FollowTerrain);
                        if (road < 0) { rep.SpansRejected++; continue; }
                        Bind(ref a, road, false, startAt);
                        Bind(ref b, road, true, endAt);
                        Built.Add(road); _profiles.Add(pts);
                        rep.Roads++; rep.Metres += len;
                        foreach (var p in pts) rep.Checksum = Mix(rep.Checksum ^ (ulong)BitConverter.SingleToInt32Bits(p.X)
                                                        ^ ((ulong)BitConverter.SingleToInt32Bits(p.Y) << 21)
                                                        ^ ((ulong)BitConverter.SingleToInt32Bits(p.Z) << 42));
                    }
                }

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
                made += walk.LayAlongRoad(walk, roads, r, EditorSidewalk.Side.Both, null, t0, t1);
            }
            return made;
        }

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
