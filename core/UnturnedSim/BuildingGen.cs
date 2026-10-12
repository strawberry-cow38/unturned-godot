using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>The sides of a building's plan: north is +Z (RoofSide's sense), south the z = 0 wall, west x = 0, east
    /// the far end along X. A set of them, or None for "any".</summary>
    [Flags]
    public enum Sides { None = 0, North = 1, East = 2, South = 4, West = 8 }

    /// <summary>A ground floor's outline: a rectangle; an L, one corner of it cut away; or a T, both corners of one side
    /// (strawberry: "does the layout support non-square footprints for floorplans?"). Auto for a seeded choice.</summary>
    public enum FootprintShape { Auto, Rect, L, T }

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
        /// <summary>The ground floor's footprint in 3 m cells, along X and along Z; 0 for as big as its rooms make it
        /// (strawberry: "allow specifying a footprint size for the building along with its seed"). Longer than the rooms
        /// need, they grow and more join; shorter or shallower than they can be laid in, it comes out bigger, with a note.</summary>
        public int Width, Depth;
        /// <summary>The side(s) the front door may be in, and the back door's (strawberry: "allow specifying front and back
        /// door side requirements along with the seed too"); None for any. A back door asked for is always made.</summary>
        public Sides FrontSide, BackSide;
        /// <summary>Sides with no windows on any floor ("as well as specifying which sides cant have windows").</summary>
        public Sides NoWindows;
        /// <summary>The outline: Auto is mostly rectangles, now and then an L or a T. One storey only for now -- a building
        /// of more floors is a rectangle, with a note when an L or a T was asked for.</summary>
        public FootprintShape Shape;
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
        /// <summary>An open doorway, no door hung in it -- between two rooms you walk through (strawberry: "some doorways
        /// may not require doors, consider this, some may fit with a wide doorframe opening").</summary>
        public bool Open;
        /// <summary>An open one that would rather be wide: BuildWalls widens it as far as its wall lets it.</summary>
        public bool Wide;
        /// <summary>How wide its opening came out, metres.</summary>
        public float Width = BuildingGen.ArchSizes[BuildingGen.ArchDoor].W;
        /// <summary>The middle of its opening in plan, metres.</summary>
        public (float X, float Z) Middle => AlongX ? (At, Line * BuildingGen.Cell) : (Line * BuildingGen.Cell, At);
        public override string ToString() => $"{A}->{(B == null ? "outside" : B.ToString())} at {(AlongX ? "z" : "x")}={Line} {At:0.0}{(Role != DoorRole.Inside ? " " + Role : "")}";
    }

    /// <summary>How a building's stairs climb a storey: one straight flight; two half flights turning back on themselves
    /// at a landing (a switchback, 180 degrees); or two half flights turning a corner at one (a quarter turn, 90).</summary>
    public enum StairShape { Straight, Switchback, Quarter }

    /// <summary>A flight of stairs as the editor lays one (EditorBuildings.AddStairs): its foot corner, the way it climbs
    /// (yaw; it recedes along local -Z and is Width wide along local +X), its run, from storey Floor to the next -- or,
    /// for each half of a switchback, from Y up Rise to the landing or from the landing up to the next storey.</summary>
    public struct StairPlan
    {
        public float X, Y, Z, Yaw, Run, Width; public int Floor;
        /// <summary>How far it climbs: a storey, or half of one for a switchback's flights. 0 reads as a storey.</summary>
        public float Rise;
        /// <summary>Unit vectors in plan: the way it climbs, and the way its width runs (AddStairs' back and right).</summary>
        public (float X, float Z) Back { get { var (c, s) = PlanTrig.CosSin(Yaw); return (-s, -c); } }
        public (float X, float Z) Right { get { var (c, s) = PlanTrig.CosSin(Yaw); return (c, -s); } }
    }

    public sealed class BuildingGenResult
    {
        public BuildingKind Kind;
        public int Floors, CellsX, CellsZ;
        /// <summary>Each floor's extent along X in cells, [X0, X1); every floor is the full CellsZ deep -- but for an L's or
        /// a T's corner(s), which Wing gives. An upper floor can be shorter than the one below, never longer or outside it.</summary>
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
        /// <summary>What stands in the rooms (BuildingGen.Furnish): retail props by name.</summary>
        public List<FurniturePlan> Furniture = new List<FurniturePlan>();
        public IEnumerable<PropDesignation> Props => Furniture.Select(m => new PropDesignation(m.Name, m.X, m.Y, m.Z, m.Yaw));
        /// <summary>The marked doors: the front one always, the back one when there is one.</summary>
        public GenDoor Front => Doors.FirstOrDefault(d => d.Role == DoorRole.Front);
        public GenDoor Back => Doors.FirstOrDefault(d => d.Role == DoorRole.Back);
        public IEnumerable<DoorDesignation> DoorMarks => Doors.Where(d => d.Role != DoorRole.Inside).Select(d => new DoorDesignation(d.Role, d.Middle.X, d.Middle.Z, d.Floor));
        /// <summary>The stair bay in cells, the same on every floor: the flight rows and the row of floor beside them.
        /// All zero for one floor.</summary>
        public (int X0, int Z0, int X1, int Z1) Bay;
        /// <summary>An L or a T: its wing in cells, the part narrower than the rest -- the rest the full CellsX long, the
        /// wing on its north side as laid out (south, once turned half round). All zero for a rectangle.</summary>
        public (int X0, int Z0, int X1, int Z1) Wing;
        public FootprintShape Shape = FootprintShape.Rect;
        /// <summary>How its stairs climb: one straight flight (a bay four cells by two or three rows); a switchback, two
        /// half flights side by side turning back at a landing (three cells by two rows); or a quarter turn, a half flight
        /// along the wall to a corner landing and one away from it (three cells by three rows).</summary>
        public StairShape StairShape;
        public bool Switchback => StairShape == StairShape.Switchback;
        /// <summary>A quarter turn's landing is at the bay's west end (the turn the other way): the corner it is built into
        /// is the one with a wall, which is the end the floors above start from.</summary>
        public bool QuarterWest;
        /// <summary>Its kitchen has the dining table in it, and it has no dining room.</summary>
        public bool KitchenDiner;
        /// <summary>Sides with no windows, in the plan as laid out (before any turn to put the front where it was asked).</summary>
        public Sides NoWindowSides;
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
    /// One storey may be an L or a T (strawberry: "does the layout support non-square footprints"): its north row of
    /// rooms comes up short of the south one at an end or both, the outside walls follow the outline, and a house's roof
    /// is a cross gable (CrossGable).
    ///
    /// Houses get a gable or a hip roof (the editor's own RoofShapes), businesses a flat one; a lower floor's part that
    /// the floor above does not cover gets a roof of its own -- a shed leaning on the upper wall for a house, flat for a
    /// business. Everything is on the editor's 3 m lattice, a storey is the wall
    /// height, the outside walls come corner-solved (the editor's SolveCorners lengthens a wall from its origin WITHOUT
    /// moving its openings, so it must find nothing to do here), and the openings are the editor's archetypes.
    /// </summary>
    public static partial class BuildingGen
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
        /// <summary>A floor whose rooms want this many cells or fewer is small: it gets switchback stairs. About the median
        /// of the houses and shops of two floors or more; a straight flight's bay is a third of a floor that size.</summary>
        public const int SmallFloor = 28;
        /// <summary>The sides a half turn of the plan carries these to: north and south swap, east and west.</summary>
        public static Sides Turned(Sides s) => (s.HasFlag(Sides.North) ? Sides.South : 0) | (s.HasFlag(Sides.South) ? Sides.North : 0)
                                              | (s.HasFlag(Sides.East) ? Sides.West : 0) | (s.HasFlag(Sides.West) ? Sides.East : 0);

        /// <summary>Turn a finished building half round about its plan's middle, so (x, z) goes to (W - x, D - z) and every
        /// yaw by 180: rigid, so walls keep their winding and their openings' places along them, flights their foot
        /// corners, pieces their fronts. A footprint's X extent stays an X extent. The plan is laid out with its walk-
        /// through rooms along the south side; this is how a front door asked for on the north gets there.</summary>
        static void TurnHalf(BuildingGenResult res)
        {
            int cx = res.CellsX, cz = res.CellsZ;
            float W = cx * Cell, D = cz * Cell;
            float Y(float yaw) => (yaw + 180f) % 360f;
            foreach (var w in res.Walls)
            {
                if (w.Pitch == -90f && w.Yaw == 0f)
                {
                    // a slab (a floor, a flat roof, a landing) keeps the editor's frame for one -- origin at its min X
                    // and max Z, running +X, deep along -Z -- so its corner moves and its holes are measured from it
                    float x0 = W - (w.X + w.Length), z1 = D - (w.Z - w.Height);
                    for (int i = 0; i < w.Openings.Count; i++)
                    {
                        var o = w.Openings[i];
                        o.U = w.Length - o.U - o.Width; o.V = w.Height - o.V - o.Height;
                        w.Openings[i] = o;
                    }
                    w.X = x0; w.Z = z1;
                    continue;
                }
                w.X = W - w.X; w.Z = D - w.Z; w.Yaw = Y(w.Yaw);
            }
            for (int i = 0; i < res.Stairs.Count; i++) { var st = res.Stairs[i]; st.X = W - st.X; st.Z = D - st.Z; st.Yaw = Y(st.Yaw); res.Stairs[i] = st; }
            foreach (var m in res.Furniture) { m.X = W - m.X; m.Z = D - m.Z; m.Yaw = Y(m.Yaw); }
            for (int i = 0; i < res.Rooms.Count; i++) { var r = res.Rooms[i]; res.Rooms[i] = new RoomDesignation(r.Kind, W - r.X, D - r.Z, r.Floor); }
            foreach (var g in res.Layout)
            {
                (g.X0, g.X1, g.Z0, g.Z1) = (cx - g.X1, cx - g.X0, cz - g.Z1, cz - g.Z0);
                for (int i = 0; i < g.More.Count; i++) { var q = g.More[i]; g.More[i] = (cx - q.X1, cz - q.Z1, cx - q.X0, cz - q.Z0); }
            }
            foreach (var d in res.Doors) { d.Line = d.AlongX ? cz - d.Line : cx - d.Line; d.At = (d.AlongX ? W : D) - d.At; }
            for (int i = 0; i < res.Footprints.Count; i++) res.Footprints[i] = (cx - res.Footprints[i].X1, cx - res.Footprints[i].X0);
            res.Bay = (cx - res.Bay.X1, cz - res.Bay.Z1, cx - res.Bay.X0, cz - res.Bay.Z0);
            if (res.Shape != FootprintShape.Rect) res.Wing = (cx - res.Wing.X1, cz - res.Wing.Z1, cx - res.Wing.X0, cz - res.Wing.Z0);
            res.NoWindowSides = Turned(res.NoWindowSides);
        }

        /// <summary>Percent of compound stairs that turn a corner rather than back on themselves.</summary>
        public const int QuarterTurns = 45;
        /// <summary>Retail's pitched roofs cluster at 15-22 degrees (EditorBuildings.RoofPitches).</summary>
        public static readonly float[] GablePitches = { 15f, 18f, 20f, 22f };

        // the editor's opening archetypes (EditorBuildings.Archetypes): index and measured size
        public const int ArchDoor = 0, ArchWindow = 1, ArchTallWindow = 2, ArchGarage = 3, ArchPorch = 4, ArchVent = 5, ArchSlit = 6;
        public static readonly (float W, float H, float Sill, bool Glazed)[] ArchSizes =
        {
            (2.5f, 2.85f, 0f, false), (3.31f, WallOpenings.WindowHeight, WallOpenings.WindowSill, true),
            (2.81f, 2.97f, 0.88f, true), (4.05f, 3.80f, 0f, false), (5.5f, WallOpenings.DoorHeight, 0f, false),
            (1.0f, 1.0f, 2.5f, false),
            // a bathroom's: wide, shallow, high and glazed (strawberry: "change the bathrooms from using 'vent' windows to
            // small glass windows, horizontal slit windows", then "widen the bathroom windows horizontally")
            (3.5f, 0.6f, 2.6f, true),
        };
        public const string DefaultDoor = "Door_Pine";
        /// <summary>Percent of the doorways between two walk-through rooms left open, and of those, made wide; and how wide
        /// a wide one gets (at most, and at least -- short of that it stays a plain doorway).</summary>
        public const int OpenDoorway = 45, WideDoorway = 55;
        public const float WideOpening = 4.5f, MinWideOpening = 3.5f;
        /// <summary>How wide a business's street window may grow.</summary>
        public const float StoreWindow = 5.5f;
        enum Face { Front, Back, Side }
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
            RoomKind.Bathroom => new Trait(5, 2, FloorPref.Any, ArchSlit),          // a high slit of glass, not a view
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
            // a business's staff room, where a house has its kitchen: a table, a counter with a sink, a fridge
            RoomKind.BreakRoom => new Trait(9, 3, FloorPref.Any, ArchWindow),
            _ => new Trait(6, 2, FloorPref.Any, ArchWindow),
        };

        public static Access AccessOf(RoomKind k) => k switch
        {
            RoomKind.Hall or RoomKind.Stairwell or RoomKind.LivingRoom or RoomKind.Kitchen or RoomKind.DiningRoom
                or RoomKind.Shop or RoomKind.Warehouse or RoomKind.Workshop => Access.Through,
            RoomKind.Bedroom or RoomKind.Bathroom or RoomKind.Office or RoomKind.Closet or RoomKind.BreakRoom => Access.Private,
            _ => Access.Leaf,
        };

        /// <summary>Rooms a flight of stairs may stand in: not private, and roomy enough to walk round one.</summary>
        /// (Not a shop floor: strawberry, "shop rooms should not be allowed stairs".)
        public static bool HoldsStairs(RoomKind k) => k is RoomKind.Hall or RoomKind.LivingRoom or RoomKind.DiningRoom
            or RoomKind.Warehouse or RoomKind.Workshop;

        /// <summary>May a room of kind `child` have its door off a room of kind `parent`? Off anything you walk through;
        /// a closet or an en-suite off a bedroom; a store room off a garage; a closet, toilet or store off an office.
        /// Common sense, not a building code: strawberry, "dont be overly strict on room rules".</summary>
        /// A break room is staff-only: off the back of the house -- a hall, the works floor -- never off the shop floor ("why
        /// is the shop doorframed to a kitchen?").
        public static bool MayOpenOff(RoomKind child, RoomKind parent) => child == RoomKind.BreakRoom
            ? parent is RoomKind.Hall or RoomKind.Warehouse or RoomKind.Workshop
            : AccessOf(parent) == Access.Through
            || (parent == RoomKind.Bedroom && child is RoomKind.Closet or RoomKind.Bathroom)
            || (parent == RoomKind.Garage && child == RoomKind.Storage)
            || (parent == RoomKind.Office && child is RoomKind.Closet or RoomKind.Bathroom or RoomKind.Storage);

        /// <summary>Rooms that need daylight: each gets at least one glazed opening in an outside wall.</summary>
        public static bool Habitable(RoomKind k) => k is RoomKind.Bedroom or RoomKind.Kitchen or RoomKind.LivingRoom
            or RoomKind.DiningRoom or RoomKind.Office or RoomKind.Shop or RoomKind.Workshop;

        /// <summary>Rooms a building has only one of unless the spec asks for more.</summary>
        public static bool OnlyOne(RoomKind k, BuildingKind b) => k == RoomKind.Kitchen || k == RoomKind.BreakRoom
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
            BuildingKind.Commercial => new[] { RoomKind.Storage, RoomKind.Hall, RoomKind.BreakRoom },
            BuildingKind.Industrial => new[] { RoomKind.Warehouse, RoomKind.Workshop, RoomKind.Storage, RoomKind.Hall },
            BuildingKind.Residential => new[] { RoomKind.Kitchen, RoomKind.Hall, RoomKind.DiningRoom },
            _ => new[] { RoomKind.Hall, RoomKind.Storage, RoomKind.BreakRoom },
        };

        /// <summary>What a kind of building is not without, however small: kept when a limit drops rooms.</summary>
        public static RoomKind[] Essentials(BuildingKind b) => b == BuildingKind.Residential
            ? new[] { RoomKind.Bedroom, RoomKind.Bathroom, RoomKind.Kitchen } : Array.Empty<RoomKind>();
        /// <summary>A kitchen-diner's cells: a kitchen's and most of a dining room's, short of the living room's.</summary>
        public const int DinerCells = 12;

        /// <summary>What a kind of building always has, and what it adds (in this order, round again if need be) to reach MinRooms.</summary>
        public static (RoomKind[] Always, RoomKind[] More) Programme(BuildingKind b) => b switch
        {
            BuildingKind.Residential => (new[] { RoomKind.LivingRoom, RoomKind.Kitchen, RoomKind.Bathroom, RoomKind.Bedroom },
                                         new[] { RoomKind.Bedroom, RoomKind.DiningRoom, RoomKind.Bathroom, RoomKind.Closet, RoomKind.Bedroom, RoomKind.Garage, RoomKind.Storage }),
            BuildingKind.Commercial => (new[] { RoomKind.Shop, RoomKind.Storage, RoomKind.Bathroom },
                                        new[] { RoomKind.Office, RoomKind.Storage, RoomKind.BreakRoom, RoomKind.Office, RoomKind.Bathroom }),
            BuildingKind.Industrial => (new[] { RoomKind.Warehouse, RoomKind.Workshop, RoomKind.Office, RoomKind.Bathroom },
                                        new[] { RoomKind.Storage, RoomKind.Office, RoomKind.Garage, RoomKind.Storage }),
            _ => (new[] { RoomKind.Office, RoomKind.Bathroom, RoomKind.Storage },
                  new[] { RoomKind.Office, RoomKind.BreakRoom, RoomKind.Storage, RoomKind.Office }),
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
            /// <summary>Each item with a draw of its own, drawn here and in order. Never draw inside a LINQ key selector: how
            /// often one runs is the runtime's business (one OrderBy over a single room called it in the plan dumper and not
            /// in the editor, and the two built the same seed with different doors and furniture).</summary>
            public List<(T Item, ulong Key)> Keyed<T>(IEnumerable<T> items) { var l = new List<(T, ulong)>(); foreach (var x in items) l.Add((x, Next())); return l; }
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
            /// <summary>The way an outside wall on a line faces at position s along it: toward the side with no room. (An
            /// L's inner corner has outside walls in the middle of the plan, so a line's number cannot say.)</summary>
            public Sides Out(bool alongX, int line, int s) => alongX ? (At(s, line) == null ? Sides.North : Sides.South)
                                                                      : (At(line, s) == null ? Sides.East : Sides.West);
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
            // a kitchen with the dining table in it is a size up (see `diner` below)
            bool diner = false;
            Trait T(RoomKind r) => r == RoomKind.Kitchen && diner ? new Trait(DinerCells, 3, FloorPref.Ground, ArchWindow) : TraitOf(r, kind);
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
            // a house eats in its kitchen when it has no dining room, and now and then when it has (strawberry: "kitchen
            // could also spawn as a kitchen and dining in one room"): the dining room goes, the kitchen grows, the
            // furnisher puts the table in it
            if (kind == BuildingKind.Residential && rooms.Contains(RoomKind.Kitchen)
                && (!rooms.Contains(RoomKind.DiningRoom) || (rng.Chance(30) && !spec.MinOfKind.ContainsKey(RoomKind.DiningRoom))))
            {
                diner = true;
                rooms.Remove(RoomKind.DiningRoom);
                more = more.Where(k => k != RoomKind.DiningRoom).ToArray();
                for (int i = 0; rooms.Count < spec.MinRooms; i++) if (Fits(more[i % more.Length])) rooms.Add(more[i % more.Length]);
            }
            res.KitchenDiner = diner;
            // what a building of the kind cannot do without (strawberry: "at the bare minimum a house needs a bedroom,
            // bathroom and kitchen"): a limit too small to hold them is raised to, and said so
            var essential = Essentials(kind);
            if (capacity < essential.Length)
            {
                perFloor = (essential.Length + maxFloors - 1) / maxFloors; capacity = maxFloors * perFloor;
                res.Notes.Add($"a {kind} needs at least {string.Join(", ", essential)}: {perFloor} rooms per floor, not {spec.MaxRoomsPerFloor}");
            }
            if (rooms.Count > capacity)
            {
                // the dropped ones are off the END: fillers first, then the spec's own minimums, then the kind's core --
                // but never the first of each essential room
                var keep = new HashSet<int>(essential.Select(k => rooms.IndexOf(k)).Where(i => i >= 0));
                for (int i = 0; i < rooms.Count && keep.Count < capacity; i++) keep.Add(i);
                res.Notes.Add($"{rooms.Count} rooms asked for and at most {capacity} fit ({maxFloors} floors of {perFloor}): dropped {string.Join(", ", rooms.Where((_, i) => !keep.Contains(i)))}");
                rooms = rooms.Where((_, i) => keep.Contains(i)).ToList();
            }

            // ---- floors: as few as hold them, at least MinFloors. A second floor is not owed to anyone ("having multiple
            // floors isnt required"): a building that fits on one gets two only on a roll, and only when it has rooms
            // that would rather be upstairs.
            int floors = Math.Max(minFloors, (rooms.Count + perFloor - 1) / perFloor);
            if (floors == 1 && floors < maxFloors && rooms.Count >= 5 && rooms.Any(r => T(r).Pref == FloorPref.Upper)
                && rng.Chance(kind == BuildingKind.Residential ? 50 : 35)) floors = 2;
            floors = Math.Min(floors, maxFloors);
            // ---- the outline: a rectangle, or (one storey only, for now) an L or a T -- drawn from a stream of its own,
            // so a rectangle is the same building it was before the outline could be anything else
            var shapeRng = new Rng(unchecked(spec.Seed * 7919 + 0x4C54));
            var shape = spec.Shape;
            if (shape == FootprintShape.Auto)
            {
                int roll = shapeRng.Int(100);
                shape = floors > 1 ? FootprintShape.Rect : roll < 25 ? FootprintShape.L : roll < 35 ? FootprintShape.T : FootprintShape.Rect;
            }
            // one asked for keeps a building on one floor that only a roll put on two
            if (shape != FootprintShape.Rect && floors > 1 && minFloors == 1 && rooms.Count <= perFloor) floors = 1;
            if (shape != FootprintShape.Rect && floors > 1)
            {
                res.Notes.Add($"{(shape == FootprintShape.L ? "an L" : "a T")} outline is one storey only for now: {floors} floors came out a rectangle");
                shape = FootprintShape.Rect;
            }
            res.Floors = floors;
            bool multi = floors > 1;
            // compound stairs for small spaces (strawberry: "add support for compound stairs (stairs that stop halfway up,
            // then change directions) for small spaces"): up one row to a landing halfway, then back along the next row
            // (a switchback, three cells by two rows) or round the corner and up the bay's last column (a quarter turn --
            // "doesnt have to be a 180 staircase either, can be 90 too" -- three cells by three rows); in a building whose
            // floors are small, and now and then in any. They stack straight up.
            bool compound = multi && (rooms.Sum(r => T(r).Cells) / floors <= SmallFloor || rng.Chance(25));
            res.StairShape = !compound ? StairShape.Straight : rng.Chance(QuarterTurns) ? StairShape.Quarter : StairShape.Switchback;
            int k1 = compound || floors <= 2 ? 1 : 2, L = multi ? (compound ? 3 : 4) : 0;   // the stair bay (see below)
            int bayRows = res.StairShape switch { StairShape.Switchback => 2, StairShape.Quarter => 3, _ => k1 + 1 };
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
            // when the row is empty, and with `hall` (a hall runs beside the row to take it) what is left over a row of
            // small rooms past one more cell -- for the caller's hall to take.
            int Pad(List<(RoomKind k, int w)> list, int length, int depth, List<RoomKind> here, int f, Func<RoomKind, bool> ok,
                    RoomKind host = RoomKind.Unassigned, bool hall = false)
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
                // a row of small rooms only, with a hall beside it: an alcove of hall rather than a 15 m bathroom
                if (hall && spare >= 2 && !list.Any(x => !Service(x.k))) return spare;
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
            // No two bathrooms side by side in a row (strawberry: "seed 1 has 2 bathrooms next to eachother??"): swap the
            // second for the first room further along that keeps them apart and the kitchen beside its dining room.
            void ApartBathrooms(List<(RoomKind k, int w)> row)
            {
                bool Bad(List<(RoomKind k, int w)> r) => Enumerable.Range(0, Math.Max(0, r.Count - 1)).Any(i => r[i].k == RoomKind.Bathroom && r[i + 1].k == RoomKind.Bathroom);
                bool KD(List<(RoomKind k, int w)> r) { int ki = r.FindIndex(x => x.k == RoomKind.Kitchen), di = r.FindIndex(x => x.k == RoomKind.DiningRoom); return ki < 0 || di < 0 || Math.Abs(ki - di) == 1; }
                bool kd = KD(row);
                for (int guard = 0; guard < 8 && Bad(row); guard++)
                {
                    int i = Enumerable.Range(0, row.Count - 1).First(q => row[q].k == RoomKind.Bathroom && row[q + 1].k == RoomKind.Bathroom);
                    List<(RoomKind k, int w)> fixedRow = null;
                    foreach (int which in new[] { i + 1, i })   // the second of the pair to another place, else the first
                        for (int j = 0; j < row.Count && fixedRow == null; j++)
                        {
                            if (j == i || j == i + 1 || row[j].k == RoomKind.Bathroom) continue;
                            var t = new List<(RoomKind k, int w)>(row);
                            (t[which], t[j]) = (t[j], t[which]);
                            if (!Bad(t) && (!kd || KD(t))) fixedRow = t;
                        }
                    if (fixedRow == null) { res.Notes.Add("two bathrooms side by side: no other room in their row to put between them"); return; }
                    row.Clear(); row.AddRange(fixedRow);
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
                => rng.Keyed(rs).OrderByDescending(x => Width(x.Item, depth)).ThenBy(x => x.Key).Select(x => x.Item).ToList();
            // An L or a T: the north row, `lb` cells of rooms long, comes up short of the south row at one end or at both,
            // by two or three cells a corner at the least (6 m: less reads as a jog in the wall, not a wing), and the south
            // row grows to make the room. Where the north row starts and how long it is; (0, w) for a rectangle.
            (int X0, int Len) wingCut = (0, 0);
            (int X0, int Len) Cut(ref int w, int lb)
            {
                if (shape == FootprintShape.Rect || lb == 0) return (0, w);
                int ends = shape == FootprintShape.T ? 2 : 1;
                int each = Math.Max(2 + shapeRng.Int(2), (w - lb) / ends);
                w = Math.Max(w, lb + each * ends);
                int len = w - each * ends;
                wingCut = (shape == FootprintShape.T || shapeRng.Chance(50) ? each : 0, len);
                return wingCut;
            }

            // ---- the stair bay: k1 flight rows against the south wall and a row of floor beside them, four cells long --
            // a 6 m flight and a full tile clear at each end of it, at the foot and on the landing above (strawberry:
            // "stairs should want 1 full tile at the foot and 1 tile on the landing above"). A switchback's is three
            // cells by two rows: the tile at the foot and the tile it arrives on side by side, then its two flights, then
            // the landing.
            int dS = 3, dN = kind == BuildingKind.Industrial ? 4 : 3;   // upper floors: rows south of the hall, and north of it
            // a depth asked for: the floors above take it as their north row (south of the hall is the stair bay's); one
            // floor splits it between its two rows -- each at least two cells, so no shallower than five or four
            int wantD = spec.Depth, wantW = spec.Width;
            if (wantD > 0 && wantD < (multi ? dS + 3 : 4))
            {
                res.Notes.Add($"a depth of {wantD} cells is too shallow for {(multi ? "stairs and a hall" : "two rows of rooms")}: {(multi ? dS + 3 : 4)} at least");
                wantD = multi ? dS + 3 : 4;
            }
            if (multi && wantD > 0) dN = wantD - 1 - dS;
            bool stairsWest = rng.Int(2) == 0;                          // the bay at the west end of the upper floors, or the east
            res.QuarterWest = stairsWest;   // that end has the wall on every floor, so a quarter turn turns into it

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
                upAbsorbS[f] = Pad(S, Lu - L, dS, onFloor[f], f, _ => true, hall: true);
                upAbsorbN[f] = Pad(N, Lu, dN, onFloor[f], f, _ => true, hall: true);
                ApartBathrooms(S); ApartBathrooms(N);
                upS[f] = S; upN[f] = N; upLen[f] = Lu;
                need = Lu;
            }

            // ---- which way round. The plan lays its walk-through rooms along the south side; a front door asked for on a
            // side the plan cannot reach is put on the opposite one and the whole building turned half round after
            var frontAsked = spec.FrontSide;
            bool turn = false;

            // ---- the ground floor: BANDS or CORRIDOR, drawn until one connects
            int bandsChance = kind switch { BuildingKind.Residential => 65, BuildingKind.Misc => 25, _ => 75 };
            bool bandsFirst = rng.Chance(bandsChance);
            Grid ground = null;
            int W = 0, D = 0, bx0 = 0;
            // a footprint asked for that no layout fits is let go, said so, and the floor drawn again at its own size
            for (int pass = 0; pass < 3 && ground == null; pass++)
            {
            if (pass == 1)
            {
                if (frontAsked == Sides.None) continue;
                res.Notes.Add($"no layout put the front door on the {spec.FrontSide} side: it went where it could");
                frontAsked = Sides.None;
            }
            if (pass == 2)
            {
                if (wantW == 0 && (wantD == 0 || multi)) break;
                res.Notes.Add($"no layout fitted the {wantW}x{wantD} cells asked for: sized to its rooms instead");
                wantW = 0; if (!multi) wantD = 0;
            }
            for (int attempt = 0; attempt < 14 && ground == null; attempt++)
            {
                bool bands = attempt < 9 ? bandsFirst : !bandsFirst;
                var here = new List<RoomKind>(onFloor[0]);
                wingCut = (0, 0);
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
                // an L or a T: the wing is the north row (and in a corridor plan what is left of it as hall)
                if (wingCut.Len > 0 && wingCut.Len < pw)
                {
                    int zj = prs.Where(r => r.X0 < wingCut.X0 || r.X1 > wingCut.X0 + wingCut.Len).Max(r => r.Z1);
                    res.Shape = shape;
                    res.Wing = (wingCut.X0, zj, wingCut.X0 + wingCut.Len, pd);
                    // a hip has nothing to hip into at the wing: an L or a T is gabled
                    if (res.Roof == RoofKind.Hip) res.Roof = RoofKind.Gable;
                }
                res.Layout.AddRange(prs);
                res.Doors.AddRange(doors);
                onFloor[0] = here;
            }
            }
            if (ground == null) throw new InvalidOperationException($"BuildingGen: no ground floor connected for {kind} seed {spec.Seed} (min {spec.MinRooms} rooms, {spec.MinFloors}-{spec.MaxFloors} floors, {spec.MaxRoomsPerFloor}/floor): {string.Join(", ", onFloor[0])}");
            res.CellsX = W; res.CellsZ = D;
            if (wantW > 0 && W > wantW) res.Notes.Add($"{W} cells long, not the {wantW} asked for: its rooms need it");
            if (wantD > 0 && D != wantD) res.Notes.Add($"{D} cells deep, not the {wantD} asked for");
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
                if (upAbsorbS[f] > 0) hall.More.Add((x, 0, x + upAbsorbS[f], dS));
                x = ux0;
                foreach (var (k, w) in upN[f]) { rs.Add(new GenRoom { Kind = k, Floor = f, X0 = x, X1 = x + w, Z0 = dS + 1, Z1 = D }); x += w; }
                if (upAbsorbN[f] > 0) hall.More.Add((x, dS + 1, ux1, D));
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

            // some doorways between two rooms you walk through are left open, and some of those are wide
            // (in houses: a business's rooms keep their doors -- a shop floor is not open to its back rooms)
            foreach (var d in res.Doors)
                if (kind == BuildingKind.Residential && d.B != null && AccessOf(d.A.Kind) == Access.Through && AccessOf(d.B.Kind) == Access.Through && rng.Chance(OpenDoorway))
                {
                    d.Open = true;
                    // not wide into a small kitchen: its counters need the wall
                    d.Wide = rng.Chance(WideDoorway) && !new[] { d.A, d.B }.Any(r => r.Kind == RoomKind.Kitchen && r.Cells < 12);
                }
            res.NoWindowSides = turn ? Turned(spec.NoWindows) : spec.NoWindows;
            BuildWalls(res, grids, k1, L);
            SolveCorners(res.Walls);
            Furnish(res, spec.Seed);
            if (turn) TurnHalf(res);
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
                if (!multi && wantD > 0) { dA = Math.Clamp(dA, 2, wantD - 2); dB = wantD - dA; }
                var A = here.Where(r => AccessOf(r) != Access.Private).ToList();
                var B = here.Where(r => AccessOf(r) == Access.Private).ToList();
                RoomKind host = RoomKind.Unassigned;
                if (multi)
                {
                    host = rng.Keyed(A.Where(HoldsStairs)).OrderByDescending(x => T(x.Item).Cells).ThenBy(x => x.Key).Select(x => x.Item).FirstOrDefault();
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
                int w = Math.Max(Math.Max(Math.Max(rowA.Sum(x => x.w), rowB.Sum(x => x.w)), Math.Max(need, 4)), wantW);
                var (bx, bl) = Cut(ref w, rowB.Sum(x => x.w));
                if (Pad(rowA, w, dA, here, 0, r => AccessOf(r) != Access.Private, multi ? host : RoomKind.Unassigned) > 0) return null;
                if (Pad(rowB, bl, dB, here, 0, _ => true) > 0) return null;
                ApartBathrooms(rowB);
                // a business's shop at an end of its row, its end wall the street front, then the rooms you walk through,
                // then the dead ends; when it holds the stairs, at the end the floors above start from
                bool shopEnd = kind != BuildingKind.Residential && rowA.Any(x0 => x0.k == RoomKind.Shop);
                bool shopWest = false;
                if (shopEnd)
                {
                    shopWest = multi && host == RoomKind.Shop ? stairsWest : rng.Chance(50);
                    var shop = rowA.First(x0 => x0.k == RoomKind.Shop);
                    var ordered = new List<(RoomKind k, int w)> { shop };
                    ordered.AddRange(rowA.Where(x0 => x0.k != RoomKind.Shop && AccessOf(x0.k) == Access.Through));
                    ordered.AddRange(rowA.Where(x0 => x0.k != RoomKind.Shop && AccessOf(x0.k) != Access.Through));
                    ordered.AddRange(rowA.Where(x0 => x0.k == RoomKind.Shop).Skip(1));
                    if (!shopWest) ordered.Reverse();
                    rowA.Clear(); rowA.AddRange(ordered);
                }
                var rs = new List<GenRoom>();
                int x = 0, b = -1;
                foreach (var (k, wd) in rowA)
                {
                    rs.Add(new GenRoom { Kind = k, Floor = 0, X0 = x, X1 = x + wd, Z0 = 0, Z1 = dA });
                    // the bay: inside the host, and where the floors above (which start or end at it) still fit
                    if (multi && k == host && b < 0 && wd >= L)
                    {
                        int lo = Math.Max(x, stairsWest ? 0 : need - L), hi = Math.Min(x + wd - L, stairsWest ? w - need : w - L);
                        // in a shop, at the far end from its street front; a quarter turn in the corner of its room it turns
                        // into (strawberry: "corner stairs need to be up against 2 supporting walls")
                        if (lo <= hi) b = shopEnd && k == RoomKind.Shop ? (shopWest ? hi : lo) : lo + rng.Int(hi - lo + 1);
                        if (res.StairShape == StairShape.Quarter) { int c = stairsWest ? x : x + wd - L; b = c >= lo && c <= hi ? c : -1; }
                    }
                    x += wd;
                }
                if (multi && b < 0) return null;
                x = bx;
                foreach (var (k, wd) in rowB) { rs.Add(new GenRoom { Kind = k, Floor = 0, X0 = x, X1 = x + wd, Z0 = dA, Z1 = dA + dB }); x += wd; }
                return (rs, w, dA + dB, multi ? b : 0);
            }

            // CORRIDOR: a hall along the floor with rooms either side -- and with stairs the bay off its south side, so
            // the hall is the stair room. `safe` puts the bay at the end that always leaves the floors above room to fit.
            (List<GenRoom> Rooms, int W, int D, int Bx0)? Corridor(List<RoomKind> here, bool safe)
            {
                int sS = multi ? dS : 3 + rng.Int(2), sN = multi ? dN : (kind == BuildingKind.Industrial ? 4 : 3 + rng.Int(2));
                if (!multi && wantD > 0)
                {
                    if (wantD < 5) return null;   // two rows of rooms and a hall between them: bands it is
                    sS = Math.Clamp(sS, 2, wantD - 3); sN = wantD - 1 - sS;
                }
                var S = new List<(RoomKind k, int w)>(); var N = new List<(RoomKind k, int w)>();
                // (the kitchen and dining room go to one side together, so they can sit side by side)
                foreach (var r in Seeded(here.Where(r => r != RoomKind.DiningRoom || !here.Contains(RoomKind.Kitchen)), sS))
                {
                    var pair = r == RoomKind.Kitchen && here.Contains(RoomKind.DiningRoom) ? new[] { r, RoomKind.DiningRoom } : new[] { r };
                    int lenS = L + S.Sum(x => x.w) + pair.Sum(p => Width(p, sS)), lenN = N.Sum(x => x.w) + pair.Sum(p => Width(p, sN));
                    foreach (var p in pair) if (lenS <= lenN) S.Add((p, Width(p, sS))); else N.Add((p, Width(p, sN)));
                }
                int w = Math.Max(Math.Max(Math.Max(L + S.Sum(x => x.w), N.Sum(x => x.w)), Math.Max(need, 4)), wantW);
                var (nx, nl) = Cut(ref w, N.Sum(x => x.w));
                int aS = Pad(S, w - L, sS, here, 0, _ => true, hall: true), aN = Pad(N, nl, sN, here, 0, _ => true, hall: true);
                rng.Shuffle(S);
                KitchenBesideDining(S); KitchenBesideDining(N);
                ApartBathrooms(S); ApartBathrooms(N);
                // a business's shop at an end of its row, its end wall the street front (the south row's at the end away
                // from the stairs, which go in at the end the floors above start from)
                if (kind != BuildingKind.Residential)
                    foreach (var (row, east) in new[] { (S, stairsWest), (N, rng.Chance(50)) })
                    {
                        int si = row.FindIndex(x0 => x0.k == RoomKind.Shop);
                        if (si < 0) continue;
                        var shop = row[si]; row.RemoveAt(si);
                        if (east) row.Add(shop); else row.Insert(0, shop);
                        KitchenBesideDining(row);
                    }
                var hall = new GenRoom { Kind = RoomKind.Hall, Floor = 0, X0 = 0, X1 = w, Z0 = sS, Z1 = sS + 1 };
                var rs = new List<GenRoom> { hall };
                int b = 0;
                if (S.Count == 0)
                {
                    // no south rooms: the whole south side is hall, and the bay anywhere in it the floors above fit
                    hall.More.Add((0, 0, w, sS));
                    if (multi)
                    {
                        int lo = stairsWest ? 0 : need - L, hi = stairsWest ? w - need : w - L;
                        if (lo > hi) return null;
                        b = lo + rng.Int(hi - lo + 1);
                        if (res.StairShape == StairShape.Quarter) b = stairsWest ? 0 : w - L;   // into the building's corner
                    }
                }
                else
                {
                    // what a row of small rooms left is an alcove of hall at one end of it, placed like a room
                    if (aS > 0) S.Insert(rng.Int(2) == 0 ? 0 : S.Count, (RoomKind.Hall, aS));
                    // the bay between two of the south rooms: the first split that leaves the floors above room
                    int split = 0;
                    if (multi)
                    {
                        var splits = safe ? new List<int> { stairsWest ? 0 : S.Count } : Enumerable.Range(0, S.Count + 1).ToList();
                        // a quarter turn: where the side it turns into is a room's wall or the building's end, not hall
                        if (res.StairShape == StairShape.Quarter)
                            splits = splits.Where(i => stairsWest ? i == 0 || S[i - 1].k != RoomKind.Hall : i == S.Count || S[i].k != RoomKind.Hall).ToList();
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
                        if (S[i].k == RoomKind.Hall) hall.More.Add((x, 0, x + S[i].w, sS));
                        else rs.Add(new GenRoom { Kind = S[i].k, Floor = 0, X0 = x, X1 = x + S[i].w, Z0 = 0, Z1 = sS });
                        x += S[i].w;
                    }
                }
                int xn = nx;
                foreach (var (k, wd) in N) { rs.Add(new GenRoom { Kind = k, Floor = 0, X0 = xn, X1 = xn + wd, Z0 = sS + 1, Z1 = sS + 1 + sN }); xn += wd; }
                if (aN > 0) hall.More.Add((xn, sS + 1, nx + nl, sS + 1 + sN));
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
                Sides SideOf(Run r) => g.Out(r.AlongX, r.Line, r.S0);
                Sides DoorSide(GenDoor d) => g.Out(d.AlongX, d.Line, (int)MathF.Floor(d.At / Cell));
                // the front door's sides as laid out: the ones asked for, or their opposites (then turned half round)
                var frontOk = frontAsked == Sides.None ? Sides.None : frontAsked | Turned(frontAsked);
                GenDoor Door(GenRoom a, GenRoom b) => Door2(a, b, out _);
                GenDoor Door2(GenRoom a, GenRoom b, out bool crowdedOut)
                {
                    crowdedOut = false;
                    var mine = runs.Where(r => (r.A == a && r.B == b) || (r.A == b && r.B == a)).ToList();
                    if (b == null && root == null && frontOk != Sides.None) mine = mine.Where(r => (SideOf(r) & frontOk) != 0).ToList();
                    rng.Shuffle(mine);
                    // a shop's street door goes in its frontage: an end wall, so the shop runs back from the street deeper
                    // than it is wide (strawberry: "in general stores should span deeper instead of wider") -- its longest
                    // outside wall only when it has no end wall
                    if (b == null && a.Kind == RoomKind.Shop) mine = mine.OrderBy(r => r.AlongX ? 1 : 0).ThenByDescending(r => r.S1 - r.S0).ToList();
                    // and off the back door's side, if one was asked for, where it can go elsewhere (first, not only: a run
                    // can be there and have no room for a door)
                    if (b == null && root == null && spec.BackSide != Sides.None && frontAsked == Sides.None)
                        mine = mine.OrderBy(r => (SideOf(r) & spec.BackSide) != 0 ? 1 : 0).ToList();
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
                        foreach (var r in rng.Keyed(rs.Where(r => r.Kind == k)).OrderBy(x => x.Key).Select(x => x.Item).ToList())
                            if ((front = Door(r, null)) != null) break;
                        if (front != null) break;
                    }
                    if (front == null) return false;
                    front.Role = DoorRole.Front;
                    // on the opposite of the side asked for: the building turns half round once it is built
                    turn = frontAsked != Sides.None && (DoorSide(front) & frontAsked) == 0;
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
                // (one asked for, always, on a side asked for -- as laid out, so turned if the building will be)
                var backOk = spec.BackSide == Sides.None ? Sides.None : turn ? Turned(spec.BackSide) : spec.BackSide;
                if (f == 0 && (backOk != Sides.None || rng.Chance(60)))
                {
                    var front = doors.First(d => d.Role == DoorRole.Front);
                    int Side(Sides sd) => sd switch { Sides.South => 0, Sides.East => 1, Sides.North => 2, _ => 3 };
                    int fs = Side(DoorSide(front));
                    var order = BackDoorOrder(kind);
                    var backRooms = rng.Keyed(rs.Where(r => order.Contains(r.Kind))).OrderBy(x => Array.IndexOf(order, x.Item.Kind)).ThenBy(x => x.Key).Select(x => x.Item).ToList();
                    // a side asked for: after the rooms that suit one, a hall
                    if (backOk != Sides.None) backRooms.AddRange(rs.Where(r => !backRooms.Contains(r) && r.Kind == RoomKind.Hall));
                    foreach (var r in backRooms)
                    {
                        var outs = runs.Where(u => u.B == null && u.A == r && Side(SideOf(u)) != fs
                                                   && (backOk == Sides.None || (SideOf(u) & backOk) != 0)).ToList();
                        rng.Shuffle(outs);
                        GenDoor back = null;
                        foreach (var u in outs.OrderBy(u => Side(SideOf(u)) == (fs + 2) % 4 ? 0 : 1))
                            if (Slot(u, out bool crowded) is float m && !float.IsNaN(m) && !crowded)
                            { back = new GenDoor { Floor = f, A = r, B = null, AlongX = u.AlongX, Line = u.Line, At = m, Role = DoorRole.Back }; break; }
                        if (back != null) { doors.Add(back); break; }
                    }
                    if (backOk != Sides.None && !doors.Any(d => d.Role == DoorRole.Back))
                        res.Notes.Add($"no back door could go on the {spec.BackSide} side");
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
        static List<(int Arch, float At, float Width)> Spread(float lo, float hi, IList<int> arches, float fill = 0f)
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
                    if ((arch == ArchWindow || arch == ArchTallWindow || arch == ArchSlit) && slot - gap >= MinWindow) width = slot - gap;
                    else continue;
                }
                // a shop window grows to its slot, up to `fill`
                else if (fill > width && (arch == ArchWindow || arch == ArchTallWindow)) width = MathF.Min(fill, slot - gap);
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
                // `into`: run on past the high end, through the upper wall to its inner face -- a side's corner with
                // that wall, which the editor's corner pass would otherwise grow blind into the wall already standing
                // there (and on into the room). A hair thinner than that wall, so the overlap hides inside it rather
                // than fighting its faces.
                void Close(float x0, float z0, float x1, float z1, float yaw, bool into = false)
                {
                    var (band, tri, highAtEnd) = RoofShapes.ShedInfill(rs, x0, z0, x1, z1);
                    float len = MathF.Abs(x1 - x0) + MathF.Abs(z1 - z0), ext = into ? Ext : 0f, thick = into ? Ext - 0.02f : Ext;
                    var (yc, ys) = PlanTrig.CosSin(yaw);
                    float dx = yc, dz = -ys;
                    float ox = highAtEnd ? x0 : x0 - dx * ext, oz = highAtEnd ? z0 : z0 - dz * ext;
                    if (band > 0.01f)
                        res.Walls.Add(new WallPlan { X = ox, Y = head, Z = oz, Yaw = yaw, Length = len + ext, Height = band, Thickness = thick, Kind = SurfaceKind.Wall });
                    if (tri > 0.01f)
                        res.Walls.Add(new WallPlan { X = ox, Y = head + band, Z = oz, Yaw = yaw, Length = len + ext, Height = tri, Thickness = thick, Kind = SurfaceKind.Wall,
                                                     InsetL1 = highAtEnd ? len : 0f, InsetR1 = highAtEnd ? 0f : len });
                }
                var (ec, es) = PlanTrig.CosSin(endWall.Yaw);
                var e1 = (X: endWall.X + ec * endWall.Length, Z: endWall.Z - es * endWall.Length);
                Close(endWall.X, endWall.Z, e1.X, e1.Z, endWall.Yaw);   // the open end
                Close(xa, Dm, xb, Dm, 0f, into: true);                   // the north side, running +X
                Close(xb, 0f, xa, 0f, 180f, into: true);                 // the south side, running -X
            }
            for (int f = 0; f < floors; f++)
            {
                var g = grids[f];
                float y = f * Storey;
                var (ux0, ux1) = res.Footprints[f];
                float x0m = ux0 * Cell, x1m = ux1 * Cell;
                // the outside walls: the floor's outline traced round, anticlockwise from above as the editor lays a room's,
                // front faces out -- four for a rectangle (north, east, south, west), six or eight for an L or a T
                var outs = Outline(g, y);
                // an outside line's wall at a point on it (metres along +X or +Z), and that point as the wall's u
                WallPlan Side(bool alongX, int line, float at) => outs.First(o => o.AlongX == alongX && o.Line == line && o.A0 <= at + 0.01f && at <= o.A1 + 0.01f).W;
                float U(WallPlan w, float at) => w.Yaw switch { 0f => at - w.X, 180f => w.X - at, 270f => at - w.Z, _ => w.Z - at };

                // the interior walls: maximal runs of owner changes along each lattice line, between two rooms (a room and
                // no room is an outside wall, which an L has inside its bounding box)
                bool Partition(float t) => t > 0f && t < Ext;
                var inner = new List<(bool AlongX, int Line, int S0, int S1, WallPlan W)>();
                for (int z = 1; z < D; z++)
                    for (int x = ux0; x < ux1;)
                    {
                        if (!Partition(g.H(x, z))) { x++; continue; }
                        int s0 = x;
                        while (x < ux1 && Partition(g.H(x, z))) x++;
                        inner.Add((true, z, s0, x, Wall(s0 * Cell, z * Cell, x * Cell, z * Cell, y, Inner)));
                    }
                for (int x = ux0 + 1; x < ux1; x++)
                    for (int z = 0; z < D;)
                    {
                        if (!Partition(g.V(x, z))) { z++; continue; }
                        int s0 = z;
                        while (z < D && Partition(g.V(x, z))) z++;
                        inner.Add((false, x, s0, z, Wall(x * Cell, s0 * Cell, x * Cell, z * Cell, y, Inner)));
                    }

                // the doors
                var outDoors = new List<(bool AlongX, int Line, float At)>();
                foreach (var d in res.Doors.Where(d => d.Floor == f))
                {
                    if (d.B == null)
                    {
                        var w = Side(d.AlongX, d.Line, d.At);
                        w.Openings.Add(Make(ArchDoor, U(w, d.At), ArchSizes[ArchDoor].W));
                        outDoors.Add((d.AlongX, d.Line, d.At));
                        continue;
                    }
                    var iw = inner.First(q => q.AlongX == d.AlongX && q.Line == d.Line && q.S0 * Cell <= d.At && d.At <= q.S1 * Cell);
                    float width = ArchSizes[ArchDoor].W;
                    if (d.Wide)
                    {
                        // as wide as the stretch where these two rooms face each other allows, off the walls meeting it and
                        // the other doors in it, centred where the door was
                        int c = (int)MathF.Floor(d.At / Cell), s0 = c, s1 = c + 1;
                        bool Pair(int s) { var (a, b) = g.Across(d.AlongX, d.Line, s); return (a == d.A && b == d.B) || (a == d.B && b == d.A); }
                        while (Pair(s0 - 1)) s0--;
                        while (Pair(s1)) s1++;
                        float lo = s0 * Cell + g.Meet(d.AlongX, d.Line, s0) + 0.3f, hi = s1 * Cell - g.Meet(d.AlongX, d.Line, s1) - 0.3f;
                        foreach (var o in res.Doors.Where(o => o != d && o.Floor == f && o.AlongX == d.AlongX && o.Line == d.Line))
                            if (o.At < d.At) lo = MathF.Max(lo, o.At + o.Width * 0.5f + 0.3f); else hi = MathF.Min(hi, o.At - o.Width * 0.5f - 0.3f);
                        // and not within a metre of a corner another door is within a metre of (strawberry: "try to avoid
                        // multiple doors on a corner") -- what is left is stretches; the one the door is in, or the longest
                        var free = new List<(float A, float B)> { (lo, hi) };
                        foreach (var o in res.Doors.Where(o => o != d && o.Floor == f))
                        {
                            var (ox, oz) = o.Middle; float half = o.Width * 0.5f + 1f;
                            float ax0 = o.AlongX ? ox - half : ox, ax1 = o.AlongX ? ox + half : ox, az0 = o.AlongX ? oz : oz - half, az1 = o.AlongX ? oz : oz + half;
                            for (int i = (int)MathF.Ceiling(ax0 / Cell - 1e-3f); i * Cell <= ax1 + 1e-3f; i++)
                                for (int j = (int)MathF.Ceiling(az0 / Cell - 1e-3f); j * Cell <= az1 + 1e-3f; j++)
                                {
                                    if ((d.AlongX ? j : i) != d.Line) continue;   // a corner on this door's line
                                    // (5 cm past the metre: a door clamped exactly to the cut's edge sat on it, within any tolerance)
                                    float u = (d.AlongX ? i : j) * Cell, c0 = u - 1.05f, c1 = u + 1.05f;
                                    free = free.SelectMany(q => q.B <= c0 || q.A >= c1 ? new[] { q } : new (float A, float B)[] { (q.A, Math.Min(q.B, c0)), (Math.Max(q.A, c1), q.B) })
                                               .Where(q => q.B - q.A > 0.01f).ToList();
                                }
                        }
                        var span = free.OrderByDescending(q => q.A <= d.At && d.At <= q.B).ThenByDescending(q => q.B - q.A).FirstOrDefault();
                        // slid along from where the door was if that is what it takes; too tight, it stays a plain doorway
                        float w2 = MathF.Min(WideOpening, span.B - span.A);
                        if (free.Count > 0 && w2 >= MinWideOpening) { d.At = Math.Clamp(d.At, span.A + w2 * 0.5f, span.B - w2 * 0.5f); width = w2; }
                    }
                    d.Width = width;
                    var op = Make(ArchDoor, d.At - iw.S0 * Cell, width);
                    if (d.Open) op.DoorProp = null;
                    iw.W.Openings.Add(op);
                }

                // what each room has in its outside walls
                bool business = res.Kind == BuildingKind.Commercial || res.Kind == BuildingKind.Misc;
                var fr = res.Front;
                var frSide = fr == null ? Sides.None : grids[0].Out(fr.AlongX, fr.Line, (int)MathF.Floor(fr.At / Cell));
                Face Facing(Sides sd) => fr == null ? Face.Side : sd == frSide ? Face.Front : sd == Turned(frSide) ? Face.Back : Face.Side;
                // an end wall with the floor below's roof leaning on it gets no windows: the roof is in front of them
                bool westUnder = f > 0 && res.Roof != RoofKind.Flat && res.Footprints[f - 1].X0 < ux0;
                bool eastUnder = f > 0 && res.Roof != RoofKind.Flat && res.Footprints[f - 1].X1 > ux1;
                var outside = Runs(g).Where(r => r.B == null).ToList();
                foreach (var run in outside)
                {
                    if (!run.AlongX && ((run.Line == ux0 && westUnder) || (run.Line == ux1 && eastUnder))) continue;
                    var room = run.A;
                    var w = Side(run.AlongX, run.Line, (run.S0 + run.S1) * 0.5f * Cell);
                    float lo = run.S0 * Cell + g.Meet(run.AlongX, run.Line, run.S0), hi = run.S1 * Cell - g.Meet(run.AlongX, run.Line, run.S1);
                    // the free stretches of this run: not where a door went, not behind the flight
                    var cuts = outDoors.Where(o => o.AlongX == run.AlongX && o.Line == run.Line && o.At > lo && o.At < hi)
                                       .Select(o => (o.At - HalfDoor - 0.3f, o.At + HalfDoor + 0.3f)).ToList();
                    // the stairs stand against solid wall on every floor they reach (strawberry: "stairs need solid walls (no
                    // windows) along their sides (all sides of 90 and 180 degree cases)"): no window in an outside wall the
                    // bay touches -- its south side, and an end wall it reaches
                    if (floors > 1)
                    {
                        if (run.AlongX && run.Line == 0) cuts.Add((bay.X0 * Cell - 0.3f, bay.X1 * Cell + 0.3f));
                        if (!run.AlongX && (run.Line == bay.X0 || run.Line == bay.X1)) cuts.Add((bay.Z0 * Cell - 0.3f, bay.Z1 * Cell + 0.3f));
                    }
                    var free = new List<(float A, float B)> { (lo, hi) };
                    foreach (var (c0, c1) in cuts)
                        free = free.SelectMany(s => s.B <= c0 || s.A >= c1 ? new[] { s } : new (float A, float B)[] { (s.A, Math.Min(s.B, c0)), (Math.Max(s.A, c1), s.B) })
                                   .Where(s => s.B - s.A > 0.1f).ToList();
                    // (its longest run that may have a window: a side asked to have none never takes the one vent or slit)
                    Sides SideOfRun(Run r) => g.Out(r.AlongX, r.Line, r.S0);
                    var mine = outside.Where(r => r.A == room && (r.AlongX || !((r.Line == ux0 && westUnder) || (r.Line == ux1 && eastUnder))))
                                      .OrderBy(r => (SideOfRun(r) & res.NoWindowSides) != 0 ? 1 : 0)
                                      .ThenByDescending(r => r.S1 - r.S0).ThenBy(r => r.AlongX ? 0 : 1).First();
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
                    // a side asked to have none
                    var runSide = SideOfRun(run);
                    if ((runSide & res.NoWindowSides) != 0) win = -1;
                    if ((win == ArchVent || win == ArchSlit) && !longest) win = -1;   // one is enough
                    // a business shows itself to the street (strawberry: "stores and businesses tend to want bigger / more
                    // windows on the front and less or none on the sides"): its front wall more and wider windows, a shop
                    // none on its sides and anything else one a stretch there
                    var face = Facing(runSide);
                    bool shopFront = business && face == Face.Front, side = business && face == Face.Side;
                    if (side && room.Kind == RoomKind.Shop) win = -1;
                    if (win >= 0)
                        foreach (var (s0, s1) in free)
                        {
                            int n = win == ArchVent || win == ArchSlit || room.Circulation ? 1
                                  : shopFront ? Math.Max(1, (int)((s1 - s0) / (1.6f * Cell)))
                                  : side ? 1 : Math.Max(1, (int)((s1 - s0) / (2 * Cell)));
                            arches.AddRange(Spread(s0, s1, Enumerable.Repeat(win, n).ToList(), shopFront ? StoreWindow : 0f));
                        }
                    foreach (var (arch, at, wd) in arches) w.Openings.Add(Make(arch, U(w, at), wd));
                }

                foreach (var o in outs) res.Walls.Add(o.W);
                if (f == floors - 1) tops.AddRange(outs.Select(o => o.W));
                if (f == 0)   // footings under the outside walls, as the editor's auto-fit lays them
                    foreach (var w in outs.Select(o => o.W))
                        res.Walls.Add(new WallPlan { X = w.X, Y = -WallOpenings.FoundationDepth, Z = w.Z, Yaw = w.Yaw, Length = w.Length,
                                                     Height = WallOpenings.FoundationDepth, Thickness = w.Thickness, Kind = SurfaceKind.Foundation });
                foreach (var q in inner) res.Walls.Add(q.W);

                // the floor, its top at the storey's base, out to the outside walls' outer faces (the editor's slab) -- over a
                // switchback, open above both its flights and its landing: a straight flight's hole the editor cuts as it
                // lays the flight, but a switchback's first flight and landing stop short of this floor and need headroom
                // under it all the same. Not past the flights' foot end: the next switchback's foot stands there.
                if (res.Shape != FootprintShape.Rect) { res.Walls.AddRange(Slabs(res, y, SurfaceKind.Floor)); continue; }   // one storey
                var floorSlab = SlabPlan(x0m - he, x1m + he, -he, Dm + he, y, SurfaceKind.Floor);
                if (f > 0 && res.StairShape != StairShape.Straight)
                    foreach (var (hx0, hz0, hx1, hz1) in res.Switchback ? new[] { SwitchbackHole(bay) } : QuarterHole(bay, res.QuarterWest))
                        floorSlab.Openings.Add(new WallOpening(hx0 - (x0m - he), (Dm + he) - hz1, hx1 - hx0, hz1 - hz0));
                res.Walls.Add(floorSlab);
                // a roof on whatever of this floor the one above leaves uncovered: flat on a business; on a house a gable of
                // its own, its ridge running into the wall of the floor above (strawberry: "any reason why the 1st floor
                // section of roof is flat instead of gable? it can be a lower angle gable if required")
                if (f + 1 < floors)
                {
                    var (a0, a1) = res.Footprints[f + 1];
                    float wallHead = (f + 1) * Storey;
                    WallPlan End(int line) => outs.First(o => !o.AlongX && o.Line == line).W;
                    if (a0 > ux0) Lower(x0m - he, a0 * Cell - he, End(ux0), true, wallHead);
                    if (a1 < ux1) Lower(a1 * Cell + he, x1m + he, End(ux1), false, wallHead);
                }
            }

            // ---- the top roof: a house's gable (the editor's RoofShapes, eaves past the walls by its overhang, the walls
            // across the ridge raised into gable ends), a business's flat slab
            var (t0, t1) = res.Footprints[floors - 1];
            float head = floors * Storey;
            if (res.Shape != FootprintShape.Rect)
            {
                if (res.Roof == RoofKind.Flat) res.Walls.AddRange(Slabs(res, head + Slab, SurfaceKind.Roof));
                else CrossGable(res, head);
            }
            else if (res.Roof != RoofKind.Flat)
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
            if (res.Shape != FootprintShape.Rect) return;   // one storey: no stairs

            // ---- the stairs: k1 flight rows against the south wall, centred along the bay. One flight climbs east up row
            // 0; with alternating rows, odd floors climb west up row 1, so each lands beside the next one's foot.
            if (floors < 2) return;
            if (res.Switchback)
            {
                // a switchback: up row 0 from the bay's second column to a landing in its third at half height, then back
                // along row 1 to arrive on the next floor beside its own foot -- every floor's the same, stacked
                float half = Storey * 0.5f, run = WallOpenings.StairDefaultRun(half), x1 = bay.X0 * Cell + Cell;
                float zA = he + WallOpenings.StairHoleMargin, zB = Cell + 0.3f + StairWidth;
                for (int f = 0; f + 1 < floors; f++)
                {
                    float y = f * Storey, t = WallOpenings.StairTreadThickness;
                    res.Stairs.Add(new StairPlan { X = x1, Y = y, Z = zA, Yaw = 270f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                    res.Walls.Add(new WallPlan { X = x1 + run, Y = y + half - t * 0.5f, Z = zB, Yaw = 0f, Pitch = -90f, Length = StairWidth,
                                                 Height = zB - zA, Thickness = t, Kind = SurfaceKind.Stairs });   // the landing
                    res.Stairs.Add(new StairPlan { X = x1 + run, Y = y + half, Z = zB, Yaw = 90f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                }
                return;
            }
            if (res.StairShape == StairShape.Quarter)
            {
                // a quarter turn: up row 0 from the bay's second column to a landing in its third at half height, then
                // round the corner and up the third column (climbing +Z) to arrive on the next floor in row 2 -- every
                // floor's the same, stacked
                float half = Storey * 0.5f, run = WallOpenings.StairDefaultRun(half), m = WallOpenings.StairHoleMargin;
                float zA = he + m, zL = zA + StairWidth;
                // east: up the second column eastward, the landing in the third; west: up the second column westward, the
                // landing in the first -- either way in the corner of the bay that has a wall beside it
                bool west = res.QuarterWest;
                float x0 = bay.X0 * Cell, xL = west ? x0 + he + m : x0 + Cell + run;   // the landing's west edge
                for (int f = 0; f + 1 < floors; f++)
                {
                    float y = f * Storey, t = WallOpenings.StairTreadThickness;
                    if (west)   // yaw 90 climbs -X, its width running -Z from its foot corner
                        res.Stairs.Add(new StairPlan { X = x0 + 2 * Cell, Y = y, Z = zL, Yaw = 90f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                    else        // yaw 270 climbs +X, its width running +Z
                        res.Stairs.Add(new StairPlan { X = x0 + Cell, Y = y, Z = zA, Yaw = 270f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                    res.Walls.Add(new WallPlan { X = xL, Y = y + half - t * 0.5f, Z = zL, Yaw = 0f, Pitch = -90f, Length = StairWidth,
                                                 Height = zL - zA, Thickness = t, Kind = SurfaceKind.Stairs });   // the corner landing
                    // yaw 180 climbs +Z, its width running -X from its foot corner at the landing's east edge
                    res.Stairs.Add(new StairPlan { X = xL + StairWidth, Y = y + half, Z = zL, Yaw = 180f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                }
                return;
            }
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

        /// <summary>An L's or a T's floor, or flat roof, with its top at `top`: the full-length part and the wing, each out
        /// to the outside walls' outer faces -- the full-length one over the wall between them, so they meet once.</summary>
        static IEnumerable<WallPlan> Slabs(BuildingGenResult res, float top, SurfaceKind kind)
        {
            float he = Ext * 0.5f;
            var (wx0, zj, wx1, wz1) = res.Wing;
            yield return SlabPlan(-he, res.CellsX * Cell + he, -he, zj * Cell + he, top, kind);
            yield return SlabPlan(wx0 * Cell - he, wx1 * Cell + he, zj * Cell + he, wz1 * Cell + he, top, kind);
        }

        /// <summary>A plan point turned clockwise from above (x east, z north) by a multiple of 90 degrees about the
        /// origin: what adding `deg` to a yaw does to the way it runs. Exact, so a turned roof meets its walls.</summary>
        static (float X, float Z) Turn(int deg, float x, float z) => (((deg % 360) + 360) % 360) switch
        {
            0 => (x, z), 90 => (z, -x), 180 => (-x, -z), _ => (-z, x),
        };

        /// <summary>An L's or a T's pitched roof: a cross gable. The part that spans more takes the main gable, its ridge
        /// along it and gable ends on both its ends; the rest -- the wing, or, when the wing spans more, the one or two
        /// arms of the long part either side of it -- each a gable of its own, its ridge running square into the main
        /// roof's slope, lower since it spans less. Where an arm's slopes meet the main roof's, both are cut along the valley
        /// (45 degrees in plan: one pitch): the arm's planes start at the valley, and the main roof's slope on that side is a
        /// band above the arm's ridge and two trapezoids below it, each ending at a valley -- the editor's planes taper at
        /// their ends (Inset*), not along their eaves, so every valley is made an end. Neither roof runs on inside the other.
        /// Worked in a frame turned so the main ridge runs along +X and the arm is off its +Z side, and turned back.</summary>
        static void CrossGable(BuildingGenResult res, float head)
        {
            float he = Ext * 0.5f, o = he + RoofOverhang;
            float deg = Math.Clamp(res.RoofPitch, RoofSpec.MinPitch, RoofSpec.MaxPitch), node = deg - 90f;
            float tan = MathF.Tan(deg * MathF.PI / 180f), cos = MathF.Cos(deg * MathF.PI / 180f);
            float W = res.CellsX * Cell, D = res.CellsZ * Cell;
            var (cx0, cz, cx1, _) = res.Wing;
            float wx0 = cx0 * Cell, wx1 = cx1 * Cell, zj = cz * Cell;
            // the main part and the arms off it, in plan metres between wall mid-planes, each arm with the turn that takes
            // local +Z to the side of the main part it is on
            (float X0, float Z0, float X1, float Z1) main;
            var arms = new List<((float X0, float Z0, float X1, float Z1) R, int Turn)>();
            int turn0;
            if (wx1 - wx0 <= zj)
            {
                main = (0f, 0f, W, zj); turn0 = 0;          // ridge along X, the wing off its north side
                arms.Add(((wx0, zj, wx1, D), 0));
            }
            else
            {
                main = (wx0, 0f, wx1, D); turn0 = 90;       // ridge along Z, over the wing and on through the long part
                if (wx1 < W - 0.01f) arms.Add(((wx1, 0f, W, zj), 90));   // east
                if (wx0 > 0.01f) arms.Add(((0f, 0f, wx0, zj), 270));     // west
            }
            (float X0, float Z0, float X1, float Z1) Local(int t, (float X0, float Z0, float X1, float Z1) r)
            {
                var (ax, az) = Turn(-t, r.X0, r.Z0); var (bx, bz) = Turn(-t, r.X1, r.Z1);
                return (Math.Min(ax, bx), Math.Min(az, bz), Math.Max(ax, bx), Math.Max(az, bz));
            }
            // a sloped plane from its eave corner (x, z) at height y, running `len` along local `yaw`, `run` across in plan
            void Plane(int t, float x, float y, float z, float yaw, float len, float run, float l0 = 0f, float r0 = 0f)
            {
                var (px, pz) = Turn(t, x, z);
                res.Walls.Add(new WallPlan { X = px, Y = y, Z = pz, Yaw = (yaw + t) % 360f, Pitch = node, Length = len, Height = run / cos,
                                             Thickness = Slab, Kind = SurfaceKind.Roof, InsetL0 = l0, InsetR0 = r0 });
            }
            // a gable end on the outside wall from (x0, z0) to (x1, z1), the floor on its right: a band where the roof
            // reaches past the wall, the triangle on that -- as the rectangle's (EditorBuildings.PlaceRoof)
            void Gable(int t, float x0, float z0, float x1, float z1, float halfRun)
            {
                var (ax, az) = Turn(t, x0, z0); var (bx, bz) = Turn(t, x1, z1);
                var w = Wall(ax, az, bx, bz, head, Ext);
                w.GableRise = w.Length * 0.5f * tan;
                w.Height = halfRun * tan - w.GableRise;
                res.Walls.Add(w);
            }
            foreach (int t in new[] { turn0, turn0 + 180 })
            {
                var m = Local(t, main);
                float X0 = m.X0 - o, X1 = m.X1 + o, Z1 = m.Z1 + o, hm = (m.Z1 - m.Z0) * 0.5f + o;
                var arm = arms.Where(q => q.Turn == t % 360).Select(q => ((float X0, float Z0, float X1, float Z1)?)Local(t, q.R)).FirstOrDefault();
                if (arm == null) { Plane(t, X0, head, Z1, 0f, X1 - X0, hm); continue; }
                var a = arm.Value;
                float hw = (a.X1 - a.X0) * 0.5f + o, xr = (a.X0 + a.X1) * 0.5f, zA = Z1 - hw, zF = a.Z1 + o;
                // the main slope on this side: below the arm's ridge, a trapezoid either side of it ending at a valley;
                // above it, all of it
                Plane(t, X0, head, Z1, 0f, xr - X0, hw, r0: hw);
                Plane(t, xr, head, Z1, 0f, X1 - xr, hw, l0: hw);
                if (hm - hw > 0.01f) Plane(t, X0, head + hw * tan, Z1 - hw, 0f, X1 - X0, hm - hw);
                // the arm's two slopes, from the valleys out to its gable end
                Plane(t, xr - hw, head, zA, 270f, zF - zA, hw, l0: hw);
                Plane(t, xr + hw, head, zF, 90f, zF - zA, hw, r0: hw);
                Gable(t, a.X0 - he, a.Z1, a.X1 + he, a.Z1, hw);
            }
            // the main roof's own two ends
            var mm = Local(turn0, main);
            float hmm = (mm.Z1 - mm.Z0) * 0.5f + o;
            Gable(turn0, mm.X1, mm.Z1 + he, mm.X1, mm.Z0 - he, hmm);
            Gable(turn0, mm.X0, mm.Z0 - he, mm.X0, mm.Z1 + he, hmm);
        }

        /// <summary>An outside wall of one floor: on the lattice line Line (of constant z when AlongX, else of constant x),
        /// its mid-plane from A0 to A1 metres along that line, facing Face.</summary>
        sealed class Outer { public WallPlan W; public bool AlongX; public int Line; public float A0, A1; public Sides Face; }

        /// <summary>A floor's outside walls, its outline traced from the grid: every unit edge between a room's cell and no
        /// room, chained head to tail with the floor on the right (x east, z north) -- so north faces run +X, east ones -Z,
        /// south -X, west +Z, as the four walls of a rectangle always did -- and merged where they run on straight. From
        /// the north-west corner, so a rectangle's come out north, east, south, west. At an outside corner both walls run on
        /// by half their thickness and meet in a square, as before; at an inside one (an L's, a T's) the wall coming in
        /// runs on by half its thickness and the one going out starts that far along, so the corner is filled once and the
        /// corner solve finds nothing to grow.</summary>
        static List<Outer> Outline(Grid g, float y)
        {
            float he = Ext * 0.5f;
            bool In(int x, int z) => g.At(x, z) != null;
            var next = new Dictionary<(int X, int Z), (int X, int Z)>();
            void Edge((int, int) a, (int, int) b)
            {
                if (!next.TryAdd(a, b)) throw new InvalidOperationException($"BuildingGen: the outline touches itself at {a}");
            }
            for (int z = 0; z <= g.D; z++)
                for (int x = g.Ux0; x < g.Ux1; x++)
                {
                    bool below = In(x, z - 1), above = In(x, z);
                    if (below && !above) Edge((x, z), (x + 1, z));        // a north face
                    else if (above && !below) Edge((x + 1, z), (x, z));   // a south face
                }
            for (int x = g.Ux0; x <= g.Ux1; x++)
                for (int z = 0; z < g.D; z++)
                {
                    bool left = In(x - 1, z), right = In(x, z);
                    if (right && !left) Edge((x, z), (x, z + 1));         // a west face
                    else if (left && !right) Edge((x, z + 1), (x, z));    // an east face
                }
            var start = next.Where(kv => kv.Value.Z == kv.Key.Z && kv.Value.X > kv.Key.X)
                            .OrderByDescending(kv => kv.Key.Z).ThenBy(kv => kv.Key.X).First().Key;
            // the corners, in order
            var pts = new List<(int X, int Z)>();
            var p = start; (int X, int Z) dir = (0, 0);
            int walked = 0;
            do
            {
                var q = next[p];
                var d = (q.X - p.X, q.Z - p.Z);
                if (d != dir) { pts.Add(p); dir = d; }
                p = q;
                walked++;
            } while (p != start && walked <= next.Count);
            // one loop through every edge: a floor in two pieces, or with a hole, is not an outline this can wall
            if (p != start || walked != next.Count || pts.Count < 4) throw new InvalidOperationException("BuildingGen: the outline is not one loop");
            int n = pts.Count;
            (int X, int Z) Dir(int i) { var a = pts[i]; var b = pts[(i + 1) % n]; return (Math.Sign(b.X - a.X), Math.Sign(b.Z - a.Z)); }
            // an outside corner turns right going round this way (x east, z north): the cross product is negative
            bool Convex(int i) { var a = Dir((i + n - 1) % n); var b = Dir(i); return a.X * b.Z - a.Z * b.X < 0; }
            var outs = new List<Outer>();
            for (int i = 0; i < n; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % n]; var d = Dir(i);
                // (its end is a corner either way, where the wall coming in runs on; an inside corner at its start moves the
                // whole wall half a thickness along)
                var w = Convex(i) ? Wall(a.X * Cell, a.Z * Cell, b.X * Cell, b.Z * Cell, y, Ext, he)
                                  : Wall(a.X * Cell + d.X * he, a.Z * Cell + d.Z * he, b.X * Cell + d.X * he, b.Z * Cell + d.Z * he, y, Ext);
                bool alongX = d.Z == 0;
                outs.Add(new Outer
                {
                    W = w, AlongX = alongX, Line = alongX ? a.Z : a.X,
                    A0 = (alongX ? Math.Min(a.X, b.X) : Math.Min(a.Z, b.Z)) * Cell, A1 = (alongX ? Math.Max(a.X, b.X) : Math.Max(a.Z, b.Z)) * Cell,
                    Face = d.X > 0 ? Sides.North : d.X < 0 ? Sides.South : d.Z > 0 ? Sides.West : Sides.East,
                });
            }
            return outs;
        }

        /// <summary>EditorBuildings.SolveCorners, run on the plan, so the walls leave here already joined and the
        /// editor's pass finds nothing to grow: a wall's end within 1.6 m of where it crosses a wall of its storey grows
        /// to it -- to that wall's centreline at a tee, its far face at a corner (the corner's square is otherwise left
        /// out of both). Where a hall turns round a room's corner the partitions met at their centrelines and left that
        /// square open. Short walls -- a roof's infills, standing on a wall head -- are laid joined and left alone: grown
        /// blind they ran on through the upper wall already filling the corner. Openings keep their place when a wall
        /// grows at its start. Returns how many walls grew.</summary>
        static int SolveCorners(List<WallPlan> all)
        {
            const float MaxGrow = 1.6f;
            var ws = all.Where(w => w.Kind == SurfaceKind.Wall || w.Kind == SurfaceKind.Foundation).ToList();
            var gs = new float[ws.Count]; var ge = new float[ws.Count];
            (float X, float Z) Dir(WallPlan w) { var (c, s) = PlanTrig.CosSin(w.Yaw); return (c, -s); }
            for (int i = 0; i < ws.Count; i++)
            {
                var a = ws[i];
                if (a.Kind == SurfaceKind.Wall && a.Height < Storey - 0.01f) continue;
                var ad = Dir(a);
                for (int j = 0; j < ws.Count; j++)
                {
                    var b = ws[j];
                    if (i == j) continue;
                    float dy = MathF.Abs(((a.Yaw - b.Yaw) % 180f + 270f) % 180f - 90f);
                    if (dy < 20f || MathF.Abs(a.Y - b.Y) > 0.6f) continue;
                    var bd = Dir(b);
                    float ex = bd.X * b.Length, ez = bd.Z * b.Length;
                    float den = ad.X * ez - ad.Z * ex;
                    if (MathF.Abs(den) < 1e-6f) continue;
                    float rx = b.X - a.X, rz = b.Z - a.Z;
                    float t = (rx * ez - rz * ex) / den, s2 = (rx * ad.Z - rz * ad.X) / den;
                    if (s2 < -0.02f || s2 > 1.02f) continue;
                    float target = t + (s2 < 0.08f || s2 > 0.92f ? b.Thickness * 0.5f : 0f);
                    if (target < 0f && target >= -MaxGrow) gs[i] = MathF.Max(gs[i], -target);
                    else if (target > a.Length && target <= a.Length + MaxGrow) ge[i] = MathF.Max(ge[i], target - a.Length);
                }
            }
            int grew = 0;
            for (int i = 0; i < ws.Count; i++)
            {
                if (gs[i] <= 1e-4f && ge[i] <= 1e-4f) continue;
                var w = ws[i]; var d = Dir(w);
                w.X -= d.X * gs[i]; w.Z -= d.Z * gs[i];
                w.Length += gs[i] + ge[i];
                for (int k = 0; k < w.Openings.Count; k++) { var o = w.Openings[k]; o.U += gs[i]; w.Openings[k] = o; }
                grew++;
            }
            return grew;
        }

        /// <summary>The hole a switchback in `bay` needs in each floor it climbs to, in plan metres: over its two flights and
        /// its landing, a StairHoleMargin past them but for the flights' foot end, where the next floor's foot is.</summary>
        public static (float X0, float Z0, float X1, float Z1) SwitchbackHole((int X0, int Z0, int X1, int Z1) bay)
        {
            float he = Ext * 0.5f, m = WallOpenings.StairHoleMargin, run = WallOpenings.StairDefaultRun(Storey * 0.5f);
            float x1 = bay.X0 * Cell + Cell;
            return (x1, he, x1 + run + StairWidth + m, Cell + 0.3f + StairWidth + m);
        }

        /// <summary>The hole a quarter turn in `bay` needs in each floor it climbs to, in plan metres, as two rectangles that
        /// meet without overlapping: over the first flight and the corner landing (a StairHoleMargin past them but at the
        /// foot, where the next floor's foot is), and over the second flight up to its top end, where you step off.</summary>
        public static (float X0, float Z0, float X1, float Z1)[] QuarterHole((int X0, int Z0, int X1, int Z1) bay, bool west)
        {
            float he = Ext * 0.5f, m = WallOpenings.StairHoleMargin, run = WallOpenings.StairDefaultRun(Storey * 0.5f);
            float x0 = bay.X0 * Cell, zA = he + m, zL = zA + StairWidth;
            if (west)
            {
                float xL = x0 + he + m;   // the landing's west edge; the first flight's foot is at x0 + 2 cells
                return new[] { (xL - m, he, x0 + 2 * Cell, zL + m), (xL - m, zL + m, xL + StairWidth + m, zL + run) };
            }
            float x1 = x0 + Cell, xE = x1 + run;
            return new[] { (x1, he, xE + StairWidth + m, zL + m), (xE - m, zL + m, xE + StairWidth + m, zL + run) };
        }

        /// <summary>A flat slab over [x0, x1] x [z0, z1] with its top at `top`: the editor's frame for one -- origin at
        /// (minX, centre height, maxZ), run along +X, depth along -Z, pitched -90.</summary>
        static WallPlan SlabPlan(float x0, float x1, float z0, float z1, float top, SurfaceKind kind)
            => new WallPlan { X = x0, Y = top - Slab * 0.5f, Z = z1, Yaw = 0f, Pitch = -90f, Length = x1 - x0, Height = z1 - z0, Thickness = Slab, Kind = kind };
    }
}
