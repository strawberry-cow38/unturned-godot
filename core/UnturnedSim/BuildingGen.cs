using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>What to generate: a building from a seed (strawberry 2026-10-11: "make the map editor buildings tool
    /// procgen. to make buildings from a seed. certain min number of rooms, min number of each type, max number per floor,
    /// max number of floors, consider the building's type, what colors it should use. which openings are relevant, which
    /// rooms prefer 2nd+ floor, which rooms prefer not to be 2nd floor etc etc").</summary>
    public sealed class BuildingGenSpec
    {
        public int Seed;
        public BuildingKind Kind = BuildingKind.Residential;
        /// <summary>At least this many ROOMS. A floor's corridor (Hall) and its stairwell are circulation and do not count.</summary>
        public int MinRooms;
        public int MinFloors = 1, MaxFloors = 2;
        /// <summary>At most this many rooms on any one floor (circulation not counted).</summary>
        public int MaxRoomsPerFloor = 6;
        /// <summary>At least this many of each kind, on top of what the building's kind always has.</summary>
        public Dictionary<RoomKind, int> MinOfKind = new Dictionary<RoomKind, int>();
    }

    /// <summary>One generated room, on the 3 m lattice: cells [X0, X1) x [Z0, Z1) of its floor.</summary>
    public sealed class GenRoom
    {
        public RoomKind Kind;
        public int Floor, X0, Z0, X1, Z1;
        public int Cells => (X1 - X0) * (Z1 - Z0);
        public bool Circulation => Kind == RoomKind.Hall || Kind == RoomKind.Stairwell;
        public override string ToString() => $"{Kind}@{Floor} [{X0},{Z0}..{X1},{Z1}]";
    }

    /// <summary>A flight of stairs as the editor lays one (EditorBuildings.AddStairs): its foot corner, the way it climbs
    /// (yaw; it recedes along local -Z and is Width wide along local +X), its run, from storey Floor to the next.</summary>
    public struct StairPlan
    {
        public float X, Y, Z, Yaw, Run, Width; public int Floor;
        /// <summary>Unit vectors in plan: the way it climbs, and the way its width runs (AddStairs' back and right).</summary>
        public (float X, float Z) Back => (-MathF.Sin(Yaw * MathF.PI / 180f), -MathF.Cos(Yaw * MathF.PI / 180f));
        public (float X, float Z) Right => (MathF.Cos(Yaw * MathF.PI / 180f), -MathF.Sin(Yaw * MathF.PI / 180f));
    }

    public sealed class BuildingGenResult
    {
        public BuildingKind Kind;
        public int Floors, CellsX, CellsZ;
        /// <summary>The retail palette its walls wear (content/wall_palettes.tsv, by NAME: the editor resolves the index).</summary>
        public string Palette;
        /// <summary>Walls with their openings, foundations, a floor per storey and the roof, in building-local metres:
        /// the outside walls' mid-planes enclose (0, 0)..(CellsX, CellsZ) x LatticeStep, and the ground floor is y 0.</summary>
        public List<WallPlan> Walls = new List<WallPlan>();
        public List<StairPlan> Stairs = new List<StairPlan>();
        public List<RoomDesignation> Rooms = new List<RoomDesignation>();
        public List<GenRoom> Layout = new List<GenRoom>();
        /// <summary>Where it could not do what was asked, said so (a minimum that did not fit, a limit it broke).</summary>
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// SEEDED BUILDINGS. The editor's own data, generated: walls with openings, a floor slab per storey, a flat roof,
    /// stairs, and a RoomDesignation for every room -- so a generated building is edited exactly like a drawn one.
    ///
    /// The plan is a double-loaded corridor, the way retail's apartments and offices are laid: on every floor a 3 m HALL
    /// runs the building's length with rooms on both sides, each with its door onto it, the front door in the hall's
    /// west end and a back door in its east end. With more than one floor a STAIRWELL stands at the west end of the
    /// north side, the same cells on every floor: its row by the hall is a landing kept clear on every floor, and the
    /// flights climb in the two rows behind it by turns -- east up one, west up the other -- so each arrives on a
    /// landing beside the foot of the next. (The first version put the flights in the row by the hall, and the door
    /// from the hall opened onto the bottom four steps.)
    ///
    /// The ROOM PROGRAMME comes from the building's kind (a house has a kitchen, a living room, a bathroom and a
    /// bedroom; a shop has its shop floor and a store room), raised to the spec's minimums and filled up to MinRooms with
    /// that kind's optional rooms. Rooms go on floors by preference: some only on the ground (garage, shop floor,
    /// warehouse, workshop), some rather on the ground (kitchen, living and dining rooms), some rather upstairs
    /// (bedrooms, closets, a business's offices), and the rest spread so a house's bathrooms are not all on one floor.
    ///
    /// Everything is on the editor's 3 m lattice, a storey is the wall height (EditorBuildings.StoreyHeight), the
    /// outside walls are corner-solved already (each runs on to its neighbour's outer face, which is what the editor's
    /// SolveCorners would do -- but that moves a wall's origin without moving its openings, so it must find nothing to
    /// do here), and the openings are the editor's archetypes at their measured sizes.
    /// </summary>
    public static class BuildingGen
    {
        public const float Cell = WallOpenings.LatticeStep;
        /// <summary>EditorBuildings.StoreyHeight: a storey is the wall height (retail WALL_HEIGHT).</summary>
        public const float Storey = WallOpenings.DoorHeight;
        public const float Slab = 0.5f;   // EditorBuildings.SlabThickness
        /// <summary>No opening reaches higher than this: the floor above occupies the top Slab of every wall.</summary>
        public const float OpeningTop = Storey - Slab;
        public const float Ext = WallOpenings.DefaultThickness, Inner = WallOpenings.InteriorThickness;
        /// <summary>The stairwell's length along the hall, in cells: a 6 m flight with a landing at each end.</summary>
        public const int StairCells = 4;

        // the editor's opening archetypes (EditorBuildings.Archetypes): index and measured size
        public const int ArchDoor = 0, ArchWindow = 1, ArchTallWindow = 2, ArchGarage = 3, ArchPorch = 4, ArchVent = 5;
        public static readonly (float W, float H, float Sill, bool Glazed)[] ArchSizes =
        {
            (2.5f, 2.85f, 0f, false), (3.31f, WallOpenings.WindowHeight, WallOpenings.WindowSill, true),
            (2.81f, 2.97f, 0.88f, true), (4.05f, 3.80f, 0f, false), (5.5f, WallOpenings.DoorHeight, 0f, false),
            (1.0f, 1.0f, 2.5f, false),
        };
        public const string DefaultDoor = "Door_Pine";
        /// <summary>A window squeezed into a short wall gets no narrower than this; below it the wall goes blank.</summary>
        public const float MinWindow = 1.5f;

        /// <summary>Where a kind of room wants to be.</summary>
        public enum FloorPref { GroundOnly, Ground, Any, Upper }

        /// <summary>A room kind's needs: its area in cells, the narrowest it may be along the hall, which floor it wants,
        /// and what goes in its outside walls (-1: nothing).</summary>
        public struct Trait
        {
            public int Cells, MinSide; public FloorPref Pref; public int Window;
            public Trait(int cells, int minSide, FloorPref pref, int window) { Cells = cells; MinSide = minSide; Pref = pref; Window = window; }
        }

        // MinSide is 2 for everything: a one-cell room at the end of a side has 2.4 m between its walls' faces and a
        // door is 2.5 (the leaf is 2.45).
        public static Trait TraitOf(RoomKind k, BuildingKind b) => k switch
        {
            RoomKind.Bedroom => new Trait(9, 3, FloorPref.Upper, ArchWindow),
            RoomKind.Bathroom => new Trait(5, 2, FloorPref.Any, ArchVent),          // a small high window, not a view
            RoomKind.Kitchen => new Trait(9, 3, FloorPref.Ground, ArchWindow),
            RoomKind.LivingRoom => new Trait(12, 3, FloorPref.Ground, ArchWindow),
            RoomKind.DiningRoom => new Trait(9, 3, FloorPref.Ground, ArchWindow),
            RoomKind.Closet => new Trait(4, 2, FloorPref.Upper, -1),
            RoomKind.Garage => new Trait(12, 3, FloorPref.GroundOnly, ArchGarage),
            RoomKind.Workshop => new Trait(16, 4, FloorPref.GroundOnly, ArchWindow),
            RoomKind.Storage => new Trait(5, 2, FloorPref.Any, -1),
            RoomKind.Office => new Trait(9, 3, b == BuildingKind.Residential ? FloorPref.Any : FloorPref.Upper, ArchWindow),
            RoomKind.Shop => new Trait(20, 4, FloorPref.GroundOnly, ArchTallWindow),
            RoomKind.Warehouse => new Trait(32, 6, FloorPref.GroundOnly, ArchGarage),
            _ => new Trait(6, 2, FloorPref.Any, ArchWindow),
        };

        /// <summary>Rooms that need daylight: each gets at least one glazed opening in an outside wall.</summary>
        public static bool Habitable(RoomKind k) => k is RoomKind.Bedroom or RoomKind.Kitchen or RoomKind.LivingRoom
            or RoomKind.DiningRoom or RoomKind.Office or RoomKind.Shop or RoomKind.Workshop;

        /// <summary>Rooms a building has only one of unless the spec asks for more.</summary>
        public static bool OnlyOne(RoomKind k, BuildingKind b) => k == RoomKind.Kitchen
            || (b == BuildingKind.Residential && (k == RoomKind.LivingRoom || k == RoomKind.DiningRoom));

        /// <summary>Small rooms that should stay small: they grow by a cell at most when a floor has length to spare.</summary>
        public static bool Service(RoomKind k) => k is RoomKind.Bathroom or RoomKind.Closet or RoomKind.Storage;

        /// <summary>What a kind of building always has, and what it adds (in this order, round again if need be) to reach MinRooms.</summary>
        public static (RoomKind[] Always, RoomKind[] More) Programme(BuildingKind b) => b switch
        {
            BuildingKind.Residential => (new[] { RoomKind.LivingRoom, RoomKind.Kitchen, RoomKind.Bathroom, RoomKind.Bedroom },
                                         new[] { RoomKind.Bedroom, RoomKind.DiningRoom, RoomKind.Bathroom, RoomKind.Closet, RoomKind.Bedroom, RoomKind.Garage, RoomKind.Storage }),
            BuildingKind.Commercial => (new[] { RoomKind.Shop, RoomKind.Storage, RoomKind.Bathroom },
                                        new[] { RoomKind.Office, RoomKind.Storage, RoomKind.Kitchen, RoomKind.Office, RoomKind.Bathroom }),
            BuildingKind.Industrial => (new[] { RoomKind.Warehouse, RoomKind.Workshop, RoomKind.Office, RoomKind.Bathroom },
                                        new[] { RoomKind.Storage, RoomKind.Office, RoomKind.Garage, RoomKind.Storage }),
            _ => (new[] { RoomKind.Office, RoomKind.Bathroom, RoomKind.Storage },
                  new[] { RoomKind.Office, RoomKind.Kitchen, RoomKind.Storage, RoomKind.Office }),
        };

        /// <summary>The retail palettes that suit a kind of building (content/wall_palettes.tsv names).</summary>
        public static string[] PalettesFor(BuildingKind b) => b switch
        {
            BuildingKind.Residential => new[] { "House_00", "House_01", "House_02", "House_03", "House_04", "House_05", "House_06", "House_07", "House_08", "House_09", "Apartment_0", "Apartment_1", "Apartment_2", "Apartment_3" },
            BuildingKind.Commercial => new[] { "Cafe_0", "Cafe_1", "Clothes_0", "Clothes_1", "Diner_0", "Diner_1", "Diner_2", "Grocer_0", "Guns_0", "Hardware_0", "Post_0", "Bank_0", "Mall_0", "Library_0" },
            BuildingKind.Industrial => new[] { "Barn_0", "Barn_1", "Mechanic_0", "Fire_0", "Drill", "Research_0" },
            _ => new[] { "Office_0", "Office_1", "Office_2", "Office_3", "Police_0", "Police_1", "Medic_0", "Medic_1", "School_0", "School_1", "Barracks_0" },
        };

        /// <summary>A small, stable RNG (xorshift64*): the same seed builds the same building on every machine and runtime,
        /// which System.Random does not promise.</summary>
        sealed class Rng
        {
            ulong _s;
            public Rng(int seed) { _s = 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)seed * 0xBF58476D1CE4E5B9UL); if (_s == 0) _s = 1; Next(); }
            public ulong Next() { _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27; return _s * 0x2545F4914F6CDD1DUL; }
            public int Int(int n) => n <= 0 ? 0 : (int)(Next() % (ulong)n);
            public void Shuffle<T>(IList<T> list) { for (int i = list.Count - 1; i > 0; i--) { int j = Int(i + 1); (list[i], list[j]) = (list[j], list[i]); } }
        }

        public static BuildingGenResult Generate(BuildingGenSpec spec)
        {
            var rng = new Rng(spec.Seed);
            var kind = spec.Kind;
            var res = new BuildingGenResult { Kind = kind };
            var pals = PalettesFor(kind);
            res.Palette = pals[rng.Int(pals.Length)];
            int maxFloors = Math.Max(1, spec.MaxFloors), minFloors = Math.Clamp(spec.MinFloors, 1, maxFloors);
            int perFloor = Math.Max(1, spec.MaxRoomsPerFloor), capacity = maxFloors * perFloor;
            Trait T(RoomKind r) => TraitOf(r, kind);

            // ---- the programme: the kind's own rooms, raised to the minimums, filled to MinRooms, and a seeded extra
            var (always, more) = Programme(kind);
            var rooms = new List<RoomKind>(always);
            foreach (var kv in spec.MinOfKind.OrderBy(kv => (int)kv.Key))
                if (kv.Key != RoomKind.Unassigned && kv.Key != RoomKind.Hall && kv.Key != RoomKind.Stairwell)
                    for (int have = rooms.Count(r => r == kv.Key); have < kv.Value; have++) rooms.Add(kv.Key);
            // a filler that can only go on the ground is skipped once the ground floor has no space for it: the
            // spec asked for rooms, not for that one, and it would break MaxRoomsPerFloor. (Every kind's list has a
            // room that can go anywhere, so this always finds one.)
            // ...and one a building has only one of (a house's kitchen, living and dining rooms) is skipped once it has
            // it -- the first plans had two dining rooms. The spec's own minimums still get as many as they ask for.
            bool Fits(RoomKind r) => (T(r).Pref != FloorPref.GroundOnly || rooms.Count(x => T(x).Pref == FloorPref.GroundOnly) < perFloor)
                                     && !(OnlyOne(r, kind) && rooms.Contains(r));
            for (int i = 0; rooms.Count < spec.MinRooms; i++) if (Fits(more[i % more.Length])) rooms.Add(more[i % more.Length]);
            if (rng.Int(2) == 0 && rooms.Count < capacity && more[rng.Int(more.Length)] is var extra && Fits(extra)) rooms.Add(extra);
            if (rooms.Count > capacity)
            {
                // the dropped ones are off the END: fillers first, then the spec's own minimums, never the kind's core
                res.Notes.Add($"{rooms.Count} rooms asked for and at most {capacity} fit ({maxFloors} floors of {perFloor}): dropped {string.Join(", ", rooms.Skip(capacity))}");
                rooms = rooms.Take(capacity).ToList();
            }

            // ---- floors: as few as hold them and at least MinFloors -- and a second when anything would rather be upstairs
            int floors = Math.Max(minFloors, (rooms.Count + perFloor - 1) / perFloor);
            if (rooms.Count >= 4 && rooms.Any(r => T(r).Pref == FloorPref.Upper)) floors = Math.Max(floors, 2);
            floors = Math.Min(floors, maxFloors);
            res.Floors = floors;

            // ---- each room to a floor: ground-only first, then ground-preferring, upstairs-preferring, the rest
            var onFloor = new List<RoomKind>[floors];
            for (int f = 0; f < floors; f++) onFloor[f] = new List<RoomKind>();
            int Rank(FloorPref p) => p switch { FloorPref.GroundOnly => 0, FloorPref.Ground => 1, FloorPref.Upper => 2, _ => 3 };
            // the emptiest floor among these with room left (same-kind count first, so a house's bathrooms spread), or -1
            int Pick(IEnumerable<int> fs, RoomKind r)
            {
                int best = -1;
                foreach (int f in fs)
                {
                    if (onFloor[f].Count >= perFloor) continue;
                    if (best < 0) { best = f; continue; }
                    int a = onFloor[f].Count(x => x == r), b = onFloor[best].Count(x => x == r);
                    if (a < b || (a == b && onFloor[f].Count < onFloor[best].Count)) best = f;
                }
                return best;
            }
            var upper = Enumerable.Range(1, floors - 1);
            var all = Enumerable.Range(0, floors);
            foreach (var r in rooms.OrderBy(r => Rank(T(r).Pref)).ThenByDescending(r => T(r).Cells).ThenBy(r => (int)r))
            {
                int at = T(r).Pref switch
                {
                    FloorPref.GroundOnly => 0,
                    FloorPref.Ground => onFloor[0].Count < perFloor ? 0 : Pick(upper, r),
                    FloorPref.Upper => Pick(upper, r) is int u && u >= 0 ? u : Pick(all, r),
                    _ => Pick(all, r),
                };
                if (at < 0 || onFloor[at].Count >= perFloor)
                {
                    at = 0;
                    res.Notes.Add($"{r} put on the ground floor over its limit of {perFloor} (every floor was full{(T(r).Pref == FloorPref.GroundOnly ? ", and it can only go on the ground" : "")})");
                }
                onFloor[at].Add(r);
            }
            // an upper floor with nothing on it would be stairs to an empty corridor: give it the most upstairs-minded
            // room the ground floor can spare
            for (int f = 1; f < floors; f++)
                if (onFloor[f].Count == 0)
                {
                    var spare = onFloor[0].Where(r => T(r).Pref != FloorPref.GroundOnly).OrderBy(r => Rank(T(r).Pref) == 2 ? 0 : Rank(T(r).Pref) == 3 ? 1 : 2).FirstOrDefault();
                    if (spare != RoomKind.Unassigned && onFloor[0].Count > 1) { onFloor[0].Remove(spare); onFloor[f].Add(spare); }
                    else res.Notes.Add($"floor {f} has no room on it: nothing on the ground floor could move up");
                }

            // ---- the plan: each floor's rooms to a side of the hall, and a width along it
            int depth = kind == BuildingKind.Industrial ? 4 : 3;   // cells either side of the hall
            bool stairs = floors > 1;
            int stairCells = stairs ? StairCells : 0;
            int Width(RoomKind r) => Math.Max(Math.Max(2, T(r).MinSide), (int)Math.Ceiling(T(r).Cells / (double)depth));
            var sides = new List<(RoomKind k, int w)>[floors, 2];   // [floor, 0 = south / 1 = north]
            int W = stairs ? StairCells + 2 : 4;
            for (int f = 0; f < floors; f++)
            {
                var s0 = sides[f, 0] = new List<(RoomKind, int)>();
                var s1 = sides[f, 1] = new List<(RoomKind, int)>();
                // biggest first, ties in seeded order, each to the shorter side
                var order = onFloor[f].Select(r => (r, key: rng.Next())).OrderByDescending(x => T(x.r).Cells).ThenBy(x => x.key).Select(x => x.r).ToList();
                foreach (var r in order)
                {
                    int u0 = s0.Sum(x => x.w), u1 = stairCells + s1.Sum(x => x.w);
                    (u0 <= u1 ? s0 : s1).Add((r, Width(r)));
                }
                // a side left empty while the other has two: the smaller crosses over
                if (s1.Count == 0 && s0.Count >= 2) { var m = s0.OrderBy(x => x.w).First(); s0.Remove(m); s1.Add(m); }
                if (s0.Count == 0 && s1.Count >= 2) { var m = s1.OrderBy(x => x.w).First(); s1.Remove(m); s0.Add(m); }
                rng.Shuffle(s0); rng.Shuffle(s1);
                W = Math.Max(W, Math.Max(s0.Sum(x => x.w), stairCells + s1.Sum(x => x.w)));
            }
            int D = depth * 2 + 1, hallZ = depth;   // the hall is row hallZ: south rooms [0, hallZ), north [hallZ + 1, D)

            // widen the rooms that pass `which`, a cell at a time from a seeded one, each up to its limit
            void Grow(List<(RoomKind k, int w)> list, ref int spare, Func<RoomKind, bool> which)
            {
                for (bool grew = true; spare > 0 && grew;)
                {
                    grew = false;
                    for (int n = 0, i = rng.Int(Math.Max(1, list.Count)); n < list.Count && spare > 0; n++, i = (i + 1) % list.Count)
                        if (which(list[i].k) && list[i].w < Width(list[i].k) + (Service(list[i].k) ? 1 : 4))
                        { list[i] = (list[i].k, list[i].w + 1); spare--; grew = true; }
                }
            }
            // a room for leftover length on floor f: the kind's own extras first, one that suits the floor, is not a
            // second of a one-per-building room, and does not stack a kind on this floor that another floor lacks
            RoomKind Filler(int f, int cells)
            {
                foreach (var k in more.Append(RoomKind.Storage))
                {
                    var p = T(k).Pref;
                    if (f == 0 ? p == FloorPref.Upper && floors > 1 : p == FloorPref.GroundOnly || p == FloorPref.Ground) continue;
                    if (OnlyOne(k, kind) && Enumerable.Range(0, floors).Any(g => onFloor[g].Contains(k))) continue;
                    if (Width(k) > cells) continue;
                    int here = onFloor[f].Count(x => x == k);
                    if (p == FloorPref.Any && Enumerable.Range(0, floors).Any(g => g != f && onFloor[g].Count(x => x == k) < here)) continue;
                    return k;
                }
                return RoomKind.Unassigned;
            }
            res.CellsX = W; res.CellsZ = D;

            // ---- rooms in cells: each side padded to W (the spare cells shared round its rooms), the hall, the stairwell
            for (int f = 0; f < floors; f++)
            {
                res.Layout.Add(new GenRoom { Kind = RoomKind.Hall, Floor = f, X0 = 0, X1 = W, Z0 = hallZ, Z1 = hallZ + 1 });
                for (int s = 0; s < 2; s++)
                {
                    var list = sides[f, s];
                    int start = s == 1 ? stairCells : 0;
                    // The spare length: the footprint is the longest side of the longest floor, so the others come up
                    // short. Handing it all round-robin made an 18 m bathroom upstairs. So the bigger rooms grow by up to
                    // four cells, what is left over becomes more rooms while the floor has space for them, then the small
                    // rooms grow by a cell, and only then does anything grow past its limit (the bigger rooms first).
                    int spare = W - start - list.Sum(x => x.w);
                    Grow(list, ref spare, k => !Service(k));
                    while (spare >= 2 && onFloor[f].Count < perFloor && Filler(f, spare) is var fill && fill != RoomKind.Unassigned)
                    {
                        int w = Math.Min(spare, Width(fill) + (Service(fill) ? 1 : 4));
                        if (spare - w == 1) w++;   // a one-cell leftover is too narrow for anything: the new room takes it
                        list.Insert(rng.Int(list.Count + 1), (fill, w));
                        onFloor[f].Add(fill);
                        spare -= w;
                    }
                    Grow(list, ref spare, Service);
                    bool wellTakesSide = s == 1 && stairs && list.Count == 0;
                    if (s == 1 && stairs)
                        res.Layout.Add(new GenRoom { Kind = RoomKind.Stairwell, Floor = f, X0 = 0, X1 = wellTakesSide ? W : StairCells, Z0 = hallZ + 1, Z1 = D });
                    if (wellTakesSide) continue;
                    if (list.Count == 0)
                    {
                        list.Add((RoomKind.Storage, 0));
                        res.Notes.Add($"floor {f}'s {(s == 0 ? "south" : "north")} side had no room and the floor is full: a Storage fills it, over its limit");
                    }
                    // what is still left goes past the limits: to the rooms that are not small first
                    if (list.Any(x => !Service(x.k)))
                        for (int i = rng.Int(list.Count); spare > 0; i = (i + 1) % list.Count)
                        { if (!Service(list[i].k)) { list[i] = (list[i].k, list[i].w + 1); spare--; } }
                    for (int i = rng.Int(list.Count); spare > 0; i = (i + 1) % list.Count, spare--) list[i] = (list[i].k, list[i].w + 1);
                    int x = start;
                    foreach (var (k, w) in list)
                    {
                        res.Layout.Add(new GenRoom { Kind = k, Floor = f, X0 = x, X1 = x + w, Z0 = s == 0 ? 0 : hallZ + 1, Z1 = s == 0 ? hallZ : D });
                        x += w;
                    }
                }
            }
            foreach (var r in res.Layout)
                res.Rooms.Add(new RoomDesignation(r.Kind, (r.X0 + r.X1) * 0.5f * Cell, (r.Z0 + r.Z1) * 0.5f * Cell, r.Floor));

            BuildWalls(res, rng, hallZ);
            return res;
        }

        // =============================================================================================================
        // Walls, openings, slabs, stairs

        /// <summary>A wall whose mid-plane runs from (x0, z0) to (x1, z1) on floor f, carried on past both ends by
        /// `extend` (the corner solve). The editor's frame: it runs along local +X (yaw 0 = +X, 90 = -Z, 180 = -X,
        /// 270 = +Z), its origin the start corner at its base, local +Z out of its front face.</summary>
        static WallPlan Wall(float x0, float z0, float x1, float z1, float y, float thickness, float extend = 0f,
                             SurfaceKind kind = SurfaceKind.Wall, float height = Storey)
        {
            float dx = x1 - x0, dz = z1 - z0, len = MathF.Sqrt(dx * dx + dz * dz);
            float yaw = MathF.Round(MathF.Atan2(-dz, dx) * 180f / MathF.PI);
            yaw = yaw < 0 ? yaw + 360f : yaw + 0f;   // (+ 0 turns the -0 that Atan2(-0, x) rounds to into 0)
            return new WallPlan { X = x0 - dx / len * extend, Y = y, Z = z0 - dz / len * extend, Yaw = yaw, Length = len + 2 * extend,
                                  Height = height, Thickness = thickness, Kind = kind };
        }

        static WallOpening Make(int arch, float uCentre, float width)
        {
            var a = ArchSizes[arch];
            float h = Math.Min(a.H, OpeningTop - a.Sill);
            var o = new WallOpening(uCentre - width * 0.5f, a.Sill, width, h, 999f, arch) { Glazed = a.Glazed };
            if (arch == ArchDoor) o.DoorProp = DefaultDoor;
            return o;
        }

        /// <summary>Openings spread evenly over a stretch of wall: [lo, hi] in the wall's own u, one slot each. A window
        /// too wide for its slot is narrowed (to MinWindow at the least); a door, garage door or vent that does not fit
        /// is left out.</summary>
        static void Spread(WallPlan w, float lo, float hi, IList<int> arches)
        {
            int n = arches.Count;
            if (n == 0 || hi - lo <= 0) return;
            float slot = (hi - lo) / n, gap = n > 1 ? 0.3f : 0f;
            for (int i = 0; i < n; i++)
            {
                int arch = arches[i];
                float width = ArchSizes[arch].W;
                if (width > slot - gap)
                {
                    if ((arch == ArchWindow || arch == ArchTallWindow) && slot - gap >= MinWindow) width = slot - gap;
                    else continue;
                }
                w.Openings.Add(Make(arch, lo + slot * (i + 0.5f), width));
            }
        }

        static void BuildWalls(BuildingGenResult res, Rng rng, int hallZ)
        {
            int W = res.CellsX, D = res.CellsZ, floors = res.Floors;
            float Wm = W * Cell, Dm = D * Cell, he = Ext * 0.5f, hi = Inner * 0.5f;
            bool stairs = floors > 1;
            for (int f = 0; f < floors; f++)
            {
                float y = f * Storey;
                var rooms = res.Layout.Where(r => r.Floor == f).ToList();
                // the four outside walls, anticlockwise from above as the editor lays a room's, front faces out
                var north = Wall(0, Dm, Wm, Dm, y, Ext, he);   // yaw 0, runs +X
                var east = Wall(Wm, Dm, Wm, 0, y, Ext, he);     // yaw 90, runs -Z
                var south = Wall(Wm, 0, 0, 0, y, Ext, he);      // yaw 180, runs -X
                var west = Wall(0, 0, 0, Dm, y, Ext, he);       // yaw 270, runs +Z
                // a stretch [a, b] of a side (metres along +X for north/south, +Z for east/west, from the building's
                // low corner) as u on that side's wall: the walls are carried on by half a thickness at each end, and
                // east and south run backwards
                (float lo, float hi) U(WallPlan w, float a, float b) => w == north || w == west ? (he + a, he + b)
                                                                       : (he + (w == east ? Dm : Wm) - b, he + (w == east ? Dm : Wm) - a);
                // the faces bounding a room's stretch of a side: a corner is half an outside wall, else half a partition
                float Mx0(GenRoom r) => r.X0 == 0 ? he : hi;
                float Mx1(GenRoom r) => r.X1 == W ? he : hi;
                float Mz0(GenRoom r) => r.Z0 == 0 ? he : hi;
                float Mz1(GenRoom r) => r.Z1 == D ? he : hi;
                void Along(WallPlan w, GenRoom r, IList<int> arches)
                {
                    var (lo, hiU) = U(w, r.X0 * Cell + Mx0(r), r.X1 * Cell - Mx1(r));
                    Spread(w, lo, hiU, w == south ? arches.Reverse().ToList() : arches);
                }
                void Across(WallPlan w, GenRoom r, IList<int> arches)
                {
                    var (lo, hiU) = U(w, r.Z0 * Cell + Mz0(r), r.Z1 * Cell - Mz1(r));
                    Spread(w, lo, hiU, w == east ? arches.Reverse().ToList() : arches);
                }

                foreach (var r in rooms)
                {
                    var longSide = r.Z1 == D ? north : r.Z0 == 0 ? south : null;
                    float len = (r.X1 - r.X0) * Cell;
                    int many = Math.Max(1, (int)(len / (2 * Cell)));   // a window per 6 m
                    List<int> side, end;
                    switch (r.Kind)
                    {
                        case RoomKind.Hall:
                            // the front door at the west end, a back door at the east, a window at each end upstairs
                            Across(west, r, new[] { f == 0 ? ArchDoor : ArchWindow });
                            Across(east, r, new[] { f == 0 ? ArchDoor : ArchWindow });
                            continue;
                        case RoomKind.Stairwell:
                            side = new List<int> { ArchTallWindow }; end = new List<int> { ArchTallWindow }; break;
                        case RoomKind.Shop when f == 0:
                            // a shopfront: the street door in the middle of tall windows
                            int k = Math.Max(3, (int)(len / (2 * Cell))) | 1;
                            side = Enumerable.Range(0, k).Select(i => i == k / 2 ? ArchDoor : ArchTallWindow).ToList();
                            end = new List<int> { ArchTallWindow }; break;
                        case RoomKind.Garage when f == 0:
                        case RoomKind.Warehouse when f == 0:
                            // the vehicle door on the long side, windows beside it in a long room
                            side = len >= 8 * Cell ? new List<int> { ArchWindow, ArchGarage, ArchWindow } : new List<int> { ArchGarage };
                            end = new List<int> { ArchWindow }; break;
                        default:
                            int arch = TraitOf(r.Kind, res.Kind).Window;
                            if (arch == ArchGarage) arch = ArchWindow;   // (a garage door upstairs would open onto air)
                            if (arch < 0) continue;
                            side = arch == ArchWindow || arch == ArchTallWindow ? Enumerable.Repeat(arch, many).ToList() : new List<int> { arch };
                            end = arch == ArchVent ? new List<int>() : new List<int> { arch };
                            break;
                    }
                    if (longSide != null) Along(longSide, r, side);
                    if (r.X0 == 0) Across(west, r, end);
                    if (r.X1 == W) Across(east, r, end);
                }
                res.Walls.Add(north); res.Walls.Add(east); res.Walls.Add(south); res.Walls.Add(west);
                if (f == 0)   // footings under the outside walls, as the editor's auto-fit lays them
                    foreach (var w in new[] { north, east, south, west })
                        res.Walls.Add(new WallPlan { X = w.X, Y = -WallOpenings.FoundationDepth, Z = w.Z, Yaw = w.Yaw, Length = w.Length,
                                                     Height = WallOpenings.FoundationDepth, Thickness = w.Thickness, Kind = SurfaceKind.Foundation });

                // the hall's two long walls (tees into the end walls, so not carried on), a door into every room off it
                var hs = Wall(0, hallZ * Cell, Wm, hallZ * Cell, y, Inner);
                var hn = Wall(0, (hallZ + 1) * Cell, Wm, (hallZ + 1) * Cell, y, Inner);
                foreach (var r in rooms)
                {
                    if (r.Kind == RoomKind.Hall) continue;
                    var hw = r.Z1 == hallZ ? hs : hn;
                    float lo = r.X0 * Cell + Mx0(r) + ArchSizes[ArchDoor].W * 0.5f, hiU = r.X1 * Cell - Mx1(r) - ArchSizes[ArchDoor].W * 0.5f;
                    if (hiU < lo) { res.Notes.Add($"{r} is too narrow for a door"); continue; }
                    int w = r.X1 - r.X0;
                    // the middle cell (one of the two middle ones, seeded, when even); a stairwell's onto its landing's middle
                    int cell = r.Kind == RoomKind.Stairwell ? 1 : r.X0 + (w - 1) / 2 + (w % 2 == 0 ? rng.Int(2) : 0);
                    hw.Openings.Add(Make(ArchDoor, Math.Clamp((cell + 0.5f) * Cell, lo, hiU), ArchSizes[ArchDoor].W));
                }
                res.Walls.Add(hs); res.Walls.Add(hn);

                // the partitions between neighbours on each side
                foreach (var r in rooms)
                    if (r.Kind != RoomKind.Hall && r.X1 < W)
                        res.Walls.Add(Wall(r.X1 * Cell, r.Z0 * Cell, r.X1 * Cell, r.Z1 * Cell, y, Inner));

                // the floor, its top at the storey's base, out to the outside walls' outer faces (the editor's slab)
                res.Walls.Add(SlabPlan(-he, Wm + he, -he, Dm + he, y, SurfaceKind.Floor));
            }
            res.Walls.Add(SlabPlan(-he, Wm + he, -he, Dm + he, floors * Storey + Slab, SurfaceKind.Roof));

            // ---- the stairs. The stairwell is StairCells long and `depth` rows deep; row 0, by the hall, is a landing on
            // every floor. Even floors climb EAST up row 1, odd floors WEST up row 2, each centred along the stairwell,
            // so a flight arrives on the landing at the end where the next one starts -- and the hole it needs in the
            // floor above (its footprint and StairHoleMargin) never reaches row 0 or under a wall: row 1's flight is
            // centred in its row, row 2's is pushed against row 1's, since row 2 is the last and the outside wall
            // takes half a thickness off it.
            if (!stairs) return;
            float run = WallOpenings.StairDefaultRun(Storey), sw = StairWidth;
            float x0 = he, x1 = StairCells * Cell - hi;                       // the stairwell's inside faces along the hall
            float foot = (x0 + x1) * 0.5f - run * 0.5f;                       // the west end of either flight
            float row1 = (hallZ + 2) * Cell, row2 = (hallZ + 3) * Cell;
            for (int f = 0; f + 1 < floors; f++)
            {
                if (f % 2 == 0)   // yaw 270: climbs +X, width along +Z
                    res.Stairs.Add(new StairPlan { X = foot, Y = f * Storey, Z = row1 + (Cell - sw) * 0.5f, Yaw = 270f, Run = run, Width = sw, Floor = f });
                else              // yaw 90: climbs -X, width along -Z
                    res.Stairs.Add(new StairPlan { X = foot + run, Y = f * Storey, Z = row2 + sw, Yaw = 90f, Run = run, Width = sw, Floor = f });
            }
        }

        /// <summary>How wide a generated flight is: a row less a margin either side.</summary>
        public const float StairWidth = 2.4f;

        /// <summary>A flat slab over [x0, x1] x [z0, z1] with its top at `top`: the editor's frame for one -- origin at
        /// (minX, centre height, maxZ), run along +X, depth along -Z, pitched -90.</summary>
        static WallPlan SlabPlan(float x0, float x1, float z0, float z1, float top, SurfaceKind kind)
            => new WallPlan { X = x0, Y = top - Slab * 0.5f, Z = z1, Yaw = 0f, Pitch = -90f, Length = x1 - x0, Height = z1 - z0, Thickness = Slab, Kind = kind };
    }
}
