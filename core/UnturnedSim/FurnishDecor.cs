using System;
using System.Collections.Generic;
using System.Linq;
using static UnturnedSim.RoomKind;

namespace UnturnedSim
{
    /// <summary>What a wall-décor row asks of the house before it goes in a room (wall_decor_spec: "various aspects of
    /// the house"). The bed and wardrobe conditions restrict bedrooms only, so one row can list a bedroom beside a hall.</summary>
    [Flags]
    public enum DecorNeed
    {
        None = 0,
        /// <summary>In a bedroom, only one with a double bed: the master bedroom's paintings.</summary>
        DoubleBed = 1 << 0,
        /// <summary>In a bedroom, only one with a single bed or bunks: a kid's or a teen's posters.</summary>
        SingleBed = 1 << 1,
        /// <summary>In a bedroom, only one with a wardrobe (a mirror goes beside it).</summary>
        Wardrobe = 1 << 2,
        /// <summary>Only a room with a door to outside -- the front, the back, a side door, a vehicle door -- or, a flat's,
        /// to the shared corridor: hooks, keys.</summary>
        OuterDoor = 1 << 3,
        /// <summary>Only in a house or a flat.</summary>
        Home = 1 << 4,
        /// <summary>Only in a business, Commercial or Industrial: the work items (whiteboards...).</summary>
        Business = 1 << 5,
    }

    /// <summary>Where on its wall a row's piece hangs.</summary>
    public enum DecorSpot
    {
        /// <summary>The middle of a free stretch of wall; better, centred over one of its Over pieces (a couch, a bed's
        /// head) or just beside one of its Beside pieces, where one is against a wall with room.</summary>
        Wall,
        /// <summary>Only centred over one of its Over pieces, lined up with it: upper cabinets over the counters (a run of
        /// them over a run of counters, side by side), a utensil rail over the stove, a medicine cabinet over the basin.</summary>
        Over,
        /// <summary>Within <see cref="BuildingGen.DecorDoorReach"/> of the room's door to outside, or failing one of any
        /// door of the room: hooks, a key rack.</summary>
        Door,
    }

    /// <summary>One wall-décor item: the prop (game/content/objects/{Name}.obj, its extents in FurnitureCatalog.Sizes, its
    /// GUID in guid_mesh.txt); the kinds of room it goes in and what else it needs there; the slot(s) of those rooms'
    /// recipes it fills (<see cref="BuildingGen.DecorRecipes"/>; space-separated); its odds against the slot's other rows
    /// (Weight) and the most of it one room gets (Max); the height of its origin over the floor (Height: a hung mesh is
    /// centred on its origin up and down, so it spans Height +- its Sizes H); where on the wall it goes (Spot, Over,
    /// Beside -- pieces by name prefix, "Couch" is both couches); how many hang side by side (Group: 2..Group of them, the
    /// row's look-alikes taking turns; 1 alone); and whether it is a room's big focal piece, of which a wall holds one
    /// (Focal: the television is one). Tags are the manifest's, for the record. Stand: a piece you stand before (the
    /// full-height mirror, down to the floor) wants that much clear floor of its room in front of it, across its width.
    /// UnderCeiling: it hangs with its top flush under the ceiling over its spot, whatever the storey's (Height unused).</summary>
    public sealed record DecorRow(string Name, RoomKind[] Rooms, float Height, string Slot, int Weight = 10, int Max = 1, DecorNeed Need = DecorNeed.None,
                                  DecorSpot Spot = DecorSpot.Wall, string[] Over = null, string[] Beside = null, int Group = 1, bool Focal = false,
                                  string Tags = "", float Stand = 0f, bool UnderCeiling = false)
    {
        public bool Fills(string slot) => (" " + Slot + " ").Contains(" " + slot + " ");
    }

    public static partial class BuildingGen
    {
        static RoomKind[] In(params RoomKind[] k) => k;
        static readonly string[] CouchOrDouble = { "Couch", "Bed_0" }, CouchOrBed = { "Couch", "Bed_0", "Bed_1" }, Couch = { "Couch" }, Wardrobe = { "Wardrobe" };

        /// <summary>The wall décor (VoX: "needs more wall doo dads like posters and art and clocks and coat hooks", then of
        /// astraclaw's kit: "start integrating these intelligently into the interior proc gen system"), one row per model.
        /// Heights are the kit manifest's suggested_origin_height (the mirror 5 cm higher: at 2.6 its bottom came 0.07 m
        /// over the basin's 1.83, inside the 0.1 a piece keeps over what stands under it); rooms the manifest's, widened as
        /// VoX's brief has them (the workshop's map and poster). A row's name must not start with a prefix the furnisher or
        /// Shortfall reads a room's furniture by (Bed, Couch, Table, Chair, Shelf, Counter, Fridge, Oven, Tub, Lamp,
        /// Television): a hung piece would pass for the floor piece. The wall clock (Clock) and the flat television
        /// (WallTv) hang in their rooms' own recipes, before this; the clock stays there, because it draws from the
        /// Furnisher's shared Rng and moving it onto the décor's would move every room furnished after it.</summary>
        public static readonly IReadOnlyList<DecorRow> Decor = new DecorRow[]
        {
            // the big pieces, one to a wall: over the couch or a double bed's head where there is one, else the middle of
            // the longest free wall
            new("Painting_Landscape_0", In(LivingRoom, DiningRoom, Hall, Bedroom), 2.6f, "focal", Weight: 3, Need: DecorNeed.DoubleBed, Over: CouchOrDouble, Focal: true, Tags: "adult landscape rustic"),
            new("Painting_Landscape_1", In(LivingRoom, DiningRoom, Hall, Bedroom), 2.6f, "focal", Weight: 3, Need: DecorNeed.DoubleBed, Over: CouchOrDouble, Focal: true, Tags: "adult landscape neutral"),
            new("Painting_Landscape_2", In(LivingRoom, DiningRoom, Hall, Bedroom), 2.6f, "focal", Weight: 3, Need: DecorNeed.DoubleBed, Over: CouchOrDouble, Focal: true, Tags: "adult landscape rustic"),
            new("Painting_Landscape_3", In(LivingRoom, DiningRoom, Hall, Bedroom), 2.6f, "focal", Weight: 3, Need: DecorNeed.DoubleBed, Over: CouchOrDouble, Focal: true, Tags: "adult landscape neutral"),
            new("Painting_Portrait_0", In(Hall, DiningRoom), 2.6f, "focal", Weight: 6, Focal: true, Tags: "formal portrait"),
            new("Painting_Portrait_1", In(Hall, DiningRoom), 2.6f, "focal", Weight: 6, Focal: true, Tags: "formal portrait"),
            new("Whiteboard_0", In(Office, BreakRoom), 2.6f, "focal", Weight: 8, Need: DecorNeed.Business, Focal: true, Tags: "work modern"),
            new("Wall_Map_0", In(Office, Hall, Garage, Workshop), 2.6f, "focal", Weight: 4, Focal: true, Tags: "map work"),
            new("Pegboard_0", In(Garage, Workshop, Storage), 2.4f, "focal", Weight: 10, Over: new[] { "Table_Metal_0" }, Focal: true, Tags: "tools work"),
            // retail: the 3 x 1.5 m pixel-art poster. Its OBJ is centred in depth (Y0 -0.1), not backed at Y 0
            new("Poster_0", In(BreakRoom, Garage, Workshop, Shop), 2.55f, "focal", Weight: 10, Focal: true, Tags: "work"),
            // a kid's or a teen's room: one over the bed (a single's; a bunk's 2.92 m never clears) and maybe another
            new("Poster_Tall_0", In(Bedroom, Garage, BreakRoom, Workshop), 2.6f, "focal wall", Weight: 3, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }, Tags: "kid teen band"),
            new("Poster_Tall_1", In(Bedroom, Garage, BreakRoom, Workshop), 2.6f, "focal wall", Weight: 3, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }, Tags: "kid teen space"),
            new("Poster_Tall_2", In(Bedroom, Garage, BreakRoom, Workshop), 2.6f, "focal wall", Weight: 3, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }, Tags: "kid teen arcade"),
            new("Poster_Tall_3", In(Bedroom, Garage, BreakRoom, Workshop), 2.6f, "focal wall", Weight: 3, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }, Tags: "kid teen sport"),
            // the lesser pieces: small paintings in twos and threes, photos, shelves, a mirror, the antlers (rare)
            new("Painting_Small_0", In(LivingRoom, DiningRoom, Hall, Bedroom, Office), 2.6f, "wall", Weight: 4, Group: 3, Over: CouchOrBed, Tags: "small groupable neutral"),
            new("Painting_Small_1", In(LivingRoom, DiningRoom, Hall, Bedroom, Office), 2.6f, "wall", Weight: 4, Group: 3, Over: CouchOrBed, Tags: "small groupable neutral"),
            new("Painting_Small_2", In(LivingRoom, DiningRoom, Hall, Bedroom, Office), 2.6f, "wall", Weight: 4, Group: 3, Over: CouchOrBed, Tags: "small groupable neutral"),
            new("Photo_Frames_0", In(LivingRoom, Bedroom, Hall), 2.6f, "wall", Weight: 8, Need: DecorNeed.Home | DecorNeed.DoubleBed, Over: CouchOrDouble, Tags: "family adult"),
            new("Wall_Shelf_0", In(LivingRoom, Bedroom, Office), 2.5f, "shelf", Weight: 5, Over: Couch, Tags: "shelf neutral"),
            new("Wall_Shelf_1", In(LivingRoom, Bedroom, Office), 2.7f, "shelf", Weight: 5, Over: Couch, Tags: "shelf books plant clutter"),
            // astraclaw's expansion, the alternatives in the same slot (decor_expansion_manifest.json, its heights): the short
            // utility shelf and the cubbies in a kitchen and a store room too, the two-tier in a store room, the picture
            // ledge where pictures go
            new("Wall_Shelf_2", In(LivingRoom, Bedroom, Office, Storage, Kitchen), 2.5f, "shelf", Weight: 3, Tags: "shelf short_utility"),
            new("Wall_Shelf_3", In(LivingRoom, Bedroom, Office, Storage), 2.5f, "shelf", Weight: 4, Over: Couch, Tags: "shelf two_tier"),
            new("Wall_Shelf_4", In(LivingRoom, Bedroom, Office, Storage, Kitchen), 2.5f, "shelf", Weight: 3, Tags: "shelf cubbies"),
            new("Wall_Shelf_5", In(LivingRoom, Bedroom, Office), 2.5f, "shelf", Weight: 3, Over: Couch, Tags: "shelf display_ledge"),
            new("Mirror_0", In(Bathroom, Hall, Bedroom), 2.65f, "sink mirror", Weight: 8, Need: DecorNeed.Wardrobe, Over: new[] { "Counter_1" }, Beside: Wardrobe, Tags: "mirror wardrobe"),
            // the full-height one, 0.1 m off the floor to 2.9 (the manifest's 1.5): beside the wardrobe, in a hall, rarely a
            // bathroom; with BedAccess of clear floor before it -- the depth the player's capsule needs to get anywhere
            new("Mirror_1", In(Bedroom, Hall, Bathroom), 1.5f, "mirror", Weight: 5, Beside: Wardrobe, Stand: BedAccess, Tags: "mirror full_height wardrobe hall"),
            new("Antlers_0", In(LivingRoom, Hall), 2.9f, "trophy", Weight: 1, Need: DecorNeed.Home, Tags: "rustic rural rare"),
            // the deer's head (strawberry approved it; astraclaw: "a rare rustic accent"): a home's living room, hall or den,
            // in the antlers' rare slot. 2.85, not the handoff's 2.1: at 2.1 its head, 0.76 m out of the wall, came down to
            // 1.43 m -- a 2 m player's face -- and at 2.85 it spans 2.18-3.52, over the player and under a storey's 3.75 ceiling.
            // A big piece, one to a wall
            new("Deer_Wall_Trophy_0", In(LivingRoom, Hall, Office), 2.85f, "trophy", Weight: 1, Need: DecorNeed.Home, Focal: true, Tags: "rustic trophy rare"),
            new("Wall_Sunburst_0", In(LivingRoom, Hall), 2.7f, "wall", Weight: 4, Tags: "decorative rustic playful"),
            // work: notes and certificates
            new("Calendar_0", In(Kitchen, Office, BreakRoom), 2.2f, "notes", Weight: 5, Tags: "work household"),
            new("Corkboard_0", In(Office, BreakRoom, Kitchen), 2.6f, "notes", Weight: 5, Tags: "work notes"),
            new("Certificate_0", In(Office), 2.6f, "certs", Weight: 5, Group: 3, Tags: "formal work"),
            // by the way in
            new("Coat_Hooks_0", In(Hall, Garage), 2.0f, "hooks", Weight: 8, Need: DecorNeed.OuterDoor, Spot: DecorSpot.Door, Tags: "entry front_or_back_door"),
            // (Coat_Hooks_1, the rack with a coat on it, was dropped -- VoX: "might be confusing to players, a coat they can't
            // use"; astraclaw's replacement is the sunburst, with the lesser art below)
            new("Key_Rack_0", In(Hall), 1.8f, "keys", Weight: 6, Need: DecorNeed.OuterDoor | DecorNeed.Home, Spot: DecorSpot.Door, Tags: "entry front_door"),
            // fixtures, over what they serve: upper cabinets over the counters, sink and dishwasher -- the counter itself, hung
            // upside down from the ceiling (strawberry, round 12: "replace the custom wall cabinets with counters hung upside
            // down", Upper_Counter_0 = Counter_0 turned over its front axis, tools/derive_prop.py), its top flush under the
            // ceiling: 1.35 m deep, so 1.05 m over the worktop under a storey's 3.75 m ceiling; Wall_Cabinet_0 and the grey
            // Wall_Cabinet_1 are no longer hung -- a utensil rail over the stove, a medicine cabinet over the basin, a towel
            // rail by the tub or the basin
            new("Upper_Counter_0", In(Kitchen), 0f, "cabinets", Max: 8, Spot: DecorSpot.Over, Over: new[] { "Counter_0", "Counter_1", "Disher_0" }, UnderCeiling: true, Tags: "upper_cabinet wood counter_0"),
            new("Utensil_Rail_0", In(Kitchen), 2.15f, "rail", Spot: DecorSpot.Over, Over: new[] { "Oven_0", "Counter_0" }, Tags: "counter cooking"),
            new("Medicine_Cabinet_0", In(Bathroom), 2.5f, "sink", Weight: 8, Spot: DecorSpot.Over, Over: new[] { "Counter_1" }, Tags: "mirror sink"),
            new("Towel_Rail_0", In(Bathroom), 1.6f, "towel", Beside: new[] { "Tub", "Counter_1" }, Tags: "towel wash"),
            // strawberry's doodad kit's indoor wall pieces (doodad_placement.tsv: suggested_origin_height and room_kinds; their
            // slots' odds the kit's rarity, DoodadCommon and DoodadUncommon; clearance_front as Stand where it is floor you
            // stand on before them): the toilet-roll holder by the basin -- the kit's "near Toilet", and the generator places
            // no toilet -- with 0.625 m before it; a plate rack in a home's kitchen or dining room; the dartboard, its 4.8 m
            // throw clear before it; the woven basket by a door, the kit's "near ExteriorDoor" (DecorSpot.Door: the one out
            // where the room has it). A home's but the dartboard, which a business's break room or garage takes too
            new("Toilet_Roll_Holder_0", In(Bathroom), 0.9f, "toilet", Need: DecorNeed.Home, Beside: new[] { "Counter_1" }, Stand: 0.625f, Tags: "doodad bathroom"),
            new("Wall_Plate_Rack_0", In(Kitchen, DiningRoom), 2.8f, "plates", Need: DecorNeed.Home, Tags: "doodad kitchen dining"),
            new("Recreation_Dartboard_0", In(Garage, LivingRoom, BreakRoom), 2.2f, "game", Stand: 4.8f, Tags: "doodad recreation"),
            new("Wall_Woven_Basket_0", In(Hall, Bedroom, Garage), 2.5625f, "basket", Need: DecorNeed.Home, Spot: DecorSpot.Door, Tags: "doodad woven"),
        };

        /// <summary>What each kind of room hangs, in order (VoX's brief, "coherent per room, not random"): its slots, each
        /// filled at the given odds by one of the rows that fill it there (a weighted draw; one that will not go anywhere
        /// gives way to the next). The art slots -- all but the fixtures' (DecorFixtures) -- are bounded by the room's free
        /// wall (DecorEvery); every piece by its wall (one focal piece, DecorFill of its length).</summary>
        public static readonly IReadOnlyDictionary<RoomKind, (string Slot, int Chance)[]> DecorRecipes = new Dictionary<RoomKind, (string, int)[]>
        {
            // (a slot added for the expansion goes LAST in its recipe: the room's draws for the slots before it are as they were)
            [Kitchen] = new[] { ("cabinets", 100), ("rail", 70), ("notes", 60), ("shelf", 35), ("plates", DoodadCommon) },
            [LivingRoom] = new[] { ("focal", 100), ("wall", 70), ("shelf", 40), ("trophy", 8), ("game", DoodadUncommon) },
            [DiningRoom] = new[] { ("focal", 100), ("wall", 50), ("plates", DoodadCommon) },
            [Bedroom] = new[] { ("focal", 100), ("wall", 60), ("mirror", 40), ("shelf", 25), ("basket", DoodadCommon) },
            [Hall] = new[] { ("hooks", 100), ("keys", 60), ("focal", 70), ("mirror", 35), ("wall", 35), ("trophy", 5), ("basket", DoodadCommon) },
            [Bathroom] = new[] { ("sink", 100), ("towel", 75), ("mirror", 20), ("toilet", DoodadCommon) },
            [Office] = new[] { ("focal", 100), ("notes", 70), ("certs", 50), ("wall", 30), ("shelf", 30), ("trophy", 5) },
            [BreakRoom] = new[] { ("focal", 100), ("notes", 70), ("game", DoodadUncommon) },
            [Garage] = new[] { ("focal", 100), ("hooks", 60), ("wall", 40), ("game", DoodadUncommon), ("basket", DoodadCommon) },
            [Workshop] = new[] { ("focal", 100), ("wall", 50) },
            [Storage] = new[] { ("focal", 60), ("shelf", 50) },
            [Shop] = new[] { ("focal", 100) },
        };
        /// <summary>The slots of fixtures, not art: they go with what they serve, outside the room's art budget. (And the doodad
        /// kit's slots: its pieces' own rarity is their budget, and the art a room hangs does not crowd them out.)</summary>
        public static readonly IReadOnlyCollection<string> DecorFixtures = new HashSet<string> { "cabinets", "rail", "sink", "towel", "hooks", "keys", "toilet", "plates", "game", "basket" };

        /// <summary>Testing only: a table to hang in place of <see cref="Decor"/> on this thread (null: Decor).</summary>
        [ThreadStatic] public static IReadOnlyList<DecorRow> DecorOverride;
        /// <summary>Debugging only: told each piece Decorate hangs and the row it came from (null: nothing is told).</summary>
        [ThreadStatic] public static Action<FurniturePlan, DecorRow> DecorTrace;

        /// <summary>THE DENSITY KNOB: how much wall décor a room gets, 1 as tuned. Above 1 each art slot's odds go up by this
        /// factor (to a sure thing), the room's art budget grows by it, and the recipe's art slots are gone through again --
        /// ceil(DecorDensity) times in all -- so a bigger room takes a second painting, a second group, on another wall. The
        /// spacing rules hold at any density: one big piece to a wall, DecorFill of a wall at most, DecorGap apart; below 1
        /// the odds and the budget shrink. Every draw at 1 is the draw the generator made before the knob.</summary>
        public const float DecorDensity = 1f;
        /// <summary>A room hangs art in at most 1 + one more slot per DecorEvery of free wall -- wall with no window, door or
        /// flight by it -- (a big home one more again: more art in a bigger house), times DecorDensity, and never more than
        /// DecorMost a pass.</summary>
        public const float DecorEvery = 6f;
        public const int DecorMost = 5;
        /// <summary>A home this big (rooms, or storeys) is a big home.</summary>
        public const int BigHomeRooms = 8, BigHomeFloors = 2;
        /// <summary>Hung pieces keep DecorGap apart (and from the clock and the television) -- fixtures over a run of
        /// counters only DecorRunGap, side by side -- DecorEnd off a corner (a fixture over a counter in the corner, none), DecorOpenGap off the edge of a window or a
        /// doorway; what stands within DecorClear of one is under it, and must be lower than its bottom by DecorUnder (along
        /// the wall a fixture over a counter keeps only DecorRunGap: it abuts the fridge beside its counter, as in any
        /// kitchen). DecorRunGap is none: the floor run under them stands edge to edge (the kitchen's Abut, 2 mm), and at the
        /// 2 cm it was, a cabinet over the counter beside one already hung, or beside the fridge, found no room. A group's pieces hang DecorGroupGap apart; a wall's hung pieces, fixtures aside, take at most DecorFill
        /// of it.</summary>
        public const float DecorGap = 0.6f, DecorRunGap = 0f, DecorEnd = 0.2f, DecorOpenGap = 0.15f, DecorClear = 0.25f, DecorUnder = 0.1f,
                           DecorGroupGap = 0.3f, DecorFill = 0.5f;
        /// <summary>No hung piece -- the décor, the clock -- within this of a window, measured in plan from its jambs, at any
        /// height: not beside it, not over or under it (a towel rail under a bathroom's high slit), not round the corner
        /// from it (strawberry: "increase wall prop no-go-zones around windows"; it was DecorOpenGap, 0.15 m, and only at
        /// the piece's height). The berth one hung piece gives another, DecorGap: a window is the biggest thing on its
        /// wall, and a picture nearer to it than to the next picture reads as crowding it.</summary>
        public const float DecorWindowGap = DecorGap;
        /// <summary>Hung counters this far apart end to end, or nearer, are one run, closed up edge to edge (PackCounterRuns).</summary>
        public const float CounterRunGap = 0.2f;
        /// <summary>A hung piece's back stands this far off the wall's face; Hang looks this far past its face for a
        /// window's or a doorway's zone (the television's 0.6 m kept every poster off a garage's side walls, the whole
        /// 6 m of its vehicle lane).</summary>
        public const float DecorWallGap = 0.01f, DecorReach = 0.1f;
        /// <summary>How far from its door a hook or a key rack hangs, at most.</summary>
        public const float DecorDoorReach = 3f;

        /// <summary>A décor row's piece, hung at its height: its bottom and top over the floor. (One hung under the ceiling: under
        /// the lowest a room has, OpeningTop -- a storey under another -- the most of the room it can take.)</summary>
        public static (float Bottom, float Top) DecorSpan(DecorRow row)
        {
            float h = FurnitureCatalog.Sizes[row.Name].H;
            return row.UnderCeiling ? (OpeningTop - 2f * h, OpeningTop) : (row.Height - h, row.Height + h);
        }

        sealed partial class Furnisher
        {
            /// <summary>The building's seed (Furnish), from which the décor pass seeds its own draws.</summary>
            public int Seed;
            /// <summary>The pieces this room's décor hung, and the row each came from.</summary>
            readonly Dictionary<FurniturePlan, DecorRow> _hung = new();

            /// <summary>Hang the room's wall décor, once its whole floor is furnished (Furnish): its kind's recipe (<see cref="DecorRecipes"/>), slot by slot,
            /// from the rows (<see cref="Decor"/>) the room's kind and the house allow. Its draws are its OWN: an Rng seeded
            /// from the building's seed and this room's key (its floor and first rectangle) -- never the Furnisher's shared
            /// one, every draw from which would move every later room's furniture. It reads the room's furniture and leaves
            /// the floor plan as it found it.</summary>
            public void Decorate()
            {
                if (!DecorRecipes.TryGetValue(_room.Kind, out var recipe)) return;
                var rows = (DecorOverride ?? Decor).Where(r => r.Rooms.Contains(_room.Kind) && FurnitureCatalog.Sizes.ContainsKey(r.Name) && Needs(r)).ToList();
                if (rows.Count == 0) return;
                var rng = new Rng(unchecked((((((Seed * 486187739 + _room.Floor) * 486187739 + _room.X0) * 486187739 + _room.Z0) * 486187739 + _room.X1) * 486187739 + _room.Z1) ^ 0x0DEC0));
                var count = new Dictionary<DecorRow, int>();
                bool big = _res.Kind is BuildingKind.Residential or BuildingKind.Apartment
                           && (_res.Floors >= BigHomeFloors || _res.Layout.Count(g => g.Unit == _room.Unit && !g.Circulation) >= BigHomeRooms);
                float free = FreeWall();
                int passes = Math.Max(1, (int)MathF.Ceiling(DecorDensity));
                int budget = Math.Min(DecorMost * passes, (int)MathF.Round((1 + (int)(free / DecorEvery) + (big ? 1 : 0)) * DecorDensity)), art = 0;
                FurnishTrace?.Invoke(System.FormattableString.Invariant($"decor in {_room}: {free:0.0} m of free wall, {budget} art slots, rows {string.Join(", ", rows.Select(r => r.Name))}"));
                // the recipe once, fixtures and art; then, at a density over 1, its art slots again
                var plan = recipe.Concat(Enumerable.Range(1, passes - 1).SelectMany(_ => recipe.Where(sl => !DecorFixtures.Contains(sl.Slot))));
                foreach (var (slot, chance) in plan)
                {
                    bool fixture = DecorFixtures.Contains(slot);
                    var cands = rows.Where(r => r.Fills(slot)).ToList();
                    // (a slot with nothing to hang draws nothing: the draws stay where they were for the slots after it)
                    if (cands.Count == 0 || (!fixture && art >= budget)) continue;
                    if (rng.Int(100) >= (int)MathF.Min(100f, chance * DecorDensity)) continue;
                    while (true)
                    {
                        var open = cands.Where(r => count.GetValueOrDefault(r) < r.Max && r.Weight > 0).ToList();
                        if (open.Count == 0) break;
                        int roll = rng.Int(open.Sum(r => r.Weight));
                        var pick = open[^1];
                        foreach (var r in open) { if (roll < r.Weight) { pick = r; break; } roll -= r.Weight; }
                        int n = 0;
                        // a fixture over furniture hangs over each of its pieces it can (a run of cabinets); the rest once
                        while (count.GetValueOrDefault(pick) < pick.Max && HangDecor(pick, rng, rows))
                        {
                            count[pick] = count.GetValueOrDefault(pick) + 1; n++;
                            if (pick.Spot != DecorSpot.Over) break;
                        }
                        // the hung counters closed up, before anything else is hung beside them
                        if (n > 0 && pick.Name == "Upper_Counter_0") PackCounterRuns();
                        if (n > 0) { if (!fixture) art++; break; }
                        cands.Remove(pick);
                    }
                }
            }

            /// <summary>The hung counters close up (strawberry: "fix gaps between counters"): each run of them on one wall --
            /// turned the same way, backs on one line, each within CounterRunGap of the next end to end -- laid edge to edge
            /// about the run's middle, so they abut whatever the floor pieces under them leave between their own (a kitchen's
            /// run's pieces stand 2 mm apart, Abut; others more), each moved a few centimetres at most and hung again under the ceiling over it
            /// (UnderCeiling). A run that would come nearer a window than DecorWindowGap, or onto another hung piece, stays as
            /// it was.</summary>
            void PackCounterRuns()
            {
                const string name = "Upper_Counter_0";
                var s = FurnitureCatalog.Sizes[name];
                float w = s.X1 - s.X0;
                foreach (var line in _mine.Where(m => m.Name == name && m.Mounted).GroupBy(m => (MathF.Round(m.Yaw), MathF.Round((FrontOf(m.Yaw).X != 0 ? m.X : m.Z) * 20f))))
                {
                    int fx = FrontOf(line.First().Yaw).X;
                    float Along(FurniturePlan m) => fx != 0 ? m.Z : m.X;
                    var sorted = line.OrderBy(Along).ToList();
                    var run = new List<FurniturePlan> { sorted[0] };
                    for (int i = 1; i <= sorted.Count; i++)
                    {
                        if (i < sorted.Count && Along(sorted[i]) - Along(run[^1]) <= w + CounterRunGap) { run.Add(sorted[i]); continue; }
                        if (run.Count > 1)
                        {
                            float start = (Along(run[0]) + Along(run[^1])) * 0.5f - (run.Count - 1) * w * 0.5f;
                            var was = run.Select(m => (m.X, m.Y, m.Z)).ToList();
                            for (int k = 0; k < run.Count; k++)
                            {
                                var m = run[k];
                                if (fx != 0) m.Z = start + k * w; else m.X = start + k * w;
                                m.Y = _room.Floor * Storey + UnderCeiling(m.Name, (m.X, m.Z, m.Yaw));
                            }
                            bool ok = run.All(m => { var f = BackFace(m.Name, m.X, m.Z, m.Yaw); return !WindowCuts(f).Any(c => c.A < f.Hi && c.B > f.Lo); })
                                      && run.All(m => !_res.Furniture.Any(o => o.Mounted && o.Floor == m.Floor && !run.Contains(o) && Overlaps(m.Footprint, o.Footprint)));
                            if (!ok) for (int k = 0; k < run.Count; k++) (run[k].X, run[k].Y, run[k].Z) = was[k];
                        }
                        if (i < sorted.Count) run = new List<FurniturePlan> { sorted[i] };
                    }
                }
            }

            /// <summary>Does this room meet a row's conditions (DecorNeed)?</summary>
            bool Needs(DecorRow r)
            {
                bool home = _res.Kind is BuildingKind.Residential or BuildingKind.Apartment;
                if (r.Need.HasFlag(DecorNeed.Home) && !home) return false;
                if (r.Need.HasFlag(DecorNeed.Business) && _res.Kind is not (BuildingKind.Commercial or BuildingKind.Industrial or BuildingKind.Strip)) return false;
                if (_room.Kind == RoomKind.Bedroom)
                {
                    var bed = _mine.FirstOrDefault(m => m.Name.StartsWith("Bed"));
                    if (r.Need.HasFlag(DecorNeed.DoubleBed) && (bed == null || !IsDoubleBed(bed.Name))) return false;
                    if (r.Need.HasFlag(DecorNeed.SingleBed) && (bed == null || IsDoubleBed(bed.Name))) return false;
                    if (r.Need.HasFlag(DecorNeed.Wardrobe) && !_mine.Any(m => m.Name.StartsWith("Wardrobe"))) return false;
                }
                if (r.Need.HasFlag(DecorNeed.OuterDoor) && DoorsOut(true).Count == 0) return false;
                return true;
            }

            /// <summary>The middles of this room's doorways, read off its walls: those to outside (no room past them on this
            /// floor: the front, the back, a side or a vehicle door; a flat's to the shared corridor) when `outside`, else
            /// every one.</summary>
            List<(float X, float Z)> DoorsOut(bool outside)
            {
                var l = new List<(float X, float Z)>();
                foreach (var w in RoomWalls())
                {
                    var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                    var (nx, nz) = (-dz, dx);
                    foreach (var o in w.Openings.Where(o => o.V < 0.05f))
                    {
                        float u = o.U + o.Width * 0.5f, cx = w.X + dx * u, cz = w.Z + dz * u, d = w.Thickness * 0.5f + 0.3f;
                        foreach (int sgn in new[] { 1, -1 })
                        {
                            float px = cx + sgn * nx * d, pz = cz + sgn * nz * d, qx = cx - sgn * nx * d, qz = cz - sgn * nz * d;
                            if (!_room.Holds(px, pz)) continue;
                            var past = _res.Layout.FirstOrDefault(g => g.Floor == _room.Floor && g.Holds(qx, qz));
                            if (!outside || past == null || (_room.Unit >= 0 && past.Unit < 0)) l.Add((px, pz));
                        }
                    }
                }
                return l;
            }

            /// <summary>The walls of this floor that the floor plan stands (full storey high), as they stand when asked: not
            /// kept from one call to the next, since the clock asks during the room's furnishing and the décor once the floor
            /// is done, and a window moved between the two is the décor's to see.</summary>
            List<WallPlan> RoomWalls() => _res.Walls.Where(w => w.Kind == SurfaceKind.Wall && (int)MathF.Round(w.Y / Storey) == _room.Floor
                                                                && w.Height > Storey - 0.01f).ToList();

            /// <summary>One face of wall into this room: its normal into the room (Nx, Nz), where it stands across, and the
            /// run of it along.</summary>
            readonly record struct Face(int Nx, int Nz, float At, float Lo, float Hi)
            {
                public int N => Nx != 0 ? Nx : Nz;
                public float Along(float x, float z) => Nx != 0 ? z : x;
                /// <summary>A box's extent along the face and in front of it.</summary>
                public (float A0, float A1, float D0, float D1) Span((float X0, float Z0, float X1, float Z1) b)
                {
                    float a0 = Nx != 0 ? b.Z0 : b.X0, a1 = Nx != 0 ? b.Z1 : b.X1;
                    float c0 = ((Nx != 0 ? b.X0 : b.Z0) - At) * N, c1 = ((Nx != 0 ? b.X1 : b.Z1) - At) * N;
                    return (a0, a1, MathF.Min(c0, c1), MathF.Max(c0, c1));
                }
            }

            IEnumerable<Face> AllFaces()
            {
                foreach (var (nx, nz) in Fronts)
                    foreach (var (at, lo, hi) in Faces(nx, nz)) yield return new Face(nx, nz, at, lo, hi);
            }

            /// <summary>The faces making one wall line with f: turned the same way, within 0.3 m across of it, their runs
            /// meeting end to end (within 0.3 m) -- an outside wall that runs on as a thinner partition, a step of 0.1 m.</summary>
            List<Face> WallLine(Face f)
            {
                var all = AllFaces().Where(g => g.Nx == f.Nx && g.Nz == f.Nz && MathF.Abs(g.At - f.At) < 0.3f).ToList();
                var line = new List<Face> { f };
                for (bool grew = true; grew;)
                {
                    grew = false;
                    foreach (var g in all)
                        if (!line.Contains(g) && line.Any(h => g.Lo <= h.Hi + 0.3f && g.Hi >= h.Lo - 0.3f)) { line.Add(g); grew = true; }
                }
                return line;
            }

            /// <summary>Is a hung piece on this face: turned to it, its back at it, along its run?</summary>
            bool HungOn(FurniturePlan m, Face f)
            {
                var q = f.Span(m.Footprint);
                float yaw = YawFacing(f.Nx, f.Nz);
                return m.Mounted && MathF.Abs(((m.Yaw - yaw) % 360f + 540f) % 360f - 180f) < 10f && q.D0 > -0.15f && q.D0 < 0.25f && q.A1 > f.Lo && q.A0 < f.Hi;
            }
            /// <summary>A room's big piece: a focal row's, or the flat television.</summary>
            bool IsFocal(FurniturePlan m) => _hung.TryGetValue(m, out var r) ? r.Focal : m.Name == "Television_0";
            bool IsFixture(FurniturePlan m) => _hung.TryGetValue(m, out var r) && r.Spot == DecorSpot.Over;

            /// <summary>The stretches of a face a piece `depth` deep, hung from `bottom` to `top`, may span: the run less
            /// DecorEnd at each corner, every doorway in the wall behind it at that height (and DecorOpenGap either side), all
            /// within DecorWindowGap of a window at any height (WindowCuts), the stairs' reach (StairWallReach, as ByStairs),
            /// and -- with `row` -- the pieces already hung on it (DecorGap; DecorRunGap between fixtures) and whatever stands
            /// within DecorClear of it taller than its bottom less DecorUnder (along the wall, DecorRunGap for a fixture).</summary>
            List<(float A, float B)> Stretches(Face f, float depth, float bottom, float top, DecorRow row)
            {
                var cuts = new List<(float, float)>(WindowCuts(f));
                float floorY = _room.Floor * Storey;
                foreach (var w in RoomWalls())
                {
                    var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                    // along this face, and standing right behind it
                    if (MathF.Abs(f.Nx != 0 ? dz : dx) < 0.99f) continue;
                    var s = f.Span(WallBox(w));
                    if (s.D1 < -0.12f || s.D1 > 0.12f || s.A1 < f.Lo || s.A0 > f.Hi) continue;
                    // (the windows are WindowCuts')
                    foreach (var o in w.Openings.Where(o => o.V < 0.05f))
                    {
                        float v0 = w.Y + o.V - floorY, v1 = v0 + o.Height;
                        if (v1 < bottom - DecorUnder || v0 > top + DecorUnder) continue;
                        float a = f.Along(w.X + dx * o.U, w.Z + dz * o.U), b = f.Along(w.X + dx * o.U1, w.Z + dz * o.U1);
                        cuts.Add((MathF.Min(a, b) - DecorOpenGap, MathF.Max(a, b) + DecorOpenGap));
                    }
                }
                foreach (var r in _p.StairRects)
                {
                    var s = f.Span(r);
                    if (s.D0 < depth + 0.03f + StairWallReach && s.D1 > -StairWallReach) cuts.Add((s.A0 - StairWallReach, s.A1 + StairWallReach));
                }
                if (row != null)
                {
                    bool fixture = row.Spot == DecorSpot.Over;
                    foreach (var o in _res.Furniture.Where(o => o.Floor == _room.Floor))
                    {
                        var s = f.Span(o.Footprint);
                        if (o.Mounted)
                        {
                            // (on this side of the wall: DecorGap is wider than a 0.5 m partition, and a picture in the next
                            // room, its back to the same wall, kept this room's side of it bare -- but a piece buried in the wall
                            // to within 0.3 m of this face still counts: a television hung through a corner, its end 0.28 m
                            // into the partition, had a hall painting 0.22 m from it on the other side)
                            float gap = fixture && IsFixture(o) ? DecorRunGap : DecorGap;
                            if (s.D1 > -0.3f && s.D0 < depth + gap) cuts.Add((s.A0 - gap, s.A1 + gap));
                            continue;
                        }
                        // its top over the floor: a piece on another stands on its top; a ceiling light hangs down from the ceiling
                        float h = FurnitureCatalog.Sizes[o.Name].H;
                        bool under = o.Ceiling ? o.Y - floorY - (o.Name.Contains("Pendant") ? PendantDrop : 0.28f) < top + DecorUnder
                                               : o.Y - floorY + h > bottom - DecorUnder;
                        float along = fixture && !o.Ceiling ? DecorRunGap : DecorClear;
                        if (under && s.D1 > -DecorClear && s.D0 < depth + DecorClear) cuts.Add((s.A0 - along, s.A1 + along));
                    }
                }
                // the run less the cuts (a fixture right into the corner, over the counter there)
                float end = row?.Spot == DecorSpot.Over ? 0f : DecorEnd;
                var free = new List<(float A, float B)> { (f.Lo + end, f.Hi - end) };
                foreach (var (c0, c1) in cuts)
                    free = free.SelectMany(q => c1 <= q.A || c0 >= q.B ? new[] { q } : new (float A, float B)[] { (q.A, c0), (c1, q.B) }).Where(q => q.B - q.A > 0.01f).ToList();
                return free;
            }

            /// <summary>The windows into this room -- openings off the floor (a bathroom's slit, a vent and the fireplace too)
            /// in the walls of its floor, the room on one side of them -- and that side (+1: the wall's -dz, dx normal). Read
            /// off the walls as they stand when asked, never the floor plan's grid, which is laid before the rooms are
            /// furnished: whatever moves a window before the décor (a room's layout shrinking one) is seen.</summary>
            List<(WallPlan W, WallOpening O, int Side)> Windows()
            {
                var l = new List<(WallPlan, WallOpening, int)>();
                foreach (var w in RoomWalls())
                {
                    var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                    var (nx, nz) = (-dz, dx);
                    float d = w.Thickness * 0.5f + 0.3f;
                    foreach (var o in w.Openings.Where(o => o.V >= 0.05f))
                    {
                        float u = o.U + o.Width * 0.5f, cx = w.X + dx * u, cz = w.Z + dz * u;
                        foreach (int sgn in new[] { 1, -1 })
                            if (_room.Holds(cx + sgn * nx * d, cz + sgn * nz * d)) l.Add((w, o, sgn));
                    }
                }
                return l;
            }

            /// <summary>The stretches of a face within DecorWindowGap of a window, in plan, at any height: the window's jambs
            /// on the room's face of its wall, as a segment, and the stretch of the face's line within DecorWindowGap of it --
            /// beside it on the wall behind, and round a corner, from the face's end, on a wall meeting it.</summary>
            IEnumerable<(float A, float B)> WindowCuts(Face f)
            {
                foreach (var (w, o, side) in Windows())
                {
                    var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                    float nx = -dz * side, nz = dx * side, t = w.Thickness * 0.5f;
                    float x0 = w.X + dx * o.U + nx * t, z0 = w.Z + dz * o.U + nz * t, x1 = w.X + dx * o.U1 + nx * t, z1 = w.Z + dz * o.U1 + nz * t;
                    float a0 = f.Along(x0, z0), a1 = f.Along(x1, z1);
                    float q0 = ((f.Nx != 0 ? x0 : z0) - f.At) * f.N, q1 = ((f.Nx != 0 ? x1 : z1) - f.At) * f.N;
                    // how far out from the face's line the segment comes nearest it (the face is the grid's, up to a cell in
                    // front of the wall's real one: in the wall behind it, a window is on it)
                    float q = q0 * q1 <= 0f ? 0f : MathF.Min(MathF.Abs(q0), MathF.Abs(q1));
                    q = MathF.Max(0f, q - 0.15f);
                    if (q >= DecorWindowGap) continue;
                    float h = MathF.Sqrt(DecorWindowGap * DecorWindowGap - q * q);
                    yield return (MathF.Min(a0, a1) - h, MathF.Max(a0, a1) + h);
                }
            }

            /// <summary>Would a hung piece at (x, z, yaw) have a window within DecorWindowGap (WindowCuts), or be on the
            /// fireplace's wall (FireFace)? For a piece hung by a room's own recipe (the clock), which takes no stretches.</summary>
            bool HangRefused(string name, float x, float z, float yaw)
            {
                var f = BackFace(name, x, z, yaw);
                return FireFace(f) || WindowCuts(f).Any(c => c.A < f.Hi && c.B > f.Lo);
            }

            /// <summary>The face a piece hung at (x, z, yaw) has its back on: turned its way, at its back, along its width.</summary>
            Face BackFace(string name, float x, float z, float yaw)
            {
                var fp = FurnitureCatalog.Footprint(name, x, z, yaw);
                var (fx, fz) = FrontOf(yaw);
                return fx != 0 ? new Face(fx, 0, fx > 0 ? fp.X0 : fp.X1, fp.Z0, fp.Z1) : new Face(0, fz, fz > 0 ? fp.Z0 : fp.Z1, fp.X0, fp.X1);
            }

            List<(float X0, float Z0, float X1, float Z1, int Nx, int Nz, bool Own)> _fires;
            /// <summary>The fireplaces whose wall this room has: each chimney breast (AddChimney: a full-storey wall
            /// ChimneyDeep thick, inside the chimney's footprint) standing in this room (Own) or in one open to it (open plan:
            /// the one wall runs on across the cut), as its box and its normal into the room.</summary>
            List<(float X0, float Z0, float X1, float Z1, int Nx, int Nz, bool Own)> Fires()
            {
                if (_fires != null) return _fires;
                _fires = new();
                var c = _res.Chimney;
                if (c.X1 <= c.X0) return _fires;
                foreach (var w in RoomWalls().Where(w => MathF.Abs(w.Thickness - ChimneyDeep) < 0.01f))
                {
                    var b = WallBox(w);
                    if (b.X0 < c.X0 - 0.05f || b.X1 > c.X1 + 0.05f || b.Z0 < c.Z0 - 0.05f || b.Z1 > c.Z1 + 0.05f) continue;
                    // its back to the house wall: the way out of it the wall is not
                    bool alongX = b.X1 - b.X0 > b.Z1 - b.Z0;
                    float mx = (b.X0 + b.X1) * 0.5f, mz = (b.Z0 + b.Z1) * 0.5f, half = alongX ? (b.Z1 - b.Z0) * 0.5f : (b.X1 - b.X0) * 0.5f;
                    bool WallAt(float px, float pz) => _p.WallRects.Any(r => px >= r.X0 && px <= r.X1 && pz >= r.Z0 && pz <= r.Z1);
                    foreach (int sgn in new[] { 1, -1 })
                    {
                        int nx = alongX ? 0 : sgn, nz = alongX ? sgn : 0;
                        if (!WallAt(mx - nx * (half + 0.15f), mz - nz * (half + 0.15f)) || WallAt(mx + nx * (half + 0.15f), mz + nz * (half + 0.15f))) continue;
                        var room = _res.Layout.FirstOrDefault(g => g.Floor == _room.Floor && g.Holds(mx + nx * (half + 0.3f), mz + nz * (half + 0.3f)));
                        if (room == null) continue;
                        if (room == _room || _res.OpenPlan.Any(o => o.Floor == _room.Floor && ((o.A == room && o.B == _room) || (o.B == room && o.A == _room))))
                            _fires.Add((b.X0, b.Z0, b.X1, b.Z1, nx, nz, room == _room));
                    }
                }
                return _fires;
            }

            /// <summary>Is a face on a fireplace's wall (strawberry: "prevent wall props placing on fireplace walls")? The
            /// whole of it: the house wall the breast stands against, both sides of it (the breast splits it in two), the
            /// breast's front over the fire, and its two cheeks -- in the fireplace's room, and in a room open to it but for
            /// a `fixture` (upper cabinets over a kitchen's counters, open to the living room: they go with the counters).</summary>
            bool FireFace(Face f, bool fixture = false)
            {
                foreach (var (x0, z0, x1, z1, nx, nz, own) in Fires())
                {
                    if (fixture && !own) continue;
                    var s = f.Span((x0, z0, x1, z1));
                    if (f.Nx == nx && f.Nz == nz)
                    {
                        // the house wall: the breast stands out of it, its back in the face's plane, anywhere along
                        if (MathF.Abs(s.D0) <= 0.2f) return true;
                        // the breast's front: the breast behind it, over its run
                        if (MathF.Abs(s.D1) <= 0.2f && f.Hi > s.A0 && f.Lo < s.A1) return true;
                    }
                    // a cheek: square to the house wall, at the breast's end, over its depth
                    else if (f.Nx != -nx || f.Nz != -nz)
                        if (MathF.Abs(s.D1) <= 0.2f && f.Hi > s.A0 + 0.05f && f.Lo < s.A1 - 0.05f) return true;
                }
                return false;
            }

            /// <summary>A wall's footprint, as FloorPlan lays it on the grid.</summary>
            static (float X0, float Z0, float X1, float Z1) WallBox(WallPlan w)
            {
                var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                float dx = wc, dz = -ws, t = w.Thickness * 0.5f;
                float ex = w.X + dx * w.Length, ez = w.Z + dz * w.Length;
                float tx = MathF.Abs(dz) * t, tz = MathF.Abs(dx) * t;
                return (MathF.Min(w.X, ex) - tx, MathF.Min(w.Z, ez) - tz, MathF.Max(w.X, ex) + tx, MathF.Max(w.Z, ez) + tz);
            }

            /// <summary>How much of this room's wall art could hang on: its faces less their windows, doorways and stairs, and
            /// less a fireplace's wall.</summary>
            float FreeWall() => AllFaces().Where(f => !FireFace(f)).Sum(f => Stretches(f, 0.2f, 1.2f, 3.4f, null).Sum(q => q.B - q.A));

            /// <summary>Where a piece hangs on a face with its middle `t` along it: its back DecorWallGap off the wall's real
            /// face, and on wall along all but a sliver (Flush, Backed), off the stairs' wall; null where it is not. (The
            /// face Faces gives is the grid's, up to a cell in front of the wall: a floor piece stood off it by Against's 3 cm
            /// is a few centimetres from the wall either way, but a poster that far out shows the gap behind it.)</summary>
            (float X, float Z, float Yaw)? AtFace(string name, Face f, float t)
            {
                var s = FurnitureCatalog.Sizes[name];
                float yaw = YawFacing(f.Nx, f.Nz), half = (s.X1 - s.X0) * 0.5f;
                // the wall nearest the room behind the piece's stretch of the face (where the line of a wall steps in
                // thickness, the thick part: the piece must not run into it)
                float face = float.NaN;
                foreach (var r in _p.WallRects)
                {
                    var q = f.Span(r);
                    if (q.A1 <= t - half + 0.01f || q.A0 >= t + half - 0.01f || q.D1 < -0.15f || q.D1 > 0.05f) continue;
                    float at = f.At + q.D1 * f.N;
                    if (float.IsNaN(face) || (at - face) * f.N > 0) face = at;
                }
                if (float.IsNaN(face)) return null;
                float off = DecorWallGap - s.Y0;
                var c = f.Nx != 0 ? (X: face + f.Nx * off, Z: t, Yaw: yaw) : (X: t, Z: face + f.Nz * off, Yaw: yaw);
                // (its middle along the face at t, whichever way its mesh runs)
                var fp = FurnitureCatalog.Footprint(name, c.X, c.Z, yaw);
                float shift = t - (f.Along(fp.X0, fp.Z0) + f.Along(fp.X1, fp.Z1)) * 0.5f;
                c = f.Nx != 0 ? (c.X, c.Z + shift, yaw) : (c.X + shift, c.Z, yaw);
                fp = FurnitureCatalog.Footprint(name, c.X, c.Z, yaw);
                if (Flush(fp, f.Nx, f.Nz) < 0.9 || !Backed(name, c.X, c.Z, yaw) || ByStairs(name, c)) return null;
                return c;
            }

            /// <summary>Where a piece hung at `p` actually goes: onto the real face of the wall behind it (AtFace: its back
            /// DecorWallGap off it, flush and backed, off the stairs' wall), and only where the WHOLE of it is on that wall --
            /// every 0.05 m of its back edge, end to end, over solid wall full storey high (not past the wall's end into a
            /// doorway or round a corner, not where a thinner wall carries on -- a step behind it), no opening there over its
            /// height `height` +- its H, and none of it into a wall. Null where not. (The candidates hung pieces are ranked from
            /// are the floor plan's faces: a 0.1 m grid on which a cell a wall's end only touches counts as wall, and whose
            /// face stands up to a cell in front of the wall's -- so a 3.8 m television ran a quarter metre past a wall's end,
            /// 8 cm off its face.)</summary>
            (float X, float Z, float Yaw)? OnWall(string name, (float X, float Z, float Yaw) p, float height)
            {
                var f = BackFace(name, p.X, p.Z, p.Yaw);
                var at = AtFace(name, f, (f.Lo + f.Hi) * 0.5f);
                if (at == null) return null;
                var s = FurnitureCatalog.Sizes[name];
                var fp = FurnitureCatalog.Footprint(name, at.Value.X, at.Value.Z, at.Value.Yaw);
                if (_p.WallRects.Any(r => Overlaps(fp, r))) return null;
                var (fx, fz) = FrontOf(at.Value.Yaw);
                var (rx, rz) = RightOf(at.Value.Yaw);
                float bottom = height - s.H, top = height + s.H, floorY = _room.Floor * Storey;
                var walls = RoomWalls();
                for (float u = s.X0 + 0.02f; u <= s.X1 - 0.02f + 1e-3f; u += 0.05f)
                {
                    // 3 cm behind its back: in the wall, whose face is DecorWallGap behind it
                    float px = at.Value.X + rx * u + fx * (s.Y0 - 0.03f), pz = at.Value.Z + rz * u + fz * (s.Y0 - 0.03f);
                    bool held = false;
                    foreach (var w in walls)
                    {
                        var (c, sn) = PlanTrig.CosSin(w.Yaw);
                        float wu = (px - w.X) * c - (pz - w.Z) * sn, wv = (px - w.X) * sn + (pz - w.Z) * c;
                        if (wu < 0f || wu > w.Length || MathF.Abs(wv) > w.Thickness * 0.5f) continue;
                        if (w.Openings.Any(o => wu > o.U - 0.01f && wu < o.U1 + 0.01f && w.Y + o.V - floorY < top && w.Y + o.V + o.Height - floorY > bottom)) return null;
                        held = true;
                    }
                    if (!held) return null;
                }
                return at;
            }

            /// <summary>The height a piece hangs at, over its floor, to meet the ceiling over it with its top: under the lowest of
            /// the ceiling over its four corners.</summary>
            float UnderCeiling(string name, (float X, float Z, float Yaw) at)
            {
                var fp = FurnitureCatalog.Footprint(name, at.X, at.Z, at.Yaw);
                float ceiling = new[] { (fp.X0, fp.Z0), (fp.X1, fp.Z0), (fp.X0, fp.Z1), (fp.X1, fp.Z1) }.Min(p => CeilingOver(p.Item1, p.Item2));
                return ceiling - _room.Floor * Storey - FurnitureCatalog.Sizes[name].H;
            }

            /// <summary>Is there floor to stand on before a piece hung at (x, z, yaw): `depth` of this room's clear floor in
            /// front of its face, across its width -- no piece, wall or stair on it, not the tile at a flight's end (where you
            /// step off the stairs), no door's leaf swinging through it? (The stairs and tiles exactly, as Put has them: the
            /// grid's cell centres miss a sliver.)</summary>
            bool StandClear(string name, float x, float z, float yaw, float depth)
            {
                var s = FurnitureCatalog.Sizes[name];
                var (fx, fz) = FrontOf(yaw); var (rx, rz) = RightOf(yaw);
                float ax = x + rx * s.X0 + fx * s.Y1, az = z + rz * s.X0 + fz * s.Y1, bx = x + rx * s.X1 + fx * (s.Y1 + depth), bz = z + rz * s.X1 + fz * (s.Y1 + depth);
                var zone = (MathF.Min(ax, bx), MathF.Min(az, bz), MathF.Max(ax, bx), MathF.Max(az, bz));
                foreach (var (i, j) in _p.Cells(zone)) if (!InRoom(i, j) || _p.State[i, j] != FloorPlan.Free) return false;
                // (and the pieces' own outlines: one reaching a few cm into the zone marks no cell of it -- a washer turned
                // side-on beside the mirror's wall)
                if (_res.Furniture.Any(o => o.Floor == _room.Floor && o.On == null && !o.Mounted && !o.Ceiling && Overlaps(zone, o.Footprint))) return false;
                return !_p.LeafRects.Concat(_p.StairRects).Concat(_p.TileRects).Any(r => Overlaps(zone, r));
            }

            /// <summary>A group's pieces, first to last: the row's own, then its look-alikes in the table (the same mesh size,
            /// the name but for its number) taking turns -- three small paintings, not one three times.</summary>
            List<string> GroupNames(DecorRow row, int n, IReadOnlyList<DecorRow> rows)
            {
                string stem = row.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                var s = FurnitureCatalog.Sizes[row.Name];
                var like = rows.Where(r => r != row && r.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') == stem
                                           && FurnitureCatalog.Sizes[r.Name].X0 == s.X0 && FurnitureCatalog.Sizes[r.Name].X1 == s.X1).Select(r => r.Name).ToList();
                var l = new List<string> { row.Name };
                for (int k = 1; k < n; k++) l.Add(like.Count == 0 ? row.Name : like[(k - 1) % like.Count]);
                return l;
            }

            /// <summary>Hang one of a row (a group of them, side by side) where its spot says, best first: the middle of the
            /// longest free stretch, centred over an Over piece or beside a Beside piece where the row has them, with a draw of
            /// the décor's own per stretch for which wall -- never on a fireplace's wall, never a second focal piece on a wall,
            /// nor more than DecorFill of a wall's length hung. Each piece goes in through Hang (no window or door in front of
            /// it, nothing hung there already). False when it fits nowhere.</summary>
            bool HangDecor(DecorRow row, Rng rng, IReadOnlyList<DecorRow> rows)
            {
                var s = FurnitureCatalog.Sizes[row.Name];
                var (bottom, top) = DecorSpan(row);
                float w = s.X1 - s.X0, depth = s.Y1 - s.Y0;
                int n = row.Group > 1 ? 2 + rng.Int(row.Group - 1) : 1;
                float span = n * w + (n - 1) * DecorGroupGap, half = span * 0.5f;
                var names = GroupNames(row, n, rows);
                bool Is(FurniturePlan m, string[] pre) => pre != null && m.On == null && !m.Mounted && !m.Ceiling && pre.Any(p => m.Name.StartsWith(p));
                var doors = row.Spot == DecorSpot.Door ? DoorsOut(true) is { Count: > 0 } outside ? outside : DoorsOut(false) : null;
                var cands = new List<(Face F, float T, double Score)>();
                foreach (var f in AllFaces())
                {
                    // nothing on a fireplace's wall, any of it
                    if (FireFace(f, row.Spot == DecorSpot.Over)) continue;
                    float yaw = YawFacing(f.Nx, f.Nz);
                    var onIt = _mine.Where(m => HungOn(m, f)).ToList();
                    // one big piece to a wall -- the whole wall line, not the face's run: where an outside wall runs on as a
                    // partition 0.1 m thinner the grid sees two faces, and at DecorDensity 2 two landscapes went up on what
                    // is one wall -- and a wall not filled past DecorFill (fixtures over counters aside)
                    if (row.Focal && WallLine(f).Any(g => _mine.Any(m => IsFocal(m) && HungOn(m, g)))) continue;
                    if (row.Spot != DecorSpot.Over && onIt.Where(m => !IsFixture(m)).Sum(m => { var q = f.Span(m.Footprint); return q.A1 - q.A0; }) + span > DecorFill * (f.Hi - f.Lo)) continue;
                    // the pieces standing at this face (not at another face the same way round, behind it or across the
                    // room), with their backs to it for Over, and where along it their middles are
                    bool AtThis(FurniturePlan m) { var q = f.Span(m.Footprint); return q.D0 > -0.1f && q.D0 < 0.25f; }
                    var over = _mine.Where(m => Is(m, row.Over) && AtThis(m) && MathF.Abs(((m.Yaw - yaw) % 360f + 540f) % 360f - 180f) < 10f)
                                    .Select(m => { var q = f.Span(m.Footprint); return (q.A0 + q.A1) * 0.5f; }).ToList();
                    var beside = _mine.Where(m => Is(m, row.Beside) && AtThis(m)).Select(m => f.Span(m.Footprint)).ToList();
                    foreach (var (a, b) in Stretches(f, depth, bottom, top, row))
                    {
                        if (b - a < span) continue;
                        float lo = a + half, hi = b - half, mid = (a + b) * 0.5f;
                        // one draw per stretch, in order: which wall it goes on, among the good ones
                        double stretch = 0.2 * (b - a) + rng.Int(1000) * 0.002;
                        var ts = new List<(float T, double Bonus)>();
                        foreach (float t in over) if (t >= lo - 1e-3f && t <= hi + 1e-3f) ts.Add((t, 10));
                        foreach (var q in beside)
                            foreach (float t in new[] { q.A0 - DecorClear - 0.05f - half, q.A1 + DecorClear + 0.05f + half })
                                if (t >= lo - 1e-3f && t <= hi + 1e-3f) ts.Add((t, 5));
                        if (row.Spot != DecorSpot.Over)
                        {
                            ts.Add((mid, 0));
                            for (float t = lo; t <= hi + 1e-3f; t += 0.25f) ts.Add((t, 0));
                        }
                        foreach (var (t, bonus) in ts)
                        {
                            double score = stretch + bonus - MathF.Abs(t - mid);
                            if (doors != null)
                            {
                                float px = f.Nx != 0 ? f.At : t, pz = f.Nx != 0 ? t : f.At;
                                float d = doors.Min(p => MathF.Sqrt((p.X - px) * (p.X - px) + (p.Z - pz) * (p.Z - pz)));
                                if (d > DecorDoorReach) continue;
                                score = stretch * 0.1 - d;
                            }
                            cands.Add((f, t, score));
                        }
                    }
                }
                int offWall = 0, refused = 0;
                foreach (var (f, t, _) in cands.OrderByDescending(c => c.Score).Take(60))
                {
                    var placed = new List<FurniturePlan>();
                    for (int k = 0; k < n; k++)
                    {
                        var at = AtFace(names[k], f, t - half + w * 0.5f + k * (w + DecorGroupGap));
                        string name = names[k];
                        var m = at == null ? null : Hang(name, new[] { at.Value }, null, row.UnderCeiling ? UnderCeiling(name, at.Value) : row.Height, rng, DecorReach,
                                                         row.Stand > 0f ? (x, z, yaw) => !StandClear(name, x, z, yaw, row.Stand) : null);
                        if (at == null) offWall++; else if (m == null) refused++;
                        if (m == null) break;
                        placed.Add(m);
                        _hung[m] = row;
                    }
                    if (placed.Count == n)
                    {
                        FurnishTrace?.Invoke(System.FormattableString.Invariant($"decor {row.Name} x{n} in {_room}: hung at ({placed[0].X:0.00},{placed[0].Z:0.00}) yaw {placed[0].Yaw:0}, {cands.Count} candidates, {offWall} off a wall, {refused} refused by Hang"));
                        if (DecorTrace != null) foreach (var m in placed) DecorTrace(m, row);
                        return true;
                    }
                    foreach (var m in placed) { _hung.Remove(m); Remove(m); }
                }
                FurnishTrace?.Invoke(System.FormattableString.Invariant($"decor {row.Name} x{n} in {_room}: nowhere, {cands.Count} candidates, {offWall} off a wall, {refused} refused by Hang"));
                return false;
            }
        }
    }
}
