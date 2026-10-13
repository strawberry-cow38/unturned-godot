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
    /// (Focal: the television is one). Tags are the manifest's, for the record.</summary>
    public sealed record DecorRow(string Name, RoomKind[] Rooms, float Height, string Slot, int Weight = 10, int Max = 1, DecorNeed Need = DecorNeed.None,
                                  DecorSpot Spot = DecorSpot.Wall, string[] Over = null, string[] Beside = null, int Group = 1, bool Focal = false,
                                  string Tags = "")
    {
        public bool Fills(string slot) => (" " + Slot + " ").Contains(" " + slot + " ");
    }

    public static partial class BuildingGen
    {
        static RoomKind[] In(params RoomKind[] k) => k;
        static readonly string[] CouchOrDouble = { "Couch", "Bed_0" }, CouchOrBed = { "Couch", "Bed_0", "Bed_1" };

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
            new("Wall_Shelf_0", In(LivingRoom, Bedroom, Office), 2.5f, "shelf", Weight: 5, Over: new[] { "Couch" }, Tags: "shelf neutral"),
            new("Wall_Shelf_1", In(LivingRoom, Bedroom, Office), 2.7f, "shelf", Weight: 5, Over: new[] { "Couch" }, Tags: "shelf books plant clutter"),
            new("Mirror_0", In(Bathroom, Hall, Bedroom), 2.65f, "sink mirror", Weight: 8, Need: DecorNeed.Wardrobe, Over: new[] { "Counter_1" }, Beside: new[] { "Wardrobe" }, Tags: "mirror wardrobe"),
            new("Antlers_0", In(LivingRoom, Hall), 2.9f, "trophy", Weight: 1, Need: DecorNeed.Home, Tags: "rustic rural rare"),
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
            // fixtures, over what they serve: upper cabinets over the counters, sink and dishwasher (wood over the wood set,
            // grey over the grey set's Counter_2 -- which the furnisher does not place yet, so the grey waits), a utensil
            // rail over the stove, a medicine cabinet over the basin, a towel rail by the tub or the basin
            new("Wall_Cabinet_0", In(Kitchen), 2.9f, "cabinets", Max: 8, Spot: DecorSpot.Over, Over: new[] { "Counter_0", "Counter_1", "Disher_0" }, Tags: "upper_cabinet wood counter_0"),
            new("Wall_Cabinet_1", In(Kitchen), 2.9f, "cabinets", Max: 8, Spot: DecorSpot.Over, Over: new[] { "Counter_2" }, Tags: "upper_cabinet grey counter_2"),
            new("Utensil_Rail_0", In(Kitchen), 2.15f, "rail", Spot: DecorSpot.Over, Over: new[] { "Oven_0", "Counter_0" }, Tags: "counter cooking"),
            new("Medicine_Cabinet_0", In(Bathroom), 2.5f, "sink", Weight: 8, Spot: DecorSpot.Over, Over: new[] { "Counter_1" }, Tags: "mirror sink"),
            new("Towel_Rail_0", In(Bathroom), 1.6f, "towel", Beside: new[] { "Tub", "Counter_1" }, Tags: "towel wash"),
        };

        /// <summary>What each kind of room hangs, in order (VoX's brief, "coherent per room, not random"): its slots, each
        /// filled at the given odds by one of the rows that fill it there (a weighted draw; one that will not go anywhere
        /// gives way to the next). The art slots -- all but the fixtures' (DecorFixtures) -- are bounded by the room's free
        /// wall (DecorEvery); every piece by its wall (one focal piece, DecorFill of its length).</summary>
        public static readonly IReadOnlyDictionary<RoomKind, (string Slot, int Chance)[]> DecorRecipes = new Dictionary<RoomKind, (string, int)[]>
        {
            [Kitchen] = new[] { ("cabinets", 100), ("rail", 70), ("notes", 60) },
            [LivingRoom] = new[] { ("focal", 100), ("wall", 70), ("shelf", 40), ("trophy", 8) },
            [DiningRoom] = new[] { ("focal", 100), ("wall", 50) },
            [Bedroom] = new[] { ("focal", 100), ("wall", 60), ("mirror", 40), ("shelf", 25) },
            [Hall] = new[] { ("hooks", 100), ("keys", 60), ("focal", 70), ("mirror", 35), ("wall", 35), ("trophy", 5) },
            [Bathroom] = new[] { ("sink", 100), ("towel", 75) },
            [Office] = new[] { ("focal", 100), ("notes", 70), ("certs", 50), ("wall", 30), ("shelf", 30) },
            [BreakRoom] = new[] { ("focal", 100), ("notes", 70) },
            [Garage] = new[] { ("focal", 100), ("hooks", 60), ("wall", 40) },
            [Workshop] = new[] { ("focal", 100), ("wall", 50) },
            [Storage] = new[] { ("focal", 60) },
            [Shop] = new[] { ("focal", 100) },
        };
        /// <summary>The slots of fixtures, not art: they go with what they serve, outside the room's art budget.</summary>
        public static readonly IReadOnlyCollection<string> DecorFixtures = new HashSet<string> { "cabinets", "rail", "sink", "towel", "hooks", "keys" };

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
        /// kitchen). A group's pieces hang DecorGroupGap apart; a wall's hung pieces, fixtures aside, take at most DecorFill
        /// of it.</summary>
        public const float DecorGap = 0.6f, DecorRunGap = 0.02f, DecorEnd = 0.2f, DecorOpenGap = 0.15f, DecorClear = 0.25f, DecorUnder = 0.1f,
                           DecorGroupGap = 0.3f, DecorFill = 0.5f;
        /// <summary>A hung piece's back stands this far off the wall's face; Hang looks this far past its face for a
        /// window's or a doorway's zone (the television's 0.6 m kept every poster off a garage's side walls, the whole
        /// 6 m of its vehicle lane).</summary>
        public const float DecorWallGap = 0.01f, DecorReach = 0.1f;
        /// <summary>How far from its door a hook or a key rack hangs, at most.</summary>
        public const float DecorDoorReach = 3f;

        /// <summary>A décor row's piece, hung at its height: its bottom and top over the floor.</summary>
        public static (float Bottom, float Top) DecorSpan(DecorRow row)
        {
            float h = FurnitureCatalog.Sizes[row.Name].H;
            return (row.Height - h, row.Height + h);
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
                        if (n > 0) { if (!fixture) art++; break; }
                        cands.Remove(pick);
                    }
                }
            }

            /// <summary>Does this room meet a row's conditions (DecorNeed)?</summary>
            bool Needs(DecorRow r)
            {
                bool home = _res.Kind is BuildingKind.Residential or BuildingKind.Apartment;
                if (r.Need.HasFlag(DecorNeed.Home) && !home) return false;
                if (r.Need.HasFlag(DecorNeed.Business) && _res.Kind is not (BuildingKind.Commercial or BuildingKind.Industrial)) return false;
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

            List<WallPlan> _walls;
            /// <summary>The walls of this floor that the floor plan stands (full storey high).</summary>
            List<WallPlan> RoomWalls() => _walls ??= _res.Walls.Where(w => w.Kind == SurfaceKind.Wall && (int)MathF.Round(w.Y / Storey) == _room.Floor
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
            /// DecorEnd at each corner, every opening in the wall behind it at that height (and DecorOpenGap either side), the
            /// stairs' reach (StairWallReach, as ByStairs), and -- with `row` -- the pieces already hung on it (DecorGap;
            /// DecorRunGap between fixtures) and whatever stands within DecorClear of it taller than its bottom less
            /// DecorUnder (along the wall, DecorRunGap for a fixture).</summary>
            List<(float A, float B)> Stretches(Face f, float depth, float bottom, float top, DecorRow row)
            {
                var cuts = new List<(float, float)>();
                float floorY = _room.Floor * Storey;
                foreach (var w in RoomWalls())
                {
                    var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                    // along this face, and standing right behind it
                    if (MathF.Abs(f.Nx != 0 ? dz : dx) < 0.99f) continue;
                    var s = f.Span(WallBox(w));
                    if (s.D1 < -0.12f || s.D1 > 0.12f || s.A1 < f.Lo || s.A0 > f.Hi) continue;
                    foreach (var o in w.Openings)
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

            /// <summary>A wall's footprint, as FloorPlan lays it on the grid.</summary>
            static (float X0, float Z0, float X1, float Z1) WallBox(WallPlan w)
            {
                var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                float dx = wc, dz = -ws, t = w.Thickness * 0.5f;
                float ex = w.X + dx * w.Length, ez = w.Z + dz * w.Length;
                float tx = MathF.Abs(dz) * t, tz = MathF.Abs(dx) * t;
                return (MathF.Min(w.X, ex) - tx, MathF.Min(w.Z, ez) - tz, MathF.Max(w.X, ex) + tx, MathF.Max(w.Z, ez) + tz);
            }

            /// <summary>How much of this room's wall art could hang on: its faces less their windows, doorways and stairs.</summary>
            float FreeWall() => AllFaces().Sum(f => Stretches(f, 0.2f, 1.2f, 3.4f, null).Sum(q => q.B - q.A));

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
            /// the décor's own per stretch for which wall -- never a second focal piece on a wall, nor more than DecorFill of
            /// a wall's length hung. Each piece goes in through Hang (no window or door in front of it, nothing hung there
            /// already). False when it fits nowhere.</summary>
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
                        var m = at == null ? null : Hang(names[k], new[] { at.Value }, null, row.Height, rng, DecorReach);
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
