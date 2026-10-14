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
    public partial class BuildingGenTests
    {
        const float Cell = BuildingGen.Cell, Storey = BuildingGen.Storey;

        // ---- the sweep: every kind, many seeds, and specs that push on each limit (UG_GENSWEEP_SEEDS runs it over more
        // seeds than the 24 a test run takes, for a one-off wider look; the counts the tests assert are floors, so they hold)
        static readonly int SweepSeeds = int.TryParse(Environment.GetEnvironmentVariable("UG_GENSWEEP_SEEDS"), out var n) && n > 0 ? n : 24;
        static IEnumerable<BuildingGenSpec> Specs()
        {
            foreach (BuildingKind kind in Enum.GetValues(typeof(BuildingKind)))
                for (int seed = 0; seed < SweepSeeds; seed++)
                {
                    // (a block of flats is five to ten times a house's rooms, and furnishing is most of the time a building
                    // takes: every other seed, and its own specs below on those)
                    if (kind == BuildingKind.Apartment && seed % 2 == 1) continue;
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 10, MaxFloors = 3, MaxRoomsPerFloor = 4,
                        MinOfKind = new Dictionary<RoomKind, int> { [RoomKind.Bedroom] = 3, [RoomKind.Bathroom] = 3 } };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 6, MaxFloors = 1, MaxRoomsPerFloor = 8 };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 12, MaxFloors = 2, MaxRoomsPerFloor = 4 };   // cannot fit
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinFloors = 3, MaxFloors = 4, MaxRoomsPerFloor = 3, MinRooms = 9,
                        MinOfKind = new Dictionary<RoomKind, int> { [RoomKind.Office] = 2, [RoomKind.Garage] = 1 } };
                    // (and sides asked for: a front door on the north turns the whole building half round, so every check
                    // here also runs over turned buildings)
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 7, MaxFloors = 2, MaxRoomsPerFloor = 6,
                        FrontSide = seed % 3 == 0 ? Sides.North : seed % 3 == 1 ? Sides.None : Sides.West,
                        BackSide = seed % 4 == 1 ? Sides.East : Sides.None, NoWindows = seed % 5 == 2 ? Sides.East : Sides.None };
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 5, MaxFloors = 2, MaxRoomsPerFloor = 8 };   // one floor or two: the roll
                    // a block of flats sized as one is: floors of three or four flats and more, some of them deep (a study as
                    // well), a depth and a length asked for (single-loaded where it is narrow)
                    if (kind == BuildingKind.Apartment)
                    {
                        yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinFloors = 2, MaxFloors = 4, MaxRoomsPerFloor = 12 };
                        yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinFloors = 3, MaxFloors = 4, MaxRoomsPerFloor = 18,
                            MinOfKind = new Dictionary<RoomKind, int> { [RoomKind.Bedroom] = 14, [RoomKind.Office] = 2 } };
                        yield return new BuildingGenSpec { Seed = seed, Kind = kind, MaxFloors = 3, MaxRoomsPerFloor = 14, Depth = 5 + seed % 9,
                            Width = seed % 4 == 0 ? 0 : 16 + seed % 19, FrontSide = seed % 6 == 0 ? Sides.West : Sides.None, BackSide = seed % 8 == 2 ? Sides.North : Sides.None };
                    }
                    // fitted to a town plot's building zone (cow tools' plots: small 4x5 ... large and corner 7x7), so every
                    // check here also runs over the compromises FitPlot makes -- smaller rooms, more floors, fewer rooms
                    var (pw, pd) = Plots[seed % Plots.Length];
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 5, MaxFloors = 2, MaxRoomsPerFloor = 6, Width = pw, Depth = pd, FitPlot = true };
                    // an L and a T of more than one storey, asked for: the floors above stand on the wing's columns and the
                    // arm(s) stay one storey -- so every check here runs over them too, chimneys and porches included
                    yield return new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 7, MinFloors = 2, MaxFloors = 3, MaxRoomsPerFloor = 5,
                        Shape = seed % 2 == 0 ? FootprintShape.L : FootprintShape.T, Chimney = seed % 3 == 0 ? ChimneyChoice.Yes : ChimneyChoice.Auto,
                        Porches = seed % 4 == 1 ? PorchChoice.Both : PorchChoice.Auto };
                }
            // and, as wide as UG_APT_SEEDS asks, blocks of flats of every shape -- through EVERY check here, not only the
            // flats' own (each is a second or so to build and furnish: the default is none)
            for (int seed = 1000; seed < 1000 + ExtraFlatSeeds; seed++)
            {
                yield return new BuildingGenSpec { Seed = seed, Kind = BuildingKind.Apartment, MinFloors = 1 + seed % 4, MaxFloors = 1 + seed % 4, MaxRoomsPerFloor = 8 + seed % 13 };
                yield return new BuildingGenSpec { Seed = seed, Kind = BuildingKind.Apartment, MinFloors = 2, MaxFloors = 5, MaxRoomsPerFloor = 10 + seed % 11, Depth = seed % 3 == 0 ? 5 + seed % 9 : 0,
                                                   Width = seed % 5 == 0 ? 15 + seed % 25 : 0, FrontSide = (Sides)(seed % 5 == 4 ? 0 : 1 << (seed % 4)), BackSide = seed % 7 == 1 ? Sides.East : Sides.None,
                                                   MinOfKind = new Dictionary<RoomKind, int> { [RoomKind.Bedroom] = seed % 17, [RoomKind.Office] = seed % 3 } };
                // (what a block of flats does not do, asked for anyway: an L, porches, a chimney -- a note each, or a canopy)
                yield return new BuildingGenSpec { Seed = seed, Kind = BuildingKind.Apartment, MaxFloors = 1 + seed % 3, MaxRoomsPerFloor = 12, Shape = FootprintShape.L,
                                                   Porches = PorchChoice.Both, Chimney = ChimneyChoice.Yes, BackSide = seed % 2 == 0 ? Sides.East : Sides.None };
            }
        }
        static int ExtraFlatSeeds => int.TryParse(Environment.GetEnvironmentVariable("UG_APT_SEEDS"), out var n) ? n : 0;
        /// <summary>Town plots' building zones in cells (cow tools, Staging-Nyatools 40bc92dd): small, medium, large, corner,
        /// shop, apartments.</summary>
        static readonly (int W, int D)[] Plots = { (4, 5), (5, 6), (7, 7), (7, 7), (4, 6), (8, 7) };

        static List<(BuildingGenSpec Spec, BuildingGenResult Res)> _all;
        // (a fitted building answers to the spec it was drawn from)
        static List<(BuildingGenSpec Spec, BuildingGenResult Res)> All() => _all ??= Specs().Select(s => BuildingGen.Generate(s) is var r ? (r.Fitted ?? s, r) : default).ToList();

        static int FloorOf(WallPlan w) => (int)MathF.Round(w.Y / Storey);
        static (float X, float Z) Start(WallPlan w) => (w.X, w.Z);
        static (float X, float Z) Dir(WallPlan w) => (MathF.Cos(w.Yaw * MathF.PI / 180f), -MathF.Sin(w.Yaw * MathF.PI / 180f));
        /// <summary>The way a wall's front face looks: its local +Z.</summary>
        static (float X, float Z) Normal(WallPlan w) => (MathF.Sin(w.Yaw * MathF.PI / 180f), MathF.Cos(w.Yaw * MathF.PI / 180f));
        static (float X, float Z) At(WallPlan w, float u) { var d = Dir(w); return (w.X + d.X * u, w.Z + d.Z * u); }
        // a storey's walls: full height -- the bands and triangles that close a roof stand on a wall head, which is the
        // next storey's base, and are not walls of it
        static IEnumerable<WallPlan> WallsOn(BuildingGenResult r, int f) => r.Walls.Where(w => w.Kind == SurfaceKind.Wall && FloorOf(w) == f && w.Height > Storey - 0.01f);
        static GenRoom RoomAt(BuildingGenResult r, int f, float x, float z) => r.Layout.FirstOrDefault(g => g.Floor == f && g.Holds(x, z));
        static bool OnFloor(BuildingGenResult r, int f, float x, float z)
            => x > r.Footprints[f].X0 * Cell && x < r.Footprints[f].X1 * Cell && z > 0 && z < r.CellsZ * Cell && !InNotch(r, x, z);
        /// <summary>In the corner an L or a T leaves out of its bounding box (metres): outside, though inside CellsX x CellsZ.
        /// The wing is the part narrower than the rest; the rest is the full length, on the far side of the wing's base.</summary>
        static bool InNotch(BuildingGenResult r, float x, float z)
        {
            if (r.Shape == FootprintShape.Rect) return false;
            var (wx0, wz0, wx1, wz1) = r.Wing;
            bool wingNorth = wz1 == r.CellsZ;   // as laid out; turned half round, the wing is on the south
            bool wingSide = wingNorth ? z > wz0 * Cell : z < wz1 * Cell;
            return wingSide && (x < wx0 * Cell || x > wx1 * Cell);
        }
        /// <summary>A wall's footprint in plan: its mid-plane grown by half its thickness.</summary>
        static (float X0, float Z0, float X1, float Z1) Box(WallPlan w)
        {
            // exact: the length as laid (its ends already run on to their corners), the thickness across it only
            var p = Start(w); var q = At(w, w.Length); var d = Dir(w); float t = w.Thickness * 0.5f;
            float tx = MathF.Abs(d.Z) * t, tz = MathF.Abs(d.X) * t;
            return (MathF.Min(p.X, q.X) - tx, MathF.Min(p.Z, q.Z) - tz, MathF.Max(p.X, q.X) + tx, MathF.Max(p.Z, q.Z) + tz);
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
        static bool Inside((float X0, float Z0, float X1, float Z1) a, (float X0, float Z0, float X1, float Z1) b)
            => a.X0 >= b.X0 && a.X1 <= b.X1 && a.Z0 >= b.Z0 && a.Z1 <= b.Z1;
        /// <summary>A slab's footprint in plan (origin at its min-X, max-Z corner, run along +X, depth along -Z).</summary>
        static (float X0, float Z0, float X1, float Z1) Plan(WallPlan w) => (w.X, w.Z - w.Height, w.X + w.Length, w.Z);
        /// <summary>The side an outside wall looks out of: its front face, local +Z, is (sin yaw, cos yaw); north is +Z.</summary>
        static Sides FaceOf(WallPlan w)
        {
            float a = w.Yaw * MathF.PI / 180f, nx = MathF.Sin(a), nz = MathF.Cos(a);
            return MathF.Abs(nz) >= MathF.Abs(nx) ? (nz > 0 ? Sides.North : Sides.South) : (nx > 0 ? Sides.East : Sides.West);
        }
        static float RiseOf(StairPlan s) => s.Rise > 0f ? s.Rise : Storey;
        static bool StartsOnFloor(StairPlan s) => MathF.Abs(s.Y - s.Floor * Storey) < 0.01f;
        static bool EndsOnFloor(StairPlan s) => MathF.Abs(s.Y + RiseOf(s) - (s.Floor + 1) * Storey) < 0.01f;
        /// <summary>The holes in floor f: what the editor cuts round a whole flight arriving there (its footprint and a
        /// margin, EditorBuildings.FitStairsToStructure), and what the generator cut in the slab itself.</summary>
        static List<(float X0, float Z0, float X1, float Z1)> Holes(BuildingGenResult r, int f)
        {
            var hs = r.Stairs.Where(s => s.Floor == f - 1 && RiseOf(s) >= Storey - 0.01f).Select(s => Grown(Foot(s), WallOpenings.StairHoleMargin)).ToList();
            foreach (var w in r.Walls.Where(w => w.Kind == SurfaceKind.Floor && MathF.Abs(w.Y + w.Thickness * 0.5f - f * Storey) < 0.05f))
                foreach (var o in w.Openings) hs.Add((w.X + o.U, w.Z - o.V - o.Height, w.X + o.U + o.Width, w.Z - o.V));
            return hs;
        }
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

        /// <summary>Does a plot-fitted building say a room of this kind came out short (of `what`, or of anything)?</summary>
        static bool ShortBy(BuildingGenResult r, RoomKind k, string what = null)
            => r.Notes.Any(n => n.StartsWith("fitted the") && n.Contains("but short of") && n.Contains(what == null ? $"{k}: " : $"{k}: {what}"));

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
                // a block of flats has two flats a floor at the least -- two of four rooms -- and raises a smaller limit to
                // that, said so; past THAT is still a violation
                bool flats = spec.Kind == BuildingKind.Apartment;
                int perLimit = flats && res.Notes.Any(x => x.Contains("rooms a floor, not")) ? Math.Max(spec.MaxRoomsPerFloor, 2 * 4) : spec.MaxRoomsPerFloor;
                if (res.Floors < Math.Max(1, spec.MinFloors) || res.Floors > spec.MaxFloors) bad.Add($"{n}: {res.Floors} floors");
                if (real.Count < spec.MinRooms && !dropped) bad.Add($"{n}: {real.Count} rooms < {spec.MinRooms} and no note");
                // a business has a break room where a house has a kitchen (strawberry: "businesses should have different
                // room defs and rules. ie break rooms instead of kitchens")
                if (spec.Kind is not (BuildingKind.Residential or BuildingKind.Apartment) && !spec.MinOfKind.ContainsKey(RoomKind.Kitchen) && real.Any(g => g.Kind == RoomKind.Kitchen))
                    bad.Add($"{n}: a business with a kitchen");
                if (spec.Kind is BuildingKind.Residential or BuildingKind.Apartment && real.Any(g => g.Kind == RoomKind.BreakRoom)) bad.Add($"{n}: a home with a break room");
                foreach (var kv in spec.MinOfKind)
                    if (real.Count(g => g.Kind == kv.Key) < kv.Value && !dropped) bad.Add($"{n}: {real.Count(g => g.Kind == kv.Key)} {kv.Key} < {kv.Value}");
                foreach (var k in BuildingGen.Programme(spec.Kind).Always)
                    if (!real.Any(g => g.Kind == k)) bad.Add($"{n}: no {k}, which a {spec.Kind} always has");
                var per = Enumerable.Range(0, res.Floors).Select(f => real.Count(g => g.Floor == f)).ToArray();
                for (int f = 0; f < res.Floors; f++)
                {
                    if (per[f] > perLimit && !over) bad.Add($"{n}: floor {f} has {per[f]} rooms > {perLimit}");
                    if (per[f] == 0) bad.Add($"{n}: floor {f} has no room");
                }
                bool upperFull = Enumerable.Range(1, res.Floors - 1).All(f => per[f] >= spec.MaxRoomsPerFloor);
                // one kitchen (and a house's one living and dining room) unless the spec asked for more -- a block of flats one
                // kitchen and one living room in each flat, and as many flats as it has
                if (flats)
                    foreach (var flat in real.GroupBy(g => g.Unit))
                        foreach (var k in new[] { RoomKind.Kitchen, RoomKind.LivingRoom, RoomKind.DiningRoom })
                            if (flat.Count(g => g.Kind == k) > 1) bad.Add($"{n}: flat {flat.Key} has {flat.Count(g => g.Kind == k)} {k}s");
                foreach (var k in real.Select(g => g.Kind).Distinct().Where(k => !flats && BuildingGen.OnlyOne(k, spec.Kind)))
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
                    if (g.X1 - g.X0 > limit && per[g.Floor] < perLimit)
                        bad.Add($"{n}: {g} is {g.X1 - g.X0} cells long (limit {limit}) with floor {g.Floor} at {per[g.Floor]} of {spec.MaxRoomsPerFloor} rooms");
                }
                // a house's rooms keep to their floors ("which rooms prefer 2nd+ floor"); a block of flats has the same flats on
                // every floor instead, a bedroom on the ground floor as on the top one -- and every floor the same rooms
                if (flats)
                    for (int f = 1; f < res.Floors; f++)
                    {
                        string Plan(int fl) => string.Join(" ", res.Layout.Where(g => g.Floor == fl).Select(g => $"{g.Kind}[{g.X0},{g.Z0}..{g.X1},{g.Z1}]+{g.More.Count}").OrderBy(x => x));
                        prefs++;
                        if (Plan(f) != Plan(0)) bad.Add($"{n}: floor {f}'s rooms are not the ground floor's");
                    }
                foreach (var g in real.Where(_ => !flats))
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

        // P: a building fitted to a plot fits it (cow tools: "keep the zones and make the generator fit them") -- every
        // town zone, the kinds it holds, many seeds and asks -- and says what it gave up. Teeth: the same specs drawn without
        // FitPlot, where the width is only a floor, must mostly NOT fit, or this test proves nothing.
        // BREAK IT: drop the Rect lever (L/T outlines need a block wider than the wing: small plots fail); stop the compact
        // ladder at 2 (small and shop plots fail); accept a plan by its PlanOnly measure without drawing it in full.
        [Test]
        public void FittedBuildingsFitTheirPlots()
        {
            var zones = new (string Name, int W, int D, BuildingKind Kind)[]
            {
                ("small", 4, 5, BuildingKind.Residential), ("medium", 5, 6, BuildingKind.Residential), ("large", 7, 7, BuildingKind.Residential),
                ("corner", 7, 7, BuildingKind.Commercial), ("shop", 4, 6, BuildingKind.Commercial), ("apartments", 8, 7, BuildingKind.Residential),
            };
            var bad = new List<string>();
            int cases = 0, unfitted = 0, gaveUp = 0, filled = 0;
            foreach (var z in zones)
                for (int seed = 0; seed < 30; seed++)
                {
                    var spec = new BuildingGenSpec { Seed = seed, Kind = z.Kind, MinRooms = 4 + seed % 4, MaxFloors = 1 + seed % 3, MaxRoomsPerFloor = 4 + seed % 3,
                                                     Width = z.W, Depth = z.D, FitPlot = true, Porches = seed % 5 == 0 ? PorchChoice.Front : PorchChoice.None };
                    var r = BuildingGen.Generate(spec);
                    cases++;
                    string n = $"{z.Name} {z.W}x{z.D} {z.Kind} seed {seed} (min {spec.MinRooms}, {spec.MaxFloors} floors, {spec.MaxRoomsPerFloor}/floor)";
                    if (r.CellsX > z.W || r.CellsZ > z.D) bad.Add($"{n}: {r.CellsX}x{r.CellsZ}");
                    var note = r.Notes.LastOrDefault(x => x.StartsWith("fitted the"));
                    if (note == null) bad.Add($"{n}: no note of the fit");
                    else if (r.Fitted != null && note.Contains(':')) gaveUp++;
                    if (r.CellsX == z.W && r.CellsZ == z.D) filled++;
                    // the full build is the plan that was measured: same footprint
                    if (r.Fitted != null && BuildingGen.Generate(r.Fitted) is var again && (again.CellsX != r.CellsX || again.CellsZ != r.CellsZ))
                        bad.Add($"{n}: measured {r.CellsX}x{r.CellsZ}, drawn again {again.CellsX}x{again.CellsZ}");
                    spec.FitPlot = false;
                    var loose = BuildingGen.Generate(spec);
                    if (loose.CellsX > z.W || loose.CellsZ > z.D) unfitted++;
                }
            TestContext.Progress.WriteLine($"[gen-fit] {cases} plots: {cases - bad.Count} fit ({filled} fill the zone exactly, {gaveUp} gave something up); without FitPlot {unfitted} of {cases} would not; {bad.Count} violations{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(cases, Is.EqualTo(180));
            Assert.That(unfitted, Is.GreaterThan(cases / 2), "the plots are not tight enough to test fitting");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // O: the walls come out of the generator already joined at their corners -- the editor's SolveCorners (copied
        // here: walls and footings, crossing a non-parallel wall of the same storey on its span, an end within 1.6 m of
        // the crossing grows to it, and on to the far face where the crossing is at the other wall's end) finds nothing
        // to grow. What it grows it grows blind: through a wall that already fills the corner, into the room past it.
        // And no two walls share a face (the same plane, overlapping: the two fight over which one shows).
        // BREAK IT: stop a shed's side infill at the upper wall's outer face; run it on at full thickness; skip the
        // generator's own corner pass.
        [Test]
        public void WallsComeOutCornerSolved()
        {
            var bad = new List<string>();
            int walls = 0, crossings = 0;
            var grown = new Dictionary<string, int>();
            foreach (var (spec, res) in All())
            {
                var ws = res.Walls.Where(w => w.Kind == SurfaceKind.Wall || w.Kind == SurfaceKind.Foundation).ToList();
                foreach (var a in ws)
                {
                    walls++;
                    var a0 = Start(a); var ad = Dir(a);
                    float growStart = 0f, growEnd = 0f;
                    foreach (var b in ws)
                    {
                        if (b == a) continue;
                        float dy = MathF.Abs(((a.Yaw - b.Yaw) % 180f + 270f) % 180f - 90f);
                        if (dy < 20f || MathF.Abs(a.Y - b.Y) > 0.6f) continue;
                        var b0 = Start(b); var e = (At(b, b.Length).X - b0.X, At(b, b.Length).Z - b0.Z);
                        float den = ad.X * e.Item2 - ad.Z * e.Item1;
                        if (MathF.Abs(den) < 1e-6f) continue;
                        float rx = b0.X - a0.X, rz = b0.Z - a0.Z;
                        float t = (rx * e.Item2 - rz * e.Item1) / den, s2 = (rx * ad.Z - rz * ad.X) / den;
                        if (s2 < -0.02f || s2 > 1.02f) continue;
                        crossings++;
                        float target = t + (s2 < 0.08f || s2 > 0.92f ? b.Thickness * 0.5f : 0f);
                        if (target < 0f && target >= -1.6f) growStart = MathF.Max(growStart, -target);
                        else if (target > a.Length && target <= a.Length + 1.6f) growEnd = MathF.Max(growEnd, target - a.Length);
                    }
                    if (growStart <= 1e-3f && growEnd <= 1e-3f) continue;
                    string what = a.Height < Storey - 0.01f ? "an infill" : a.Thickness == BuildingGen.Ext ? "an outside wall" : "a partition";
                    grown[what] = grown.GetValueOrDefault(what) + 1;
                    bad.Add($"{Name(spec)}: {what} at ({a.X:0.00},{a.Y:0.00},{a.Z:0.00}) yaw {a.Yaw} {a.Length:0.00} m grows {growStart:0.00} at its start, {growEnd:0.00} at its end");
                }
            }
            // and no two walls share a face: parallel, on one line, one thickness, overlapping along it and in height
            int pairs = 0, shared = 0;
            foreach (var (spec, res) in All())
            {
                var ws = res.Walls.Where(w => w.Kind == SurfaceKind.Wall).ToList();
                (bool AlongX, float Line, float Lo, float Hi) Seg(WallPlan w)
                {
                    var p = Start(w); var q = At(w, w.Length);
                    bool ax = MathF.Abs(p.Z - q.Z) < 0.01f;
                    return ax ? (true, p.Z, MathF.Min(p.X, q.X), MathF.Max(p.X, q.X)) : (false, p.X, MathF.Min(p.Z, q.Z), MathF.Max(p.Z, q.Z));
                }
                for (int i = 0; i < ws.Count; i++)
                    for (int j = i + 1; j < ws.Count; j++)
                    {
                        var (a, b) = (ws[i], ws[j]);
                        var (sa, sb) = (Seg(a), Seg(b));
                        if (sa.AlongX != sb.AlongX || MathF.Abs(sa.Line - sb.Line) > 0.01f) continue;
                        pairs++;
                        if (MathF.Abs(a.Thickness - b.Thickness) > 0.005f) continue;
                        float along = MathF.Min(sa.Hi, sb.Hi) - MathF.Max(sa.Lo, sb.Lo);
                        float up = MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y);
                        if (along > 0.05f && up > 0.05f)
                        {
                            shared++;
                            bad.Add($"{Name(spec)}: walls at ({a.X:0.00},{a.Y:0.00},{a.Z:0.00}) and ({b.X:0.00},{b.Y:0.00},{b.Z:0.00}) share a face {along:0.00} m along and {up:0.00} m up");
                        }
                    }
            }
            TestContext.Progress.WriteLine($"[gen-solve] {walls} walls, {crossings} crossings; {grown.Values.Sum()} would grow ({string.Join(", ", grown.Select(kv => $"{kv.Value} {kv.Key}"))}); {pairs} pairs of walls on one line, {shared} sharing a face{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(pairs, Is.GreaterThan(1000));
            Assert.That(crossings, Is.GreaterThan(1000));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // C: the walls enclose exactly the planned rooms -- found by RoomEnclosure from the wall centrelines, and each
        // designation resolves to the room of its kind with the planned area (a hall's landing included). Open plan does
        // not change this: the wall between two rooms that open into each other is kept, cut away (OpenPlan below), so
        // the two stay two rooms to the editor -- the floors with an open span are counted.
        // BREAK IT: leave out the partitions between rooms; wall a hall off from its own landing; leave an open plan's
        // wall out instead of cutting it.
        [Test]
        public void WallsEncloseThePlannedRooms()
        {
            var bad = new List<string>();
            int floors = 0, found = 0, openFloors = 0;
            foreach (var (spec, res) in All())
                for (int f = 0; f < res.Floors; f++)
                {
                    floors++;
                    if (WallsOn(res, f).Any(w => w.Openings.Any(o => OpenSpan(w, o)))) openFloors++;
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
            TestContext.Progress.WriteLine($"[gen-enclose] {floors} floors ({openFloors} with an open plan's span), {found} rooms enclosed by the walls; {bad.Count} mismatches{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(found, Is.GreaterThan(1000));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // C2: open plan (strawberry, 2026-10-13: "allow buildings to actually omit interior walls, within reason. ie a
        // kitchen can open into a living room. but not a bathroom into a kitchen or something. both rooms keep their room
        // definitions, floor paints, but can open into eachother"). Found from the walls, not the generator's list: a cut
        // in a partition from the floor up, no door hung in it, wider than any doorway (a wide one is at most
        // WideOpening). Each runs between ONE pair of rooms that may share a space by this test's own list -- a living
        // room, a kitchen, a dining room, a hall, not two halls -- in a house or inside one flat: never a bedroom,
        // bathroom, closet, office, garage or store, never across a party wall or into the corridor, never in a business.
        // Clear of every flight, landing, hole and flight-end tile by a metre. And both rooms keep what they were: the
        // walls still enclose each on its own (RoomEnclosure, as the editor finds rooms), every designation resolving with
        // no orphan and each side of the span to its own room's kind; each room's floor the texel of its kind, its cover
        // inside its own cells.
        // BREAK IT: let MayOpenPlan take a bedroom; drop its Unit check; open beside the stair bay. And here, on fresh
        // buildings: a span cut into a bedroom, one across a party wall, and an open plan's wall left out altogether (the
        // two rooms one enclosure: a designation orphaned) -- each must be caught.
        static bool SharesASpace(RoomKind k) => k is RoomKind.LivingRoom or RoomKind.Kitchen or RoomKind.DiningRoom or RoomKind.Hall;
        /// <summary>An open plan's span: in a partition, walkable, no door hung, wider than any doorway.</summary>
        static bool OpenSpan(WallPlan w, WallOpening o) => w.Thickness < BuildingGen.Ext - 0.01f && Walkable(o) && !o.HasDoor && o.Width > BuildingGen.WideOpening + 0.01f;
        /// <summary>A wall's footprint less its open spans: the wall that is there to stand a piece against or to see past.</summary>
        static IEnumerable<(float X0, float Z0, float X1, float Z1)> SolidBoxes(WallPlan w)
        {
            var cuts = w.Openings.Where(o => OpenSpan(w, o)).Select(o => (A: o.U, B: o.U1)).OrderBy(c => c.A).ToList();
            var d = Dir(w); float t = w.Thickness * 0.5f, tx = MathF.Abs(d.Z) * t, tz = MathF.Abs(d.X) * t;
            (float, float, float, float) Part(float u0, float u1) { var p = At(w, u0); var q = At(w, u1); return (MathF.Min(p.X, q.X) - tx, MathF.Min(p.Z, q.Z) - tz, MathF.Max(p.X, q.X) + tx, MathF.Max(p.Z, q.Z) + tz); }
            float u = 0f;
            foreach (var (a, b) in cuts) { if (a > u + 1e-3f) yield return Part(u, a); u = MathF.Max(u, b); }
            if (w.Length > u + 1e-3f) yield return Part(u, w.Length);
        }

        static List<string> OpenPlanViolations(BuildingGenResult res, string name, Dictionary<string, int> tally = null)
        {
            var bad = new List<string>();
            void T(string k) { if (tally != null) tally[k] = tally.GetValueOrDefault(k) + 1; }
            int spans = 0;
            for (int f = 0; f < res.Floors; f++)
            {
                var walls = WallsOn(res, f).ToList();
                // the stairs on this floor: its flights, landings and holes, and the full tile at a flight's foot or top -- a
                // span by any of them is a wall the stairs stand against or step off beside
                var stairs = res.Stairs.Where(s => s.Floor == f).Select(Foot).Concat(res.Walls.Where(w => w.Kind == SurfaceKind.Stairs && FloorOf(w) == f).Select(Plan))
                                .Concat(Holes(res, f)).ToList();
                foreach (var st in res.Stairs)
                {
                    var (bx, bz) = st.Back; var (rx, rz) = st.Right;
                    (float, float, float, float) Tile(float ox, float oz, float dir)
                    {
                        float ex = ox + bx * Cell * dir + rx * st.Width, ez = oz + bz * Cell * dir + rz * st.Width;
                        return (MathF.Min(ox, ex), MathF.Min(oz, ez), MathF.Max(ox, ex), MathF.Max(oz, ez));
                    }
                    if (st.Floor == f && StartsOnFloor(st)) stairs.Add(Tile(st.X, st.Z, -1f));
                    if (st.Floor == f - 1 && EndsOnFloor(st)) stairs.Add(Tile(st.X + bx * st.Run, st.Z + bz * st.Run, +1f));
                }
                // the rooms as the editor finds them, from the wall centrelines: a wall left out makes two rooms one, and
                // one of their designations an orphan
                var rooms = RoomEnclosure.Find(walls.Select(x => { var e = At(x, x.Length); return new Seg(x.X, x.Z, e.X, e.Z, -1, x.Thickness); }).ToList());
                RoomDesignations.Resolve(rooms, res.Rooms.Where(x => x.Floor == f).ToList(), out var byRoom, out var orphans);
                if (orphans.Count > 0) bad.Add($"{name} floor {f}: {orphans.Count} designations match no room ({string.Join(", ", orphans.Select(x => x.Kind))})");
                var checkedFloor = new HashSet<GenRoom>();
                foreach (var w in walls)
                    foreach (var o in w.Openings)
                    {
                        var mid = At(w, o.U + o.Width * 0.5f);
                        string at = $"{name} floor {f}: the span at ({mid.X:0.0},{mid.Z:0.0})";
                        // (the generator's mark for one -- the porch archetype in a partition -- is one by this measure too)
                        if (w.Thickness < BuildingGen.Ext - 0.01f && o.Archetype == BuildingGen.ArchPorch && !OpenSpan(w, o)) bad.Add($"{at} is cut {o.Width:0.00} x {o.Height:0.00} from {o.V:0.00}, door {o.HasDoor}: not an open span");
                        if (!OpenSpan(w, o)) continue;
                        spans++; T("spans");
                        // the rooms either side, all along it
                        var n = Normal(w); float d = w.Thickness * 0.5f + 0.3f;
                        var sides = new HashSet<(GenRoom, GenRoom)>();
                        for (int i = 0; i <= 8; i++)
                        {
                            var c = At(w, o.U + 0.05f + (o.Width - 0.1f) * i / 8f);
                            sides.Add((RoomAt(res, f, c.X + n.X * d, c.Z + n.Z * d), RoomAt(res, f, c.X - n.X * d, c.Z - n.Z * d)));
                        }
                        if (sides.Count != 1) { bad.Add($"{at} runs between {sides.Count} pairs of rooms"); continue; }
                        var (a, b) = sides.First();
                        if (a == null || b == null) { bad.Add($"{at} opens onto no room"); continue; }
                        if (string.CompareOrdinal(a.Kind.ToString(), b.Kind.ToString()) > 0) (a, b) = (b, a);
                        T($"{res.Kind} {a.Kind}-{b.Kind}");
                        if (!(res.Kind is BuildingKind.Residential or BuildingKind.Apartment)) bad.Add($"{at}: a {res.Kind} with its {a.Kind} open to its {b.Kind}");
                        if (!SharesASpace(a.Kind) || !SharesASpace(b.Kind) || (a.Kind == RoomKind.Hall && b.Kind == RoomKind.Hall)) bad.Add($"{at}: {a} is open to {b}");
                        if (a.Unit != b.Unit) bad.Add($"{at}: {(a.Unit < 0 ? "shared space" : $"flat {a.Unit}")}'s {a.Kind} is open to {(b.Unit < 0 ? "shared space" : $"flat {b.Unit}")}'s {b.Kind}");
                        else if (res.Kind == BuildingKind.Apartment && a.Unit < 0) bad.Add($"{at}: the shared {a.Kind} is open to the shared {b.Kind}");
                        // clear of the stairs: a metre either side of the wall along the span meets no flight, landing, hole or end tile
                        var p0 = At(w, o.U); var p1 = At(w, o.U1); float r = w.Thickness * 0.5f + 1f;
                        var reach = (MathF.Min(p0.X, p1.X) - MathF.Abs(n.X) * r, MathF.Min(p0.Z, p1.Z) - MathF.Abs(n.Z) * r, MathF.Max(p0.X, p1.X) + MathF.Abs(n.X) * r, MathF.Max(p0.Z, p1.Z) + MathF.Abs(n.Z) * r);
                        if (stairs.Any(s => Overlap(reach, s))) bad.Add($"{at} is beside the stairs");
                        // both rooms keep their designations: the walls still enclose each, and each resolves to its kind
                        (float X, float Z) pa = (mid.X + n.X * d, mid.Z + n.Z * d), pb = (mid.X - n.X * d, mid.Z - n.Z * d);
                        var (ga, gb) = (RoomAt(res, f, pa.X, pa.Z), RoomAt(res, f, pb.X, pb.Z));
                        int ia = rooms.FindIndex(x => RoomDesignations.Contains(x.Outline, pa.X, pa.Z)), ib = rooms.FindIndex(x => RoomDesignations.Contains(x.Outline, pb.X, pb.Z));
                        if (ia < 0 || ib < 0 || ia == ib) bad.Add($"{at}: {ga} and {gb} are {(ia == ib && ia >= 0 ? "one room" : "not enclosed")} to the editor");
                        else if (byRoom[ia] != ga.Kind || byRoom[ib] != gb.Kind) bad.Add($"{at}: {ga} and {gb} resolve as {byRoom[ia]} and {byRoom[ib]}");
                        else T("sides keeping their designations");
                        // and their floors: each the texel of its kind -- a cover of its own, inside its own cells, where its
                        // kind's is not the building's floor colour; none where it is
                        if (BuildingGen.FloorTexels.TryGetValue(res.Palette ?? "", out var ft))
                            foreach (var g in new[] { a, b })
                            {
                                if (!checkedFloor.Add(g)) continue;
                                int want = BuildingGen.FloorTexelFor(res.Palette, g.Kind);
                                var covers = res.Walls.Where(x => x.Kind == SurfaceKind.Floor && MathF.Abs(x.Thickness - 0.04f) < 0.005f && MathF.Abs(x.Y - f * Storey) < 0.05f)
                                                      .Where(x => { var pl = Plan(x); return g.Holds((pl.X0 + pl.X1) * 0.5f, (pl.Z0 + pl.Z1) * 0.5f); }).ToList();
                                float area = 0f;
                                foreach (var c in covers)
                                {
                                    var pl = Plan(c);
                                    if (!g.Rects.Any(q => Inside(pl, (q.X0 * Cell - 0.01f, q.Z0 * Cell - 0.01f, q.X1 * Cell + 0.01f, q.Z1 * Cell + 0.01f)))) bad.Add($"{at}: {g}'s floor cover runs out of its cells");
                                    if (c.Texel != want) bad.Add($"{at}: {g}'s floor is texel {c.Texel}, its kind's {want}");
                                    area += (pl.X1 - pl.X0) * (pl.Z1 - pl.Z0);
                                }
                                if (want != ft.Floor && MathF.Abs(area - g.Cells * Cell * Cell) > 0.5f) bad.Add($"{at}: {g}'s cover is {area:0.0} m2 of its {g.Cells * Cell * Cell} (texel {want})");
                                if (want == ft.Floor && covers.Count > 0) bad.Add($"{at}: {g} has a cover over the building's floor colour");
                                T("rooms whose floor kept its own colour");
                            }
                    }
            }
            if (spans > 0) T($"{res.Kind} buildings with a span");
            return bad;
        }

        /// <summary>A span cut in a partition of floor 0 between rooms `pick` likes, as open plan cuts one -- for breaking a
        /// building on purpose. True when one was cut.</summary>
        static bool CutSpan(BuildingGenResult res, Func<GenRoom, GenRoom, bool> pick)
        {
            const float Wd = 5f;
            foreach (var w in WallsOn(res, 0).Where(w => w.Thickness < BuildingGen.Ext - 0.01f))
                for (float u = 0.6f; u + Wd + 0.6f <= w.Length; u += 0.25f)
                {
                    var n = Normal(w);
                    var pairs = Enumerable.Range(0, 5).Select(i => At(w, u + Wd * i / 4f)).Select(c => (RoomAt(res, 0, c.X + n.X * 0.6f, c.Z + n.Z * 0.6f), RoomAt(res, 0, c.X - n.X * 0.6f, c.Z - n.Z * 0.6f))).Distinct().ToList();
                    if (pairs.Count != 1 || pairs[0].Item1 == null || pairs[0].Item2 == null || !(pick(pairs[0].Item1, pairs[0].Item2) || pick(pairs[0].Item2, pairs[0].Item1))) continue;
                    if (w.Openings.Any(o => o.U < u + Wd + 0.3f && o.U1 > u - 0.3f)) continue;
                    w.Openings.Add(new WallOpening(u, 0f, Wd, BuildingGen.OpeningTop - BuildingGen.OpenBeam, 999f, BuildingGen.ArchPorch));
                    return true;
                }
            return false;
        }

        [Test]
        public void OpenPlanOnlyJoinsRoomsThatShareASpace()
        {
            var bad = new List<string>();
            var tally = new Dictionary<string, int>();
            int homes = 0;
            foreach (var (spec, res) in All())
            {
                if (spec.Kind is BuildingKind.Residential or BuildingKind.Apartment) homes++;
                bad.AddRange(OpenPlanViolations(res, Name(spec), tally));
            }
            int spans = tally.GetValueOrDefault("spans");
            TestContext.Progress.WriteLine($"[gen-open] {spans} open spans in {tally.GetValueOrDefault("Residential buildings with a span") + tally.GetValueOrDefault("Apartment buildings with a span")} of {homes} homes ({tally.GetValueOrDefault("Residential buildings with a span")} houses, {tally.GetValueOrDefault("Apartment buildings with a span")} blocks of flats), a span in any other kind of building in {tally.Where(kv => kv.Key.EndsWith("buildings with a span") && !kv.Key.StartsWith("Residential") && !kv.Key.StartsWith("Apartment")).Sum(kv => kv.Value)}; by the rooms either side: {string.Join(", ", tally.Where(kv => kv.Key.Contains('-')).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"))}; {tally.GetValueOrDefault("sides keeping their designations")} of {spans} spans with both sides their own room to the editor; {tally.GetValueOrDefault("rooms whose floor kept its own colour")} rooms' floors checked; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(spans, Is.GreaterThan(100), "too few open spans to say anything");
            Assert.That(tally.Where(kv => kv.Key.StartsWith("Apartment ") && kv.Key.Contains('-')).Sum(kv => kv.Value), Is.GreaterThan(10), "flats never open up");
            Assert.That(tally.GetValueOrDefault("Residential Kitchen-LivingRoom"), Is.GreaterThan(10), "a kitchen never opens into a living room");
            Assert.That(tally.GetValueOrDefault("sides keeping their designations"), Is.EqualTo(spans));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));

            // the checks have teeth: each way of breaking it is caught, on a fresh building that has a span and the rooms
            var probe = Enumerable.Range(0, 60).Select(sd => new BuildingGenSpec { Seed = sd, Kind = BuildingKind.Residential, MinRooms = 7, MaxFloors = 1, MaxRoomsPerFloor = 8 })
                                  .First(sp => BuildingGen.Generate(sp) is var r && r.OpenPlan.Count > 0 && r.Layout.Any(g => g.Kind == RoomKind.Bedroom && g.Floor == 0));
            Assert.That(OpenPlanViolations(BuildingGen.Generate(probe), "control"), Is.Empty, "the control house");
            void Broken(string what, BuildingGenSpec sp, Func<BuildingGenResult, bool> breakIt, string expect)
            {
                var r = BuildingGen.Generate(sp);
                Assert.That(breakIt(r), Is.True, $"{what}: nothing to break");
                var v = OpenPlanViolations(r, what);
                TestContext.Progress.WriteLine($"[gen-open] break it: {what} -> {v.Count} violations{(v.Count > 0 ? ": " + v[0] : "")}");
                Assert.That(v.Any(x => x.Contains(expect)), Is.True, $"{what} was not caught: {string.Join("; ", v)}");
            }
            Broken("a span into a bedroom", probe, r => CutSpan(r, (a, b) => a.Kind == RoomKind.Bedroom), "is open to");
            // (the wall a span is cut in, taken out whole: what open plan would be if it left the wall out)
            Broken("an open plan's wall left out", probe, r => r.Walls.Remove(WallsOn(r, 0).First(x => x.Openings.Any(o => OpenSpan(x, o)))), "match no room");
            var flats = Enumerable.Range(0, 40).Select(sd => new BuildingGenSpec { Seed = sd, Kind = BuildingKind.Apartment, MinFloors = 2, MaxFloors = 2, MaxRoomsPerFloor = 16 })
                                  .First(sp => CutSpan(BuildingGen.Generate(sp), (a, b) => a.Unit >= 0 && b.Unit >= 0 && a.Unit != b.Unit));
            Broken("a span across a party wall", flats, r => CutSpan(r, (a, b) => a.Unit >= 0 && b.Unit >= 0 && a.Unit != b.Unit), "is open to");
        }

        // D: every room can be walked to from outside without walking THROUGH a private room or a store -- through the
        // doors as placed and up the flights -- and a dead end has one way in (its children aside: a closet or en-suite
        // off a bedroom, a store off a garage). strawberry: "bedrooms, bathrooms, offices are private". In a block of flats,
        // nor through ANOTHER flat: the walk never steps from one flat's room into another's (the corridor and the stairwell
        // are everyone's) -- ApartmentsAreFlatsOffASharedCorridor checks that no door even offers to.
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
                // walk in: on through rooms you may walk through, into a dead end, and from a dead end only to its own children;
                // never from one flat into another
                bool Child(GenRoom c, GenRoom p) => DeadEnd(p.Kind) && ChildOf(c.Kind, p.Kind);
                bool Trespass(GenRoom a, GenRoom b) => a.Unit >= 0 && b.Unit >= 0 && a.Unit != b.Unit;
                var seen = new HashSet<GenRoom> { outside };
                var q = new Queue<GenRoom>(); q.Enqueue(outside);
                while (q.Count > 0)
                {
                    var a = q.Dequeue();
                    if (!edges.TryGetValue(a, out var nb)) continue;
                    foreach (var b in nb)
                    {
                        if (seen.Contains(b)) continue;
                        if (!(a == outside || !DeadEnd(a.Kind) || Child(b, a)) || Trespass(a, b)) continue;
                        seen.Add(b); q.Enqueue(b);
                    }
                }
                foreach (var g in res.Layout) { total++; if (seen.Contains(g)) reached++; else bad.Add($"{Name(spec)}: {g} cannot be reached without walking through a private room or another flat"); }
                // a dead end: exactly one door to a room that is not its child (outside does not count: a garage's vehicle door)
                foreach (var g in res.Layout.Where(g => DeadEnd(g.Kind)))
                {
                    deadEnds++;
                    var ways = edges.TryGetValue(g, out var nb) ? nb.Where(o => o != outside && !Child(o, g)).ToList() : new List<GenRoom>();
                    if (ways.Count != 1) bad.Add($"{Name(spec)}: {g} has {ways.Count} ways in ({string.Join(", ", ways)})");
                    if (ways.Count == 1 && DeadEnd(ways[0].Kind)) suites++;
                }
            }
            TestContext.Progress.WriteLine($"[gen-reach] {doors} doors, {flights} flights; {reached} of {total} rooms reached without walking through a private room or another flat; {deadEnds} dead ends ({suites} off a bedroom/garage/office){(bad.Count > 0 ? "; first: " + bad[0] : "")}");
            Assert.That(flights, Is.GreaterThan(200));
            Assert.That(suites, Is.GreaterThan(0), "no closet or en-suite ever opens off a bedroom");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // E: each flight stands in ONE room you walk through (never a bedroom, bathroom, office -- nor a store room or
        // garage, which would make the floor above reachable only through a dead end), clear of every wall; every hole in
        // a floor is in one such room and clear of every wall; and in the stair room on every floor, every door, the foot
        // of the flight up and the top of the flight from below are joined by floor you can stand on (0.1 m grid). A full
        // tile of floor at the foot and at the top ("stairs should want 1 full tile at the foot and 1 tile on the landing
        // above"); a switchback's landing, at its flights' height, at least as deep as they are wide; and a door's height
        // clear above every tread and landing -- which is what keeps a flight from running under the one above it.
        // BREAK IT: the flight away from the wall (hole under it); both flights in one row (treads over the hole); a door
        // allowed into the bay (onto the steps); the stairs in any room; a three-cell bay (half a tile at each end); a
        // switchback's hole over its second flight only, or not over the landing; its landing short or at the wrong
        // height; its second flight in the first one's row.
        [Test]
        public void StairsStandInOpenRoomsAndLandOnFloor()
        {
            var bad = new List<string>();
            int flights = 0, halves = 0, landingsChecked = 0, walked = 0, tiles = 0, holes = 0, headroomPoints = 0, quarterLandings = 0;
            float leastClear = float.MaxValue;
            const float M = WallOpenings.StairHoleMargin;
            const float DoorTall = 2.85f;   // a door's height: what you walk through upright, you walk up upright
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
                var landings = res.Walls.Where(w => w.Kind == SurfaceKind.Stairs).ToList();
                // a quarter turn's landing is in a corner: walls within 0.7 m of two of its sides (strawberry: "corner
                // stairs need to be up against 2 supporting walls")
                if (res.StairShape == StairShape.Quarter)
                    foreach (var l in landings)
                    {
                        var (lx0, lz0, lx1, lz1) = Plan(l);
                        int lf = (int)MathF.Floor((l.Y + 0.1f) / Storey);
                        var ws = WallsOn(res, lf).Select(Box).ToList();
                        bool Near((float X0, float Z0, float X1, float Z1) side) => ws.Any(b => Overlap(side, b, 0f));
                        int sides = new[] { (lx0 - 0.7f, lz0, lx0, lz1), (lx1, lz0, lx1 + 0.7f, lz1), (lx0, lz0 - 0.7f, lx1, lz0), (lx0, lz1, lx1, lz1 + 0.7f) }.Count(Near);
                        quarterLandings++;
                        if (sides < 2) bad.Add($"{Name(spec)}: a quarter turn's landing at ({lx0:0.0},{lz0:0.0}) has a wall on {sides} side(s), not in a corner");
                    }
                foreach (var s in res.Stairs)
                {
                    flights++;
                    bool half = RiseOf(s) < Storey - 0.01f;
                    if (half) halves++;
                    var fp = Foot(s);
                    var low = Holder(fp, s.Floor);
                    var high = Holder(Grown(fp, M), s.Floor + 1);
                    if (low == null || DeadEnd(low.Kind)) bad.Add($"{Name(spec)}: flight {s.Floor} stands in {low?.ToString() ?? "more than one room"}");
                    else hostKinds[low.Kind] = hostKinds.GetValueOrDefault(low.Kind) + 1;
                    if (low?.Kind == RoomKind.Shop) bad.Add($"{Name(spec)}: flight {s.Floor} stands on a shop floor");
                    if (high == null || DeadEnd(high.Kind)) bad.Add($"{Name(spec)}: the room over flight {s.Floor} is {high?.ToString() ?? "more than one room"}");
                    foreach (var w in WallsOn(res, s.Floor)) if (Overlap(fp, Box(w))) bad.Add($"{Name(spec)}: flight {s.Floor} runs into a wall at yaw {w.Yaw}");
                    // (a whole flight never stands over the hole the one below cut; a switchback's do, a storey above it,
                    // and the headroom check below is what says that is all right)
                    if (!half && Holes(res, s.Floor).Any(h => Overlap(fp, h, 0f))) bad.Add($"{Name(spec)}: flight {s.Floor} stands over the hole in its floor");
                    var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                    (float, float, float, float) Tile(float ox, float oz, float dir, float depth)
                    {
                        float ex = ox + bx * depth * dir + rx * s.Width, ez = oz + bz * depth * dir + rz * s.Width;
                        return (MathF.Min(ox, ex), MathF.Min(oz, ez), MathF.Max(ox, ex), MathF.Max(oz, ez));
                    }
                    float topX = s.X + bx * s.Run, topZ = s.Z + bz * s.Run;
                    // a full tile at whichever end meets a floor: the 3 m before the foot or past the top, the flight's width
                    // wide, all floor of one room -- no wall, no flight or landing, no hole in it but the top's own margin
                    var ends = new List<((float, float, float, float) Tile, int F, string What, GenRoom Room)>();
                    if (StartsOnFloor(s)) ends.Add((Tile(s.X, s.Z, -1f, Cell), s.Floor, "foot", low));
                    if (EndsOnFloor(s)) ends.Add((Tile(topX, topZ, +1f, Cell), s.Floor + 1, "landing", high));
                    foreach (var (tile, f, what, room) in ends)
                    {
                        tiles++;
                        var inner = Grown(tile, -0.4f);   // a wall's half thickness may stand at the tile's far edge
                        if (Holder(inner, f) != room) bad.Add($"{Name(spec)}: flight {s.Floor}'s {what} tile is not all {room}");
                        foreach (var w in WallsOn(res, f)) if (Overlap(inner, Box(w))) bad.Add($"{Name(spec)}: a wall stands in flight {s.Floor}'s {what} tile");
                        foreach (var o in res.Stairs.Where(o => o.Floor == f)) if (Overlap(Grown(tile, -0.01f), Foot(o))) bad.Add($"{Name(spec)}: flight {o.Floor} is in flight {s.Floor}'s {what} tile");
                        foreach (var l in landings.Where(l => FloorOf(l) == f)) if (Overlap(Grown(tile, -0.01f), Plan(l))) bad.Add($"{Name(spec)}: a landing is in flight {s.Floor}'s {what} tile");
                        foreach (var h in Holes(res, f)) if (Overlap(Grown(tile, what == "landing" ? -M - 0.01f : -0.01f), h)) bad.Add($"{Name(spec)}: a hole in floor {f} is in flight {s.Floor}'s {what} tile");
                    }
                    // and at whichever end stops between floors, a landing at that height as deep as the flight is wide
                    var mids = new List<((float, float, float, float) Sq, float Y, string What)>();
                    if (!StartsOnFloor(s)) mids.Add((Tile(s.X, s.Z, -1f, s.Width), s.Y, "foot"));
                    if (!EndsOnFloor(s)) mids.Add((Tile(topX, topZ, +1f, s.Width), s.Y + RiseOf(s), "top"));
                    foreach (var (sq, y, what) in mids)
                    {
                        landingsChecked++;
                        bool held = landings.Any(l => MathF.Abs(l.Y + l.Thickness * 0.5f - y) < 0.02f && Inside(Grown(sq, -0.02f), Plan(l)));
                        if (!held) bad.Add($"{Name(spec)}: flight {s.Floor}'s {what} stops at {y:0.00} m with no landing under the square past it");
                    }
                    // a door's height clear over every tread
                    int steps = WallOpenings.StairSteps(RiseOf(s));
                    float going = s.Run / steps, stepRise = RiseOf(s) / steps;
                    for (int k = 0; k < steps; k++)
                        foreach (float across in new[] { 0.15f, 0.5f, 0.85f })
                        {
                            float d = (k + 0.5f) * going, x = s.X + bx * d + rx * s.Width * across, z = s.Z + bz * d + rz * s.Width * across;
                            Headroom(res, s.Y + (k + 1) * stepRise, x, z, $"flight {s.Floor}'s step {k + 1}");
                        }
                }
                foreach (var l in landings)
                {
                    var (x0, z0, x1, z1) = Plan(l);
                    for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
                        Headroom(res, l.Y + l.Thickness * 0.5f, x0 + (x1 - x0) * (i + 0.5f) / 3f, z0 + (z1 - z0) * (j + 0.5f) / 3f, $"a landing at {l.Y + l.Thickness * 0.5f:0.00} m");
                }
                void Headroom(BuildingGenResult r, float y0, float x, float z, string what)
                {
                    headroomPoints++;
                    float lid = float.MaxValue;
                    bool In((float X0, float Z0, float X1, float Z1) a) => x > a.X0 && x < a.X1 && z > a.Z0 && z < a.Z1;
                    foreach (var o in r.Stairs)
                    {
                        if (!In(Foot(o))) continue;
                        var (obx, obz) = o.Back;
                        int n = WallOpenings.StairSteps(RiseOf(o));
                        float d = (x - o.X) * obx + (z - o.Z) * obz;
                        int k = Math.Clamp((int)MathF.Floor(d / (o.Run / n)), 0, n - 1);
                        float under = o.Y + (k + 1) * RiseOf(o) / n - WallOpenings.StairTreadThickness;
                        if (under > y0 + 0.01f) lid = MathF.Min(lid, under);
                    }
                    foreach (var l in landings)
                        if (In(Plan(l)) && l.Y - l.Thickness * 0.5f > y0 + 0.01f) lid = MathF.Min(lid, l.Y - l.Thickness * 0.5f);
                    for (int f = 1; f < r.Floors; f++)
                    {
                        float under = f * Storey - BuildingGen.Slab;
                        // (a hole cut as two rectangles -- a quarter turn's L -- has a seam between them: a point on it is in
                        // the hole, so each is grown a millimetre)
                        if (under > y0 + 0.01f && OnFloor(r, f, x, z) && !Holes(r, f).Any(h => In(Grown(h, 1e-3f)))) lid = MathF.Min(lid, under);
                    }
                    if (lid == float.MaxValue) return;   // only the roof over it
                    leastClear = MathF.Min(leastClear, lid - y0);
                    if (lid - y0 < DoorTall) bad.Add($"{Name(spec)}: {what} has {lid - y0:0.00} m over it at ({x:0.0},{z:0.0})");
                }
                // every hole is in one walk-through room, clear of the walls round it
                for (int f = 1; f < res.Floors; f++)
                    foreach (var h in Holes(res, f))
                    {
                        holes++;
                        var room = Holder(h, f);
                        if (room == null || DeadEnd(room.Kind)) bad.Add($"{Name(spec)}: a hole in floor {f} is in {room?.ToString() ?? "more than one room"}");
                        foreach (var w in WallsOn(res, f)) if (Overlap(h, Box(w))) bad.Add($"{Name(spec)}: a hole in floor {f} runs under a wall (yaw {w.Yaw}, thick {w.Thickness})");
                    }
                // walk each floor's stair room on a 0.1 m grid
                for (int f = 0; f < res.Floors; f++)
                {
                    var up = res.Stairs.Where(s => s.Floor == f && StartsOnFloor(s)).ToList();
                    var down = res.Stairs.Where(s => s.Floor == f - 1 && EndsOnFloor(s)).ToList();
                    var host = up.Count > 0 ? Holder(Foot(up[0]), f) : Holder(Grown(Foot(down[0]), M), f);
                    if (host == null) continue;   // reported above
                    float gx0 = host.Rects.Min(r => r.X0) * Cell, gz0 = host.Rects.Min(r => r.Z0) * Cell;
                    float gx1 = host.Rects.Max(r => r.X1) * Cell, gz1 = host.Rects.Max(r => r.Z1) * Cell;
                    int nx = (int)((gx1 - gx0) / 0.1f), nz = (int)((gz1 - gz0) / 0.1f);
                    var blocks = res.Stairs.Where(s => s.Floor == f).Select(Foot).Concat(landings.Where(l => FloorOf(l) == f).Select(Plan))
                                    .Concat(Holes(res, f)).Concat(WallsOn(res, f).Select(Box)).ToList();
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
            int switchbacks = All().Count(x => x.Res.StairShape == StairShape.Switchback), quarters = All().Count(x => x.Res.StairShape == StairShape.Quarter);
            int multis = All().Count(x => x.Res.Floors > 1);
            TestContext.Progress.WriteLine($"[gen-stairs] {flights} flights ({halves} half flights of {switchbacks} switchbacks and {quarters} quarter turns, in {multis} buildings with stairs), in {string.Join(", ", hostKinds.Select(kv => $"{kv.Value} {kv.Key}"))}; {tiles} foot/top tiles, {landingsChecked} landings, {holes} floor holes checked; {headroomPoints} points' headroom, the least {leastClear:0.00} m; {walked} stair rooms walked; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(flights, Is.GreaterThan(200));
            Assert.That(switchbacks + quarters, Is.GreaterThan(multis / 4), "compound stairs are rare");
            Assert.That(switchbacks, Is.GreaterThan(multis / 10), "switchbacks are rare");
            Assert.That(quarters, Is.GreaterThan(multis / 10), "quarter turns are rare");
            Assert.That(quarterLandings, Is.GreaterThan(0));
            Assert.That(multis - switchbacks - quarters, Is.GreaterThan(multis / 4), "straight flights are rare");
            Assert.That(landingsChecked, Is.EqualTo(halves));
            Assert.That(walked, Is.GreaterThan(300));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // E2: a compound flight's landing rests on two walls (strawberry: "the landing of the stairs is only supported by 1
        // wall, should require two"; before that, "corner stairs need to be up against 2 supporting walls"): on the floor
        // it stands in, SOLID wall -- no opening in it there -- with its face within 0.15 m of nine tenths of one of the
        // landing's east/west sides and of one of its north/south sides. A corner: an outside one, or an outside wall
        // and a partition. Every floor a flight climbs from is checked, so a third floor's landing is too. (A straight
        // flight has no landing: its top end is the floor above's.)
        // BREAK IT: lay the switchback's landing at the bay's east end whatever end the floors above start from; put the
        // bay anywhere in its room rather than at the end its landing is on; stop the landing short of the walls by the
        // hole margin, as it was.
        [Test]
        public void LandingsRestOnTwoWalls()
        {
            const float Reach = 0.15f, Most = 0.9f;
            var bad = new List<string>();
            var byShape = new Dictionary<StairShape, (int All, int Two)>();
            int straight = 0, upper = 0;
            foreach (var (spec, res) in All())
            {
                if (res.Floors < 2) continue;
                if (res.StairShape == StairShape.Straight) { straight++; continue; }
                foreach (var l in res.Walls.Where(w => w.Kind == SurfaceKind.Stairs))
                {
                    var (x0, z0, x1, z1) = Plan(l);
                    int f = FloorOf(l);
                    if (f > 0) upper++;
                    var ws = WallsOn(res, f).ToList();
                    // how much of one side of the landing (the line `at`, from a0 to a1 along it) has solid wall facing it
                    // within Reach, out on the side `outward` (+1: toward +X or +Z)
                    float Held(bool alongX, float at, float a0, float a1, int outward)
                    {
                        var spans = new List<(float A, float B)>();
                        foreach (var w in ws)
                        {
                            var b = Box(w);
                            float face = alongX ? (outward > 0 ? b.Z0 : b.Z1) : (outward > 0 ? b.X0 : b.X1);
                            float gap = outward * (face - at);
                            if (gap < -0.05f || gap > Reach) continue;
                            float lo = MathF.Max(alongX ? b.X0 : b.Z0, a0), hi = MathF.Min(alongX ? b.X1 : b.Z1, a1);
                            if (hi <= lo) continue;
                            var solid = new List<(float A, float B)> { (lo, hi) };
                            foreach (var o in w.Openings)
                            {
                                var p = At(w, o.U); var q = At(w, o.U + o.Width);
                                float o0 = alongX ? MathF.Min(p.X, q.X) : MathF.Min(p.Z, q.Z), o1 = alongX ? MathF.Max(p.X, q.X) : MathF.Max(p.Z, q.Z);
                                solid = solid.SelectMany(s => s.B <= o0 || s.A >= o1 ? new[] { s } : new (float A, float B)[] { (s.A, MathF.Min(s.B, o0)), (MathF.Max(s.A, o1), s.B) })
                                             .Where(s => s.B - s.A > 0.01f).ToList();
                            }
                            spans.AddRange(solid);
                        }
                        float held = 0f, upTo = float.NegativeInfinity;
                        foreach (var (a, b) in spans.OrderBy(s => s.A)) { float from = MathF.Max(a, upTo); if (b > from) { held += b - from; upTo = b; } }
                        return held / (a1 - a0);
                    }
                    bool W = Held(false, x0, z0, z1, -1) >= Most, E = Held(false, x1, z0, z1, +1) >= Most;
                    bool S = Held(true, z0, x0, x1, -1) >= Most, N = Held(true, z1, x0, x1, +1) >= Most;
                    bool cornered = (W || E) && (S || N);
                    var c = byShape.GetValueOrDefault(res.StairShape);
                    byShape[res.StairShape] = (c.All + 1, c.Two + (cornered ? 1 : 0));
                    if (!cornered) bad.Add($"{Name(spec)}: a {res.StairShape} landing on floor {f} at ({x0:0.00},{z0:0.00})..({x1:0.00},{z1:0.00}) has wall {(W || E || S || N ? $"on its {(W ? "W " : "")}{(E ? "E " : "")}{(S ? "S " : "")}{(N ? "N " : "")}side only" : "on no side")}");
                }
            }
            int all = byShape.Values.Sum(v => v.All), two = byShape.Values.Sum(v => v.Two);
            TestContext.Progress.WriteLine($"[gen-landing] {two} of {all} landings rest on two walls ({string.Join(", ", byShape.Select(kv => $"{kv.Key} {kv.Value.Two}/{kv.Value.All}"))}; {upper} on floors above the ground); {straight} buildings with straight flights, which have none{(bad.Count > 0 ? "; first: " + bad[0] : "")}");
            Assert.That(byShape.GetValueOrDefault(StairShape.Switchback).All, Is.GreaterThan(100));
            Assert.That(byShape.GetValueOrDefault(StairShape.Quarter).All, Is.GreaterThan(100));
            Assert.That(upper, Is.GreaterThan(20), "no landing on a floor above the ground was checked");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // E3: no room has a thin ARM (strawberry: "in the bedroom, ive noticed rooms tend to want to do this thing where
        // they have a really long thin tumor. which isnt necessary and looks weird"): no rectangle of a room one cell across
        // and two or more long that touches the rest of the room along less than its length -- a strip sticking out of it
        // by its end, typically along the outside wall behind a bathroom. Allowed: a one-cell-deep alcove open to the room
        // along its whole length (a room an L, not a room with a corridor), and a 1x1 nub. Both are counted and printed,
        // with the cruder measure the arms were first counted by (any rectangle 1 across and 2+ long, end-on or not).
        // BREAK IT: let a small room's one-row strip go to the room beside it in its row again; hand a garage's inner strip
        // to its row neighbour whatever its depth.
        [Test]
        public void RoomsHaveNoThinArms()
        {
            var bad = new List<string>();
            int rooms = 0, multi = 0, crude = 0, alcoves = 0, nubs = 0;
            var armKinds = new Dictionary<RoomKind, int>();
            foreach (var (spec, res) in All())
                foreach (var g in res.Layout.Where(g => !g.Circulation))
                {
                    rooms++;
                    var rl = g.Rects.ToList();
                    if (rl.Count < 2) continue;
                    multi++;
                    if (rl.Any(q => Math.Min(q.X1 - q.X0, q.Z1 - q.Z0) == 1 && Math.Max(q.X1 - q.X0, q.Z1 - q.Z0) >= 2)) crude++;
                    if (g.More.Any(q => q.X1 - q.X0 == 1 && q.Z1 - q.Z0 == 1)) nubs++;
                    bool arm = false, alcove = false;
                    for (int i = 0; i < rl.Count; i++)
                    {
                        var q = rl[i];
                        int wx = q.X1 - q.X0, wz = q.Z1 - q.Z0;
                        if (Math.Min(wx, wz) != 1 || Math.Max(wx, wz) < 2) continue;
                        // how long an edge it shares with the room's other rectangles
                        int contact = 0;
                        for (int j = 0; j < rl.Count; j++)
                        {
                            if (j == i) continue;
                            var o = rl[j];
                            if (o.X1 == q.X0 || o.X0 == q.X1) contact += Math.Max(0, Math.Min(o.Z1, q.Z1) - Math.Max(o.Z0, q.Z0));
                            if (o.Z1 == q.Z0 || o.Z0 == q.Z1) contact += Math.Max(0, Math.Min(o.X1, q.X1) - Math.Max(o.X0, q.X0));
                        }
                        if (contact < Math.Max(wx, wz)) arm = true; else alcove = true;
                    }
                    if (alcove && !arm) alcoves++;
                    if (!arm) continue;
                    armKinds[g.Kind] = armKinds.GetValueOrDefault(g.Kind) + 1;
                    bad.Add($"{Name(spec)}: {g.Kind}@{g.Floor} has a thin arm: {string.Join(" ", rl.Select(q => $"[{q.X0},{q.Z0}..{q.X1},{q.Z1}]"))}");
                }
            TestContext.Progress.WriteLine($"[gen-arms] {rooms} rooms, {multi} of more than one rectangle: {bad.Count} with a thin arm ({string.Join(", ", armKinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"))}), {alcoves} with a one-cell alcove, {nubs} with a 1x1 nub; {crude} with any rectangle 1 across and 2+ long{(bad.Count > 0 ? "; first: " + bad[0] : "")}");
            Assert.That(multi, Is.GreaterThan(100), "too few rooms of more than one rectangle to say anything");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // E4: a chimney stands out of the outside wall it climbs (strawberry: "chimney should buldge out of the exterior
        // wall to some degree, if its on an exterior wall"). Found from the walls, not the generator's word for it: the
        // fireplace is the breast with a low vent opening in it on the ground floor; the outside wall is the one behind
        // it; the stack is whatever wall stands past that wall's outer face across the breast's span. The stack reaches out
        // half the breast's depth or more, stands on a footing, climbs every storey without a gap and on over the top
        // roof by a metre where it comes through; no opening in any floor's wall behind it; and no porch within 0.3 m.
        // And it is wider at its foot (strawberry, 2026-10-13: "widen the bottom of a chimney with sloped sections on the
        // 1st floor" -- her first floor, the ground storey): its lowest piece wider along the wall than its top one by half
        // a metre or more, its footing as wide; and between them sloped shoulders -- a piece cut in at both sides from its
        // full width at its foot to a narrower one at its top (the editor's InsetL1/R1), as wide at its foot as the piece it
        // stands on and at its top as the piece on it, all three on one centre, sloping 30 to 75 degrees, and ending by the
        // ground storey's head, its wall head (the floor above, or the eaves).
        // BREAK IT: lay the stack inside the footprint, as it was; leave out its footing; stop it at the wall head;
        // place it where a window or a porch is; the stack one width all the way up, as round 10 laid it; its shoulders
        // a step (no inset) or narrowing to less than the stack.
        [Test]
        public void ChimneysStandOutOfTheWall()
        {
            var bad = new List<string>();
            int chimneys = 0, widened = 0;
            var shape = new List<string>();
            var outs = new List<float>();
            foreach (var (spec, res) in All())
            {
                var breasts = WallsOn(res, 0).Where(w => MathF.Abs(w.Thickness - BuildingGen.ChimneyDeep) < 0.01f
                                                        && w.Openings.Any(o => o.Archetype == BuildingGen.ArchVent && o.V < 0.1f)).ToList();
                if (breasts.Count == 0) continue;
                chimneys++;
                var br = breasts[0];
                var bb = Box(br);
                bool alongX = bb.X1 - bb.X0 > bb.Z1 - bb.Z0;
                float mid = alongX ? (bb.Z0 + bb.Z1) * 0.5f : (bb.X0 + bb.X1) * 0.5f;
                // the outside wall behind it: Ext thick, parallel, its inner face against the breast's back
                var house = WallsOn(res, 0).Where(w => MathF.Abs(w.Thickness - BuildingGen.Ext) < 0.01f).Select(w => (W: w, B: Box(w)))
                                           .Where(x => (alongX ? x.B.X1 - x.B.X0 > x.B.Z1 - x.B.Z0 : x.B.Z1 - x.B.Z0 > x.B.X1 - x.B.X0)
                                                       && (alongX ? x.B.X0 <= bb.X0 + 0.01f && x.B.X1 >= bb.X1 - 0.01f : x.B.Z0 <= bb.Z0 + 0.01f && x.B.Z1 >= bb.Z1 - 0.01f)
                                                       && MathF.Abs((alongX ? (x.B.Z0 + x.B.Z1) * 0.5f : (x.B.X0 + x.B.X1) * 0.5f) - mid) < (BuildingGen.ChimneyDeep + BuildingGen.Ext) * 0.5f + 0.05f)
                                           .ToList();
                if (house.Count != 1) { bad.Add($"{Name(spec)}: the fireplace stands against {house.Count} outside walls"); continue; }
                var hb = house[0].B;
                float line = alongX ? (hb.Z0 + hb.Z1) * 0.5f : (hb.X0 + hb.X1) * 0.5f;
                int outward = line > mid ? 1 : -1;
                float face = outward > 0 ? (alongX ? hb.Z1 : hb.X1) : (alongX ? hb.Z0 : hb.X0);
                float s0 = alongX ? bb.X0 : bb.Z0, s1 = alongX ? bb.X1 : bb.Z1;
                // the stack: walls and footings past the outer face, across the breast's span
                var stack = res.Walls.Where(w => w.Kind is SurfaceKind.Wall or SurfaceKind.Foundation).Select(w => (W: w, B: Box(w)))
                                     .Where(x => (alongX ? x.B.X0 < s1 - 0.01f && x.B.X1 > s0 + 0.01f : x.B.Z0 < s1 - 0.01f && x.B.Z1 > s0 + 0.01f)
                                                 && outward * ((alongX ? (x.B.Z0 + x.B.Z1) : (x.B.X0 + x.B.X1)) * 0.5f - face) > 0.01f
                                                 && outward * ((outward > 0 ? (alongX ? x.B.Z0 : x.B.X0) : (alongX ? x.B.Z1 : x.B.X1)) - face) > -0.01f
                                                 && outward * ((outward > 0 ? (alongX ? x.B.Z0 : x.B.X0) : (alongX ? x.B.Z1 : x.B.X1)) - face) < 0.05f)
                                     .ToList();
                float reach = stack.Count == 0 ? 0f : stack.Max(x => outward * ((outward > 0 ? (alongX ? x.B.Z1 : x.B.X1) : (alongX ? x.B.Z0 : x.B.X0)) - face));
                outs.Add(reach);
                if (reach < BuildingGen.ChimneyDeep * 0.5f - 0.01f) { bad.Add($"{Name(spec)}: the chimney stands {reach:0.00} m out of its wall, under half its depth"); continue; }
                // from its footing up, storey on storey without a gap, to a metre over the roof where it comes through
                var pieces = stack.Select(x => x.W).OrderBy(w => w.Y).ToList();
                if (!pieces.Any(w => w.Kind == SurfaceKind.Foundation && w.Y + w.Height >= -0.01f)) bad.Add($"{Name(spec)}: the chimney's stack has no footing");
                float upTo = pieces.Where(w => w.Kind == SurfaceKind.Wall).Select(w => w.Y).DefaultIfEmpty(1f).Min() > 0.01f ? float.NaN : 0f;
                foreach (var w in pieces.Where(w => w.Kind == SurfaceKind.Wall)) if (!float.IsNaN(upTo) && w.Y <= upTo + 0.01f) upTo = MathF.Max(upTo, w.Y + w.Height);
                // wider at its foot, with sloped shoulders between that and the stack
                var ws = pieces.Where(w => w.Kind == SurfaceKind.Wall).ToList();
                if (ws.Count >= 2)
                {
                    float TopW(WallPlan w) => w.Length - w.InsetL1 - w.InsetR1;
                    float Centre(WallPlan w, bool top) { var c = At(w, top ? w.InsetL1 + TopW(w) * 0.5f : w.InsetL0 + (w.Length - w.InsetL0 - w.InsetR0) * 0.5f); return alongX ? c.X : c.Z; }
                    var foot = ws[0]; var head = ws[^1];
                    var footing = pieces.FirstOrDefault(w => w.Kind == SurfaceKind.Foundation);
                    bool wide = foot.Length >= TopW(head) + 0.5f;
                    if (!wide) bad.Add($"{Name(spec)}: the chimney is {foot.Length:0.00} m wide at its foot and {TopW(head):0.00} at its top: not wider at the foot");
                    if (footing != null && footing.Length < foot.Length - 0.01f) bad.Add($"{Name(spec)}: the chimney's footing is {footing.Length:0.00} m under a foot {foot.Length:0.00} wide");
                    var sl = ws.Where(w => w.InsetL1 > 0.05f && w.InsetR1 > 0.05f && w.InsetL0 < 0.01f && w.InsetR0 < 0.01f).ToList();
                    if (sl.Count == 0) bad.Add($"{Name(spec)}: the chimney has no sloped shoulders ({string.Join(" ", ws.Select(w => $"[y {w.Y:0.00} h {w.Height:0.00} {w.Length:0.00} m]"))})");
                    else
                    {
                        var sh = sl[0];
                        var under = ws.FirstOrDefault(w => MathF.Abs(w.Y + w.Height - sh.Y) < 0.01f);
                        var over = ws.FirstOrDefault(w => MathF.Abs(w.Y - sh.Y - sh.Height) < 0.01f);
                        float deg = MathF.Atan2(sh.Height, MathF.Max(sh.InsetL1, sh.InsetR1)) * 180f / MathF.PI;
                        if (under == null || MathF.Abs(TopW(under) - sh.Length) > 0.01f) bad.Add($"{Name(spec)}: the chimney's shoulders do not stand on a piece as wide as their foot");
                        else if (over == null || MathF.Abs(over.Length - TopW(sh)) > 0.01f) bad.Add($"{Name(spec)}: the chimney's shoulders narrow to {TopW(sh):0.00} m under a stack {over?.Length ?? 0f:0.00} wide");
                        else if (MathF.Abs(Centre(under, true) - Centre(sh, false)) > 0.01f || MathF.Abs(Centre(sh, true) - Centre(over, false)) > 0.01f) bad.Add($"{Name(spec)}: the chimney's shoulders are off its centre");
                        else if (deg < 30f || deg > 75f) bad.Add($"{Name(spec)}: the chimney's shoulders slope {deg:0} degrees");
                        else if (sh.Y + sh.Height > Storey + 0.01f) bad.Add($"{Name(spec)}: the chimney's shoulders end at {sh.Y + sh.Height:0.00} m, over the ground storey");
                        else if (wide)
                        {
                            widened++;
                            if (shape.Count < 3) shape.Add($"{foot.Length:0.00} m to {foot.Y + foot.Height:0.00}, shoulders {sh.Y:0.00}-{sh.Y + sh.Height:0.00} at {deg:0} deg, stack {TopW(head):0.00} m");
                        }
                    }
                }
                var ubox = (X0: stack.Min(x => x.B.X0), Z0: stack.Min(x => x.B.Z0), X1: stack.Max(x => x.B.X1), Z1: stack.Max(x => x.B.Z1));
                // (where it comes through the roof: its top piece's footprint -- the foot under it is wider, and nowhere near
                // the roof)
                var top = stack.Where(x => x.W.Kind == SurfaceKind.Wall).OrderBy(x => x.W.Y).Last().B;
                var sbox = (X0: top.X0, Z0: top.Z0, X1: top.X1, Z1: top.Z1);
                float roof = new[] { (sbox.X0, sbox.Z0), (sbox.X1, sbox.Z0), (sbox.X0, sbox.Z1), (sbox.X1, sbox.Z1), ((sbox.X0 + sbox.X1) * 0.5f, (sbox.Z0 + sbox.Z1) * 0.5f) }
                                 .Select(p => BuildingGen.RoofTopAt(res, p.Item1, p.Item2)).Where(h => !float.IsNaN(h)).DefaultIfEmpty(res.Floors * Storey).Max();
                if (float.IsNaN(upTo) || upTo < roof + BuildingGen.ChimneyOverRoof - 0.01f) bad.Add($"{Name(spec)}: the chimney's stack climbs to {upTo:0.00} m, the roof it comes through is at {roof:0.00}");
                // no opening behind it on any floor, and no porch by it
                for (int f = 0; f < res.Floors; f++)
                    foreach (var w in WallsOn(res, f).Where(w => MathF.Abs(w.Thickness - BuildingGen.Ext) < 0.01f))
                    {
                        var b = Box(w);
                        if (MathF.Abs((alongX ? (b.Z0 + b.Z1) : (b.X0 + b.X1)) * 0.5f - line) > 0.01f || (alongX ? b.X1 - b.X0 < b.Z1 - b.Z0 : b.Z1 - b.Z0 < b.X1 - b.X0)) continue;
                        foreach (var o in w.Openings)
                        {
                            var p = At(w, o.U); var q = At(w, o.U + o.Width);
                            float o0 = alongX ? MathF.Min(p.X, q.X) : MathF.Min(p.Z, q.Z), o1 = alongX ? MathF.Max(p.X, q.X) : MathF.Max(p.Z, q.Z);
                            if (o0 < s1 && o1 > s0) bad.Add($"{Name(spec)}: an opening in floor {f}'s wall is behind the chimney");
                        }
                    }
                foreach (var q in res.Porches)
                    if (q.X0 < ubox.X1 + 0.3f && q.X1 > ubox.X0 - 0.3f && q.Z0 < ubox.Z1 + 0.3f && q.Z1 > ubox.Z0 - 0.3f) bad.Add($"{Name(spec)}: a porch runs into the chimney");
            }
            outs.Sort();
            TestContext.Progress.WriteLine($"[gen-chimney] {chimneys} chimneys; their stacks stand out of the wall {(outs.Count > 0 ? $"{outs[0]:0.00} to {outs[^1]:0.00} m (median {outs[outs.Count / 2]:0.00})" : "-")}; {widened} of {chimneys} wider at the foot with sloped shoulders ({string.Join("; ", shape.Distinct())}); {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(chimneys, Is.GreaterThan(30));
            Assert.That(widened, Is.EqualTo(chimneys), "chimneys one width all the way up");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // E5: a car fits in a garage (strawberry approved "2x3": cow tools measured the game's built cars at 3.0 m across the
        // tyres and up to 6.15 m long with fittings, the sedan, and a 2x2-cell garage has 5.3 to 5.5 m between its walls).
        // Measured from the generated walls, not the plan: from the inner face of the wall its vehicle door is in, straight
        // back into the room to the first wall -- at the door's middle and a car's half width either side -- at least
        // 6.5 m (6.15 m and room to shut the door); and along that wall, between the walls either side, at least 3.6 m, near
        // the door, half way back and at the back. Every ground-floor garage has a vehicle door, or a note saying it fell
        // short of one.
        // BREAK IT: garages two cells by two, as they were; the vehicle door in a garage's three-cell side; the trim keeping
        // two rows.
        [Test]
        public void GaragesFitACar()
        {
            const float Deep = 6.5f, Wide = 3.6f, Step = 0.02f;
            var bad = new List<string>();
            int garages = 0, measured = 0, excused = 0;
            float leastDeep = float.MaxValue, leastWide = float.MaxValue;
            foreach (var (spec, res) in All())
            {
                var walls = WallsOn(res, 0).ToList();
                var boxes = walls.Select(w => (W: w, B: Box(w))).ToList();
                bool InWall((float X, float Z) p, WallPlan except) => boxes.Any(b => b.W != except && p.X > b.B.X0 && p.X < b.B.X1 && p.Z > b.B.Z0 && p.Z < b.B.Z1);
                // how far from p along d before a wall (any but `except`), up to 30 m
                float Reach((float X, float Z) p, (float X, float Z) d, WallPlan except)
                {
                    for (float t = 0f; t < 30f; t += Step) if (InWall((p.X + d.X * t, p.Z + d.Z * t), except)) return t;
                    return 30f;
                }
                foreach (var g in res.Layout.Where(g => g.Floor == 0 && g.Kind == RoomKind.Garage))
                {
                    garages++;
                    var doors = new List<(WallPlan W, WallOpening O)>();
                    foreach (var w in walls.Where(w => MathF.Abs(w.Thickness - BuildingGen.Ext) < 0.01f))
                        foreach (var o in w.Openings.Where(o => o.Archetype == BuildingGen.ArchGarage))
                        {
                            var c = At(w, o.U + o.Width * 0.5f); var n = Normal(w);
                            if (RoomAt(res, 0, c.X - n.X, c.Z - n.Z) == g) doors.Add((w, o));
                        }
                    if (doors.Count == 0)
                    {
                        if (ShortBy(res, RoomKind.Garage, "vehicle door")) excused++;
                        else bad.Add($"{Name(spec)}: {g} has no vehicle door");
                        continue;
                    }
                    foreach (var (w, o) in doors)
                    {
                        measured++;
                        var d = Dir(w); var n = Normal(w); var inward = (X: -n.X, Z: -n.Z);
                        float half = w.Thickness * 0.5f;
                        // straight back from the wall's inner face, at the door's middle and a car's half width either side
                        float deep = float.MaxValue;
                        foreach (float across in new[] { -1.5f, 0f, 1.5f })
                        {
                            var p = At(w, o.U + o.Width * 0.5f + across);
                            deep = MathF.Min(deep, Reach((p.X + inward.X * (half + Step), p.Z + inward.Z * (half + Step)), inward, w) + Step);
                        }
                        // along the wall, both ways, near the door, half way back and at the back
                        float wide = float.MaxValue;
                        var mid = At(w, o.U + o.Width * 0.5f);
                        foreach (float back in new[] { 0.5f, deep * 0.5f, deep - 0.5f })
                        {
                            var p = (X: mid.X + inward.X * (half + back), Z: mid.Z + inward.Z * (half + back));
                            wide = MathF.Min(wide, Reach(p, d, null) + Reach(p, (-d.X, -d.Z), null));
                        }
                        leastDeep = MathF.Min(leastDeep, deep); leastWide = MathF.Min(leastWide, wide);
                        if (deep < Deep) bad.Add($"{Name(spec)}: {g} is {deep:0.00} m deep behind its vehicle door, not {Deep}");
                        if (wide < Wide) bad.Add($"{Name(spec)}: {g} is {wide:0.00} m wide along its vehicle door, not {Wide}");
                    }
                }
            }
            TestContext.Progress.WriteLine($"[gen-garage] {garages} ground-floor garages, {measured} vehicle doors measured ({excused} garages without one, with a note): the least {(measured > 0 ? $"{leastDeep:0.00} m deep and {leastWide:0.00} m wide" : "-")}; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(measured, Is.GreaterThan(40));
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
                            // (a bathroom's slit is a third of a window's: OpeningsSuitTheRoom holds it to that)
                            if (o.Glazed && o.Archetype != BuildingGen.ArchSlit && o.Width < BuildingGen.MinWindow - 0.01f) bad.Add($"{at}: a window only {o.Width:0.00} wide");
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

        /// <summary>The height of a wall's top over the plan point at x along it (a wall running along X), NaN when x is
        /// off its run. A tapered wall keeps, at height v, u from InsetL0..L1 to Length - InsetR0..R1 (lerped over its
        /// height: WallSurface.CutL/CutR), so with its bottom insets zero its top at u is where u meets a cut line.</summary>
        static float TopAt(WallPlan w, float x)
        {
            float u = (x - w.X) * Dir(w).X;
            if (u < -0.01f || u > w.Length + 0.01f) return float.NaN;
            float k = 1f;
            if (w.InsetL1 > 0.01f) k = MathF.Min(k, u / w.InsetL1);
            if (w.InsetR1 > 0.01f) k = MathF.Min(k, (w.Length - u) / w.InsetR1);
            return w.Y + w.Height * Math.Clamp(k, 0f, 1f);
        }

        /// <summary>How much of an outside wall a room has: the length of its stretch of the building's edge on that wall --
        /// on the wall's line AND along the wall's run, by more than a wall's thickness. (On the line alone, an L's or a T's
        /// inner end wall, which stands on the line of a room's edge in the full-length row a cell or more away from it,
        /// counted as that room's; and an inside corner's wall runs on half its thickness past the corner, along a room
        /// it does not bound -- which made a bathroom whose one outside wall faces a side asked to have no windows look as if
        /// it had another.)</summary>
        static float SpanOf(GenRoom g, WallPlan w)
        {
            bool alongX = w.Yaw == 0f || w.Yaw == 180f;
            float line = alongX ? w.Z : w.X;
            var p = Start(w); var q = At(w, w.Length);
            float a0 = alongX ? MathF.Min(p.X, q.X) : MathF.Min(p.Z, q.Z), a1 = alongX ? MathF.Max(p.X, q.X) : MathF.Max(p.Z, q.Z);
            float Along(float b0, float b1) { float o = MathF.Min(b1, a1) - MathF.Max(b0, a0); return o > BuildingGen.Ext ? o : 0f; }
            return g.Rects.Sum(r => alongX ? ((MathF.Abs(r.Z0 * Cell - line) < 0.5f || MathF.Abs(r.Z1 * Cell - line) < 0.5f) ? Along(r.X0 * Cell, r.X1 * Cell) : 0f)
                                           : ((MathF.Abs(r.X0 * Cell - line) < 0.5f || MathF.Abs(r.X1 * Cell - line) < 0.5f) ? Along(r.Z0 * Cell, r.Z1 * Cell) : 0f));
        }

        // G: the openings suit the room: daylight in every habitable room, a vent or window in a bathroom, a vehicle door
        // in a ground-floor garage or warehouse, a shop's street door in its longest outside wall, nothing in a closet or
        // store room, no outside door upstairs, and nothing in the outside wall a flight climbs along.
        // BREAK IT: give habitable rooms no window; no vehicle door; put the shop door on any wall; drop the bay cut.
        [Test]
        public void OpeningsSuitTheRoom()
        {
            var bad = new List<string>();
            int lit = 0, vented = 0, vehicle = 0, shops = 0, endFronts = 0, blank = 0, stockDoors = 0, clear = 0, shortExcused = 0, backFronted = 0, slits = 0;
            float widest = 0f;
            foreach (var (spec, res) in All())
            {
                var outer = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && w.Thickness == BuildingGen.Ext).ToList();
                // a bathroom's slit a third of the 3.5 m it was (strawberry: "make the bathroom window slit 1/3 as wide"),
                // the 1.5 m least a squeezed window keeps a third of that too
                foreach (var w in outer)
                    foreach (var o in w.Openings.Where(o => o.Archetype == BuildingGen.ArchSlit))
                    {
                        slits++; widest = MathF.Max(widest, o.Width);
                        if (o.Width > 3.5f / 3f + 0.01f || o.Width < 1.5f / 3f - 0.01f) bad.Add($"{Name(spec)}: a slit {o.Width:0.00} m wide");
                    }
                List<(WallPlan W, WallOpening O)> Of(GenRoom g) => outer.Where(w => FloorOf(w) == g.Floor).SelectMany(w => w.Openings.Select(o => (w, o)))
                    .Where(p => { var c = At(p.w, p.o.U + p.o.Width * 0.5f); var n = Normal(p.w); return g.Holds(c.X - n.X * 1f, c.Z - n.Z * 1f); }).ToList();
                foreach (var g in res.Layout)
                {
                    var mine = Of(g);
                    var os = mine.Select(p => p.O).ToList();
                    string n = $"{Name(spec)}: {g}";
                    // (a room whose every outside wall faces a side asked to have no windows is let off)
                    var faces = outer.Where(w => FloorOf(w) == g.Floor && SpanOf(g, w) > 0.01f && w.Openings.Count >= 0).Select(FaceOf).Distinct().ToList();
                    bool blind = spec.NoWindows != Sides.None && faces.Count > 0 && faces.All(fc => (fc & spec.NoWindows) != 0);
                    if (BuildingGen.Habitable(g.Kind) && !blind) { if (os.Any(o => o.Glazed)) lit++; else if (ShortBy(res, g.Kind, "window")) shortExcused++; else bad.Add($"{n} has no window"); }
                    // (a bathroom tucked inside, behind the room it opens off, has no outside wall to vent through)
                    // (and an end wall with the floor below's roof leaning on it carries no window: BuildWalls)
                    bool Room(int fl, int x, int z) => res.Layout.Any(o => o.Floor == fl && o.HasCell(x, z));
                    bool outside = g.Rects.Any(q => Enumerable.Range(q.X0, q.X1 - q.X0).Any(x => Enumerable.Range(q.Z0, q.Z1 - q.Z0).Any(z =>
                        new[] { (x, z - 1), (x, z + 1) }.Any(c => !Room(g.Floor, c.Item1, c.Item2))
                        || new[] { x - 1, x + 1 }.Any(cx => !Room(g.Floor, cx, z) && (g.Floor == 0 || res.Roof == RoofKind.Flat || !Room(g.Floor - 1, cx, z))))));
                    if (g.Kind == RoomKind.Bathroom && !blind && outside) { if (os.Any(o => o.Archetype == BuildingGen.ArchVent || o.Glazed)) vented++; else bad.Add($"{n} has no vent"); }
                    // and none at all on those sides
                    foreach (var (w, o) in mine) if (o.Glazed && (FaceOf(w) & spec.NoWindows) != 0) bad.Add($"{n} has a window on its {FaceOf(w)} side, asked to have none");
                    if ((g.Kind == RoomKind.Garage || g.Kind == RoomKind.Warehouse) && g.Floor == 0)
                    { if (os.Any(o => o.Archetype == BuildingGen.ArchGarage)) vehicle++; else if (ShortBy(res, g.Kind, "vehicle door")) shortExcused++; else bad.Add($"{n} has no vehicle door"); }
                    if (g.Kind == RoomKind.Shop && g.Floor == 0 && spec.Kind == BuildingKind.Commercial)
                    {
                        // its street door in its frontage: an end wall where it has one, so it runs back from the street
                        // (strawberry: "in general stores should span deeper instead of wider"), else its longest wall
                        // (any of its doors: a back door may be on another wall)
                        var doorWalls = mine.Where(p => p.O.HasDoor).Select(p => p.W).ToList();
                        var ground = outer.Where(w => FloorOf(w) == 0).ToList();
                        var ends = ground.Where(w => !(w.Yaw == 0f || w.Yaw == 180f) && SpanOf(g, w) > 0.01f).ToList();
                        float longest = ground.Max(w => SpanOf(g, w));
                        var front = ends.Count > 0 ? ends : ground.Where(w => SpanOf(g, w) >= longest - 0.01f).ToList();
                        // a front side asked for outranks the frontage
                        if (spec.FrontSide != Sides.None) front = ground.Where(w => SpanOf(g, w) > 0.01f && (FaceOf(w) & spec.FrontSide) != 0).ToList();
                        // and so does a back side asked for, where the frontage faces it: the street door keeps off that side
                        // where it can go elsewhere (BuildingGen's Door2), so it is in another of the shop's walls -- the
                        // generator's priority since sides could be asked for. The sweep's 24 seeds never met it; a wider
                        // one does, in rectangles too: Commercial, min 7, 1-2 floors of 6, back door East -- 99 of 200
                        // seeds, all rectangles, on the generator before L and T had upper floors
                        else if (spec.BackSide != Sides.None && ends.Count > 0 && ends.All(w => (FaceOf(w) & spec.BackSide) != 0))
                        {
                            front = ground.Where(w => SpanOf(g, w) > 0.01f && (FaceOf(w) & spec.BackSide) == 0).ToList();
                            backFronted++;
                        }
                        if (doorWalls.Count == 0) bad.Add($"{n} has no street door");
                        else if (!doorWalls.Any(front.Contains)) bad.Add($"{n}'s doors are not in its frontage ({(ends.Count > 0 ? "an end wall" : "its longest wall")})");
                        else { shops++; if (ends.Count > 0) endFronts++; }
                    }
                    // blank, but for a stockroom's back door (where the deliveries come in)
                    bool BackDoor((WallPlan W, WallOpening O) p) { var c = At(p.W, p.O.U + p.O.Width * 0.5f); return p.O.HasDoor && res.DoorMarks.Any(m => m.Role == DoorRole.Back && MathF.Abs(m.X - c.X) < 0.05f && MathF.Abs(m.Z - c.Z) < 0.05f); }
                    if (g.Kind == RoomKind.Closet || g.Kind == RoomKind.Storage)
                    {
                        int others = mine.Count(p => !(g.Kind == RoomKind.Storage && BackDoor(p)));
                        if (others == 0) { blank++; if (os.Count > 0) stockDoors++; } else bad.Add($"{n} has {others} openings");
                    }
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
            TestContext.Progress.WriteLine($"[gen-suit] {slits} bathroom slits, the widest {widest:0.00} m; {lit} habitable rooms lit, {vented} bathrooms vented, {vehicle} vehicle doors, {shops} shopfronts ({endFronts} in an end wall, {backFronted} kept off a back side asked for), {blank} closets/stores blank ({stockDoors} stores with only their back door), {clear} outside openings checked clear of flights; {shortExcused} rooms short of a window or vehicle door in plot-fitted buildings that say so; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(shortExcused, Is.LessThanOrEqualTo(All().Count(x => x.Spec.FitPlot) / 20), "plot-fitted buildings often come out without windows");
            Assert.That(lit, Is.GreaterThan(500));
            Assert.That(vented, Is.GreaterThan(100));
            Assert.That(slits, Is.GreaterThan(100), "too few bathroom slits to measure");
            Assert.That(vehicle, Is.GreaterThan(50));
            Assert.That(shops, Is.GreaterThan(20));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // H: houses get a gable or hip roof, businesses a flat one (strawberry: "businesses having flat roofs while houses
        // have gable"); a house's lower floor where the one above leaves it uncovered gets a shed leaning on the upper
        // wall ("slope from the wall off the edge"), meeting it under its head, closed down both sides, with no window
        // behind it; and every cell of every floor is under the floor above or a roof.
        // BREAK IT: a flat roof on houses (or on their lower parts); forget a roof; no gable ends; the lower roof at the
        // house's pitch (too tall); windows left in the wall behind it; a shed with no side infills; a shed rising to its
        // open end.
        [Test]
        public void RoofsSuitTheKindAndCoverEverything()
        {
            var bad = new List<string>();
            int gables = 0, hips = 0, flats = 0, cells = 0, exposed = 0, lowers = 0, leaning = 0, closed = 0, blockFlat = 0, blockGable = 0;
            foreach (var (spec, res) in All())
            {
                // (a porch's roof is its own, over its deck: not the house's)
                bool OverPorch(WallPlan w)
                {
                    var d = Dir(w); var nn = Normal(w); float run = w.Height * MathF.Cos((w.Pitch + 90f) * MathF.PI / 180f);
                    float cx = w.X + d.X * w.Length * 0.5f - nn.X * run * 0.5f, cz = w.Z + d.Z * w.Length * 0.5f - nn.Z * run * 0.5f;
                    if (MathF.Abs(w.Pitch + 90f) < 0.5f) { cx = w.X + w.Length * 0.5f; cz = w.Z - w.Height * 0.5f; }
                    return res.Porches.Any(p => cx > p.X0 - 0.6f && cx < p.X1 + 0.6f && cz > p.Z0 - 0.6f && cz < p.Z1 + 0.6f);
                }
                var roofs = res.Walls.Where(w => w.Kind == SurfaceKind.Roof && !OverPorch(w)).ToList();
                // (an L or a T of one storey has a cross gable; of more, the floors above stand on its main block and the
                // top roof is over them, a rectangle's, the arm(s) a shed each like any lower part left uncovered)
                bool cross = res.Shape != FootprintShape.Rect && res.Floors == 1;
                var slopes = roofs.Where(w => MathF.Abs(w.Pitch + 90f) > 0.5f).ToList();
                var topSlopes = slopes.Where(w => MathF.Abs(w.Y - res.Floors * Storey) < 0.05f).ToList();
                bool raised = res.Walls.Any(w => w.Kind == SurfaceKind.Wall && w.GableRise > 0.01f);
                int exposedParts = Enumerable.Range(0, res.Floors - 1).Sum(f => (res.Footprints[f + 1].X0 > res.Footprints[f].X0 ? 1 : 0) + (res.Footprints[f + 1].X1 < res.Footprints[f].X1 ? 1 : 0));
                if (spec.Kind == BuildingKind.Residential)
                {
                    // the top a gable (two slopes, ends raised) or a hip (four, none raised); each part a floor above
                    // leaves uncovered one shed slope of its own
                    // (an L or a T: a cross gable -- every slope over the top floor, starting at its head or, above an
                    // arm's ridge, higher; the main roof's ends and each arm's raised)
                    int raisedEnds = res.Walls.Count(w => w.Kind == SurfaceKind.Wall && w.GableRise > 0.01f);
                    if (cross) topSlopes = slopes.Where(w => w.Y > res.Floors * Storey - 0.05f).ToList();
                    bool topOk = cross ? res.Roof == RoofKind.Gable && topSlopes.Count >= 5 && raisedEnds >= 3
                               : res.Roof == RoofKind.Gable ? topSlopes.Count == 2 && raised : res.Roof == RoofKind.Hip && topSlopes.Count == 4 && !raised;
                    if (!topOk) bad.Add($"{Name(spec)}: a house's top roof is {res.Roof} with {topSlopes.Count} slopes, gable ends {raised}");
                    else if (res.Roof == RoofKind.Gable) gables++; else hips++;
                    if (slopes.Count - topSlopes.Count != exposedParts) bad.Add($"{Name(spec)}: {exposedParts} uncovered lower parts, {slopes.Count - topSlopes.Count} lower slopes");
                }
                else if (spec.Kind == BuildingKind.Apartment)
                {
                    // a block of flats: flat, or one plain gable over the whole block, ends raised -- every floor the same
                    // length, so nothing lower is left to roof
                    if (res.Roof == RoofKind.Flat && slopes.Count == 0 && !raised) blockFlat++;
                    else if (res.Roof == RoofKind.Gable && topSlopes.Count == 2 && slopes.Count == 2 && raised) blockGable++;
                    else bad.Add($"{Name(spec)}: a block of flats' roof is {res.Roof} with {slopes.Count} slopes ({topSlopes.Count} on top), gable ends {raised}");
                }
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
                    // closed at its sides: along the two side walls, the wall and what stands on it reach the roof's underside
                    int below = above - 1;
                    var dw = Dir(w); var nw = Normal(w);
                    float runW = w.Height * MathF.Cos((w.Pitch + 90f) * MathF.PI / 180f);
                    var xs = new[] { w.X, w.X + dw.X * w.Length, w.X - nw.X * runW, w.X + dw.X * w.Length - nw.X * runW };
                    float sx0 = xs.Min(), sx1 = xs.Max();
                    // (and along z: the building's depth, or an L's or a T's arm's, the full-length row's -- its sides are
                    // the walls an overhang in from its edges)
                    var zs = new[] { w.Z, w.Z + dw.Z * w.Length, w.Z - nw.Z * runW, w.Z + dw.Z * w.Length - nw.Z * runW };
                    float sz0 = zs.Min(), sz1 = zs.Max(), inset = BuildingGen.Ext * 0.5f + BuildingGen.RoofOverhang;
                    // leaning on the upper wall: highest against it, its eave at the open end
                    var (u0, u1) = res.Footprints[above];
                    bool westPart = sx1 <= u0 * Cell + 0.5f;
                    float zMid = (sz0 + sz1) * 0.5f;
                    float hWall = RoofHeightAt(w, westPart ? u0 * Cell - 0.6f : u1 * Cell + 0.6f, zMid);
                    float hOpen = RoofHeightAt(w, westPart ? sx0 + 0.6f : sx1 - 0.6f, zMid);
                    if (float.IsNaN(hWall) || float.IsNaN(hOpen) || hWall < hOpen + 0.5f) bad.Add($"{Name(spec)}: the roof over floor {above - 1} does not rise to the wall above it ({hWall:0.00} m there, {hOpen:0.00} at its open end)");
                    else leaning++;
                    foreach (float zLine in new[] { sz0 + inset, sz1 - inset })
                        for (int t = 1; t < 8; t++)
                        {
                            float x = sx0 + (sx1 - sx0) * t / 8f;
                            float h = RoofHeightAt(w, x, zLine);
                            if (float.IsNaN(h)) continue;
                            float topAt = res.Walls.Where(u => u.Kind == SurfaceKind.Wall && u.Pitch == 0f && (u.Yaw == 0f || u.Yaw == 180f) && MathF.Abs(u.Z - zLine) < 0.01f)
                                                   .Select(u => TopAt(u, x)).DefaultIfEmpty(float.NaN).Max();
                            closed++;
                            if (float.IsNaN(topAt) || topAt < h - 0.1f) { bad.Add($"{Name(spec)}: the roof over floor {below} is open at its side at x {x:0.0}, z {zLine:0} ({h:0.00} m over, the wall reaches {topAt:0.00})"); break; }
                        }
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
                            if (InNotch(res, px, pz)) continue;
                            if (f < top && x >= res.Footprints[f + 1].X0 && x < res.Footprints[f + 1].X1) continue;
                            exposed++;
                            bool covered = res.Roof != RoofKind.Flat
                                ? slopes.Any(w => (cross ? w.Y > (f + 1) * Storey - 0.05f : MathF.Abs(w.Y - (f + 1) * Storey) < 0.05f) && !float.IsNaN(RoofHeightAt(w, px, pz)))
                                : roofs.Any(w => MathF.Abs(w.Pitch + 90f) < 0.5f && MathF.Abs(w.Y - (f + 1) * Storey - BuildingGen.Slab * 0.5f) < 0.05f
                                                 && px > w.X && px < w.X + w.Length && pz > w.Z - w.Height && pz < w.Z);
                            if (!covered) bad.Add($"{Name(spec)}: floor {f} cell ({x},{z}) has no roof over it");
                        }
            }
            TestContext.Progress.WriteLine($"[gen-roof] {gables} gabled and {hips} hipped houses ({lowers} sheds on lower floors, {leaning} rising to the wall above, {closed} side points closed), {flats} flat-roofed businesses, {blockFlat} flat-roofed and {blockGable} gabled blocks of flats; {exposed} of {cells} floor cells open to the sky, each checked for its roof; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(gables, Is.GreaterThan(60));
            Assert.That(hips, Is.GreaterThan(20));
            Assert.That(closed, Is.GreaterThan(500));
            Assert.That(leaning, Is.EqualTo(lowers).And.GreaterThan(50));
            Assert.That(flats, Is.GreaterThan(300));
            Assert.That(blockFlat, Is.GreaterThan(30)); Assert.That(blockGable, Is.GreaterThan(30));
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
            int pairs = 0, beside = 0, facing = 0, excused = 0, doors = 0, crowded = 0, spansApart = 0;
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
                    // (an open plan's span is no door: a wall left out has no frame to crowd a corner with -- its piers are
                    // what is left of the wall; counted apart)
                    var all = Doors(res, f).ToList();
                    var ds = all.Where(x => !OpenSpan(x.W, x.O)).ToList();
                    spansApart += all.Count - ds.Count;
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
            TestContext.Progress.WriteLine($"[gen-corner] {pairs} kitchens with a dining room on their floor: {beside} beside it, {facing} facing it across a hall, {excused} apart with a note; {doors} doors ({spansApart} open spans not counted as doors), {crowded} pairs crowding one corner; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
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

        // M: one front door and at most one back door, marked, each into a room that suits it (strawberry: "mark a
        // front and back door of the house only in appropriate rooms"); the back in another wall than the front; and
        // each mark is a real door in an outside wall at that spot.
        // BREAK IT: let the front door into any walk-through room; let the back door into any; mark nothing; put the
        // back door in the front's wall.
        [Test]
        public void FrontAndBackDoorsAreMarkedInTheRightRooms()
        {
            // the test's own idea of a sensible room for each, not BuildingGen.EntranceOrder's
            RoomKind[] FrontOk(BuildingKind b) => b switch
            {
                BuildingKind.Residential => new[] { RoomKind.Hall, RoomKind.LivingRoom },
                BuildingKind.Commercial => new[] { RoomKind.Shop, RoomKind.Hall },
                BuildingKind.Industrial => new[] { RoomKind.Hall, RoomKind.Workshop, RoomKind.Warehouse, RoomKind.Shop },
                // a block of flats: its lobby (the stairwell) or its corridor -- never a flat (checked below: shared rooms only)
                BuildingKind.Apartment => new[] { RoomKind.Stairwell, RoomKind.Hall },
                _ => new[] { RoomKind.Hall },
            };
            RoomKind[] BackOk(BuildingKind b) => b switch
            {
                BuildingKind.Residential => new[] { RoomKind.Kitchen, RoomKind.Hall, RoomKind.DiningRoom },
                // (a business's staff room -- its break room -- takes a staff entrance)
                BuildingKind.Commercial => new[] { RoomKind.Storage, RoomKind.BreakRoom, RoomKind.Hall },
                BuildingKind.Industrial => new[] { RoomKind.Warehouse, RoomKind.Workshop, RoomKind.Storage, RoomKind.Hall },
                BuildingKind.Apartment => new[] { RoomKind.Hall },
                _ => new[] { RoomKind.BreakRoom, RoomKind.Hall, RoomKind.Storage },
            };
            var bad = new List<string>();
            int fronts = 0, backs = 0, opposite = 0;
            var into = new Dictionary<string, int>();
            foreach (var (spec, res) in All())
            {
                var marks = res.DoorMarks.ToList();
                var outs = Doors(res, 0).Where(d => (d.A == null) != (d.B == null)).ToList();   // the walkable openings to outside
                foreach (var m in marks)
                {
                    var near = outs.Where(d => { var c = At(d.W, d.O.U + d.O.Width * 0.5f); return MathF.Abs(c.X - m.X) < 0.05f && MathF.Abs(c.Z - m.Z) < 0.05f; }).ToList();
                    if (m.Floor != 0 || near.Count != 1 || !near[0].O.HasDoor) { bad.Add($"{Name(spec)}: the {m.Role} mark at ({m.X:0.0},{m.Z:0.0}) is not a door to outside"); continue; }
                    var room = near[0].A ?? near[0].B;
                    var ok = m.Role == DoorRole.Front ? FrontOk(spec.Kind) : BackOk(spec.Kind);
                    if (!ok.Contains(room.Kind)) bad.Add($"{Name(spec)}: the {m.Role} door opens into the {room.Kind}");
                    if (room.Unit >= 0) bad.Add($"{Name(spec)}: the {m.Role} door opens into flat {room.Unit}'s {room.Kind}");
                    string key = $"{spec.Kind} {m.Role} {room.Kind}";
                    into[key] = into.GetValueOrDefault(key) + 1;
                }
                int nf = marks.Count(m => m.Role == DoorRole.Front), nb = marks.Count(m => m.Role == DoorRole.Back);
                fronts += nf; backs += nb;
                if (nf != 1) bad.Add($"{Name(spec)}: {nf} front doors");
                if (nb > 1) bad.Add($"{Name(spec)}: {nb} back doors");
                if (nf == 1 && nb == 1)
                {
                    var f = res.Front; var b = res.Back;
                    if (f.AlongX == b.AlongX && f.Line == b.Line) bad.Add($"{Name(spec)}: the back door is in the front door's wall");
                    else if (f.AlongX == b.AlongX) opposite++;
                }
            }
            TestContext.Progress.WriteLine($"[gen-doors] {fronts} front doors, {backs} back doors ({opposite} in the wall opposite the front); {string.Join(", ", into.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Value} {kv.Key}"))}; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(fronts, Is.EqualTo(All().Count));
            Assert.That(backs, Is.GreaterThan(All().Count / 3));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // N: rooms keep to their proportions (strawberry: "make sure rooms are scaled proportionately to what they are,
        // i think the dining room is way too big compared to the living room, which is further eaten by stairs"): what is
        // left of the living room beside the stairs is at least as big as the dining room and the kitchen on its floor,
        // and rooms sharing a row are scaled alike against their kinds' sizes.
        // BREAK IT: grow by turns instead of by share; forget the stairs' area in the stair room's share; drop the check.
        [Test]
        public void RoomsKeepToTheirProportions()
        {
            var bad = new List<string>();
            int livings = 0, excused = 0, rows = 0;
            var spreads = new List<double>();
            foreach (var (spec, res) in All())
            {
                var bay = res.Bay;
                foreach (var living in res.Layout.Where(g => g.Kind == RoomKind.LivingRoom))
                {
                    livings++;
                    int usable = living.Cells - (bay.X1 > bay.X0 && living.Floor == 0 && living.HasCell(bay.X0, bay.Z0) ? (bay.X1 - bay.X0) * (bay.Z1 - bay.Z0) : 0);
                    // (in a block of flats, against its own flat's: Unit is -1 in every other building)
                    foreach (var o in res.Layout.Where(g => g.Floor == living.Floor && g.Unit == living.Unit && (g.Kind == RoomKind.DiningRoom || g.Kind == RoomKind.Kitchen)))
                        if (o.Cells > usable)
                        {
                            if (res.Notes.Any(n => n.Contains("living room came out smaller"))) excused++;
                            else bad.Add($"{Name(spec)}: {o} ({o.Cells} cells) outgrows the {usable} cells left of {living}");
                        }
                }
                // rooms sharing a row share its length by their kinds' sizes: what each got over its kind's size is about
                // the same for all of them (the stair room counted without the stairs, service rooms left out -- they are
                // kept narrow on purpose -- and garages, kept to a car's size: two cells by three)
                double Usable(GenRoom g) => g.Cells - (bay.X1 > bay.X0 && g.Floor == 0 && g.HasCell(bay.X0, bay.Z0) ? (bay.X1 - bay.X0) * (bay.Z1 - bay.Z0) : 0);
                foreach (var row in res.Layout.Where(g => !g.Circulation && !BuildingGen.Service(g.Kind) && g.Kind != RoomKind.Garage && g.More.Count == 0).GroupBy(g => (g.Floor, g.Z0, g.Z1)))
                {
                    var rs = row.ToList();
                    if (rs.Count < 2) continue;
                    rows++;
                    // (a kitchen with the dining table in it is sized as a kitchen-diner, a size up)
                    double Size(GenRoom g) => g.Kind == RoomKind.Kitchen && res.KitchenDiner ? BuildingGen.DinerCells : BuildingGen.TraitOf(g.Kind, spec.Kind).Cells;
                    var share = rs.Select(g => (G: g, R: Usable(g) / Size(g))).OrderBy(x => x.R).ToList();
                    double spread = share[^1].R / share[0].R;
                    spreads.Add(spread);
                    // within a whole cell: two rooms of one kind two and three cells wide are 1.5x apart and as even as
                    // a row of odd length can make them; five and three (1.67x) is a cell given to the wrong one
                    if (spread > 1.5 + 1e-6) bad.Add($"{Name(spec)}: {share[^1].G} has {share[^1].R:0.0}x its kind's size beside {share[0].G} with {share[0].R:0.0}x");
                }
            }
            spreads.Sort();
            double Pct(double q) => spreads.Count == 0 ? 0 : spreads[(int)((spreads.Count - 1) * q)];
            TestContext.Progress.WriteLine($"[gen-size] {livings} living rooms checked against their dining rooms and kitchens ({excused} excused by a note); {rows} rows of rooms, the most over its kind's size against the least over: median {Pct(0.5):0.00}x, 90% {Pct(0.9):0.00}x, worst {Pct(1):0.00}x; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(livings, Is.GreaterThan(100));
            Assert.That(rows, Is.GreaterThan(300));
            Assert.That(excused, Is.LessThanOrEqualTo(livings / 50), "living rooms often lose to the dining room, with a note");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        static string Objects()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "game", "content", "objects", "guid_mesh.txt"))) return Path.Combine(d.FullName, "game", "content", "objects");
            return null;
        }

        /// <summary>The doodad kit's stock children (game/content/objects/doodad_kit_children.json): each doodad's item rips
        /// as the kit composes them -- (x, y, z) -> (x, -z, y), scaled, turned Rz Ry Rx, moved -- read here independently of
        /// the game's DoodadKitView, as vertices in the doodad's native frame.</summary>
        static Dictionary<string, List<float[]>> DoodadChildVerts(string objects)
        {
            var d = new Dictionary<string, List<float[]>>();
            string path = Path.Combine(objects, "doodad_kit_children.json");
            if (!File.Exists(path)) return d;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var row in doc.RootElement.EnumerateObject())
            {
                if (row.Name.StartsWith("_")) continue;
                var l = d[row.Name] = new List<float[]>();
                foreach (var c in row.Value.EnumerateArray())
                {
                    float[] F(string k) => c.GetProperty(k).EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
                    var p = F("position"); var r = F("rotation_deg").Select(a => a * MathF.PI / 180f).ToArray(); float sc = (float)c.GetProperty("scale").GetDouble();
                    float cx = MathF.Cos(r[0]), sx = MathF.Sin(r[0]), cy = MathF.Cos(r[1]), sy = MathF.Sin(r[1]), cz = MathF.Cos(r[2]), sz = MathF.Sin(r[2]);
                    string rip = Path.Combine(objects, "..", "items", c.GetProperty("item").GetInt32() + ".txt");
                    foreach (var line in File.ReadLines(rip).Where(q => q.StartsWith("v ")))
                    {
                        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(3).Select(q => float.Parse(q, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        // the rip turned into the native frame, then Rx, Ry, Rz, then scaled and moved
                        float x = t[0], y = -t[2], z = t[1];
                        (y, z) = (cx * y - sx * z, sx * y + cx * z);
                        (x, z) = (cy * x + sy * z, -sy * x + cy * z);
                        (x, y) = (cz * x - sz * y, sz * x + cz * y);
                        l.Add(new[] { x * sc + p[0], y * sc + p[1], z * sc + p[2] });
                    }
                }
            }
            return d;
        }

        // Q: the furnisher's sizes are the meshes' own (each OBJ's extents, to a centimetre), and every prop it uses has a
        // GUID -- EditorObjects.Save writes a placement by GUID and skips one without. A doodad of the kit is its art AND
        // its stock children, composed (three of them are nothing but children: no OBJ of their own).
        // BREAK IT: a wrong extent in the table (a bed 0.3 m short); a prop with no GUID (Workbench_0); a child's turn
        // dropped from the composition.
        [Test]
        public void FurnitureCatalogMatchesTheMeshes()
        {
            string dir = Objects();
            Assert.That(dir, Is.Not.Null, "game/content/objects not found");
            var guids = new HashSet<string>(File.ReadAllLines(Path.Combine(dir, "guid_mesh.txt")).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2).Select(p => p[1]));
            var kids = DoodadChildVerts(dir);
            var bad = new List<string>();
            foreach (var (name, sz) in FurnitureCatalog.Sizes)
            {
                if (!guids.Contains(name)) bad.Add($"{name} has no GUID: a save would drop it");
                string obj = Path.Combine(dir, name + ".obj");
                if (!File.Exists(obj) && !kids.ContainsKey(name)) { bad.Add($"{name}.obj is missing"); continue; }
                var v = (File.Exists(obj) ? File.ReadLines(obj) : Enumerable.Empty<string>()).Where(l => l.StartsWith("v ")).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(3)
                            .Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
                if (kids.TryGetValue(name, out var cv)) v.AddRange(cv);
                var got = (v.Min(p => p[0]), v.Max(p => p[0]), v.Min(p => p[1]), v.Max(p => p[1]), v.Max(p => p[2]));
                var want = (sz.X0, sz.X1, sz.Y0, sz.Y1, sz.H);
                if (MathF.Abs(got.Item1 - want.X0) > 0.015f || MathF.Abs(got.Item2 - want.X1) > 0.015f || MathF.Abs(got.Item3 - want.Y0) > 0.015f
                    || MathF.Abs(got.Item4 - want.Y1) > 0.015f || MathF.Abs(got.Item5 - want.H) > 0.015f)
                    bad.Add($"{name}: table {want}, mesh {got}");
            }
            TestContext.Progress.WriteLine($"[furn-catalog] {FurnitureCatalog.Sizes.Count} props held to their meshes; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(FurnitureCatalog.Sizes.Count, Is.GreaterThan(30));
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        // P: the rooms are furnished, sensibly (strawberry: "work on placement rules/guides for the furniture in the rooms.
        // should try for good feng shui if possible"). Every piece stands wholly in its room on its floor, clear of the
        // walls, the stairs, the holes, the tiles at a flight's ends and every other piece; a metre and a bit in front of
        // any door is clear (4 m in from a vehicle door); nothing taller than a man in front of a window; and in every room
        // its doors, stair ends and the fronts of the pieces you use are joined by clear floor. Each kind of room has what
        // makes it one -- and these with no tolerance at all (strawberry, 2026-10-12): a bed has a clear strip to get in at
        // along a long side, both sides of a double ("double beds should require space on either long sides to get in/out
        // of, single beds need at least one long side free"); a kitchen has a plain counter beside its sink or stove
        // ("require counters - not just sinks in kitchens"); a house with no dining room has a table with a chair in its
        // kitchen ("houses without a dining room require at table with at least one chair in the kitchen"). A desk's chair
        // with its back to a wall and the door ahead of it; a couch's television facing it; chairs facing their table; the
        // stove never beside the sink. And the feng shui, which is a preference ("feng shui is a suggestion not a
        // requirement, always preffered where layout allows"), counted and bounded rather than forbidden: a bed's head to a
        // solid wall, not under a window, the door not straight past its foot; a couch's back to a wall; the fridge, sink and
        // stove a short walk apart. (These are the test's own numbers, looser than the generator's: it keeps 1.3 m before a
        // door, this asks 1.2; a bed's strip is 0.8 m deep to 1.0 m short of its head there, 0.75 and 1.1 here.)
        // BREAK IT: drop the door zones; drop the walk check; let tall pieces stand at windows; score beds without the
        // coffin rule; put the sink beside the stove; turn the chairs round; ignore the desk's door; let a double into a
        // corner; drop the kitchen's counters for its appliances; let a kitchen-diner's table go without a chair.
        [Test]
        public void FurnitureFollowsTheRules()
        {
            const float DoorClear = 1.2f, LaneClear = 4f, Tall = 1.9f, Sill = 0.5f;
            var bad = new List<string>();
            int pieces = 0, ceilingLights = 0, rooms = 0, walkedRooms = 0, beds = 0, desks = 0, couches = 0, tvs = 0, chairs = 0, kitchensFull = 0, wallPieces = 0, swung = 0;
            int topClocks = 0;   // clocks under a top storey's ceiling higher than a lower storey's
            int wallSets = 0, crts = 0, lKitchens = 0, islands = 0, freeFridges = 0, gondolas = 0, movable = 0, turned = 0, shopsWithShelves = 0, shopsWithRows = 0;
            var missing = new Dictionary<string, int>(); var kinds = new Dictionary<RoomKind, int>(); int shortExcused = 0;
            // the round-9 requirements' counts, and the feng shui given up where a room would not have it
            int bedWays = 0, doubles = 0, worktops = 0, eatIns = 0, eatInsSeated = 0, flatBeds = 0, flatWorktops = 0, flatEatIns = 0;
            var feng = new Dictionary<string, int>();
            void Feng(string what) => feng[what] = feng.GetValueOrDefault(what) + 1;
            // (a plain counter is worktop, reached along its run: the one in an L's corner is a blind corner)
            bool Use(string n) => !(n.StartsWith("Chair") || n.StartsWith("Lamp") || n.StartsWith("Barrel") || n.StartsWith("Crate") || n.StartsWith("Cardboard")
                                    || n.StartsWith("Propane") || n.StartsWith("Pallet") || n.StartsWith("Coat") || n.StartsWith("Potted") || n == "Counter_0");
            foreach (var (spec, res) in All())
            {
                for (int f = 0; f < res.Floors; f++)
                {
                    var walls = WallsOn(res, f).ToList();
                    var stairs = res.Stairs.Where(st => st.Floor == f).Select(Foot)
                        .Concat(res.Walls.Where(w => w.Kind == SurfaceKind.Stairs && FloorOf(w) == f).Select(Plan))
                        .Concat(Holes(res, f)).ToList();
                    var tiles = new List<(float, float, float, float)>();
                    foreach (var st in res.Stairs)
                    {
                        var (bx, bz) = st.Back; var (rx, rz) = st.Right;
                        (float, float, float, float) T(float ox, float oz, float dir)
                        {
                            float ex = ox + bx * Cell * dir + rx * st.Width, ez = oz + bz * Cell * dir + rz * st.Width;
                            return (MathF.Min(ox, ex), MathF.Min(oz, ez), MathF.Max(ox, ex), MathF.Max(oz, ez));
                        }
                        if (st.Floor == f && StartsOnFloor(st)) tiles.Add(T(st.X, st.Z, -1f));
                        if (st.Floor == f - 1 && EndsOnFloor(st)) tiles.Add(T(st.X + bx * st.Run, st.Z + bz * st.Run, +1f));
                    }
                    // doorways and windows, as boxes in plan
                    var doorZones = new List<(float, float, float, float)>(); var windows = new List<((float, float, float, float) Box, float Sill)>();
                    var doorPts = new List<(float X, float Z)>();
                    foreach (var w in walls)
                    {
                        var n = Normal(w);
                        foreach (var o in w.Openings)
                        {
                            bool door = o.V < 0.05f;
                            float deep = door ? (o.Archetype == BuildingGen.ArchGarage ? LaneClear : DoorClear) : Sill;
                            var a = At(w, o.U); var b = At(w, o.U1);
                            float t = w.Thickness * 0.5f + deep;
                            var box = (MathF.Min(MathF.Min(a.X, b.X) - n.X * t, MathF.Min(a.X, b.X) + n.X * t), MathF.Min(MathF.Min(a.Z, b.Z) - n.Z * t, MathF.Min(a.Z, b.Z) + n.Z * t),
                                       MathF.Max(MathF.Max(a.X, b.X) - n.X * t, MathF.Max(a.X, b.X) + n.X * t), MathF.Max(MathF.Max(a.Z, b.Z) - n.Z * t, MathF.Max(a.Z, b.Z) + n.Z * t));
                            if (door)
                            {
                                doorZones.Add(box);
                                // a hung door's leaf swings its own width round its hinge, out of the face the opening says
                                // it opens to (DoorOpensFront: the wall's front, +Z; else its back) -- both faces for a door that
                                // swings both ways, neither for a pocket door
                                if (o.HasDoor && o.Archetype != BuildingGen.ArchGarage) doorZones.AddRange(LeafBoxes(w, o));
                                var c = At(w, o.U + o.Width * 0.5f); float d = w.Thickness * 0.5f + 0.4f;
                                doorPts.Add((c.X + n.X * d, c.Z + n.Z * d)); doorPts.Add((c.X - n.X * d, c.Z - n.Z * d));
                            }
                            else if (o.Glazed) windows.Add((box, o.V));
                        }
                    }
                    var here = res.Furniture.Where(m => m.Floor == f).ToList();
                    var floorPieces = here.Where(m => m.On == null && !m.Mounted && !m.Ceiling).ToList();
                    // the floor at 0.1 m: wall, and blocked (wall, stairs, holes, furniture)
                    const float G = 0.1f, O = -1f;
                    int gnx = (int)((res.CellsX * Cell + 2f) / G) + 1, gnz = (int)((res.CellsZ * Cell + 2f) / G) + 1;
                    var isWall = new bool[gnx, gnz]; var blocked = new bool[gnx, gnz];
                    void Mark((float X0, float Z0, float X1, float Z1) b, bool[,] g)
                    {
                        for (int i = Math.Max(0, (int)MathF.Ceiling((b.X0 - O) / G - 0.5f)); i < gnx && O + (i + 0.5f) * G < b.X1; i++)
                            for (int j = Math.Max(0, (int)MathF.Ceiling((b.Z0 - O) / G - 0.5f)); j < gnz && O + (j + 0.5f) * G < b.Z1; j++) g[i, j] = true;
                    }
                    foreach (var w in walls) { Mark(Box(w), isWall); Mark(Box(w), blocked); }
                    foreach (var st in stairs) Mark(st, blocked);
                    // a piece's true outline: a round table is a disc, a turned chair a turned box (its axis-aligned box
                    // would make the chairs round a round table collide with it)
                    bool Boxy(FurniturePlan m) => !FurnitureCatalog.Round.ContainsKey(m.Name) && MathF.Abs(m.Yaw / 90f - MathF.Round(m.Yaw / 90f)) < 1e-4f;
                    bool Hits(FurniturePlan m, (float X0, float Z0, float X1, float Z1) b)
                    {
                        if (!Overlap(m.Footprint, b)) return false;
                        if (Boxy(m)) return true;
                        var fp = m.Footprint;
                        float x0 = MathF.Max(fp.X0, b.X0) + 0.01f, x1 = MathF.Min(fp.X1, b.X1) - 0.01f, z0 = MathF.Max(fp.Z0, b.Z0) + 0.01f, z1 = MathF.Min(fp.Z1, b.Z1) - 0.01f;
                        for (float x = x0; x <= x1; x += 0.05f)
                            for (float z = z0; z <= z1; z += 0.05f)
                                if (FurnitureCatalog.Covers(m.Name, m.X, m.Z, m.Yaw, x, z)) return true;
                        return false;
                    }
                    foreach (var m in floorPieces)
                    {
                        if (Boxy(m)) { Mark(m.Footprint, blocked); continue; }
                        var fp = m.Footprint;
                        for (int i = Math.Max(0, (int)MathF.Ceiling((fp.X0 - O) / G - 0.5f)); i < gnx && O + (i + 0.5f) * G < fp.X1; i++)
                            for (int j = Math.Max(0, (int)MathF.Ceiling((fp.Z0 - O) / G - 0.5f)); j < gnz && O + (j + 0.5f) * G < fp.Z1; j++)
                                if (FurnitureCatalog.Covers(m.Name, m.X, m.Z, m.Yaw, O + (i + 0.5f) * G, O + (j + 0.5f) * G)) blocked[i, j] = true;
                    }
                    // exactly, against the boxes: a grid cell whose centre sits on a wall's face is neither in nor out --
                    // and only the wall that is there: an open plan's span is no wall to stand a piece against
                    var wallBoxes = walls.SelectMany(SolidBoxes).ToList();
                    bool WallAt(float x, float z) => wallBoxes.Any(b => x >= b.Item1 && x <= b.Item3 && z >= b.Item2 && z <= b.Item4);
                    foreach (var m in here)
                    {
                        pieces++;
                        var fp = m.Footprint;
                        string nm = $"{Name(spec)}: {m}";
                        // a ceiling light: in its room, high enough to walk under, not hung over a flight
                        if (m.Ceiling)
                        {
                            ceilingLights++;
                            if (!m.Room.Holds(m.X, m.Z)) bad.Add($"{nm} hangs outside its room");
                            float drop = m.Name.Contains("Pendant") ? 0.95f : 0.28f;
                            if (m.Y - drop < f * Storey + 2.4f) bad.Add($"{nm} hangs down to {m.Y - drop - f * Storey:0.00} m");
                            if (stairs.Any(st => Overlap(fp, st))) bad.Add($"{nm} hangs over a flight");
                            continue;
                        }
                        if (m.On != null)
                        {
                            var hfp = m.On.Footprint; var cx = (fp.X0 + fp.X1) * 0.5f; var cz = (fp.Z0 + fp.Z1) * 0.5f;
                            if (!(cx > hfp.X0 && cx < hfp.X1 && cz > hfp.Z0 && cz < hfp.Z1)) bad.Add($"{nm} is not on the {m.On.Name} it stands on");
                            if (MathF.Abs(m.Y - (m.On.Y + FurnitureCatalog.Sizes[m.On.Name].H)) > 0.01f) bad.Add($"{nm} floats or sinks on its {m.On.Name}");
                            // a set on a table against a wall: not the wall the stairs run along
                            if (m.Name.StartsWith("Television") && wallBoxes.Any(b => Overlap(Grown(hfp, 0.3f), b, 0f))
                                && stairs.Any(st => Overlap(Grown(hfp, BuildingGen.StairWallReach - 0.05f), st, 0f))) bad.Add($"{nm} is on a wall the stairs run along");
                            continue;
                        }
                        if (m.Mounted)
                        {
                            if (stairs.Any(st => Overlap(Grown(fp, BuildingGen.StairWallReach - 0.05f), st, 0f))) bad.Add($"{nm} hangs on a wall the stairs run along");
                            // hung at a sensible height, on a wall with no window or door behind it at that height
                            // (the wall décor's heights, and the openings at them, are its rows' own, held to the piece's own span by
                            // WallDecorFollowsTheRules: an upper cabinet at 2.9, a key rack under a bathroom's high slit)
                            bool decor = BuildingGen.Decor.Any(r => r.Name == m.Name);
                            // (a clock near the top of the wall, strawberry round 12: "hang them much higher on the walls", then
                            // "bring clocks down slightly" -- its face's top within 0.3-0.5 m of the ceiling over it, the lowest over
                            // its outline's corners: the floor above's underside, a top storey's wall heads or roof -- the same gap
                            // on every storey)
                            if (m.Name == "Clock_0")
                            {
                                var cf = m.Footprint;
                                float clockTop = m.Y + FurnitureCatalog.Sizes[m.Name].H;
                                float over = new[] { (cf.X0, cf.Z0), (cf.X1, cf.Z0), (cf.X0, cf.Z1), (cf.X1, cf.Z1) }.Min(q => CeilingAt(res, f, q.Item1, q.Item2)), under = over - clockTop;
                                if (under < 0.3f || under > 0.5f) bad.Add($"{nm}: its face's top {under:0.00} m under the ceiling over it, {over - f * Storey:0.00} m up");
                                if (f == res.Floors - 1 && over - f * Storey > Storey - BuildingGen.Slab + 0.1f) topClocks++;
                            }
                            else if ((m.Y < f * Storey + 1.8f || m.Y > f * Storey + 2.6f) && !decor) bad.Add($"{nm} hangs at {m.Y - f * Storey:0.00} m");
                            var (bx, bz) = m.Front;
                            var behind = walls.Where(w => { var b = Box(w); var c = (X: m.X - bx * 0.3f, Z: m.Z - bz * 0.3f); return c.X > b.Item1 - 0.05f && c.X < b.Item3 + 0.05f && c.Z > b.Item2 - 0.05f && c.Z < b.Item4 + 0.05f; }).ToList();
                            if (behind.Count == 0) bad.Add($"{nm} hangs on no wall");
                            foreach (var w in behind)
                                foreach (var o in w.Openings)
                                {
                                    // along the wall, the opening's stretch against the set's
                                    var d = Dir(w);
                                    float s0 = (fp.X0 - w.X) * d.X + (fp.Z0 - w.Z) * d.Z, s1 = (fp.X1 - w.X) * d.X + (fp.Z1 - w.Z) * d.Z;
                                    bool across = MathF.Min(o.U1, MathF.Max(s0, s1)) - MathF.Max(o.U, MathF.Min(s0, s1)) > 0.05f;
                                    bool up = w.Y + o.V < m.Y + 1f && w.Y + o.V + o.Height > m.Y - 1f;
                                    if (across && up && !decor) bad.Add($"{nm} hangs over an opening ({o.Archetype})");
                                }
                            continue;
                        }
                        if (MathF.Abs(m.Y - f * Storey) > 0.01f) bad.Add($"{nm} is not on its floor (y {m.Y:0.00})");
                        // square to the walls, but for what gets moved in use, and that only a little
                        float off = MathF.Abs(((m.Yaw % 90f) + 90f + 45f) % 90f - 45f);
                        // a chair at a round table is square to the table instead: off is how far it looks past its middle
                        var round = m.Name.StartsWith("Chair") ? floorPieces.FirstOrDefault(t => t.Room == m.Room && FurnitureCatalog.Round.TryGetValue(t.Name, out var tr)
                                        && MathF.Sqrt((t.X - m.X) * (t.X - m.X) + (t.Z - m.Z) * (t.Z - m.Z)) < tr + 1.2f) : null;
                        if (round != null)
                        {
                            var (cfx, cfz) = m.Front; float tx = round.X - m.X, tz = round.Z - m.Z, tl = MathF.Sqrt(tx * tx + tz * tz);
                            off = MathF.Acos(Math.Clamp((cfx * tx + cfz * tz) / tl, -1f, 1f)) * 180f / MathF.PI;
                        }
                        float allowed = m.Name.StartsWith("Chair") ? 10f : m.Name.StartsWith("Table") ? 5f : m.Name.StartsWith("Couch") ? 3f : 0.01f;
                        if (off > allowed) bad.Add($"{nm} stands {off:0.0} degrees off square");
                        if (allowed > 0.01f) { movable++; if (off > 0.2f) turned++; }
                        var inner = Grown(fp, -0.02f);
                        if (!new[] { (inner.Item1, inner.Item2), (inner.Item3, inner.Item2), (inner.Item1, inner.Item4), (inner.Item3, inner.Item4) }.All(p => m.Room.Holds(p.Item1, p.Item2)))
                            bad.Add($"{nm} is not wholly in its room");
                        foreach (var w in walls) if (Hits(m, Box(w))) { bad.Add($"{nm} runs into a wall"); break; }
                        if (stairs.Any(st => Hits(m, st))) bad.Add($"{nm} stands on the stairs or over a hole");
                        if (tiles.Any(t => Hits(m, t))) bad.Add($"{nm} stands on the tile at a flight's end");
                        if (doorZones.Any(z => Hits(m, z))) bad.Add($"{nm} stands in a doorway");
                        // (a piece no taller than a window's sill -- a bathroom's high slit -- is under it, not in front of it)
                        if (FurnitureCatalog.Sizes[m.Name].H > Tall && windows.Any(z => FurnitureCatalog.Sizes[m.Name].H > z.Sill && Hits(m, z.Box))) bad.Add($"{nm} stands in front of a window");
                        foreach (var o in floorPieces) if (o != m && Overlap(fp, o.Footprint) && FurnitureCatalog.Intersects(m, o)) { bad.Add($"{nm} overlaps {o}"); break; }
                        // doors and drawers open into clear floor: no wall, stairs or other piece in front of it
                        float swing = m.Name.StartsWith("Wardrobe") ? 0.9f : m.Name.StartsWith("Fridge") ? 0.8f
                                    : new[] { "Oven", "Disher", "Washer", "Dryer", "Files", "Cooler", "Ice_Box" }.Any(m.Name.StartsWith) ? 0.7f : 0f;
                        if (swing > 0f)
                        {
                            swung++;
                            var sz = FurnitureCatalog.Sizes[m.Name]; var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                            var p0 = (X: m.X + rx * (sz.X0 + 0.05f) + fx * (sz.Y1 + 0.05f), Z: m.Z + rz * (sz.X0 + 0.05f) + fz * (sz.Y1 + 0.05f));
                            var p1 = (X: m.X + rx * (sz.X1 - 0.05f) + fx * (sz.Y1 + swing), Z: m.Z + rz * (sz.X1 - 0.05f) + fz * (sz.Y1 + swing));
                            var zone = (MathF.Min(p0.X, p1.X), MathF.Min(p0.Z, p1.Z), MathF.Max(p0.X, p1.X), MathF.Max(p0.Z, p1.Z));
                            if (walls.Any(w => Overlap(zone, Box(w))) || stairs.Any(st => Overlap(zone, st)) || floorPieces.Any(o => o != m && Overlap(zone, o.Footprint)))
                                bad.Add($"{nm}: its doors cannot open");
                        }
                    }
                    // the walk: per room, doors, stair ends and the fronts of what you use, joined by clear floor
                    foreach (var room in res.Layout.Where(g => g.Floor == f))
                    {
                        rooms++;
                        var mine = floorPieces.Where(m => m.Room == room).ToList();
                        kinds[room.Kind] = kinds.GetValueOrDefault(room.Kind) + 1;
                        // what makes the room one
                        bool Has(params string[] ns) => here.Any(m => m.Room == room && ns.Any(n => m.Name.StartsWith(n)));
                        // (a building squeezed into a plot that says which of its rooms came out short is excused those --
                        // counted, and bounded below)
                        void Need(string what, bool ok)
                        {
                            if (ok) return;
                            if (ShortBy(res, room.Kind)) shortExcused++;
                            else missing[$"{room.Kind} {what}"] = missing.GetValueOrDefault($"{room.Kind} {what}") + 1;
                        }
                        switch (room.Kind)
                        {
                            case RoomKind.Bedroom: Need("bed", Has("Bed")); break;
                            case RoomKind.LivingRoom: Need("couch", Has("Couch")); break;
                            case RoomKind.DiningRoom: Need("table", Has("Table")); Need("4 chairs", here.Count(m => m.Room == room && m.Name.StartsWith("Chair")) >= 4); break;
                            case RoomKind.Kitchen: Need("fridge", Has("Fridge")); Need("sink", Has("Counter_1")); Need("stove", Has("Oven")); if (Has("Fridge") && Has("Counter_1") && Has("Oven")) kitchensFull++;
                            {
                                // required outright: a plain counter in the run beside the sink or the stove (the same way
                                // round, within 0.15 m)...
                                bool SameWay(FurniturePlan a, FurniturePlan b) => MathF.Abs(((a.Yaw - b.Yaw) % 360f + 360f) % 360f) < 1f;
                                bool worktop = mine.Any(c => c.Name == "Counter_0" && mine.Any(h => (h.Name == "Counter_1" || h.Name.StartsWith("Oven")) && SameWay(c, h)
                                                                                                       && Overlap(Grown(h.Footprint, 0.15f), c.Footprint, 0f)));
                                if (worktop) { worktops++; if (room.Unit >= 0) flatWorktops++; } else if (ShortBy(res, room.Kind, "counter")) shortExcused++; else bad.Add($"{Name(spec)}: {room} has no plain counter beside its sink or stove");
                                // ...and where the home eats in its kitchen (a kitchen-diner, or a house or flat with no dining
                                // room of its own), a table with a chair at it
                                if (res.KitchenDiner || (res.Kind is BuildingKind.Residential or BuildingKind.Apartment
                                                         && !res.Layout.Any(g => g.Kind == RoomKind.DiningRoom && g.Unit == room.Unit)))
                                {
                                    eatIns++;
                                    var tables = mine.Where(t => t.Name is "Table_Wood_0" or "Table_Wood_1" or "Table_Wood_3").ToList();
                                    bool seated = tables.Any(t => mine.Any(c => c.Name.StartsWith("Chair") && MathF.Sqrt((t.X - c.X) * (t.X - c.X) + (t.Z - c.Z) * (t.Z - c.Z)) < 3f));
                                    if (seated) { eatInsSeated++; if (room.Unit >= 0) flatEatIns++; }
                                    else if (ShortBy(res, room.Kind, "dining table")) shortExcused++;
                                    else bad.Add($"{Name(spec)}: {room} is where the house eats, with {(tables.Count == 0 ? "no table" : "a table and no chair")}");
                                }
                            }
                                break;
                            case RoomKind.Bathroom: Need("tub or sink", Has("Tub", "Counter_1")); break;
                            case RoomKind.Office: Need("desk", Has("Table_Wood_2")); break;
                            case RoomKind.BreakRoom: Need("table", Has("Table")); Need("sink", Has("Counter_1")); Need("fridge", Has("Fridge")); break;
                            case RoomKind.Shop: Need("shelves", Has("Shelf_1")); if (res.Front?.A == room) Need("checkout", Has("Checkout")); break;
                            case RoomKind.Warehouse: case RoomKind.Storage: Need("racks", Has("Shelf")); break;
                        }
                        if (mine.Count == 0) continue;
                        walkedRooms++;
                        float gx0 = room.Rects.Min(r => r.X0) * Cell, gz0 = room.Rects.Min(r => r.Z0) * Cell, gx1 = room.Rects.Max(r => r.X1) * Cell, gz1 = room.Rects.Max(r => r.Z1) * Cell;
                        int nx = (int)((gx1 - gx0) / 0.1f), nz = (int)((gz1 - gz0) / 0.1f);
                        var rects = room.Rects.ToList();
                        var open = new bool[nx, nz];
                        for (int i = 0; i < nx; i++)
                            for (int j = 0; j < nz; j++)
                            {
                                float x = gx0 + (i + 0.5f) * 0.1f, z = gz0 + (j + 0.5f) * 0.1f;
                                int bi = (int)((x - O) / G), bj = (int)((z - O) / G);
                                open[i, j] = rects.Any(r => x >= r.X0 * Cell && x < r.X1 * Cell && z >= r.Z0 * Cell && z < r.Z1 * Cell) && !blocked[bi, bj];
                            }
                        var targets = doorPts.Where(p => room.Holds(p.X, p.Z)).Select(p => ("a door", p.X, p.Z)).ToList();
                        foreach (var st in res.Stairs.Where(st => (st.Floor == f && StartsOnFloor(st)) || (st.Floor == f - 1 && EndsOnFloor(st))))
                        {
                            var (bx, bz) = st.Back; var (rx, rz) = st.Right;
                            float mx = st.X + rx * st.Width * 0.5f, mz = st.Z + rz * st.Width * 0.5f;
                            var p = st.Floor == f ? (mx - bx * 0.4f, mz - bz * 0.4f) : (mx + bx * (st.Run + 0.65f), mz + bz * (st.Run + 0.65f));
                            if (room.Holds(p.Item1, p.Item2)) targets.Add(("a stair end", p.Item1, p.Item2));
                        }
                        // and what you use, from its front or a side (a workbench along a wall is used from its long side)
                        var uses = new List<(string What, (float X, float Z)[] At)>();
                        foreach (var m in mine.Where(m => Use(m.Name)))
                        {
                            var s = FurnitureCatalog.Sizes[m.Name]; var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                            float mx = (s.X0 + s.X1) * 0.5f, my = (s.Y0 + s.Y1) * 0.5f;
                            uses.Add((m.Name, new[] { (m.X + rx * mx + fx * (s.Y1 + 0.4f), m.Z + rz * mx + fz * (s.Y1 + 0.4f)),
                                                      (m.X + rx * (s.X1 + 0.4f) + fx * my, m.Z + rz * (s.X1 + 0.4f) + fz * my),
                                                      (m.X + rx * (s.X0 - 0.4f) + fx * my, m.Z + rz * (s.X0 - 0.4f) + fz * my) }));
                        }
                        if (targets.Count == 0 && uses.Count > 0) targets.Add(($"the {uses[0].What}", uses[0].At[0].X, uses[0].At[0].Z));
                        if (targets.Count == 0) continue;
                        (int, int)? Cell0(float x, float z)
                        {
                            int i0 = (int)((x - gx0) / 0.1f), j0 = (int)((z - gz0) / 0.1f);
                            for (int r = 0; r <= 3; r++)
                                for (int i = i0 - r; i <= i0 + r; i++) for (int j = j0 - r; j <= j0 + r; j++)
                                    if (i >= 0 && j >= 0 && i < nx && j < nz && open[i, j]) return (i, j);
                            return null;
                        }
                        var start = Cell0(targets[0].Item2, targets[0].Item3);
                        if (start == null) { bad.Add($"{Name(spec)} floor {f} {room}: {targets[0].Item1} is not clear floor"); continue; }
                        var seen = new bool[nx, nz]; var q = new Queue<(int, int)>(); q.Enqueue(start.Value); seen[start.Value.Item1, start.Value.Item2] = true;
                        while (q.Count > 0)
                        {
                            var (i, j) = q.Dequeue();
                            foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            {
                                int a = i + di, b = j + dj;
                                if (a < 0 || b < 0 || a >= nx || b >= nz || seen[a, b] || !open[a, b]) continue;
                                seen[a, b] = true; q.Enqueue((a, b));
                            }
                        }
                        bool Reached(float x, float z) { var c = Cell0(x, z); return c != null && seen[c.Value.Item1, c.Value.Item2]; }
                        // a bed's way in: beside a long side, a strip 0.75 m deep from its foot to 1.1 m short of its head (where
                        // a lamp stands), every cell of it clear floor of the room, walked to -- both sides of a double
                        foreach (var m in mine.Where(m => m.Name.StartsWith("Bed")))
                        {
                            var s = FurnitureCatalog.Sizes[m.Name]; var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                            int ways = 0;
                            foreach (int sgn in new[] { -1, 1 })
                            {
                                float side = sgn > 0 ? s.X1 : s.X0;
                                var cs = new[] { (side + sgn * 0.05f, s.Y0 + 1.1f), (side + sgn * 0.75f, s.Y0 + 1.1f), (side + sgn * 0.05f, s.Y1), (side + sgn * 0.75f, s.Y1) }
                                    .Select(q => (X: m.X + rx * q.Item1 + fx * q.Item2, Z: m.Z + rz * q.Item1 + fz * q.Item2)).ToList();
                                float bx0 = cs.Min(q => q.X), bx1 = cs.Max(q => q.X), bz0 = cs.Min(q => q.Z), bz1 = cs.Max(q => q.Z);
                                bool clearAll = true, got = false; int n = 0;
                                for (int i = (int)MathF.Ceiling((bx0 - gx0) / 0.1f - 0.5f); gx0 + (i + 0.5f) * 0.1f < bx1; i++)
                                    for (int j = (int)MathF.Ceiling((bz0 - gz0) / 0.1f - 0.5f); gz0 + (j + 0.5f) * 0.1f < bz1; j++)
                                    {
                                        n++;
                                        if (i < 0 || j < 0 || i >= nx || j >= nz || !open[i, j]) clearAll = false;
                                        else if (seen[i, j]) got = true;
                                    }
                                if (clearAll && got && n > 0) ways++;
                            }
                            bool dbl = m.Name == "Bed_0";
                            if (dbl) doubles++;
                            if (ways >= (dbl ? 2 : 1)) { bedWays++; if (room.Unit >= 0) flatBeds++; }
                            else bad.Add($"{Name(spec)}: {m}: a {(dbl ? "double" : "single")} bed with {ways} long side{(ways == 1 ? "" : "s")} to get in at");
                        }
                        foreach (var (what, x, z) in targets.Skip(1))
                            if (!Reached(x, z)) { bad.Add($"{Name(spec)} floor {f} {room}: {what} cannot be walked to from {targets[0].Item1}"); break; }
                        foreach (var (what, at) in uses)
                            if (!at.Any(p => Reached(p.X, p.Z))) { bad.Add($"{Name(spec)} floor {f} {room}: no side of the {what} can be walked to from {targets[0].Item1}"); break; }
                        // feng shui
                        bool WallBehind((float X0, float Z0, float X1, float Z1) fp, float bx, float bz)
                        {
                            // a wall within 0.15 m behind the side facing (bx, bz), along 80% of it
                            const float Back = 0.14f;
                            int n = 0, hit = 0;
                            if (MathF.Abs(bx) > 0.5f)
                            {
                                float x = bx > 0 ? fp.X1 + Back : fp.X0 - Back;
                                for (float z = fp.Z0 + 0.05f; z < fp.Z1; z += 0.1f) { n++; if (WallAt(x, z)) hit++; }
                            }
                            else
                            {
                                float z = bz > 0 ? fp.Z1 + Back : fp.Z0 - Back;
                                for (float x = fp.X0 + 0.05f; x < fp.X1; x += 0.1f) { n++; if (WallAt(x, z)) hit++; }
                            }
                            return n > 0 && hit >= 0.8 * n;
                        }
                        var roomDoors = doorPts.Where(p => room.Holds(p.X, p.Z)).ToList();
                        foreach (var m in mine)
                        {
                            var (fx, fz) = m.Front; var (rx, rz) = m.Right; var s = FurnitureCatalog.Sizes[m.Name]; var fp = m.Footprint;
                            string nm = $"{Name(spec)}: {m}";
                            // what stands at a wall stands with its back to one, not half off its end
                            if (new[] { "Wardrobe", "Shelf_2", "Fridge", "Counter", "Oven", "Disher", "Washer", "Dryer", "Files", "Cooler", "Ice_Box" }.Any(m.Name.StartsWith))
                            {
                                wallPieces++;
                                // (or back to back with another counter: an island)
                                bool island = m.Name.StartsWith("Counter") && mine.Any(o => o != m && o.Name.StartsWith("Counter") && (o.Front.X * fx + o.Front.Z * fz) < -0.9f
                                                                                     && MathF.Abs((o.X - m.X) * fx + (o.Z - m.Z) * fz + 1.0f) < 0.1f && MathF.Abs((o.X - m.X) * rx + (o.Z - m.Z) * rz) < 0.1f);
                                if (island) islands++;
                                else if (!WallBehind(fp, -fx, -fz)) bad.Add($"{nm}: its back is not to a wall");
                            }
                            if (m.Name.StartsWith("Bed"))
                            {
                                beds++;
                                if (!WallBehind(fp, -fx, -fz)) Feng("a bed's head not to a wall");
                                var head = fx > 0.5f ? (fp.X0 - 0.6f, fp.Z0, fp.X0, fp.Z1) : fx < -0.5f ? (fp.X1, fp.Z0, fp.X1 + 0.6f, fp.Z1)
                                         : fz > 0.5f ? (fp.X0, fp.Z0 - 0.6f, fp.X1, fp.Z0) : (fp.X0, fp.Z1, fp.X1, fp.Z1 + 0.6f);
                                if (windows.Any(z => Overlap(head, z.Box))) Feng("a bed's head under a window");
                                foreach (var (dx, dz) in roomDoors)
                                {
                                    float ahead = (dx - m.X) * fx + (dz - m.Z) * fz, across = (dx - m.X) * rx + (dz - m.Z) * rz;
                                    if (ahead > s.Y1 && MathF.Abs(across) < (s.X1 - s.X0) * 0.5f) { Feng("a bed with the door straight past its foot"); break; }
                                }
                            }
                            // (a living room's couch: a break room's has no set to watch)
                            if (m.Name.StartsWith("Couch") && room.Kind == RoomKind.LivingRoom)
                            {
                                couches++;
                                if (!WallBehind(fp, -fx, -fz)) Feng("a couch's back not to a wall");
                                // a set it can watch: facing it, near enough for its kind, nothing tall between
                                bool Watch(FurniturePlan tv)
                                {
                                    var (tx, tz) = tv.Front; float vx = m.X - tv.X, vz = m.Z - tv.Z, d = MathF.Sqrt(vx * vx + vz * vz);
                                    float reach = tv.Name == "Television_0" ? 8.5f : 5f;
                                    if ((vx * tx + vz * tz) / d < 0.7f || d > reach || d < 1.8f) return false;
                                    // straight ahead of the couch: off its middle line by the generator's bound, and what the
                                    // couch's few degrees of jitter swing that line by at this distance
                                    var (crx, crz) = m.Right;
                                    if (MathF.Abs(-vx * crx - vz * crz) > BuildingGen.TvOffAxis + 0.06f * d) return false;
                                    for (float t = 0.6f; t < d - 0.6f; t += 0.2f)
                                    {
                                        float x = tv.X + vx * t / d, z = tv.Z + vz * t / d;
                                        if (WallAt(x, z)) return false;
                                        if (mine.Any(o => o != m && o != tv.On && FurnitureCatalog.Sizes[o.Name].H > 1.4f && x > o.Footprint.X0 && x < o.Footprint.X1 && z > o.Footprint.Z0 && z < o.Footprint.Z1)) return false;
                                    }
                                    return true;
                                }
                                var set = here.Where(t => t.Room == room && t.Name.StartsWith("Television")).FirstOrDefault(Watch);
                                if (set != null) { tvs++; if (set.Mounted) wallSets++; else crts++; }
                            }
                            if (m.Name.StartsWith("Chair"))
                            {
                                chairs++;
                                var table = mine.Where(t => t.Name.StartsWith("Table")).OrderBy(t => (t.X - m.X) * (t.X - m.X) + (t.Z - m.Z) * (t.Z - m.Z)).FirstOrDefault();
                                if (table == null) bad.Add($"{nm}: a chair with no table");
                                else
                                {
                                    float vx = table.X - m.X, vz = table.Z - m.Z, d = MathF.Sqrt(vx * vx + vz * vz) + 1e-3f;
                                    if ((vx * fx + vz * fz) / d < 0.7f) bad.Add($"{nm}: the chair does not face its table");
                                    if (room.Kind == RoomKind.Office)
                                    {
                                        desks++;
                                        if (!WallBehind(fp, -fx, -fz)) bad.Add($"{nm}: the desk chair's back is not to a wall");
                                        if (roomDoors.Count > 0 && !roomDoors.Any(p => (p.X - m.X) * fx + (p.Z - m.Z) * fz > 0)) bad.Add($"{nm}: the desk chair faces away from every door");
                                    }
                                }
                            }
                        }
                        var fr = mine.FirstOrDefault(m => m.Name.StartsWith("Fridge")); var sk = mine.FirstOrDefault(m => m.Name == "Counter_1"); var ov = mine.FirstOrDefault(m => m.Name.StartsWith("Oven"));
                        if (room.Kind == RoomKind.Kitchen && sk != null && ov != null && MathF.Abs(((sk.Yaw - ov.Yaw) % 180f + 180f) % 180f - 90f) < 10f) lKitchens++;
                        if (room.Kind == RoomKind.Kitchen && fr != null && !mine.Any(o => o != fr && (o.Name.StartsWith("Counter") || o.Name.StartsWith("Disher") || o.Name.StartsWith("Oven")) && Overlap(Grown(fr.Footprint, 0.15f), o.Footprint, 0f))) freeFridges++;
                        if (room.Kind == RoomKind.Kitchen && fr != null && ov != null && Overlap(Grown(fr.Footprint, 0.3f), ov.Footprint)) bad.Add($"{Name(spec)}: {room}'s fridge stands beside its stove");
                        if (room.Kind == RoomKind.Shop)
                        {
                            var gs = mine.Where(o => o.Name == "Shelf_1").ToList();
                            // all one way, and where there are rows, each in a column with one in another row
                            bool alongX(FurniturePlan g) => MathF.Abs(MathF.Sin(g.Yaw * MathF.PI / 180f)) < 0.5f;
                            int rowsHere = gs.Select(g => MathF.Round(alongX(g) ? g.Z : g.X, 1)).Distinct().Count();
                            if (gs.Count > 0) { shopsWithShelves++; if (rowsHere > 1) shopsWithRows++; }
                            foreach (var g in gs)
                            {
                                gondolas++;
                                if (gs.Any(o => alongX(o) != alongX(g))) bad.Add($"{Name(spec)}: {g} is turned across the others");
                                bool lined = gs.Any(o => o != g && MathF.Abs((alongX(g) ? o.Z - g.Z : o.X - g.X)) > 0.5f && MathF.Abs(alongX(g) ? o.X - g.X : o.Z - g.Z) < 0.05f);
                                if (rowsHere > 1 && !lined) bad.Add($"{Name(spec)}: {g} lines up with nothing in the next row");
                            }
                        }
                        if (room.Kind == RoomKind.Kitchen && sk != null && ov != null)
                        {
                            var a = Grown(sk.Footprint, 0.3f);
                            if (Overlap(a, ov.Footprint)) bad.Add($"{Name(spec)}: {room}'s stove stands beside its sink");
                            if (fr != null)
                            {
                                float D(FurniturePlan p, FurniturePlan o) => MathF.Sqrt((p.X - o.X) * (p.X - o.X) + (p.Z - o.Z) * (p.Z - o.Z));
                                float l1 = D(fr, sk), l2 = D(sk, ov), l3 = D(ov, fr);
                                // (the real 26 ft, 7.9 m, for furniture about twice real size: 13.5 m)
                                if (MathF.Min(l1, MathF.Min(l2, l3)) < 1.2f) bad.Add($"{Name(spec)}: {room}'s fridge, sink and stove are {l1:0.0}, {l2:0.0} and {l3:0.0} m apart");
                                else if (l1 + l2 + l3 > 13.5f) Feng("a kitchen's fridge, sink and stove more than 13.5 m round");
                            }
                        }
                    }
                }
            }
            int Of(RoomKind k) => kinds.GetValueOrDefault(k);
            TestContext.Progress.WriteLine($"[furn] {pieces} pieces in {walkedRooms} of {rooms} rooms; {beds} beds, {desks} desk chairs, {couches} couches ({tvs} with a television they can watch: {wallSets} on the wall, {crts} on a table), {chairs} chairs, {wallPieces} pieces backed to a wall ({islands} island counters), {swung} doors and drawers with room to open, kitchens {lKitchens} L, {freeFridges} with the fridge on its own; {gondolas} gondolas ({shopsWithRows} of {shopsWithShelves} shops in aligned rows), {turned} of {movable} tables, chairs and couches a little off square, {kitchensFull} of {Of(RoomKind.Kitchen)} kitchens with fridge, sink and stove; missing: {(missing.Count == 0 ? "nothing" : string.Join(", ", missing.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")))} ({shortExcused} more in plot-fitted buildings that say so); {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(shortExcused, Is.LessThanOrEqualTo(All().Count(x => x.Spec.FitPlot) / 4), "plot-fitted buildings often come out short of what their rooms are for");
            foreach (var g in bad.GroupBy(b => System.Text.RegularExpressions.Regex.Replace(b.Substring(b.IndexOf(':') + 1), @"[\d.,()-]+", "#")).OrderByDescending(g => g.Count()).Take(12))
                TestContext.Progress.WriteLine($"[furn]   {g.Count()} x {g.Key.Trim()} -- e.g. {g.First()}");
            Assert.That(pieces, Is.GreaterThan(3000));
            Assert.That(ceilingLights, Is.GreaterThan(1000), "rooms get ceiling lights");
            Assert.That(wallPieces, Is.GreaterThan(3000));
            TestContext.Progress.WriteLine($"[furn-clock] {topClocks} clocks under a top storey's higher ceiling, held to the same gap");
            Assert.That(topClocks, Is.GreaterThan(100), "top-storey clocks were there to check");
            // a room without what makes it one is a room the furnisher gave up on: a few that are too small for it, no more
            foreach (var (what, n) in missing)
            {
                var kind = Enum.Parse<RoomKind>(what.Split(' ')[0]);
                Assert.That(n, Is.LessThanOrEqualTo(Math.Max(2, Of(kind) / 100)), $"{n} of {Of(kind)} {what.Split(' ')[0]}s without {what.Substring(what.IndexOf(' ') + 1)}");
            }
            // every couch has a set it can watch, of either kind; and the kitchens vary
            Assert.That(tvs, Is.GreaterThanOrEqualTo(couches - Math.Max(1, couches / 100)), "couches with no television they can watch");
            Assert.That(wallSets, Is.GreaterThan(couches / 5)); Assert.That(crts, Is.GreaterThan(couches / 5));
            int kitchens = Of(RoomKind.Kitchen);
            Assert.That(lKitchens, Is.GreaterThan(kitchens / 5), "kitchens are all one run");
            Assert.That(freeFridges, Is.GreaterThan(kitchens / 10), "the fridge never stands on its own");
            Assert.That(islands, Is.GreaterThan(10), "no kitchen has an island");
            Assert.That(turned, Is.GreaterThan(movable / 5), "what gets moved in use all stands square");
            Assert.That(shopsWithRows, Is.GreaterThanOrEqualTo(shopsWithShelves / 2), "shops' gondolas rarely stand in aligned rows");
            // the round-9 requirements held over enough cases to mean it, and the feng shui given up only now and then
            TestContext.Progress.WriteLine($"[furn-r9] {bedWays} of {beds} beds with their way in ({doubles} doubles, both sides); {worktops} of {kitchens} kitchens with a plain counter at the sink or stove; {eatInsSeated} of {eatIns} kitchens where the house eats with a table and a chair; of these in flats: {flatBeds} beds, {flatWorktops} kitchens with the counter, {flatEatIns} seated; feng shui given up: {(feng.Count == 0 ? "none" : string.Join(", ", feng.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")))}");
            Assert.That(doubles, Is.GreaterThan(100), "too few double beds to test their way in");
            Assert.That(eatIns, Is.GreaterThan(50), "too few kitchens where the house eats to test their table");
            foreach (var (what, n) in feng)
            {
                int of = what.Contains("bed") ? beds : what.Contains("couch") ? couches : kitchens;
                Assert.That(n, Is.LessThanOrEqualTo(Math.Max(2, of / 20)), $"{n} of {of}: {what} -- a preference, but given up too often");
            }
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // R: a block of flats (strawberry: "apartments should function differently from houses, no staircase inside the
        // apartment, shared corridor and staircases"), read off its walls and flights like everything above -- the layout
        // only names the rooms and says which flat each is in. A flat: one floor; a living room, a kitchen, a bathroom no
        // bigger than a bathroom may be, one or two bedrooms (or a bedroom and a study). Shared space: a corridor on every
        // floor and, with floors above, a stairwell on every floor, the same cells on each. Doors: none between two flats;
        // into a flat exactly one, from the corridor into its living room or its own hall; its bedrooms, bathroom and study
        // only off its living room or hall; no flat with a door of its own to outside. Stairs: every flight standing in the
        // stairwell, over the stairwell above, and flights from every floor to the next. And every room walked to from
        // outside, through the doors as cut and up the flights, never from one flat into another.
        static List<string> FlatViolations(BuildingGenResult res, string name, Dictionary<string, int> tally = null)
        {
            var bad = new List<string>();
            void T(string k, int n = 1) { if (tally != null) tally[k] = tally.GetValueOrDefault(k) + n; }
            var flats = res.Layout.Where(g => g.Unit >= 0).GroupBy(g => g.Unit).ToDictionary(g => g.Key, g => g.ToList());
            T("flats", flats.Count);
            foreach (var (id, rs) in flats)
            {
                int Of(RoomKind k) => rs.Count(g => g.Kind == k);
                if (rs.Select(g => g.Floor).Distinct().Count() != 1) bad.Add($"{name}: flat {id} is on more than one floor");
                if (Of(RoomKind.LivingRoom) != 1 || Of(RoomKind.Kitchen) != 1 || Of(RoomKind.Bathroom) != 1 || Of(RoomKind.Bedroom) < 1
                    || Of(RoomKind.Bedroom) + Of(RoomKind.Office) > 2 || Of(RoomKind.Hall) > 1
                    || rs.Any(g => !(g.Kind is RoomKind.LivingRoom or RoomKind.Kitchen or RoomKind.Bathroom or RoomKind.Bedroom or RoomKind.Office or RoomKind.Hall)))
                    bad.Add($"{name}: flat {id} is {string.Join(", ", rs.Select(g => g.Kind))}");
                foreach (var g in rs.Where(g => g.Kind == RoomKind.Bathroom && g.Cells > BuildingGen.MaxCells(RoomKind.Bathroom))) bad.Add($"{name}: {g} is {g.Cells} cells");
                T(Of(RoomKind.Bedroom) + Of(RoomKind.Office) == 2 ? (Of(RoomKind.Office) > 0 ? "flats with a study" : "two-bedroom flats") : "one-bedroom flats");
                T(Of(RoomKind.Hall) == 1 ? "flats entered by a hall" : "flats entered by the living room");
                // a flat's entry hall is a vestibule at its door, not a second corridor beside the shared one: two cells (three
                // where it bends), one deep -- the rooms behind run on to the corridor wall (the coordinator, on e85d091f7's
                // 51 m strips)
                foreach (var h in rs.Where(g => g.Kind == RoomKind.Hall))
                {
                    bool bent = h.More.Count > 0;
                    int most = bent ? 3 : 2;
                    T("entry-hall cells", h.Cells);
                    if (h.Cells > most || h.Rects.Any(q => Math.Min(q.X1 - q.X0, q.Z1 - q.Z0) > 1)) bad.Add($"{name}: flat {id}'s entry hall {h} is {h.Cells} cells, not a vestibule of {most} or fewer one deep");
                }
                // and a flat is a flat, not a whole row: its frontage no more than its rooms at their designed widths (three
                // cells a room, two for the bathroom) and two cells over -- the rest of a long row goes to more flats or to
                // shared space, never to one flat's rooms
                var cells = rs.SelectMany(g => g.Rects).ToList();
                int front = cells.Max(q => q.X1) - cells.Min(q => q.X0), designed = 3 * rs.Count(g => !(g.Kind is RoomKind.Hall or RoomKind.Bathroom)) + 2 * Of(RoomKind.Bathroom);
                T("flat frontage cells", front);
                // its living room's own column no wider than a couch can see a flat television across (LivingAcross): wider,
                // and every set in it is a CRT on a table out in the room
                foreach (var g in rs.Where(g => g.Kind == RoomKind.LivingRoom && g.X1 - g.X0 > BuildingGen.LivingAcross))
                    bad.Add($"{name}: flat {id}'s {g} is {g.X1 - g.X0} cells across its row, more than {BuildingGen.LivingAcross}");
                if (tally != null) { var key = $"frontage {front}"; tally[key] = tally.GetValueOrDefault(key) + 1; }
                if (front > designed + 2) bad.Add($"{name}: flat {id} is {front} cells along its row, its rooms {designed} at their designed widths");
            }
            var shared = res.Layout.Where(g => g.Unit < 0).ToList();
            foreach (var g in shared.Where(g => !(g.Kind is RoomKind.Hall or RoomKind.Stairwell))) bad.Add($"{name}: {g} is shared but neither corridor nor stairwell");
            for (int f = 0; f < res.Floors; f++)
            {
                int halls = shared.Count(g => g.Floor == f && g.Kind == RoomKind.Hall), wells = shared.Count(g => g.Floor == f && g.Kind == RoomKind.Stairwell);
                if (halls != 1) bad.Add($"{name} floor {f}: {halls} corridors");
                if (wells != (res.Floors > 1 ? 1 : 0)) bad.Add($"{name} floor {f}: {wells} stairwells");
            }
            if (shared.Where(g => g.Kind == RoomKind.Stairwell).Select(g => (g.X0, g.Z0, g.X1, g.Z1)).Distinct().Count() > 1) bad.Add($"{name}: the stairwell is not the same cells on every floor");
            // (how often the shared space was stretched to make up a row: the generator's least-used paths)
            var corridor0 = shared.FirstOrDefault(g => g.Floor == 0 && g.Kind == RoomKind.Hall);
            if (corridor0 != null && corridor0.More.Count > 0) T(res.Floors == 1 ? "lobbies of one storey" : "corridors with an alcove");
            var well0 = shared.FirstOrDefault(g => g.Floor == 0 && g.Kind == RoomKind.Stairwell);
            if (well0 != null && well0.X1 - well0.X0 > res.Bay.X1 - res.Bay.X0 + 1) T("stairwells wider than their stairs and a landing");
            // the doors, as cut in the walls
            var ways = flats.Keys.ToDictionary(k => k, _ => 0);
            var edges = new Dictionary<GenRoom, HashSet<GenRoom>>();
            var outside = new GenRoom { Kind = RoomKind.Unassigned, Floor = -1 };
            void Link(GenRoom a, GenRoom b) { if (a == null || b == null || a == b) return;
                (edges.TryGetValue(a, out var p) ? p : edges[a] = new HashSet<GenRoom>()).Add(b);
                (edges.TryGetValue(b, out var q) ? q : edges[b] = new HashSet<GenRoom>()).Add(a); }
            for (int f = 0; f < res.Floors; f++)
                foreach (var d in Doors(res, f))
                {
                    T("doors");
                    var (a, b) = (d.A, d.B);
                    if (a == null || b == null)
                    {
                        var r = a ?? b;
                        if (r != null && r.Unit >= 0) bad.Add($"{name} floor {f}: flat {r.Unit}'s {r.Kind} has a door to outside");
                        if (r != null && f == 0) Link(r, outside);
                        continue;
                    }
                    Link(a, b);
                    if (a.Unit >= 0 && b.Unit >= 0 && a.Unit != b.Unit) { bad.Add($"{name} floor {f}: a door between flat {a.Unit}'s {a.Kind} and flat {b.Unit}'s {b.Kind}"); continue; }
                    if ((a.Unit < 0) != (b.Unit < 0))
                    {
                        var (sh, fl) = a.Unit < 0 ? (a, b) : (b, a);
                        ways[fl.Unit]++;
                        T("ways in");
                        if (sh.Kind != RoomKind.Hall) bad.Add($"{name} floor {f}: flat {fl.Unit} is entered from the {sh.Kind}, not the corridor");
                        if (!(fl.Kind is RoomKind.LivingRoom or RoomKind.Hall)) bad.Add($"{name} floor {f}: flat {fl.Unit} is entered into its {fl.Kind}");
                        continue;
                    }
                    if (a.Unit >= 0)
                        foreach (var (c, p) in new[] { (a, b), (b, a) })
                            if (c.Kind is RoomKind.Bedroom or RoomKind.Bathroom or RoomKind.Office && !(p.Kind is RoomKind.LivingRoom or RoomKind.Hall))
                                bad.Add($"{name} floor {f}: flat {c.Unit}'s {c.Kind} opens off its {p.Kind}");
                }
            foreach (var (id, n) in ways) if (n != 1) bad.Add($"{name}: flat {id} has {n} doors to shared space");
            // the stairs: the flight and the floor over it all stairwell; flights from every floor to the next
            foreach (var s in res.Stairs)
            {
                T("flights");
                var (x0, z0, x1, z1) = Grown(Foot(s), -0.05f);
                bool inWell = true;
                foreach (int f in new[] { s.Floor, s.Floor + 1 })
                    for (float x = x0; x <= x1 + 1e-3f && inWell; x += (x1 - x0) / 6f)
                        for (float z = z0; z <= z1 + 1e-3f && inWell; z += (z1 - z0) / 4f)
                            if (RoomAt(res, f, x, z) is not { Kind: RoomKind.Stairwell, Unit: < 0 } r) { inWell = false; bad.Add($"{name}: flight {s.Floor} stands over {RoomAt(res, f, x, z)?.ToString() ?? "no room"} on floor {f}"); }
                var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                float mx = s.X + rx * s.Width * 0.5f, mz = s.Z + rz * s.Width * 0.5f;
                Link(RoomAt(res, s.Floor, mx - bx * 0.3f, mz - bz * 0.3f), RoomAt(res, s.Floor + 1, mx + bx * (s.Run + 0.3f), mz + bz * (s.Run + 0.3f)));
            }
            for (int f = 0; f + 1 < res.Floors; f++)
                if (!res.Stairs.Any(s => s.Floor == f && StartsOnFloor(s)) || !res.Stairs.Any(s => s.Floor == f && EndsOnFloor(s)))
                    bad.Add($"{name}: no stairs from floor {f} to floor {f + 1}");
            // and the walk in
            var seen = new HashSet<GenRoom> { outside };
            var q = new Queue<GenRoom>(); q.Enqueue(outside);
            while (q.Count > 0)
            {
                var a = q.Dequeue();
                if (!edges.TryGetValue(a, out var nb)) continue;
                foreach (var b in nb)
                    if (!(a.Unit >= 0 && b.Unit >= 0 && a.Unit != b.Unit) && seen.Add(b)) q.Enqueue(b);
            }
            foreach (var g in res.Layout.Where(g => !seen.Contains(g))) bad.Add($"{name}: {g} cannot be walked to from outside without going through another flat");
            T("rooms", res.Layout.Count);
            return bad;
        }

        /// <summary>A door cut in a wall of floor 0 where `pick` likes the rooms either side -- for breaking a block of flats
        /// on purpose. True when one was cut.</summary>
        static bool CutDoor(BuildingGenResult res, Func<GenRoom, GenRoom, bool> pick)
        {
            foreach (var w in WallsOn(res, 0).Where(w => w.Thickness < BuildingGen.Ext - 0.01f))
                for (float u = 1.7f; u + 1.7f <= w.Length; u += 0.5f)
                {
                    var c = At(w, u); var n = Normal(w);
                    var a = RoomAt(res, 0, c.X + n.X * 0.6f, c.Z + n.Z * 0.6f); var b = RoomAt(res, 0, c.X - n.X * 0.6f, c.Z - n.Z * 0.6f);
                    if (a == null || b == null || !(pick(a, b) || pick(b, a))) continue;
                    if (w.Openings.Any(o => o.U < u + 1.6f && o.U1 > u - 1.6f)) continue;
                    w.Openings.Add(new WallOpening(u - 1.25f, 0f, 2.5f, 2.85f, 999f, BuildingGen.ArchDoor) { DoorProp = BuildingGen.DefaultDoor });
                    return true;
                }
            return false;
        }

        // BREAK IT (each below, on a fresh building): cut a door through a party wall; cut a second door from the corridor
        // into a flat's bedroom; move the stairs into the flat beside the stairwell; brick up a flat's only door. In the
        // generator: drop MayJoin from Connect's Next (doors between flats, flats entered anywhere); drop OffInAFlat (a
        // bedroom off the kitchen); lay the bay outside the stairwell.
        [Test]
        public void ApartmentsAreFlatsOffASharedCorridor()
        {
            var bad = new List<string>();
            var tally = new Dictionary<string, int>();
            int blocks = 0, broken = 0, single = 0;
            var byFloors = new SortedDictionary<int, int>();
            void Check(BuildingGenSpec spec, BuildingGenResult res)
            {
                blocks++;
                byFloors[res.Floors] = byFloors.GetValueOrDefault(res.Floors) + 1;
                if (res.Plan.Contains("single")) single++;
                var v = FlatViolations(res, Name(spec), tally);
                if (v.Count > 0) { broken++; bad.AddRange(v); }
            }
            foreach (var (spec, res) in All().Where(x => x.Spec.Kind == BuildingKind.Apartment)) Check(spec, res);
            int seeds = ExtraFlatSeeds, shared = blocks - All().Count(x => x.Spec.Kind == BuildingKind.Apartment && x.Spec.Seed >= 1000);
            TestContext.Progress.WriteLine($"[flats] {blocks} blocks of flats ({shared} from the shared sweep, {blocks - shared} from UG_APT_SEEDS={seeds}; by floors {string.Join(", ", byFloors.Select(kv => $"{kv.Value} of {kv.Key}"))}; {single} single-loaded): {string.Join(", ", tally.Where(kv => !kv.Key.StartsWith("frontage ")).OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} {kv.Key}"))}; {broken} blocks with any violation, {bad.Count} violations{(bad.Count > 0 ? ": " + bad[0] : "")}");
            foreach (var g in bad.GroupBy(b => System.Text.RegularExpressions.Regex.Replace(b.Substring(b.IndexOf(':') + 1), @"[\d.,()-]+", "#")).OrderByDescending(g => g.Count()).Take(8))
                TestContext.Progress.WriteLine($"[flats]   {g.Count()} x {g.Key.Trim()} -- e.g. {g.First()}");
            // the flats' frontage, cells along their row: the distribution, min / median / max
            var fronts = tally.Where(kv => kv.Key.StartsWith("frontage ")).SelectMany(kv => Enumerable.Repeat(int.Parse(kv.Key.Substring(9)), kv.Value)).OrderBy(x => x).ToList();
            if (fronts.Count > 0)
                TestContext.Progress.WriteLine($"[flats] frontage of {fronts.Count} flats: min {fronts[0]}, median {fronts[fronts.Count / 2]}, max {fronts[^1]} cells ({string.Join(" ", fronts.GroupBy(x => x).Select(g => $"{g.Key}:{g.Count()}"))})");
            Assert.That(blocks, Is.GreaterThan(100));
            Assert.That(tally.GetValueOrDefault("flats"), Is.GreaterThan(500));
            Assert.That(tally.GetValueOrDefault("lobbies of one storey"), Is.EqualTo(byFloors.GetValueOrDefault(1)), "a block of one storey without its lobby");
            Assert.That(tally.GetValueOrDefault("ways in"), Is.EqualTo(tally.GetValueOrDefault("flats")));
            foreach (var k in new[] { "one-bedroom flats", "two-bedroom flats", "flats with a study", "flats entered by a hall", "flats entered by the living room" })
                Assert.That(tally.GetValueOrDefault(k), Is.GreaterThan(20), $"few {k}");
            Assert.That(single, Is.GreaterThan(10), "never single-loaded");
            Assert.That(byFloors.Keys, Is.SupersetOf(new[] { 1, 2, 3, 4 }));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));

            // the kind by name, as the editor's GEN= field and a save read it, and round a save
            Assert.That(RoomDesignations.ParseBuilding("Apartment"), Is.EqualTo(BuildingKind.Apartment));
            Assert.That(RoomDesignations.ParseBuilding("apartment"), Is.EqualTo(BuildingKind.Apartment));
            var saved = All().First(x => x.Spec.Kind == BuildingKind.Apartment).Res;
            WallSave.Read(WallSave.Write(saved.Walls, saved.Kind, saved.Rooms).Split('\n'), out var savedKind, out List<RoomDesignation> _);
            Assert.That(savedKind, Is.EqualTo(BuildingKind.Apartment), "a block of flats saves as one");

            // the checks have teeth: each way of breaking a block is caught
            // (a block with two flats side by side somewhere on the ground floor, so a party wall to cut)
            bool PartyWall(BuildingGenResult r) => r.Layout.Any(a => a.Floor == 0 && a.Unit >= 0 && r.Layout.Any(b => b.Floor == 0 && b.Unit >= 0 && b.Unit != a.Unit
                && a.Rects.Any(p => b.Rects.Any(q => (p.X1 == q.X0 || q.X1 == p.X0) && Math.Min(p.Z1, q.Z1) - Math.Max(p.Z0, q.Z0) >= 2))));
            int probe = Enumerable.Range(5, 40).First(sd => PartyWall(BuildingGen.Generate(new BuildingGenSpec { Seed = sd, Kind = BuildingKind.Apartment, MinFloors = 2, MaxFloors = 2, MaxRoomsPerFloor = 16 })));
            BuildingGenResult Fresh() => BuildingGen.Generate(new BuildingGenSpec { Seed = probe, Kind = BuildingKind.Apartment, MinFloors = 2, MaxFloors = 2, MaxRoomsPerFloor = 16 });
            var clean = Fresh();
            Assert.That(FlatViolations(clean, "control"), Is.Empty, "the control block");
            Assert.That(clean.Layout.Count(g => g.Unit >= 0 && g.Floor == 0 && g.Entry), Is.GreaterThanOrEqualTo(2), "the control has two flats a floor");
            void Broken(string what, Func<BuildingGenResult, bool> breakIt, string expect)
            {
                var r = Fresh();
                Assert.That(breakIt(r), Is.True, $"{what}: nothing to break");
                var v = FlatViolations(r, what);
                TestContext.Progress.WriteLine($"[flats] break it: {what} -> {v.Count} violations{(v.Count > 0 ? ": " + v[0] : "")}");
                Assert.That(v.Any(x => x.Contains(expect)), Is.True, $"{what} was not caught: {string.Join("; ", v)}");
            }
            Broken("a door through a party wall", r => CutDoor(r, (a, b) => a.Unit >= 0 && b.Unit >= 0 && a.Unit != b.Unit), "a door between flat");
            Broken("a second door into a flat", r => CutDoor(r, (a, b) => a.Unit < 0 && a.Kind == RoomKind.Hall && b.Kind == RoomKind.Bedroom), "doors to shared space");
            Broken("the stairs moved into a flat", r =>
            {
                var well = r.Layout.First(g => g.Kind == RoomKind.Stairwell);
                float dx = (well.X0 > 0 ? -1 : 1) * (well.X1 - well.X0) * Cell;
                for (int i = 0; i < r.Stairs.Count; i++) { var s = r.Stairs[i]; s.X += dx; r.Stairs[i] = s; }
                return r.Stairs.Count > 0;
            }, "flight");
            Broken("a flat's only door bricked up", r =>
            {
                foreach (var w in WallsOn(r, 0))
                    for (int i = w.Openings.Count - 1; i >= 0; i--)
                    {
                        var o = w.Openings[i];
                        if (!Walkable(o)) continue;
                        var c = At(w, o.U + o.Width * 0.5f); var nn = Normal(w);
                        var a = RoomAt(r, 0, c.X + nn.X * 0.6f, c.Z + nn.Z * 0.6f); var b = RoomAt(r, 0, c.X - nn.X * 0.6f, c.Z - nn.Z * 0.6f);
                        if (a != null && b != null && (a.Unit < 0) != (b.Unit < 0) && (a.Unit == 0 || b.Unit == 0)) { w.Openings.RemoveAt(i); return true; }
                    }
                return false;
            }, "cannot be walked to");
        }

        // S: a block of flats on a town plot (FitPlot: width x depth a cap). The town's apartments zone is 8x7 cells (cow
        // tools' plots); a block there needs a corridor, a stairwell, two flats a floor at the least and two to four floors.
        // Each zone is built in full over the seeds and held to FlatViolations; a block that fits says "fitted the", one that
        // cannot says "could not fit" and is the closest -- never silently oversize. The smallest zones that hold one are
        // pinned, so a change that loses them fails here. UG_APT_PLOT_SEEDS widens it (60 for the report).
        // BREAK IT: drop the compact rung of the fit ladder (11x9 stops fitting); keep the landing column on a plot (11x9
        // stops fitting); let a fit pass the depth cap (a double-loaded block "fits" 8 deep).
        [Test]
        public void ApartmentsFitTownPlots()
        {
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("UG_APT_PLOT_SEEDS"), out var ps) ? ps : 16;
            var zones = new (string Name, int W, int D)[] { ("the apartments zone", 8, 7), ("smallest double-loaded", 11, 9), ("smallest single-loaded", 19, 5), ("roomier", 16, 11) };
            var bad = new List<string>();
            foreach (var z in zones)
            {
                int fits = 0, valid = 0, closest = 0, shortOf = 0;
                var gave = new Dictionary<string, int>();
                var sizes = new Dictionary<string, int>();
                for (int seed = 0; seed < seeds; seed++)
                {
                    var spec = new BuildingGenSpec { Seed = seed, Kind = BuildingKind.Apartment, MinFloors = 2, MaxFloors = 4, MaxRoomsPerFloor = 16, Width = z.W, Depth = z.D, FitPlot = true };
                    var r = BuildingGen.Generate(spec);
                    string n = $"{z.Name} {z.W}x{z.D} seed {seed}";
                    bool fit = r.CellsX <= z.W && r.CellsZ <= z.D;
                    var note = r.Notes.LastOrDefault(x => x.StartsWith("fitted the") || x.StartsWith("could not fit"));
                    sizes[$"{r.CellsX}x{r.CellsZ}"] = sizes.GetValueOrDefault($"{r.CellsX}x{r.CellsZ}") + 1;
                    if (fit) { fits++; if (note == null || !note.StartsWith("fitted the")) bad.Add($"{n}: fits as {r.CellsX}x{r.CellsZ} but says {note ?? "nothing"}"); }
                    else { closest++; if (note == null || !note.StartsWith("could not fit")) bad.Add($"{n}: {r.CellsX}x{r.CellsZ}, over the plot, and says {note ?? "nothing"}"); }
                    if (note != null && note.Contains("but short of")) shortOf++;
                    if (note != null && note.IndexOf(':') is int c && c > 0 && note.StartsWith("fitted the"))
                        foreach (var g in note.Substring(c + 1).Split(',').Select(x => x.Trim()).Where(x => !x.StartsWith("but") && x.Length > 0 && !x.Contains(':')))
                            gave[g] = gave.GetValueOrDefault(g) + 1;
                    int flats = r.Layout.Where(g => g.Unit >= 0 && g.Floor == 0).Select(g => g.Unit).Distinct().Count();
                    var v = FlatViolations(r, n);
                    bool ok = v.Count == 0 && r.Floors >= 2 && r.Floors <= 4 && flats >= 2 && r.Layout.Any(g => g.Kind == RoomKind.Stairwell);
                    if (ok) valid++; else bad.Add($"{n}: {r.Floors} floors, {flats} flats a floor; {string.Join("; ", v.Take(2))}");
                }
                TestContext.Progress.WriteLine($"[flats-plot] {z.Name} {z.W}x{z.D}: {fits} of {seeds} fit, {closest} the closest instead ({string.Join(", ", sizes.Select(kv => $"{kv.Value} at {kv.Key}"))}); {valid} of {seeds} valid blocks (corridor, stairwell, 2+ flats a floor, 2-4 floors, every flat check); {shortOf} short of furniture; gave up {string.Join(", ", gave.Select(kv => $"{kv.Value} x {kv.Key}"))}");
                if (z.W >= 11 && z.D >= 9 || z.W >= 19) Assert.That(fits, Is.EqualTo(seeds), $"{z.Name} {z.W}x{z.D}: not every block fits");
            }
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // R: an L or a T asked for keeps its outline with floors above (strawberry asked for the shapes; "one storey only"
        // made every L or T of two floors a rectangle, with a note). The floors above stand on exactly the wing's columns,
        // the full depth; the full-length row runs on past them one storey -- one arm for an L, one each side for a T, two
        // cells at the least -- and each arm has a roof of its own at the ground floor's head, a shed for a house and a flat
        // slab for a business. RoofsSuitTheKindAndCoverEverything and the rest of the sweep check those roofs and the walls
        // round them; this pins that the shape is kept at all.
        // BREAK IT: make floors > 1 a rectangle again; stand the floor above on the full-length row instead; give an arm no
        // roof (Lower with the arm's depth left out).
        [Test]
        public void ForcedLAndTKeepTheirOutlineWithFloorsAbove()
        {
            var bad = new List<string>();
            var kept = new Dictionary<FootprintShape, (int Kept, int Asked)>();
            int arms = 0, roofed = 0;
            foreach (BuildingKind kind in Enum.GetValues(typeof(BuildingKind)))
                if (kind != BuildingKind.Apartment)   // (a block of flats is a rectangle by design, and says so in a note)
                foreach (var shape in new[] { FootprintShape.L, FootprintShape.T })
                    for (int seed = 0; seed < 12; seed++)
                        foreach (int fl in new[] { 2, 3 })
                        {
                            var spec = new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 3 * fl + seed % 3, MinFloors = fl, MaxFloors = fl, MaxRoomsPerFloor = 5, Shape = shape };
                            var r = BuildingGen.Generate(spec);
                            string n = $"{Name(spec)} {shape}";
                            var k = kept.GetValueOrDefault(shape); k.Asked++;
                            if (r.Floors != fl) { bad.Add($"{n}: {r.Floors} floors"); kept[shape] = k; continue; }
                            if (r.Shape != shape) { bad.Add($"{n} came out {r.Shape}: {string.Join("; ", r.Notes)}"); kept[shape] = k; continue; }
                            k.Kept++; kept[shape] = k;
                            var (wx0, wz0, wx1, wz1) = r.Wing;
                            for (int f = 1; f < r.Floors; f++)
                                if (r.Footprints[f].X0 < wx0 || r.Footprints[f].X1 > wx1) bad.Add($"{n}: floor {f} [{r.Footprints[f].X0},{r.Footprints[f].X1}) is past the wing's columns [{wx0},{wx1})");
                            if (r.Footprints[1] != (wx0, wx1)) bad.Add($"{n}: floor 1 is [{r.Footprints[1].X0},{r.Footprints[1].X1}), not the wing's columns [{wx0},{wx1})");
                            int west = wx0, east = r.CellsX - wx1;
                            if (shape == FootprintShape.T ? west < 2 || east < 2 : Math.Min(west, east) != 0 || Math.Max(west, east) < 2) bad.Add($"{n}: arms of {west} and {east} cells");
                            if (r.Notes.Any(x => x.Contains("rectangle") || x.Contains("one storey"))) bad.Add($"{n}: {string.Join("; ", r.Notes)}");
                            // each arm has its own roof at the ground floor's head, over the middle of it
                            bool wingNorth = wz1 == r.CellsZ;   // turned half round, the wing is on the south
                            foreach (var (a0, a1) in new[] { (0, wx0), (wx1, r.CellsX) }.Where(a => a.Item2 > a.Item1))
                            {
                                arms++;
                                float px = (a0 + a1) * 0.5f * Cell, pz = (wingNorth ? wz0 * 0.5f : (wz1 + r.CellsZ) * 0.5f) * Cell;
                                bool roof = r.Walls.Any(w => w.Kind == SurfaceKind.Roof && (r.Roof == RoofKind.Flat
                                    ? MathF.Abs(w.Pitch + 90f) < 0.5f && MathF.Abs(w.Y - Storey - BuildingGen.Slab * 0.5f) < 0.05f && px > w.X && px < w.X + w.Length && pz > w.Z - w.Height && pz < w.Z
                                    : MathF.Abs(w.Pitch + 90f) > 0.5f && MathF.Abs(w.Y - Storey) < 0.05f && !float.IsNaN(RoofHeightAt(w, px, pz))));
                                if (roof) roofed++; else bad.Add($"{n}: the arm [{a0},{a1}) has no roof of its own over ({px:0.0},{pz:0.0})");
                            }
                        }
            TestContext.Progress.WriteLine($"[gen-ltup] {string.Join(", ", kept.Select(kv => $"{kv.Value.Kept} of {kv.Value.Asked} {kv.Key}s"))} of 2-3 floors kept their outline; {roofed} of {arms} arms roofed at the ground floor's head; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(kept[FootprintShape.L].Asked, Is.EqualTo(96));
            Assert.That(arms, Is.GreaterThan(150));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // T: a town house eats in its kitchen when it has no dining room -- at a table with a chair at it (strawberry: "houses
        // without a dining room require at table with at least one chair in the kitchen") -- and cooks at a counter beside
        // its sink or stove ("require counters - not just sinks in kitchens"), with no tolerance: over the town plots'
        // building zones a house is fitted to (cow tools: small 4x6, medium 5x6, large 7x7, the front to the street), where
        // the rooms come out at their smallest. A fit that says it came out short of one is excused, counted and bounded.
        // BREAK IT: seat a kitchen-diner's table on its open sides only, keeping it with no chair; take the counters out of
        // the kitchen's last-resort layout.
        [Test]
        public void TownHousesEatAndCookInTheirKitchens()
        {
            var bad = new List<string>();
            int kitchens = 0, eatIns = 0, excused = 0, whole = 0, houses = 0;
            foreach (var (w, d) in new[] { (4, 6), (5, 6), (7, 7) })
                for (int seed = 1; seed <= 30; seed++)
                {
                    var r = BuildingGen.Generate(new BuildingGenSpec { Kind = BuildingKind.Residential, Seed = seed, MinRooms = 5, MaxFloors = 2, MaxRoomsPerFloor = 6,
                                                                        Width = w, Depth = d, FitPlot = true, FrontSide = Sides.South });
                    houses++;
                    var note = r.Notes.LastOrDefault(x => x.StartsWith("fitted the")) ?? "";
                    if (!note.Contains("but")) whole++;
                    string n = $"{w}x{d} seed {seed}";
                    bool eats = r.KitchenDiner || !r.Layout.Any(g => g.Kind == RoomKind.DiningRoom);
                    foreach (var k in r.Layout.Where(g => g.Kind == RoomKind.Kitchen))
                    {
                        kitchens++;
                        var mine = r.Furniture.Where(m => m.Room == k && m.On == null && !m.Mounted && !m.Ceiling).ToList();
                        bool SameWay(FurniturePlan a, FurniturePlan b) => MathF.Abs(((a.Yaw - b.Yaw) % 360f + 360f) % 360f) < 1f;
                        bool worktop = mine.Any(c => c.Name == "Counter_0" && mine.Any(h => (h.Name == "Counter_1" || h.Name.StartsWith("Oven")) && SameWay(c, h)
                                                                                               && Overlap(Grown(h.Footprint, 0.15f), c.Footprint, 0f)));
                        if (!worktop) { if (note.Contains("Kitchen: counter")) excused++; else bad.Add($"{n}: {k} has no plain counter beside its sink or stove"); }
                        if (!eats) continue;
                        eatIns++;
                        var tables = mine.Where(t => t.Name is "Table_Wood_0" or "Table_Wood_1" or "Table_Wood_3").ToList();
                        bool seated = tables.Any(t => mine.Any(c => c.Name.StartsWith("Chair") && MathF.Sqrt((t.X - c.X) * (t.X - c.X) + (t.Z - c.Z) * (t.Z - c.Z)) < 3f));
                        if (!seated) { if (note.Contains("Kitchen: dining table")) excused++; else bad.Add($"{n}: {k} is where the house eats, with {(tables.Count == 0 ? "no table" : "a table and no chair")}"); }
                    }
                }
            TestContext.Progress.WriteLine($"[gen-eat] {houses} town houses ({whole} fitted whole), {kitchens} kitchens, {eatIns} where the house eats; {excused} short of a counter or a seated table in fits that say so; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(eatIns, Is.GreaterThan(40), "too few kitchens where the house eats to test");
            Assert.That(excused, Is.LessThanOrEqualTo(houses / 20), "town houses often come out short of a kitchen's counter or table");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // S: a storey's floor stops inside its outside walls, and a porch's deck is the house's floor colour (strawberry: "floor
        // color is adding a floor colored strip to the exterior walls", "give porches floor color"). A slab laid out to a
        // wall's outer face has its edge flush with that face -- and a surface's edges wear its top's colour (WallSurface),
        // so the floor's colour showed as a band round the outside between storeys and at the base. No floor slab's edge
        // stands within 2 cm of the outer face of an outside wall of the storey on it or under it. Every porch's deck wears
        // the palette's floor texel.
        // BREAK IT: lay the slabs out to the outer faces (SlabInset 0); leave a deck the wall colour.
        [Test]
        public void FloorsStopInsideTheirWallsAndPorchesWearTheFloor()
        {
            var bad = new List<string>();
            int slabs = 0, faces = 0, decks = 0;
            foreach (var (spec, res) in All())
            {
                var outer = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && w.Thickness == BuildingGen.Ext).ToList();
                int floorTexel = BuildingGen.FloorTexels.TryGetValue(res.Palette ?? "", out var ft) ? ft.Floor : -1;
                foreach (var sl in res.Walls.Where(w => w.Kind == SurfaceKind.Floor && MathF.Abs(w.Thickness - BuildingGen.Slab) < 0.01f))
                {
                    var pl = Plan(sl);
                    int f = (int)MathF.Round((sl.Y + sl.Thickness * 0.5f) / Storey);
                    float cx = (pl.X0 + pl.X1) * 0.5f, cz = (pl.Z0 + pl.Z1) * 0.5f;
                    if (res.Porches.Any(p => cx > p.X0 && cx < p.X1 && cz > p.Z0 && cz < p.Z1))
                    {
                        decks++;
                        if (floorTexel >= 0 && sl.Texel != floorTexel) bad.Add($"{Name(spec)}: a porch deck in texel {sl.Texel}, the floor's is {floorTexel}");
                        continue;
                    }
                    slabs++;
                    foreach (var w in outer.Where(w => FloorOf(w) == f || FloorOf(w) == f - 1))
                    {
                        if (!Overlap(pl, Box(w), 0.01f)) continue;
                        faces++;
                        // how far the slab runs the way the wall looks out, against where its outer face stands
                        var n = Normal(w);
                        float face = w.X * n.X + w.Z * n.Z + w.Thickness * 0.5f;
                        float reach = n.X > 0.5f ? pl.X1 : n.X < -0.5f ? -pl.X0 : n.Z > 0.5f ? pl.Z1 : -pl.Z0;
                        if (MathF.Abs(reach - face) < 0.02f) bad.Add($"{Name(spec)}: floor {f}'s slab runs out flush with the {FaceOf(w)} wall's outer face");
                    }
                }
            }
            TestContext.Progress.WriteLine($"[gen-floorband] {slabs} floor slabs against {faces} outside walls over or under them, {decks} porch decks; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(faces, Is.GreaterThan(2000));
            Assert.That(decks, Is.GreaterThan(100));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
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
            string text = WallSave.Write(res.Walls, res.Kind, res.Rooms, res.DoorMarks, res.Props);
            var walls = WallSave.Read(text.Split('\n'), out var kind, out var rooms, out var marks, out var props);
            // (the props, then the curtains and rugs: RuntimeDecorSurvivesASave holds those)
            Assert.That(props.Count, Is.EqualTo(res.Furniture.Count + res.RuntimeDecor.Count + res.HvacVents.Count + res.Electrical.Count).And.GreaterThan(0), "the furniture survives the save");
            Assert.That(props.Zip(res.Furniture).All(t => t.First.Name == t.Second.Name && MathF.Abs(t.First.X - t.Second.X) < 0.01f && MathF.Abs(t.First.Y - t.Second.Y) < 0.01f
                                                           && MathF.Abs(t.First.Z - t.Second.Z) < 0.01f && MathF.Abs(t.First.Yaw - t.Second.Yaw) < 0.01f), "each piece as it was");
            Assert.That(marks.Select(m => (m.Role, m.X, m.Floor)), Is.EqualTo(res.DoorMarks.Select(m => (m.Role, MathF.Round(m.X * 1000f) / 1000f, m.Floor))).Using<(DoorRole, float, int)>((a, b) => a.Item1 == b.Item1 && MathF.Abs(a.Item2 - b.Item2) < 0.01f && a.Item3 == b.Item3),
                        "the door marks survive the save");
            Assert.That(marks.Count, Is.GreaterThan(0));
            Assert.That(kind, Is.EqualTo(res.Kind));
            Assert.That(walls.Count, Is.EqualTo(res.Walls.Count));
            Assert.That(rooms.Select(r => (r.Kind, r.Floor)), Is.EqualTo(res.Rooms.Select(r => (r.Kind, r.Floor))));
            Assert.That(rooms.Max(r => r.Floor), Is.GreaterThan(0), "the case must have upper floors to test anything");
            var ground = text.Split('\n').First(l => l.StartsWith("room "));
            // (an HVAC room's vents follow, keyed, and its switch and outlets: HvacRecordSurvivesASave, ElectricalFixturesFollowTheRules)
            Assert.That(ground.Split(new[] { " hvac ", " elec " }, StringSplitOptions.None)[0].Split(' ').Length, Is.EqualTo(4), "a ground-floor designation keeps the old three-field form: " + ground);
        }
    }
}
