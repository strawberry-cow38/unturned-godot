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
        /// <summary>At least this many ROOMS. Halls are circulation and do not count.</summary>
        public int MinRooms;
        public int MinFloors = 1, MaxFloors = 2;
        /// <summary>At most this many rooms on any one floor (halls not counted).</summary>
        public int MaxRoomsPerFloor = 6;
        /// <summary>At least this many of each kind, on top of what the building's kind always has.</summary>
        public Dictionary<RoomKind, int> MinOfKind = new Dictionary<RoomKind, int>();
    }

    /// <summary>One generated room on the 3 m lattice: the cells [X0, X1) x [Z0, Z1) of its floor, and any further
    /// rectangles in More (a hall's stair bay, a stretch of landing) -- the room is their union.</summary>
    public sealed class GenRoom
    {
        public RoomKind Kind;
        public int Floor, X0, Z0, X1, Z1;
        public readonly List<(int X0, int Z0, int X1, int Z1)> More = new List<(int, int, int, int)>();
        public IEnumerable<(int X0, int Z0, int X1, int Z1)> Rects
        { get { yield return (X0, Z0, X1, Z1); foreach (var r in More) yield return r; } }
        public int Cells => Rects.Sum(r => (r.X1 - r.X0) * (r.Z1 - r.Z0));
        public bool Circulation => Kind == RoomKind.Hall || Kind == RoomKind.Stairwell;
        public bool HasCell(int x, int z) => Rects.Any(r => x >= r.X0 && x < r.X1 && z >= r.Z0 && z < r.Z1);
        /// <summary>Is this plan point (metres) in the room? Half-open, so a point is in at most one room.</summary>
        public bool Holds(float x, float z) => Rects.Any(r => x >= r.X0 * BuildingGen.Cell && x < r.X1 * BuildingGen.Cell
                                                            && z >= r.Z0 * BuildingGen.Cell && z < r.Z1 * BuildingGen.Cell);
        public override string ToString() => $"{Kind}@{Floor} [{X0},{Z0}..{X1},{Z1}]" + (More.Count > 0 ? $"+{More.Count}" : "");
    }

    /// <summary>A door the generator decided on: between two rooms (B null: to outside), cut in the wall on a lattice
    /// line -- AlongX, the line of constant z = Line, else of constant x = Line -- centred At metres along it.</summary>
    public sealed class GenDoor
    {
        public int Floor;
        public GenRoom A, B;
        public bool AlongX;
        public int Line;
        public float At;
        /// <summary>Front or back for the two marked doors to outside; Inside for everything else.</summary>
        public DoorRole Role;
        /// <summary>The middle of its opening in plan, metres.</summary>
        public (float X, float Z) Middle => AlongX ? (At, Line * BuildingGen.Cell) : (Line * BuildingGen.Cell, At);
        public override string ToString() => $"{A}->{(B == null ? "outside" : B.ToString())} at {(AlongX ? "z" : "x")}={Line} {At:0.0}{(Role != DoorRole.Inside ? " " + Role : "")}";
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
        /// <summary>Each floor's extent along X in cells, [X0, X1); every floor is the full CellsZ deep. An upper floor
        /// can be shorter than the one below, never longer or outside it.</summary>
        public List<(int X0, int X1)> Footprints = new List<(int, int)>();
        /// <summary>How the ground floor is laid out: "bands" (rooms open into each other, no hall) or "corridor".</summary>
        public string Plan;
        /// <summary>The retail palette its walls wear (content/wall_palettes.tsv, by NAME: the editor resolves the index).</summary>
        public string Palette;
        public RoofKind Roof;
        public float RoofPitch;
        /// <summary>Walls with their openings, footings, a floor per storey and the roofs, in building-local metres: the
        /// ground floor's outside walls' mid-planes enclose (0, 0)..(CellsX, CellsZ) x LatticeStep, and its floor is y 0.</summary>
        public List<WallPlan> Walls = new List<WallPlan>();
        public List<StairPlan> Stairs = new List<StairPlan>();
        public List<RoomDesignation> Rooms = new List<RoomDesignation>();
        public List<GenRoom> Layout = new List<GenRoom>();
        public List<GenDoor> Doors = new List<GenDoor>();
        /// <summary>The marked doors: the front one always, the back one when there is one.</summary>
        public GenDoor Front => Doors.FirstOrDefault(d => d.Role == DoorRole.Front);
        public GenDoor Back => Doors.FirstOrDefault(d => d.Role == DoorRole.Back);
        public IEnumerable<DoorDesignation> DoorMarks => Doors.Where(d => d.Role != DoorRole.Inside).Select(d => new DoorDesignation(d.Role, d.Middle.X, d.Middle.Z, d.Floor));
        /// <summary>The stair bay in cells, the same on every floor: the flight rows and the row of floor beside them.
        /// All zero for one floor.</summary>
        public (int X0, int Z0, int X1, int Z1) Bay;
        /// <summary>Where it could not do what was asked, said so (a minimum that did not fit, a limit it broke).</summary>
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// SEEDED BUILDINGS. The editor's own data, generated: walls with openings, a floor slab per storey, roofs, stairs,
    /// and a RoomDesignation for every room -- so a generated building is edited exactly like a drawn one.
    ///
    /// strawberry, on the first version (a hall down the middle of every floor and a stairwell room): "hallways shouldnt
    /// be guaranteed, and some rooms may go directly into eachother. stairs dont need their own room and may be in the
    /// halls or other 'non-private' rooms (bedrooms, bathrooms, offices are private). having multiple floors isnt
    /// required. the roof may change depending on the type of building too, businesses having flat roofs while houses
    /// have gable. the 2nd floor need not be a direct mirror of the floor below, it can be smaller" -- and "use common
    /// sense for all of the room connection logic and layouts. but dont be overly strict on room rules".
    ///
    /// So a floor is a GRID of 3 m cells, each owned by a room, and the walls are wherever two owners meet. The ground
    /// floor is laid out one of two ways. BANDS: the rooms you walk through (living room, kitchen, shop floor...) in a
    /// row along one side, the private ones (bedrooms, bathrooms, offices) in a row along the other, each opening
    /// straight into whichever walk-through room it faces -- no hall at all. CORRIDOR: a hall the length of the floor
    /// with rooms either side, for when that is what the rooms need. Doors are then DECIDED, not assumed: a front door
    /// into the most entrance-like room, a tree of doors through the walk-through rooms (and a second where two of them
    /// belong together, kitchen to dining room), and every private room a dead end off one of them -- or off a bedroom,
    /// for a closet or an en-suite. A layout whose rooms cannot all be reached that way is thrown away and another drawn.
    ///
    /// Upper floors, when there are any, are a landing hall with the rooms round it, and may be shorter than the floor
    /// below (never longer). The stairs stand in a STAIR BAY -- the flight against the south wall and a row of floor
    /// beside it to step off onto -- inside a walk-through room on the ground floor and inside the landing hall above;
    /// with three or more floors the flights alternate between two rows so each lands beside the next one's foot.
    ///
    /// Houses get a gable or a hip roof (the editor's own RoofShapes), businesses a flat one; a lower floor's part that
    /// the floor above does not cover gets a roof of its own -- a shed leaning on the upper wall for a house, flat for a
    /// business. Everything is on the editor's 3 m lattice, a storey is the wall
    /// height, the outside walls come corner-solved (the editor's SolveCorners lengthens a wall from its origin WITHOUT
    /// moving its openings, so it must find nothing to do here), and the openings are the editor's archetypes.
    /// </summary>
    public static class BuildingGen
    {
        public const float Cell = WallOpenings.LatticeStep;
        /// <summary>EditorBuildings.StoreyHeight: a storey is the wall height (retail WALL_HEIGHT).</summary>
        public const float Storey = WallOpenings.DoorHeight;
        public const float Slab = 0.5f;   // EditorBuildings.SlabThickness
        /// <summary>EditorBuildings.RoofOverhang: how far a pitched roof's eaves run past the wall face.</summary>
        public const float RoofOverhang = 0.4f;
        /// <summary>No opening reaches higher than this: the floor above occupies the top Slab of every wall.</summary>
        public const float OpeningTop = Storey - Slab;
        public const float Ext = WallOpenings.DefaultThickness, Inner = WallOpenings.InteriorThickness;
        /// <summary>How wide a generated flight is.</summary>
        public const float StairWidth = 2.4f;
        /// <summary>Retail's pitched roofs cluster at 15-22 degrees (EditorBuildings.RoofPitches).</summary>
        public static readonly float[] GablePitches = { 15f, 18f, 20f, 22f };

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
        const float HalfDoor = 1.25f;

        /// <summary>Where a kind of room wants to be.</summary>
        public enum FloorPref { GroundOnly, Ground, Any, Upper }

        /// <summary>How a room is used for getting about: Through rooms you walk through to reach others; Private rooms
        /// (strawberry: "bedrooms, bathrooms, offices are private") are dead ends; Leaf rooms (a garage, a store room) are
        /// dead ends that are not private.</summary>
        public enum Access { Through, Private, Leaf }

        /// <summary>A room kind's needs: its area in cells, the narrowest it may be, which floor it wants, and what goes in
        /// its outside walls (-1: nothing).</summary>
        public struct Trait
        {
            public int Cells, MinSide; public FloorPref Pref; public int Window;
            public Trait(int cells, int minSide, FloorPref pref, int window) { Cells = cells; MinSide = minSide; Pref = pref; Window = window; }
        }

        // Every room is at least 2 cells along its row (Width): a one-cell room at a corner has 2.4 m between its walls'
        // faces and a door is 2.5.
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

        public static Access AccessOf(RoomKind k) => k switch
        {
            RoomKind.Hall or RoomKind.Stairwell or RoomKind.LivingRoom or RoomKind.Kitchen or RoomKind.DiningRoom
                or RoomKind.Shop or RoomKind.Warehouse or RoomKind.Workshop => Access.Through,
            RoomKind.Bedroom or RoomKind.Bathroom or RoomKind.Office or RoomKind.Closet => Access.Private,
            _ => Access.Leaf,
        };

        /// <summary>Rooms a flight of stairs may stand in: not private, and roomy enough to walk round one.</summary>
        public static bool HoldsStairs(RoomKind k) => k is RoomKind.Hall or RoomKind.LivingRoom or RoomKind.DiningRoom
            or RoomKind.Shop or RoomKind.Warehouse or RoomKind.Workshop;

        /// <summary>May a room of kind `child` have its door off a room of kind `parent`? Off anything you walk through;
        /// a closet or an en-suite off a bedroom; a store room off a garage; a closet, toilet or store off an office.
        /// Common sense, not a building code: strawberry, "dont be overly strict on room rules".</summary>
        public static bool MayOpenOff(RoomKind child, RoomKind parent) => AccessOf(parent) == Access.Through
            || (parent == RoomKind.Bedroom && child is RoomKind.Closet or RoomKind.Bathroom)
            || (parent == RoomKind.Garage && child == RoomKind.Storage)
            || (parent == RoomKind.Office && child is RoomKind.Closet or RoomKind.Bathroom or RoomKind.Storage);

        /// <summary>Rooms that need daylight: each gets at least one glazed opening in an outside wall.</summary>
        public static bool Habitable(RoomKind k) => k is RoomKind.Bedroom or RoomKind.Kitchen or RoomKind.LivingRoom
            or RoomKind.DiningRoom or RoomKind.Office or RoomKind.Shop or RoomKind.Workshop;

        /// <summary>Rooms a building has only one of unless the spec asks for more.</summary>
        public static bool OnlyOne(RoomKind k, BuildingKind b) => k == RoomKind.Kitchen
            || (b == BuildingKind.Residential && (k == RoomKind.LivingRoom || k == RoomKind.DiningRoom));

        /// <summary>Small rooms that should stay small: they grow by a cell at most when a floor has length to spare.</summary>
        public static bool Service(RoomKind k) => k is RoomKind.Bathroom or RoomKind.Closet or RoomKind.Storage;

        /// <summary>The rooms a front door may open into, best first (strawberry: "mark a front and back door of the house
        /// only in appropriate rooms"): a house's hall or living room, a shop's shop floor, a works' hall or shop floor, an
        /// office block's hall. A ground floor with none of them on an outside wall is redrawn.</summary>
        public static RoomKind[] EntranceOrder(BuildingKind b) => b switch
        {
            BuildingKind.Commercial => new[] { RoomKind.Shop, RoomKind.Hall },
            BuildingKind.Industrial => new[] { RoomKind.Hall, RoomKind.Workshop, RoomKind.Warehouse, RoomKind.Shop },
            BuildingKind.Residential => new[] { RoomKind.Hall, RoomKind.LivingRoom },
            _ => new[] { RoomKind.Hall },
        };

        /// <summary>The rooms a back door may open out of, best first: a house's kitchen (or hall, or the dining room's
        /// garden door), a shop's stockroom or kitchen, a works' warehouse or workshop, an office block's kitchen or hall.</summary>
        public static RoomKind[] BackDoorOrder(BuildingKind b) => b switch
        {
            BuildingKind.Commercial => new[] { RoomKind.Storage, RoomKind.Kitchen, RoomKind.Hall },
            BuildingKind.Industrial => new[] { RoomKind.Warehouse, RoomKind.Workshop, RoomKind.Storage, RoomKind.Hall },
            BuildingKind.Residential => new[] { RoomKind.Kitchen, RoomKind.Hall, RoomKind.DiningRoom },
            _ => new[] { RoomKind.Kitchen, RoomKind.Hall, RoomKind.Storage },
        };

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
            public bool Chance(int percent) => Int(100) < percent;
            public void Shuffle<T>(IList<T> list) { for (int i = list.Count - 1; i > 0; i--) { int j = Int(i + 1); (list[i], list[j]) = (list[j], list[i]); } }
        }

        /// <summary>One floor's cells: which room owns each, inside the floor's extent [Ux0, Ux1) x [0, D).</summary>
        sealed class Grid
        {
            public int Ux0, Ux1, D;
            public GenRoom[,] Cell;
            public Grid(int w, int d, int ux0, int ux1) { Ux0 = ux0; Ux1 = ux1; D = d; Cell = new GenRoom[w, d]; }
            public GenRoom At(int x, int z) => x < Ux0 || x >= Ux1 || z < 0 || z >= D ? null : Cell[x, z];
            static float Edge(GenRoom a, GenRoom b) => a == b ? 0f : a == null || b == null ? Ext : Inner;
            /// <summary>The wall on the line of constant z between cells (x, z-1) and (x, z): its thickness, 0 for none.</summary>
            public float H(int x, int z) => x < Ux0 || x >= Ux1 ? 0f : Edge(At(x, z - 1), At(x, z));
            /// <summary>The wall on the line of constant x between cells (x-1, z) and (x, z).</summary>
            public float V(int x, int z) => z < 0 || z >= D ? 0f : Edge(At(x - 1, z), At(x, z));
            /// <summary>Half the thickness of the wall meeting a line at a lattice point: an opening keeps that far off it.</summary>
            public float Meet(bool alongX, int line, int s) => 0.5f * (alongX ? Math.Max(V(s, line - 1), V(s, line)) : Math.Max(H(line - 1, s), H(line, s)));
            /// <summary>The two cells either side of a line at position s along it.</summary>
            public (GenRoom A, GenRoom B) Across(bool alongX, int line, int s) => alongX ? (At(s, line - 1), At(s, line)) : (At(line - 1, s), At(line, s));
            public void Paint(GenRoom r) { foreach (var q in r.Rects) for (int x = q.X0; x < q.X1; x++) for (int z = q.Z0; z < q.Z1; z++) Cell[x, z] = r; }
        }

        /// <summary>A run of a lattice line where the same two owners face each other: cells [S0, S1) along it. A is a room;
        /// B is the room on the other side, or null for outside.</summary>
        struct Run { public bool AlongX; public int Line, S0, S1; public GenRoom A, B; }

        public static BuildingGenResult Generate(BuildingGenSpec spec)
        {
            var rng = new Rng(spec.Seed);
            var kind = spec.Kind;
            var res = new BuildingGenResult { Kind = kind };
            var pals = PalettesFor(kind);
            res.Palette = pals[rng.Int(pals.Length)];
            // a house's top roof is a gable or, now and then, a hip; a business's is flat
            res.Roof = kind == BuildingKind.Residential ? (rng.Chance(35) ? RoofKind.Hip : RoofKind.Gable) : RoofKind.Flat;
            res.RoofPitch = res.Roof != RoofKind.Flat ? GablePitches[rng.Int(GablePitches.Length)] : 0f;
            int maxFloors = Math.Max(1, spec.MaxFloors), minFloors = Math.Clamp(spec.MinFloors, 1, maxFloors);
            int perFloor = Math.Max(1, spec.MaxRoomsPerFloor), capacity = maxFloors * perFloor;
            Trait T(RoomKind r) => TraitOf(r, kind);
            int Width(RoomKind r, int depth) => Math.Max(Math.Max(2, T(r).MinSide), (int)Math.Ceiling(T(r).Cells / (double)depth));

            // ---- the programme: the kind's own rooms, raised to the minimums, filled to MinRooms, and a seeded extra
            var (always, more) = Programme(kind);
            var rooms = new List<RoomKind>(always);
            foreach (var kv in spec.MinOfKind.OrderBy(kv => (int)kv.Key))
                if (kv.Key != RoomKind.Unassigned && kv.Key != RoomKind.Hall && kv.Key != RoomKind.Stairwell)
                    for (int have = rooms.Count(r => r == kv.Key); have < kv.Value; have++) rooms.Add(kv.Key);
            // A filler that can only go on the ground is skipped once the ground has no space for it (the spec asked for
            // rooms, not for that one), and so is a second of a room a building has one of -- the first plans had two
            // dining rooms. The spec's own minimums still get as many as they ask for. (Every kind's list has a room
            // that can go anywhere, so this always finds one.)
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

            // ---- floors: as few as hold them, at least MinFloors. A second floor is not owed to anyone ("having multiple
            // floors isnt required"): a building that fits on one gets two only on a roll, and only when it has rooms
            // that would rather be upstairs.
            int floors = Math.Max(minFloors, (rooms.Count + perFloor - 1) / perFloor);
            if (floors == 1 && floors < maxFloors && rooms.Count >= 5 && rooms.Any(r => T(r).Pref == FloorPref.Upper)
                && rng.Chance(kind == BuildingKind.Residential ? 50 : 35)) floors = 2;
            floors = Math.Min(floors, maxFloors);
            res.Floors = floors;
            bool multi = floors > 1;
            int k1 = floors > 2 ? 2 : 1, L = multi ? 4 : 0, bayRows = k1 + 1;   // the stair bay (see below)
            // A room's share of a row: its target area -- the stair room's grown by the bay it gives over to the stairs
            // (strawberry: "the living room, which is further eaten by stairs") -- and how far past that it may grow:
            // three quarters again, a small room one cell. Growing by share rather than by turns is what keeps a dining
            // room from outgrowing the living room ("make sure rooms are scaled proportionately to what they are").
            int Target(RoomKind r, bool host) => T(r).Cells + (host ? L * bayRows : 0);
            int Cap(RoomKind r, int depth, bool host = false) => Service(r) ? Width(r, depth) + 1
                                                             : Math.Max(Width(r, depth), (int)(1.75f * Target(r, host) / depth));
            int HostWidth(RoomKind r, int depth) => Math.Max(L, (int)Math.Ceiling(Target(r, true) / (double)depth));

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
            var upperFloors = Enumerable.Range(1, floors - 1);
            var allFloors = Enumerable.Range(0, floors);
            foreach (var r in rooms.OrderBy(r => Rank(T(r).Pref)).ThenByDescending(r => T(r).Cells).ThenBy(r => (int)r))
            {
                int at = T(r).Pref switch
                {
                    FloorPref.GroundOnly => 0,
                    FloorPref.Ground => onFloor[0].Count < perFloor ? 0 : Pick(upperFloors, r),
                    FloorPref.Upper => Pick(upperFloors, r) is int u && u >= 0 ? u : Pick(allFloors, r),
                    _ => Pick(allFloors, r),
                };
                if (at < 0 || onFloor[at].Count >= perFloor)
                {
                    at = 0;
                    res.Notes.Add($"{r} put on the ground floor over its limit of {perFloor} (every floor was full{(T(r).Pref == FloorPref.GroundOnly ? ", and it can only go on the ground" : "")})");
                }
                onFloor[at].Add(r);
            }
            // an upper floor with nothing on it would be stairs to an empty landing: give it the most upstairs-minded room
            // the ground floor can spare
            for (int f = 1; f < floors; f++)
                if (onFloor[f].Count == 0)
                {
                    var spare = onFloor[0].Where(r => T(r).Pref != FloorPref.GroundOnly).OrderBy(r => Rank(T(r).Pref) == 2 ? 0 : Rank(T(r).Pref) == 3 ? 1 : 2).FirstOrDefault();
                    if (spare != RoomKind.Unassigned && onFloor[0].Count > 1) { onFloor[0].Remove(spare); onFloor[f].Add(spare); }
                    else res.Notes.Add($"floor {f} has no room on it: nothing on the ground floor could move up");
                }

            // ---- room widths along a row, and what to do with a row's spare length
            // Widen the rooms that pass `which`, a cell at a time from a seeded one, each up to its limit.
            // Widen the rooms that pass `which`, a cell at a time, each time the one furthest below its share (seeded among
            // equals), each up to its limit -- or past it, when `capped` is off.
            void Grow(List<(RoomKind k, int w)> list, ref int spare, int depth, Func<RoomKind, bool> which, RoomKind host, bool capped = true)
            {
                int hostAt = host == RoomKind.Unassigned ? -1 : list.FindIndex(x => x.k == host);
                while (spare > 0)
                {
                    int best = -1; double low = double.MaxValue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var (k, w) = list[i];
                        if (!which(k) || (capped && w >= Cap(k, depth, i == hostAt))) continue;
                        double share = (double)(w * depth) / Target(k, i == hostAt) + rng.Int(1000) * 1e-6;
                        if (share < low) { low = share; best = i; }
                    }
                    if (best < 0) break;
                    list[best] = (list[best].k, list[best].w + 1);
                    spare--;
                }
            }
            // A room for leftover length on floor f: the kind's own extras first, one that suits the floor and the row,
            // is not a second of a one-per-building room, and does not stack a kind here that another floor lacks.
            RoomKind Filler(List<RoomKind> here, int f, int cells, int depth, Func<RoomKind, bool> ok)
            {
                foreach (var k in more.Append(RoomKind.Storage))
                {
                    var p = T(k).Pref;
                    if (f == 0 ? p == FloorPref.Upper && floors > 1 : p == FloorPref.GroundOnly || p == FloorPref.Ground) continue;
                    if (OnlyOne(k, kind) && (here.Contains(k) || Enumerable.Range(0, floors).Any(g => g != f && onFloor[g].Contains(k)))) continue;
                    if (Width(k, depth) > cells || !ok(k)) continue;
                    int mine = here.Count(x => x == k);
                    if (p == FloorPref.Any && Enumerable.Range(0, floors).Any(g => g != f && onFloor[g].Count(x => x == k) < mine)) continue;
                    return k;
                }
                return RoomKind.Unassigned;
            }
            // Fill a row to `length`. The footprint is the longest row of the longest floor, so the others come up short:
            // the bigger rooms grow by up to four cells, what is left becomes more rooms while the floor has space for
            // them, then the small rooms grow a cell, and only then does anything grow past its limit (the bigger rooms
            // first). Handing it all out round-robin made an 18 m bathroom. Returns what it could not place -- all of it,
            // when the row is empty -- for the caller's hall to take.
            int Pad(List<(RoomKind k, int w)> list, int length, int depth, List<RoomKind> here, int f, Func<RoomKind, bool> ok,
                    RoomKind host = RoomKind.Unassigned)
            {
                int spare = length - list.Sum(x => x.w);
                Grow(list, ref spare, depth, k => !Service(k), host);
                while (spare >= 2 && here.Count < perFloor && Filler(here, f, spare, depth, ok) is var fill && fill != RoomKind.Unassigned)
                {
                    int w = Math.Min(spare, Cap(fill, depth));
                    // a one-cell leftover is too narrow for anything: a bigger room in the row takes it, or else the new room
                    if (spare - w == 1 && (Service(fill) ? !list.Any(x => !Service(x.k)) : true)) w++;
                    list.Insert(rng.Int(list.Count + 1), (fill, w));
                    here.Add(fill);
                    spare -= w;
                }
                Grow(list, ref spare, depth, Service, host);
                if (list.Count == 0) return spare;
                // past the limits now, still by share: the bigger rooms first, the small ones only if there is nothing else
                if (list.Any(x => !Service(x.k))) Grow(list, ref spare, depth, k => !Service(k), host, capped: false);
                Grow(list, ref spare, depth, _ => true, host, capped: false);
                Balance(list, depth, host);
                return 0;
            }
            // Even out the shares: a room taken up to its limit before a filler joined the row keeps a cell the newcomer is
            // short of (two offices of 5 and 3). Move a cell from the room furthest over its share to the one furthest
            // under while that brings them closer, neither going below its least width.
            void Balance(List<(RoomKind k, int w)> list, int depth, RoomKind host)
            {
                int hostAt = host == RoomKind.Unassigned ? -1 : list.FindIndex(x => x.k == host);
                double Share(int i, int w) => (double)(w * depth) / Target(list[i].k, i == hostAt);
                for (int guard = 0; guard < 64; guard++)
                {
                    var big = Enumerable.Range(0, list.Count).Where(i => !Service(list[i].k) && list[i].w > Width(list[i].k, depth)).OrderByDescending(i => Share(i, list[i].w)).FirstOrDefault(-1);
                    var small = Enumerable.Range(0, list.Count).Where(i => !Service(list[i].k)).OrderBy(i => Share(i, list[i].w)).FirstOrDefault(-1);
                    if (big < 0 || small < 0 || big == small) return;
                    if (Math.Max(Share(big, list[big].w - 1), Share(small, list[small].w + 1)) >= Share(big, list[big].w) - 1e-9) return;
                    list[big] = (list[big].k, list[big].w - 1);
                    list[small] = (list[small].k, list[small].w + 1);
                }
            }
            // put a row's dining room right next to its kitchen, on a seeded side
            void KitchenBesideDining(List<(RoomKind k, int w)> row)
            {
                int ki = row.FindIndex(x => x.k == RoomKind.Kitchen), di = row.FindIndex(x => x.k == RoomKind.DiningRoom);
                if (ki < 0 || di < 0 || Math.Abs(ki - di) == 1) return;
                var dining = row[di];
                row.RemoveAt(di);
                ki = row.FindIndex(x => x.k == RoomKind.Kitchen);
                row.Insert(ki + rng.Int(2), dining);
            }
            List<RoomKind> Seeded(IEnumerable<RoomKind> rs, int depth)
                => rs.Select(r => (r, key: rng.Next())).OrderByDescending(x => Width(x.r, depth)).ThenBy(x => x.key).Select(x => x.r).ToList();

            // ---- the stair bay: k1 flight rows against the south wall and a row of floor beside them, four cells long --
            // a 6 m flight and a full tile clear at each end of it, at the foot and on the landing above (strawberry:
            // "stairs should want 1 full tile at the foot and 1 tile on the landing above")
            int dS = 3, dN = kind == BuildingKind.Industrial ? 4 : 3;   // upper floors: rows south of the hall, and north of it
            bool stairsWest = rng.Int(2) == 0;                          // the bay at the west end of the upper floors, or the east

            // ---- upper floors, top down (each must fit on the one below): a landing hall the length of the floor with the
            // bay off its south side, and the rooms either side of it
            var upS = new List<(RoomKind k, int w)>[floors];
            var upN = new List<(RoomKind k, int w)>[floors];
            var upLen = new int[floors];
            var upAbsorbS = new int[floors];
            var upAbsorbN = new int[floors];
            int need = 0;
            for (int f = floors - 1; f >= 1; f--)
            {
                var S = new List<(RoomKind k, int w)>(); var N = new List<(RoomKind k, int w)>();
                foreach (var r in Seeded(onFloor[f], dS))
                {
                    int lenS = L + S.Sum(x => x.w) + Width(r, dS), lenN = N.Sum(x => x.w) + Width(r, dN);
                    if (lenS <= lenN) S.Add((r, Width(r, dS))); else N.Add((r, Width(r, dN)));
                }
                int Lu = Math.Max(Math.Max(L + S.Sum(x => x.w), N.Sum(x => x.w)), Math.Max(L, need));
                upAbsorbS[f] = Pad(S, Lu - L, dS, onFloor[f], f, _ => true);
                upAbsorbN[f] = Pad(N, Lu, dN, onFloor[f], f, _ => true);
                upS[f] = S; upN[f] = N; upLen[f] = Lu;
                need = Lu;
            }

            // ---- the ground floor: BANDS or CORRIDOR, drawn until one connects
            int bandsChance = kind switch { BuildingKind.Residential => 65, BuildingKind.Misc => 25, _ => 75 };
            bool bandsFirst = rng.Chance(bandsChance);
            Grid ground = null;
            int W = 0, D = 0, bx0 = 0;
            for (int attempt = 0; attempt < 14 && ground == null; attempt++)
            {
                bool bands = attempt < 9 ? bandsFirst : !bandsFirst;
                var here = new List<RoomKind>(onFloor[0]);
                var plan = bands ? Bands(here, attempt >= 6 && attempt < 9 || attempt >= 12) : Corridor(here, attempt >= 12);
                if (plan == null) continue;
                var (prs, pw, pd, pb) = plan.Value;
                var g = new Grid(pw, pd, 0, pw);
                foreach (var r in prs) g.Paint(r);
                var doors = new List<GenDoor>();
                // the dining room beside the kitchen, or facing it across a hall (strawberry: "require dining room to be
                // adjascent or at least nearby a kitchen"); the last, safe draws take what they get and say so
                bool apart = !KitchenNearDining(g, prs);
                bool lopsided = !Proportioned(prs, multi ? (pb, 0, pb + L, bayRows) : (0, 0, 0, 0));
                if ((apart || lopsided) && attempt < 12) continue;
                if (!Connect(g, prs, 0, null, doors, multi ? (pb, 0, pb + L, bayRows) : (0, 0, 0, 0))) continue;
                if (apart) res.Notes.Add("the dining room could not be put beside the kitchen");
                if (lopsided) res.Notes.Add("the living room came out smaller than the dining room or kitchen");
                ground = g; W = pw; D = pd; bx0 = pb;
                res.Plan = bands ? "bands" : "corridor";
                res.Layout.AddRange(prs);
                res.Doors.AddRange(doors);
                onFloor[0] = here;
            }
            if (ground == null) throw new InvalidOperationException($"BuildingGen: no ground floor connected for {kind} seed {spec.Seed} (min {spec.MinRooms} rooms, {spec.MinFloors}-{spec.MaxFloors} floors, {spec.MaxRoomsPerFloor}/floor): {string.Join(", ", onFloor[0])}");
            res.CellsX = W; res.CellsZ = D;
            res.Bay = multi ? (bx0, 0, bx0 + L, bayRows) : (0, 0, 0, 0);
            res.Footprints.Add((0, W));
            var grids = new List<Grid> { ground };

            // ---- upper floors placed over the bay, each inside the one below
            for (int f = 1; f < floors; f++)
            {
                int Lu = upLen[f], ux0 = stairsWest ? bx0 : bx0 + L - Lu, ux1 = ux0 + Lu;
                var hall = new GenRoom { Kind = RoomKind.Hall, Floor = f, X0 = ux0, X1 = ux1, Z0 = dS, Z1 = dS + 1 };
                hall.More.Add((bx0, 0, bx0 + L, dS));
                var rs = new List<GenRoom> { hall };
                // the south side beyond the bay, the north side the whole length; a side with no room is landing
                int x = stairsWest ? bx0 + L : ux0;
                foreach (var (k, w) in upS[f]) { rs.Add(new GenRoom { Kind = k, Floor = f, X0 = x, X1 = x + w, Z0 = 0, Z1 = dS }); x += w; }
                if (upAbsorbS[f] > 0) hall.More.Add(stairsWest ? (bx0 + L, 0, ux1, dS) : (ux0, 0, bx0, dS));
                x = ux0;
                foreach (var (k, w) in upN[f]) { rs.Add(new GenRoom { Kind = k, Floor = f, X0 = x, X1 = x + w, Z0 = dS + 1, Z1 = D }); x += w; }
                if (upAbsorbN[f] > 0) hall.More.Add((ux0, dS + 1, ux1, D));
                var g = new Grid(W, D, ux0, ux1);
                foreach (var r in rs) g.Paint(r);
                var doors = new List<GenDoor>();
                if (!Connect(g, rs, f, hall, doors, (bx0, 0, bx0 + L, bayRows)))
                    res.Notes.Add($"floor {f}: not every room could be given a door");
                res.Layout.AddRange(rs);
                res.Doors.AddRange(doors);
                res.Footprints.Add((ux0, ux1));
                grids.Add(g);
            }
            foreach (var r in res.Layout)
                res.Rooms.Add(new RoomDesignation(r.Kind, (r.X0 + r.X1) * 0.5f * Cell, (r.Z0 + r.Z1) * 0.5f * Cell, r.Floor));

            BuildWalls(res, grids, k1, L);
            return res;

            // =========================================================================================================
            // The two ground-floor plans. Each returns its rooms on a w x d grid, and where the bay starts.

            // BANDS: walk-through and leaf rooms in a row along the south side (band A), private ones along the north
            // (band B), the lengths evened out by moving rooms across; every room opens into whichever walk-through room it
            // faces or sits beside. With stairs, the bay stands in band A's roomiest walk-through room.
            (List<GenRoom> Rooms, int W, int D, int Bx0)? Bands(List<RoomKind> here, bool forceEnds)
            {
                int dA, dB;
                if (multi) { dA = k1 == 2 ? 4 : 3 + rng.Int(2); dB = dS + 1 + dN - dA; }
                else { dA = 3 + rng.Int(2); dB = 3 + rng.Int(2); }
                var A = here.Where(r => AccessOf(r) != Access.Private).ToList();
                var B = here.Where(r => AccessOf(r) == Access.Private).ToList();
                RoomKind host = RoomKind.Unassigned;
                if (multi)
                {
                    host = A.Where(HoldsStairs).OrderByDescending(r => T(r).Cells).ThenBy(_ => rng.Next()).FirstOrDefault();
                    if (host == RoomKind.Unassigned) return null;
                }
                int LenA() => A.Sum(r => Width(r, dA)) + (multi ? HostWidth(host, dA) - Width(host, dA) : 0);
                int LenB() => B.Sum(r => Width(r, dB));
                for (int guard = 0; guard < 12; guard++)
                {
                    int la = LenA(), lb = LenB();
                    // (the kitchen and dining room stay in band A when anything else can go, and cross together when
                    // nothing else can, so they always share a band and can sit side by side)
                    var fromA = A.Where(r => r != host || A.Count(x => x == host) > 1).ToList();
                    bool KD(RoomKind r) => r == RoomKind.Kitchen || r == RoomKind.DiningRoom;
                    if (fromA.Any(r => !KD(r))) fromA = fromA.Where(r => !KD(r)).ToList();
                    if ((la > lb + 3 || B.Count == 0) && fromA.Count > 0 && A.Count > 1)
                    {
                        var m = fromA[rng.Int(fromA.Count)];
                        var moving = KD(m) ? A.Where(KD).ToList() : new List<RoomKind> { m };
                        if (moving.Count >= A.Count) moving = new List<RoomKind> { m };
                        foreach (var mv in moving) { A.Remove(mv); B.Add(mv); }
                    }
                    else if ((lb > la + 3 || A.Count == 0) && B.Count > 1) { var m = B[rng.Int(B.Count)]; B.Remove(m); A.Add(m); }
                    else break;
                }
                if (A.Count == 0 || B.Count == 0) return null;
                // band A: its dead ends to the ends of the row, so they never sit between two rooms you walk through
                var rowA = new List<(RoomKind k, int w)>();
                var thr = Seeded(A.Where(r => AccessOf(r) == Access.Through), dA);
                // the dining room next to the kitchen, on a seeded side of it
                if (thr.Contains(RoomKind.Kitchen) && thr.Contains(RoomKind.DiningRoom))
                {
                    thr.Remove(RoomKind.DiningRoom);
                    thr.Insert(thr.IndexOf(RoomKind.Kitchen) + rng.Int(2), RoomKind.DiningRoom);
                }
                foreach (var r in thr)
                    rowA.Add((r, r == host ? HostWidth(r, dA) : Width(r, dA)));
                foreach (var r in Seeded(A.Where(r => AccessOf(r) != Access.Through), dA))
                    if ((forceEnds && multi) ? !stairsWest : rng.Int(2) == 0) rowA.Insert(0, (r, Width(r, dA))); else rowA.Add((r, Width(r, dA)));
                // the stair room at the end of the row the floors above start from: the bay is four cells and the floors
                // above must fit inside this one from it, so a stair room in the middle of a long row often has no room
                if (multi && (forceEnds || rng.Chance(60)))
                {
                    int hi0 = rowA.FindIndex(x => x.k == host);
                    var hr = rowA[hi0];
                    rowA.RemoveAt(hi0);
                    if (stairsWest) rowA.Insert(0, hr); else rowA.Add(hr);
                    KitchenBesideDining(rowA);
                }
                var rowB = B.Select(r => (k: r, w: Width(r, dB))).ToList();
                rng.Shuffle(rowB);
                KitchenBesideDining(rowB);
                int w = Math.Max(Math.Max(rowA.Sum(x => x.w), rowB.Sum(x => x.w)), Math.Max(need, 4));
                if (Pad(rowA, w, dA, here, 0, r => AccessOf(r) != Access.Private, multi ? host : RoomKind.Unassigned) > 0) return null;
                if (Pad(rowB, w, dB, here, 0, _ => true) > 0) return null;
                var rs = new List<GenRoom>();
                int x = 0, b = -1;
                foreach (var (k, wd) in rowA)
                {
                    rs.Add(new GenRoom { Kind = k, Floor = 0, X0 = x, X1 = x + wd, Z0 = 0, Z1 = dA });
                    // the bay: inside the host, and where the floors above (which start or end at it) still fit
                    if (multi && k == host && b < 0 && wd >= L)
                    {
                        int lo = Math.Max(x, stairsWest ? 0 : need - L), hi = Math.Min(x + wd - L, stairsWest ? w - need : w - L);
                        if (lo <= hi) b = lo + rng.Int(hi - lo + 1);
                    }
                    x += wd;
                }
                if (multi && b < 0) return null;
                x = 0;
                foreach (var (k, wd) in rowB) { rs.Add(new GenRoom { Kind = k, Floor = 0, X0 = x, X1 = x + wd, Z0 = dA, Z1 = dA + dB }); x += wd; }
                return (rs, w, dA + dB, multi ? b : 0);
            }

            // CORRIDOR: a hall along the floor with rooms either side -- and with stairs the bay off its south side, so
            // the hall is the stair room. `safe` puts the bay at the end that always leaves the floors above room to fit.
            (List<GenRoom> Rooms, int W, int D, int Bx0)? Corridor(List<RoomKind> here, bool safe)
            {
                int sS = multi ? dS : 3 + rng.Int(2), sN = multi ? dN : (kind == BuildingKind.Industrial ? 4 : 3 + rng.Int(2));
                var S = new List<(RoomKind k, int w)>(); var N = new List<(RoomKind k, int w)>();
                // (the kitchen and dining room go to one side together, so they can sit side by side)
                foreach (var r in Seeded(here.Where(r => r != RoomKind.DiningRoom || !here.Contains(RoomKind.Kitchen)), sS))
                {
                    var pair = r == RoomKind.Kitchen && here.Contains(RoomKind.DiningRoom) ? new[] { r, RoomKind.DiningRoom } : new[] { r };
                    int lenS = L + S.Sum(x => x.w) + pair.Sum(p => Width(p, sS)), lenN = N.Sum(x => x.w) + pair.Sum(p => Width(p, sN));
                    foreach (var p in pair) if (lenS <= lenN) S.Add((p, Width(p, sS))); else N.Add((p, Width(p, sN)));
                }
                int w = Math.Max(Math.Max(L + S.Sum(x => x.w), N.Sum(x => x.w)), Math.Max(need, 4));
                int aS = Pad(S, w - L, sS, here, 0, _ => true), aN = Pad(N, w, sN, here, 0, _ => true);
                rng.Shuffle(S);
                KitchenBesideDining(S); KitchenBesideDining(N);
                var hall = new GenRoom { Kind = RoomKind.Hall, Floor = 0, X0 = 0, X1 = w, Z0 = sS, Z1 = sS + 1 };
                var rs = new List<GenRoom> { hall };
                int b = 0;
                if (aS > 0 || S.Count == 0)
                {
                    // no south rooms: the whole south side is hall, and the bay anywhere in it the floors above fit
                    hall.More.Add((0, 0, w, sS));
                    if (multi)
                    {
                        int lo = stairsWest ? 0 : need - L, hi = stairsWest ? w - need : w - L;
                        if (lo > hi) return null;
                        b = lo + rng.Int(hi - lo + 1);
                    }
                }
                else
                {
                    // the bay between two of the south rooms: the first split that leaves the floors above room
                    int split = 0;
                    if (multi)
                    {
                        var splits = safe ? new List<int> { stairsWest ? 0 : S.Count } : Enumerable.Range(0, S.Count + 1).ToList();
                        if (!safe) rng.Shuffle(splits);
                        split = -1;
                        foreach (int i in splits)
                        {
                            int at = S.Take(i).Sum(x => x.w);
                            if (stairsWest ? at + need <= w : at + L >= need) { split = i; b = at; break; }
                        }
                        if (split < 0) return null;
                    }
                    int x = 0;
                    for (int i = 0; i <= S.Count; i++)
                    {
                        if (multi && i == split) { hall.More.Add((x, 0, x + L, sS)); x += L; }
                        if (i == S.Count) break;
                        rs.Add(new GenRoom { Kind = S[i].k, Floor = 0, X0 = x, X1 = x + S[i].w, Z0 = 0, Z1 = sS });
                        x += S[i].w;
                    }
                }
                int xn = 0;
                foreach (var (k, wd) in N) { rs.Add(new GenRoom { Kind = k, Floor = 0, X0 = xn, X1 = xn + wd, Z0 = sS + 1, Z1 = sS + 1 + sN }); xn += wd; }
                if (aN > 0) hall.More.Add((0, sS + 1, w, sS + 1 + sN));
                return (rs, w, sS + 1 + sN, b);
            }

            // =========================================================================================================
            // Doors. A front door on the ground floor (an upper floor is entered by its stairs, into its landing hall);
            // a tree of doors through the walk-through rooms, and a second where two of them belong together; then every
            // dead end off a room it may open off. False when a room cannot be reached.
            bool Connect(Grid g, List<GenRoom> rs, int f, GenRoom root, List<GenDoor> doors, (int X0, int Z0, int X1, int Z1) bay)
            {
                var runs = Runs(g);
                var reached = new HashSet<GenRoom>();
                bool InBay(int x, int z) => x >= bay.X0 && x < bay.X1 && z >= bay.Z0 && z < bay.Z1;
                // a door's middle on a run, or NaN: kept off the walls meeting the run, and not opening onto the stairs
                // A door's middle on a run, or NaN: kept off the walls meeting the run, not opening onto the stairs, and
                // -- where the run allows -- not at a corner another door already uses (strawberry: "try to avoid
                // multiple doors on a corner"). `crowded` says the only place left was such a corner.
                float Slot(Run r, out bool crowded)
                {
                    crowded = false;
                    float lo = r.S0 * Cell + g.Meet(r.AlongX, r.Line, r.S0) + HalfDoor;
                    float hi = r.S1 * Cell - g.Meet(r.AlongX, r.Line, r.S1) - HalfDoor;
                    if (lo > hi) return float.NaN;
                    // where it may go: the middle of each cell, and each lattice point inside the run (a door straddling
                    // two cells keeps clear of both their corners, which a cell-middle one in a two-cell wall cannot)
                    var spots = Enumerable.Range(r.S0, r.S1 - r.S0).Select(c => (c + 0.5f) * Cell)
                                          .Concat(Enumerable.Range(r.S0 + 1, Math.Max(0, r.S1 - r.S0 - 1)).Select(c => c * Cell)).ToList();
                    rng.Shuffle(spots);
                    float fallback = float.NaN;
                    foreach (float spot in spots)
                    {
                        float m = Math.Clamp(spot, lo, hi);
                        bool clear = true;
                        for (int s = (int)MathF.Floor((m - HalfDoor) / Cell); s <= (int)MathF.Floor((m + HalfDoor - 0.01f) / Cell) && clear; s++)
                            clear = r.AlongX ? !InBay(s, r.Line - 1) && !InBay(s, r.Line) : !InBay(r.Line - 1, s) && !InBay(r.Line, s);
                        if (!clear) continue;
                        if (!doors.Any(d => SharesCorner(r.AlongX, r.Line, m, d.AlongX, d.Line, d.At))) return m;
                        if (float.IsNaN(fallback)) fallback = m;
                    }
                    crowded = !float.IsNaN(fallback);
                    return fallback;
                }
                GenDoor Door(GenRoom a, GenRoom b) => Door2(a, b, out _);
                GenDoor Door2(GenRoom a, GenRoom b, out bool crowdedOut)
                {
                    crowdedOut = false;
                    var mine = runs.Where(r => (r.A == a && r.B == b) || (r.A == b && r.B == a)).ToList();
                    rng.Shuffle(mine);
                    // a shop's street door goes in its longest outside wall, the shopfront
                    if (b == null && a.Kind == RoomKind.Shop) mine = mine.OrderByDescending(r => r.S1 - r.S0).ToList();
                    GenDoor second = null;
                    foreach (var r in mine)
                        if (Slot(r, out bool crowded) is float m && !float.IsNaN(m))
                        {
                            var d = new GenDoor { Floor = f, A = a, B = b, AlongX = r.AlongX, Line = r.Line, At = m };
                            if (!crowded) return d;
                            second ??= d;
                        }
                    crowdedOut = second != null;
                    return second;
                }
                List<GenRoom> Next(GenRoom a) => runs.Where(r => r.A == a || r.B == a).Select(r => r.A == a ? r.B : r.A).Where(b => b != null).Distinct().ToList();
                bool Doored(GenRoom a, GenRoom b) => doors.Any(d => (d.A == a && d.B == b) || (d.A == b && d.B == a));

                if (root == null)
                {
                    // the front door: into the most entrance-like walk-through room with an outside wall to take it
                    GenDoor front = null;
                    foreach (var k in EntranceOrder(kind))
                    {
                        foreach (var r in rs.Where(r => r.Kind == k).OrderBy(_ => rng.Next()).ToList())
                            if ((front = Door(r, null)) != null) break;
                        if (front != null) break;
                    }
                    if (front == null) return false;
                    front.Role = DoorRole.Front;
                    doors.Add(front);
                    root = front.A;
                }
                reached.Add(root);
                // walk-through rooms that belong together get their door first, so it has the pick of the wall: a kitchen
                // and the dining room beside it always, the others usually -- and not where it would crowd a corner
                // (the kitchen-dining door first of all: its wall may have one spot, and the optional ones then keep off it)
                var together = rs.Where(r => AccessOf(r.Kind) == Access.Through)
                                 .SelectMany(a => Next(a).Where(b => AccessOf(b.Kind) == Access.Through && rs.IndexOf(b) > rs.IndexOf(a) && Together(a.Kind, b.Kind)).Select(b => (a, b)))
                                 .OrderBy(p => Pair(p.a.Kind, p.b.Kind, RoomKind.Kitchen, RoomKind.DiningRoom) ? 0 : 1).ToList();
                foreach (var (a, b) in together)
                {
                    bool kd = Pair(a.Kind, b.Kind, RoomKind.Kitchen, RoomKind.DiningRoom);
                    if (!Doored(a, b) && (kd || rng.Chance(85)) && Door2(a, b, out bool c) is GenDoor d && (kd || !c)) doors.Add(d);
                }
                // then a tree through the rest, from the way in, using those doors where they already join two rooms --
                // first with only doors that crowd no corner, then, for whatever that left unreached, any door at all
                foreach (bool anyDoor in new[] { false, true })
                {
                    var q = new Queue<GenRoom>(reached);
                    while (q.Count > 0)
                    {
                        var a = q.Dequeue();
                        var nb = Next(a).Where(b => AccessOf(b.Kind) == Access.Through && !reached.Contains(b)).ToList();
                        rng.Shuffle(nb);
                        foreach (var b in nb.OrderBy(b => Doored(a, b) ? 0 : 1))
                        {
                            if (reached.Contains(b)) continue;
                            if (Doored(a, b)) { reached.Add(b); q.Enqueue(b); }
                            else if (Door2(a, b, out bool c) is GenDoor d && (anyDoor || !c)) { doors.Add(d); reached.Add(b); q.Enqueue(b); }
                        }
                    }
                }
                if (rs.Any(r => AccessOf(r.Kind) == Access.Through && !reached.Contains(r))) return false;
                // and now and then a second way between two others, where it crowds no corner
                foreach (var a in rs.Where(r => AccessOf(r.Kind) == Access.Through).ToList())
                    foreach (var b in Next(a).Where(b => AccessOf(b.Kind) == Access.Through && rs.IndexOf(b) > rs.IndexOf(a)).ToList())
                        if (!Doored(a, b) && rng.Chance(20) && Door2(a, b, out bool c) is GenDoor d && !c) doors.Add(d);
                // the dead ends, each off a room already reached that it may open off: a closet off a bedroom by
                // preference, a bathroom off a walk-through room unless it rolls an en-suite, the rest off what they face
                var left = rs.Where(r => !reached.Contains(r)).ToList();
                for (bool progress = true; left.Count > 0 && progress;)
                {
                    progress = false;
                    foreach (var r in left.ToList())
                    {
                        var parents = Next(r).Where(p => reached.Contains(p) && MayOpenOff(r.Kind, p.Kind)).ToList();
                        if (parents.Count == 0) continue;
                        // a closet (or a bathroom that rolls an en-suite) off a bedroom or office first; then the
                        // hall, since a bathroom off the hall beats one off the kitchen; then whatever it faces
                        bool suite = r.Kind == RoomKind.Closet || (r.Kind == RoomKind.Bathroom && rng.Chance(30));
                        rng.Shuffle(parents);
                        int Prefer(GenRoom p) => suite && AccessOf(p.Kind) != Access.Through ? 0 : p.Kind == RoomKind.Hall ? 1 : AccessOf(p.Kind) == Access.Through ? 2 : 3;
                        // the first parent (in that order) with a spot that crowds no corner; failing that, the first at all
                        GenDoor pick = null, crowdedPick = null;
                        foreach (var p in parents.OrderBy(Prefer).ToList())
                        {
                            var d = Door2(p, r, out bool c);
                            if (d == null) continue;
                            if (!c) { pick = d; break; }
                            crowdedPick ??= d;
                        }
                        pick ??= crowdedPick;
                        if (pick != null) { doors.Add(pick); reached.Add(r); left.Remove(r); progress = true; }
                    }
                }
                if (left.Count > 0) return false;
                // a back door, often: out of a room that suits one (BackDoorOrder), in another wall than the front's --
                // the opposite one by preference, which is what makes it the back
                if (f == 0 && rng.Chance(60))
                {
                    var front = doors.First(d => d.Role == DoorRole.Front);
                    int Side(bool alongX, int line) => alongX ? (line == 0 ? 0 : 2) : (line == g.Ux0 ? 3 : 1);
                    int fs = Side(front.AlongX, front.Line);
                    var order = BackDoorOrder(kind);
                    foreach (var r in rs.Where(r => order.Contains(r.Kind)).OrderBy(r => Array.IndexOf(order, r.Kind)).ThenBy(_ => rng.Next()).ToList())
                    {
                        var outs = runs.Where(u => u.B == null && u.A == r && Side(u.AlongX, u.Line) != fs).ToList();
                        rng.Shuffle(outs);
                        GenDoor back = null;
                        foreach (var u in outs.OrderBy(u => Side(u.AlongX, u.Line) == (fs + 2) % 4 ? 0 : 1))
                            if (Slot(u, out bool crowded) is float m && !float.IsNaN(m) && !crowded)
                            { back = new GenDoor { Floor = f, A = r, B = null, AlongX = u.AlongX, Line = u.Line, At = m, Role = DoorRole.Back }; break; }
                        if (back != null) { doors.Add(back); break; }
                    }
                }
                // last, a door that crowds a corner moves to a clean spot on the same wall if it has one: placed early it
                // had the pick, and a door placed later may have had only that corner
                for (int pass = 0; pass < 2; pass++)
                    foreach (var d in doors.ToList())
                    {
                        if (!doors.Any(o => o != d && SharesCorner(d.AlongX, d.Line, d.At, o.AlongX, o.Line, o.At))) continue;
                        int at = doors.IndexOf(d);
                        doors.RemoveAt(at);
                        // (the front and back doors stay put: they were placed clear, and the ones crowding them move)
                        if (d.Role != DoorRole.Inside) continue;
                        var moved = Door2(d.A, d.B, out bool still);
                        doors.Insert(at, moved != null && !still ? moved : d);
                    }
                return true;
            }
        }

        static bool Pair(RoomKind a, RoomKind b, RoomKind x, RoomKind y) => (a == x && b == y) || (a == y && b == x);

        /// <summary>Do two doors crowd the same corner -- both within a metre of one lattice point (where walls meet)? Two
        /// doors on perpendicular walls by the same corner, or side by side either side of a partition's end.</summary>
        public static bool SharesCorner(bool alongX1, int line1, float at1, bool alongX2, int line2, float at2)
        {
            const float Near = 1.0f;
            IEnumerable<(int X, int Z)> Points(bool alongX, int line, float at)
            {
                for (int s = (int)MathF.Ceiling((at - HalfDoor - Near) / Cell); s * Cell <= at + HalfDoor + Near; s++)
                    yield return alongX ? (s, line) : (line, s);
            }
            return Points(alongX1, line1, at1).Intersect(Points(alongX2, line2, at2)).Any();
        }

        /// <summary>Is the living room (what is left of it beside the stairs) at least as big as the dining room and the
        /// kitchen on its floor?</summary>
        static bool Proportioned(List<GenRoom> rs, (int X0, int Z0, int X1, int Z1) bay)
        {
            var living = rs.FirstOrDefault(r => r.Kind == RoomKind.LivingRoom);
            if (living == null) return true;
            int usable = living.Cells - (bay.X1 > bay.X0 && living.HasCell(bay.X0, bay.Z0) ? (bay.X1 - bay.X0) * (bay.Z1 - bay.Z0) : 0);
            return rs.Where(r => r.Kind == RoomKind.DiningRoom || r.Kind == RoomKind.Kitchen).All(r => r.Cells <= usable);
        }

        /// <summary>Is the dining room (if any) beside the kitchen (if any) on this floor, or facing it across a hall?</summary>
        static bool KitchenNearDining(Grid g, List<GenRoom> rs)
        {
            var k = rs.FirstOrDefault(r => r.Kind == RoomKind.Kitchen);
            var d = rs.FirstOrDefault(r => r.Kind == RoomKind.DiningRoom);
            if (k == null || d == null) return true;
            if (Runs(g).Any(r => (r.A == k && r.B == d) || (r.A == d && r.B == k))) return true;
            // facing: both on a hall, overlapping along it by a cell or more
            var halls = rs.Where(r => r.Kind == RoomKind.Hall).ToList();
            bool OnHall(GenRoom x) => Runs(g).Any(r => (r.A == x && halls.Contains(r.B)) || (r.B == x && halls.Contains(r.A)));
            return OnHall(k) && OnHall(d) && Math.Min(k.X1, d.X1) - Math.Max(k.X0, d.X0) >= 1;
        }

        /// <summary>Two walk-through rooms that a house would join with a door even when another way round exists.</summary>
        static bool Together(RoomKind a, RoomKind b)
            => (a, b) is (RoomKind.Kitchen, RoomKind.DiningRoom) or (RoomKind.DiningRoom, RoomKind.Kitchen)
                      or (RoomKind.LivingRoom, RoomKind.DiningRoom) or (RoomKind.DiningRoom, RoomKind.LivingRoom)
                      or (RoomKind.Kitchen, RoomKind.LivingRoom) or (RoomKind.LivingRoom, RoomKind.Kitchen);

        /// <summary>Every run where two owners face each other on a floor: room and room, or room and outside (B null).</summary>
        static List<Run> Runs(Grid g)
        {
            var runs = new List<Run>();
            void Scan(bool alongX, int line, int from, int to)
            {
                int s = from;
                while (s < to)
                {
                    var (a, b) = g.Across(alongX, line, s);
                    if (a == b) { s++; continue; }
                    int s0 = s;
                    while (s < to && g.Across(alongX, line, s) == (a, b)) s++;
                    runs.Add(a == null ? new Run { AlongX = alongX, Line = line, S0 = s0, S1 = s, A = b, B = null }
                                       : new Run { AlongX = alongX, Line = line, S0 = s0, S1 = s, A = a, B = b });
                }
            }
            for (int z = 0; z <= g.D; z++) Scan(true, z, g.Ux0, g.Ux1);
            for (int x = g.Ux0; x <= g.Ux1; x++) Scan(false, x, 0, g.D);
            return runs;
        }

        // =============================================================================================================
        // Walls, openings, slabs, roofs, stairs

        /// <summary>A wall whose mid-plane runs from (x0, z0) to (x1, z1), carried on past both ends by `extend` (the
        /// corner solve). The editor's frame: it runs along local +X (yaw 0 = +X, 90 = -Z, 180 = -X, 270 = +Z), its
        /// origin the start corner at its base, local +Z out of its front face.</summary>
        static WallPlan Wall(float x0, float z0, float x1, float z1, float y, float thickness, float extend = 0f)
        {
            float dx = x1 - x0, dz = z1 - z0, len = MathF.Sqrt(dx * dx + dz * dz);
            float yaw = MathF.Round(MathF.Atan2(-dz, dx) * 180f / MathF.PI);
            yaw = yaw < 0 ? yaw + 360f : yaw + 0f;   // (+ 0 turns the -0 that Atan2(-0, x) rounds to into 0)
            return new WallPlan { X = x0 - dx / len * extend, Y = y, Z = z0 - dz / len * extend, Yaw = yaw, Length = len + 2 * extend,
                                  Height = Storey, Thickness = thickness, Kind = SurfaceKind.Wall };
        }

        static WallOpening Make(int arch, float uCentre, float width)
        {
            var a = ArchSizes[arch];
            float h = Math.Min(a.H, OpeningTop - a.Sill);
            var o = new WallOpening(uCentre - width * 0.5f, a.Sill, width, h, 999f, arch) { Glazed = a.Glazed };
            if (arch == ArchDoor) o.DoorProp = DefaultDoor;
            return o;
        }

        /// <summary>Openings spread evenly over [lo, hi] (metres along the side), one slot each. A window too wide for its
        /// slot is narrowed (to MinWindow at the least); a door, garage door or vent that does not fit is left out.</summary>
        static List<(int Arch, float At, float Width)> Spread(float lo, float hi, IList<int> arches)
        {
            var outp = new List<(int, float, float)>();
            int n = arches.Count;
            if (n == 0 || hi - lo <= 0) return outp;
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
                outp.Add((arch, lo + slot * (i + 0.5f), width));
            }
            return outp;
        }

        static void BuildWalls(BuildingGenResult res, List<Grid> grids, int k1, int L)
        {
            int D = res.CellsZ, floors = res.Floors;
            float Dm = D * Cell, he = Ext * 0.5f;
            var bay = res.Bay;
            var tops = new List<WallPlan>();
            // A lower floor's own roof over [xa, xb] (strawberry: "slope from the wall off the edge"): a SHED leaning on
            // the wall of the floor above, rising from its eave at the open end to that wall -- RoofShapes' Shed, so it is
            // the same roof the editor's Shed button builds. Pitched no steeper than lets it meet that wall under its
            // head. Closed as the editor closes one (RoofShapes.ShedInfill): the open end's wall gets a band up to the
            // eave's underside, and the two side walls a band and a right triangle each over the stretch it covers.
            void Lower(float xa, float xb, WallPlan endWall, bool westEnd, float head)
            {
                if (res.Roof == RoofKind.Flat) { res.Walls.Add(SlabPlan(xa, xb, -he, Dm + he, head + Slab, SurfaceKind.Roof)); return; }
                float minX = westEnd ? xa - RoofOverhang : xa, maxX = westEnd ? xb : xb + RoofOverhang;
                float run = maxX - minX;
                float pitch = MathF.Min(res.RoofPitch, MathF.Atan((Storey - 0.6f) / run) * 180f / MathF.PI);
                var rs = new RoofSpec
                {
                    Kind = RoofKind.Shed, High = westEnd ? RoofSide.East : RoofSide.West, MinX = minX, MaxX = maxX,
                    MinZ = -he - RoofOverhang, MaxZ = Dm + he + RoofOverhang, TopY = head, PitchDeg = pitch, Thickness = Slab,
                };
                foreach (var pl in RoofShapes.Planes(rs))
                    res.Walls.Add(new WallPlan { X = pl.X, Y = pl.Y, Z = pl.Z, Yaw = pl.YawDeg, Pitch = pl.PitchDeg, Length = pl.Length, Height = pl.Height,
                                                 Thickness = Slab, Kind = SurfaceKind.Roof });
                void Close(float x0, float z0, float x1, float z1, float yaw)
                {
                    var (band, tri, highAtEnd) = RoofShapes.ShedInfill(rs, x0, z0, x1, z1);
                    float len = MathF.Abs(x1 - x0) + MathF.Abs(z1 - z0);
                    if (band > 0.01f)
                        res.Walls.Add(new WallPlan { X = x0, Y = head, Z = z0, Yaw = yaw, Length = len, Height = band, Thickness = Ext, Kind = SurfaceKind.Wall });
                    if (tri > 0.01f)
                        res.Walls.Add(new WallPlan { X = x0, Y = head + band, Z = z0, Yaw = yaw, Length = len, Height = tri, Thickness = Ext, Kind = SurfaceKind.Wall,
                                                     InsetL1 = highAtEnd ? len : 0f, InsetR1 = highAtEnd ? 0f : len });
                }
                var e1 = (X: endWall.X + MathF.Cos(endWall.Yaw * MathF.PI / 180f) * endWall.Length, Z: endWall.Z - MathF.Sin(endWall.Yaw * MathF.PI / 180f) * endWall.Length);
                Close(endWall.X, endWall.Z, e1.X, e1.Z, endWall.Yaw);   // the open end
                Close(xa, Dm, xb, Dm, 0f);                               // the north side, running +X
                Close(xb, 0f, xa, 0f, 180f);                             // the south side, running -X
            }
            for (int f = 0; f < floors; f++)
            {
                var g = grids[f];
                float y = f * Storey;
                var (ux0, ux1) = res.Footprints[f];
                float x0m = ux0 * Cell, x1m = ux1 * Cell;
                // the four outside walls, anticlockwise from above as the editor lays a room's, front faces out
                var north = Wall(x0m, Dm, x1m, Dm, y, Ext, he);   // yaw 0, runs +X
                var east = Wall(x1m, Dm, x1m, 0, y, Ext, he);      // yaw 90, runs -Z
                var south = Wall(x1m, 0, x0m, 0, y, Ext, he);      // yaw 180, runs -X
                var west = Wall(x0m, 0, x0m, Dm, y, Ext, he);      // yaw 270, runs +Z
                // an outside line's wall, and a point on it (metres along +X or +Z) as that wall's u
                WallPlan Side(bool alongX, int line) => alongX ? (line == 0 ? south : north) : (line == ux0 ? west : east);
                float U(WallPlan w, float at) => w == north ? he + at - x0m : w == south ? he + x1m - at : w == west ? he + at : he + Dm - at;

                // the interior walls: maximal runs of owner changes along each lattice line
                var inner = new List<(bool AlongX, int Line, int S0, int S1, WallPlan W)>();
                for (int z = 1; z < D; z++)
                    for (int x = ux0; x < ux1;)
                    {
                        if (g.H(x, z) <= 0f) { x++; continue; }
                        int s0 = x;
                        while (x < ux1 && g.H(x, z) > 0f) x++;
                        inner.Add((true, z, s0, x, Wall(s0 * Cell, z * Cell, x * Cell, z * Cell, y, Inner)));
                    }
                for (int x = ux0 + 1; x < ux1; x++)
                    for (int z = 0; z < D;)
                    {
                        if (g.V(x, z) <= 0f) { z++; continue; }
                        int s0 = z;
                        while (z < D && g.V(x, z) > 0f) z++;
                        inner.Add((false, x, s0, z, Wall(x * Cell, s0 * Cell, x * Cell, z * Cell, y, Inner)));
                    }

                // the doors
                var outDoors = new List<(bool AlongX, int Line, float At)>();
                foreach (var d in res.Doors.Where(d => d.Floor == f))
                {
                    if (d.B == null)
                    {
                        var w = Side(d.AlongX, d.Line);
                        w.Openings.Add(Make(ArchDoor, U(w, d.At), ArchSizes[ArchDoor].W));
                        outDoors.Add((d.AlongX, d.Line, d.At));
                        continue;
                    }
                    var iw = inner.First(q => q.AlongX == d.AlongX && q.Line == d.Line && q.S0 * Cell <= d.At && d.At <= q.S1 * Cell);
                    iw.W.Openings.Add(Make(ArchDoor, d.At - iw.S0 * Cell, ArchSizes[ArchDoor].W));
                }

                // what each room has in its outside walls
                bool flightAgainstSouth = f < floors - 1 && (k1 == 1 || f % 2 == 0);
                // an end wall with the floor below's roof leaning on it gets no windows: the roof is in front of them
                bool westUnder = f > 0 && res.Roof != RoofKind.Flat && res.Footprints[f - 1].X0 < ux0;
                bool eastUnder = f > 0 && res.Roof != RoofKind.Flat && res.Footprints[f - 1].X1 > ux1;
                var outside = Runs(g).Where(r => r.B == null).ToList();
                foreach (var run in outside)
                {
                    if (!run.AlongX && ((run.Line == ux0 && westUnder) || (run.Line == ux1 && eastUnder))) continue;
                    var room = run.A;
                    var w = Side(run.AlongX, run.Line);
                    float lo = run.S0 * Cell + g.Meet(run.AlongX, run.Line, run.S0), hi = run.S1 * Cell - g.Meet(run.AlongX, run.Line, run.S1);
                    // the free stretches of this run: not where a door went, not behind the flight
                    var cuts = outDoors.Where(o => o.AlongX == run.AlongX && o.Line == run.Line && o.At > lo && o.At < hi)
                                       .Select(o => (o.At - HalfDoor - 0.3f, o.At + HalfDoor + 0.3f)).ToList();
                    if (flightAgainstSouth && run.AlongX && run.Line == 0) cuts.Add((bay.X0 * Cell, bay.X1 * Cell));
                    var free = new List<(float A, float B)> { (lo, hi) };
                    foreach (var (c0, c1) in cuts)
                        free = free.SelectMany(s => s.B <= c0 || s.A >= c1 ? new[] { s } : new (float A, float B)[] { (s.A, Math.Min(s.B, c0)), (Math.Max(s.A, c1), s.B) })
                                   .Where(s => s.B - s.A > 0.1f).ToList();
                    var mine = outside.Where(r => r.A == room && (r.AlongX || !((r.Line == ux0 && westUnder) || (r.Line == ux1 && eastUnder))))
                                      .OrderByDescending(r => r.S1 - r.S0).ThenBy(r => r.AlongX ? 0 : 1).First();
                    bool longest = mine.AlongX == run.AlongX && mine.Line == run.Line && mine.S0 == run.S0;
                    var arches = new List<(int Arch, float At, float Width)>();
                    // the vehicle door first, on a garage's or warehouse's longest outside run, then windows round it
                    if ((room.Kind == RoomKind.Garage || room.Kind == RoomKind.Warehouse) && f == 0 && longest && free.Count > 0)
                    {
                        var span = free.OrderByDescending(s => s.B - s.A).First();
                        float gw = ArchSizes[ArchGarage].W;
                        if (span.B - span.A >= gw)
                        {
                            // the middle of the span, or the nearest spot to it that keeps off the lattice points a door
                            // beside it is near (a man door and the vehicle door either side of one point crowd it)
                            IEnumerable<int> Near(float at, float half)
                            {
                                for (int c = (int)MathF.Ceiling((at - half - 1f) / Cell); c * Cell <= at + half + 1f; c++) yield return c;
                            }
                            bool Crowds(float at) => outDoors.Any(o => o.AlongX == run.AlongX && o.Line == run.Line && Near(at, gw * 0.5f).Intersect(Near(o.At, HalfDoor)).Any());
                            float mid = (span.A + span.B) * 0.5f, m = mid;
                            for (int k = 0; k * 0.25f <= (span.B - span.A - gw) * 0.5f; k++)
                                if (!Crowds(mid - k * 0.25f)) { m = mid - k * 0.25f; break; }
                                else if (!Crowds(mid + k * 0.25f)) { m = mid + k * 0.25f; break; }
                            arches.Add((ArchGarage, m, gw));
                            free = free.SelectMany(s => s == span ? new (float A, float B)[] { (s.A, m - gw * 0.5f - 0.3f), (m + gw * 0.5f + 0.3f, s.B) } : new[] { s })
                                       .Where(s => s.B - s.A > 0.1f).ToList();
                        }
                    }
                    int win = room.Kind switch
                    {
                        RoomKind.Hall or RoomKind.Stairwell => ArchWindow,
                        RoomKind.Shop => ArchTallWindow,
                        RoomKind.Garage or RoomKind.Warehouse => ArchWindow,
                        _ => TraitOf(room.Kind, res.Kind).Window,
                    };
                    if (win == ArchGarage) win = ArchWindow;
                    if (win == ArchVent && !longest) win = -1;   // one vent is enough
                    if (win >= 0)
                        foreach (var (s0, s1) in free)
                        {
                            int n = win == ArchVent || room.Circulation ? 1 : Math.Max(1, (int)((s1 - s0) / (2 * Cell)));
                            arches.AddRange(Spread(s0, s1, Enumerable.Repeat(win, n).ToList()));
                        }
                    foreach (var (arch, at, wd) in arches) w.Openings.Add(Make(arch, U(w, at), wd));
                }

                res.Walls.Add(north); res.Walls.Add(east); res.Walls.Add(south); res.Walls.Add(west);
                if (f == floors - 1) tops.AddRange(new[] { north, east, south, west });
                if (f == 0)   // footings under the outside walls, as the editor's auto-fit lays them
                    foreach (var w in new[] { north, east, south, west })
                        res.Walls.Add(new WallPlan { X = w.X, Y = -WallOpenings.FoundationDepth, Z = w.Z, Yaw = w.Yaw, Length = w.Length,
                                                     Height = WallOpenings.FoundationDepth, Thickness = w.Thickness, Kind = SurfaceKind.Foundation });
                foreach (var q in inner) res.Walls.Add(q.W);

                // the floor, its top at the storey's base, out to the outside walls' outer faces (the editor's slab)
                res.Walls.Add(SlabPlan(x0m - he, x1m + he, -he, Dm + he, y, SurfaceKind.Floor));
                // a roof on whatever of this floor the one above leaves uncovered: flat on a business; on a house a gable of
                // its own, its ridge running into the wall of the floor above (strawberry: "any reason why the 1st floor
                // section of roof is flat instead of gable? it can be a lower angle gable if required")
                if (f + 1 < floors)
                {
                    var (a0, a1) = res.Footprints[f + 1];
                    float wallHead = (f + 1) * Storey;
                    if (a0 > ux0) Lower(x0m - he, a0 * Cell - he, west, true, wallHead);
                    if (a1 < ux1) Lower(a1 * Cell + he, x1m + he, east, false, wallHead);
                }
            }

            // ---- the top roof: a house's gable (the editor's RoofShapes, eaves past the walls by its overhang, the walls
            // across the ridge raised into gable ends), a business's flat slab
            var (t0, t1) = res.Footprints[floors - 1];
            float head = floors * Storey;
            if (res.Roof != RoofKind.Flat)
            {
                var rs = new RoofSpec
                {
                    Kind = res.Roof, MinX = t0 * Cell - he - RoofOverhang, MaxX = t1 * Cell + he + RoofOverhang,
                    MinZ = -he - RoofOverhang, MaxZ = Dm + he + RoofOverhang, TopY = head, PitchDeg = res.RoofPitch, Thickness = Slab,
                };
                foreach (var p in RoofShapes.Planes(rs))
                    res.Walls.Add(new WallPlan { X = p.X, Y = p.Y, Z = p.Z, Yaw = p.YawDeg, Pitch = p.PitchDeg, Length = p.Length, Height = p.Height,
                                                 Thickness = Slab, Kind = SurfaceKind.Roof, InsetL0 = p.InsetL0, InsetL1 = p.InsetL1, InsetR0 = p.InsetR0, InsetR1 = p.InsetR1 });
                foreach (var w in tops)
                {
                    if (!RoofShapes.WallGetsGable(rs, w.Yaw == 0f || w.Yaw == 180f)) continue;
                    float tri = RoofShapes.GableRiseForWall(rs, w.Length), band = RoofShapes.GableBandForWall(rs, w.Length);
                    // as EditorBuildings.PlaceRoof does it: a straight band where the roof reaches past the wall, the
                    // triangle on top of that
                    if (band > 0.01f)
                        res.Walls.Add(new WallPlan { X = w.X, Y = w.Y + w.Height, Z = w.Z, Yaw = w.Yaw, Length = w.Length, Height = band,
                                                     Thickness = w.Thickness, Kind = SurfaceKind.Wall, GableRise = tri });
                    else w.GableRise = tri;
                }
            }
            else res.Walls.Add(SlabPlan(t0 * Cell - he, t1 * Cell + he, -he, Dm + he, head + Slab, SurfaceKind.Roof));

            // ---- the stairs: k1 flight rows against the south wall, centred along the bay. One flight climbs east up row
            // 0; with alternating rows, odd floors climb west up row 1, so each lands beside the next one's foot.
            if (floors < 2) return;
            float flight = WallOpenings.StairDefaultRun(Storey), sw = StairWidth;
            float foot = bay.X0 * Cell + (L * Cell - flight) * 0.5f;
            for (int f = 0; f + 1 < floors; f++)
            {
                if (k1 == 1 || f % 2 == 0)   // yaw 270: climbs +X, width along +Z from z 0.6 -- its hole stops at the wall's face
                    res.Stairs.Add(new StairPlan { X = foot, Y = f * Storey, Z = he + WallOpenings.StairHoleMargin, Yaw = 270f, Run = flight, Width = sw, Floor = f });
                else                         // yaw 90: climbs -X, width along -Z, z 5.7 down to 3.3
                    res.Stairs.Add(new StairPlan { X = foot + flight, Y = f * Storey, Z = Cell + 0.3f + sw, Yaw = 90f, Run = flight, Width = sw, Floor = f });
            }
        }

        /// <summary>A flat slab over [x0, x1] x [z0, z1] with its top at `top`: the editor's frame for one -- origin at
        /// (minX, centre height, maxZ), run along +X, depth along -Z, pitched -90.</summary>
        static WallPlan SlabPlan(float x0, float x1, float z0, float z1, float top, SurfaceKind kind)
            => new WallPlan { X = x0, Y = top - Slab * 0.5f, Z = z1, Yaw = 0f, Pitch = -90f, Length = x1 - x0, Height = z1 - z0, Thickness = Slab, Kind = kind };
    }
}
