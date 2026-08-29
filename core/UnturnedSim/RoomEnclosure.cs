using System;
using System.Collections.Generic;

namespace UnturnedSim
{
    /// <summary>Which walls form a ROOM -- a closed region of the floor plan you could stand inside.
    ///
    /// strawberry_cow asked for "automatic foundations/floors on enclosed rooms" and, separately, for room
    /// designators. Both need the same thing first and neither can be faked: a designator has to know a room
    /// IS a room, and an auto-floor has to know its shape. So the enclosure test lives here, engine-free and
    /// covered at L0, rather than inside the editor where only a running game could exercise it.
    ///
    /// THE PROBLEM IS NOT "ARE THE FOUR WALLS THERE". Enclosure is a property of the whole plan, not of any
    /// wall: drawing a partition across a room turns one room into two without adding a loop anybody drew,
    /// and the partition's ends land in the MIDDLE of the walls it meets, so its endpoints are not endpoints
    /// of anything. Rooms are therefore recovered as the FACES of the planar graph the walls cut the ground
    /// into, which finds the rooms that exist rather than the ones that were drawn.
    ///
    /// Openings are deliberately ignored. A room with a door in it is still a room; a doorway is what makes
    /// it useful. Nothing here reads WallOpening.</summary>
    public static class RoomEnclosure
    {
        /// <summary>How far apart two wall ends may be and still be the same corner.
        ///
        /// This is NOT a floating-point epsilon. It closes corners that MISS -- a wall dragged a little short
        /// of its neighbour, which neither touches it nor crosses it and would otherwise leave the room open.
        /// One wall thickness swallows that while staying far below LatticeStep (3.0), so it can never fuse
        /// two genuinely different corners.
        ///
        /// It is NOT what rescues a corner-solved building, though I wrote that here first and it is worth
        /// recording as wrong. Corner solving runs each wall PAST its neighbour to the outer face, so solved
        /// walls properly CROSS -- and the crossing split below already lands an exact node on the junction,
        /// with no weld involved. Measured: a solved square resolves correctly at a weld of 1e-3. The weld
        /// earns its size on near misses, not on overshoots.</summary>
        public const float DefaultWeld = WallOpenings.DefaultThickness;

        /// <summary>Below this, a "room" is a sliver from two nearly-coincident walls, not a space.</summary>
        public const float MinRoomArea = 1.0f;

        /// <summary>Straightness tolerance for calling an edge axis-aligned, in metres of drift over the
        /// edge's own length -- so a long wall is not failed for the same angle a short one passes.</summary>
        const float AxisTolerance = 0.02f;

        // ---- inputs / outputs ------------------------------------------------------------------------

        /// <summary>One wall's centreline in plan. Centreline, NOT the outer face: two rooms either side of a
        /// partition each own half of it, and building from faces would make their floors overlap by a full
        /// thickness -- two coplanar slabs fighting for the same pixels.</summary>
        public struct PlanSegment
        {
            public float X0, Z0, X1, Z1;
            /// <summary>Index of the wall this came from, so the caller can map a room's edges back to the
            /// surfaces that bound it. -1 when the caller does not care.</summary>
            public int Source;
            public float Thickness;

            public PlanSegment(float x0, float z0, float x1, float z1, int source = -1,
                               float thickness = WallOpenings.DefaultThickness)
            { X0 = x0; Z0 = z0; X1 = x1; Z1 = z1; Source = source; Thickness = thickness; }

            public float Length => MathF.Sqrt((X1 - X0) * (X1 - X0) + (Z1 - Z0) * (Z1 - Z0));
        }

        public struct PlanPoint
        {
            public float X, Z;
            public PlanPoint(float x, float z) { X = x; Z = z; }
        }

        /// <summary>An axis-aligned piece of a room's floor. A WallSurface is a generated BOX -- Rebuild()
        /// has no polygon path -- so an L-shaped room becomes two of these rather than one impossible slab.
        /// They meet edge to edge and never overlap.</summary>
        public struct RoomRect
        {
            public float MinX, MinZ, MaxX, MaxZ;
            public float Width => MaxX - MinX;
            public float Depth => MaxZ - MinZ;
            public float Area => Width * Depth;
        }

        public sealed class RoomEdge
        {
            public PlanPoint A, B;
            public int Source;
            /// <summary>This edge has a room on BOTH sides, so it is an interior partition. The distinction
            /// decides where a floor stops: at the centreline of a shared wall (each room takes its half) but
            /// at the outer face of an exterior one, which is the flush convention AddSlab already uses.
            /// Getting this backwards is not cosmetic -- it either overlaps two slabs or leaves a gap.</summary>
            public bool Shared;
        }

        public sealed class Room
        {
            /// <summary>Corners in order, counter-clockwise in (x, z), first point not repeated at the end.</summary>
            public List<PlanPoint> Outline = new List<PlanPoint>();
            public List<RoomEdge> Edges = new List<RoomEdge>();
            /// <summary>Plan area, always positive.</summary>
            public float Area;
            /// <summary>Every edge runs along X or along Z. Only these can be decomposed into slabs; a room
            /// with a diagonal wall is reported with an empty <see cref="Slabs"/> rather than given a wrong
            /// one, because a bounding box over a diagonal room overhangs the walls it is supposed to fill.</summary>
            public bool IsRectilinear;
            /// <summary>Axis-aligned cover of the room, disjoint, union == the room. Empty when not rectilinear.</summary>
            public List<RoomRect> Slabs = new List<RoomRect>();

            public List<int> SourceWalls()
            {
                var seen = new List<int>();
                foreach (var e in Edges)
                    if (e.Source >= 0 && !seen.Contains(e.Source)) seen.Add(e.Source);
                return seen;
            }
        }

        // ---- entry point -----------------------------------------------------------------------------

        /// <summary>Every enclosed room in a floor plan.</summary>
        /// <param name="walls">Wall centrelines. Order is irrelevant; disconnected buildings are fine.</param>
        /// <param name="weld">Corner tolerance; see <see cref="DefaultWeld"/> before changing it.</param>
        public static List<Room> Find(IReadOnlyList<PlanSegment> walls, float weld = DefaultWeld)
        {
            var rooms = new List<Room>();
            if (walls == null || walls.Count < 3) return rooms;

            var pieces = SplitAtJunctions(walls, weld);
            var nodes = new List<PlanPoint>();
            var edges = BuildGraph(pieces, nodes, weld);
            if (edges.Count < 3) return rooms;

            // How many faces use each undirected edge. Both sides of an interior partition are walked, so a
            // partition is seen twice and an exterior wall once -- but only after the outer face is dropped,
            // which is why this is counted over the INTERIOR faces below rather than over the traversal.
            var faces = TraverseFaces(edges, nodes);

            // ONE place decides what counts as a room, and everything downstream reads that decision.
            //
            // This was written twice -- once to pick the faces and again while building each Room -- and the
            // duplicate quietly disarmed the first: a mutation test that removed the sign check up here still
            // passed, because the copy below caught it. Two statements of one rule is the same defect as the
            // five hand-written tool-clearing branches, and it hides in exactly the same way.
            var kept = new List<(List<int> Face, List<PlanPoint> Ring, float Area)>();
            foreach (var face in faces)
            {
                var ring = PruneBacktracks(FaceRing(face, edges, nodes));
                if (ring.Count < 3) continue;
                // The outer boundary of every connected component comes back CLOCKWISE (negative) under the
                // traversal rule below, and every room comes back counter-clockwise. Sign is the test, not
                // magnitude: a building with exactly one room has an outer face of exactly equal size.
                float area = SignedArea(ring);
                if (area <= MinRoomArea) continue;
                kept.Add((face, ring, area));
            }

            // How many DISTINCT rooms each edge borders -- not how many times it was walked. A spur into a
            // room is walked twice by the one face that contains it, so counting traversals would call a
            // dead-end partition and stop the floor at its centreline as if a second room were behind it.
            var borders = new Dictionary<int, List<int>>();
            for (int f = 0; f < kept.Count; f++)
                foreach (int h in kept[f].Face)
                {
                    int e = h >> 1;
                    if (!borders.TryGetValue(e, out var list)) borders[e] = list = new List<int>();
                    if (!list.Contains(f)) list.Add(f);
                }

            foreach (var (face, ring, area) in kept)
            {
                var room = new Room { Outline = ring, Area = area };

                foreach (int h in face)
                {
                    var ed = edges[h >> 1];
                    borders.TryGetValue(h >> 1, out var seenBy);
                    room.Edges.Add(new RoomEdge
                    {
                        A = nodes[ed.A],
                        B = nodes[ed.B],
                        Source = ed.Source,
                        Shared = seenBy != null && seenBy.Count >= 2,
                    });
                }

                room.IsRectilinear = IsRectilinear(ring);
                if (room.IsRectilinear) room.Slabs = Decompose(ring);
                rooms.Add(room);
            }

            return rooms;
        }

        // ---- 1. split every wall wherever another one meets it -----------------------------------------

        /// <summary>Cut walls at tee and cross junctions so meeting walls share a node.
        ///
        /// Without this the graph is only as connected as the drawing order made it: a partition laid across
        /// a room touches the outer walls in their MIDDLE, contributes no node, and hangs off the plan as a
        /// dangling edge -- so the two rooms it makes read as one. Endpoint-onto-segment covers the tee (much
        /// the commoner case, and robust when the end merely lands near the wall rather than exactly on it);
        /// segment-segment covers a genuine cross.</summary>
        static List<PlanSegment> SplitAtJunctions(IReadOnlyList<PlanSegment> walls, float weld)
        {
            int n = walls.Count;
            var cuts = new List<List<float>>(n);
            for (int i = 0; i < n; i++) cuts.Add(new List<float>());

            for (int i = 0; i < n; i++)
            {
                float li = walls[i].Length;
                if (li <= 1e-4f) continue;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    var b = walls[j];

                    // tee: does either end of j land on the body of i?
                    foreach (var p in new[] { new PlanPoint(b.X0, b.Z0), new PlanPoint(b.X1, b.Z1) })
                    {
                        float t = ProjectParam(walls[i], p, out float dist);
                        if (dist <= weld) AddCut(cuts[i], t, weld / li);
                    }

                    // cross: do the two bodies intersect away from their ends?
                    if (j > i && LineIntersect(walls[i], b, out float ti, out float tj))
                    {
                        AddCut(cuts[i], ti, weld / li);
                        float lj = b.Length;
                        if (lj > 1e-4f) AddCut(cuts[j], tj, weld / lj);
                    }
                }
            }

            var outp = new List<PlanSegment>();
            for (int i = 0; i < n; i++)
            {
                var w = walls[i];
                var ts = cuts[i];
                ts.Add(0f); ts.Add(1f);
                ts.Sort();
                for (int k = 1; k < ts.Count; k++)
                {
                    float t0 = ts[k - 1], t1 = ts[k];
                    if (t1 - t0 < 1e-4f) continue;
                    var a = Lerp(w, t0);
                    var b = Lerp(w, t1);
                    var piece = new PlanSegment(a.X, a.Z, b.X, b.Z, w.Source, w.Thickness);
                    if (piece.Length > weld * 0.5f) outp.Add(piece);
                }
            }
            return outp;
        }

        static void AddCut(List<float> into, float t, float tol)
        {
            if (t <= tol || t >= 1f - tol) return;      // an end, not a cut
            foreach (float e in into) if (MathF.Abs(e - t) < tol) return;
            into.Add(t);
        }

        static PlanPoint Lerp(PlanSegment s, float t)
            => new PlanPoint(s.X0 + (s.X1 - s.X0) * t, s.Z0 + (s.Z1 - s.Z0) * t);

        /// <summary>Where p falls along s (0..1, clamped) and how far off the line it is.</summary>
        static float ProjectParam(PlanSegment s, PlanPoint p, out float dist)
        {
            float dx = s.X1 - s.X0, dz = s.Z1 - s.Z0;
            float len2 = dx * dx + dz * dz;
            if (len2 <= 1e-8f) { dist = float.MaxValue; return 0f; }
            float t = ((p.X - s.X0) * dx + (p.Z - s.Z0) * dz) / len2;
            float ct = Math.Clamp(t, 0f, 1f);
            float qx = s.X0 + dx * ct, qz = s.Z0 + dz * ct;
            dist = MathF.Sqrt((p.X - qx) * (p.X - qx) + (p.Z - qz) * (p.Z - qz));
            return t;
        }

        /// <summary>Proper crossing of two segment BODIES. Ends are excluded -- those are tees, already
        /// handled above, and letting them through here produces a cut at t=0 that splits nothing.</summary>
        static bool LineIntersect(PlanSegment a, PlanSegment b, out float ta, out float tb)
        {
            ta = tb = 0f;
            float ax = a.X1 - a.X0, az = a.Z1 - a.Z0;
            float bx = b.X1 - b.X0, bz = b.Z1 - b.Z0;
            float den = ax * bz - az * bx;
            if (MathF.Abs(den) < 1e-6f) return false;          // parallel or collinear
            float cx = b.X0 - a.X0, cz = b.Z0 - a.Z0;
            ta = (cx * bz - cz * bx) / den;
            tb = (cx * az - cz * ax) / den;
            const float m = 1e-3f;
            return ta > m && ta < 1f - m && tb > m && tb < 1f - m;
        }

        // ---- 2. weld the pieces into a graph -----------------------------------------------------------

        struct Edge { public int A, B; public int Source; }

        /// <summary>Cluster piece ends into nodes, then place each node EXACTLY.
        ///
        /// The cluster centroid is not good enough. Solved corners overshoot each other by half a thickness
        /// in two different directions, so the centroid sits diagonally inside the true corner by ~0.25 --
        /// which would put the auto-floor a quarter of a metre clear of the walls all the way round. Where
        /// two non-parallel walls meet, their centrelines have one exact crossing point and that IS the
        /// corner, overshoot or not, so intersect them and use it.</summary>
        static List<Edge> BuildGraph(List<PlanSegment> pieces, List<PlanPoint> nodes, float weld)
        {
            var edges = new List<Edge>();
            var owners = new List<List<PlanSegment>>();   // pieces incident on each node

            int NodeAt(PlanPoint p, PlanSegment owner)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    float dx = nodes[i].X - p.X, dz = nodes[i].Z - p.Z;
                    if (dx * dx + dz * dz <= weld * weld) { owners[i].Add(owner); return i; }
                }
                nodes.Add(p);
                owners.Add(new List<PlanSegment> { owner });
                return nodes.Count - 1;
            }

            foreach (var s in pieces)
            {
                int a = NodeAt(new PlanPoint(s.X0, s.Z0), s);
                int b = NodeAt(new PlanPoint(s.X1, s.Z1), s);
                if (a == b) continue;                                     // shorter than the weld: not an edge
                bool dup = false;
                foreach (var e in edges)
                    if ((e.A == a && e.B == b) || (e.A == b && e.B == a)) { dup = true; break; }
                if (!dup) edges.Add(new Edge { A = a, B = b, Source = s.Source });
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                if (Sharpen(owners[i], nodes[i], weld, out var exact)) nodes[i] = exact;
            }
            return edges;
        }

        /// <summary>The crossing of the two most divergent centrelines through this cluster.</summary>
        static bool Sharpen(List<PlanSegment> incident, PlanPoint approx, float weld, out PlanPoint exact)
        {
            exact = approx;
            if (incident.Count < 2) return false;
            float best = 0.3f;                       // below ~17 deg apart the crossing is ill-conditioned
            PlanSegment p = default, q = default;
            for (int i = 0; i < incident.Count; i++)
                for (int j = i + 1; j < incident.Count; j++)
                {
                    float li = incident[i].Length, lj = incident[j].Length;
                    if (li <= 1e-4f || lj <= 1e-4f) continue;
                    float ax = (incident[i].X1 - incident[i].X0) / li, az = (incident[i].Z1 - incident[i].Z0) / li;
                    float bx = (incident[j].X1 - incident[j].X0) / lj, bz = (incident[j].Z1 - incident[j].Z0) / lj;
                    float sin = MathF.Abs(ax * bz - az * bx);
                    if (sin > best) { best = sin; p = incident[i]; q = incident[j]; }
                }
            if (best <= 0.3f) return false;

            float px = p.X1 - p.X0, pz = p.Z1 - p.Z0;
            float qx = q.X1 - q.X0, qz = q.Z1 - q.Z0;
            float den = px * qz - pz * qx;
            if (MathF.Abs(den) < 1e-6f) return false;
            float cx = q.X0 - p.X0, cz = q.Z0 - p.Z0;
            float t = (cx * qz - cz * qx) / den;
            var hit = new PlanPoint(p.X0 + px * t, p.Z0 + pz * t);

            // Only trust it if it agrees with the cluster it is refining -- a near-parallel pair can throw
            // the crossing a long way off, and a silently relocated corner is worse than a blunt one.
            float dx2 = hit.X - approx.X, dz2 = hit.Z - approx.Z;
            if (dx2 * dx2 + dz2 * dz2 > weld * weld) return false;
            exact = hit;
            return true;
        }

        // ---- 3. walk the faces -------------------------------------------------------------------------

        /// <summary>Every face of the planar graph, as lists of half-edge ids (edge*2, +1 for the reverse).
        ///
        /// At each arrival the walk takes the neighbour immediately CLOCKWISE from the way it came, which
        /// keeps a face's interior on one side the whole way round; rooms then come out counter-clockwise
        /// and the component's outer boundary clockwise, so the sign of the area separates them. A spur
        /// sticking into a room is walked out and back within the same face and contributes zero area.</summary>
        static List<List<int>> TraverseFaces(List<Edge> edges, List<PlanPoint> nodes)
        {
            int hCount = edges.Count * 2;
            int From(int h) => (h & 1) == 0 ? edges[h >> 1].A : edges[h >> 1].B;
            int To(int h) => (h & 1) == 0 ? edges[h >> 1].B : edges[h >> 1].A;

            var outgoing = new List<List<int>>();
            for (int i = 0; i < nodes.Count; i++) outgoing.Add(new List<int>());
            for (int h = 0; h < hCount; h++) outgoing[From(h)].Add(h);

            float Angle(int h)
            {
                var a = nodes[From(h)]; var b = nodes[To(h)];
                return MathF.Atan2(b.Z - a.Z, b.X - a.X);
            }
            foreach (var list in outgoing) list.Sort((x, y) => Angle(x).CompareTo(Angle(y)));

            var next = new int[hCount];
            for (int h = 0; h < hCount; h++)
            {
                int twin = h ^ 1;
                var ring = outgoing[From(twin)];
                int at = ring.IndexOf(twin);
                next[h] = ring[(at - 1 + ring.Count) % ring.Count];
            }

            var faces = new List<List<int>>();
            var seen = new bool[hCount];
            for (int h = 0; h < hCount; h++)
            {
                if (seen[h]) continue;
                var face = new List<int>();
                int cur = h;
                while (!seen[cur])
                {
                    seen[cur] = true;
                    face.Add(cur);
                    cur = next[cur];
                    if (face.Count > hCount) break;      // malformed graph; drop it rather than spin
                }
                if (face.Count >= 3) faces.Add(face);
            }
            return faces;
        }

        static List<PlanPoint> FaceRing(List<int> face, List<Edge> edges, List<PlanPoint> nodes)
        {
            var ring = new List<PlanPoint>(face.Count);
            foreach (int h in face)
                ring.Add(nodes[(h & 1) == 0 ? edges[h >> 1].A : edges[h >> 1].B]);
            return ring;
        }

        public static float SignedArea(IReadOnlyList<PlanPoint> ring)
        {
            float s = 0f;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                s += a.X * b.Z - b.X * a.Z;
            }
            return s * 0.5f;
        }

        /// <summary>Drop out-and-back spurs so the outline is a simple polygon. They cancel in the area but
        /// they are still in the point list, and the slab decomposition below would read the doubled edge as
        /// a zero-width strip.</summary>
        static List<PlanPoint> PruneBacktracks(List<PlanPoint> ring)
        {
            var pts = new List<PlanPoint>(ring);
            bool changed = true;
            while (changed && pts.Count > 3)
            {
                changed = false;
                for (int i = 0; i < pts.Count; i++)
                {
                    var prev = pts[(i - 1 + pts.Count) % pts.Count];
                    var next = pts[(i + 1) % pts.Count];
                    if (Same(prev, next))
                    {
                        int drop1 = i, drop2 = (i + 1) % pts.Count;
                        if (drop2 < drop1) { pts.RemoveAt(drop1); pts.RemoveAt(drop2); }
                        else { pts.RemoveAt(drop2); pts.RemoveAt(drop1); }
                        changed = true;
                        break;
                    }
                }
            }
            return pts;
        }

        static bool Same(PlanPoint a, PlanPoint b)
            => MathF.Abs(a.X - b.X) < 1e-3f && MathF.Abs(a.Z - b.Z) < 1e-3f;

        // ---- 4. cut a rectilinear room into slabs ------------------------------------------------------

        static bool IsRectilinear(IReadOnlyList<PlanPoint> ring)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                float dx = MathF.Abs(b.X - a.X), dz = MathF.Abs(b.Z - a.Z);
                if (MathF.Min(dx, dz) > AxisTolerance) return false;
            }
            return true;
        }

        /// <summary>Cover a rectilinear room with disjoint axis-aligned boxes.
        ///
        /// Vertical decomposition: between each neighbouring pair of corner X values the room's cross-section
        /// cannot change, so one scan down the middle of the strip gives its spans, and even-odd pairing of
        /// the crossings gives the inside ones. Strips whose spans match are then merged back together, so a
        /// plain rectangular room comes out as ONE slab rather than a row of them.</summary>
        static List<RoomRect> Decompose(IReadOnlyList<PlanPoint> ring)
        {
            var result = new List<RoomRect>();

            var xs = new List<float>();
            foreach (var p in ring)
            {
                bool have = false;
                foreach (float x in xs) if (MathF.Abs(x - p.X) < 1e-3f) { have = true; break; }
                if (!have) xs.Add(p.X);
            }
            xs.Sort();
            if (xs.Count < 2) return result;

            var strips = new List<(float X0, float X1, List<(float Z0, float Z1)> Spans)>();
            for (int k = 1; k < xs.Count; k++)
            {
                float x0 = xs[k - 1], x1 = xs[k];
                if (x1 - x0 < 1e-3f) continue;
                float mid = (x0 + x1) * 0.5f;

                var hits = new List<float>();
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (MathF.Abs(b.Z - a.Z) > AxisTolerance) continue;         // vertical edge: parallel to the scan
                    float lo = MathF.Min(a.X, b.X), hi = MathF.Max(a.X, b.X);
                    if (mid > lo && mid < hi) hits.Add((a.Z + b.Z) * 0.5f);
                }
                hits.Sort();

                var spans = new List<(float, float)>();
                for (int i = 0; i + 1 < hits.Count; i += 2)
                    if (hits[i + 1] - hits[i] > 1e-3f) spans.Add((hits[i], hits[i + 1]));
                if (spans.Count > 0) strips.Add((x0, x1, spans));
            }

            for (int i = 0; i < strips.Count; i++)
            {
                var s = strips[i];
                while (i + 1 < strips.Count && SameSpans(s.Spans, strips[i + 1].Spans)
                       && MathF.Abs(strips[i + 1].X0 - s.X1) < 1e-3f)
                { s.X1 = strips[i + 1].X1; i++; }
                foreach (var (z0, z1) in s.Spans)
                    result.Add(new RoomRect { MinX = s.X0, MaxX = s.X1, MinZ = z0, MaxZ = z1 });
            }
            return result;
        }

        static bool SameSpans(List<(float Z0, float Z1)> a, List<(float Z0, float Z1)> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (MathF.Abs(a[i].Z0 - b[i].Z0) > 1e-3f || MathF.Abs(a[i].Z1 - b[i].Z1) > 1e-3f) return false;
            return true;
        }
    }
}
