using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Seg = UnturnedSim.RoomEnclosure.PlanSegment;

namespace UnturnedSim.Tests
{
    // L0 for seeded buildings (BuildingGen).
    //
    // Every check reads the GENERATED WALLS, not the generator's own plan of them, wherever the two could disagree:
    // which rooms the walls enclose comes from RoomEnclosure, which rooms a door joins comes from where the door sits
    // in its wall, and what a flight lands on comes from the flight's own footprint. The layout is used only to name
    // the rooms -- and WallsEncloseThePlannedRooms pins that the walls enclose exactly the layout's rooms, so that
    // naming is earned. Each check prints its counts beside its verdict: "0 violations" over 0 cases is not a pass.
    [TestFixture]
    public class BuildingGenTests
    {
        const float Cell = BuildingGen.Cell, Storey = BuildingGen.Storey;

        // ---- the sweep: every kind, many seeds, and specs that push on each limit
        static IEnumerable<BuildingGenSpec> Specs()
        {
            foreach (BuildingKind kind in Enum.GetValues(typeof(BuildingKind)))
                for (int seed = 0; seed < 24; seed++)
                {
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 10, MaxFloors = 3, MaxRoomsPerFloor = 4,
                        MinOfKind = new Dictionary<RoomKind, int> { [RoomKind.Bedroom] = 3, [RoomKind.Bathroom] = 3 } };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 6, MaxFloors = 1, MaxRoomsPerFloor = 8 };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 12, MaxFloors = 2, MaxRoomsPerFloor = 4 };   // cannot fit
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinFloors = 3, MaxFloors = 4, MaxRoomsPerFloor = 3, MinRooms = 9,
                        MinOfKind = new Dictionary<RoomKind, int> { [RoomKind.Office] = 2, [RoomKind.Garage] = 1 } };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 7, MaxFloors = 2, MaxRoomsPerFloor = 6 };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 5, MaxFloors = 2, MaxRoomsPerFloor = 8 };   // one floor or two: the roll
                }
        }

        static List<(BuildingGenSpec Spec, BuildingGenResult Res)> _all;
        static List<(BuildingGenSpec Spec, BuildingGenResult Res)> All() => _all ??= Specs().Select(s => (s, BuildingGen.Generate(s))).ToList();

        static int FloorOf(WallPlan w) => (int)MathF.Round(w.Y / Storey);
        static (float X, float Z) Start(WallPlan w) => (w.X, w.Z);
        static (float X, float Z) Dir(WallPlan w) => (MathF.Cos(w.Yaw * MathF.PI / 180f), -MathF.Sin(w.Yaw * MathF.PI / 180f));
        /// <summary>The way a wall's front face looks: its local +Z.</summary>
        static (float X, float Z) Normal(WallPlan w) => (MathF.Sin(w.Yaw * MathF.PI / 180f), MathF.Cos(w.Yaw * MathF.PI / 180f));
        static (float X, float Z) At(WallPlan w, float u) { var d = Dir(w); return (w.X + d.X * u, w.Z + d.Z * u); }
        static IEnumerable<WallPlan> WallsOn(BuildingGenResult r, int f) => r.Walls.Where(w => w.Kind == SurfaceKind.Wall && FloorOf(w) == f);
        static GenRoom RoomAt(BuildingGenResult r, int f, float x, float z) => r.Layout.FirstOrDefault(g => g.Floor == f && g.Holds(x, z));
        static bool OnFloor(BuildingGenResult r, int f, float x, float z)
            => x > r.Footprints[f].X0 * Cell && x < r.Footprints[f].X1 * Cell && z > 0 && z < r.CellsZ * Cell;
        /// <summary>A wall's footprint in plan: its mid-plane grown by half its thickness.</summary>
        static (float X0, float Z0, float X1, float Z1) Box(WallPlan w)
        {
            var p = Start(w); var q = At(w, w.Length); float t = w.Thickness * 0.5f;
            return (MathF.Min(p.X, q.X) - t, MathF.Min(p.Z, q.Z) - t, MathF.Max(p.X, q.X) + t, MathF.Max(p.Z, q.Z) + t);
        }
        static bool Overlap((float X0, float Z0, float X1, float Z1) a, (float X0, float Z0, float X1, float Z1) b, float eps = 0.01f)
            => a.X0 < b.X1 - eps && a.X1 > b.X0 + eps && a.Z0 < b.Z1 - eps && a.Z1 > b.Z0 + eps;
        static (float X0, float Z0, float X1, float Z1) Foot(StairPlan s)
        {
            var (bx, bz) = s.Back; var (rx, rz) = s.Right;
            float ax = s.X, az = s.Z, cx = s.X + bx * s.Run + rx * s.Width, cz = s.Z + bz * s.Run + rz * s.Width;
            return (MathF.Min(ax, cx), MathF.Min(az, cz), MathF.Max(ax, cx), MathF.Max(az, cz));
        }
        static (float X0, float Z0, float X1, float Z1) Grown((float X0, float Z0, float X1, float Z1) a, float m) => (a.X0 - m, a.Z0 - m, a.X1 + m, a.Z1 + m);
        static bool Walkable(WallOpening o) => o.V < 0.05f && o.Height >= 2.5f && o.Width >= 2.4f;
        /// <summary>Every walkable opening on a floor, with the rooms (or null: no room) on its two sides.</summary>
        static IEnumerable<(WallPlan W, WallOpening O, GenRoom A, GenRoom B, (float X, float Z) PA, (float X, float Z) PB)> Doors(BuildingGenResult r, int f)
        {
            foreach (var w in WallsOn(r, f))
                foreach (var o in w.Openings.Where(Walkable))
                {
                    var c = At(w, o.U + o.Width * 0.5f);
                    var n = Normal(w);
                    float d = w.Thickness * 0.5f + 0.3f;
                    (float X, float Z) pa = (c.X + n.X * d, c.Z + n.Z * d), pb = (c.X - n.X * d, c.Z - n.Z * d);
                    yield return (w, o, RoomAt(r, f, pa.X, pa.Z), RoomAt(r, f, pb.X, pb.Z), pa, pb);
                }
        }
        // strawberry's rules, written out HERE rather than borrowed from BuildingGen.AccessOf/MayOpenOff/HoldsStairs: a
        // check that asks the generator which rooms are private passes whatever the generator says.
        // "bedrooms, bathrooms, offices are private" (a closet goes with them); a garage and a store room are dead ends.
        static bool Private(RoomKind k) => k is RoomKind.Bedroom or RoomKind.Bathroom or RoomKind.Office or RoomKind.Closet;
        static bool DeadEnd(RoomKind k) => Private(k) || k is RoomKind.Garage or RoomKind.Storage;
        // the dead ends that may open off another dead end: a closet or en-suite off a bedroom or office, a store off a
        // garage or office
        static bool ChildOf(RoomKind child, RoomKind parent)
            => (parent == RoomKind.Bedroom && child is RoomKind.Closet or RoomKind.Bathroom)
            || (parent == RoomKind.Office && child is RoomKind.Closet or RoomKind.Bathroom or RoomKind.Storage)
            || (parent == RoomKind.Garage && child == RoomKind.Storage);

        static string Name(BuildingGenSpec s) => $"{s.Kind} seed {s.Seed} (min {s.MinRooms}, floors {s.MinFloors}-{s.MaxFloors}, {s.MaxRoomsPerFloor}/floor)";

        // A: the same seed gives the same building, byte for byte, and seeds differ.
        // BREAK IT: seed the RNG from the clock; make it constant (every seed the same plan).
        [Test]
        public void SameSeedSameBuildingAndSeedsDiffer()
        {
            string Text(BuildingGenResult r) => WallSave.Write(r.Walls, r.Kind, r.Rooms) + string.Join(";", r.Stairs.Select(s => $"{s.X},{s.Z},{s.Yaw}")) + r.Palette;
            // the PLAN alone, so a palette pick cannot pass for variety
            string Plan(BuildingGenResult r) => string.Join(" ", r.Layout) + "|" + string.Join(" ", r.Walls.SelectMany(w => w.Openings).Select(o => o.U.ToString("0.0")));
            int same = 0, distinct = 0, plans = 0, specs = 0;
            foreach (BuildingKind kind in Enum.GetValues(typeof(BuildingKind)))
            {
                var seen = new HashSet<string>();
                var seenPlans = new HashSet<string>();
                for (int seed = 0; seed < 20; seed++)
                {
                    var spec = new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 6 };
                    var r = BuildingGen.Generate(spec);
                    string a = Text(r), b = Text(BuildingGen.Generate(spec));
                    if (a == b) same++;
                    seen.Add(a); seenPlans.Add(Plan(r));
                    specs++;
                }
                distinct += seen.Count; plans += seenPlans.Count;
                Assert.That(seenPlans.Count, Is.GreaterThanOrEqualTo(10), $"{kind}: only {seenPlans.Count} distinct plans from 20 seeds");
            }
            TestContext.Progress.WriteLine($"[gen-seed] {same} of {specs} regenerated identically; {distinct} distinct buildings and {plans} distinct plans over {specs} seeds");
            Assert.That(same, Is.EqualTo(specs));
        }

        // B: the room programme meets the spec -- or says in a note exactly where it could not.
        // BREAK IT: skip the MinOfKind loop; put Ground rooms on the emptiest floor; ignore Upper; let GroundOnly go
        // anywhere; stop spreading same-kind rooms; let fillers repeat a one-per-building room; hand the spare length
        // round-robin (the first plans had two dining rooms and an 18 m bathroom).
        [Test]
        public void RoomsMeetTheSpec()
        {
            var bad = new List<string>();
            int cases = 0, rooms = 0, prefs = 0, spreads = 0, noted = 0, serviced = 0;
            foreach (var (spec, res) in All())
            {
                cases++;
                if (res.Notes.Count > 0) noted++;
                var real = res.Layout.Where(g => !g.Circulation).ToList();
                rooms += real.Count;
                string n = Name(spec);
                bool dropped = res.Notes.Any(x => x.Contains("dropped"));
                bool over = res.Notes.Any(x => x.Contains("over its limit"));
                if (res.Floors < Math.Max(1, spec.MinFloors) || res.Floors > spec.MaxFloors) bad.Add($"{n}: {res.Floors} floors");
                if (real.Count < spec.MinRooms && !dropped) bad.Add($"{n}: {real.Count} rooms < {spec.MinRooms} and no note");
                foreach (var kv in spec.MinOfKind)
                    if (real.Count(g => g.Kind == kv.Key) < kv.Value && !dropped) bad.Add($"{n}: {real.Count(g => g.Kind == kv.Key)} {kv.Key} < {kv.Value}");
                foreach (var k in BuildingGen.Programme(spec.Kind).Always)
                    if (!real.Any(g => g.Kind == k)) bad.Add($"{n}: no {k}, which a {spec.Kind} always has");
                var per = Enumerable.Range(0, res.Floors).Select(f => real.Count(g => g.Floor == f)).ToArray();
                for (int f = 0; f < res.Floors; f++)
                {
                    if (per[f] > spec.MaxRoomsPerFloor && !over) bad.Add($"{n}: floor {f} has {per[f]} rooms > {spec.MaxRoomsPerFloor}");
                    if (per[f] == 0) bad.Add($"{n}: floor {f} has no room");
                }
                bool upperFull = Enumerable.Range(1, res.Floors - 1).All(f => per[f] >= spec.MaxRoomsPerFloor);
                // one kitchen (and a house's one living and dining room) unless the spec asked for more
                foreach (var k in real.Select(g => g.Kind).Distinct().Where(k => BuildingGen.OnlyOne(k, spec.Kind)))
                {
                    int have = real.Count(g => g.Kind == k), asked = spec.MinOfKind.TryGetValue(k, out var a) ? a : 1;
                    if (have > Math.Max(1, asked)) bad.Add($"{n}: {have} {k}s, asked for {asked}");
                }
                // small rooms stay small: past a cell of growth only on a floor with no space left for another room --
                // or by one more where its row has only small rooms, since a one-cell leftover is too narrow to be a room
                foreach (var g in real.Where(g => BuildingGen.Service(g.Kind)))
                {
                    int limit = Math.Max(2, (int)Math.Ceiling(BuildingGen.TraitOf(g.Kind, spec.Kind).Cells / (double)(g.Z1 - g.Z0))) + 1;
                    if (!real.Any(o => o.Floor == g.Floor && o.Z0 == g.Z0 && o.Z1 == g.Z1 && !BuildingGen.Service(o.Kind))) limit++;
                    serviced++;
                    if (g.X1 - g.X0 > limit && per[g.Floor] < spec.MaxRoomsPerFloor)
                        bad.Add($"{n}: {g} is {g.X1 - g.X0} cells long (limit {limit}) with floor {g.Floor} at {per[g.Floor]} of {spec.MaxRoomsPerFloor} rooms");
                }
                foreach (var g in real)
                {
                    var p = BuildingGen.TraitOf(g.Kind, spec.Kind).Pref;
                    if (p == BuildingGen.FloorPref.Any) continue;
                    prefs++;
                    if (p == BuildingGen.FloorPref.GroundOnly && g.Floor != 0) bad.Add($"{n}: {g} must be on the ground");
                    if (p == BuildingGen.FloorPref.Ground && g.Floor != 0 && per[0] < spec.MaxRoomsPerFloor) bad.Add($"{n}: {g} upstairs with the ground floor at {per[0]}");
                    if (p == BuildingGen.FloorPref.Upper && g.Floor == 0 && res.Floors > 1 && !upperFull) bad.Add($"{n}: {g} on the ground with room upstairs");
                }
                // same-kind rooms with no preference spread over the floors: no floor has two while another has none
                foreach (var k in real.Where(g => BuildingGen.TraitOf(g.Kind, spec.Kind).Pref == BuildingGen.FloorPref.Any).Select(g => g.Kind).Distinct())
                {
                    var c = Enumerable.Range(0, res.Floors).Select(f => real.Count(g => g.Kind == k && g.Floor == f)).ToArray();
                    if (c.Max() - c.Min() < 2) { spreads++; continue; }
                    int lo = Array.IndexOf(c, c.Min());
                    if (per[lo] < spec.MaxRoomsPerFloor) bad.Add($"{n}: {k} per floor {string.Join("/", c)} with floor {lo} not full");
                    else spreads++;
                }
            }
            TestContext.Progress.WriteLine($"[gen-spec] {cases} buildings, {rooms} rooms, {prefs} floor preferences, {spreads} same-kind spreads, {serviced} service-room widths checked; {noted} carry notes; {bad.Count} violations{(bad.Count > 0 ? ": " + bad[0] : "")}");
            // what the notes SAY, by shape and by spec: a note is a sanctioned exception, so a new kind of one is news
            foreach (var g in All().SelectMany(x => x.Res.Notes.Select(n => (shape: string.Join(" ", n.Split(' ').Where(w => w.Length > 0 && !char.IsDigit(w[0])).Take(6)), spec: $"min {x.Spec.MinRooms} floors {x.Spec.MinFloors}-{x.Spec.MaxFloors} {x.Spec.MaxRoomsPerFloor}/floor")))
                                   .GroupBy(x => x).OrderByDescending(g => g.Count()))
                TestContext.Progress.WriteLine($"[gen-spec]   {g.Count()} x \"{g.Key.shape}...\" ({g.Key.spec})");
            Assert.That(cases, Is.GreaterThan(500));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // C: the walls enclose exactly the planned rooms -- found by RoomEnclosure from the wall centrelines, and each
        // designation resolves to the room of its kind with the planned area (a hall's landing included).
        // BREAK IT: leave out the partitions between rooms; wall a hall off from its own landing.
        [Test]
        public void WallsEncloseThePlannedRooms()
        {
            var bad = new List<string>();
            int floors = 0, found = 0;
            foreach (var (spec, res) in All())
                for (int f = 0; f < res.Floors; f++)
                {
                    floors++;
                    var plan = WallsOn(res, f).Select(w => { var b = At(w, w.Length); return new Seg(w.X, w.Z, b.X, b.Z, -1, w.Thickness); }).ToList();
                    var rooms = RoomEnclosure.Find(plan);
                    var want = res.Layout.Where(g => g.Floor == f).ToList();
                    found += rooms.Count;
                    if (rooms.Count != want.Count) { bad.Add($"{Name(spec)} floor {f}: {rooms.Count} enclosed, {want.Count} planned"); continue; }
                    RoomDesignations.Resolve(rooms, res.Rooms.Where(d => d.Floor == f).ToList(), out var byRoom, out var orphans);
                    if (orphans.Count > 0) bad.Add($"{Name(spec)} floor {f}: {orphans.Count} designations match no room");
                    foreach (var g in want)
                    {
                        int i = rooms.FindIndex(r => RoomDesignations.Contains(r.Outline, (g.X0 + g.X1) * 0.5f * Cell, (g.Z0 + g.Z1) * 0.5f * Cell));
                        if (i < 0) { bad.Add($"{Name(spec)}: {g} not enclosed"); continue; }
                        if (byRoom[i] != g.Kind) bad.Add($"{Name(spec)}: {g} resolves as {byRoom[i]}");
                        if (MathF.Abs(rooms[i].Area - g.Cells * Cell * Cell) > 0.5f) bad.Add($"{Name(spec)}: {g} encloses {rooms[i].Area:0.0} m2, planned {g.Cells * Cell * Cell}");
                    }
                }
            TestContext.Progress.WriteLine($"[gen-enclose] {floors} floors, {found} rooms enclosed by the walls; {bad.Count} mismatches{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(found, Is.GreaterThan(1000));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // D: every room can be walked to from outside without walking THROUGH a private room or a store -- through the
        // doors as placed and up the flights -- and a dead end has one way in (its children aside: a closet or en-suite
        // off a bedroom, a store off a garage). strawberry: "bedrooms, bathrooms, offices are private".
        // BREAK IT: no door into the stair room; no front door; let a dead end open onto two rooms it is not the parent
        // of; let the walk-through tree go through a bedroom.
        [Test]
        public void EveryRoomReachedAndPrivateRoomsAreDeadEnds()
        {
            var bad = new List<string>();
            int doors = 0, flights = 0, reached = 0, total = 0, deadEnds = 0, suites = 0;
            foreach (var (spec, res) in All())
            {
                var edges = new Dictionary<GenRoom, HashSet<GenRoom>>();
                var outside = new GenRoom { Kind = RoomKind.Unassigned, Floor = -1 };
                void Link(GenRoom a, GenRoom b) { if (a == null || b == null || a == b) return;
                    (edges.TryGetValue(a, out var s) ? s : edges[a] = new HashSet<GenRoom>()).Add(b);
                    (edges.TryGetValue(b, out var t) ? t : edges[b] = new HashSet<GenRoom>()).Add(a); }
                for (int f = 0; f < res.Floors; f++)
                    foreach (var d in Doors(res, f))
                    {
                        doors++;
                        GenRoom A = d.A ?? (f == 0 && !OnFloor(res, f, d.PA.X, d.PA.Z) ? outside : null);
                        GenRoom B = d.B ?? (f == 0 && !OnFloor(res, f, d.PB.X, d.PB.Z) ? outside : null);
                        if (A == null || B == null) { bad.Add($"{Name(spec)} floor {f}: a door opens onto nothing at ({d.PA.X:0.0},{d.PA.Z:0.0})"); continue; }
                        Link(A, B);
                    }
                foreach (var s in res.Stairs)
                {
                    flights++;
                    var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                    float mx = s.X + rx * s.Width * 0.5f, mz = s.Z + rz * s.Width * 0.5f;
                    Link(RoomAt(res, s.Floor, mx - bx * 0.3f, mz - bz * 0.3f), RoomAt(res, s.Floor + 1, mx + bx * (s.Run + 0.3f), mz + bz * (s.Run + 0.3f)));
                }
                // walk in: on through rooms you may walk through, into a dead end, and from a dead end only to its own children
                bool Child(GenRoom c, GenRoom p) => DeadEnd(p.Kind) && ChildOf(c.Kind, p.Kind);
                var seen = new HashSet<GenRoom> { outside };
                var q = new Queue<GenRoom>(); q.Enqueue(outside);
                while (q.Count > 0)
                {
                    var a = q.Dequeue();
                    if (!edges.TryGetValue(a, out var nb)) continue;
                    foreach (var b in nb)
                    {
                        if (seen.Contains(b)) continue;
                        if (!(a == outside || !DeadEnd(a.Kind) || Child(b, a))) continue;
                        seen.Add(b); q.Enqueue(b);
                    }
                }
                foreach (var g in res.Layout) { total++; if (seen.Contains(g)) reached++; else bad.Add($"{Name(spec)}: {g} cannot be reached without walking through a private room"); }
                // a dead end: exactly one door to a room that is not its child (outside does not count: a garage's vehicle door)
                foreach (var g in res.Layout.Where(g => DeadEnd(g.Kind)))
                {
                    deadEnds++;
                    var ways = edges.TryGetValue(g, out var nb) ? nb.Where(o => o != outside && !Child(o, g)).ToList() : new List<GenRoom>();
                    if (ways.Count != 1) bad.Add($"{Name(spec)}: {g} has {ways.Count} ways in ({string.Join(", ", ways)})");
                    if (ways.Count == 1 && DeadEnd(ways[0].Kind)) suites++;
                }
            }
            TestContext.Progress.WriteLine($"[gen-reach] {doors} doors, {flights} flights; {reached} of {total} rooms reached without walking through a private room; {deadEnds} dead ends ({suites} off a bedroom/garage/office){(bad.Count > 0 ? "; first: " + bad[0] : "")}");
            Assert.That(flights, Is.GreaterThan(200));
            Assert.That(suites, Is.GreaterThan(0), "no closet or en-suite ever opens off a bedroom");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // E: each flight stands in ONE room you walk through (never a bedroom, bathroom, office -- nor a store room or
        // garage, which would make the floor above reachable only through a dead end), clear of every
        // wall; the hole it needs in the floor above is in one such room up there and clear of every wall; its treads are
        // not over the hole the flight below cut; and in the stair room on every floor, every door, the foot of the
        // flight up and the top of the flight from below are joined by floor you can stand on (0.1 m grid).
        // BREAK IT: the flight away from the wall (hole under it); both flights in one row (treads over the hole); a door
        // allowed into the bay (onto the steps); the stairs in any room; a three-cell bay (half a tile at each end).
        [Test]
        public void StairsStandInOpenRoomsAndLandOnFloor()
        {
            var bad = new List<string>();
            int flights = 0, walked = 0, tiles = 0;
            const float M = WallOpenings.StairHoleMargin;
            var hostKinds = new Dictionary<RoomKind, int>();
            foreach (var (spec, res) in All())
            {
                if (res.Floors < 2) continue;
                GenRoom Holder((float X0, float Z0, float X1, float Z1) a, int f)
                {
                    var c = new[] { (a.X0 + 0.02f, a.Z0 + 0.02f), (a.X1 - 0.02f, a.Z0 + 0.02f), (a.X0 + 0.02f, a.Z1 - 0.02f), (a.X1 - 0.02f, a.Z1 - 0.02f) }
                                .Select(p => RoomAt(res, f, p.Item1, p.Item2)).Distinct().ToList();
                    return c.Count == 1 ? c[0] : null;
                }
                foreach (var s in res.Stairs)
                {
                    flights++;
                    var fp = Foot(s);
                    var hole = Grown(fp, M);
                    var low = Holder(fp, s.Floor);
                    var high = Holder(hole, s.Floor + 1);
                    if (low == null || DeadEnd(low.Kind)) bad.Add($"{Name(spec)}: flight {s.Floor} stands in {low?.ToString() ?? "more than one room"}");
                    else hostKinds[low.Kind] = hostKinds.GetValueOrDefault(low.Kind) + 1;
                    if (high == null || DeadEnd(high.Kind)) bad.Add($"{Name(spec)}: flight {s.Floor}'s hole is in {high?.ToString() ?? "more than one room"}");
                    foreach (var w in WallsOn(res, s.Floor)) if (Overlap(fp, Box(w))) bad.Add($"{Name(spec)}: flight {s.Floor} runs into a wall at yaw {w.Yaw}");
                    foreach (var w in WallsOn(res, s.Floor + 1)) if (Overlap(hole, Box(w))) bad.Add($"{Name(spec)}: flight {s.Floor}'s hole runs under a wall on floor {s.Floor + 1} (yaw {w.Yaw}, thick {w.Thickness})");
                    foreach (var b in res.Stairs.Where(b => b.Floor == s.Floor - 1))
                        if (Overlap(fp, Grown(Foot(b), M), 0f)) bad.Add($"{Name(spec)}: flight {s.Floor} stands over the hole flight {b.Floor} cut");
                    // a full tile clear at the foot and on the landing above (strawberry: "stairs should want 1 full tile at
                    // the foot and 1 tile on the landing above"): the 3 m before the foot and past the top, the flight's
                    // width wide, all floor of the same room -- no wall, no flight, no hole in it but the top's own margin
                    var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                    (float, float, float, float) Tile(float ox, float oz, float dir)
                    {
                        float ex = ox + bx * Cell * dir + rx * s.Width, ez = oz + bz * Cell * dir + rz * s.Width;
                        return (MathF.Min(ox, ex), MathF.Min(oz, ez), MathF.Max(ox, ex), MathF.Max(oz, ez));
                    }
                    var footTile = Tile(s.X, s.Z, -1f);
                    var topTile = Tile(s.X + bx * s.Run, s.Z + bz * s.Run, +1f);
                    foreach (var (tile, f, what, room) in new[] { (footTile, s.Floor, "foot", low), (topTile, s.Floor + 1, "landing", high) })
                    {
                        tiles++;
                        var inner = Grown(tile, -0.4f);   // a wall's half thickness may stand at the tile's far edge
                        if (Holder(inner, f) != room) bad.Add($"{Name(spec)}: flight {s.Floor}'s {what} tile is not all {room}");
                        foreach (var w in WallsOn(res, f)) if (Overlap(inner, Box(w))) bad.Add($"{Name(spec)}: a wall stands in flight {s.Floor}'s {what} tile");
                        foreach (var o in res.Stairs.Where(o => o.Floor == f)) if (Overlap(Grown(tile, -0.01f), Foot(o))) bad.Add($"{Name(spec)}: flight {o.Floor} is in flight {s.Floor}'s {what} tile");
                        foreach (var o in res.Stairs.Where(o => o.Floor == f - 1 && !(o.Floor == s.Floor && what == "landing"))) if (Overlap(Grown(tile, -0.01f), Grown(Foot(o), M))) bad.Add($"{Name(spec)}: flight {o.Floor}'s hole is in flight {s.Floor}'s {what} tile");
                        if (what == "landing" && Overlap(Grown(tile, -M - 0.01f), Grown(Foot(s), M))) bad.Add($"{Name(spec)}: flight {s.Floor}'s own hole runs into its landing tile");
                    }
                }
                // walk each floor's stair room on a 0.1 m grid
                for (int f = 0; f < res.Floors; f++)
                {
                    var up = res.Stairs.Where(s => s.Floor == f).ToList();
                    var down = res.Stairs.Where(s => s.Floor == f - 1).ToList();
                    var host = up.Count > 0 ? Holder(Foot(up[0]), f) : Holder(Grown(Foot(down[0]), M), f);
                    if (host == null) continue;   // reported above
                    float gx0 = host.Rects.Min(r => r.X0) * Cell, gz0 = host.Rects.Min(r => r.Z0) * Cell;
                    float gx1 = host.Rects.Max(r => r.X1) * Cell, gz1 = host.Rects.Max(r => r.Z1) * Cell;
                    int nx = (int)((gx1 - gx0) / 0.1f), nz = (int)((gz1 - gz0) / 0.1f);
                    var blocks = up.Select(Foot).Concat(down.Select(s => Grown(Foot(s), M))).Concat(WallsOn(res, f).Select(Box)).ToList();
                    var open = new bool[nx, nz];
                    for (int i = 0; i < nx; i++)
                        for (int j = 0; j < nz; j++)
                        {
                            float x = gx0 + (i + 0.5f) * 0.1f, z = gz0 + (j + 0.5f) * 0.1f;
                            open[i, j] = host.Holds(x, z) && !blocks.Any(b => x > b.X0 && x < b.X1 && z > b.Z0 && z < b.Z1);
                        }
                    (int, int) G(float x, float z) => (Math.Clamp((int)((x - gx0) / 0.1f), 0, nx - 1), Math.Clamp((int)((z - gz0) / 0.1f), 0, nz - 1));
                    var targets = new List<(string, float, float)>();
                    foreach (var d in Doors(res, f))
                    {
                        if (d.A == host) targets.Add(($"door at ({d.PA.X:0.0},{d.PA.Z:0.0})", d.PA.X, d.PA.Z));
                        if (d.B == host) targets.Add(($"door at ({d.PB.X:0.0},{d.PB.Z:0.0})", d.PB.X, d.PB.Z));
                    }
                    if (targets.Count == 0) { bad.Add($"{Name(spec)} floor {f}: the stair room {host} has no door"); continue; }
                    foreach (var s in up) { var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                        targets.Add(($"foot of flight {f}", s.X + rx * s.Width * 0.5f - bx * 0.3f, s.Z + rz * s.Width * 0.5f - bz * 0.3f)); }
                    foreach (var s in down) { var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                        targets.Add(($"top of flight {f - 1}", s.X + rx * s.Width * 0.5f + bx * (s.Run + M + 0.2f), s.Z + rz * s.Width * 0.5f + bz * (s.Run + M + 0.2f))); }
                    var start = G(targets[0].Item2, targets[0].Item3);
                    var seen = new bool[nx, nz];
                    var fill = new Queue<(int, int)>();
                    if (open[start.Item1, start.Item2]) { seen[start.Item1, start.Item2] = true; fill.Enqueue(start); }
                    while (fill.Count > 0)
                    {
                        var (i, j) = fill.Dequeue();
                        foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int a = i + di, b = j + dj;
                            if (a < 0 || b < 0 || a >= nx || b >= nz || seen[a, b] || !open[a, b]) continue;
                            seen[a, b] = true; fill.Enqueue((a, b));
                        }
                    }
                    walked++;
                    foreach (var (what, x, z) in targets)
                    {
                        var (i, j) = G(x, z);
                        if (!open[i, j]) bad.Add($"{Name(spec)} floor {f} {host}: the {what} is not floor you can stand on");
                        else if (!seen[i, j]) bad.Add($"{Name(spec)} floor {f} {host}: the {what} cannot be walked to from the {targets[0].Item1}");
                    }
                }
            }
            TestContext.Progress.WriteLine($"[gen-stairs] {flights} flights, in {string.Join(", ", hostKinds.Select(kv => $"{kv.Value} {kv.Key}"))}; {tiles} foot/landing tiles checked; {walked} stair rooms walked; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(flights, Is.GreaterThan(200));
            Assert.That(walked, Is.GreaterThan(300));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // F: every opening fits its wall: inside its length, under the floor above, clear of its siblings, and clear of
        // the walls that meet this one (an opening cut across a partition's end shows the partition in its jamb).
        // BREAK IT: drop the half-thickness offset from the outside walls' u; ignore the meeting wall's thickness when
        // placing a door; let a squeezed window keep its full width.
        [Test]
        public void OpeningsFitTheirWalls()
        {
            var bad = new List<string>();
            int openings = 0, junctions = 0;
            foreach (var (spec, res) in All())
                for (int f = 0; f < res.Floors; f++)
                {
                    var walls = WallsOn(res, f).ToList();
                    foreach (var w in walls)
                    {
                        var bands = new List<(float lo, float hi)>();
                        var a0 = Start(w); var d = Dir(w);
                        foreach (var o in walls)
                        {
                            if (o == w) continue;
                            var b0 = Start(o); var e = Dir(o);
                            float den = d.X * e.Z - d.Z * e.X;
                            if (MathF.Abs(den) < 1e-4f) continue;
                            float rx = b0.X - a0.X, rz = b0.Z - a0.Z;
                            float t = (rx * e.Z - rz * e.X) / den, s = (rx * d.Z - rz * d.X) / den;
                            if (t < -0.01f || t > w.Length + 0.01f || s < -0.01f || s > o.Length + 0.01f) continue;
                            bands.Add((t - o.Thickness * 0.5f, t + o.Thickness * 0.5f));
                            junctions++;
                        }
                        for (int i = 0; i < w.Openings.Count; i++)
                        {
                            var o = w.Openings[i];
                            openings++;
                            string at = $"{Name(spec)} floor {f} wall yaw {w.Yaw} at ({w.X:0.0},{w.Z:0.0}) opening {o.Archetype} u {o.U:0.00}..{o.U1:0.00}";
                            if (o.U < -0.01f || o.U1 > w.Length + 0.01f || o.V < -0.01f || o.V1 > BuildingGen.OpeningTop + 0.01f) bad.Add($"{at}: outside its wall (len {w.Length:0.00}, top {o.V1:0.00})");
                            if (WallOpenings.Overlaps(o, w.Openings, i)) bad.Add($"{at}: overlaps a sibling");
                            foreach (var (lo, hi) in bands)
                                if (o.U < hi - 0.01f && o.U1 > lo + 0.01f) bad.Add($"{at}: crosses a wall meeting it at u {lo:0.00}..{hi:0.00}");
                            if (o.Glazed && o.Width < BuildingGen.MinWindow - 0.01f) bad.Add($"{at}: a window only {o.Width:0.00} wide");
                        }
                    }
                }
            TestContext.Progress.WriteLine($"[gen-openings] {openings} openings against {junctions} wall junctions; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(openings, Is.GreaterThan(5000));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        /// <summary>The height of a pitched roof plane over a plan point, or NaN when the point is not under it. The plane's
        /// eave is its origin edge, Length along its yaw; it rises at its pitch (node pitch + 90) away from the eave for its
        /// sloped Height, i.e. Height * cos(pitch) in plan.</summary>
        static float RoofHeightAt(WallPlan w, float x, float z)
        {
            float pitch = (w.Pitch + 90f) * MathF.PI / 180f, run = w.Height * MathF.Cos(pitch);
            var d = Dir(w); var n = Normal(w);
            float along = (x - w.X) * d.X + (z - w.Z) * d.Z;     // along the eave
            float into = -((x - w.X) * n.X + (z - w.Z) * n.Z);   // away from the eave, up the slope (the plane rises along -local Z)
            if (along < 0f || along > w.Length || into < 0f || into > run) return float.NaN;
            return w.Y + into * MathF.Tan(pitch);
        }

        /// <summary>How much of an outside wall a room has: the length of its stretch of the building's edge on that wall.</summary>
        static float SpanOf(GenRoom g, WallPlan w)
        {
            bool alongX = w.Yaw == 0f || w.Yaw == 180f;
            float line = alongX ? w.Z : w.X;
            return g.Rects.Sum(r => alongX ? ((MathF.Abs(r.Z0 * Cell - line) < 0.5f || MathF.Abs(r.Z1 * Cell - line) < 0.5f) ? (r.X1 - r.X0) * Cell : 0f)
                                           : ((MathF.Abs(r.X0 * Cell - line) < 0.5f || MathF.Abs(r.X1 * Cell - line) < 0.5f) ? (r.Z1 - r.Z0) * Cell : 0f));
        }

        // G: the openings suit the room: daylight in every habitable room, a vent or window in a bathroom, a vehicle door
        // in a ground-floor garage or warehouse, a shop's street door in its longest outside wall, nothing in a closet or
        // store room, no outside door upstairs, and nothing in the outside wall a flight climbs along.
        // BREAK IT: give habitable rooms no window; no vehicle door; put the shop door on any wall; drop the bay cut.
        [Test]
        public void OpeningsSuitTheRoom()
        {
            var bad = new List<string>();
            int lit = 0, vented = 0, vehicle = 0, shops = 0, blank = 0, clear = 0;
            foreach (var (spec, res) in All())
            {
                var outer = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && w.Thickness == BuildingGen.Ext).ToList();
                List<(WallPlan W, WallOpening O)> Of(GenRoom g) => outer.Where(w => FloorOf(w) == g.Floor).SelectMany(w => w.Openings.Select(o => (w, o)))
                    .Where(p => { var c = At(p.w, p.o.U + p.o.Width * 0.5f); var n = Normal(p.w); return g.Holds(c.X - n.X * 1f, c.Z - n.Z * 1f); }).ToList();
                foreach (var g in res.Layout)
                {
                    var mine = Of(g);
                    var os = mine.Select(p => p.O).ToList();
                    string n = $"{Name(spec)}: {g}";
                    if (BuildingGen.Habitable(g.Kind)) { if (os.Any(o => o.Glazed)) lit++; else bad.Add($"{n} has no window"); }
                    if (g.Kind == RoomKind.Bathroom) { if (os.Any(o => o.Archetype == BuildingGen.ArchVent || o.Glazed)) vented++; else bad.Add($"{n} has no vent"); }
                    if ((g.Kind == RoomKind.Garage || g.Kind == RoomKind.Warehouse) && g.Floor == 0)
                    { if (os.Any(o => o.Archetype == BuildingGen.ArchGarage)) vehicle++; else bad.Add($"{n} has no vehicle door"); }
                    if (g.Kind == RoomKind.Shop && g.Floor == 0 && spec.Kind == BuildingKind.Commercial)
                    {
                        // (any of its doors: a back door may be on an end wall too)
                        var doorWalls = mine.Where(p => p.O.HasDoor).Select(p => p.W).ToList();
                        float longest = outer.Where(w => FloorOf(w) == 0).Max(w => SpanOf(g, w));
                        if (doorWalls.Count == 0) bad.Add($"{n} has no street door");
                        else if (doorWalls.Max(w => SpanOf(g, w)) < longest - 0.01f) bad.Add($"{n}'s doors are on a {doorWalls.Max(w => SpanOf(g, w)):0.0} m wall, not its {longest:0.0} m front");
                        else shops++;
                    }
                    if (g.Kind == RoomKind.Closet || g.Kind == RoomKind.Storage) { if (os.Count == 0) blank++; else bad.Add($"{n} has {os.Count} openings"); }
                    if (g.Floor > 0 && os.Any(o => o.Archetype == BuildingGen.ArchGarage || o.HasDoor)) bad.Add($"{n} has a door to outside upstairs");
                }
                // nothing in the outside wall a flight climbs along
                foreach (var s in res.Stairs)
                {
                    var fp = Grown(Foot(s), 0.6f);
                    foreach (var w in outer.Where(w => FloorOf(w) == s.Floor))
                        foreach (var o in w.Openings)
                        {
                            var c0 = At(w, o.U); var c1 = At(w, o.U1);
                            var ob = (MathF.Min(c0.X, c1.X), MathF.Min(c0.Z, c1.Z), MathF.Max(c0.X, c1.X), MathF.Max(c0.Z, c1.Z));
                            if (Overlap(fp, ob, 0f)) bad.Add($"{Name(spec)}: opening {o.Archetype} on floor {s.Floor} is in the wall behind flight {s.Floor}");
                            else clear++;
                        }
                }
            }
            TestContext.Progress.WriteLine($"[gen-suit] {lit} habitable rooms lit, {vented} bathrooms vented, {vehicle} vehicle doors, {shops} shopfronts, {blank} closets/stores blank, {clear} outside openings checked clear of flights; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(lit, Is.GreaterThan(500));
            Assert.That(vented, Is.GreaterThan(100));
            Assert.That(vehicle, Is.GreaterThan(50));
            Assert.That(shops, Is.GreaterThan(20));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // H: houses get a gable roof, businesses a flat one (strawberry: "businesses having flat roofs while houses have
        // gable") -- a house's lower floor too, where the one above leaves it uncovered ("any reason why the 1st floor
        // section of roof is flat instead of gable?") -- every cell of every floor is under the floor above or a roof,
        // a lower gable meets the upper wall under its head, and no window is behind it.
        // BREAK IT: a flat roof on houses (or on their lower parts); forget a roof; no gable ends; the lower gable at the
        // house's pitch (too tall); windows left in the wall behind it.
        [Test]
        public void RoofsSuitTheKindAndCoverEverything()
        {
            var bad = new List<string>();
            int gables = 0, flats = 0, cells = 0, exposed = 0, lowers = 0;
            foreach (var (spec, res) in All())
            {
                var roofs = res.Walls.Where(w => w.Kind == SurfaceKind.Roof).ToList();
                var slopes = roofs.Where(w => MathF.Abs(w.Pitch + 90f) > 0.5f).ToList();
                bool raised = res.Walls.Any(w => w.Kind == SurfaceKind.Wall && w.GableRise > 0.01f);
                if (spec.Kind == BuildingKind.Residential)
                { if (slopes.Count >= 2 && slopes.Count % 2 == 0 && raised) gables++; else bad.Add($"{Name(spec)}: a house without a gable roof ({slopes.Count} slopes, gable ends {raised})"); }
                else if (slopes.Count > 0 || raised) bad.Add($"{Name(spec)}: a {spec.Kind} with a pitched roof");
                else flats++;
                // a lower floor's gable meets the wall of the floor above under that wall's head, and nothing in that wall
                // opens below the roof in front of it
                foreach (var w in slopes.Where(w => w.Y < res.Floors * Storey - 0.05f))
                {
                    lowers++;
                    float pitch = w.Pitch + 90f, rise = w.Height * MathF.Sin(pitch * MathF.PI / 180f);
                    if (rise > Storey - 0.5f) bad.Add($"{Name(spec)}: a lower gable rises {rise:0.00} m, over the upper wall's head");
                    int above = (int)MathF.Round(w.Y / Storey);
                    foreach (var u in WallsOn(res, above).Where(u => u.Thickness == BuildingGen.Ext))
                        foreach (var o in u.Openings)
                            for (int t = 0; t <= 4; t++)
                            {
                                var p = At(u, o.U + o.Width * t / 4f);
                                var n = Normal(u);
                                float h = RoofHeightAt(w, p.X + n.X * 0.4f, p.Z + n.Z * 0.4f);
                                if (!float.IsNaN(h) && h > u.Y + o.V - 0.05f) { bad.Add($"{Name(spec)}: an opening on floor {above} is behind the roof below it ({h:0.00} m over its sill {u.Y + o.V:0.00})"); t = 5; }
                            }
                }
                int top = res.Floors - 1;
                for (int f = 0; f < res.Floors; f++)
                    for (int x = res.Footprints[f].X0; x < res.Footprints[f].X1; x++)
                        for (int z = 0; z < res.CellsZ; z++)
                        {
                            cells++;
                            float px = (x + 0.5f) * Cell, pz = (z + 0.5f) * Cell;
                            if (f < top && x >= res.Footprints[f + 1].X0 && x < res.Footprints[f + 1].X1) continue;
                            exposed++;
                            bool covered = res.Roof == RoofKind.Gable
                                ? slopes.Any(w => MathF.Abs(w.Y - (f + 1) * Storey) < 0.05f && !float.IsNaN(RoofHeightAt(w, px, pz)))
                                : roofs.Any(w => MathF.Abs(w.Pitch + 90f) < 0.5f && MathF.Abs(w.Y - (f + 1) * Storey - BuildingGen.Slab * 0.5f) < 0.05f
                                                 && px > w.X && px < w.X + w.Length && pz > w.Z - w.Height && pz < w.Z);
                            if (!covered) bad.Add($"{Name(spec)}: floor {f} cell ({x},{z}) has no roof over it");
                        }
            }
            TestContext.Progress.WriteLine($"[gen-roof] {gables} gabled houses ({lowers} slopes on lower floors), {flats} flat-roofed businesses; {exposed} of {cells} floor cells open to the sky, each checked for its roof; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(gables, Is.GreaterThan(100));
            Assert.That(flats, Is.GreaterThan(300));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // I: the layouts strawberry asked for actually occur, and nothing breaks the stacking: halls not guaranteed
        // (ground floors with no hall), rooms opening straight into each other, stairs in rooms other than a hall, one
        // floor where one is enough, upper floors shorter than the one below -- and every upper floor inside the one
        // under it.
        // BREAK IT: always the corridor plan; always a second floor; upper floors the full length; an upper floor
        // hanging past the one below.
        [Test]
        public void LayoutsVaryTheWayStrawberryAsked()
        {
            var bad = new List<string>();
            int noHall = 0, direct = 0, nonHallStairs = 0, single = 0, couldStack = 0, smaller = 0, upper = 0, bands = 0, corridors = 0;
            foreach (var (spec, res) in All())
            {
                if (res.Plan == "bands") bands++; else corridors++;
                if (!res.Layout.Any(g => g.Floor == 0 && g.Kind == RoomKind.Hall)) noHall++;
                for (int f = 0; f < res.Floors; f++)
                    foreach (var d in Doors(res, f))
                        if (d.A != null && d.B != null && d.A.Kind != RoomKind.Hall && d.B.Kind != RoomKind.Hall) direct++;
                foreach (var s in res.Stairs)
                    if (RoomAt(res, s.Floor, s.X + s.Right.X * 1f + s.Back.X * 1f, s.Z + s.Right.Z * 1f + s.Back.Z * 1f) is GenRoom h && h.Kind != RoomKind.Hall) nonHallStairs++;
                // buildings that fit on one floor but are big enough that a second would be plausible: some of each
                int real = res.Layout.Count(g => !g.Circulation);
                if (spec.MaxFloors >= 2 && spec.MinFloors <= 1 && real <= spec.MaxRoomsPerFloor && real >= 5) { couldStack++; if (res.Floors == 1) single++; }
                for (int f = 1; f < res.Floors; f++)
                {
                    upper++;
                    var (a0, a1) = res.Footprints[f - 1]; var (b0, b1) = res.Footprints[f];
                    if (b0 < a0 || b1 > a1) bad.Add($"{Name(spec)}: floor {f} [{b0},{b1}) hangs past floor {f - 1} [{a0},{a1})");
                    if (b1 - b0 < a1 - a0) smaller++;
                }
            }
            TestContext.Progress.WriteLine($"[gen-vary] {bands} band plans, {corridors} corridor plans; {noHall} ground floors with no hall; {direct} doors between two rooms neither of them a hall; {nonHallStairs} flights in a room that is not a hall; {single} of {couldStack} buildings that fit one floor kept one; {smaller} of {upper} upper floors shorter than the floor below{(bad.Count > 0 ? "; " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
            Assert.That(noHall, Is.GreaterThan(All().Count / 5), "halls are close to guaranteed");
            Assert.That(direct, Is.GreaterThan(All().Count), "rooms hardly ever open into each other");
            Assert.That(nonHallStairs, Is.GreaterThan(30), "the stairs are nearly always in a hall");
            Assert.That(single, Is.GreaterThan(couldStack / 5), "a second floor is close to guaranteed");
            Assert.That(couldStack - single, Is.GreaterThan(couldStack / 5), "a second floor hardly ever happens");
            Assert.That(smaller, Is.GreaterThan(upper / 5), "upper floors are nearly always the full length");
        }

        // L: the dining room is beside the kitchen, or facing it across a hall (strawberry: "require dining room to be
        // adjascent or at least nearby a kitchen") and has a door to it when beside it; and no two doors crowd one corner
        // -- both within a metre of the same point where walls meet ("try to avoid multiple doors on a corner, like in
        // the floorplan of residential seed 8's dining room").
        // BREAK IT: drop the kitchen/dining check; drop the corner preference.
        [Test]
        public void DiningByTheKitchenAndDoorsOffEachOthersCorners()
        {
            var bad = new List<string>();
            int pairs = 0, beside = 0, facing = 0, excused = 0, doors = 0, crowded = 0;
            foreach (var (spec, res) in All())
            {
                var k = res.Layout.FirstOrDefault(g => g.Kind == RoomKind.Kitchen);
                var d = res.Layout.FirstOrDefault(g => g.Kind == RoomKind.DiningRoom);
                if (k != null && d != null && k.Floor == d.Floor)
                {
                    pairs++;
                    // beside: their cells touch along a whole cell edge; facing: both on one hall, a cell or more in common along it
                    bool touch = k.Rects.Any(a => d.Rects.Any(b => (a.X1 == b.X0 || b.X1 == a.X0) && Math.Min(a.Z1, b.Z1) - Math.Max(a.Z0, b.Z0) >= 1
                                                                 || (a.Z1 == b.Z0 || b.Z1 == a.Z0) && Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0) >= 1));
                    var hall = res.Layout.FirstOrDefault(g => g.Floor == k.Floor && g.Kind == RoomKind.Hall);
                    bool Touches(GenRoom x, GenRoom y) => x.Rects.Any(a => y.Rects.Any(b => (a.Z1 == b.Z0 || b.Z1 == a.Z0) && Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0) >= 1));
                    bool face = hall != null && Touches(k, hall) && Touches(d, hall) && Math.Min(k.X1, d.X1) - Math.Max(k.X0, d.X0) >= 1;
                    if (touch)
                    {
                        beside++;
                        if (!Doors(res, k.Floor).Any(x => (x.A == k && x.B == d) || (x.A == d && x.B == k))) bad.Add($"{Name(spec)}: {k} and {d} touch but have no door between them");
                    }
                    else if (face) facing++;
                    else if (res.Notes.Any(n => n.Contains("dining room could not"))) excused++;
                    else bad.Add($"{Name(spec)}: {d} is neither beside nor facing {k}");
                }
                for (int f = 0; f < res.Floors; f++)
                {
                    var ds = Doors(res, f).ToList();
                    doors += ds.Count;
                    // the lattice points within a metre of each door, in cells
                    HashSet<(int, int)> Points((WallPlan W, WallOpening O, GenRoom A, GenRoom B, (float X, float Z) PA, (float X, float Z) PB) x)
                    {
                        var p0 = At(x.W, x.O.U - 1f); var p1 = At(x.W, x.O.U1 + 1f);
                        var set = new HashSet<(int, int)>();
                        for (int i = (int)MathF.Ceiling(MathF.Min(p0.X, p1.X) / Cell - 1e-3f); i * Cell <= MathF.Max(p0.X, p1.X) + 1e-3f; i++)
                            for (int j = (int)MathF.Ceiling(MathF.Min(p0.Z, p1.Z) / Cell - 1e-3f); j * Cell <= MathF.Max(p0.Z, p1.Z) + 1e-3f; j++)
                                set.Add((i, j));
                        return set;
                    }
                    for (int i = 0; i < ds.Count; i++)
                        for (int j = i + 1; j < ds.Count; j++)
                            if (Points(ds[i]).Overlaps(Points(ds[j])))
                            {
                                crowded++;
                                if (crowded <= 3) TestContext.Progress.WriteLine($"[gen-corner]   {Name(spec)} floor {f}: doors of {ds[i].A?.Kind}/{ds[i].B?.Kind} and {ds[j].A?.Kind}/{ds[j].B?.Kind} share a corner");
                            }
                }
            }
            TestContext.Progress.WriteLine($"[gen-corner] {pairs} kitchens with a dining room on their floor: {beside} beside it, {facing} facing it across a hall, {excused} apart with a note; {doors} doors, {crowded} pairs crowding one corner; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(pairs, Is.GreaterThan(50));
            // the note is the generator's last resort, not a licence: it is excused only when it is rare (none today)
            Assert.That(excused, Is.LessThanOrEqualTo(pairs / 50), "the dining room is often apart from the kitchen, with a note");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
            // "try to avoid": the generator now gets every door of the sweep off every shared corner (93 pairs crowded
            // before it tried, 24 before it moved doors back, 5 before the tree preferred clean doors). A corner that NO
            // spot avoids would be a fair exception -- but none occurs, so none is allowed until one does.
            Assert.That(crowded, Is.EqualTo(0), "doors crowd corners");
            // strawberry's own example: residential seed 8's dining room had two doors on one corner
            var eight = BuildingGen.Generate(new BuildingGenSpec { Seed = 8, Kind = BuildingKind.Residential, MinRooms = 7, MaxFloors = 2, MaxRoomsPerFloor = 6 });
            var ed = Doors(eight, 0).ToList();
            for (int i = 0; i < ed.Count; i++)
                for (int j = i + 1; j < ed.Count; j++)
                {
                    var a0 = At(ed[i].W, ed[i].O.U + ed[i].O.Width * 0.5f); var b0 = At(ed[j].W, ed[j].O.U + ed[j].O.Width * 0.5f);
                    bool ai = ed[i].W.Yaw == 0f || ed[i].W.Yaw == 180f, bj = ed[j].W.Yaw == 0f || ed[j].W.Yaw == 180f;
                    Assert.That(BuildingGen.SharesCorner(ai, (int)MathF.Round((ai ? a0.Z : a0.X) / Cell), ai ? a0.X : a0.Z,
                                                         bj, (int)MathF.Round((bj ? b0.Z : b0.X) / Cell), bj ? b0.X : b0.Z), Is.False,
                                $"seed 8: doors of {ed[i].A?.Kind}/{ed[i].B?.Kind} and {ed[j].A?.Kind}/{ed[j].B?.Kind} share a corner");
                }
        }

        // J: every palette a kind can draw from exists in the retail table, and generated buildings use them.
        // BREAK IT: misspell a palette name ("House_10").
        [Test]
        public void PalettesExistAndSuitTheKind()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            string tsv = null;
            for (; d != null && tsv == null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "game", "content", "wall_palettes.tsv"))) tsv = Path.Combine(d.FullName, "game", "content", "wall_palettes.tsv");
            Assert.That(tsv, Is.Not.Null, "wall_palettes.tsv not found");
            var names = new HashSet<string>(WallPalettes.Parse(File.ReadAllLines(tsv)).Select(p => p.Name));
            int listed = 0;
            foreach (BuildingKind kind in Enum.GetValues(typeof(BuildingKind)))
                foreach (var p in BuildingGen.PalettesFor(kind)) { listed++; Assert.That(names, Does.Contain(p), $"{kind} palette {p}"); }
            var used = All().Select(x => x.Res.Palette).Distinct().Count();
            foreach (var (spec, res) in All()) Assert.That(BuildingGen.PalettesFor(spec.Kind), Does.Contain(res.Palette));
            TestContext.Progress.WriteLine($"[gen-palette] {listed} palettes listed of {names.Count} in the table, {used} used by the sweep");
            Assert.That(used, Is.GreaterThan(20));
        }

        // K: the storey of a designation survives a save, and a ground-floor one saves exactly as before.
        // BREAK IT: drop the optional floor token on write or on read.
        [Test]
        public void DesignationFloorsSurviveASave()
        {
            var res = BuildingGen.Generate(new BuildingGenSpec { Seed = 3, Kind = BuildingKind.Residential, MinRooms = 8, MinFloors = 2, MaxFloors = 3, MaxRoomsPerFloor = 4 });
            string text = WallSave.Write(res.Walls, res.Kind, res.Rooms);
            var walls = WallSave.Read(text.Split('\n'), out var kind, out var rooms);
            Assert.That(kind, Is.EqualTo(res.Kind));
            Assert.That(walls.Count, Is.EqualTo(res.Walls.Count));
            Assert.That(rooms.Select(r => (r.Kind, r.Floor)), Is.EqualTo(res.Rooms.Select(r => (r.Kind, r.Floor))));
            Assert.That(rooms.Max(r => r.Floor), Is.GreaterThan(0), "the case must have upper floors to test anything");
            var ground = text.Split('\n').First(l => l.StartsWith("room "));
            Assert.That(ground.Split(' ').Length, Is.EqualTo(4), "a ground-floor designation keeps the old three-field form: " + ground);
        }
    }
}
