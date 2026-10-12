using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>
    /// BLOCKS OF FLATS (strawberry, 2026-10-12: "apartments should function differently from houses, no staircase inside
    /// the apartment, shared corridor and staircases").
    ///
    /// A rectangle, the same plan on every floor: a CORRIDOR (a shared Hall, one cell deep) the length of the block; a row of
    /// flats on its south side and -- where the block is deep enough -- another on its north (double-loaded; one row with the
    /// corridor along the north wall is single-loaded); and in the south row a STAIRWELL (shared, RoomKind.Stairwell) holding
    /// the only stairs, stacked straight up through every floor and opening onto the corridor on each. On the ground floor
    /// the stairwell is the lobby: the front door opens into it, or into the end of the corridor -- never into a flat. A
    /// block of one storey has no stairs, and a lobby of corridor in the stairwell's place.
    ///
    /// A FLAT is a run of columns across its row, each the row's depth: a bedroom (two in a bigger flat, or a bedroom and a
    /// study), a bathroom, the living room and the kitchen. Its bathroom keeps the two rows by the way in and the cells behind
    /// it, against the outside wall, go to the room beside it -- as TuckServiceRooms does for a house, so no bathroom is bigger
    /// than MaxCells and every habitable room has an outside wall. A flat is entered one of two ways:
    ///   LIVING: from the corridor straight into its living room; the bathroom opens off that, and the bedroom reaches it
    ///   round the back of the bathroom (the strip behind it is the living room's);
    ///   HALL: from the corridor into an entry hall of its own, one cell deep along the corridor side, and every room off it.
    /// Either way exactly ONE door joins a flat to shared space, from the corridor into that way in; no door joins two flats;
    /// its bedrooms, bathroom and study open off its living room or hall (BuildingGen.MayJoin and OffInAFlat, which Connect
    /// keeps). From there it is the house pipeline: Connect for the doors, BuildWalls, the corner solve, Furnish, the turn.
    /// </summary>
    public static partial class BuildingGen
    {
        /// <summary>Generate's door planner (its Connect), handed to the flats so they are doored by the same rules.</summary>
        delegate bool DoorPlanner(Grid g, List<GenRoom> rs, int f, GenRoom root, List<GenDoor> doors, (int X0, int Z0, int X1, int Z1) bay,
                                  Sides frontAsked, ref bool turn);

        /// <summary>How a flat is entered: straight into its living room, or into an entry hall of its own.</summary>
        enum FlatWay { Living, Hall }

        /// <summary>One column of a flat: a room as deep as the flat's rows (its bathroom only the two rows by the way in),
        /// how wide it is and may be, and the strip behind the bathroom when it is the room that owns it.</summary>
        sealed class FlatCol
        {
            public RoomKind Kind;
            public int W, Min, Cap, Depth, Strip;
            public double Target;
            /// <summary>How far past its kind's size it is: rooms in a row grow by share, the furthest under first.</summary>
            public double Share => (W * Depth + Strip) / Target;
        }

        sealed class Flat
        {
            public FlatWay Way;
            public readonly List<FlatCol> Cols = new List<FlatCol>();
            public int Rooms => Cols.Count;
            public int Width => Cols.Sum(c => c.W);
            public int MinW => Cols.Sum(c => c.Min);
            public int MaxW => Cols.Sum(c => c.Cap);
        }

        /// <summary>The fewest flats a floor has: one either side of the corridor, or two along it.</summary>
        public const int MinFlatsPerFloor = 2;
        /// <summary>The rooms of the smallest flat (a bedroom, a bathroom, a living room, a kitchen).</summary>
        public const int FlatRooms = 4;
        /// <summary>A flat's rows, corridor to outside wall: at the least (its bathroom's two and a strip behind them two
        /// deep enough for a door into it), and at the most (deeper, a bedroom the row's depth is past its cap).</summary>
        public const int FlatMinDepth = 4, FlatMaxDepth = 6;
        /// <summary>The most shared space that makes up a row its flats cannot fill: past it their rooms grow past their caps.</summary>
        const int MaxPad = 3;

        /// <summary>A flat for a row `ud` cells deep: its columns west to east (east to west when `mirror`), each at its least
        /// width. `second`: Unassigned for one bedroom, else the second bedroom or the study.</summary>
        static Flat MakeFlat(FlatWay way, RoomKind second, int ud, bool diner, bool mirror, bool kitchenLast)
        {
            var ks = new List<RoomKind> { RoomKind.Bedroom, RoomKind.Bathroom, RoomKind.LivingRoom, RoomKind.Kitchen };
            if (second != RoomKind.Unassigned) { if (kitchenLast) ks.Insert(3, second); else ks.Add(second); }
            if (mirror) ks.Reverse();
            int depth = way == FlatWay.Living ? ud : ud - 1;   // under an entry hall, a row less
            int strip = 2 * (depth - 2);                        // the cells behind the bathroom
            // they go to the living room in a flat entered by it (the strip is how its bedroom reaches it), else to the
            // bedroom beside the bathroom while that stays within its cap, else to the living room
            bool toLiving = way == FlatWay.Living || 3 * depth + strip > CapCells(RoomKind.Bedroom, BuildingKind.Apartment);
            int bath = ks.IndexOf(RoomKind.Bathroom);
            var flat = new Flat { Way = way };
            for (int i = 0; i < ks.Count; i++)
            {
                var k = ks[i];
                var c = new FlatCol { Kind = k, Depth = depth };
                if (k == RoomKind.Bathroom) { c.W = c.Min = c.Cap = 2; c.Target = 1; flat.Cols.Add(c); continue; }
                bool owner = Math.Abs(i - bath) == 1 && (k == RoomKind.LivingRoom) == toLiving;
                c.Strip = owner ? strip : 0;
                c.Target = k == RoomKind.LivingRoom ? TraitOf(k, BuildingKind.Apartment).Cells
                         : k == RoomKind.Kitchen && diner ? DinerCells : TraitOf(k, BuildingKind.Apartment).Cells;
                c.Min = Math.Max(3, (int)Math.Ceiling((c.Target - c.Strip) / depth));
                c.Cap = Math.Max(c.Min, (CapCells(k, BuildingKind.Apartment) - c.Strip) / depth);
                c.W = c.Min;
                flat.Cols.Add(c);
            }
            return flat;
        }

        static BuildingGenResult Apartments(BuildingGenSpec spec, BuildingGenResult res, Rng rng, DoorPlanner connect)
        {
            const BuildingKind kind = BuildingKind.Apartment;
            // a flat roof, or a plain gable over the whole block; a kitchen with a table in it, or not, in every flat
            res.Roof = rng.Chance(50) ? RoofKind.Gable : RoofKind.Flat;
            res.RoofPitch = res.Roof == RoofKind.Gable ? GablePitches[rng.Int(GablePitches.Length)] : 0f;
            res.KitchenDiner = rng.Chance(50);
            if (spec.Shape is FootprintShape.L or FootprintShape.T)
                res.Notes.Add($"a block of flats is a rectangle: the {(spec.Shape == FootprintShape.L ? "L" : "T")} asked for came out one");
            if (spec.Chimney == ChimneyChoice.Yes) res.Notes.Add("a chimney was asked for, but a flat has no fireplace");

            // ---- floors: two to four by the seed, within what was asked
            int maxF = Math.Max(1, spec.MaxFloors), minF = Math.Clamp(spec.MinFloors, 1, maxF);
            int floors = Math.Clamp(2 + rng.Int(3), minF, maxF);
            int perFloor = Math.Max(1, spec.MaxRoomsPerFloor);
            if (perFloor < MinFlatsPerFloor * FlatRooms)
            {
                res.Notes.Add($"a block of flats has two flats a floor at the least: {MinFlatsPerFloor * FlatRooms} rooms a floor, not {spec.MaxRoomsPerFloor}");
                perFloor = MinFlatsPerFloor * FlatRooms;
            }
            int Want(RoomKind k) => spec.MinOfKind.TryGetValue(k, out var n) ? Math.Max(0, n) : 0;
            foreach (var kv in spec.MinOfKind.OrderBy(kv => (int)kv.Key))
                if (kv.Value > 0 && !(kv.Key is RoomKind.Unassigned or RoomKind.Hall or RoomKind.Stairwell or RoomKind.Bedroom or RoomKind.Bathroom
                                      or RoomKind.Kitchen or RoomKind.LivingRoom or RoomKind.Office))
                    res.Notes.Add($"a flat has no {kv.Key}: the {kv.Value} asked for dropped");
            // What a floor must hold for the minimums, on `fl` floors: flats (each has one bathroom, kitchen and living
            // room), bedrooms, studies and rooms. A flat with a second room (a bedroom or a study) has five, else four.
            (int Flats, int Beds, int Studies, int Rooms) Needs(int fl)
            {
                int Per(int n) => (n + fl - 1) / fl;
                int flats = Math.Max(MinFlatsPerFloor, Per(Math.Max(Want(RoomKind.Bathroom), Math.Max(Want(RoomKind.Kitchen), Want(RoomKind.LivingRoom)))));
                return (flats, Per(Want(RoomKind.Bedroom)), Per(Want(RoomKind.Office)), Per(spec.MinRooms));
            }
            // the fewest flats a floor of them can have, and how many of those must have a second room; -1 when none fits
            (int U, int Twos) Least((int Flats, int Beds, int Studies, int Rooms) n)
            {
                for (int u = n.Flats; u * FlatRooms <= perFloor; u++)
                {
                    int twos = Math.Max(n.Studies + Math.Max(0, n.Beds - u), n.Rooms - u * FlatRooms);
                    if (twos <= u && u * FlatRooms + Math.Max(0, twos) <= perFloor) return (u, Math.Max(0, twos));
                }
                return (-1, 0);
            }
            // more floors, when the minimums need them and the spec allows them
            while (Least(Needs(floors)).U < 0 && floors < maxF) floors++;
            var needs = Needs(floors);
            if (Least(needs).U < 0)
            {
                // what no number of floors holds: as much as fits, said so
                int uMax = perFloor / FlatRooms, twoMax = Math.Min(uMax, perFloor - uMax * FlatRooms);
                res.Notes.Add($"{spec.MinRooms} rooms ({string.Join(", ", spec.MinOfKind.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}"))}) asked for and at most {uMax} flats of {perFloor} rooms a floor fit on {floors} floors: the rest dropped");
                needs = (Math.Min(needs.Flats, uMax), Math.Min(needs.Beds, uMax + twoMax), Math.Min(needs.Studies, twoMax), Math.Min(needs.Rooms, perFloor));
                while (Least(needs).U < 0 && needs.Studies > 0) needs.Studies--;
                while (Least(needs).U < 0 && needs.Beds > 0) needs.Beds--;
                while (Least(needs).U < 0 && needs.Rooms > 0) needs.Rooms--;
                while (Least(needs).U < 0 && needs.Flats > MinFlatsPerFloor) needs.Flats--;
            }
            var least = Least(needs);
            bool multi = floors > 1;
            res.Floors = floors;

            // ---- the stairs (as a house's: one straight flight, or a switchback or a quarter turn now and then) and the
            // stairwell round them: the bay, and a column of landing beside it -- the lobby on the ground floor, where the
            // front door comes in. A quarter turn's landing is in the corner of the bay with a wall, so the stairwell's
            // landing column is on its other side.
            bool compound = multi && rng.Chance(40);
            res.StairShape = !compound ? StairShape.Straight : rng.Chance(QuarterTurns) ? StairShape.Quarter : StairShape.Switchback;
            res.QuarterWest = rng.Chance(50);
            int k1 = compound || floors <= 2 ? 1 : 2, L = multi ? (compound ? 3 : 4) : 0;
            int bayRows = res.StairShape switch { StairShape.Switchback => 2, StairShape.Quarter => 3, _ => k1 + 1 };
            bool landingWest = res.StairShape == StairShape.Quarter ? !res.QuarterWest : rng.Chance(50);
            // (one storey: no stairs, and a lobby two cells wide in their place)
            int SW = multi ? L + 1 : 2;

            // ---- the depth: double-loaded where it allows, single-loaded (the corridor along the north wall) on a narrow
            // block; a row of flats FlatMinDepth to FlatMaxDepth deep
            int wantD = spec.Depth, wantW = spec.Width;
            bool single;
            int dS, dN;
            if (wantD > 0)
            {
                int d = wantD;
                int singleMax = FlatMaxDepth + 1, doubleMin = 2 * FlatMinDepth + 1, doubleMax = 2 * FlatMaxDepth + 1;
                if (d < FlatMinDepth + 1) { res.Notes.Add($"a depth of {wantD} cells is too shallow for a corridor and a row of flats: {FlatMinDepth + 1} at the least"); d = FlatMinDepth + 1; }
                else if (d > singleMax && d < doubleMin) { res.Notes.Add($"a depth of {wantD} cells is too deep for one row of flats and too shallow for two: {doubleMin}"); d = doubleMin; }
                else if (d > doubleMax) { res.Notes.Add($"a depth of {wantD} cells is deeper than two rows of flats go: {doubleMax}"); d = doubleMax; }
                single = d <= singleMax;
                if (single) { dS = d - 1; dN = 0; }
                else
                {
                    dS = Math.Clamp((d - 1) / 2 + ((d - 1) % 2 == 1 && rng.Chance(50) ? 1 : 0), FlatMinDepth, FlatMaxDepth);
                    dN = d - 1 - dS;
                }
            }
            else
            {
                single = rng.Chance(25);
                dS = FlatMinDepth + rng.Int(2);
                dN = single ? 0 : FlatMinDepth + rng.Int(2);
            }
            int D = dS + 1 + dN;

            // ---- the flats: how many a floor, which have a second room, how each is entered, and which side of the corridor;
            // drawn until both rows fit one length -- the north row's is the block's, the south row's that less the stairwell's
            // -- and the length asked for, when one was. A row its flats cannot fill at their rooms' caps is made up with
            // shared space, a little at the most: the stairwell's landing wider in the south row, an alcove of corridor
            // (opposite the stairs) in the north; failing that, the rooms grow past their caps, said so.
            List<Flat> south = null, north = null;
            int W = 0, stairAt = 0;
            double bestMiss = double.MaxValue;
            for (int attempt = 0; attempt < 300 && bestMiss > (wantW > 0 ? 0 : 5); attempt++)
            {
                int uLo = least.U, uHi = Math.Max(uLo, Math.Min(perFloor / FlatRooms, wantW > 0 || attempt >= 100 ? perFloor / FlatRooms : uLo + 2));
                // (one row of flats is the block's whole length: four and a stairwell made it 150 m and more)
                if (single && wantW == 0) uHi = Math.Max(uLo, Math.Min(uHi, 3));
                int u = uLo + rng.Int(uHi - uLo + 1);
                int twoLo = Math.Max(needs.Studies + Math.Max(0, needs.Beds - u), needs.Rooms - u * FlatRooms), twoHi = Math.Min(u, perFloor - u * FlatRooms);
                if (twoLo > twoHi) continue;
                twoLo = Math.Max(0, twoLo);
                int twos = twoLo + rng.Int(twoHi - twoLo + 1);
                // studies: those asked for, and now and then one more where the bedrooms allow
                int studies = Math.Min(needs.Studies, twos);
                for (int i = studies; i < twos; i++) if (u + twos - (studies + 1) >= needs.Beds && rng.Chance(20)) studies++;
                int nS = single ? u : Math.Max(1, u / 2 + (u % 2 == 1 && rng.Chance(50) ? 1 : 0));
                if (!single && u - nS < 1) nS = u - 1;
                var kinds = Enumerable.Range(0, u).Select(i => i < twos ? (i < studies ? RoomKind.Office : RoomKind.Bedroom) : RoomKind.Unassigned).ToList();
                rng.Shuffle(kinds);
                var sRow = new List<Flat>(); var nRow = new List<Flat>();
                for (int i = 0; i < u; i++)
                {
                    bool inSouth = i < nS;
                    int ud = inSouth ? dS : dN;
                    var second = kinds[i];
                    // a one-bedroom flat entered by its living room, or by a hall; a bigger one by a hall (its living room
                    // has but two sides for the rest to open off)
                    var way = second == RoomKind.Unassigned && ud <= 5 && rng.Chance(55) ? FlatWay.Living : FlatWay.Hall;
                    var flat = MakeFlat(way, second, ud, res.KitchenDiner, rng.Chance(50), rng.Chance(50));
                    (inSouth ? sRow : nRow).Add(flat);
                }
                int sAt = rng.Int(sRow.Count + 1);
                int loS = sRow.Sum(f => f.MinW) + SW, hiS = sRow.Sum(f => f.MaxW) + SW;
                int loN = single ? 0 : nRow.Sum(f => f.MinW), hiN = single ? int.MaxValue : nRow.Sum(f => f.MaxW);
                int lo = Math.Max(loS, loN);
                int w = wantW > 0 ? Math.Max(wantW, lo) : lo + rng.Int(Math.Min(Math.Max(0, Math.Min(hiS, hiN) - lo), 2) + 1);
                // what is wrong with it, worst first: off the length asked, rooms past their caps, shared space padding a row,
                // and then how far its rooms must grow past their least to fill the length (one flat stretched to its caps
                // across a row the other side's two fill was the first draw that fit)
                int padS = Math.Max(0, w - hiS), padN = Math.Max(0, w - hiN);
                // (the growth a flat, in the row that grows its flats most: five cells is a room or two a size up; one flat
                // alone in a row the other side's two fill grew ten, to its caps)
                double growth = Math.Max(Math.Max(0, w - padS - loS) / (double)Math.Max(1, sRow.Count), single ? 0 : Math.Max(0, w - padN - loN) / (double)Math.Max(1, nRow.Count));
                double miss = 100000 * (wantW > 0 ? w - wantW : 0)   // (w is never under it: at least the length asked)
                            + 1000 * (Math.Max(0, padS - MaxPad) + Math.Max(0, padN - MaxPad)) + 10 * (padS + padN) + growth;
                if (miss < bestMiss) { bestMiss = miss; south = sRow; north = nRow; stairAt = sAt; W = w; }
            }
            if (south == null) throw new InvalidOperationException($"BuildingGen: no flats drawn for {kind} seed {spec.Seed}");
            if (wantW > 0 && W != wantW) res.Notes.Add($"{W} cells long, not the {wantW} asked for: its flats need it");
            // each row to its length: a cell at a time to the room furthest under its share, within its cap (or past it,
            // `capped` off), and a kitchen never past its own flat's living room ("make sure rooms are scaled proportionately
            // to what they are": at the same share the draw gave a kitchen the cell); what is left over is returned
            int Grow(List<Flat> row, int len, bool capped)
            {
                int spare = len - row.Sum(f => f.Width);
                var cols = row.SelectMany(f => f.Cols.Select(c => (c, Living: f.Cols.First(l => l.Kind == RoomKind.LivingRoom)))).Where(x => x.c.Kind != RoomKind.Bathroom).ToList();
                static int Cells(FlatCol c) => c.W * c.Depth + c.Strip;
                while (spare > 0)
                {
                    FlatCol best = null; double low = double.MaxValue;
                    foreach (var (c, living) in cols)
                    {
                        if (capped && c.W >= c.Cap) continue;
                        if (c.Kind == RoomKind.Kitchen && Cells(c) + c.Depth > Cells(living)) continue;
                        double sh = c.Share + rng.Int(1000) * 1e-6;
                        if (sh < low) { low = sh; best = c; }
                    }
                    if (best == null) break;
                    best.W++; spare--;
                }
                return spare;
            }
            bool past = false;
            int Fill(List<Flat> row, int len)
            {
                int left = Grow(row, len, true);
                if (left <= MaxPad) return left;
                past = true;
                Grow(row, len - MaxPad, false);
                return MaxPad;
            }
            int padSouth = Fill(south, W - SW), padNorth = single ? 0 : Fill(north, W);
            if (past) res.Notes.Add($"{W} cells long: more than its flats fill, so their rooms came out past their sizes");
            SW += padSouth;
            // the north row's alcove of corridor: between the flats nearest opposite the stairs
            int alcoveAt = 0;
            if (padNorth > 0)
            {
                int sx = south.Take(stairAt).Sum(f => f.Width), best = int.MaxValue;
                for (int i = 0, x = 0; i <= north.Count; x += i < north.Count ? north[i].Width : 0, i++)
                    if (Math.Abs(x - sx) < best) { best = Math.Abs(x - sx); alcoveAt = i; }
            }

            // ---- the rooms, floor by floor: every floor the same plan, its flats numbered on from the floor below's
            int flatsPer = south.Count + north.Count;
            int bx0 = 0;
            var grids = new List<Grid>();
            var doorsAll = new List<GenDoor>();
            var layout = new List<GenRoom>();
            Sides frontAsked = spec.FrontSide;
            bool turn = false;
            for (int f = 0; f < floors; f++)
            {
                var rs = new List<GenRoom>();
                var corridor = new GenRoom { Kind = RoomKind.Hall, Floor = f, X0 = 0, X1 = W, Z0 = dS, Z1 = dS + 1 };
                rs.Add(corridor);
                GenRoom stair = null;
                int unit = f * flatsPer;
                void Place(Flat flat, int x0, bool inSouth)
                {
                    int ud = inSouth ? dS : dN;
                    // rows counted from the corridor, as cells of the plan
                    (int Z0, int Z1) Rows(int v0, int v1) => inSouth ? (dS - v1, dS - v0) : (dS + 1 + v0, dS + 1 + v1);
                    int v = 0;
                    if (flat.Way == FlatWay.Hall)
                    {
                        var (hz0, hz1) = Rows(0, 1);
                        rs.Add(new GenRoom { Kind = RoomKind.Hall, Floor = f, Unit = unit, Entry = true, X0 = x0, X1 = x0 + flat.Width, Z0 = hz0, Z1 = hz1 });
                        v = 1;
                    }
                    int x = x0;
                    var placed = new List<GenRoom>();
                    (int X0, int Z0, int X1, int Z1) strip = default;
                    foreach (var c in flat.Cols)
                    {
                        if (c.Kind == RoomKind.Bathroom)
                        {
                            var (bz0, bz1) = Rows(v, v + 2);
                            var (sz0, sz1) = Rows(v + 2, ud);
                            strip = (x, sz0, x + 2, sz1);
                            placed.Add(new GenRoom { Kind = RoomKind.Bathroom, Floor = f, Unit = unit, X0 = x, X1 = x + 2, Z0 = bz0, Z1 = bz1 });
                        }
                        else
                        {
                            var (z0, z1) = Rows(v, ud);
                            placed.Add(new GenRoom { Kind = c.Kind, Floor = f, Unit = unit, X0 = x, X1 = x + c.W, Z0 = z0, Z1 = z1,
                                                     Entry = flat.Way == FlatWay.Living && c.Kind == RoomKind.LivingRoom });
                        }
                        x += c.W;
                    }
                    for (int i = 0; i < flat.Cols.Count; i++) if (flat.Cols[i].Strip > 0) placed[i].More.Add(strip);
                    rs.AddRange(placed);
                    unit++;
                }
                int xs = 0;
                for (int i = 0; i <= south.Count; i++)
                {
                    if (i == stairAt)
                    {
                        if (multi)
                        {
                            stair = new GenRoom { Kind = RoomKind.Stairwell, Floor = f, X0 = xs, X1 = xs + SW, Z0 = 0, Z1 = dS };
                            rs.Add(stair);
                            bx0 = landingWest ? xs + SW - L : xs;   // the landing column(s) on the side away from the bay
                        }
                        else corridor.More.Add((xs, 0, xs + SW, dS));   // one storey: a lobby, part of the corridor
                        xs += SW;
                    }
                    if (i == south.Count) break;
                    Place(south[i], xs, true);
                    xs += south[i].Width;
                }
                int xn = 0;
                for (int i = 0; i <= north.Count; i++)
                {
                    if (i == alcoveAt && padNorth > 0) { corridor.More.Add((xn, dS + 1, xn + padNorth, D)); xn += padNorth; }
                    if (i == north.Count) break;
                    Place(north[i], xn, false);
                    xn += north[i].Width;
                }
                // (the bathrooms are laid out tucked already: this finds nothing to do, and would keep anything it did in a flat)
                TuckServiceRooms(rs, D, kind, perFloor, (_, _) => false);
                var g = new Grid(W, D, 0, W);
                foreach (var r in rs) g.Paint(r);
                var bay = multi ? (bx0, 0, bx0 + L, bayRows) : (0, 0, 0, 0);
                var doors = new List<GenDoor>();
                bool ok;
                if (f == 0)
                {
                    ok = connect(g, rs, 0, null, doors, bay, frontAsked, ref turn);
                    if (!ok && frontAsked != Sides.None)
                    {
                        res.Notes.Add($"no layout put the front door on the {spec.FrontSide} side: it went where it could");
                        frontAsked = Sides.None; turn = false; doors.Clear();
                        ok = connect(g, rs, 0, null, doors, bay, frontAsked, ref turn);
                    }
                }
                else
                {
                    bool upTurn = false;
                    ok = connect(g, rs, f, stair, doors, bay, Sides.None, ref upTurn);
                }
                if (!ok) throw new InvalidOperationException($"BuildingGen: floor {f} of the flats did not connect for {kind} seed {spec.Seed}: {string.Join(", ", rs)}");
                layout.AddRange(rs);
                doorsAll.AddRange(doors);
                grids.Add(g);
                res.Footprints.Add((0, W));
            }
            res.CellsX = W; res.CellsZ = D;
            if (wantD > 0 && D != wantD) res.Notes.Add($"{D} cells deep, not the {wantD} asked for");
            res.Bay = multi ? (bx0, 0, bx0 + L, bayRows) : (0, 0, 0, 0);
            res.Plan = single ? "flats, single-loaded" : "flats, double-loaded";
            res.Layout.AddRange(layout);
            res.Doors.AddRange(doorsAll);
            foreach (var r in res.Layout)
                res.Rooms.Add(new RoomDesignation(r.Kind, (r.X0 + r.X1) * 0.5f * Cell, (r.Z0 + r.Z1) * 0.5f * Cell, r.Floor));
            // inside a flat, some doorways between two rooms you walk through are left open, as a house's are -- never the
            // door into a flat, nor the stairwell's
            foreach (var d in res.Doors)
                if (d.B != null && d.A.Unit >= 0 && d.A.Unit == d.B.Unit && AccessOf(d.A.Kind) == Access.Through && AccessOf(d.B.Kind) == Access.Through
                    && rng.Chance(OpenDoorway))
                {
                    d.Open = true;
                    d.Wide = rng.Chance(WideDoorway) && !new[] { d.A, d.B }.Any(r => r.Kind == RoomKind.Kitchen && r.Cells < 12);
                }
            res.NoWindowSides = turn ? Turned(spec.NoWindows) : spec.NoWindows;
            BuildWalls(res, grids, k1, L);
            SolveCorners(res.Walls);
            // no porch unless one was asked for (a canopy over the lobby door, then), and no chimney
            AddPorches(res, grids[0], spec.Porches == PorchChoice.Auto ? PorchChoice.None : spec.Porches, spec.Seed);
            Furnish(res, spec.Seed);
            if (turn) TurnHalf(res);
            return res;
        }
    }
}
