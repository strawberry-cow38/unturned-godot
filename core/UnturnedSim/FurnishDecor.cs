using System;
using System.Collections.Generic;
using System.Linq;

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
        /// <summary>Only a room with a door to outside -- the front, the back, a side door, a vehicle door (hooks, keys).</summary>
        OuterDoor = 1 << 3,
        /// <summary>Only in a house or a flat.</summary>
        Home = 1 << 4,
        /// <summary>Only in a business, Commercial or Industrial: the work items (whiteboards, pegboards...).</summary>
        Business = 1 << 5,
    }

    /// <summary>Where on its wall a row's piece hangs.</summary>
    public enum DecorSpot
    {
        /// <summary>The middle of a free stretch of wall; better, centred over one of its Over pieces (a couch, a bed's
        /// head) or just beside one of its Beside pieces, where one is against a wall with room.</summary>
        Wall,
        /// <summary>Only centred over one of its Over pieces, lined up with it: upper cabinets over the counters, a run
        /// of them over a run of counters.</summary>
        Over,
        /// <summary>Within <see cref="BuildingGen.DecorDoorReach"/> of the room's door to outside, or failing one of any
        /// door of the room: hooks, a key rack.</summary>
        Door,
    }

    /// <summary>One wall-décor item: the prop (game/content/objects/{Name}.obj, its extents in FurnitureCatalog.Sizes, its
    /// GUID in guid_mesh.txt), the kinds of room it goes in and what else it needs there, its odds against the room's other
    /// rows (Weight), the most of it one room gets (Max), the height of its origin over the floor (Height: a hung mesh is
    /// centred on its origin up and down, so it spans Height +- its Sizes H), and how many hang side by side as a group
    /// (Group: 2..Group of them; 1 alone). Over and Beside name pieces by prefix ("Couch" is both couches). Tags are the
    /// manifest's (formal, rustic, kid, work), for the record.</summary>
    public sealed record DecorRow(string Name, RoomKind[] Rooms, float Height, int Weight = 10, int Max = 1, DecorNeed Need = DecorNeed.None,
                                  DecorSpot Spot = DecorSpot.Wall, string[] Over = null, string[] Beside = null, int Group = 1, string Tags = "");

    public static partial class BuildingGen
    {
        static RoomKind[] In(params RoomKind[] k) => k;
        // (names by prefix: a couch either size; a bed's head is where a painting goes, a bunk's 2.92 m never clears)
        static readonly string[] Couches = { "Couch" }, DoubleBeds = { "Bed_0" }, SingleBeds = { "Bed_1" };

        /// <summary>The wall décor (VoX: "needs more wall doo dads like posters and art and clocks and coat hooks"), one
        /// row per model; Decorate hangs them. A row's name must not start with a prefix the furnisher or Shortfall reads
        /// a room's furniture by (Bed, Couch, Table, Chair, Shelf, Counter, Fridge, Oven, Tub, Lamp, Television): a hung
        /// piece would pass for the floor piece. The wall clock (Clock) and the flat television (WallTv) hang in their
        /// rooms' own recipes, before this -- the clock stays there: it draws from the Furnisher's shared Rng, so moving it
        /// onto the décor's would move every kitchen's and living room's clock, and every room furnished after them.</summary>
        public static readonly IReadOnlyList<DecorRow> Decor = new DecorRow[]
        {
            // retail: the 3 x 1.5 m pixel-art poster (16x8 texture). Its OBJ is centred in depth too (Y0 -0.1), so its back
            // stands 3 cm off the wall like every piece; 2.55 m up its bottom is at 1.8, over a couch, a washer, a counter
            new("Poster_0", In(RoomKind.BreakRoom, RoomKind.Garage, RoomKind.Workshop, RoomKind.Shop), Height: 2.55f, Weight: 10, Tags: "work"),
            // (Board_0, retail, 3.5 x 2.5 m, is a CHALKBOARD -- a dark green-grey panel recessed in a wood frame with a
            // chalk ledge along its foot -- not a noticeboard; left out. Live, it would be:
            //  new("Board_0", In(RoomKind.Office, RoomKind.BreakRoom), Height: 2.5f, Weight: 4, Need: DecorNeed.Business, Tags: "work"),
            //  with Sizes ["Board_0"] = new(-1.75f, 1.75f, -0.12f, 0.28f, 1.25f).)

            // astraclaw's kit (wall_decor_spec.md), each going live with its line uncommented, its Sizes entry measured off
            // its OBJ, and its GUID in guid_mesh.txt. Heights: each bottom clears what it is meant to hang over by 0.1 m
            // (a couch 1.5, a bed 1.67, a sink 1.83, a microwave on a counter 2.15) at the spec's target size -- recheck
            // Height against the delivered mesh's (spec: +-20%), since a piece hangs Height +- its Sizes H.
            // new("Painting_Landscape_0", In(RoomKind.LivingRoom, RoomKind.DiningRoom, RoomKind.Hall, RoomKind.Bedroom), Height: 2.55f, Weight: 6, Need: DecorNeed.DoubleBed, Over: new[] { "Couch", "Bed_0" }, Tags: "formal"),
            // new("Painting_Landscape_1", In(RoomKind.LivingRoom, RoomKind.DiningRoom, RoomKind.Hall, RoomKind.Bedroom), Height: 2.55f, Weight: 6, Need: DecorNeed.DoubleBed, Over: new[] { "Couch", "Bed_0" }, Tags: "formal"),
            // new("Painting_Landscape_2", In(RoomKind.LivingRoom, RoomKind.DiningRoom, RoomKind.Hall, RoomKind.Bedroom), Height: 2.55f, Weight: 6, Need: DecorNeed.DoubleBed, Over: new[] { "Couch", "Bed_0" }, Tags: "rustic"),
            // new("Painting_Landscape_3", In(RoomKind.LivingRoom, RoomKind.DiningRoom, RoomKind.Hall, RoomKind.Bedroom), Height: 2.55f, Weight: 6, Need: DecorNeed.DoubleBed, Over: new[] { "Couch", "Bed_0" }, Tags: "rustic"),
            // new("Painting_Portrait_0", In(RoomKind.Hall, RoomKind.DiningRoom, RoomKind.Stairwell), Height: 2.55f, Weight: 6, Tags: "formal"),
            // new("Painting_Portrait_1", In(RoomKind.Hall, RoomKind.DiningRoom, RoomKind.Stairwell), Height: 2.55f, Weight: 6, Tags: "formal"),
            // new("Painting_Small_0", In(RoomKind.LivingRoom, RoomKind.DiningRoom, RoomKind.Kitchen, RoomKind.Bedroom, RoomKind.Hall, RoomKind.Office), Height: 2.4f, Weight: 4, Group: 3, Over: new[] { "Couch", "Bed" }),
            // new("Painting_Small_1", In(RoomKind.LivingRoom, RoomKind.DiningRoom, RoomKind.Kitchen, RoomKind.Bedroom, RoomKind.Hall, RoomKind.Office), Height: 2.4f, Weight: 4, Group: 3, Over: new[] { "Couch", "Bed" }),
            // new("Painting_Small_2", In(RoomKind.LivingRoom, RoomKind.DiningRoom, RoomKind.Kitchen, RoomKind.Bedroom, RoomKind.Hall, RoomKind.Office), Height: 2.4f, Weight: 4, Group: 3, Over: new[] { "Couch", "Bed" }),
            // new("Photo_Frames_0", In(RoomKind.LivingRoom, RoomKind.Bedroom, RoomKind.Hall), Height: 2.3f, Weight: 6, Need: DecorNeed.Home, Over: new[] { "Couch" }),
            // new("Poster_Tall_0", In(RoomKind.Bedroom, RoomKind.Garage, RoomKind.BreakRoom), Height: 2.55f, Weight: 6, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }, Tags: "kid"),
            // new("Poster_Tall_1", In(RoomKind.Bedroom, RoomKind.Garage, RoomKind.BreakRoom), Height: 2.55f, Weight: 6, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }, Tags: "kid"),
            // new("Poster_Tall_2", In(RoomKind.Bedroom, RoomKind.Garage, RoomKind.BreakRoom), Height: 2.55f, Weight: 6, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }, Tags: "kid"),
            // new("Poster_Tall_3", In(RoomKind.Bedroom, RoomKind.Garage, RoomKind.BreakRoom), Height: 2.55f, Weight: 6, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }, Tags: "kid"),
            // new("Mirror_0", In(RoomKind.Bathroom, RoomKind.Hall, RoomKind.Bedroom), Height: 2.65f, Weight: 8, Need: DecorNeed.Wardrobe, Over: new[] { "Counter_1" }, Beside: new[] { "Wardrobe" }),
            // new("Coat_Hooks_0", In(RoomKind.Hall, RoomKind.Garage, RoomKind.Kitchen), Height: 2.0f, Weight: 8, Need: DecorNeed.OuterDoor, Spot: DecorSpot.Door),
            // new("Coat_Hooks_1", In(RoomKind.Hall, RoomKind.Garage, RoomKind.Kitchen), Height: 2.0f, Weight: 8, Need: DecorNeed.OuterDoor, Spot: DecorSpot.Door),
            // new("Key_Rack_0", In(RoomKind.Hall), Height: 2.0f, Weight: 6, Need: DecorNeed.OuterDoor | DecorNeed.Home, Spot: DecorSpot.Door),
            // new("Wall_Shelf_0", In(RoomKind.LivingRoom, RoomKind.Bedroom, RoomKind.Office), Height: 2.4f, Weight: 5),
            // new("Wall_Shelf_1", In(RoomKind.LivingRoom, RoomKind.Bedroom, RoomKind.Office), Height: 2.4f, Weight: 5),
            // (upper cabinets: one over each plain counter and the sink, in a run; _1 is the grey set's, Counter_2's)
            // new("Wall_Cabinet_0", In(RoomKind.Kitchen, RoomKind.BreakRoom), Height: 2.8f, Max: 6, Spot: DecorSpot.Over, Over: new[] { "Counter_0", "Counter_1" }),
            // new("Wall_Cabinet_1", In(RoomKind.Kitchen, RoomKind.BreakRoom), Height: 2.8f, Max: 6, Spot: DecorSpot.Over, Over: new[] { "Counter_2" }),
            // new("Utensil_Rail_0", In(RoomKind.Kitchen), Height: 2.4f, Spot: DecorSpot.Over, Over: new[] { "Oven_0", "Counter_0" }),
            // new("Calendar_0", In(RoomKind.Kitchen, RoomKind.Office, RoomKind.BreakRoom), Height: 2.3f, Weight: 5),
            // new("Towel_Rail_0", In(RoomKind.Bathroom), Height: 2.1f, Weight: 8, Beside: new[] { "Tub", "Counter_1" }),
            // new("Medicine_Cabinet_0", In(RoomKind.Bathroom), Height: 2.45f, Weight: 6, Spot: DecorSpot.Over, Over: new[] { "Counter_1" }),
            // new("Whiteboard_0", In(RoomKind.Office, RoomKind.BreakRoom), Height: 2.5f, Weight: 8, Need: DecorNeed.Business, Tags: "work"),
            // new("Corkboard_0", In(RoomKind.Office, RoomKind.BreakRoom, RoomKind.Kitchen), Height: 2.4f, Weight: 6, Tags: "work"),
            // new("Certificate_0", In(RoomKind.Office), Height: 2.4f, Weight: 4, Group: 2, Tags: "formal"),
            // new("Wall_Map_0", In(RoomKind.Office, RoomKind.Hall, RoomKind.Garage), Height: 2.5f, Weight: 4, Tags: "work"),
            // new("Pegboard_0", In(RoomKind.Garage, RoomKind.Workshop, RoomKind.Storage), Height: 2.3f, Weight: 8, Over: new[] { "Table_Metal_0" }, Tags: "work"),
            // new("Antlers_0", In(RoomKind.LivingRoom, RoomKind.Hall), Height: 2.7f, Weight: 1, Need: DecorNeed.Home, Tags: "rustic"),
        };

        /// <summary>Testing only: a table to hang in place of <see cref="Decor"/> on this thread (null: Decor).</summary>
        [ThreadStatic] public static IReadOnlyList<DecorRow> DecorOverride;
        /// <summary>Debugging only: told each piece Decorate hangs and the row it came from (null: nothing is told).</summary>
        [ThreadStatic] public static Action<FurniturePlan, DecorRow> DecorTrace;

        /// <summary>A room gets about one piece of wall art per this much free wall -- wall with no window, door or flight
        /// by it (a big home: DecorEveryBig, more art in a bigger house) -- and at most DecorMost; each piece is left out
        /// one time in DecorBare out of a hundred. Pieces anchored over furniture (DecorSpot.Over: the kitchen's upper
        /// cabinets) are not art and do not count.</summary>
        public const float DecorEvery = 9f, DecorEveryBig = 7f;
        public const int DecorMost = 4, DecorBare = 25;
        /// <summary>A home this big (rooms, or storeys) is a big home.</summary>
        public const int BigHomeRooms = 8, BigHomeFloors = 2;
        /// <summary>Hung pieces keep DecorGap apart (and from the clock and the television), DecorEnd off a corner,
        /// DecorOpenGap off the edge of a window or a doorway; what stands within DecorClear of one is under it, and must be
        /// lower than its bottom by DecorUnder. A group's pieces hang DecorGroupGap apart.</summary>
        public const float DecorGap = 0.3f, DecorEnd = 0.2f, DecorOpenGap = 0.15f, DecorClear = 0.25f, DecorUnder = 0.1f, DecorGroupGap = 0.3f;
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

            /// <summary>Hang the room's wall décor (<see cref="Decor"/>), last of all, from the pieces the room's kind and the
            /// house allow. Its draws are its OWN: an Rng seeded from the building's seed and this room's key (its floor and
            /// first rectangle) -- never the Furnisher's shared one, every draw from which would move every later room's
            /// furniture. It reads the room's furniture and leaves the floor plan as it found it.</summary>
            void Decorate()
            {
                var rows = (DecorOverride ?? Decor).Where(r => r.Rooms.Contains(_room.Kind) && FurnitureCatalog.Sizes.ContainsKey(r.Name) && Needs(r)).ToList();
                if (rows.Count == 0) return;
                var rng = new Rng(unchecked((((((Seed * 486187739 + _room.Floor) * 486187739 + _room.X0) * 486187739 + _room.Z0) * 486187739 + _room.X1) * 486187739 + _room.Z1) ^ 0x0DEC0));
                var count = new Dictionary<DecorRow, int>();
                bool Hung(DecorRow r) { if (HangDecor(r, rng)) { count[r] = count.GetValueOrDefault(r) + 1; return true; } return false; }
                // what hangs over furniture first, over each of its anchors while it goes (not art: no part of the budget)
                foreach (var r in rows.Where(r => r.Spot == DecorSpot.Over))
                    while (count.GetValueOrDefault(r) < r.Max && Hung(r)) { }
                // then the art: a slot per DecorEvery of free wall, each a weighted draw among the rows that still have room,
                // or bare; a row that does not fit anywhere is out for the room, and the slot draws again without it
                bool big = _res.Kind is BuildingKind.Residential or BuildingKind.Apartment
                           && (_res.Floors >= BigHomeFloors || _res.Layout.Count(g => g.Unit == _room.Unit && !g.Circulation) >= BigHomeRooms);
                float free = FreeWall();
                int slots = Math.Min(DecorMost, (int)MathF.Round(free / (big ? DecorEveryBig : DecorEvery)));
                FurnishTrace?.Invoke(System.FormattableString.Invariant($"decor in {_room}: {free:0.0} m of free wall, {slots} slots, rows {string.Join(", ", rows.Select(r => r.Name))}"));
                var art = rows.Where(r => r.Spot != DecorSpot.Over).ToList();
                for (int k = 0; k < slots; k++)
                {
                    if (rng.Int(100) < DecorBare) continue;
                    while (true)
                    {
                        var open = art.Where(r => count.GetValueOrDefault(r) < r.Max && r.Weight > 0).ToList();
                        if (open.Count == 0) return;
                        int roll = rng.Int(open.Sum(r => r.Weight));
                        var pick = open[^1];
                        foreach (var r in open) { if (roll < r.Weight) { pick = r; break; } roll -= r.Weight; }
                        if (Hung(pick)) break;
                        art.Remove(pick);
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
            /// floor: the front, the back, a side or a vehicle door) when `outside`, else every one.</summary>
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
                            if (!outside || !_res.Layout.Any(g => g.Floor == _room.Floor && g.Holds(qx, qz))) l.Add((px, pz));
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

            /// <summary>The stretches of a face a piece `depth` deep, hung from `bottom` to `top`, may span: the run less
            /// DecorEnd at each corner, every opening in the wall behind it at that height (and DecorOpenGap either side), the
            /// stairs' reach (StairWallReach, as ByStairs), and -- with `furnished` -- the pieces already hung on it
            /// (DecorGap) and whatever stands within DecorClear of it taller than its bottom less DecorUnder.</summary>
            List<(float A, float B)> Stretches(Face f, float depth, float bottom, float top, bool furnished)
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
                if (furnished)
                    foreach (var o in _res.Furniture.Where(o => o.Floor == _room.Floor))
                    {
                        var s = f.Span(o.Footprint);
                        if (o.Mounted)
                        {
                            if (s.D1 > -DecorGap && s.D0 < depth + DecorGap) cuts.Add((s.A0 - DecorGap, s.A1 + DecorGap));
                            continue;
                        }
                        // its top over the floor: a piece on another stands on its top; a ceiling light hangs down from the ceiling
                        float h = FurnitureCatalog.Sizes[o.Name].H;
                        bool under = o.Ceiling ? o.Y - floorY - (o.Name.Contains("Pendant") ? PendantDrop : 0.28f) < top + DecorUnder
                                               : o.Y - floorY + h > bottom - DecorUnder;
                        if (under && s.D1 > -DecorClear && s.D0 < depth + DecorClear) cuts.Add((s.A0 - DecorClear, s.A1 + DecorClear));
                    }
                // the run less the cuts
                var free = new List<(float A, float B)> { (f.Lo + DecorEnd, f.Hi - DecorEnd) };
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
            float FreeWall() => AllFaces().Sum(f => Stretches(f, 0.2f, 1.2f, 3.4f, false).Sum(q => q.B - q.A));

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

            /// <summary>Hang one of a row (a group of them, side by side) where its spot says, best first: the middle of the
            /// longest free stretch, centred over an Over piece or beside a Beside piece where the row has them, with a draw of
            /// the décor's own per stretch for which wall. Each piece goes in through Hang (no window or door in front of it,
            /// nothing hung there already). False when it fits nowhere.</summary>
            bool HangDecor(DecorRow row, Rng rng)
            {
                var s = FurnitureCatalog.Sizes[row.Name];
                var (bottom, top) = DecorSpan(row);
                float w = s.X1 - s.X0, depth = s.Y1 - s.Y0;
                int n = row.Group > 1 ? 2 + rng.Int(row.Group - 1) : 1;
                float span = n * w + (n - 1) * DecorGroupGap, half = span * 0.5f;
                bool Is(FurniturePlan m, string[] names) => names != null && m.On == null && !m.Mounted && !m.Ceiling && names.Any(p => m.Name.StartsWith(p));
                var doors = row.Spot == DecorSpot.Door ? DoorsOut(true) is { Count: > 0 } outside ? outside : DoorsOut(false) : null;
                var cands = new List<(Face F, float T, double Score)>();
                foreach (var f in AllFaces())
                {
                    float yaw = YawFacing(f.Nx, f.Nz);
                    // the pieces standing at this face (not at another face the same way round, behind it or across the
                    // room), with their backs to it for Over, and where along it their middles are
                    bool AtThis(FurniturePlan m) { var q = f.Span(m.Footprint); return q.D0 > -0.1f && q.D0 < 0.25f; }
                    var over = _mine.Where(m => Is(m, row.Over) && AtThis(m) && MathF.Abs(((m.Yaw - yaw) % 360f + 540f) % 360f - 180f) < 10f)
                                    .Select(m => { var q = f.Span(m.Footprint); return (q.A0 + q.A1) * 0.5f; }).ToList();
                    var beside = _mine.Where(m => Is(m, row.Beside) && AtThis(m)).Select(m => f.Span(m.Footprint)).ToList();
                    foreach (var (a, b) in Stretches(f, depth, bottom, top, true))
                    {
                        if (b - a < span) continue;
                        float lo = a + half, hi = b - half, mid = (a + b) * 0.5f;
                        // one draw per stretch, in order: which wall it goes on, among the good ones
                        double stretch = 0.2 * (b - a) + rng.Int(1000) * 0.002;
                        var ts = new List<(float T, double Bonus)>();
                        foreach (float t in over) if (t >= lo - 1e-3f && t <= hi + 1e-3f) ts.Add((t, 10));
                        foreach (var q in beside)
                            foreach (float t in new[] { q.A0 - DecorGap - half, q.A1 + DecorGap + half })
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
                        var at = AtFace(row.Name, f, t - half + w * 0.5f + k * (w + DecorGroupGap));
                        var m = at == null ? null : Hang(row.Name, new[] { at.Value }, null, row.Height, rng, DecorReach);
                        if (at == null) offWall++; else if (m == null) refused++;
                        if (m == null) break;
                        placed.Add(m);
                    }
                    if (placed.Count == n)
                    {
                        FurnishTrace?.Invoke(System.FormattableString.Invariant($"decor {row.Name} x{n} in {_room}: hung at ({placed[0].X:0.00},{placed[0].Z:0.00}) yaw {placed[0].Yaw:0}, {cands.Count} candidates, {offWall} off a wall, {refused} refused by Hang"));
                        if (DecorTrace != null) foreach (var m in placed) DecorTrace(m, row);
                        return true;
                    }
                    foreach (var m in placed) Remove(m);
                }
                FurnishTrace?.Invoke(System.FormattableString.Invariant($"decor {row.Name} x{n} in {_room}: nowhere, {cands.Count} candidates, {offWall} off a wall, {refused} refused by Hang"));
                return false;
            }
        }
    }
}
