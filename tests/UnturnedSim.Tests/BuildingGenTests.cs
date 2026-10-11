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
    // which rooms the walls enclose comes from RoomEnclosure, which rooms a door joins comes from where the door
    // sits in its wall, and what a flight lands on comes from the flight's own footprint. The layout is used only
    // to name the rooms -- and test C pins that the walls enclose exactly the layout's rooms, so that naming is
    // earned. Each check prints its counts beside its verdict: "0 violations" over 0 cases is not a pass.
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
        static bool In(GenRoom g, float x, float z) => x > g.X0 * Cell && x < g.X1 * Cell && z > g.Z0 * Cell && z < g.Z1 * Cell;
        static GenRoom RoomAt(BuildingGenResult r, int f, float x, float z) => r.Layout.FirstOrDefault(g => g.Floor == f && In(g, x, z));
        static string Name(BuildingGenSpec s) => $"{s.Kind} seed {s.Seed} (min {s.MinRooms}, floors {s.MinFloors}-{s.MaxFloors}, {s.MaxRoomsPerFloor}/floor)";

        // A: the same seed gives the same building, byte for byte, and seeds differ.
        // BREAK IT: seed the RNG from the clock; drop the seeded shuffles and picks (every seed the same plan).
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
                // or by one more where the side has only small rooms, since a one-cell leftover is too narrow to be a room
                foreach (var g in real.Where(g => BuildingGen.Service(g.Kind)))
                {
                    int limit = Math.Max(2, (int)Math.Ceiling(BuildingGen.TraitOf(g.Kind, spec.Kind).Cells / (double)(g.Z1 - g.Z0))) + 1;
                    if (!real.Any(o => o.Floor == g.Floor && o.Z0 == g.Z0 && !BuildingGen.Service(o.Kind))) limit++;
                    serviced++;
                    if (g.X1 - g.X0 > limit && per[g.Floor] < spec.MaxRoomsPerFloor) bad.Add($"{n}: {g} is {g.X1 - g.X0} cells long (limit {limit}, side {string.Join(",", res.Layout.Where(o => o.Floor == g.Floor && o.Z0 == g.Z0).Select(o => o.Kind + ":" + (o.X1 - o.X0)))}) with floor {g.Floor} at {per[g.Floor]} of {spec.MaxRoomsPerFloor} rooms");
                }
                foreach (var g in real)
                {
                    var p = BuildingGen.TraitOf(g.Kind, spec.Kind).Pref;
                    if (p == BuildingGen.FloorPref.Any) continue;
                    prefs++;
                    if (p == BuildingGen.FloorPref.GroundOnly && g.Floor != 0) bad.Add($"{n}: {g} must be on the ground");
                    if (p == BuildingGen.FloorPref.Ground && g.Floor != 0 && per[0] < spec.MaxRoomsPerFloor) bad.Add($"{n}: {g} upstairs with the ground floor at {per[0]}");
                    // an upstairs room moved down to fill an otherwise empty upper floor is the one sanctioned exception, the other way
                    if (p == BuildingGen.FloorPref.Upper && g.Floor == 0 && res.Floors > 1 && !upperFull) bad.Add($"{n}: {g} on the ground with room upstairs");
                }
                // same-kind rooms with no preference spread over the floors: no floor has two while another has none
                foreach (var k in real.Where(g => BuildingGen.TraitOf(g.Kind, spec.Kind).Pref == BuildingGen.FloorPref.Any).Select(g => g.Kind).Distinct())
                {
                    var c = Enumerable.Range(0, res.Floors).Select(f => real.Count(g => g.Kind == k && g.Floor == f)).ToArray();
                    if (c.Max() - c.Min() < 2) { spreads++; continue; }
                    // ...unless the emptier floor was full
                    int lo = Array.IndexOf(c, c.Min());
                    if (per[lo] < spec.MaxRoomsPerFloor) bad.Add($"{n}: {k} per floor {string.Join("/", c)} with floor {lo} not full");
                    else spreads++;
                }
            }
            TestContext.Progress.WriteLine($"[gen-spec] {cases} buildings, {rooms} rooms, {prefs} floor preferences, {spreads} same-kind spreads, {serviced} service-room widths checked; {noted} carry notes; {bad.Count} violations{(bad.Count > 0 ? ": " + bad[0] : "")}");
            // what the notes SAY, by shape and by spec: a note is a sanctioned exception, so a new kind of one is news
            foreach (var g in All().SelectMany(x => x.Res.Notes.Select(n => (shape: string.Join(" ", n.Split(' ').Where(w => !char.IsDigit(w[0])).Take(6)), spec: $"min {x.Spec.MinRooms} floors {x.Spec.MinFloors}-{x.Spec.MaxFloors} {x.Spec.MaxRoomsPerFloor}/floor")))
                                   .GroupBy(x => x).OrderByDescending(g => g.Count()))
                TestContext.Progress.WriteLine($"[gen-spec]   {g.Count()} x \"{g.Key.shape}...\" ({g.Key.spec})");
            Assert.That(cases, Is.GreaterThan(400));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // C: the walls enclose exactly the planned rooms -- found by RoomEnclosure from the wall centrelines, and
        // each designation resolves to the room of its kind with the planned area.
        // BREAK IT: leave out the partitions (rooms merge), or one hall wall (the hall merges with a side).
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
                    var ds = res.Rooms.Where(d => d.Floor == f).ToList();
                    RoomDesignations.Resolve(rooms, ds, out var byRoom, out var orphans);
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

        // D: every room can be walked to from outside -- through the DOORS in the walls (each door's two sides mapped
        // to rooms by where they are) and up the flights (foot and top mapped the same way).
        // BREAK IT: no door into the stairwell; doors on the wrong hall wall; no front door.
        [Test]
        public void EveryRoomCanBeWalkedToFromOutside()
        {
            var bad = new List<string>();
            int doors = 0, flights = 0, reached = 0, total = 0;
            foreach (var (spec, res) in All())
            {
                var edges = new Dictionary<GenRoom, HashSet<GenRoom>>();
                var outside = new GenRoom { Kind = RoomKind.Unassigned, Floor = -1 };
                void Link(GenRoom a, GenRoom b) { if (a == null || b == null || a == b) return;
                    (edges.TryGetValue(a, out var s) ? s : edges[a] = new HashSet<GenRoom>()).Add(b);
                    (edges.TryGetValue(b, out var t) ? t : edges[b] = new HashSet<GenRoom>()).Add(a); }
                GenRoom Where(int f, float x, float z)
                {
                    if (x < 0 || z < 0 || x > res.CellsX * Cell || z > res.CellsZ * Cell) return f == 0 ? outside : null;
                    return RoomAt(res, f, x, z);
                }
                foreach (var w in res.Walls.Where(w => w.Kind == SurfaceKind.Wall))
                    foreach (var o in w.Openings)
                    {
                        if (o.V > 0.05f || o.Height < 2.5f || o.Width < 2.4f) continue;   // only what you can walk through
                        doors++;
                        var c = At(w, o.U + o.Width * 0.5f);
                        var nm = Normal(w);
                        float d = w.Thickness * 0.5f + 0.3f;
                        Link(Where(FloorOf(w), c.X + nm.X * d, c.Z + nm.Z * d), Where(FloorOf(w), c.X - nm.X * d, c.Z - nm.Z * d));
                    }
                foreach (var s in res.Stairs)
                {
                    flights++;
                    var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                    float mx = s.X + rx * s.Width * 0.5f, mz = s.Z + rz * s.Width * 0.5f;
                    Link(Where(s.Floor, mx - bx * 0.3f, mz - bz * 0.3f), Where(s.Floor + 1, mx + bx * (s.Run + 0.3f), mz + bz * (s.Run + 0.3f)));
                }
                var seen = new HashSet<GenRoom> { outside };
                var q = new Queue<GenRoom>(); q.Enqueue(outside);
                while (q.Count > 0) { var a = q.Dequeue(); if (edges.TryGetValue(a, out var nb)) foreach (var b in nb) if (seen.Add(b)) q.Enqueue(b); }
                foreach (var g in res.Layout) { total++; if (seen.Contains(g)) reached++; else bad.Add($"{Name(spec)}: {g} cannot be reached"); }
            }
            TestContext.Progress.WriteLine($"[gen-reach] {doors} doors, {flights} flights; {reached} of {total} rooms reached from outside{(bad.Count > 0 ? "; first unreached " + bad[0] : "")}");
            Assert.That(flights, Is.GreaterThan(200));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // E: each flight stands inside its stairwell clear of every wall, the hole it cuts in the floor above is
        // clear of every wall up there, and on every floor the stairwell's door, the arrival from below and the foot
        // of the next flight are joined by floor you can stand on (not treads, not a hole).
        // BREAK IT: the flights in the row by the hall (the door opens onto the steps); the last row's flight
        // centred in its row (its hole runs under the outside wall); one row for every flight (holes under treads).
        [Test]
        public void StairsLandOnFloorAndClearOfWalls()
        {
            var bad = new List<string>();
            int flights = 0, floorsWalked = 0;
            const float M = WallOpenings.StairHoleMargin;
            foreach (var (spec, res) in All())
            {
                if (res.Floors < 2) continue;
                (float x0, float z0, float x1, float z1) Foot(StairPlan s)
                {
                    var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                    float ax = s.X, az = s.Z, cx = s.X + bx * s.Run + rx * s.Width, cz = s.Z + bz * s.Run + rz * s.Width;
                    return (MathF.Min(ax, cx), MathF.Min(az, cz), MathF.Max(ax, cx), MathF.Max(az, cz));
                }
                bool Hit((float x0, float z0, float x1, float z1) a, WallPlan w)
                {
                    var p = Start(w); var q = At(w, w.Length); float t = w.Thickness * 0.5f;
                    float wx0 = MathF.Min(p.X, q.X) - t, wx1 = MathF.Max(p.X, q.X) + t, wz0 = MathF.Min(p.Z, q.Z) - t, wz1 = MathF.Max(p.Z, q.Z) + t;
                    return a.x0 < wx1 - 0.01f && a.x1 > wx0 + 0.01f && a.z0 < wz1 - 0.01f && a.z1 > wz0 + 0.01f;
                }
                foreach (var s in res.Stairs)
                {
                    flights++;
                    var fp = Foot(s);
                    var hole = (fp.x0 - M, fp.z0 - M, fp.x1 + M, fp.z1 + M);
                    var well = res.Layout.First(g => g.Floor == s.Floor && g.Kind == RoomKind.Stairwell);
                    if (!In(well, fp.x0 + 0.01f, fp.z0 + 0.01f) || !In(well, fp.x1 - 0.01f, fp.z1 - 0.01f)) bad.Add($"{Name(spec)}: flight {s.Floor} leaves its stairwell");
                    foreach (var w in WallsOn(res, s.Floor)) if (Hit(fp, w)) bad.Add($"{Name(spec)}: flight {s.Floor} runs into a wall at yaw {w.Yaw}");
                    foreach (var w in WallsOn(res, s.Floor + 1)) if (Hit(hole, w)) bad.Add($"{Name(spec)}: flight {s.Floor}'s hole runs under a wall on floor {s.Floor + 1} (yaw {w.Yaw}, thick {w.Thickness})");
                    // its treads stand on floor, not over the hole the flight below cut in it
                    foreach (var b in res.Stairs.Where(b => b.Floor == s.Floor - 1))
                    {
                        var bf = Foot(b);
                        if (fp.x0 < bf.x1 + M && fp.x1 > bf.x0 - M && fp.z0 < bf.z1 + M && fp.z1 > bf.z0 - M)
                            bad.Add($"{Name(spec)}: flight {s.Floor} stands over the hole flight {b.Floor} cut");
                    }
                }
                // walk each floor's stairwell on a 0.1 m grid
                for (int f = 0; f < res.Floors; f++)
                {
                    var well = res.Layout.First(g => g.Floor == f && g.Kind == RoomKind.Stairwell);
                    float gx0 = well.X0 * Cell, gz0 = well.Z0 * Cell;
                    int nx = (int)((well.X1 - well.X0) * Cell / 0.1f), nz = (int)((well.Z1 - well.Z0) * Cell / 0.1f);
                    var open = new bool[nx, nz];
                    var blocks = new List<(float, float, float, float)>();
                    var up = res.Stairs.Where(s => s.Floor == f).ToList();
                    var down = res.Stairs.Where(s => s.Floor == f - 1).ToList();
                    foreach (var s in up) blocks.Add(Foot(s));
                    foreach (var s in down) { var p = Foot(s); blocks.Add((p.x0 - M, p.z0 - M, p.x1 + M, p.z1 + M)); }
                    foreach (var w in WallsOn(res, f)) { var p = Start(w); var q = At(w, w.Length); float t = w.Thickness * 0.5f;
                        blocks.Add((MathF.Min(p.X, q.X) - t, MathF.Min(p.Z, q.Z) - t, MathF.Max(p.X, q.X) + t, MathF.Max(p.Z, q.Z) + t)); }
                    for (int i = 0; i < nx; i++)
                        for (int j = 0; j < nz; j++)
                        {
                            float x = gx0 + (i + 0.5f) * 0.1f, z = gz0 + (j + 0.5f) * 0.1f;
                            open[i, j] = !blocks.Any(b => x > b.Item1 && x < b.Item3 && z > b.Item2 && z < b.Item4);
                        }
                    (int, int) G(float x, float z) => (Math.Clamp((int)((x - gx0) / 0.1f), 0, nx - 1), Math.Clamp((int)((z - gz0) / 0.1f), 0, nz - 1));
                    // the door from the hall: on the hall's north wall, inside the stairwell's stretch
                    var hn = WallsOn(res, f).First(w => MathF.Abs(w.Z - (well.Z0 * Cell)) < 0.01f && w.Yaw == 0f);
                    var door = hn.Openings.Where(o => o.HasDoor).Select(o => At(hn, o.U + o.Width * 0.5f)).FirstOrDefault(c => c.X > well.X0 * Cell && c.X < well.X1 * Cell);
                    if (door == default) { bad.Add($"{Name(spec)}: floor {f}'s stairwell has no door"); continue; }
                    var targets = new List<(string, float, float)> { ("door", door.X, door.Z + hn.Thickness * 0.5f + 0.2f) };
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
                    floorsWalked++;
                    foreach (var (what, x, z) in targets)
                    {
                        var (i, j) = G(x, z);
                        if (!open[i, j]) bad.Add($"{Name(spec)} floor {f}: the {what} at ({x:0.0}, {z:0.0}) is not floor you can stand on");
                        else if (!seen[i, j]) bad.Add($"{Name(spec)} floor {f}: the {what} at ({x:0.0}, {z:0.0}) cannot be walked to from the door");
                    }
                }
            }
            TestContext.Progress.WriteLine($"[gen-stairs] {flights} flights, {floorsWalked} stairwell floors walked; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(flights, Is.GreaterThan(200));
            Assert.That(floorsWalked, Is.GreaterThan(300));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // F: every opening fits its wall: inside its length, under the floor above, clear of its siblings, and
        // clear of the walls that meet this one (an opening cut across a partition's end shows the partition in
        // its jamb).
        // BREAK IT: drop the half-thickness offset from the outside walls' u; margin a corner room by half a
        // partition instead of half an outside wall; let a squeezed window keep its full width.
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
                        // where every other wall on this floor crosses or meets this one's centreline, as a u band
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

        // G: the openings suit the room: daylight in every habitable room, a vent or window in a bathroom, a vehicle
        // door in a ground-floor garage or warehouse, a street door in a ground-floor shop, and nothing at all in a
        // closet or store room.
        // BREAK IT: give habitable rooms no window; no vehicle door; no shop door.
        [Test]
        public void OpeningsSuitTheRoom()
        {
            var bad = new List<string>();
            int lit = 0, vented = 0, vehicle = 0, shops = 0, blank = 0;
            foreach (var (spec, res) in All())
            {
                var outer = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && w.Thickness == BuildingGen.Ext).ToList();
                // the openings in a room's outside walls: those whose middle lies on its stretch of the building's edge
                List<WallOpening> Of(GenRoom g) => outer.Where(w => FloorOf(w) == g.Floor).SelectMany(w => w.Openings.Select(o => (w, o)))
                    .Where(p => { var c = At(p.w, p.o.U + p.o.Width * 0.5f); var n = Normal(p.w);
                                  return In(g, c.X - n.X * 1f, c.Z - n.Z * 1f); }).Select(p => p.o).ToList();
                foreach (var g in res.Layout)
                {
                    var os = Of(g);
                    string n = $"{Name(spec)}: {g}";
                    if (BuildingGen.Habitable(g.Kind)) { if (os.Any(o => o.Glazed)) lit++; else bad.Add($"{n} has no window"); }
                    if (g.Kind == RoomKind.Bathroom) { if (os.Any(o => o.Archetype == BuildingGen.ArchVent || o.Glazed)) vented++; else bad.Add($"{n} has no vent"); }
                    if ((g.Kind == RoomKind.Garage || g.Kind == RoomKind.Warehouse) && g.Floor == 0)
                    { if (os.Any(o => o.Archetype == BuildingGen.ArchGarage)) vehicle++; else bad.Add($"{n} has no vehicle door"); }
                    if (g.Kind == RoomKind.Shop && g.Floor == 0) { if (os.Any(o => o.HasDoor)) shops++; else bad.Add($"{n} has no street door"); }
                    if (g.Kind == RoomKind.Closet || g.Kind == RoomKind.Storage) { if (os.Count == 0) blank++; else bad.Add($"{n} has {os.Count} openings"); }
                    if (g.Floor > 0 && os.Any(o => o.Archetype == BuildingGen.ArchGarage || o.HasDoor)) bad.Add($"{n} has a door to outside upstairs");
                }
            }
            TestContext.Progress.WriteLine($"[gen-suit] {lit} habitable rooms lit, {vented} bathrooms vented, {vehicle} vehicle doors, {shops} shopfronts, {blank} closets/stores blank; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(lit, Is.GreaterThan(500));
            Assert.That(vented, Is.GreaterThan(100));
            Assert.That(vehicle, Is.GreaterThan(50));
            Assert.That(shops, Is.GreaterThan(20));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // H: every palette a kind can draw from exists in the retail table, and generated buildings use them.
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

        // I: the storey of a designation survives a save, and a ground-floor one saves exactly as before.
        // BREAK IT: drop the optional floor token on write or on read.
        [Test]
        public void DesignationFloorsSurviveASave()
        {
            var res = BuildingGen.Generate(new BuildingGenSpec { Seed = 3, Kind = BuildingKind.Residential, MinRooms = 8, MaxFloors = 3, MaxRoomsPerFloor = 4 });
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
