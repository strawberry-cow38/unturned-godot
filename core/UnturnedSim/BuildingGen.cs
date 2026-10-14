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

    /// <summary>Which marked doors get a porch (strawberry: "porches: floored, separate outdoor roofed areas outside front
    /// and/or back doors with columns supporting the roof"). Auto for a seeded choice.</summary>
    public enum PorchChoice { Auto, None, Front, Back, Both }

    /// <summary>Whether a house's living room gets a fireplace and a chimney up through the roof. Auto for a seeded choice.</summary>
    public enum ChimneyChoice { Auto, None, Yes }

    /// <summary>What a shop sells (strawberry: "shops should use the pallette of the shop they are, ie donuts, hardware etc
    /// etc"): one of the retail shops, whose palettes it wears (BuildingGen.ShopPalettes). None: not a shop -- or, asked of
    /// a Commercial spec, any of them, a seeded draw. A doughnut shop is a Cafe (strawberry: "so if its not doughnuts its a
    /// cafe pallette"): no retail building is a donut shop, so there is no palette of its own to wear.</summary>
    public enum Business { None, Cafe, Clothes, Diner, Grocer, Guns, Hardware, Botanist }

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
        /// <summary>The outline: Auto is mostly rectangles, now and then an L or a T. With floors above, they stand on the
        /// L's or T's full-depth part and the rest stays one storey (see BuildingGenResult.Wing); one that cannot be laid
        /// out that way comes out a rectangle, with a note.</summary>
        public FootprintShape Shape;
        /// <summary>Porches outside the front and/or back door: Auto is a house's front door now and then, its back door
        /// less often, a business's front a canopy sometimes.</summary>
        public PorchChoice Porches;
        /// <summary>A fireplace in the living room, its chimney up through every floor and out of the roof (strawberry:
        /// "chimneys, living rooms may [have one] that extends up and out the roof"). Auto: half the houses with a living room.</summary>
        public ChimneyChoice Chimney;
        /// <summary>Width x Depth is a PLOT the building must fit inside (a town plot's building zone), not a size its
        /// rooms may outgrow: the generator takes fewer floors' worth of rooms, more floors, and smaller rooms (down to
        /// each kind's minimum) before it would ever exceed it, and says what it gave up in a note.</summary>
        public bool FitPlot;
        /// <summary>Room sizes scaled toward each kind's minimum: 0 as designed, 1 and 2 smaller, 3 every room at its least
        /// (set by FitPlot's search; a fitted plot's rooms are padded back out to fill it).</summary>
        public int Compact;
        /// <summary>Stop once the ground floor fixes the footprint: FitPlot's search measures plans this way.</summary>
        internal bool PlanOnly;
        /// <summary>No garage among the rooms added to reach MinRooms or drawn as an extra: FitPlot's last compromise for a
        /// house whose garage -- two cells by three, for a car -- leaves its plot no whole plan.</summary>
        internal bool NoGarage;
        /// <summary>A Commercial building's shop (its palettes are that shop's); None for a seeded one.</summary>
        public Business Business;

        internal BuildingGenSpec Copy()
        {
            var c = (BuildingGenSpec)MemberwiseClone();
            c.MinOfKind = new Dictionary<RoomKind, int>(MinOfKind);
            return c;
        }
    }

    /// <summary>One generated room on the 3 m lattice: the cells [X0, X1) x [Z0, Z1) of its floor, and any further
    /// rectangles in More (a hall's stair bay, a stretch of landing) -- the room is their union.</summary>
    public sealed class GenRoom
    {
        public RoomKind Kind;
        public int Floor, X0, Z0, X1, Z1;
        /// <summary>The flat it belongs to in an apartment block, numbered across the whole building; -1 for shared space
        /// (the corridor, the stairwell) and for every room of any other kind of building.</summary>
        public int Unit = -1;
        /// <summary>A flat's way in: the one room of it that may have a door to shared space (its living room, or its own
        /// entry hall), and only from the corridor.</summary>
        public bool Entry;
        /// <summary>The room's HVAC record (FurnishHvac): its vents, null for a room with none. The same record its label in
        /// BuildingGenResult.Rooms carries, and the save keeps.</summary>
        public RoomHvac Hvac;
        /// <summary>The room's electrical record (FurnishElectrical): its switch, outlets and ceiling lights, null for a room
        /// with no switch or outlet. The same record its label carries, and the save keeps.</summary>
        public RoomElectrical Electrical;
        public readonly List<(int X0, int Z0, int X1, int Z1)> More = new List<(int, int, int, int)>();
        public IEnumerable<(int X0, int Z0, int X1, int Z1)> Rects
        { get { yield return (X0, Z0, X1, Z1); foreach (var r in More) yield return r; } }
        public int Cells => Rects.Sum(r => (r.X1 - r.X0) * (r.Z1 - r.Z0));
        public bool Circulation => Kind == RoomKind.Hall || Kind == RoomKind.Stairwell;
        public bool HasCell(int x, int z) => Rects.Any(r => x >= r.X0 && x < r.X1 && z >= r.Z0 && z < r.Z1);
        /// <summary>Is this plan point (metres) in the room? Half-open, so a point is in at most one room.</summary>
        public bool Holds(float x, float z) => Rects.Any(r => x >= r.X0 * BuildingGen.Cell && x < r.X1 * BuildingGen.Cell
                                                            && z >= r.Z0 * BuildingGen.Cell && z < r.Z1 * BuildingGen.Cell);
        public override string ToString() => $"{Kind}@{Floor} [{X0},{Z0}..{X1},{Z1}]" + (More.Count > 0 ? $"+{More.Count}" : "") + (Unit >= 0 ? $" flat {Unit}" : "");
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

    /// <summary>Two rooms that open into each other (strawberry, 2026-10-13: "allow buildings to actually omit interior
    /// walls, within reason. ie a kitchen can open into a living room. but not a bathroom into a kitchen or something.
    /// both rooms keep their room definitions, floor paints, but can open into eachother"): the wall between them on a
    /// lattice line -- AlongX, the line of constant z = Line, else of constant x -- over cells [S0, S1) along it, cut away.
    /// The wall itself stays, as the editor's rooms need it: they are derived from wall centrelines (RoomEnclosure), so a
    /// wall left out would make the two rooms one enclosure and orphan one of their designations. What is left of it is a
    /// pier at each end and a beam overhead -- the retail kit's own "open interior span" (WallOpenings: a garage, a porch
    /// and an open span are one shape), cut as BuildWalls' ArchPorch opening.</summary>
    public sealed class GenOpening
    {
        public int Floor;
        public GenRoom A, B;
        public bool AlongX;
        public int Line, S0, S1;
        public override string ToString() => $"{A}<->{B} open at {(AlongX ? "z" : "x")}={Line} [{S0},{S1})";
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
        /// <summary>What a Commercial building sells, which chose its palette (BuildingGen.ShopPalettes); None for every other kind.</summary>
        public Business Business;
        public RoofKind Roof;
        public float RoofPitch;
        /// <summary>Walls with their openings, footings, a floor per storey and the roofs, in building-local metres: the
        /// ground floor's outside walls' mid-planes enclose (0, 0)..(CellsX, CellsZ) x LatticeStep, and its floor is y 0.</summary>
        public List<WallPlan> Walls = new List<WallPlan>();
        public List<StairPlan> Stairs = new List<StairPlan>();
        public List<RoomDesignation> Rooms = new List<RoomDesignation>();
        public List<GenRoom> Layout = new List<GenRoom>();
        public List<GenDoor> Doors = new List<GenDoor>();
        /// <summary>The walls cut away between rooms that open into each other (BuildingGen.OpenPlan): an open plan.</summary>
        public List<GenOpening> OpenPlan = new List<GenOpening>();
        /// <summary>Pairs of rooms whose door swings one way only, though it might swing both: one of them came out short of
        /// what it is for beside the double sweep, and the building was built again with the door a plain swing (BuildOut).</summary>
        public List<(GenRoom A, GenRoom B)> SingleSwing = new List<(GenRoom A, GenRoom B)>();
        /// <summary>What stands in the rooms (BuildingGen.Furnish): retail props by name.</summary>
        public List<FurniturePlan> Furniture = new List<FurniturePlan>();
        /// <summary>The pieces built at load from their inputs rather than read off an OBJ (FurniturePlan.Gen): curtains and
        /// rugs (FurnishTextiles). Kept out of Furniture, which every placement rule reads as solid catalog props -- a rug
        /// lies under the couch, a curtain hangs in the window's own berth -- and saved with it (Props).</summary>
        public List<FurniturePlan> RuntimeDecor = new List<FurniturePlan>();
        /// <summary>The HVAC vents (FurnishHvac): a floor register and a wall grille a room, in the order their Ids number them.
        /// Props of the catalog's, kept out of Furniture as the runtime pieces are -- a register is a cover you walk over, not
        /// a piece standing in the way -- and saved with it (Props).</summary>
        public List<FurniturePlan> HvacVents = new List<FurniturePlan>();
        /// <summary>The HVAC record of the room a building-local plan point is in on a storey, or null (no room there, or one
        /// with no vents). RoomDesignations.HvacAt asks the same of a saved layout.</summary>
        public RoomHvac HvacAt(float x, float z, int floor) => Layout.FirstOrDefault(g => g.Floor == floor && g.Holds(x, z))?.Hvac is { HasHvac: true } h ? h : null;
        /// <summary>The light switches and outlets (FurnishElectrical), in the order their Ids number them: wall props of the
        /// catalog's, kept out of Furniture as the vents are, and saved with it (Props).</summary>
        public List<FurniturePlan> Electrical = new List<FurniturePlan>();
        /// <summary>The electrical record of the room a building-local plan point is in on a storey, or null, as HvacAt.</summary>
        public RoomElectrical ElectricalAt(float x, float z, int floor) => Layout.FirstOrDefault(g => g.Floor == floor && g.Holds(x, z))?.Electrical is { HasElectrical: true } e ? e : null;
        /// <summary>The windows a room's layout changed after the walls were cut (strawberry: "allow room layout to alter
        /// windows after the fact if necessary"): each written straight into its wall's Openings, so the wall, its glass,
        /// its collision, the save and the décor all see the window as it now is. This is the record of what changed.</summary>
        public List<WindowEdit> WindowEdits = new List<WindowEdit>();
        /// <summary>Where each window was free to be (BuildWalls): its share of its stretch of wall, which a room's layout
        /// may slide it along (Furnish). The generator's own bookkeeping, in wall U (which TurnHalf keeps).</summary>
        internal List<WindowSlot> WindowSlots = new List<WindowSlot>();
        public IEnumerable<PropDesignation> Props => Furniture.Concat(RuntimeDecor).Concat(HvacVents).Concat(Electrical).Select(m => new PropDesignation(m.Name, m.X, m.Y, m.Z, m.Yaw, m.Gen));
        /// <summary>The marked doors: the front one always, the back one when there is one.</summary>
        public GenDoor Front => Doors.FirstOrDefault(d => d.Role == DoorRole.Front);
        public GenDoor Back => Doors.FirstOrDefault(d => d.Role == DoorRole.Back);
        public IEnumerable<DoorDesignation> DoorMarks => Doors.Where(d => d.Role != DoorRole.Inside).Select(d => new DoorDesignation(d.Role, d.Middle.X, d.Middle.Z, d.Floor));
        /// <summary>The stair bay in cells, the same on every floor: the flight rows and the row of floor beside them.
        /// All zero for one floor.</summary>
        public (int X0, int Z0, int X1, int Z1) Bay;
        /// <summary>An L or a T: its wing in cells, the part narrower than the rest -- the rest the full CellsX long, the
        /// wing on its north side as laid out (south, once turned half round). All zero for a rectangle. With floors
        /// above, the wing's columns are the MAIN block: the floor above stands on exactly [Wing.X0, Wing.X1), the full
        /// depth, and what the full-length row has past it -- an arm at one end of an L, at both ends of a T, Wing.Z0
        /// deep -- stays one storey under a shed leaning on the main block's end wall.</summary>
        public (int X0, int Z0, int X1, int Z1) Wing;
        public FootprintShape Shape = FootprintShape.Rect;
        /// <summary>Each porch's deck in plan metres, against the outside wall it serves.</summary>
        public List<(float X0, float Z0, float X1, float Z1)> Porches = new List<(float, float, float, float)>();
        /// <summary>The chimney's footprint in plan metres -- the breast inside, the wall, and the stack standing out past
        /// the wall's outer face -- or all zero for none.</summary>
        public (float X0, float Z0, float X1, float Z1) Chimney;
        /// <summary>How its stairs climb: one straight flight (a bay four cells by two or three rows); a switchback, two
        /// half flights side by side turning back at a landing (three cells by two rows); or a quarter turn, a half flight
        /// along the wall to a corner landing and one away from it (three cells by three rows).</summary>
        public StairShape StairShape;
        public bool Switchback => StairShape == StairShape.Switchback;
        /// <summary>A compound flight's landing is at the bay's west end, not its east (a quarter turn turning the other
        /// way, a switchback laid mirror-wise): the end the floors above start from, so on every floor the landing stands
        /// in the corner of that end wall and the south wall (strawberry: "the landing of the stairs is only supported by
        /// 1 wall, should require two"). As laid out: a building turned half round has its landing at the other end.</summary>
        public bool LandingWest;
        /// <summary>Its kitchen has the dining table in it, and it has no dining room.</summary>
        public bool KitchenDiner;
        /// <summary>Sides with no windows, in the plan as laid out (before any turn to put the front where it was asked).</summary>
        public Sides NoWindowSides;
        /// <summary>Where it could not do what was asked, said so (a minimum that did not fit, a limit it broke).</summary>
        public List<string> Notes = new List<string>();
        /// <summary>The spec the plan was drawn from when FitPlot changed it to fit (fewer rooms, more floors, smaller
        /// rooms...), else null: what the building actually answers to.</summary>
        public BuildingGenSpec Fitted;
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
    /// The ground floor may be an L or a T (strawberry: "does the layout support non-square footprints"): its north row
    /// of rooms comes up short of the south one at an end or both, and the outside walls follow the outline. One storey,
    /// a house's roof is a cross gable (CrossGable). With floors above, the north row is exactly the length of the floor
    /// above and the floors above stand on its columns, the full depth -- the upper-floor plan (a landing hall with rooms
    /// either side) needs the full depth, and the south row alone is three or four cells -- so the full-length row's
    /// arm(s) past it stay one storey, roofed like any lower part the floor above leaves uncovered.
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
        /// <summary>How far a storey's floor slab stops short of the outside walls' outer faces: its edge sits inside the
        /// wall, not flush with its face. Flush, the slab's edge (the floor's colour: a surface's edges wear its front face's
        /// tint, WallSurface) fought the wall's face for the same pixels and showed outside as a floor-coloured band between
        /// storeys and at the base (strawberry: "floor color is adding a floor colored strip to the exterior walls").</summary>
        public const float SlabInset = 0.05f;
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
            foreach (var m in res.Furniture.Concat(res.RuntimeDecor).Concat(res.HvacVents).Concat(res.Electrical)) { m.X = W - m.X; m.Z = D - m.Z; m.Yaw = Y(m.Yaw); }
            for (int i = 0; i < res.Rooms.Count; i++) { var r = res.Rooms[i]; res.Rooms[i] = new RoomDesignation(r.Kind, W - r.X, D - r.Z, r.Floor); }
            foreach (var g in res.Layout) g.Hvac = g.Hvac?.Map(v => v with { X = W - v.X, Z = D - v.Z, Yaw = Y(v.Yaw) });
            foreach (var g in res.Layout) g.Electrical = g.Electrical?.Map(v => v with { X = W - v.X, Z = D - v.Z, Yaw = Y(v.Yaw) });
            foreach (var g in res.Layout)
            {
                (g.X0, g.X1, g.Z0, g.Z1) = (cx - g.X1, cx - g.X0, cz - g.Z1, cz - g.Z0);
                for (int i = 0; i < g.More.Count; i++) { var q = g.More[i]; g.More[i] = (cx - q.X1, cz - q.Z1, cx - q.X0, cz - q.Z0); }
            }
            foreach (var d in res.Doors) { d.Line = d.AlongX ? cz - d.Line : cx - d.Line; d.At = (d.AlongX ? W : D) - d.At; }
            foreach (var o in res.OpenPlan) { o.Line = o.AlongX ? cz - o.Line : cx - o.Line; (o.S0, o.S1) = o.AlongX ? (cx - o.S1, cx - o.S0) : (cz - o.S1, cz - o.S0); }
            for (int i = 0; i < res.Footprints.Count; i++) res.Footprints[i] = (cx - res.Footprints[i].X1, cx - res.Footprints[i].X0);
            res.Bay = (cx - res.Bay.X1, cz - res.Bay.Z1, cx - res.Bay.X0, cz - res.Bay.Z0);
            if (res.Shape != FootprintShape.Rect) res.Wing = (cx - res.Wing.X1, cz - res.Wing.Z1, cx - res.Wing.X0, cz - res.Wing.Z0);
            for (int i = 0; i < res.Porches.Count; i++) { var q = res.Porches[i]; res.Porches[i] = (W - q.X1, D - q.Z1, W - q.X0, D - q.Z0); }
            if (res.Chimney.X1 > res.Chimney.X0) res.Chimney = (W - res.Chimney.X1, D - res.Chimney.Z1, W - res.Chimney.X0, D - res.Chimney.Z0);
            LabelRecords(res);
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
            // a bathroom's: shallow, high and glazed (strawberry: "change the bathrooms from using 'vent' windows to small glass
            // windows, horizontal slit windows", then "widen the bathroom windows horizontally", then "make the bathroom
            // window slit 1/3 as wide": 3.5 m, now a third of it)
            (3.5f / 3f, 0.6f, 2.6f, true),
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
            RoomKind.Garage => new Trait(GarageWide * GarageDeep, 2, FloorPref.GroundOnly, ArchGarage),   // 2 x 3 cells: see MaxCells
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

        /// <summary>May a door join these two rooms? In anything but a block of flats, always (every room's Unit is -1). In
        /// one: within a flat, between two shared rooms, or between the corridor and a flat's way in (its Entry) -- never
        /// from one flat into another, nor into a flat any other way (strawberry: "shared corridor and staircases").</summary>
        public static bool MayJoin(GenRoom a, GenRoom b) => a.Unit == b.Unit
            || (a.Unit < 0 ? a.Kind == RoomKind.Hall && b.Entry : b.Unit < 0 && b.Kind == RoomKind.Hall && a.Entry);

        /// <summary>Inside a flat, its bedrooms, bathroom and study open off its living room or its own entry hall -- not off
        /// its kitchen, and its one bathroom not off a bedroom, where a guest would walk through someone's room to it.</summary>
        public static bool OffInAFlat(RoomKind child, RoomKind parent) => child is RoomKind.Bedroom or RoomKind.Bathroom or RoomKind.Office
            ? parent is RoomKind.LivingRoom or RoomKind.Hall : MayOpenOff(child, parent);

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
            // a block of flats: its lobby -- the stairwell, on the ground floor -- or the corridor, never a flat
            BuildingKind.Apartment => new[] { RoomKind.Stairwell, RoomKind.Hall },
            _ => new[] { RoomKind.Hall },
        };

        /// <summary>The rooms a back door may open out of, best first: a house's kitchen (or hall, or the dining room's
        /// garden door), a shop's stockroom or kitchen, a works' warehouse or workshop, an office block's kitchen or hall.</summary>
        public static RoomKind[] BackDoorOrder(BuildingKind b) => b switch
        {
            BuildingKind.Commercial => new[] { RoomKind.Storage, RoomKind.Hall, RoomKind.BreakRoom },
            BuildingKind.Industrial => new[] { RoomKind.Warehouse, RoomKind.Workshop, RoomKind.Storage, RoomKind.Hall },
            BuildingKind.Residential => new[] { RoomKind.Kitchen, RoomKind.Hall, RoomKind.DiningRoom },
            // a block of flats: out of the end of its corridor (a flat has no door of its own to outside)
            BuildingKind.Apartment => new[] { RoomKind.Hall },
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
            // what EVERY flat has (BuildingGen.Apartments), and what a bigger one adds: a second bedroom, or a study
            BuildingKind.Apartment => (new[] { RoomKind.LivingRoom, RoomKind.Kitchen, RoomKind.Bathroom, RoomKind.Bedroom },
                                       new[] { RoomKind.Bedroom, RoomKind.Office }),
            _ => (new[] { RoomKind.Office, RoomKind.Bathroom, RoomKind.Storage },
                  new[] { RoomKind.Office, RoomKind.BreakRoom, RoomKind.Storage, RoomKind.Office }),
        };

        /// <summary>The retail palettes that suit a kind of building (content/wall_palettes.tsv names). A retail business's or
        /// service's own palette (a fire station's, a mechanic's, a diner's) is for that business (strawberry: "only the
        /// specified businesses/services should use those palletes, industry opting for apartment pallettes", then "office
        /// should use office pallette, shops should use the pallette of the shop they are"): industry wears the plain
        /// apartment block's (ApartmentPalettes), an office building an office's, and a shop its own shop's -- every shop's
        /// here, one shop's with its Business (ShopPalettes). Never a bank's, a post office's, a library's or a mall's: those
        /// are services and landmarks, not shops; nor a police station's, a clinic's, a school's or a barracks'.</summary>
        public static string[] PalettesFor(BuildingKind b) => b switch
        {
            BuildingKind.Residential => new[] { "House_00", "House_01", "House_02", "House_03", "House_04", "House_05", "House_06", "House_07", "House_08", "House_09" },
            // (a house drew from these too until a block of flats was a kind of its own: see Generate)
            BuildingKind.Apartment => ApartmentPalettes(),
            BuildingKind.Commercial => Shops.SelectMany(ShopPalettes).ToArray(),
            BuildingKind.Industrial => ApartmentPalettes(),
            _ => new[] { "Office_0", "Office_1", "Office_2", "Office_3" },
        };
        /// <summary>A kind's palettes, a shop's its own business's: what Generate draws a building's from.</summary>
        public static string[] PalettesFor(BuildingKind b, Business shop) => b == BuildingKind.Commercial && shop != Business.None ? ShopPalettes(shop) : PalettesFor(b);
        /// <summary>The shops a Commercial building may be, drawn among when its spec names none.</summary>
        public static readonly Business[] Shops = { Business.Cafe, Business.Clothes, Business.Diner, Business.Grocer, Business.Guns, Business.Hardware, Business.Botanist };
        /// <summary>Each shop's own palettes, measured off its retail buildings (Botanist_0: a storefront under a "Botanist"
        /// sign with planters at its windows -- a plant shop).</summary>
        public static string[] ShopPalettes(Business shop) => shop switch
        {
            Business.Cafe => new[] { "Cafe_0", "Cafe_1" },
            Business.Clothes => new[] { "Clothes_0", "Clothes_1" },
            Business.Diner => new[] { "Diner_0", "Diner_1", "Diner_2" },
            Business.Grocer => new[] { "Grocer_0" },
            Business.Guns => new[] { "Guns_0" },
            Business.Hardware => new[] { "Hardware_0" },
            Business.Botanist => new[] { "Botanist_0" },
            _ => Array.Empty<string>(),
        };
        /// <summary>The apartment block's palettes: a block of flats', and an industrial building's (PalettesFor). Each call
        /// a fresh array, as every kind's.</summary>
        static string[] ApartmentPalettes() => new[] { "Apartment_0", "Apartment_1", "Apartment_2", "Apartment_3" };

        /// <summary>Each retail palette's floor texels, measured off its own mesh (strawberry: "the floors need the actual
        /// floor colors for the pallete of the building, and we should also do diff materials per room"): Floor, the
        /// texel covering the most up-facing area at its ground level (a wood brown in a house, a grey in an office);
        /// Tile, its lightest neutral texel that is neither wall, floor nor concrete; Concrete, its mid grey (texel 6,
        /// 7e7e7e, in most -- the retail buildings' own second floor colour, where they have one).</summary>
        public static readonly Dictionary<string, (int Floor, int Tile, int Concrete)> FloorTexels = new Dictionary<string, (int, int, int)>
        {
            ["Airport_0"] = (4, 3, 6),
            ["Apartment_0"] = (4, 3, 6),
            ["Apartment_1"] = (4, 3, 6),
            ["Apartment_2"] = (4, 7, 6),
            ["Apartment_3"] = (4, 7, 1),
            ["Bank_0"] = (4, 3, 6),
            ["Barn_0"] = (6, 2, 1),
            ["Barn_1"] = (6, 2, 4),
            ["Barracks_0"] = (4, 7, 6),
            ["Barracks_2"] = (4, 7, 6),
            ["Botanist_0"] = (4, 2, 6),
            ["Cafe_0"] = (4, 3, 1),
            ["Cafe_1"] = (4, 3, 6),
            ["Castle_0"] = (5, 7, 2),
            ["Clothes_0"] = (4, 3, 6),
            ["Clothes_1"] = (4, 5, 6),
            ["Diner_0"] = (4, 3, 6),
            ["Diner_1"] = (4, 5, 6),
            ["Diner_2"] = (4, 7, 6),
            ["Drill"] = (7, 5, 6),
            ["Fire_0"] = (6, 2, 6),
            ["Grocer_0"] = (4, 3, 6),
            ["Guns_0"] = (4, 5, 6),
            ["Hardware_0"] = (6, 4, 6),
            ["House_00"] = (5, 2, 1),
            ["House_01"] = (5, 3, 6),
            ["House_02"] = (5, 2, 6),
            ["House_03"] = (5, 7, 6),
            ["House_04"] = (4, 5, 6),
            ["House_05"] = (4, 3, 6),
            ["House_06"] = (4, 5, 6),
            ["House_07"] = (4, 3, 6),
            ["House_08"] = (4, 7, 6),
            ["House_09"] = (4, 3, 6),
            ["Library_0"] = (4, 7, 6),
            ["Mall_0"] = (4, 5, 6),
            ["Mechanic_0"] = (4, 3, 6),
            ["Medic_0"] = (4, 2, 6),
            ["Medic_1"] = (4, 3, 1),
            ["Medic_2"] = (4, 3, 6),
            ["Office_0"] = (4, 3, 6),
            ["Office_1"] = (4, 3, 6),
            ["Office_2"] = (4, 3, 6),
            ["Office_3"] = (4, 3, 6),
            ["Police_0"] = (4, 5, 6),
            ["Police_1"] = (4, 7, 6),
            ["Post_0"] = (6, 4, 6),
            ["Research_0"] = (4, 0, 6),
            ["School_0"] = (4, 3, 6),
            ["School_1"] = (4, 3, 6),
            ["Ship_0"] = (4, 3, 6),
            ["Ship_1"] = (4, 3, 6),
        };

        /// <summary>The texel a room's floor wears: tile where water is, concrete where vehicles and stock are, the
        /// building's floor colour everywhere else. -1 for "the palette's default" when the palette is not in the table.</summary>
        public static int FloorTexelFor(string palette, RoomKind k)
        {
            if (palette == null || !FloorTexels.TryGetValue(palette, out var t)) return -1;
            return k switch
            {
                RoomKind.Bathroom or RoomKind.Kitchen or RoomKind.BreakRoom => t.Tile,
                RoomKind.Garage or RoomKind.Workshop or RoomKind.Warehouse or RoomKind.Storage => t.Concrete,
                _ => t.Floor,
            };
        }

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
            /// <summary>Where the stream is, and back to there: so a room can try a layout and, giving it up, draw as if it never had.</summary>
            public ulong Mark => _s;
            public void Rewind(ulong mark) => _s = mark;
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

        /// <summary>Generate a building. With `FitPlot` and a Width x Depth, the footprint is a cap: plans are drawn with
        /// growing compromises -- smaller rooms first (padded back out to the plot, so they cost least), then more floors,
        /// then fewer rooms a floor, then fewer rooms -- and the first that fits is built, with a note of what it gave up.
        /// A building may come out smaller than its plot (the yard takes the rest); if nothing fits, the least oversized
        /// plan is built, said so.</summary>
        public static BuildingGenResult Generate(BuildingGenSpec spec)
        {
            // (a block of flats fits itself -- corridor, stairwell and whole flats, not rooms a floor: BuildingGenApartments)
            if (!spec.FitPlot || spec.Width <= 0 || spec.Depth <= 0 || spec.Kind == BuildingKind.Apartment) return GenerateOnce(spec);
            int maxF = Math.Max(1, spec.MaxFloors), minF = Math.Clamp(spec.MinFloors, 1, maxF);
            // stairs need a bay two rows deep (on a narrow plot; three elsewhere), a hall and two rows north of it: a
            // shallower plot is one storey
            if (spec.Depth < 5) maxF = minF = 1;
            int floorRooms = Math.Min(spec.MinRooms, Math.Max(Essentials(spec.Kind).Length, Programme(spec.Kind).Always.Length));
            // the best so far: 0 fits whole; up to Lopsided, fits but rooms came out short of what they need (by how
            // badly); Lopsided, fits with rooms out of proportion; past it, over the plot by that many cells
            const int Lopsided = 1000;
            BuildingGenSpec best = null; BuildingGenResult bestBuilt = null; int bestScore = int.MaxValue, tries = 0;
            // and when nothing fits whole, all of it again without a garage the spec did not ask for (a garage is two
            // cells by three, for a car: strawberry, "yes 2x3"), and the house goes without one, as it would without a room
            bool sawGarage = false;
            for (int pass = 0; pass < 2; pass++)
            {
            if (pass == 1 && (!sawGarage || spec.MinOfKind.ContainsKey(RoomKind.Garage))) break;
            int passTries = 0;
            foreach (int minRooms in new[] { spec.MinRooms, spec.MinRooms - 1, floorRooms }.Where(m => m >= floorRooms).Distinct())
                foreach (int perFloor in new[] { spec.MaxRoomsPerFloor, 4, 3, 2 }.Where(p => p <= spec.MaxRoomsPerFloor).Distinct())
                    for (int floors = minF; floors <= maxF; floors++)
                        // (never so few rooms a floor that a room the kind always has is dropped)
                        if (floors * perFloor >= Programme(spec.Kind).Always.Length || perFloor == spec.MaxRoomsPerFloor)
                        // (an L or a T is a block and a narrower wing: on a narrow plot, a rectangle)
                        foreach (var shape in spec.Shape == FootprintShape.Auto ? new[] { FootprintShape.Auto, FootprintShape.Rect } : new[] { spec.Shape })
                        foreach (int compact in new[] { 0, 1, 2, 3 })
                        {
                            if (++passTries > FitTries) goto nextPass;
                            tries++;
                            var s = spec.Copy();
                            s.Shape = shape; s.Compact = compact; s.MaxFloors = floors; s.MinFloors = Math.Min(minF, floors);
                            s.MaxRoomsPerFloor = perFloor; s.MinRooms = minRooms; s.PlanOnly = true; s.NoGarage = pass == 1;
                            BuildingGenResult plan;
                            try { plan = GenerateOnce(s); } catch (InvalidOperationException) { continue; }
                            s.PlanOnly = false;
                            sawGarage |= plan.Layout.Any(g => g.Kind == RoomKind.Garage);
                            int over = Math.Max(0, plan.CellsX - spec.Width) + Math.Max(0, plan.CellsZ - spec.Depth);
                            // (a plan squeezed in with its rooms out of proportion -- a shop floor at a third of its size
                            // beside an office at its full one -- is a fit of last resort, after every other compromise)
                            int score = over > 0 ? Lopsided + over : Proportioned(plan) ? 0 : Lopsided;
                            BuildingGenResult r = null;
                            if (score == 0)
                            {
                                // drawn in full: a room squeezed to its least can come out without what it is for
                                r = GenerateOnce(s);
                                r.Fitted = s;
                                score = Shortfall(r).Sum(x => x.Weight);
                            }
                            if (score < bestScore) { best = s; bestBuilt = r; bestScore = score; }
                            if (score > 0) continue;
                            var gave = new List<string>();
                            if (shape != spec.Shape) gave.Add("a rectangle, not an L or a T");
                            if (compact > 0) gave.Add($"rooms smaller (compact {compact})");
                            if (floors > minF) gave.Add($"{floors} floors");
                            if (perFloor < spec.MaxRoomsPerFloor) gave.Add($"at most {perFloor} rooms a floor");
                            if (minRooms < spec.MinRooms) gave.Add($"{minRooms} rooms, not {spec.MinRooms}");
                            if (pass == 1) gave.Add("no garage");
                            r.Notes.Add($"fitted the {spec.Width}x{spec.Depth} plot as {r.CellsX}x{r.CellsZ}" + (gave.Count > 0 ? ": " + string.Join(", ", gave) : ""));
                            return r;
                        }
            nextPass: ;
            }
            var closest = bestBuilt ?? (best == null ? GenerateOnce(spec) : GenerateOnce(best));
            closest.Fitted = best;
            closest.Notes.Add(bestScore < Lopsided
                ? $"fitted the {spec.Width}x{spec.Depth} plot as {closest.CellsX}x{closest.CellsZ}, but short of {string.Join(", ", Shortfall(closest).Select(x => x.What))}"
                : bestScore == Lopsided ? $"fitted the {spec.Width}x{spec.Depth} plot as {closest.CellsX}x{closest.CellsZ}, but only with rooms out of proportion"
                : $"could not fit the {spec.Width}x{spec.Depth} plot: {closest.CellsX}x{closest.CellsZ} is the closest of {tries} plans");
            return closest;
        }

        /// <summary>What a drawn building is short of, as "Kind: need", weighted 2 for what a room is for (a bed, a fridge, a
        /// window) and 1 for the rest (a table's chairs, a shop's shelves, a kitchen's counter at its sink or stove, the table
        /// and a chair of a house that eats in its kitchen): every room furnished as its kind needs, every
        /// room that needs daylight lit by a window in an outside wall, every ground-floor garage and warehouse with its
        /// vehicle door. (FitPlot's search asks: a room drawn at its least can come out without -- a kitchen-diner with no
        /// floor clear of its three doors' walks for a table, a corner shop whose one street wall is its door.)</summary>
        /// (`unfurnished` and `unlit`, when given, collect the rooms short of their furniture and of their daylight.)
        static List<(string What, int Weight)> Shortfall(BuildingGenResult r, HashSet<GenRoom> unfurnished = null, HashSet<GenRoom> unlit = null)
        {
            var miss = new List<(string What, int Weight)>();
            bool Has(GenRoom g, params string[] ns) => r.Furniture.Any(m => m.Room == g && ns.Any(n => m.Name.StartsWith(n)));
            void Need(GenRoom g, string what, bool ok, int weight = 2) { if (!ok) { miss.Add(($"{g.Kind}: {what}", weight)); unfurnished?.Add(g); } }
            foreach (var g in r.Layout)
                switch (g.Kind)
                {
                    case RoomKind.Bedroom: Need(g, "bed", Has(g, "Bed")); break;
                    case RoomKind.LivingRoom: Need(g, "couch", Has(g, "Couch")); break;
                    case RoomKind.DiningRoom: Need(g, "table", Has(g, "Table")); Need(g, "chairs", r.Furniture.Count(m => m.Room == g && m.Name.StartsWith("Chair")) >= 4, 1); break;
                    case RoomKind.Kitchen:
                        Need(g, "fridge", Has(g, "Fridge")); Need(g, "sink", Has(g, "Counter_1")); Need(g, "stove", Has(g, "Oven"));
                        // a plain counter at the sink or stove; and where the house eats in its kitchen, a table with a chair
                        Need(g, "counter", Worktop(r.Furniture.Where(m => m.Room == g)), 1);
                        if (EatsInKitchen(r, g)) Need(g, "dining table", Has(g, "Table_Wood_0", "Table_Wood_1", "Table_Wood_3") && Has(g, "Chair_Wood_0"), 1);
                        break;
                    case RoomKind.Bathroom: Need(g, "tub or sink", Has(g, "Tub", "Counter_1")); break;
                    case RoomKind.Office: Need(g, "desk", Has(g, "Table_Wood_2")); break;
                    case RoomKind.BreakRoom: Need(g, "table", Has(g, "Table"), 1); Need(g, "sink", Has(g, "Counter_1")); Need(g, "fridge", Has(g, "Fridge")); break;
                    case RoomKind.Shop: Need(g, "shelves", Has(g, "Shelf_1"), 1); break;
                    case RoomKind.Warehouse: case RoomKind.Storage: Need(g, "racks", Has(g, "Shelf"), 1); break;
                }
            // what each outside wall's openings let into: the room a metre in from the middle of each
            var lit = new HashSet<GenRoom>(); var vehicle = new HashSet<GenRoom>();
            foreach (var w in r.Walls.Where(w => w.Kind == SurfaceKind.Wall && w.Thickness == Ext))
            {
                int f = (int)MathF.Round(w.Y / Storey);
                float a = w.Yaw * MathF.PI / 180f, dx = MathF.Cos(a), dz = -MathF.Sin(a), nx = MathF.Sin(a), nz = MathF.Cos(a);
                foreach (var o in w.Openings)
                {
                    float u = o.U + o.Width * 0.5f, cx = w.X + dx * u - nx, cz = w.Z + dz * u - nz;
                    var g = r.Layout.FirstOrDefault(q => q.Floor == f && q.Holds(cx, cz));
                    if (g == null) continue;
                    if (o.Glazed) lit.Add(g);
                    if (o.Archetype == ArchGarage) vehicle.Add(g);
                }
            }
            foreach (var g in r.Layout)
            {
                // (a building with a side asked to have no windows is let off its daylight: a room may face only that side)
                if (Habitable(g.Kind) && r.NoWindowSides == Sides.None && !lit.Contains(g)) { miss.Add(($"{g.Kind}: window", 2)); unlit?.Add(g); }
                if (g.Floor == 0 && g.Kind is RoomKind.Garage or RoomKind.Warehouse && !vehicle.Contains(g)) miss.Add(($"{g.Kind}: vehicle door", 2));
            }
            return miss;
        }

        /// <summary>Do a plan's rooms keep to their sizes? What is left of a living room beside its stairs is no smaller than
        /// its kitchen or dining room, and the rooms of a row share its length by their kinds' sizes, within a cell's worth
        /// (1.5x: two and three cells). Service rooms are kept narrow on purpose; a hall bent round a bay is no row.</summary>
        static bool Proportioned(BuildingGenResult r)
        {
            var bay = r.Bay;
            int Usable(GenRoom g) => g.Cells - (bay.X1 > bay.X0 && g.Floor == 0 && g.HasCell(bay.X0, bay.Z0) ? (bay.X1 - bay.X0) * (bay.Z1 - bay.Z0) : 0);
            foreach (var living in r.Layout.Where(g => g.Kind == RoomKind.LivingRoom))
                if (r.Layout.Any(o => o.Floor == living.Floor && (o.Kind == RoomKind.DiningRoom || o.Kind == RoomKind.Kitchen) && o.Cells > Usable(living))) return false;
            // (a garage is kept to a car's size, as a service room to its own: neither is in a row's share)
            foreach (var row in r.Layout.Where(g => !g.Circulation && !Service(g.Kind) && g.Kind != RoomKind.Garage && g.More.Count == 0).GroupBy(g => (g.Floor, g.Z0, g.Z1)))
            {
                if (row.Count() < 2) continue;
                var share = row.Select(g => Usable(g) / (double)(g.Kind == RoomKind.Kitchen && r.KitchenDiner ? DinerCells : TraitOf(g.Kind, r.Kind).Cells)).ToList();
                if (share.Max() > share.Min() * 1.5 + 1e-6) return false;
            }
            return true;
        }

        /// <summary>The most plans FitPlot measures before it settles for the closest.</summary>
        const int FitTries = 160;

        static BuildingGenResult GenerateOnce(BuildingGenSpec spec)
        {
            var rng = new Rng(spec.Seed);
            var kind = spec.Kind;
            var res = new BuildingGenResult { Kind = kind };
            // a shop is one kind of shop, and wears its palettes: the one asked for, else one drawn on a stream of its own
            // (so the draw below, and everything after it, stays where it was)
            if (kind == BuildingKind.Commercial)
                res.Business = spec.Business != Business.None ? spec.Business : Shops[new Rng(unchecked(spec.Seed * 7919 + 0x5409)).Int(Shops.Length)];
            var pals = PalettesFor(kind, res.Business);
            // A house drew from fourteen palettes, its ten and the four apartment ones, until a block of flats became a
            // kind of its own (strawberry: "apartments should function differently from houses"). The same draw over the
            // same fourteen keeps every house that drew a house palette exactly as it was; one that drew an apartment's
            // draws again among the houses, from a stream of its own so nothing after it moves.
            int pick = rng.Int(kind == BuildingKind.Residential ? pals.Length + PalettesFor(BuildingKind.Apartment).Length : pals.Length);
            res.Palette = pals[pick < pals.Length ? pick : new Rng(unchecked(spec.Seed * 7349 + 0x5041)).Int(pals.Length)];
            // a house's top roof is a gable or, now and then, a hip; a business's is flat
            res.Roof = kind == BuildingKind.Residential ? (rng.Chance(35) ? RoofKind.Hip : RoofKind.Gable) : RoofKind.Flat;
            res.RoofPitch = res.Roof != RoofKind.Flat ? GablePitches[rng.Int(GablePitches.Length)] : 0f;
            // a block of flats is laid out its own way (BuildingGenApartments.cs) -- corridor, stairwell, flats -- and joins
            // this one for its doors (Connect, which keeps every door inside a flat or at its way in), walls, roof, furniture
            if (kind == BuildingKind.Apartment) return Apartments(spec, res, rng, Connect);
            int maxFloors = Math.Max(1, spec.MaxFloors), minFloors = Math.Clamp(spec.MinFloors, 1, maxFloors);
            int perFloor = Math.Max(1, spec.MaxRoomsPerFloor), capacity = maxFloors * perFloor;
            // a kitchen with the dining table in it is a size up (see `diner` below)
            bool diner = false;
            Trait T0(RoomKind r) => r == RoomKind.Kitchen && diner ? new Trait(DinerCells, 3, FloorPref.Ground, ArchWindow) : TraitOf(r, kind);
            // a compact building's rooms: smaller toward each kind's minimum (a plot to fit, see FitPlot)
            Trait T(RoomKind r)
            {
                var t = T0(r);
                if (spec.Compact <= 0) return t;
                int side = Math.Max(2, t.MinSide - spec.Compact);
                int cells = Math.Max(side * 2, spec.Compact >= 3 ? 0 : (int)Math.Ceiling(t.Cells * (spec.Compact == 1 ? 0.7 : 0.5)));
                return new Trait(Math.Min(t.Cells, cells), side, t.Pref, t.Window);
            }
            int Width(RoomKind r, int depth) => Math.Max(Math.Max(2, T(r).MinSide), (int)Math.Ceiling(T(r).Cells / (double)depth));

            // ---- the programme: the kind's own rooms, raised to the minimums, filled to MinRooms, and a seeded extra
            var (always, more) = Programme(kind);
            if (spec.NoGarage) more = more.Where(k => k != RoomKind.Garage).ToArray();
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
            // ---- the outline: a rectangle, or an L or a T -- drawn from a stream of its own, so a rectangle is the same
            // building it was before the outline could be anything else. With floors above, they stand on the outline's
            // full-depth part and the rest stays one storey (LtMulti, below).
            var shapeRng = new Rng(unchecked(spec.Seed * 7919 + 0x4C54));
            var shape = spec.Shape;
            if (shape == FootprintShape.Auto)
            {
                int roll = shapeRng.Int(100);
                shape = roll < 25 ? FootprintShape.L : roll < 35 ? FootprintShape.T : FootprintShape.Rect;
            }
            res.Floors = floors;
            bool multi = floors > 1;
            // compound stairs for small spaces (strawberry: "add support for compound stairs (stairs that stop halfway up,
            // then change directions) for small spaces"): up one row to a landing halfway, then back along the next row
            // (a switchback, three cells by two rows) or round the corner and up the bay's last column (a quarter turn --
            // "doesnt have to be a 180 staircase either, can be 90 too" -- three cells by three rows); in a building whose
            // floors are small, and now and then in any. They stack straight up.
            bool compound = multi && (rooms.Sum(r => T(r).Cells) / floors <= SmallFloor || rng.Chance(25));
            // (a plot too narrow for a room beside the bay -- FitPlot -- takes a switchback, two rows deep: the hall runs right
            // behind it, below)
            // (the roll is drawn exactly where it always was, so every other building keeps its stairs and its stream)
            bool narrowPlot(int bayLen) => spec.FitPlot && spec.Width > 0 && spec.Width < bayLen + 2;
            res.StairShape = !compound ? StairShape.Straight : rng.Chance(QuarterTurns) && !narrowPlot(3) ? StairShape.Quarter : StairShape.Switchback;
            int k1 = compound || floors <= 2 ? 1 : 2, L = multi ? (compound ? 3 : 4) : 0;   // the stair bay (see below)
            int bayRows = res.StairShape switch { StairShape.Switchback => 2, StairShape.Quarter => 3, _ => k1 + 1 };
            // a compound flight stops at a landing half way up, which stands at the bay's stairsWest end: that end goes
            // against a wall -- the end of its room, or of the building -- so the landing has two, that and the south wall
            bool landing = res.StairShape != StairShape.Straight;
            // A room's share of a row: its target area -- the stair room's grown by the bay it gives over to the stairs
            // (strawberry: "the living room, which is further eaten by stairs") -- and how far past that it may grow:
            // three quarters again, a small room one cell. Growing by share rather than by turns is what keeps a dining
            // room from outgrowing the living room ("make sure rooms are scaled proportionately to what they are").
            // (by the size a kind is DESIGNED at: a compact building's rooms start smaller but grow back alike, so a shop
            // floor stays the big room beside an office -- only its least width is compact, in HostWidth and Width)
            int Target(RoomKind r, bool host) => T0(r).Cells + (host ? L * bayRows : 0);
            // (a garage not past its own width: it is wide for its vehicle door, and deep for the car)
            int Cap(RoomKind r, int depth, bool host = false) => Service(r) ? Width(r, depth) + 1 : r == RoomKind.Garage ? Width(r, depth)
                                                             : Math.Max(Width(r, depth), (int)(1.75f * Target(r, host) / depth));
            int HostWidth(RoomKind r, int depth) => Math.Max(L, (int)Math.Ceiling((T(r).Cells + L * bayRows) / (double)depth));

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
            int overflowStores = 0;
            // With `hard`, never past CapCells either (twice a kind's size; the stair room's bay on top).
            void Grow(List<(RoomKind k, int w)> list, ref int spare, int depth, Func<RoomKind, bool> which, RoomKind host, bool capped = true, bool hard = false)
            {
                int hostAt = host == RoomKind.Unassigned ? -1 : list.FindIndex(x => x.k == host);
                while (spare > 0)
                {
                    int best = -1; double low = double.MaxValue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var (k, w) = list[i];
                        if (!which(k) || (capped && w >= Cap(k, depth, i == hostAt))) continue;
                        if (hard && (long)(w + 1) * depth > (long)CapCells(k, kind) + (i == hostAt ? L * bayRows : 0)) continue;
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
                // past the limits now, still by share: the bigger rooms first, the small ones only if there is nothing else --
                // but none past twice its kind's size (strawberry: "try to clamp maximum sizes for each room")
                if (list.Any(x => !Service(x.k))) Grow(list, ref spare, depth, k => !Service(k), host, capped: false, hard: true);
                Grow(list, ref spare, depth, _ => true, host, capped: false, hard: true);
                // what is still left becomes store rooms while the floor has room for them, rather than a 300 m2 room
                bool Spread(RoomKind k) => !Enumerable.Range(0, floors).Any(g => g != f && onFloor[g].Count < perFloor && onFloor[g].Count(x => x == k) < here.Count(x => x == k) + 1);
                while (spare >= 2 && ok(RoomKind.Storage) && here.Count < perFloor && Spread(RoomKind.Storage))
                {
                    int w = Math.Min(spare, Width(RoomKind.Storage, depth) + 1);
                    if (spare - w == 1) w++;
                    list.Insert(rng.Int(list.Count + 1), (RoomKind.Storage, w));
                    here.Add(RoomKind.Storage);
                    spare -= w;
                    overflowStores++;
                }
                // a last odd cell, or a row that takes no store room: anyone, as before
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
                // a garage is a car's width, not a share of the row: what it was given past that goes to the room furthest
                // under its share, and it takes no part in the evening out
                bool Shares(int i) => !Service(list[i].k) && list[i].k != RoomKind.Garage;
                for (int gi = 0; gi < list.Count; gi++)
                    while (list[gi].k == RoomKind.Garage && list[gi].w > Width(RoomKind.Garage, depth)
                           && Enumerable.Range(0, list.Count).Where(Shares).OrderBy(i => Share(i, list[i].w)).FirstOrDefault(-1) is int to && to >= 0)
                    {
                        list[gi] = (list[gi].k, list[gi].w - 1);
                        list[to] = (list[to].k, list[to].w + 1);
                    }
                for (int guard = 0; guard < 64; guard++)
                {
                    var big = Enumerable.Range(0, list.Count).Where(i => Shares(i) && list[i].w > Width(list[i].k, depth)).OrderByDescending(i => Share(i, list[i].w)).FirstOrDefault(-1);
                    var small = Enumerable.Range(0, list.Count).Where(Shares).OrderBy(i => Share(i, list[i].w)).FirstOrDefault(-1);
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
            // a store room may be added on floor fl, which has n of them, only if no other floor with space has fewer
            bool StoreOk(int fl, int n) => !Enumerable.Range(0, floors).Any(g => g != fl && onFloor[g].Count < perFloor && onFloor[g].Count(x => x == RoomKind.Storage) < n + 1);
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
            // a plot with no room beside the bay: the row past the stairs would be landing (two thirds of a 12 m house's upper
            // floor was hall), so the hall runs right behind the stairs and the rooms north of it get the row
            if (multi && narrowPlot(L) && bayRows < dS) dS = bayRows;
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
            res.LandingWest = stairsWest;   // that end has the wall on every floor, so a compound flight's landing goes there

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

            // An L or a T with floors above. They stand on the north row's columns, the full depth (the landing hall and
            // its rooms either side need it; the south row alone is three or four cells), so the north row -- the wing --
            // is exactly the length of the floor above, `need`, and starts where that floor does; the full-length south row
            // runs on past it, one storey, at one end (an L) or both (a T), two cells a side at the least. Ux0: where the
            // floors above start for a bay at b, as they are placed below; ArmsOk: whether that leaves the arms the shape has.
            bool LtMulti() => multi && shape != FootprintShape.Rect;
            int Ux0(int b) => stairsWest ? b : b + L - need;
            bool ArmsOk(int b, int w)
            {
                int west = Ux0(b), east = w - Ux0(b) - need;
                if (west < 0 || east < 0) return false;
                return shape == FootprintShape.T ? west >= 2 && east >= 2 : Math.Min(west, east) == 0 && Math.Max(west, east) >= 2;
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
            // an L or a T that no layout fits -- with floors above, its wing must be just the floor above's length -- is
            // drawn again a rectangle, from scratch: what giving up a side or a size said of the L is not true of it
            int notes0 = res.Notes.Count, wantW0 = wantW, wantD0 = wantD;
            for (int outline = 0; outline < 2 && ground == null; outline++)
            {
            if (outline == 1)
            {
                if (shape == FootprintShape.Rect) break;
                res.Notes.RemoveRange(notes0, res.Notes.Count - notes0);
                res.Notes.Add($"no {(shape == FootprintShape.L ? "L" : "T")} layout fitted {(multi ? $"{floors} floors" : "its rooms")}: it came out a rectangle");
                shape = FootprintShape.Rect;
                frontAsked = spec.FrontSide; wantW = wantW0; wantD = wantD0;
            }
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
            // (what a draw that is thrown away said -- two bathrooms it could not part -- goes with it)
            int passNotes = res.Notes.Count;
            for (int attempt = 0; attempt < 14 && ground == null; attempt++)
            {
                res.Notes.RemoveRange(passNotes, res.Notes.Count - passNotes);
                bool bands = attempt < 9 ? bandsFirst : !bandsFirst;
                var here = new List<RoomKind>(onFloor[0]);
                wingCut = (0, 0);
                var plan = bands ? Bands(here, attempt >= 6 && attempt < 9 || attempt >= 12) : Corridor(here, attempt >= 12);
                if (plan == null) continue;
                var (prs, pw, pd, pb) = plan.Value;
                TuckServiceRooms(prs, pd, kind, perFloor, StoreOk);
                var g = new Grid(pw, pd, 0, pw);
                foreach (var r in prs) g.Paint(r);
                var doors = new List<GenDoor>();
                // the dining room beside the kitchen, or facing it across a hall (strawberry: "require dining room to be
                // adjascent or at least nearby a kitchen"); the last, safe draws take what they get and say so
                bool apart = !KitchenNearDining(g, prs);
                bool lopsided = !Proportioned(prs, multi ? (pb, 0, pb + L, bayRows) : (0, 0, 0, 0));
                // (and a compound flight's landing against its end of the bay: not hall or more of its own room past it)
                bool unwalled = multi && landing && !LandingWalled(g, pb);
                // (and every garage with an outside wall on a side it runs GarageDeep cells back from, for its vehicle door:
                // a car fits in behind it -- on the last draws, a garage that has none is a store room instead, with a note,
                // and the house goes without one as it goes without a room that does not fit)
                var shallow = prs.Where(r => r.Kind == RoomKind.Garage && !Runs(g).Any(u => u.B == null && u.A == r && u.S1 - u.S0 >= GarageWide
                                                                                           && (u.AlongX ? r.Z1 - r.Z0 : r.X1 - r.X0) >= GarageDeep)).ToList();
                if ((apart || lopsided || unwalled || shallow.Count > 0) && attempt < 12) continue;
                foreach (var r in shallow)
                {
                    r.Kind = RoomKind.Storage;
                    if (here.IndexOf(RoomKind.Garage) is int gi && gi >= 0) here[gi] = RoomKind.Storage;
                    res.Notes.Add("no garage could be laid deep enough for a car: a store room instead");
                }
                if (!Connect(g, prs, 0, null, doors, multi ? (pb, 0, pb + L, bayRows) : (0, 0, 0, 0), frontAsked, ref turn)) continue;
                if (apart) res.Notes.Add("the dining room could not be put beside the kitchen");
                if (lopsided) res.Notes.Add("the living room came out smaller than the dining room or kitchen");
                if (unwalled) res.Notes.Add("the stairs' landing on the ground floor has a wall on one side only");
                ground = g; W = pw; D = pd; bx0 = pb;
                res.Plan = bands ? "bands" : "corridor";
                // an L or a T: the wing is the north row (and in a corridor plan what is left of it as hall)
                if (wingCut.Len > 0 && wingCut.Len < pw)
                {
                    int zj = prs.Where(r => r.X0 < wingCut.X0 || r.X1 > wingCut.X0 + wingCut.Len).Max(r => r.Z1);
                    res.Shape = shape;
                    res.Wing = (wingCut.X0, zj, wingCut.X0 + wingCut.Len, pd);
                    // a hip has nothing to hip into at the wing: an L or a T of one storey is gabled (with floors above,
                    // the top roof is over the main block, a rectangle, and the arms have sheds of their own)
                    if (res.Roof == RoofKind.Hip && !multi) res.Roof = RoofKind.Gable;
                }
                res.Layout.AddRange(prs);
                res.Doors.AddRange(doors);
                onFloor[0] = here;
            }
            if (ground == null) res.Notes.RemoveRange(passNotes, res.Notes.Count - passNotes);
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
                TuckServiceRooms(rs, D, kind, perFloor, StoreOk);
                var g = new Grid(W, D, ux0, ux1);
                foreach (var r in rs) g.Paint(r);
                var doors = new List<GenDoor>();
                if (!Connect(g, rs, f, hall, doors, (bx0, 0, bx0 + L, bayRows), frontAsked, ref turn))
                    res.Notes.Add($"floor {f}: not every room could be given a door");
                // (the floors above start or end at the bay's stairsWest end, so a landing there is in their corner)
                if (landing && f + 1 < floors && !LandingWalled(g, bx0)) res.Notes.Add($"floor {f}: the stairs' landing has a wall on one side only");
                res.Layout.AddRange(rs);
                res.Doors.AddRange(doors);
                res.Footprints.Add((ux0, ux1));
                grids.Add(g);
            }
            foreach (var r in res.Layout)
                res.Rooms.Add(new RoomDesignation(r.Kind, (r.X0 + r.X1) * 0.5f * Cell, (r.Z0 + r.Z1) * 0.5f * Cell, r.Floor));
            if (spec.PlanOnly) return res;

            // some doorways between two rooms you walk through are left open, and some of those are wide
            // (in houses: a business's rooms keep their doors -- a shop floor is not open to its back rooms)
            // (dining room to kitchen always: strawberry, "prefer arches over doors for dining -> kitchen")
            foreach (var d in res.Doors)
                if (kind == BuildingKind.Residential && d.B != null && AccessOf(d.A.Kind) == Access.Through && AccessOf(d.B.Kind) == Access.Through
                    && (rng.Chance(OpenDoorway) | Pair(d.A.Kind, d.B.Kind, RoomKind.DiningRoom, RoomKind.Kitchen)))
                {
                    d.Open = true;
                    // not wide into a small kitchen: its counters need the wall
                    d.Wide = rng.Chance(WideDoorway) && !new[] { d.A, d.B }.Any(r => r.Kind == RoomKind.Kitchen && r.Cells < 12);
                }
            res.NoWindowSides = turn ? Turned(spec.NoWindows) : spec.NoWindows;
            // walls, porches, the chimney, furniture -- and now and then the wall between two of them left out altogether
            BuildOut(res, grids, k1, L, spec, spec.Porches, chimney: true);
            if (turn) TurnHalf(res);
            return res;

            // Does a compound flight's landing, in the bay at bx, have a wall at its end of the bay -- another room or the
            // outside past it, on every row it spans (a switchback's two, a quarter turn's one)? The south wall it always has.
            bool LandingWalled(Grid g, int bx)
            {
                int col = stairsWest ? bx : bx + L - 1, past = stairsWest ? bx - 1 : bx + L;
                for (int z = 0; z < (res.StairShape == StairShape.Switchback ? 2 : 1); z++)
                    if (g.At(past, z) == g.At(col, z)) return false;
                return true;
            }

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
                // (the kitchen and dining room stay in band A when anything else can go, and cross together when
                // nothing else can, so they always share a band and can sit side by side)
                bool KD(RoomKind r) => r == RoomKind.Kitchen || r == RoomKind.DiningRoom;
                List<RoomKind> Moving(RoomKind m) { var mv = KD(m) ? A.Where(KD).ToList() : new List<RoomKind> { m }; return mv.Count >= A.Count ? new List<RoomKind> { m } : mv; }
                // an L or a T with floors above: band B is the wing, just the floor above's length (LtMulti) -- rooms cross
                // back to A while it is longer than that, and to it while it is short of it and one fits: a wing of one
                // small room padded out to the floor above's length was two 16-cell bathrooms side by side
                bool lt = LtMulti();
                int ends = shape == FootprintShape.T ? 2 : 1;
                for (int guard = 0; guard < 12; guard++)
                {
                    int la = LenA(), lb = LenB();
                    if (lt && lb > need && B.Count > 1) { var m = B[rng.Int(B.Count)]; B.Remove(m); A.Add(m); continue; }
                    var fromA = A.Where(r => r != host || A.Count(x => x == host) > 1).ToList();
                    // (and the garage stays on the entrance side, band A, when anything else can cross: strawberry, "prefer
                    // garage on the front door side of the house")
                    if (fromA.Any(r => r != RoomKind.Garage)) fromA = fromA.Where(r => r != RoomKind.Garage).ToList();
                    if (fromA.Any(r => !KD(r))) fromA = fromA.Where(r => !KD(r)).ToList();
                    if (lt) fromA = fromA.Where(r => lb + Moving(r).Sum(q => Width(q, dB)) <= need).ToList();
                    bool toB = (lt ? lb < need - 1 : la > lb + 3) || B.Count == 0;
                    if (toB && fromA.Count > 0 && A.Count > 1)
                    {
                        var m = fromA[rng.Int(fromA.Count)];
                        var moving = Moving(m);
                        // (a dining room holding the stairs stays, and its kitchen with it: band A keeps the stair room)
                        if (multi && moving.Contains(host) && A.Count(x => x == host) <= moving.Count(x => x == host)) break;
                        foreach (var mv in moving) { A.Remove(mv); B.Add(mv); }
                        continue;
                    }
                    if (!lt && (lb > la + 3 || A.Count == 0) && B.Count > 1) { var m = B[rng.Int(B.Count)]; B.Remove(m); A.Add(m); }
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
                int w, bx, bl;
                if (lt)
                {
                    // the wing just the floor above's length, where that floor starts (once the bay is placed); the row
                    // past it the arms' length on at the least, two or three cells a side
                    if (rowB.Sum(x => x.w) > need) return null;
                    w = Math.Max(Math.Max(rowA.Sum(x => x.w), need + (2 + shapeRng.Int(2)) * ends), Math.Max(4, wantW));
                    (bx, bl) = (0, need);
                }
                else
                {
                    w = Math.Max(Math.Max(Math.Max(rowA.Sum(x => x.w), rowB.Sum(x => x.w)), Math.Max(need, 4)), wantW);
                    (bx, bl) = Cut(ref w, rowB.Sum(x => x.w));
                }
                if (Pad(rowA, w, dA, here, 0, r => AccessOf(r) != Access.Private, multi ? host : RoomKind.Unassigned) > 0) return null;
                if (Pad(rowB, bl, dB, here, 0, _ => true) > 0) return null;
                // (a wing whose small rooms had to grow to fill the floor above's length is not one to keep -- it made 12-
                // and 16-cell bathrooms, which nothing could tuck back: no row neighbour to take a strip and the outside
                // across from it. A corridor plan's north row has a hall to take the length instead)
                if (lt && rowB.Any(x => Service(x.k) && x.w > Width(x.k, dB))) return null;
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
                        // in a shop, at the far end from its street front; a compound flight in the corner of its room its
                        // landing stands in (strawberry: "corner stairs need to be up against 2 supporting walls", and "the
                        // landing of the stairs is only supported by 1 wall, should require two")
                        if (lo <= hi) b = shopEnd && k == RoomKind.Shop ? (shopWest ? hi : lo) : lo + rng.Int(hi - lo + 1);
                        int corner = stairsWest ? x : x + wd - L;
                        if (landing) b = corner >= lo && corner <= hi ? corner : -1;
                        // an L's or a T's floors above stand on the wing: where they start must leave the row its arms
                        if (lt && b >= 0 && !ArmsOk(b, w))
                        {
                            var ok = Enumerable.Range(lo, Math.Max(0, hi - lo + 1)).Where(q => ArmsOk(q, w) && (!landing || q == corner)).ToList();
                            b = ok.Count == 0 ? -1 : ok[rng.Int(ok.Count)];
                        }
                    }
                    x += wd;
                }
                if (multi && b < 0) return null;
                if (lt) { bx = Ux0(b); wingCut = (bx, need); }
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
                // an L or a T with floors above: the north row is the wing, just the floor above's length (LtMulti), and
                // the south row has the arms besides
                bool lt = LtMulti();
                int ends = shape == FootprintShape.T ? 2 : 1;
                // (the kitchen and dining room go to one side together, so they can sit side by side)
                foreach (var r in Seeded(here.Where(r => r != RoomKind.DiningRoom || !here.Contains(RoomKind.Kitchen)), sS))
                {
                    var pair = r == RoomKind.Kitchen && here.Contains(RoomKind.DiningRoom) ? new[] { r, RoomKind.DiningRoom } : new[] { r };
                    int lenS = L + S.Sum(x => x.w) + pair.Sum(p => Width(p, sS)), lenN = N.Sum(x => x.w) + pair.Sum(p => Width(p, sN));
                    // (a house's garage on the south side, the side its front faces once it is turned to the street)
                    bool south = (lt ? lenS <= lenN + 2 * ends || lenN > need : lenS <= lenN) || (kind == BuildingKind.Residential && r == RoomKind.Garage);
                    foreach (var p in pair) if (south) S.Add((p, Width(p, sS))); else N.Add((p, Width(p, sN)));
                }
                int w, nx, nl;
                if (lt)
                {
                    if (N.Sum(x => x.w) > need) return null;
                    w = Math.Max(Math.Max(L + S.Sum(x => x.w), need + (2 + shapeRng.Int(2)) * ends), Math.Max(4, wantW));
                    (nx, nl) = (0, need);   // placed once the bay is
                }
                else
                {
                    w = Math.Max(Math.Max(Math.Max(L + S.Sum(x => x.w), N.Sum(x => x.w)), Math.Max(need, 4)), wantW);
                    (nx, nl) = Cut(ref w, N.Sum(x => x.w));
                }
                int aS = Pad(S, w - L, sS, here, 0, _ => true, hall: true), aN = Pad(N, nl, sN, here, 0, _ => true, hall: true);
                rng.Shuffle(S);
                KitchenBesideDining(S); KitchenBesideDining(N);
                ApartBathrooms(S); ApartBathrooms(N);
                // a business's shop at an end of its row, its end wall the street front (the south row's at the end away
                // from the stairs, which go in at the end the floors above start from)
                // (a T's floors above start past an arm: there the shop is that arm, beyond the stairs, its end wall the
                // street front still -- and its wall, not the hall, beside a quarter turn's landing)
                if (kind != BuildingKind.Residential)
                    foreach (var (row, east) in new[] { (S, lt && shape == FootprintShape.T ? !stairsWest : stairsWest), (N, rng.Chance(50)) })
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
                        if (landing) b = stairsWest ? 0 : w - L;   // its landing into the building's corner
                        // an L's or a T's floors above stand on the wing: where they start must leave the row its arms
                        if (lt && !ArmsOk(b, w))
                        {
                            if (landing) return null;
                            var ok = Enumerable.Range(lo, hi - lo + 1).Where(q => ArmsOk(q, w)).ToList();
                            if (ok.Count == 0) return null;
                            b = ok[rng.Int(ok.Count)];
                        }
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
                        int Split()
                        {
                            // (an L's or a T's floors above stand on the wing: any split that leaves the arms, which the
                            // building's end -- no arm on that side -- would not for a T)
                            var splits = safe && !lt ? new List<int> { stairsWest ? 0 : S.Count } : Enumerable.Range(0, S.Count + 1).ToList();
                            // a compound flight: where the side its landing is on is a room's wall or the building's end,
                            // not hall
                            if (landing)
                                splits = splits.Where(i => stairsWest ? i == 0 || S[i - 1].k != RoomKind.Hall : i == S.Count || S[i].k != RoomKind.Hall).ToList();
                            if (!safe || lt) rng.Shuffle(splits);
                            foreach (int i in splits)
                            {
                                int at = S.Take(i).Sum(x => x.w);
                                if ((stairsWest ? at + need <= w : at + L >= need) && (!lt || ArmsOk(at, w))) { b = at; return i; }
                            }
                            return -1;
                        }
                        split = Split();
                        // a T whose south row leaves it no arm on a side (a business's can be its shop and nothing else):
                        // the shortest that gives it one, on the side away from a shop at an end of the row, else the
                        // stairs' side first -- a store room where the floor has space for one more room, else an alcove of
                        // hall (which a quarter turn, needing a wall on its landing's side, will not take there)
                        if (split < 0 && lt && shape == FootprintShape.T)
                        {
                            bool store = here.Count < perFloor && StoreOk(0, here.Count(k => k == RoomKind.Storage));
                            // (away from a shop at an end of the row first: it stays at that end, an arm, its end wall the
                            // street front)
                            bool shopW = S[0].k == RoomKind.Shop, shopE = S[^1].k == RoomKind.Shop;
                            foreach (bool west in shopW != shopE ? new[] { shopE, shopW } : new[] { stairsWest, !stairsWest })
                            {
                                for (int a = 2; a <= 6 && split < 0; a++)
                                {
                                    var arm = (store && a == Width(RoomKind.Storage, sS) ? RoomKind.Storage : RoomKind.Hall, a);
                                    if (west) S.Insert(0, arm); else S.Add(arm);
                                    w += a; hall.X1 = w;
                                    split = Split();
                                    // (an alcove of hall on the side a compound flight's landing is on would leave the landing
                                    // one wall: there, the arm is a hall of its own, walled off from the bay -- Unassigned in
                                    // the row marks it, and it is laid as a Hall)
                                    if (split < 0 && landing && arm.Item1 == RoomKind.Hall)
                                    {
                                        arm = (RoomKind.Unassigned, a);
                                        S[west ? 0 : S.Count - 1] = arm;
                                        split = Split();
                                    }
                                    if (split >= 0) { if (arm.Item1 == RoomKind.Storage) here.Add(RoomKind.Storage); break; }
                                    if (west) S.RemoveAt(0); else S.RemoveAt(S.Count - 1);
                                    w -= a; hall.X1 = w;
                                }
                                if (split >= 0) break;
                            }
                        }
                        if (split < 0) return null;
                    }
                    int x = 0;
                    for (int i = 0; i <= S.Count; i++)
                    {
                        if (multi && i == split) { hall.More.Add((x, 0, x + L, sS)); x += L; }
                        if (i == S.Count) break;
                        if (S[i].k == RoomKind.Hall) hall.More.Add((x, 0, x + S[i].w, sS));
                        else rs.Add(new GenRoom { Kind = S[i].k == RoomKind.Unassigned ? RoomKind.Hall : S[i].k, Floor = 0, X0 = x, X1 = x + S[i].w, Z0 = 0, Z1 = sS });
                        x += S[i].w;
                    }
                }
                if (lt) { nx = Ux0(b); wingCut = (nx, need); }
                int xn = nx;
                foreach (var (k, wd) in N) { rs.Add(new GenRoom { Kind = k, Floor = 0, X0 = xn, X1 = xn + wd, Z0 = sS + 1, Z1 = sS + 1 + sN }); xn += wd; }
                if (aN > 0) hall.More.Add((xn, sS + 1, nx + nl, sS + 1 + sN));
                return (rs, w, sS + 1 + sN, b);
            }

            // =========================================================================================================
            // Doors. A front door on the ground floor (an upper floor is entered by its stairs, into its landing hall);
            // a tree of doors through the walk-through rooms, and a second where two of them belong together; then every
            // dead end off a room it may open off. False when a room cannot be reached. `frontAsked`: the side(s) the front
            // door may go in; `turn` comes back true when it went in the opposite one (the building is then turned).
            // In a block of flats a door joins only rooms that MayJoin -- no door from one flat into another, and into a flat
            // only by its way in, from the corridor -- so the walk-through tree enters each flat once, by its entry.
            bool Connect(Grid g, List<GenRoom> rs, int f, GenRoom root, List<GenDoor> doors, (int X0, int Z0, int X1, int Z1) bay,
                         Sides frontAsked, ref bool turn)
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
                List<GenRoom> Next(GenRoom a) => runs.Where(r => r.A == a || r.B == a).Select(r => r.A == a ? r.B : r.A).Where(b => b != null && MayJoin(a, b)).Distinct().ToList();
                bool Doored(GenRoom a, GenRoom b) => doors.Any(d => (d.A == a && d.B == b) || (d.A == b && d.B == a));

                if (root == null)
                {
                    // the front door: into the most entrance-like walk-through room with an outside wall to take it
                    GenDoor front = null;
                    foreach (var k in EntranceOrder(kind))
                    {
                        foreach (var r in rng.Keyed(rs.Where(r => r.Kind == k && r.Unit < 0)).OrderBy(x => x.Key).Select(x => x.Item).ToList())
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
                        var parents = Next(r).Where(p => reached.Contains(p) && MayOpenOff(r.Kind, p.Kind) && (r.Unit < 0 || OffInAFlat(r.Kind, p.Kind))).ToList();
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
                    var backRooms = rng.Keyed(rs.Where(r => order.Contains(r.Kind) && r.Unit < 0)).OrderBy(x => Array.IndexOf(order, x.Item.Kind)).ThenBy(x => x.Key).Select(x => x.Item).ToList();
                    // a side asked for: after the rooms that suit one, a hall
                    if (backOk != Sides.None) backRooms.AddRange(rs.Where(r => !backRooms.Contains(r) && r.Kind == RoomKind.Hall && r.Unit < 0));
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

        /// <summary>The most cells a small room may have: past it, a bathroom is a hall (strawberry: "try to clamp maximum
        /// sizes for each room. seeing a lot of super bathrooms etc").
        /// A garage is two cells wide by three deep (strawberry, approving it 2026-10-13: "yes 2x3"): her first clamp, "2x
        /// the width of a garage door hole, and 2x the depth", was two cells by two, whose 5.3 to 5.5 m between the walls a
        /// car does not fit -- cow tools measured the game's built cars at 3.0 m across the tyres and up to 6.15 m long
        /// with their fittings (the sedan). Its vehicle door is in a two-cell side and the three cells run back from it.</summary>
        public static int MaxCells(RoomKind k) => k switch { RoomKind.Bathroom => 4, RoomKind.Closet => 4, RoomKind.Storage => 6, RoomKind.Garage => GarageWide * GarageDeep, _ => int.MaxValue };
        /// <summary>A garage's cells: across its vehicle door (twice the door's 4.05 m, floored to whole cells), and back
        /// from it (a 6.15 m car and room to walk round it: 8.4 m or so between the walls).</summary>
        public const int GarageWide = 2, GarageDeep = 3;
        /// <summary>The most cells any room of a kind should have: a small room's MaxCells, else twice its kind's size (a
        /// living room 24, a bedroom 18).</summary>
        public static int CapCells(RoomKind k, BuildingKind b) => MaxCells(k) < int.MaxValue ? MaxCells(k) : k == RoomKind.Hall || k == RoomKind.Stairwell ? int.MaxValue : 2 * TraitOf(k, b).Cells;

        /// <summary>A small room deeper than its kind allows -- it spans its row's whole depth, three to five cells, so a
        /// bathroom two cells wide was 54 m2 and up -- keeps the INNER part of the row, beside the hall or the rooms it opens
        /// off, and the strip behind it along the outside wall goes to a neighbour in the row: a bedroom or office by
        /// preference (an en-suite tucked into its corner), else a hall, else any room but another small one. Without such a
        /// neighbour it stays as it was. A strip only one row deep never goes to a row neighbour, where it would be an arm
        /// sticking out of that room by its end along the outside wall: it goes across, to the hall or the room on its
        /// inner side as an alcove open along its length, or stays. Runs on a floor's rooms before they are painted, so
        /// walls and doors follow. In a block of flats only a room of the same flat takes anything: a strip handed across a
        /// party wall would be a door's worth of one flat inside another.</summary>
        static void TuckServiceRooms(List<GenRoom> rs, int depth, BuildingKind kind, int perFloor, Func<int, int, bool> storeOk)
        {
            foreach (var r in rs.Where(r => MaxCells(r.Kind) < int.MaxValue && r.More.Count == 0).ToList())
            {
                // wider than two cells: hand a column at a time to a row neighbour that stays within its cap
                while (r.X1 - r.X0 > 2 && (r.X1 - r.X0) * (r.Z1 - r.Z0) > MaxCells(r.Kind))
                {
                    int dd = r.Z1 - r.Z0;
                    var side = rs.Where(n => n != r && n.Floor == r.Floor && n.Unit == r.Unit && n.More.Count == 0 && MaxCells(n.Kind) == int.MaxValue && n.Kind != RoomKind.Stairwell
                                             && n.Z0 == r.Z0 && n.Z1 == r.Z1 && (n.X1 == r.X0 || n.X0 == r.X1) && (long)n.Cells + dd <= CapCells(n.Kind, kind))
                                 .OrderBy(n => n.Cells).ThenBy(n => n.X0).FirstOrDefault();
                    // (failing that, one a quarter past its cap: a bedroom 20 cells big is no stranger than a bathroom 12)
                    side ??= rs.Where(n => n != r && n.Floor == r.Floor && n.Unit == r.Unit && n.More.Count == 0 && MaxCells(n.Kind) == int.MaxValue && n.Kind != RoomKind.Stairwell
                                           && n.Z0 == r.Z0 && n.Z1 == r.Z1 && (n.X1 == r.X0 || n.X0 == r.X1) && (long)n.Cells + dd <= (long)CapCells(n.Kind, kind) * 5 / 4)
                               .OrderBy(n => n.Cells + dd - CapCells(n.Kind, kind)).ThenBy(n => n.X0).FirstOrDefault();
                    if (side == null) break;
                    if (side.X1 == r.X0) { side.X1++; r.X0++; } else { side.X0--; r.X1--; }
                }
                int wd = r.X1 - r.X0, d = r.Z1 - r.Z0;
                if (wd * d <= MaxCells(r.Kind)) continue;
                bool south = r.Z0 == 0, north = r.Z1 == depth;
                if (south == north) continue;   // not in an outer row (or the whole depth): leave it
                // (a garage keeps GarageDeep rows, the depth a car needs back from its vehicle door, whatever its width)
                int keep = r.Kind == RoomKind.Garage ? Math.Min(d, GarageDeep) : Math.Max(1, Math.Min(d - 1, MaxCells(r.Kind) / wd));
                if (keep >= d) continue;
                // A strip one row deep handed to a room BESIDE it in the row sticks out of that room by its end, a corridor
                // along the outside wall as long as the small room is wide (strawberry: "rooms tend to want to do this thing
                // where they have a really long thin tumor. which isnt necessary and looks weird"). Handed ACROSS, to the
                // room on its long side, it is an alcove of that room, open to it along its whole length -- so a one-row
                // strip only ever goes across, or stays.
                bool thin = d - keep == 1 && wd >= 2;
                // a garage keeps its OUTER rows -- the outside wall its vehicle door goes in -- and the strip on its inner
                // side goes to the room across, or becomes a store room off the garage (a store opens off a garage)
                if (r.Kind == RoomKind.Garage)
                {
                    var gIn = south ? (X0: r.X0, Z0: r.Z0 + keep, X1: r.X1, Z1: r.Z1) : (X0: r.X0, Z0: r.Z0, X1: r.X1, Z1: r.Z1 - keep);
                    int gCells = (gIn.X1 - gIn.X0) * (gIn.Z1 - gIn.Z0);
                    int gAcross = south ? r.Z1 : r.Z0 - 1;
                    var gOwners = Enumerable.Range(r.X0, wd).Select(x => rs.FirstOrDefault(n => n != r && n.Floor == r.Floor && n.HasCell(x, gAcross))).Distinct().ToList();
                    var gNb = gOwners.Count == 1 ? gOwners[0] : null;
                    if (gNb != null && MaxCells(gNb.Kind) == int.MaxValue && gNb.Kind != RoomKind.Stairwell
                        && (gNb.Kind == RoomKind.Hall || gNb.More.Count == 0 && (long)gNb.Cells + gCells <= (long)CapCells(gNb.Kind, kind) * 5 / 4))
                        Give(gNb, gIn);
                    else if (!thin && rs.Where(n => n != r && n.Floor == r.Floor && MaxCells(n.Kind) == int.MaxValue && n.Kind != RoomKind.Stairwell
                                           && (n.Kind == RoomKind.Hall || n.More.Count == 0) && (n.X1 == r.X0 || n.X0 == r.X1)
                                           && n.Z0 <= gIn.Z0 && n.Z1 >= gIn.Z1 && (long)n.Cells + gCells <= (long)CapCells(n.Kind, kind) * 5 / 4)
                               .OrderBy(n => n.Kind == RoomKind.Hall ? 0 : 1).ThenBy(n => n.Cells).FirstOrDefault() is { } gSide)
                        gSide.More.Add(gIn);   // (or the room beside it in its row takes that strip: an L round the garage's back)
                    else if (gCells <= MaxCells(RoomKind.Storage) && gIn.Z1 - gIn.Z0 >= 1 && gIn.X1 - gIn.X0 >= 2
                             && rs.Count(n => n.Floor == r.Floor && !n.Circulation) < perFloor
                             && storeOk(r.Floor, rs.Count(n => n.Floor == r.Floor && n.Kind == RoomKind.Storage)))
                        rs.Add(new GenRoom { Kind = RoomKind.Storage, Floor = r.Floor, X0 = gIn.X0, Z0 = gIn.Z0, X1 = gIn.X1, Z1 = gIn.Z1 });
                    else continue;   // nothing can take it: the garage stays a row deep
                    if (south) r.Z1 = r.Z0 + keep; else r.Z0 = r.Z1 - keep;
                    continue;
                }
                // the strip behind it, along the outside wall
                var strip = south ? (X0: r.X0, Z0: r.Z0, X1: r.X1, Z1: r.Z1 - keep) : (X0: r.X0 + 0, Z0: r.Z0 + keep, X1: r.X1, Z1: r.Z1);
                int Pref(GenRoom n) => n.Kind is RoomKind.Bedroom or RoomKind.Office ? 0 : n.Kind == RoomKind.Hall ? 1 : 2;
                int stripCells = (strip.X1 - strip.X0) * (strip.Z1 - strip.Z0);
                // the strip on its inner side, and the one room that owns the whole side across it (or none)
                var inner = south ? (X0: r.X0, Z0: r.Z0 + keep, X1: r.X1, Z1: r.Z1) : (X0: r.X0, Z0: r.Z0, X1: r.X1, Z1: r.Z1 - keep);
                int across = south ? r.Z1 : r.Z0 - 1;
                var owners = Enumerable.Range(r.X0, wd).Select(x => rs.FirstOrDefault(n => n != r && n.Floor == r.Floor && n.Unit == r.Unit && n.HasCell(x, across))).Distinct().ToList();
                var inb = owners.Count == 1 ? owners[0] : null;
                if (thin)
                {
                    // the room across takes the inner row: a hall (an alcove leading to its door) first, else a room that
                    // may have it within its cap, but never a small one or the stairwell; the small room keeps the outside
                    // wall. Failing that it stays as it is -- a row over its cap, not an arm on its neighbour.
                    bool Takes(GenRoom n) => n != null && (n.Kind == RoomKind.Hall
                        || MaxCells(n.Kind) == int.MaxValue && n.Kind != RoomKind.Stairwell && n.More.Count == 0 && (long)n.Cells + stripCells <= CapCells(n.Kind, kind));
                    if (Takes(inb)) { Give(inb, inner); if (south) r.Z1 = r.Z0 + keep; else r.Z0 = r.Z1 - keep; }
                    continue;
                }
                // (and only where the neighbour stays within its own cap: a strip that makes a super bedroom is no better)
                // (against its main rectangle, and one strip to a room: a bedroom that took two became a corridor 6 m long
                // round to a closet door, every wall of it facing a door)
                var nb = rs.Where(n => n != r && n.Floor == r.Floor && n.Unit == r.Unit && MaxCells(n.Kind) == int.MaxValue && n.Kind != RoomKind.Stairwell
                                       && (long)n.Cells + stripCells <= CapCells(n.Kind, kind) && (n.Kind == RoomKind.Hall || n.More.Count == 0)
                                       && (n.X1 == r.X0 || n.X0 == r.X1) && n.Z0 <= strip.Z0 && n.Z1 >= strip.Z1)
                           .OrderBy(Pref).ThenBy(n => n.X0 < r.X0 ? 0 : 1).FirstOrDefault();
                // not if that leaves it an island inside its neighbour: a room with that one room on every side
                bool Island(GenRoom taker)
                {
                    var after = south ? (X0: r.X0, Z0: r.Z1 - keep, X1: r.X1, Z1: r.Z1) : (X0: r.X0, Z0: r.Z0, X1: r.X1, Z1: r.Z0 + keep);
                    GenRoom Owner(int x, int z) => z >= strip.Z0 && z < strip.Z1 && x >= strip.X0 && x < strip.X1 ? taker
                        : rs.FirstOrDefault(n => n != r && n.Floor == r.Floor && n.Unit == r.Unit && n.HasCell(x, z));
                    var ring = new List<GenRoom>();
                    for (int x = after.X0; x < after.X1; x++) { ring.Add(Owner(x, after.Z0 - 1)); ring.Add(Owner(x, after.Z1)); }
                    for (int z = after.Z0; z < after.Z1; z++) { ring.Add(Owner(after.X0 - 1, z)); ring.Add(Owner(after.X1, z)); }
                    return ring.All(o => o == taker);
                }
                if (nb != null && Island(nb)) nb = null;
                if (nb == null)
                {
                    // or the other way round: it keeps the OUTER rows (its outside wall, for a vent) and the strip on its
                    // inner side goes to the room across that side -- a hall (a nook leading to its door) or a bedroom or
                    // office within its cap (an en-suite's lobby), when one room owns that whole side
                    if (inb != null && (inb.Kind == RoomKind.Hall
                                        || inb.Kind is RoomKind.Bedroom or RoomKind.Office && inb.More.Count == 0 && (long)inb.Cells + stripCells <= CapCells(inb.Kind, kind)))
                    {
                        Give(inb, inner);
                        if (south) r.Z1 = r.Z0 + keep; else r.Z0 = r.Z1 - keep;
                        continue;
                    }
                    // no room can take it: a closet or store room of its own, opening off a row neighbour it may open off
                    // (two cells deep at least: a one-cell strip has no room for a door and a shelf both)
                    var own = stripCells <= MaxCells(RoomKind.Storage) && strip.Z1 - strip.Z0 >= 2 ? RoomKind.Storage : RoomKind.Unassigned;
                    bool parent = rs.Any(n => n != r && n.Floor == r.Floor && n.Unit == r.Unit && MayOpenOff(own, n.Kind)
                                              && n.Rects.Any(q => (q.X1 == r.X0 || q.X0 == r.X1) && q.Z0 <= strip.Z0 && q.Z1 >= strip.Z1));
                    if (own != RoomKind.Unassigned && rs.Count(n => n.Floor == r.Floor && !n.Circulation) < perFloor
                        && storeOk(r.Floor, rs.Count(n => n.Floor == r.Floor && n.Kind == RoomKind.Storage)) && parent)
                    {
                        rs.Add(new GenRoom { Kind = own, Floor = r.Floor, X0 = strip.X0, Z0 = strip.Z0, X1 = strip.X1, Z1 = strip.Z1 });
                        if (south) r.Z0 = r.Z1 - keep; else r.Z1 = r.Z0 + keep;
                        continue;
                    }
                    // last of all a big room a little past its cap (a quarter at most) beats a super bathroom: the row
                    // neighbour takes the outer strip, or the room across the inner side takes the inner one
                    bool Roomy(GenRoom n) => n != null && MaxCells(n.Kind) == int.MaxValue && n.Kind != RoomKind.Stairwell
                                             && (n.Kind == RoomKind.Hall || n.More.Count == 0) && (long)n.Cells + stripCells <= (long)CapCells(n.Kind, kind) * 5 / 4;
                    var over = rs.Where(n => n != r && n.Floor == r.Floor && n.Unit == r.Unit && Roomy(n) && (n.X1 == r.X0 || n.X0 == r.X1) && n.Z0 <= strip.Z0 && n.Z1 >= strip.Z1 && !Island(n))
                                 .OrderBy(n => n.Cells + stripCells - CapCells(n.Kind, kind)).ThenBy(Pref).FirstOrDefault();
                    if (over != null)
                    {
                        over.More.Add(strip);
                        if (south) r.Z0 = r.Z1 - keep; else r.Z1 = r.Z0 + keep;
                        continue;
                    }
                    if (Roomy(inb))
                    {
                        Give(inb, inner);
                        if (south) r.Z1 = r.Z0 + keep; else r.Z0 = r.Z1 - keep;
                        continue;
                    }
                    continue;
                }
                nb.More.Add(strip);
                if (south) r.Z0 = r.Z1 - keep; else r.Z1 = r.Z0 + keep;
            }
            // A strip handed ACROSS to a room whose main rectangle it runs the whole width of is a row more of that
            // rectangle, not a rectangle of its own: the room stays a rectangle.
            void Give(GenRoom n, (int X0, int Z0, int X1, int Z1) q)
            {
                if (n.X0 == q.X0 && n.X1 == q.X1 && (n.Z1 == q.Z0 || n.Z0 == q.Z1)) { n.Z0 = Math.Min(n.Z0, q.Z0); n.Z1 = Math.Max(n.Z1, q.Z1); }
                else n.More.Add(q);
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

        /// <summary>Rooms that may share one space with no wall between them: the living room, the kitchen, the dining room,
        /// and a hall (circulation, which an open-plan room is walked through anyway).</summary>
        public static bool OpenPlanKind(RoomKind k) => k is RoomKind.LivingRoom or RoomKind.Kitchen or RoomKind.DiningRoom or RoomKind.Hall;
        /// <summary>May the wall between these two rooms be left out (strawberry: "a kitchen can open into a living room. but
        /// not a bathroom into a kitchen")? Both of the open-plan kinds, not two halls; in a house, or inside one flat -- never
        /// the corridor, never a party wall. Never in a business: a shop floor keeps its walls to the back of the house (its
        /// office and break room are private besides), as its doorways keep their doors.</summary>
        public static bool MayOpenPlan(GenRoom a, GenRoom b, BuildingKind kind)
            => (kind == BuildingKind.Residential || (kind == BuildingKind.Apartment && a.Unit >= 0)) && a.Unit == b.Unit
               && OpenPlanKind(a.Kind) && OpenPlanKind(b.Kind) && !(a.Kind == RoomKind.Hall && b.Kind == RoomKind.Hall);
        /// <summary>Percent of homes -- houses, and flats one by one -- with rooms that may open into each other that are
        /// open plan; and of an open-plan home's pairs of such rooms, the percent that open (one at least: OpenPlanRank).
        /// strawberry asked for it "within reason": a home is open plan or it is not, as real ones are, and a little under
        /// half are -- measured, OpenPlan's doc says how many of what.</summary>
        public const int OpenPlanHomes = 45, OpenPlanPairs = 60;
        /// <summary>The least stretch of wall left out, in cells: one cell is a wide doorway's width, not a room opening up.
        /// And the most in one opening: four cells is about 11 m clear between its piers, the widest open span the retail
        /// kit has (WallOpenings.Widths) -- a longer stretch (a hall the length of a living room) is opened in parts of four
        /// cells or fewer, a pier standing between them under the beam.</summary>
        public const int OpenPlanCells = 2, OpenPlanMostCells = 4;
        /// <summary>What is left of a wall left out: a pier this long at each end, off the faces of the walls meeting it (as
        /// a wide doorway keeps off them), and under a floor above a beam this deep below the slab -- the slab's own
        /// underside, a reveal flush with it, would fight it for the same pixels. Under a roof the wall's top half metre,
        /// which the next storey's slab would otherwise fill, is the beam.</summary>
        public const float OpenPier = 0.3f, OpenBeam = 0.3f;

        /// <summary>From the rooms and doors on: the walls, their corners solved, the porches, the chimney and the furniture
        /// -- built, and where what they came out as says so, built again, for one of two reasons. The chimney's stack is
        /// wider at its foot than a window's clear stretch of wall often is, so where every spot for it has a window behind
        /// it, the walls are built again with that wall's windows laid round the spot (BuildWalls' keepClear) -- unless that
        /// leaves a room that had daylight without it, when the chimney goes instead. And open plan (OpenPlan): a room it
        /// leaves short of what it is for -- a kitchen of its counters, a living room of its couch -- has its walls back,
        /// its pairs closed, and the building is built again; after three tries, all of them. Everything rebuilt here is
        /// laid afresh: the walls, stairs, porches, chimney and furniture, the notes they wrote, and the doors' places
        /// (BuildWalls slides a wide one).</summary>
        static void BuildOut(BuildingGenResult res, List<Grid> grids, int k1, int L, BuildingGenSpec spec, PorchChoice porches, bool chimney)
        {
            var doors = res.Doors.ToList();
            var placed = doors.Select(d => (d.At, d.Width)).ToList();
            int notes = res.Notes.Count;
            // (what a pass makes, Reset clears and Built keeps: one added to either goes in the other too -- a building put
            // back with the vents of the build it was put back from had them in its doors' swings)
            void Reset()
            {
                res.Walls.Clear(); res.Stairs.Clear(); res.Porches.Clear(); res.Furniture.Clear(); res.RuntimeDecor.Clear(); res.HvacVents.Clear(); res.Electrical.Clear();
                foreach (var g in res.Layout) { g.Hvac = null; g.Electrical = null; }
                res.WindowEdits.Clear(); res.WindowSlots.Clear();
                res.Chimney = default;
                res.Notes.RemoveRange(notes, res.Notes.Count - notes);
                for (int i = 0; i < doors.Count; i++) { doors[i].At = placed[i].At; doors[i].Width = placed[i].Width; }
                // (the doors between rooms that are still open to each other stay out)
                res.Doors.Clear();
                res.Doors.AddRange(doors.Where(d => !res.OpenPlan.Any(o => o.Floor == d.Floor && ((o.A == d.A && o.B == d.B) || (o.A == d.B && o.B == d.A)))));
            }
            OpenPlan(res, grids, spec.Seed);
            List<(int Floor, bool AlongX, int Line, float A0, float A1)> keep = null;
            (bool AlongX, int Line, float P)? spot = null;
            bool mayMove = true;
            HashSet<GenRoom> unlitBefore = null;
            // (the doors last hung single, how short the building was with them both ways, and that building as built -- what
            // Reset clears -- to go back to without building it again; and the doors tried single for nothing)
            List<(GenRoom A, GenRoom B)> latched = null; int latchedShortBy = 0; Action builtBoth = null;
            var tried = new HashSet<(GenRoom A, GenRoom B)>();
            static Action Keep<T>(List<T> list) { var kept = list.ToList(); return () => { list.Clear(); list.AddRange(kept); }; }
            Action Built()
            {
                var back = new List<Action>
                {
                    Keep(res.Walls), Keep(res.Stairs), Keep(res.Porches), Keep(res.Furniture), Keep(res.RuntimeDecor), Keep(res.HvacVents),
                    Keep(res.Electrical), Keep(res.WindowEdits), Keep(res.WindowSlots), Keep(res.Notes), Keep(res.Doors),
                    Keep(res.Rooms),   // (their labels carry the rooms' records: LabelRecords)
                };
                var chimney = res.Chimney;
                var records = res.Layout.Select(g => (g, g.Hvac, g.Electrical)).ToList();
                var at = doors.Select(d => (d.At, d.Width)).ToList();
                return () =>
                {
                    foreach (var b in back) b();
                    res.Chimney = chimney;
                    foreach (var (g, h, e) in records) { g.Hvac = h; g.Electrical = e; }
                    for (int i = 0; i < doors.Count; i++) (doors[i].At, doors[i].Width) = at[i];
                };
            }
            for (int pass = 0, closes = 0; ; pass++)
            {
                BuildWalls(res, grids, k1, L, keep);
                SolveCorners(res.Walls);
                // (after the corner solve, which must not grow a house wall toward a porch post)
                AddPorches(res, grids[0], porches, spec.Seed);
                var move = chimney ? AddChimney(res, grids, spec.Chimney, spec.Seed, spot, mayMove && spot == null) : null;
                if (move != null)
                {
                    // the only spots for the chimney have windows behind them: build again with them laid round it
                    unlitBefore = new HashSet<GenRoom>(); Shortfall(res, null, unlitBefore);
                    spot = move;
                    keep = ChimneyKeepClear(res, move.Value);
                    Reset();
                    continue;
                }
                if (spot != null && unlitBefore != null)
                {
                    var unlit = new HashSet<GenRoom>(); Shortfall(res, null, unlit);
                    if (!unlit.IsSubsetOf(unlitBefore) || res.Chimney.X1 <= res.Chimney.X0)
                    {
                        // moving the windows left a room in the dark (or the spot failed after all): the walls as they were,
                        // and a chimney only where one is clear of them -- none, as the first build found
                        spot = null; keep = null; mayMove = false; unlitBefore = null;
                        Reset();
                        continue;
                    }
                }
                Furnish(res, spec.Seed);
                var unfurnished = new HashSet<GenRoom>(); int shortBy = Shortfall(res, unfurnished).Sum(x => x.Weight);
                if (res.OpenPlan.Count > 0)
                {
                    int before = res.OpenPlan.Count;
                    if (++closes > 3) res.OpenPlan.Clear();
                    else res.OpenPlan.RemoveAll(o => unfurnished.Contains(o.A) || unfurnished.Contains(o.B));
                    if (res.OpenPlan.Count != before) { latched = null; builtBoth = null; Reset(); continue; }   // (doors hung single stay so)
                }
                // a door that swings both ways is a nicety (strawberry, round 12b: "some may swing both ways"), and its
                // second sweep is floor a room loses: where a room it opens into comes out short of what it is for, the door
                // is hung as a plain swing and the building built again -- kept so only where that leaves the building short
                // of less, else it swings both ways again (a kitchen of 2 x 2 cells lost the counter beside its sink to one,
                // and FitPlot's search dropped a bedroom from the seed-1 house on a 4x5 plot to get it back)
                if (latched != null)
                {
                    if (shortBy >= latchedShortBy)
                    {
                        // no better for it: the doors swing both ways again, in the building as it was
                        res.SingleSwing.RemoveAll(latched.Contains); tried.UnionWith(latched);
                        builtBoth();
                        return;
                    }
                    latched = null; builtBoth = null;
                }
                var single = DoubleSwingDoors(res).Where(p => (unfurnished.Contains(p.A) || unfurnished.Contains(p.B)) && !tried.Contains(p)).Distinct().ToList();
                if (single.Count == 0) return;
                res.SingleSwing.AddRange(single); latched = single; latchedShortBy = shortBy; builtBoth = Built();
                Reset();
            }
        }

        /// <summary>Open plan: the walls between pairs of rooms that MayOpenPlan, left out in some homes -- a house, or a flat
        /// (OpenPlanHomes), and in one of those some of its pairs and one at least (OpenPlanPairs, OpenPlanRank). Every
        /// stretch of 2+ cells where the two face each other is opened, but never beside the stair bay (whose walls the
        /// flights and the landing stand against: strawberry, "the landing of the stairs is only supported by 1 wall, should
        /// require two"). The doors that joined the two go: the opening joins them. The rooms keep their kinds, their floors
        /// (each room's cover is its own rectangles) and their furnishing; the wall's line keeps a wall, so its corners solve
        /// as before and the editor's rooms stay two (GenOpening). Its own rng stream, so a building that opens nothing is
        /// the one it was. (BuildOut closes a pair again where it left a room short of its furniture.)</summary>
        static void OpenPlan(BuildingGenResult res, List<Grid> grids, int seed)
        {
            var orng = new Rng(unchecked(seed * 6007 + 0x4F50));
            var bay = res.Bay;
            bool InBay(int x, int z) => res.Floors > 1 && bay.X1 > bay.X0 && x >= bay.X0 && x < bay.X1 && z >= bay.Z0 && z < bay.Z1;
            // every pair that may open and has a stretch to open, with that stretch: floor by floor, in the order the floor's
            // runs come (a stable order: the draws follow it)
            var pairs = new List<(int Floor, GenRoom A, GenRoom B, List<(bool AlongX, int Line, int S0, int S1)> Spans)>();
            for (int f = 0; f < grids.Count; f++)
                foreach (var r in Runs(grids[f]))
                {
                    if (r.B == null || !MayOpenPlan(r.A, r.B, res.Kind)) continue;
                    int at = pairs.FindIndex(p => (p.A == r.A && p.B == r.B) || (p.A == r.B && p.B == r.A));
                    if (at < 0) { pairs.Add((f, r.A, r.B, new List<(bool, int, int, int)>())); at = pairs.Count - 1; }
                    // the run, less its cells beside the bay
                    int s = r.S0;
                    while (s < r.S1)
                    {
                        bool Bay(int c) => r.AlongX ? InBay(c, r.Line - 1) || InBay(c, r.Line) : InBay(r.Line - 1, c) || InBay(r.Line, c);
                        if (Bay(s)) { s++; continue; }
                        int s0 = s;
                        while (s < r.S1 && !Bay(s)) s++;
                        if (s - s0 >= OpenPlanCells) pairs[at].Spans.Add((r.AlongX, r.Line, s0, s));
                    }
                }
            pairs.RemoveAll(p => p.Spans.Count == 0);
            // home by home -- a house, or a flat -- open plan or not; an open one opens each of its pairs on a roll, and at
            // least the best of them (OpenPlanRank)
            foreach (var home in pairs.GroupBy(p => p.A.Unit).ToList())
            {
                if (!orng.Chance(OpenPlanHomes)) continue;
                var mine = home.ToList();
                var open = mine.Where(_ => orng.Chance(OpenPlanPairs)).ToList();
                if (open.Count == 0) open.Add(mine.OrderBy(p => OpenPlanRank(p.A.Kind, p.B.Kind)).First());
                foreach (var (f, a, b, spans) in open)
                {
                    foreach (var (alongX, line, s0, s1) in spans)
                    {
                        // in parts as even as the lattice makes them, none longer than OpenPlanMostCells (and so none shorter
                        // than OpenPlanCells)
                        int parts = (s1 - s0 + OpenPlanMostCells - 1) / OpenPlanMostCells, at = s0;
                        for (int k = 0; k < parts; k++)
                        {
                            int len = (s1 - at + (parts - k) - 1) / (parts - k);
                            res.OpenPlan.Add(new GenOpening { Floor = f, A = a, B = b, AlongX = alongX, Line = line, S0 = at, S1 = at + len });
                            at += len;
                        }
                    }
                    res.Doors.RemoveAll(d => d.Floor == f && ((d.A == a && d.B == b) || (d.A == b && d.B == a)));
                }
            }
        }

        /// <summary>Which pair an open-plan home opens first, when its rolls opened none: the kitchen into the living room
        /// (strawberry's own example), into the dining room, the dining room into the living room, then a hall into any.</summary>
        static int OpenPlanRank(RoomKind a, RoomKind b) => Pair(a, b, RoomKind.Kitchen, RoomKind.LivingRoom) ? 0 : Pair(a, b, RoomKind.Kitchen, RoomKind.DiningRoom) ? 1
            : Pair(a, b, RoomKind.DiningRoom, RoomKind.LivingRoom) ? 2 : 3;

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
                // a bathroom's slit keeps to a third of what a window would: a third of its slot, at the least a third of
                // MinWindow (strawberry: "make the bathroom window slit 1/3 as wide")
                float room = slot - gap, least = MinWindow;
                if (arch == ArchSlit) { room /= 3f; least /= 3f; }
                if (width > room)
                {
                    if ((arch == ArchWindow || arch == ArchTallWindow || arch == ArchSlit) && room >= least) width = room;
                    else continue;
                }
                // a shop window grows to its slot, up to `fill`
                else if (fill > width && (arch == ArchWindow || arch == ArchTallWindow)) width = MathF.Min(fill, slot - gap);
                outp.Add((arch, lo + slot * (i + 0.5f), width));
            }
            return outp;
        }

        /// (`keepClear`: stretches of an outside wall's line, by floor, that no window goes in -- the chimney's spot, where it
        /// had to have the windows laid round it: BuildOut.)
        static void BuildWalls(BuildingGenResult res, List<Grid> grids, int k1, int L,
                               IReadOnlyList<(int Floor, bool AlongX, int Line, float A0, float A1)> keepClear = null)
        {
            int D = res.CellsZ, floors = res.Floors;
            float Dm = D * Cell, he = Ext * 0.5f;
            var bay = res.Bay;
            var tops = new List<WallPlan>();
            // A lower floor's own roof over [xa, xb] (strawberry: "slope from the wall off the edge"): a SHED leaning on
            // the wall of the floor above, rising from its eave at the open end to that wall -- RoofShapes' Shed, so it is
            // the same roof the editor's Shed button builds. Pitched no steeper than lets it meet that wall under its
            // head. Closed as the editor closes one (RoofShapes.ShedInfill): the open end's wall gets a band up to the
            // eave's underside, and the two side walls a band and a right triangle each over the stretch it covers. Its
            // north side on the line z = zN: the building's north wall, or an L's or a T's arm's, which is only as deep as
            // the full-length row (Wing.Z0) -- the arm's roof goes no further north than the arm.
            void Lower(float xa, float xb, WallPlan endWall, bool westEnd, float head, float zN)
            {
                if (res.Roof == RoofKind.Flat) { res.Walls.Add(SlabPlan(xa, xb, -he, zN + he, head + Slab, SurfaceKind.Roof)); return; }
                float minX = westEnd ? xa - RoofOverhang : xa, maxX = westEnd ? xb : xb + RoofOverhang;
                float run = maxX - minX;
                float pitch = MathF.Min(res.RoofPitch, MathF.Atan((Storey - 0.6f) / run) * 180f / MathF.PI);
                var rs = new RoofSpec
                {
                    Kind = RoofKind.Shed, High = westEnd ? RoofSide.East : RoofSide.West, MinX = minX, MaxX = maxX,
                    MinZ = -he - RoofOverhang, MaxZ = zN + he + RoofOverhang, TopY = head, PitchDeg = pitch, Thickness = Slab,
                };
                foreach (var pl in RoofShapes.Planes(rs))
                    res.Walls.Add(new WallPlan { X = pl.X, Y = pl.Y, Z = pl.Z, Yaw = pl.YawDeg, Pitch = pl.PitchDeg, Length = pl.Length, Height = pl.Height,
                                                 Thickness = Slab, Kind = SurfaceKind.Roof });
                // `into`: run on past the high end, through the upper wall to its inner face -- a side's corner with
                // that wall, which the editor's corner pass would otherwise grow blind into the wall already standing
                // there (and on into the room). A hair thinner than that wall, so the overlap hides inside it rather
                // than fighting its faces. Where the side meets the upper wall part way along it -- an arm's north side,
                // a tee, not a corner -- only to its middle: the corner pass grows a tee to the middle, and an end face on
                // the inner face, with no wall across it there, would fight that face in the room upstairs.
                void Close(float x0, float z0, float x1, float z1, float yaw, bool into = false, bool tee = false)
                {
                    var (band, tri, highAtEnd) = RoofShapes.ShedInfill(rs, x0, z0, x1, z1);
                    float len = MathF.Abs(x1 - x0) + MathF.Abs(z1 - z0), ext = into ? (tee ? he : Ext) : 0f, thick = into ? Ext - 0.02f : Ext;
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
                Close(xa, zN, xb, zN, 0f, into: true, tee: zN < Dm - 0.01f);   // the north side, running +X
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
                var vehicleDoors = new List<(bool AlongX, int Line, float At, float Half)>();   // placed so far on this floor
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
                        // the other doors in it, centred where the door was -- and not along the stair bay, whose walls the
                        // flights and the landing stand against (a wide doorway grown into the bay left a landing one wall)
                        int c = (int)MathF.Floor(d.At / Cell), s0 = c, s1 = c + 1;
                        bool InBay(int x, int z) => floors > 1 && x >= bay.X0 && x < bay.X1 && z >= bay.Z0 && z < bay.Z1;
                        bool Pair(int s)
                        {
                            var (a, b) = g.Across(d.AlongX, d.Line, s);
                            if (d.AlongX ? InBay(s, d.Line - 1) || InBay(s, d.Line) : InBay(d.Line - 1, s) || InBay(d.Line, s)) return false;
                            return (a == d.A && b == d.B) || (a == d.B && b == d.A);
                        }
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
                // open plan: the wall between two rooms that share one space, cut away from a pier at one end to a pier at
                // the other -- each OpenPier off the face of the wall meeting it there -- and from the floor to a beam: up
                // to OpenBeam under a slab, where the floor above stands over it, else to OpeningTop (the wall's top half
                // metre is the beam). The wall stays: the editor's rooms are found from wall centrelines (GenOpening).
                foreach (var op in res.OpenPlan.Where(o => o.Floor == f))
                {
                    var iw = inner.First(q => q.AlongX == op.AlongX && q.Line == op.Line && q.S0 <= op.S0 && op.S1 <= q.S1);
                    float lo = op.S0 * Cell + g.Meet(op.AlongX, op.Line, op.S0) + OpenPier, hi = op.S1 * Cell - g.Meet(op.AlongX, op.Line, op.S1) - OpenPier;
                    // (under a slab anywhere along it: a reveal flush with the slab's underside fights it, so the beam)
                    float xa = op.AlongX ? lo : op.Line * Cell, xb = op.AlongX ? hi : op.Line * Cell;
                    bool slabOver = f + 1 < floors && xb >= res.Footprints[f + 1].X0 * Cell - he && xa <= res.Footprints[f + 1].X1 * Cell + he;
                    iw.W.Openings.Add(new WallOpening(lo - iw.S0 * Cell, 0f, hi - lo, slabOver ? OpeningTop - OpenBeam : OpeningTop, 999f, ArchPorch) { Glazed = false });
                }

                // what each room has in its outside walls
                bool business = res.Kind == BuildingKind.Commercial || res.Kind == BuildingKind.Misc;
                var fr = res.Front;
                var frSide = fr == null ? Sides.None : grids[0].Out(fr.AlongX, fr.Line, (int)MathF.Floor(fr.At / Cell));
                Face Facing(Sides sd) => fr == null ? Face.Side : sd == frSide ? Face.Front : sd == Turned(frSide) ? Face.Back : Face.Side;
                // A pitched roof below leaning on this storey's wall (strawberry: the lower roof's "slope from the wall off
                // the edge") stands in front of it, so no window goes where it does. A run with one the whole way along has
                // none at all, as an end wall over the uncovered part of the floor below always had; a run it covers part
                // of -- an L's or a T's end wall, whose arm is only the full-length row deep -- loses that stretch. And no
                // window anywhere that does not clear every roof below it by WindowOverRoof. All read off the roofs
                // already laid (the floors below's), just outside the wall's face.
                var lowRoofs = f > 0 ? res.Walls.Where(w => w.Kind == SurfaceKind.Roof).ToList() : new List<WallPlan>();
                float y0 = f * Storey;
                (float X, float Z) Before(Run r, float at, float off)
                {
                    var sd = g.Out(r.AlongX, r.Line, r.S0);
                    float o = he + off, l = r.Line * Cell;
                    return r.AlongX ? (at, l + (sd == Sides.North ? o : -o)) : (l + (sd == Sides.East ? o : -o), at);
                }
                // the stretches of a run with a pitched roof below leaning on this storey's wall in front of them, and
                // whether that is all of it
                (List<(float A, float B)> Stretches, bool Whole) Leaned(Run r)
                {
                    var st = new List<(float A, float B)>();
                    bool all = true;
                    if (lowRoofs.Count == 0) return (st, false);
                    float a0 = r.S0 * Cell, a1 = r.S1 * Cell;
                    int n = (int)MathF.Round((a1 - a0) / 0.1f);
                    for (int i = 0; i <= n; i++)
                    {
                        float at = a0 + (a1 - a0) * i / n;
                        var (px, pz) = Before(r, at, 0.05f);
                        bool lean = lowRoofs.Any(w => MathF.Abs(w.Pitch + 90f) > 0.01f && SurfaceTopAt(w, px, pz) > y0);
                        if (!lean) { all = false; continue; }
                        if (st.Count > 0 && st[^1].B >= at - 0.15f) st[^1] = (st[^1].A, at); else st.Add((at, at));
                    }
                    return (st, all);
                }
                // does a window of this kind, centred `at` along the run and `wd` wide, clear every roof below by WindowOverRoof
                bool Clears(Run r, int arch, float at, float wd)
                {
                    if (lowRoofs.Count == 0) return true;
                    float top = float.NegativeInfinity;
                    for (int i = 0; i <= 8; i++)
                        foreach (float off in new[] { 0.05f, 0.4f })
                        {
                            var (px, pz) = Before(r, at - wd * 0.5f + wd * i / 8f, off);
                            foreach (var w in lowRoofs) if (SurfaceTopAt(w, px, pz) is float h && h > top) top = h;
                        }
                    return top + WindowOverRoof <= y0 + ArchSizes[arch].Sill;
                }
                var outside = Runs(g).Where(r => r.B == null).ToList();
                var leaned = outside.Select(Leaned).ToList();
                // the free stretches of outside run ri: not where a door went, not behind the flight, not beside a roof below
                List<(float A, float B)> FreeOf(int ri)
                {
                    var run = outside[ri];
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
                    // and not where a roof below leans on it, nor within WindowBesideRoof of that
                    cuts.AddRange(leaned[ri].Stretches.Select(q => (q.A - WindowBesideRoof, q.B + WindowBesideRoof)));
                    // nor behind the chimney, where it needed them out of its way
                    if (keepClear != null)
                        cuts.AddRange(keepClear.Where(k => k.Floor == f && k.AlongX == run.AlongX && k.Line == run.Line).Select(k => (k.A0, k.A1)));
                    var free = new List<(float A, float B)> { (lo, hi) };
                    foreach (var (c0, c1) in cuts)
                        free = free.SelectMany(s => s.B <= c0 || s.A >= c1 ? new[] { s } : new (float A, float B)[] { (s.A, Math.Min(s.B, c0)), (Math.Max(s.A, c1), s.B) })
                                   .Where(s => s.B - s.A > 0.1f).ToList();
                    return free;
                }
                for (int ri = 0; ri < outside.Count; ri++)
                {
                    var run = outside[ri];
                    if (leaned[ri].Whole) continue;
                    var room = run.A;
                    var w = Side(run.AlongX, run.Line, (run.S0 + run.S1) * 0.5f * Cell);
                    var free = FreeOf(ri);
                    // (its longest run that may have a window: a side asked to have none never takes the one vent or slit)
                    Sides SideOfRun(Run r) => g.Out(r.AlongX, r.Line, r.S0);
                    // (a garage's: on the front door's own wall where it has a stretch of it, so the vehicle door faces the street)
                    var frontDoor = res.Doors.FirstOrDefault(d => d.Role == DoorRole.Front && d.Floor == 0);
                    bool OnFrontWall(Run r) => room.Kind == RoomKind.Garage && frontDoor != null && r.AlongX == frontDoor.AlongX && r.Line == frontDoor.Line
                                               && (r.S1 - r.S0) * Cell >= ArchSizes[ArchGarage].W;
                    // (and a garage's or warehouse's vehicle door on a run with a free stretch it fits: the longest wall can be
                    // the one the stairs stand against -- a narrow plot's warehouse lost its door that way)
                    bool vehicleRoom = (room.Kind == RoomKind.Garage || room.Kind == RoomKind.Warehouse) && f == 0;
                    bool DoorFits(int i) => FreeOf(i).Any(q => q.B - q.A >= ArchSizes[ArchGarage].W);
                    // (a garage's in a side it runs GarageDeep cells back from, so a car fits in behind it -- first of all)
                    bool Deep(Run r) => room.Kind != RoomKind.Garage || (r.AlongX ? room.Z1 - room.Z0 : room.X1 - room.X0) >= GarageDeep;
                    var mine = outside.Select((r, i) => (r, i)).Where(x => x.r.A == room && !leaned[x.i].Whole)
                                      .OrderBy(x => vehicleRoom && !Deep(x.r) ? 1 : 0)
                                      .ThenBy(x => (SideOfRun(x.r) & res.NoWindowSides) != 0 ? 1 : 0)
                                      .ThenBy(x => !vehicleRoom || DoorFits(x.i) ? 0 : 1)
                                      .ThenBy(x => OnFrontWall(x.r) ? 0 : 1)
                                      .ThenByDescending(x => x.r.S1 - x.r.S0).ThenBy(x => x.r.AlongX ? 0 : 1).First().r;
                    bool longest = mine.AlongX == run.AlongX && mine.Line == run.Line && mine.S0 == run.S0;
                    var arches = new List<(int Arch, float At, float Width)>();
                    // the vehicle door first, on a garage's or warehouse's longest outside run, then windows round it
                    if ((room.Kind == RoomKind.Garage || room.Kind == RoomKind.Warehouse) && f == 0 && longest && Deep(run) && free.Count > 0)
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
                            // and off any corner a door of this floor is by, on whatever wall: a garage is two cells wide now,
                            // so a centred vehicle door is within a metre of BOTH its corners and crowds the door into the
                            // house; the 1.95 m of slack clears the one corner that has a door
                            HashSet<(int, int)> Pts(bool ax, int line, float at, float half) => Near(at, half).Select(c => ax ? (c, line) : (line, c)).ToHashSet();
                            var floorDoors = res.Doors.Where(d => d.Floor == f).ToList();
                            bool Crowds(float at) => outDoors.Any(o => o.AlongX == run.AlongX && o.Line == run.Line && Near(at, gw * 0.5f).Intersect(Near(o.At, HalfDoor)).Any())
                                                     || floorDoors.Any(d => Pts(run.AlongX, run.Line, at, gw * 0.5f).Overlaps(Pts(d.AlongX, d.Line, d.At, d.Width * 0.5f)))
                                                     || vehicleDoors.Any(v => Pts(run.AlongX, run.Line, at, gw * 0.5f).Overlaps(Pts(v.AlongX, v.Line, v.At, v.Half)));
                            float mid = (span.A + span.B) * 0.5f, m = mid;
                            for (int k = 0; k * 0.25f <= (span.B - span.A - gw) * 0.5f; k++)
                                if (!Crowds(mid - k * 0.25f)) { m = mid - k * 0.25f; break; }
                                else if (!Crowds(mid + k * 0.25f)) { m = mid + k * 0.25f; break; }
                            arches.Add((ArchGarage, m, gw));
                            vehicleDoors.Add((run.AlongX, run.Line, m, gw * 0.5f));
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
                    // (and each window's share of its stretch -- its slot, half Spread's gap in from either end -- where a
                    // room's layout may later slide it: Furnish. Spread lays a window anywhere in its slot as readily as at
                    // its middle, so anywhere in it is a place BuildWalls itself might have put it.)
                    var slots = new Dictionary<float, (float A, float B)>();
                    if (win >= 0)
                        foreach (var (s0, s1) in free)
                        {
                            int n = win == ArchVent || win == ArchSlit || room.Circulation ? 1
                                  : shopFront ? Math.Max(1, (int)((s1 - s0) / (1.6f * Cell)))
                                  : side ? 1 : Math.Max(1, (int)((s1 - s0) / (2 * Cell)));
                            var spread = Spread(s0, s1, Enumerable.Repeat(win, n).ToList(), shopFront ? StoreWindow : 0f);
                            float share = (s1 - s0) / n, half = n > 1 ? 0.15f : 0f;
                            foreach (var (_, at, _) in spread)
                            {
                                int i = Math.Clamp((int)((at - s0) / share), 0, n - 1);
                                slots[at] = (s0 + share * i + half, s0 + share * (i + 1) - half);
                            }
                            arches.AddRange(spread);
                        }
                    foreach (var (arch, at, wd) in arches)
                        if (arch == ArchGarage || Clears(run, arch, at, wd))
                        {
                            w.Openings.Add(Make(arch, U(w, at), wd));
                            if (arch == ArchGarage || !slots.TryGetValue(at, out var sl)) continue;
                            // the whole slot clear of every roof below, or the window stays where it is
                            if (!Clears(run, arch, (sl.A + sl.B) * 0.5f, sl.B - sl.A)) sl = (at - wd * 0.5f, at + wd * 0.5f);
                            float ua = U(w, sl.A), ub = U(w, sl.B);
                            res.WindowSlots.Add(new WindowSlot(w, room, MathF.Min(ua, ub), MathF.Max(ua, ub)));
                        }
                }

                foreach (var o in outs) res.Walls.Add(o.W);
                if (f == floors - 1) tops.AddRange(outs.Select(o => o.W));
                if (f == 0)   // footings under the outside walls, as the editor's auto-fit lays them
                    foreach (var w in outs.Select(o => o.W))
                        res.Walls.Add(new WallPlan { X = w.X, Y = -WallOpenings.FoundationDepth, Z = w.Z, Yaw = w.Yaw, Length = w.Length,
                                                     Height = WallOpenings.FoundationDepth, Thickness = w.Thickness, Kind = SurfaceKind.Foundation });
                foreach (var q in inner) res.Walls.Add(q.W);

                // the floor, its top at the storey's base, out to just inside the outside walls' outer faces (SlabInset) -- over a
                // switchback, open above both its flights and its landing: a straight flight's hole the editor cuts as it
                // lays the flight, but a switchback's first flight and landing stop short of this floor and need headroom
                // under it all the same. Not past the flights' foot end: the next switchback's foot stands there.
                // the rooms that are not the building's floor colour get a cover of their own over the slab, 2 cm proud of it
                // (flush, the two faces would fight): tile in the wet rooms, concrete in garages and stores
                int baseTexel = FloorTexels.TryGetValue(res.Palette ?? "", out var ft) ? ft.Floor : -1;
                foreach (var room in res.Layout.Where(r => r.Floor == f))
                {
                    int tx = FloorTexelFor(res.Palette, room.Kind);
                    if (tx < 0 || tx == baseTexel) continue;
                    foreach (var q in room.Rects)
                    {
                        var cover = SlabPlan(q.X0 * Cell, q.X1 * Cell, q.Z0 * Cell, q.Z1 * Cell, y + 0.02f, SurfaceKind.Floor);
                        cover.Thickness = 0.04f; cover.Y = y + 0.02f - 0.02f; cover.Texel = tx;
                        res.Walls.Add(cover);
                    }
                }
                // an L's or a T's ground floor is its two slabs (the floors above stand on the main block: rectangles)
                if (f == 0 && res.Shape != FootprintShape.Rect)
                    foreach (var sl in Slabs(res, y, SurfaceKind.Floor)) { sl.Texel = baseTexel; res.Walls.Add(sl); }
                else
                {
                    const float si = SlabInset;
                    var floorSlab = SlabPlan(x0m - he + si, x1m + he - si, -he + si, Dm + he - si, y, SurfaceKind.Floor);
                    floorSlab.Texel = baseTexel;
                    if (f > 0 && res.StairShape != StairShape.Straight)
                        foreach (var (hx0, hz0, hx1, hz1) in res.Switchback ? new[] { SwitchbackHole(bay, res.LandingWest) } : QuarterHole(bay, res.LandingWest))
                            floorSlab.Openings.Add(new WallOpening(hx0 - floorSlab.X, floorSlab.Z - hz1, hx1 - hx0, hz1 - hz0));
                    res.Walls.Add(floorSlab);
                }
                // a roof on whatever of this floor the one above leaves uncovered: flat on a business; on a house a shed of
                // its own leaning on the wall of the floor above (Lower). Over an L's or a T's ground floor that is the
                // full-length row's arm(s) past the main block, only that row deep.
                if (f + 1 < floors)
                {
                    var (a0, a1) = res.Footprints[f + 1];
                    float wallHead = (f + 1) * Storey, zN = f == 0 && res.Shape != FootprintShape.Rect ? res.Wing.Z0 * Cell : Dm;
                    WallPlan End(int line) => outs.First(o => !o.AlongX && o.Line == line).W;
                    if (a0 > ux0) Lower(x0m - he, a0 * Cell - he, End(ux0), true, wallHead, zN);
                    if (a1 < ux1) Lower(a1 * Cell + he, x1m + he, End(ux1), false, wallHead, zN);
                }
            }

            // ---- the top roof: a house's gable (the editor's RoofShapes, eaves past the walls by its overhang, the walls
            // across the ridge raised into gable ends), a business's flat slab -- over the top floor, a rectangle but for a
            // one-storey L or T, whose house roof is a cross gable
            var (t0, t1) = res.Footprints[floors - 1];
            float head = floors * Storey;
            if (res.Shape != FootprintShape.Rect && floors == 1)
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

            // ---- the stairs: k1 flight rows against the south wall, centred along the bay. One flight climbs east up row
            // 0; with alternating rows, odd floors climb west up row 1, so each lands beside the next one's foot.
            if (floors < 2) return;
            // A compound flight's landing stands in the corner of the bay's end wall and the south wall, and runs out to both
            // their faces -- borne by two walls, not standing free (strawberry: "the landing of the stairs is only supported
            // by 1 wall, should require two"). Out to an outside wall's face at the end, which is the hole's edge in the floor
            // above; a partition's face is a hair further, inside the 0.15 m a landing's support is measured to.
            if (res.Switchback)
            {
                // a switchback: up row 0 from the bay's middle column to a landing in its end column at half height, then
                // back along row 1 to arrive on the next floor beside its own foot -- every floor's the same, stacked. East:
                // the foot in the west column, the landing in the east one; west, the mirror of that.
                float half = Storey * 0.5f, run = WallOpenings.StairDefaultRun(half), x0 = bay.X0 * Cell;
                float zA = he + WallOpenings.StairHoleMargin, zB = Cell + 0.3f + StairWidth;
                bool west = res.LandingWest;
                // the landing's extent along X: from the flights' end to the end wall's face
                float lx0 = west ? x0 + he : x0 + Cell + run, lx1 = west ? x0 + 2 * Cell - run : x0 + L * Cell - he;
                for (int f = 0; f + 1 < floors; f++)
                {
                    float y = f * Storey, t = WallOpenings.StairTreadThickness;
                    if (west)   // yaw 90 climbs -X, its width running -Z from its foot corner; yaw 270 climbs +X, its width +Z
                    {
                        res.Stairs.Add(new StairPlan { X = x0 + 2 * Cell, Y = y, Z = zA + StairWidth, Yaw = 90f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                        res.Stairs.Add(new StairPlan { X = x0 + 2 * Cell - run, Y = y + half, Z = zB - StairWidth, Yaw = 270f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                    }
                    else
                    {
                        res.Stairs.Add(new StairPlan { X = x0 + Cell, Y = y, Z = zA, Yaw = 270f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                        res.Stairs.Add(new StairPlan { X = x0 + Cell + run, Y = y + half, Z = zB, Yaw = 90f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                    }
                    res.Walls.Add(new WallPlan { X = lx0, Y = y + half - t * 0.5f, Z = zB, Yaw = 0f, Pitch = -90f, Length = lx1 - lx0,
                                                 Height = zB - he, Thickness = t, Kind = SurfaceKind.Stairs });   // the landing
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
                bool west = res.LandingWest;
                float x0 = bay.X0 * Cell, xL = west ? x0 + he + m : x0 + Cell + run;   // the second flight's west edge
                // the landing: under the second flight's foot, and on out to the end wall's face and the south wall's
                float lx0 = west ? x0 + he : xL, lx1 = west ? xL + StairWidth : x0 + L * Cell - he;
                for (int f = 0; f + 1 < floors; f++)
                {
                    float y = f * Storey, t = WallOpenings.StairTreadThickness;
                    if (west)   // yaw 90 climbs -X, its width running -Z from its foot corner
                        res.Stairs.Add(new StairPlan { X = x0 + 2 * Cell, Y = y, Z = zL, Yaw = 90f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                    else        // yaw 270 climbs +X, its width running +Z
                        res.Stairs.Add(new StairPlan { X = x0 + Cell, Y = y, Z = zA, Yaw = 270f, Run = run, Width = StairWidth, Rise = half, Floor = f });
                    res.Walls.Add(new WallPlan { X = lx0, Y = y + half - t * 0.5f, Z = zL, Yaw = 0f, Pitch = -90f, Length = lx1 - lx0,
                                                 Height = zL - he, Thickness = t, Kind = SurfaceKind.Stairs });   // the corner landing
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
        /// to the outside walls' outer faces -- the full-length one over the wall between them, so they meet once. A floor
        /// stops SlabInset inside those faces (the wing's join moved back with the full-length part's edge, so the two still
        /// meet); a roof runs out to them.</summary>
        static IEnumerable<WallPlan> Slabs(BuildingGenResult res, float top, SurfaceKind kind)
        {
            float he = Ext * 0.5f, i = kind == SurfaceKind.Floor ? SlabInset : 0f;
            var (wx0, zj, wx1, wz1) = res.Wing;
            yield return SlabPlan(-he + i, res.CellsX * Cell + he - i, -he + i, zj * Cell + he - i, top, kind);
            yield return SlabPlan(wx0 * Cell - he + i, wx1 * Cell + he - i, zj * Cell + he - i, wz1 * Cell + he - i, top, kind);
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
        /// (45 degrees in plan: one pitch): the arm's planes start at the valley, and the main roof's slope on that side is
        /// two planes, eave to ridge, meeting over the arm's ridge, each ending at a valley up to that ridge -- the editor's
        /// planes taper at their ends (Inset*), not along their eaves, so every valley is made an end. Neither roof runs on
        /// inside the other.
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
            void Plane(int t, float x, float y, float z, float yaw, float len, float run, float l0 = 0f, float r0 = 0f, float l1 = 0f, float r1 = 0f)
            {
                var (px, pz) = Turn(t, x, z);
                res.Walls.Add(new WallPlan { X = px, Y = y, Z = pz, Yaw = (yaw + t) % 360f, Pitch = node, Length = len, Height = run / cos,
                                             Thickness = Slab, Kind = SurfaceKind.Roof, InsetL0 = l0, InsetR0 = r0, InsetL1 = l1, InsetR1 = r1 });
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
                // the main slope on this side: two planes, eave to ridge, meeting over the arm's ridge -- each cut along a
                // valley up to the arm's ridge, and past it by that valley's line carried on (a negative inset) until the
                // plane's own end stops it. Not a band above the arm's ridge over a trapezoid either side below it, as it
                // was: a slope's top edge is cut plumb to meet the slope across the ridge (RoofShapes.PlumbFaces), and
                // across a joint ALONG the slope that cut met a band's eave cut square, leaving a groove under the joint.
                // Split along the slope instead, the two meet on a line that is plumb and square at once.
                Plane(t, X0, head, Z1, 0f, xr - X0, hm, r0: hw, r1: hw - hm);
                Plane(t, xr, head, Z1, 0f, X1 - xr, hm, l0: hw, l1: hw - hm);
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

        /// <summary>A chimney breast: this long along its wall and this deep into the room; its stack stands this far over
        /// the roof where it comes through.</summary>
        public const float ChimneyWide = 1.8f, ChimneyDeep = 0.9f, ChimneyOverRoof = 1.0f;
        /// <summary>How far a chimney's stack stands out past the outer face of the outside wall it climbs (strawberry:
        /// "chimney should buldge out of the exterior wall to some degree"): a breast's depth, so it clears the eaves'
        /// RoofOverhang by half a metre and reads as passing up through them rather than tucked under.</summary>
        public const float ChimneyOut = 0.9f;
        /// <summary>The stack's foot, through the ground storey: this wide along its wall (the stack's 1.8 m and 0.6 m a
        /// side, about what a real breast's foot is to its flue), up to this high; its shoulders then slope in to the stack's
        /// width by OpeningTop -- 0.6 m in over 0.75 m up, about 50 degrees, a weathering's slope.</summary>
        public const float ChimneyBaseWide = 3.0f, ChimneyBaseTop = 3.0f;

        /// <summary>The top of the roof over a plan point: the highest roof surface there (a pitched plane's, a flat slab's
        /// top), NaN where no roof is. The pitched planes' trapezoid ends (Inset*) are honoured, so a cross gable's valley
        /// cuts are respected.</summary>
        public static float RoofTopAt(BuildingGenResult res, float x, float z)
        {
            float best = float.NaN;
            foreach (var w in res.Walls)
            {
                if (w.Kind != SurfaceKind.Roof) continue;
                float h;
                if (MathF.Abs(w.Pitch + 90f) < 0.01f)
                {
                    if (x < w.X || x > w.X + w.Length || z < w.Z - w.Height || z > w.Z) continue;   // a slab: origin at min X, max Z
                    h = w.Y + w.Thickness * 0.5f;
                }
                else
                {
                    var (c, s) = PlanTrig.CosSin(w.Yaw);
                    float dx = c, dz = -s, ux = dz, uz = -dx;        // along the eave, and up the slope (to its right)
                    float px = x - w.X, pz = z - w.Z, u = px * dx + pz * dz, vp = px * ux + pz * uz;
                    float pitch = (w.Pitch + 90f) * MathF.PI / 180f, Hp = w.Height * MathF.Cos(pitch);
                    if (vp < 0f || vp > Hp) continue;
                    float t = Hp > 1e-4f ? vp / Hp : 0f;
                    float lo = MathF.Max(0f, w.InsetL0 + (w.InsetL1 - w.InsetL0) * t), hi = MathF.Min(w.Length, w.Length - (w.InsetR0 + (w.InsetR1 - w.InsetR0) * t));
                    if (u < lo || u > hi) continue;
                    h = w.Y + vp * MathF.Tan(pitch);
                }
                if (float.IsNaN(best) || h > best) best = h;
            }
            return best;
        }

        /// <summary>How far a window's sill stands over the top face of a roof below it, at the least: a window with a roof
        /// that close in front of it is behind the roof, and is left out (BuildWalls).</summary>
        public const float WindowOverRoof = 0.3f;
        /// <summary>How far along a wall a window keeps from where a roof below leans on it: an arm's shed stands up to
        /// 3.65 m over its eave at the wall, level with an upper window's head, so a window at its very edge would read as
        /// tucked under it from any angle but square on.</summary>
        public const float WindowBesideRoof = 0.6f;

        /// <summary>The top face of one roof surface over a plan point, NaN where it is not over it: a slab's top, or a
        /// pitched plane's mid-surface (RoofTopAt's height) plus half its thickness, measured square to the slope.</summary>
        public static float SurfaceTopAt(WallPlan w, float x, float z)
        {
            if (MathF.Abs(w.Pitch + 90f) < 0.01f)
                return x < w.X || x > w.X + w.Length || z < w.Z - w.Height || z > w.Z ? float.NaN : w.Y + w.Thickness * 0.5f;
            var (c, s) = PlanTrig.CosSin(w.Yaw);
            float dx = c, dz = -s, ux = dz, uz = -dx;        // along the eave, and up the slope (to its right)
            float px = x - w.X, pz = z - w.Z, u = px * dx + pz * dz, vp = px * ux + pz * uz;
            float pitch = (w.Pitch + 90f) * MathF.PI / 180f, Hp = w.Height * MathF.Cos(pitch);
            if (vp < 0f || vp > Hp) return float.NaN;
            float t = Hp > 1e-4f ? vp / Hp : 0f;
            float lo = MathF.Max(0f, w.InsetL0 + (w.InsetL1 - w.InsetL0) * t), hi = MathF.Min(w.Length, w.Length - (w.InsetR0 + (w.InsetR1 - w.InsetR0) * t));
            if (u < lo || u > hi) return float.NaN;
            return w.Y + vp * MathF.Tan(pitch) + w.Thickness * 0.5f / MathF.Cos(pitch);
        }

        /// <summary>The fireplace and chimney: a breast ChimneyWide by ChimneyDeep against an outside wall of the living room,
        /// a fireplace opening at its foot; and outside that wall the stack, ChimneyOut deep, from a footing up the face of
        /// every floor's outside wall on the same line and out past the eaves to ChimneyOverRoof over the top roof's highest
        /// point there (strawberry: "chimney should buldge out of the exterior wall to some degree, if its on an exterior
        /// wall") -- the flue is in the stack, so the floors above have no breast. On every floor the spot is in a stretch
        /// of that wall with no opening behind the stack, far enough from that stretch's ends that nothing meeting the wall
        /// there (a corner, a lower roof leaning on it) reaches the stack, and clear of any porch on that wall; on the
        /// ground floor the breast keeps clear of the partitions and the stairs. A position that fails any floor is not
        /// used; with none that passes, no chimney (said so when one was asked for). Added after the corner solve, which
        /// must not grow walls toward it, and before the furnisher, which then keeps its pieces off it and clear of the fire.
        /// The stack's foot is wider than the clear wall between a living room's windows often is (round 10's 1.8 m stack
        /// found a spot in 84 of 160 houses asked for one; a 3 m foot, 19 -- and with the windows laid round it, 146). So
        /// with `mayMove`, when no spot is clear, the first that would be but for windows (an opening off the floor) is
        /// returned and nothing is laid: BuildOut builds the walls again with their windows laid round it and asks again for
        /// `only` that spot. Null otherwise.</summary>
        static (bool AlongX, int Line, float P)? AddChimney(BuildingGenResult res, List<Grid> grids, ChimneyChoice choice, int seed,
                                                            (bool AlongX, int Line, float P)? only = null, bool mayMove = false)
        {
            var crng = new Rng(unchecked(seed * 7177 + 0x4348));
            bool roll = crng.Chance(50);
            var living = res.Layout.FirstOrDefault(g => g.Kind == RoomKind.LivingRoom && g.Floor == 0);
            if (choice == ChimneyChoice.None || (choice == ChimneyChoice.Auto && !roll) || res.Kind != BuildingKind.Residential) return null;
            if (living == null) { if (choice == ChimneyChoice.Yes) res.Notes.Add("a chimney was asked for, but there is no living room on the ground floor for its fireplace"); return null; }
            // (its ends kept further from any wall crossing its line than the corner solve reaches, 1.6 m, or the editor would
            // grow the breast -- or the stack -- to meet it)
            float he = Ext * 0.5f, half = ChimneyWide * 0.5f, clearEnds = 2.1f;   // 1.6 m and the far face of a corner wall
            // (the stack is wider at its foot, through the ground storey: what keeps clear of the stack there keeps clear of that)
            float baseHalf = ChimneyBaseWide * 0.5f;
            var ground = grids[0];
            // the floors' outside walls as built (with their openings), by floor, line and run
            WallPlan OuterWall(int f, bool alongX, int line, float at)
            {
                foreach (var w in res.Walls)
                {
                    if (w.Kind != SurfaceKind.Wall || MathF.Abs(w.Y - f * Storey) > 0.01f || MathF.Abs(w.Thickness - Ext) > 0.01f || w.Height < Storey - 0.01f) continue;
                    var (c, s) = PlanTrig.CosSin(w.Yaw);
                    float dx = c, dz = -s;
                    if (alongX ? MathF.Abs(dz) > 0.5f || MathF.Abs(w.Z - line * Cell) > 0.01f : MathF.Abs(dx) > 0.5f || MathF.Abs(w.X - line * Cell) > 0.01f) continue;
                    float a = alongX ? w.X : w.Z, b = a + (alongX ? dx : dz) * w.Length;
                    if (at >= MathF.Min(a, b) && at <= MathF.Max(a, b)) return w;
                }
                return null;
            }
            float U(WallPlan w, float at) => w.Yaw switch { 0f => at - w.X, 180f => w.X - at, 270f => at - w.Z, _ => w.Z - at };
            // (`windows`: whether a window -- an opening off the floor -- counts; a door never moves)
            bool Clear(WallPlan w, float p, float half, bool windows) { float u0 = U(w, p - half - 0.3f), u1 = U(w, p + half + 0.3f); float lo = MathF.Min(u0, u1), hi = MathF.Max(u0, u1); return !w.Openings.Any(o => (windows || o.V < 0.05f) && o.U < hi && o.U + o.Width > lo); }
            // the stretch of floor f's outline on a line, facing `face`, that holds position p (metres along it): the cells
            // either side of p's, while they have floor inside and nothing out
            (float A0, float A1) Stretch(Grid g, bool alongX, int line, Sides face, float p)
            {
                bool Edge(int c) => (alongX ? g.At(c, face == Sides.North ? line - 1 : line) : g.At(face == Sides.East ? line - 1 : line, c)) != null
                                    && (alongX ? g.At(c, face == Sides.North ? line : line - 1) : g.At(face == Sides.East ? line : line - 1, c)) == null;
                int c0 = (int)MathF.Floor(p / Cell);
                if (!Edge(c0)) return (0f, 0f);
                int lo = c0, hi = c0;
                while (Edge(lo - 1)) lo--;
                while (Edge(hi + 1)) hi++;
                return (lo * Cell, (hi + 1) * Cell);
            }
            var bay = res.Bay;
            var cands = new List<(bool AlongX, int Line, float P, float E, int Pref)>();
            foreach (var run in Runs(ground).Where(r => r.B == null && r.A == living))
            {
                var face = ground.Out(run.AlongX, run.Line, run.S0);
                float e = face is Sides.North or Sides.East ? 1f : -1f;
                // the front door's wall last: the fire on the street wall reads oddly; an end wall first
                var fr = res.Front;
                bool frontWall = fr != null && fr.AlongX == run.AlongX && fr.Line == run.Line;
                int pref = frontWall ? 2 : run.AlongX ? 1 : 0;
                for (float p = run.S0 * Cell + clearEnds + baseHalf; p <= run.S1 * Cell - clearEnds - baseHalf + 1e-3f; p += 0.75f)
                    cands.Add((run.AlongX, run.Line, p, e, pref));
            }
            var order = crng.Keyed(cands).OrderBy(c => c.Item.Pref).ThenBy(c => c.Key).Select(c => c.Item).ToList();
            // (at a spot asked for -- the one the walls were built again round -- that spot alone)
            if (only != null) order = order.Where(c => c.AlongX == only.Value.AlongX && c.Line == only.Value.Line && MathF.Abs(c.P - only.Value.P) < 1e-3f).ToList();
            // a spot clear as the walls are, else (with mayMove) one that would be but for its windows
            foreach (bool windows in mayMove ? new[] { true, false } : new[] { true })
            foreach (var (alongX, line, p, e, _) in order)
            {
                var face = e > 0 ? (alongX ? Sides.North : Sides.East) : (alongX ? Sides.South : Sides.West);
                // across the wall: the breast from its inner face into the room, the stack from its outer face out
                float inner = line * Cell - e * he, outer = line * Cell + e * he;
                float across = inner - e * ChimneyDeep * 0.5f, stackAt = outer + e * ChimneyOut * 0.5f;
                (float X0, float Z0, float X1, float Z1) Span(float a, float b, float hw) => alongX
                    ? (p - hw, MathF.Min(a, b), p + hw, MathF.Max(a, b)) : (MathF.Min(a, b), p - hw, MathF.Max(a, b), p + hw);
                var box = Span(inner, inner - e * ChimneyDeep, half);
                var stackBox = Span(outer, outer + e * ChimneyOut, half);
                var baseBox = Span(outer, outer + e * ChimneyOut, baseHalf);
                bool ok = true;
                for (int f = 0; f < res.Floors && ok; f++)
                {
                    // (the stack's half width on this floor: its base's on the ground floor)
                    float hw = f == 0 ? baseHalf : half;
                    // the floor has the spot inside it, on the same outside wall, with nothing in that wall behind the
                    // chimney, and that wall runs on past it both ways at least as far as from the ground floor's ends
                    int ci = (int)MathF.Floor((alongX ? p : across) / Cell), cj = (int)MathF.Floor((alongX ? across : p) / Cell);
                    var room = grids[f].At(ci, cj);
                    var w = OuterWall(f, alongX, line, p);
                    var (a0, a1) = Stretch(grids[f], alongX, line, face, p);
                    ok = room != null && grids[f].Out(alongX, line, alongX ? ci : cj) == face && w != null && Clear(w, p, hw, windows)
                         && p - hw - clearEnds >= a0 - 1e-3f && p + hw + clearEnds <= a1 + 1e-3f;
                    // no wall of this floor crossing the breast's line (on the ground floor) or the stack's within the
                    // corner solve's reach of their ends
                    foreach (float lineAt in f == 0 ? new[] { across, stackAt } : new[] { stackAt })
                        if (ok)
                            ok = !res.Walls.Any(q =>
                            {
                                if (q.Kind != SurfaceKind.Wall || MathF.Abs(q.Y - f * Storey) > 0.01f || q.Height < Storey - 0.01f) return false;
                                var (c, s) = PlanTrig.CosSin(q.Yaw); float dx = c, dz = -s;
                                if (alongX ? MathF.Abs(dx) > 0.5f : MathF.Abs(dz) > 0.5f) return false;   // parallel to it
                                float ql = alongX ? q.X : q.Z;
                                float qa = alongX ? q.Z : q.X, qb = qa + (alongX ? dz : dx) * q.Length;
                                if (lineAt < MathF.Min(qa, qb) - 0.05f || lineAt > MathF.Max(qa, qb) + 0.05f) return false;
                                return MathF.Abs(ql - p) < (lineAt == stackAt ? hw : half) + 2.05f;
                            });
                    // nor one meeting the wall behind the stack -- a partition ending at the wall's centreline, across the
                    // stack's span, is in the corner solve's reach of the stack and would grow through the wall to it
                    if (ok)
                        ok = !res.Walls.Any(q =>
                        {
                            if (q.Kind != SurfaceKind.Wall || MathF.Abs(q.Y - f * Storey) > 0.01f || q.Height < Storey - 0.01f) return false;
                            var (c, s) = PlanTrig.CosSin(q.Yaw); float dx = c, dz = -s;
                            if (alongX ? MathF.Abs(dx) > 0.5f : MathF.Abs(dz) > 0.5f) return false;   // parallel to it
                            float ql = alongX ? q.X : q.Z;
                            if (MathF.Abs(ql - p) > hw + 0.1f) return false;
                            float qa = alongX ? q.Z : q.X, qb = qa + (alongX ? dz : dx) * q.Length;
                            return MathF.Max(0f, MathF.Max(MathF.Min(qa, qb) - stackAt, stackAt - MathF.Max(qa, qb))) < 1.7f;
                        });
                    if (f > 0) continue;
                    // no partition of the ground floor running into the breast
                    if (ok)
                        ok = !res.Walls.Any(q =>
                        {
                            if (q.Kind != SurfaceKind.Wall || MathF.Abs(q.Y - f * Storey) > 0.01f || q.Height < Storey - 0.01f || q == w) return false;
                            var (c, s) = PlanTrig.CosSin(q.Yaw); float ex = q.X + c * q.Length, ez = q.Z - s * q.Length, t = q.Thickness * 0.5f;
                            float tx = MathF.Abs(s) * t, tz = MathF.Abs(c) * t;
                            return box.X0 < MathF.Max(q.X, ex) + tx - 0.01f && box.X1 > MathF.Min(q.X, ex) - tx + 0.01f
                                && box.Z0 < MathF.Max(q.Z, ez) + tz - 0.01f && box.Z1 > MathF.Min(q.Z, ez) - tz + 0.01f;
                        });
                    // and clear of the stair bay
                    if (ok && bay.X1 > bay.X0)
                        ok = !(box.X0 < bay.X1 * Cell && box.X1 > bay.X0 * Cell && box.Z0 < bay.Z1 * Cell && box.Z1 > bay.Z0 * Cell);
                }
                // and clear of every porch -- its deck, its roof's overhang, and far enough along the wall that its posts'
                // and footings' corner solve does not reach the stack's footing either
                if (ok)
                    ok = !res.Porches.Any(q => q.X0 - clearEnds < baseBox.X1 && q.X1 + clearEnds > baseBox.X0 && q.Z0 - clearEnds < baseBox.Z1 && q.Z1 + clearEnds > baseBox.Z0);
                if (!ok) continue;
                if (!windows) return (alongX, line, p);
                WallPlan Seg(float at, float deep, float y, float h, float hw = -1f)
                {
                    if (hw < 0f) hw = half;
                    var w = alongX ? Wall(p - hw, at, p + hw, at, y, deep) : Wall(at, p - hw, at, p + hw, y, deep);
                    w.Height = h;
                    return w;
                }
                // the breast in the living room
                var foot = Seg(across, ChimneyDeep, 0f, Storey);
                // the fireplace: a low opening in the breast's room face, its back the house wall behind it (V just off the
                // floor, so the furnisher keeps tall pieces off it but does not take it for a door to walk through)
                foot.Openings.Add(new WallOpening(ChimneyWide * 0.5f - 0.55f, 0.06f, 1.1f, 1.0f, 999f, ArchVent) { Glazed = false });
                res.Walls.Add(foot);
                // the stack outside, wider at its foot (strawberry, 2026-10-13: "widen the bottom of a chimney with sloped
                // sections on the 1st floor" -- her first floor, the ground storey, the fireplace's): on a footing as deep as
                // the house's and as wide as it, a base ChimneyBaseWide across up to ChimneyBaseTop; its shoulders, sloping
                // in on both sides to the stack's ChimneyWide by OpeningTop -- the floor above's underside, and under the
                // eaves of a house of one storey, which a wider shoulder up to the wall head would run into -- then the
                // stack a storey at a time up the wall and on up past the eaves over the roof where it comes through by
                // ChimneyOverRoof, and -- the usual rule for a flue -- 0.6 m over any roof within 3 m of it, so one by the
                // eave stands tall instead of a stub
                var footing = Seg(stackAt, ChimneyOut, -WallOpenings.FoundationDepth, WallOpenings.FoundationDepth, baseHalf);
                footing.Kind = SurfaceKind.Foundation;
                res.Walls.Add(footing);
                res.Walls.Add(Seg(stackAt, ChimneyOut, 0f, ChimneyBaseTop, baseHalf));
                // (a trapezoid, as a shed's gable infill is: the editor cuts it to InsetL/R, its sides lerped up its height)
                var shoulders = Seg(stackAt, ChimneyOut, ChimneyBaseTop, OpeningTop - ChimneyBaseTop, baseHalf);
                shoulders.InsetL1 = shoulders.InsetR1 = baseHalf - half;
                res.Walls.Add(shoulders);
                res.Walls.Add(Seg(stackAt, ChimneyOut, OpeningTop, Storey - OpeningTop));
                for (int f = 1; f < res.Floors; f++) res.Walls.Add(Seg(stackAt, ChimneyOut, f * Storey, Storey));
                float head = res.Floors * Storey;
                var sb = stackBox;
                float roofTop = new[] { (sb.X0, sb.Z0), (sb.X1, sb.Z0), (sb.X0, sb.Z1), (sb.X1, sb.Z1), ((sb.X0 + sb.X1) * 0.5f, (sb.Z0 + sb.Z1) * 0.5f) }
                                .Select(q => RoofTopAt(res, q.Item1, q.Item2)).Where(h => !float.IsNaN(h)).DefaultIfEmpty(head).Max();
                float near = head;
                for (float x = sb.X0 - 3f; x <= sb.X1 + 3f; x += 0.5f)
                    for (float z = sb.Z0 - 3f; z <= sb.Z1 + 3f; z += 0.5f)
                        if (RoofTopAt(res, x, z) is float h && !float.IsNaN(h)) near = MathF.Max(near, h);
                float top = MathF.Max(roofTop + ChimneyOverRoof, near + 0.6f);
                res.Walls.Add(Seg(stackAt, ChimneyOut, head, top - head));
                res.Chimney = (MathF.Min(box.X0, baseBox.X0), MathF.Min(box.Z0, baseBox.Z0), MathF.Max(box.X1, baseBox.X1), MathF.Max(box.Z1, baseBox.Z1));
                return null;
            }
            if (choice == ChimneyChoice.Yes) res.Notes.Add("a chimney was asked for, but no outside wall of the living room had a clear stretch for it on every floor");
            return null;
        }

        /// <summary>Where the walls keep their windows off a chimney at `spot` (AddChimney's measure of clear, and 5 cm): on
        /// every floor, its line, the stack's width -- its foot's on the ground floor -- and AddChimney's 0.3 m either side.</summary>
        static List<(int Floor, bool AlongX, int Line, float A0, float A1)> ChimneyKeepClear(BuildingGenResult res, (bool AlongX, int Line, float P) spot)
        {
            var keep = new List<(int, bool, int, float, float)>();
            for (int f = 0; f < res.Floors; f++)
            {
                float hw = (f == 0 ? ChimneyBaseWide : ChimneyWide) * 0.5f + 0.35f;
                keep.Add((f, spot.AlongX, spot.Line, spot.P - hw, spot.P + hw));
            }
            return keep;
        }

        /// <summary>Porch posts: square, this wide.</summary>
        public const float PorchPost = 0.35f;
        /// <summary>A porch roof's pitch when the house's is pitched: shallow, a lean-to.</summary>
        public const float PorchPitch = 10f;

        /// <summary>Porches (strawberry: "floored, separate outdoor roofed areas outside front and/or back doors with columns
        /// supporting the roof"): a deck one cell deep against the outside wall, two to four cells along it and centred on
        /// the door as the lattice and the wall allow; posts at its outer corners and between, no further apart than 3.6 m;
        /// a roof on them -- a shed leaning on the house for a pitched-roof house, its high edge just under the wall head and
        /// over the windows behind it (they reach OpeningTop), a flat canopy for a flat-roofed business; footings under its
        /// open edges, as the house has. Its own rng stream, so a building without one is the building it was.</summary>
        static void AddPorches(BuildingGenResult res, Grid ground, PorchChoice choice, int seed)
        {
            var prng = new Rng(unchecked(seed * 31337 + 0x504F));
            bool house = res.Kind == BuildingKind.Residential;
            bool rollFront = prng.Chance(house ? 55 : 30), rollBack = prng.Chance(house ? 30 : 10);
            bool front = choice is PorchChoice.Front or PorchChoice.Both || (choice == PorchChoice.Auto && rollFront);
            bool back = choice is PorchChoice.Back or PorchChoice.Both || (choice == PorchChoice.Auto && rollBack);
            float he = Ext * 0.5f, ovp = 0.3f, depth = Cell, hi = Storey - 0.2f;
            var outs = Outline(ground, 0f);
            foreach (var (door, want) in new[] { (res.Front, front), (res.Back, back) })
            {
                if (!want) continue;
                if (door == null) { res.Notes.Add("a back porch was asked for, but it has no back door (ask for a back door's side to get one)"); continue; }
                if (door.Floor != 0) continue;
                var o = outs.FirstOrDefault(q => q.AlongX == door.AlongX && q.Line == door.Line && q.A0 <= door.At && door.At <= q.A1);
                if (o == null) continue;
                int lo = (int)MathF.Ceiling(o.A0 / Cell - 1e-3f), hiCell = (int)MathF.Floor(o.A1 / Cell + 1e-3f);
                int n = Math.Min(house ? 2 + prng.Int(2) : 3 + prng.Int(2), hiCell - lo);
                if (n < 1) continue;
                int s0 = Math.Clamp((int)MathF.Round(door.At / Cell - n * 0.5f), lo, hiCell - n);
                float e = o.Face is Sides.North or Sides.East ? 1f : -1f, face = o.Line * Cell + e * he;
                // along the wall: where it reaches the wall's end, flush with the building's corner at an outside corner,
                // and up to the other wall's face at an inside one (an L's, where the wing stands just past the end)
                int ext = e > 0 ? o.Line : o.Line - 1;   // the row (or column) of cells on the outside of this wall
                bool Inside(float A, bool past) { int c = (int)MathF.Round(A / Cell) + (past ? 0 : -1); return (o.AlongX ? ground.At(c, ext) : ground.At(ext, c)) != null; }
                bool in0 = s0 * Cell <= o.A0 + 0.01f && Inside(o.A0, false), in1 = (s0 + n) * Cell >= o.A1 - 0.01f && Inside(o.A1, true);
                float a0 = s0 * Cell > o.A0 + 0.01f ? s0 * Cell : in0 ? o.A0 + he : o.A0 - he;
                float a1 = (s0 + n) * Cell < o.A1 - 0.01f ? (s0 + n) * Cell : in1 ? o.A1 - he : o.A1 + he;
                if (door.At - HalfDoor < a0 - 0.01f || door.At + HalfDoor > a1 + 0.01f) continue;
                (float X0, float Z0, float X1, float Z1) R(float al0, float al1, float ac0, float ac1)
                {
                    float c0 = MathF.Min(ac0, ac1), c1 = MathF.Max(ac0, ac1);
                    return o.AlongX ? (al0, c0, al1, c1) : (c0, al0, c1, al1);
                }
                var deck = R(a0, a1, face, face + e * depth);
                if (res.Porches.Any(q => q.X0 < deck.X1 && q.X1 > deck.X0 && q.Z0 < deck.Z1 && q.Z1 > deck.Z0)) continue;
                res.Porches.Add(deck);
                // its deck the house's floor colour (strawberry: "give porches floor color")
                var deckSlab = SlabPlan(deck.X0, deck.X1, deck.Z0, deck.Z1, 0f, SurfaceKind.Floor);
                deckSlab.Texel = FloorTexels.TryGetValue(res.Palette ?? "", out var pft) ? pft.Floor : -1;
                res.Walls.Add(deckSlab);
                // the roof, and how tall the posts stand to meet its underside over them
                var top = R(a0 - ovp, a1 + ovp, face, face + e * (depth + ovp));
                float postH;
                if (res.Roof == RoofKind.Flat)
                {
                    res.Walls.Add(SlabPlan(top.X0, top.X1, top.Z0, top.Z1, hi, SurfaceKind.Roof));
                    postH = hi - Slab;
                }
                else
                {
                    float tan = MathF.Tan(PorchPitch * MathF.PI / 180f), eave = hi - (depth + ovp) * tan;
                    var rs = new RoofSpec
                    {
                        Kind = RoofKind.Shed, MinX = top.X0, MaxX = top.X1, MinZ = top.Z0, MaxZ = top.Z1, TopY = eave, PitchDeg = PorchPitch, Thickness = Slab,
                        High = o.Face switch { Sides.South => RoofSide.North, Sides.North => RoofSide.South, Sides.West => RoofSide.East, _ => RoofSide.West },
                    };
                    foreach (var pl in RoofShapes.Planes(rs))
                        res.Walls.Add(new WallPlan { X = pl.X, Y = pl.Y, Z = pl.Z, Yaw = pl.YawDeg, Pitch = pl.PitchDeg, Length = pl.Length, Height = pl.Height,
                                                     Thickness = Slab, Kind = SurfaceKind.Roof });
                    postH = eave + (ovp + PorchPost) * tan;
                }
                // the posts, along its outer edge (none at an end against an inside corner: the wing's wall carries it)
                float across = face + e * (depth - PorchPost * 0.5f), p0 = a0 + PorchPost * 0.5f, p1 = a1 - PorchPost * 0.5f;
                int posts = Math.Max(2, (int)MathF.Ceiling((p1 - p0) / 3.6f) + 1);
                for (int k = 0; k < posts; k++)
                {
                    if ((k == 0 && in0) || (k == posts - 1 && in1)) continue;
                    float at = p0 + (p1 - p0) * k / (posts - 1);
                    var w = o.AlongX ? Wall(at - PorchPost * 0.5f, across, at + PorchPost * 0.5f, across, 0f, PorchPost)
                                     : Wall(across, at - PorchPost * 0.5f, across, at + PorchPost * 0.5f, 0f, PorchPost);
                    w.Height = postH;
                    res.Walls.Add(w);
                }
                // footings under its three open edges
                const float tf = 0.35f;
                float outer = face + e * (depth - tf * 0.5f);
                // (into the wing's footing at an inside corner, which stands where this side's own would)
                float f0 = in0 ? a0 - he : a0, f1 = in1 ? a1 + he : a1;
                var footings = new List<(float, float, float, float)> { o.AlongX ? (f0, outer, f1, outer) : (outer, f0, outer, f1) };
                // (each side from the house wall's centreline, inside its footing, so the corner solve finds them already met)
                if (!in0) footings.Add(o.AlongX ? (a0 + tf * 0.5f, face - e * he, a0 + tf * 0.5f, face + e * depth) : (face - e * he, a0 + tf * 0.5f, face + e * depth, a0 + tf * 0.5f));
                if (!in1) footings.Add(o.AlongX ? (a1 - tf * 0.5f, face - e * he, a1 - tf * 0.5f, face + e * depth) : (face - e * he, a1 - tf * 0.5f, face + e * depth, a1 - tf * 0.5f));
                foreach (var (x0, z0, x1, z1) in footings)
                {
                    var f = Wall(x0, z0, x1, z1, -WallOpenings.FoundationDepth, tf);
                    f.Height = WallOpenings.FoundationDepth; f.Kind = SurfaceKind.Foundation;
                    res.Walls.Add(f);
                }
            }
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
        /// its landing, a StairHoleMargin past them but for the flights' foot end, where the next floor's foot is -- the
        /// landing at the bay's east end, or with `west` its west end (the mirror image).</summary>
        public static (float X0, float Z0, float X1, float Z1) SwitchbackHole((int X0, int Z0, int X1, int Z1) bay, bool west = false)
        {
            float he = Ext * 0.5f, m = WallOpenings.StairHoleMargin, run = WallOpenings.StairDefaultRun(Storey * 0.5f);
            float x1 = bay.X0 * Cell + Cell, xe = x1 + run + StairWidth + m, mirror = 2 * bay.X0 * Cell + 3 * Cell;
            return west ? (mirror - xe, he, mirror - x1, Cell + 0.3f + StairWidth + m) : (x1, he, xe, Cell + 0.3f + StairWidth + m);
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
