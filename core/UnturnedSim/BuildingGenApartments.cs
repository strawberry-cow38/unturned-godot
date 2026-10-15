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
    /// the stairwell is the lobby: the front door opens into it, or into the corridor -- never into a flat. A block of one
    /// storey has no stairs, and a lobby of corridor in the stairwell's place.
    ///
    /// A FLAT is a run of columns across its row, its LIVING ROOM in the middle and everything else opening off it: a bedroom
    /// and the bathroom on one side, the kitchen on the other -- and in a bigger flat a second bedroom (or a study) as well,
    /// on the outer rows of a column whose two inner rows are the living room's, so the room beyond it reaches the living room
    /// too. The bathroom keeps the two rows by the corridor; the cells behind it, against the outside wall, are the bedroom's,
    /// which reaches the living room round the back of the bathroom -- so no bathroom is bigger than MaxCells and every room
    /// that needs daylight has an outside wall. A flat is entered from the corridor straight into its living room, or into a
    /// VESTIBULE, two cells along the corridor and one deep, that opens into the living room -- where the living room has
    /// the two cells to spare without growing past LivingAcross (a one-bedroom flat's kitchen goes over its inner rows for
    /// them, in a row deep enough). Exactly one door joins a flat to
    /// shared space; no door joins two flats; its bedrooms, bathroom and study open off its living room or vestibule
    /// (BuildingGen.MayJoin and OffInAFlat, which Connect keeps).
    ///
    /// A flat is as wide as its rooms at their designed widths and at most FlatSlack cells more: a row its flats cannot fill
    /// takes more flats, or shared space -- an alcove of corridor -- never one flat stretched to the row (the coordinator, on
    /// e85d091f7's 51 m flat and its 51 m entry hall). FitPlot makes Width x Depth a cap: the plan is searched for one that
    /// fits, rooms compact before anything else gives, and the closest is built, said so, when none does.
    /// From the rooms on it is the house pipeline: Connect, BuildWalls, the corner solve, Furnish, the turn.
    /// </summary>
    public static partial class BuildingGen
    {
        /// <summary>Generate's door planner (its Connect), handed to the flats so they are doored by the same rules.</summary>
        delegate bool DoorPlanner(Grid g, List<GenRoom> rs, int f, GenRoom root, List<GenDoor> doors, (int X0, int Z0, int X1, int Z1) bay,
                                  Sides frontAsked, ref bool turn);

        /// <summary>How a flat is entered: straight into its living room, or into a vestibule that opens into it.</summary>
        enum FlatWay { Living, Vestibule }

        /// <summary>How a flat's column is laid: a room the row's whole depth; the bathroom, the two rows by the corridor (the
        /// rest of its column is the bedroom beside it); or a room on the outer rows over two inner rows of living room.</summary>
        enum ColShape { Full, Bath, Over }

        sealed class FlatCol
        {
            public RoomKind Kind;
            public ColShape Shape;
            public int W, Min, Cap, Depth, Strip;
            public double Target;
            public int Cells => W * Depth + Strip;
            /// <summary>How far past its kind's size it is: rooms grow by share, the furthest under first.</summary>
            public double Share => Cells / Target;
        }

        sealed class Flat
        {
            public FlatWay Way;
            public int Ud;
            public readonly List<FlatCol> Cols = new List<FlatCol>();
            public int Rooms => Cols.Count;
            public int Width => Cols.Sum(c => c.W);
            public int MinW => Cols.Sum(c => c.Min);
            /// <summary>The widest it may grow: its designed width and FlatSlack more, as far as its rooms' caps allow (Reach).</summary>
            public int MaxW;
            public FlatCol Living => Cols.First(c => c.Kind == RoomKind.LivingRoom);
            /// <summary>The living room's cells: its column, the inner rows under every Over column, less a vestibule.</summary>
            public int LivingCells => Living.W * Ud + Cols.Where(c => c.Shape == ColShape.Over).Sum(c => 2 * c.W) - (Way == FlatWay.Vestibule ? 2 : 0);
        }

        /// <summary>The fewest flats a floor has: one either side of the corridor, or two along it.</summary>
        public const int MinFlatsPerFloor = 2;
        /// <summary>The rooms of the smallest flat (a bedroom, a bathroom, a living room, a kitchen).</summary>
        public const int FlatRooms = 4;
        /// <summary>A flat's rows, corridor to outside wall: at the least (its bathroom's two and two behind them, deep enough
        /// for the bedroom's door round it), and at the most (deeper, its bedroom and living room pass their caps).</summary>
        public const int FlatMinDepth = 4, FlatMaxDepth = 5;
        /// <summary>How much wider than its rooms at their designed widths a flat may be.</summary>
        public const int FlatSlack = 2;
        /// <summary>A flat's entry vestibule: two cells along the corridor, one deep.</summary>
        public const int VestibuleCells = 2;
        /// <summary>The widest a flat's living room grows across its row: a couch backed to one of its long walls sees a flat
        /// television on the other only across three cells (8.5 m clear, and <see cref="WallTvReach"/> is 8), so one wider gets a
        /// CRT on a table every time. (Measured over the sweep's 689 flats: living rooms four and five cells across hung 10
        /// sets in 310; three across, 121 in 379.)</summary>
        public const int LivingAcross = 3;

        /// <summary>A flat for a row `ud` cells deep, each column at its least width: the bedroom, the bathroom, the living room,
        /// then the kitchen -- and `second` (a second bedroom or a study; Unassigned for none) with whichever of the two is
        /// laid over the living room (`kitchenOver`; with no second room, the kitchen goes over it when `oneOver`, which is
        /// where a vestibule needs it). West to east, or east to west when `mirror`. Null when the row is too shallow for it.
        /// `compact` (FitPlot's) lets each room down to two cells wide and toward its least size.</summary>
        static Flat MakeFlat(FlatWay way, RoomKind second, int ud, bool diner, bool mirror, bool kitchenOver, int compact, bool oneOver = false)
        {
            int side = compact > 0 ? 2 : 3;
            double Scale(double cells) => compact <= 0 ? cells : compact >= 3 ? 0 : Math.Ceiling(cells * (compact == 1 ? 0.7 : 0.5));
            if (ud < FlatMinDepth) return null;
            oneOver &= second == RoomKind.Unassigned;
            if ((second != RoomKind.Unassigned || oneOver) && ud - 2 < side) return null;   // the Over room's rows
            var cols = new List<FlatCol>
            {
                new FlatCol { Kind = RoomKind.Bedroom, Shape = ColShape.Full },
                new FlatCol { Kind = RoomKind.Bathroom, Shape = ColShape.Bath },
                new FlatCol { Kind = RoomKind.LivingRoom, Shape = ColShape.Full },
            };
            if (second == RoomKind.Unassigned) cols.Add(new FlatCol { Kind = RoomKind.Kitchen, Shape = oneOver ? ColShape.Over : ColShape.Full });
            else if (kitchenOver) { cols.Add(new FlatCol { Kind = RoomKind.Kitchen, Shape = ColShape.Over }); cols.Add(new FlatCol { Kind = second, Shape = ColShape.Full }); }
            else { cols.Add(new FlatCol { Kind = second, Shape = ColShape.Over }); cols.Add(new FlatCol { Kind = RoomKind.Kitchen, Shape = ColShape.Full }); }
            var flat = new Flat { Way = way, Ud = ud };
            foreach (var c in cols)
            {
                c.Depth = c.Shape == ColShape.Over ? ud - 2 : c.Shape == ColShape.Bath ? 2 : ud;
                if (c.Shape == ColShape.Bath) { c.W = c.Min = c.Cap = 2; c.Target = 1; continue; }
                // the bedroom beside the bathroom has the cells behind it: two wide at the corridor is room enough for it
                c.Strip = c.Kind == RoomKind.Bedroom && c == cols[0] ? 2 * (ud - 2) : 0;
                c.Target = c.Kind == RoomKind.Kitchen && diner ? DinerCells : TraitOf(c.Kind, BuildingKind.Apartment).Cells;
                int least = c == cols[0] ? 2 : side;
                c.Min = Math.Max(least, (int)Math.Ceiling((Scale(c.Target) - c.Strip) / c.Depth));
                c.Cap = Math.Max(c.Min, (CapCells(c.Kind, BuildingKind.Apartment) - c.Strip) / c.Depth);
                c.W = c.Min;
            }
            // a living room no smaller than its kitchen ("make sure rooms are scaled proportionately"), a vestibule taken out --
            // and no wider than LivingAcross, so a vestibule goes only where the living room has cells to spare for it (the
            // rows under an Over column); without them the flat's door opens straight into the living room
            var kitchen = cols.First(c => c.Kind == RoomKind.Kitchen);
            var living = cols.First(c => c.Kind == RoomKind.LivingRoom);
            flat.Cols.AddRange(cols);
            if (flat.Way == FlatWay.Vestibule && flat.LivingCells < kitchen.Cells)
            {
                // a one-bedroom flat's kitchen goes over the living room's inner rows instead, where the row is deep enough
                if (second == RoomKind.Unassigned && !oneOver && ud - 2 >= side
                    && MakeFlat(way, second, ud, diner, mirror, kitchenOver, compact, oneOver: true) is { Way: FlatWay.Vestibule } over) return over;
                flat.Way = FlatWay.Living;
            }
            living.Cap = Math.Max(living.Min, LivingAcross);
            while (flat.LivingCells < kitchen.Cells) { living.W++; living.Min++; living.Cap = Math.Max(living.Cap, living.W); }
            if (flat.LivingCells > CapCells(RoomKind.LivingRoom, BuildingKind.Apartment) && compact == 0) return null;
            if (mirror) flat.Cols.Reverse();
            flat.MaxW = flat.MinW + Grow(flat, FlatSlack, null);
            foreach (var c in flat.Cols) c.W = c.Min;
            return flat;
        }

        /// <summary>Widen a flat's rooms by up to `cells`, a cell at a time to the one furthest under its kind's size (`rng`
        /// breaks ties; null, the first): each within its cap, the living room within its own -- it has the cells under an
        /// Over column too -- and never smaller than its kitchen. Returns how many it took. (Run on a flat at its least, with
        /// no rng, it is how far the flat CAN grow.)</summary>
        static int Grow(Flat f, int cells, Rng rng)
        {
            var kitchen = f.Cols.First(c => c.Kind == RoomKind.Kitchen);
            int took = 0;
            for (; took < cells; took++)
            {
                FlatCol best = null; double low = double.MaxValue;
                foreach (var c in f.Cols)
                {
                    if (c.Shape == ColShape.Bath || c.W >= c.Cap) continue;
                    c.W++;
                    bool ok = f.LivingCells <= CapCells(RoomKind.LivingRoom, BuildingKind.Apartment) && kitchen.Cells <= f.LivingCells;
                    c.W--;
                    if (!ok) continue;
                    double sh = c.Share + (rng == null ? 0 : rng.Int(1000) * 1e-6);
                    if (sh < low) { low = sh; best = c; }
                }
                if (best == null) break;
                best.W++;
            }
            return took;
        }

        /// <summary>One way of laying a floor of flats, and how far it is from what was asked.</summary>
        sealed class FlatPlan
        {
            public List<Flat> South, North;
            public int StairAt, W, PadS, PadN;
            public double Miss;
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
            // a town plot (FitPlot): its width and depth are caps, not asks
            bool fit = spec.FitPlot && spec.Width > 0 && spec.Depth > 0;
            int capW = fit ? spec.Width : int.MaxValue, capD = fit ? spec.Depth : int.MaxValue;
            int wantD = fit ? 0 : spec.Depth, wantW = fit ? 0 : spec.Width;

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
            // landing column is on its other side. (A plot too tight for them gets compound stairs and no landing column.)
            bool compoundRoll = rng.Chance(40), quarterRoll = rng.Chance(QuarterTurns);
            bool quarterWestRoll = rng.Chance(50);
            bool landingWestRoll = rng.Chance(50);

            // ---- the depth: double-loaded where it allows, single-loaded (the corridor along the north wall) on a narrow
            // block; a row of flats FlatMinDepth to FlatMaxDepth deep. Each row's own depth is drawn here, so a plot's search
            // below does not move the seed's other draws.
            bool singleRoll = rng.Chance(25);
            int dSRoll = rng.Chance(60) ? 5 : 4, dNRoll = rng.Chance(60) ? 5 : 4;
            var depths = new List<(bool Single, int DS, int DN)>();
            if (fit)
            {
                // as deep as the plot allows, both rows if it holds them, one if not; the one that fits best is chosen below
                int room = capD - 1;
                if (room >= 2 * FlatMinDepth) depths.Add((false, Math.Min(FlatMaxDepth, room - FlatMinDepth), Math.Min(FlatMaxDepth, room - Math.Min(FlatMaxDepth, room - FlatMinDepth))));
                if (room >= FlatMinDepth) depths.Add((true, Math.Min(FlatMaxDepth, room), 0));
                // and, for the closest when nothing fits, the least of each
                if (room < 2 * FlatMinDepth) depths.Add((false, FlatMinDepth, FlatMinDepth));
                if (room < FlatMinDepth) depths.Add((true, FlatMinDepth, 0));
            }
            else if (wantD > 0)
            {
                int d = wantD;
                int singleMax = FlatMaxDepth + 1, doubleMin = 2 * FlatMinDepth + 1, doubleMax = 2 * FlatMaxDepth + 1;
                if (d < FlatMinDepth + 1) { res.Notes.Add($"a depth of {wantD} cells is too shallow for a corridor and a row of flats: {FlatMinDepth + 1} at the least"); d = FlatMinDepth + 1; }
                else if (d > singleMax && d < doubleMin) { res.Notes.Add($"a depth of {wantD} cells is too deep for one row of flats and too shallow for two: {doubleMin}"); d = doubleMin; }
                else if (d > doubleMax) { res.Notes.Add($"a depth of {wantD} cells is deeper than two rows of flats go: {doubleMax}"); d = doubleMax; }
                if (d <= singleMax) depths.Add((true, d - 1, 0));
                else
                {
                    int s = Math.Clamp((d - 1) / 2 + ((d - 1) % 2 == 1 && dSRoll == 5 ? 1 : 0), FlatMinDepth, FlatMaxDepth);
                    depths.Add((false, s, d - 1 - s));
                }
            }
            else
            {
                depths.Add(singleRoll ? (true, dSRoll, 0) : (false, dSRoll, dNRoll));
                // (a row four deep has no room for a second bedroom over the living room: five, when one is needed)
                if (dSRoll < FlatMaxDepth || (!singleRoll && dNRoll < FlatMaxDepth)) depths.Add(singleRoll ? (true, FlatMaxDepth, 0) : (false, FlatMaxDepth, FlatMaxDepth));
            }

            // ---- the flats: how many a floor, which have a second room, how each is entered, which side of the corridor
            // -- drawn until both rows fit one length: the north row's is the block's, the south row's that less the
            // stairwell's, each flat its designed width and at most FlatSlack more. What a row's flats cannot fill is shared
            // space, an alcove of corridor (the less the better); the length asked for, when one was; within the plot, on one.
            bool downgraded = false;
            // (`downgrade`: a flat whose second room the row is too shallow for comes out a one-bedroom flat)
            FlatPlan Draw(bool single, int dS, int dN, int SW, int compact, bool downgrade = false)
            {
                FlatPlan best = null;
                for (int attempt = 0; attempt < 300 && (best == null || best.Miss > 0); attempt++)
                {
                    int uLo = least.U, uHi = Math.Max(uLo, Math.Min(perFloor / FlatRooms, wantW > 0 || fit || attempt >= 100 ? perFloor / FlatRooms : uLo + 2));
                    // (one row of flats is the block's whole length: four and a stairwell made it 150 m and more)
                    if (single && wantW == 0 && !fit) uHi = Math.Max(uLo, Math.Min(uHi, 3));
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
                    bool ok = true;
                    for (int i = 0; i < u && ok; i++)
                    {
                        bool inSouth = i < nS;
                        var way = rng.Chance(45) ? FlatWay.Vestibule : FlatWay.Living;
                        bool mirror = rng.Chance(50), kOver = rng.Chance(50);
                        var flat = MakeFlat(way, kinds[i], inSouth ? dS : dN, res.KitchenDiner, mirror, kOver, compact);
                        if (flat == null && downgrade) { flat = MakeFlat(way, RoomKind.Unassigned, inSouth ? dS : dN, res.KitchenDiner, mirror, kOver, compact); downgraded = true; }
                        if (flat == null) ok = false;
                        else (inSouth ? sRow : nRow).Add(flat);
                    }
                    if (!ok) continue;
                    int sAt = rng.Int(sRow.Count + 1);
                    int loS = sRow.Sum(f => f.MinW) + SW, hiS = sRow.Sum(f => f.MaxW) + SW;
                    int loN = single ? 0 : nRow.Sum(f => f.MinW), hiN = single ? int.MaxValue : nRow.Sum(f => f.MaxW);
                    int lo = Math.Max(loS, loN);
                    int w = wantW > 0 ? Math.Max(wantW, lo) : fit ? lo : lo + rng.Int(Math.Min(Math.Max(0, Math.Min(hiS, hiN) - lo), 2) + 1);
                    // what is wrong with it, worst first: over the plot, off the length asked, then shared space padding a row
                    // (an alcove of corridor) -- a flat is never stretched past its slack to fill one
                    int padS = Math.Max(0, w - hiS), padN = Math.Max(0, w - hiN);
                    double miss = 1e6 * Math.Max(0, w - capW) + 1e5 * (wantW > 0 ? w - wantW : 0) + padS + padN;
                    if (best == null || miss < best.Miss) best = new FlatPlan { South = sRow, North = nRow, StairAt = sAt, W = w, PadS = padS, PadN = padN, Miss = miss };
                }
                return best;
            }
            // the least compromise that fits: the seed's own depth and stairs, then rooms compact, then compound stairs and no
            // landing column -- off a plot, the seed's own and nothing else
            FlatPlan plan = null;
            (bool Single, int DS, int DN) depth = depths[0];
            bool compound = multi && compoundRoll, landing = true;
            int compactUsed = 0, plans = 0;
            double planMiss = double.MaxValue;
            foreach (var dp in depths)
                foreach (int compact in fit ? new[] { 0, 1, 2, 3 } : new[] { 0 })
                    foreach (var (cmp, land) in fit ? new[] { (multi && compoundRoll, true), (multi, true), (multi, false) } : new[] { (multi && compoundRoll, true) })
                    {
                        int L0 = multi ? (cmp ? 3 : 4) : 0, sw = multi ? L0 + (land ? 1 : 0) : (land ? 2 : 0);
                        var p = Draw(dp.Single, dp.DS, dp.DN, sw, compact);
                        plans++;
                        if (p == null) continue;
                        int D0 = dp.DS + 1 + dp.DN;
                        double m = p.Miss + 1e6 * Math.Max(0, D0 - capD);
                        if (m < planMiss) { planMiss = m; plan = p; depth = dp; compound = cmp; landing = land; compactUsed = compact; }
                        if (m < 1e5) goto chosen;   // fits (any padding is the plan's own business)
                    }
            chosen:
            if (plan == null)
            {
                // rows too shallow for the second bedrooms asked for (a depth asked of four-deep rows): one-bedroom flats
                plan = Draw(depth.Single, depth.DS, depth.DN, multi ? (compound ? 3 : 4) + 1 : 2, 0, downgrade: true);
                if (downgraded) res.Notes.Add($"a second bedroom or a study needs a row of flats {FlatMaxDepth} deep: the ones asked for dropped");
            }
            if (plan == null) throw new InvalidOperationException($"BuildingGen: no flats drawn for {kind} seed {spec.Seed}");
            bool single = depth.Single;
            int dS = depth.DS, dN = depth.DN, D = dS + 1 + dN;
            int W = plan.W;
            var south = plan.South; var north = plan.North;
            int stairAt = plan.StairAt;
            res.StairShape = !compound ? StairShape.Straight : quarterRoll ? StairShape.Quarter : StairShape.Switchback;
            int k1 = compound || floors <= 2 ? 1 : 2, L = multi ? (compound ? 3 : 4) : 0;
            int bayRows = res.StairShape switch { StairShape.Switchback => 2, StairShape.Quarter => 3, _ => k1 + 1 };
            bool landingWest = res.StairShape == StairShape.Quarter ? !quarterWestRoll : landingWestRoll;
            // (the stairs' own landing, half way up, at the other end of the bay: against the stairwell's end wall, so
            // borne by that and the south wall -- strawberry, "the landing of the stairs is only supported by 1 wall,
            // should require two" -- a switchback's as well as a quarter turn's)
            res.LandingWest = !landingWest;
            int SW = multi ? L + (landing ? 1 : 0) : (landing ? 2 : 0);
            if (fit)
            {
                var gave = new List<string>();
                if (compactUsed > 0) gave.Add($"rooms smaller (compact {compactUsed})");
                if (multi && compound && !compoundRoll) gave.Add("compound stairs");
                if (!landing) gave.Add(multi ? "no landing beside the stairs" : "no lobby");
                if (single && !singleRoll) gave.Add("flats one side of the corridor");
                if (W > capW || D > capD) res.Notes.Add($"could not fit the {spec.Width}x{spec.Depth} plot: {W}x{D} is the closest of {plans} plans");
                else res.Notes.Add($"fitted the {spec.Width}x{spec.Depth} plot as {W}x{D}" + (gave.Count > 0 ? ": " + string.Join(", ", gave) : ""));
            }
            if (wantW > 0 && W != wantW) res.Notes.Add($"{W} cells long, not the {wantW} asked for: its flats need it");
            // (FitPlot's measure: the footprint, nothing built)
            if (spec.PlanOnly) { res.CellsX = W; res.CellsZ = D; res.Plan = single ? "flats, single-loaded" : "flats, double-loaded"; return res; }

            // each row to its length: a cell at a time to the flat (and in it the room) furthest under its share, each flat at
            // most FlatSlack past its designed width (Flat.MaxW); what is left over is returned, for shared space
            int Fill(List<Flat> row, int len)
            {
                int spare = len - row.Sum(f => f.Width);
                while (spare > 0)
                {
                    var open = row.Where(f => f.Width < f.MaxW).ToList();
                    if (open.Count == 0) break;
                    // the flat whose rooms are least over their sizes, then its room the same way
                    var flat = open.OrderBy(f => f.Cols.Where(c => c.Shape != ColShape.Bath).Sum(c => c.Cells) / f.Cols.Where(c => c.Shape != ColShape.Bath).Sum(c => c.Target)).ThenBy(f => row.IndexOf(f)).First();
                    if (Grow(flat, 1, rng) == 0) { flat.MaxW = flat.Width; continue; }
                    spare--;
                }
                return spare;
            }
            int padSouth = Fill(south, W - SW), padNorth = single ? 0 : Fill(north, W);
            // shared space for what is left: alcoves of corridor, four cells at the most, in the gaps between the flats --
            // the south row's by the stairwell (and so part of it, where there are stairs), the north row's opposite it
            int stairX = south.Take(stairAt).Sum(f => f.Width);
            List<(int Gap, int Cells)> Alcoves(List<Flat> row, int pad, int anchor)
            {
                var gaps = Enumerable.Range(0, row.Count + 1).Select(i => (Gap: i, X: row.Take(i).Sum(f => f.Width))).OrderBy(g => Math.Abs(g.X - anchor)).ThenBy(g => g.Gap).ToList();
                var outp = new List<(int Gap, int Cells)>();
                int parts = Math.Max(1, (pad + 3) / 4);
                for (int i = 0; i < parts && pad > 0; i++)
                {
                    int cells = (pad + (parts - i) - 1) / (parts - i);
                    outp.Add((gaps[i % gaps.Count].Gap, cells));
                    pad -= cells;
                }
                return outp;
            }
            // (gaps are counted in flats, the stairwell standing in the south row's gap stairAt)
            var alcS = padSouth > 0 ? Alcoves(south, padSouth, stairX) : new List<(int Gap, int Cells)>();
            var alcN = padNorth > 0 ? Alcoves(north, padNorth, stairX) : new List<(int Gap, int Cells)>();

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
                    int ud = flat.Ud;
                    // rows counted from the corridor, as cells of the plan
                    (int Z0, int Z1) Rows(int v0, int v1) => inSouth ? (dS - v1, dS - v0) : (dS + 1 + v0, dS + 1 + v1);
                    var placed = new List<GenRoom>();
                    int x = x0, bathX = -1, livingAt = flat.Cols.FindIndex(c => c.Kind == RoomKind.LivingRoom);
                    foreach (var c in flat.Cols)
                    {
                        var (z0, z1) = c.Shape switch { ColShape.Bath => Rows(0, 2), ColShape.Over => Rows(2, ud), _ => Rows(0, ud) };
                        if (c.Shape == ColShape.Bath) bathX = x;
                        placed.Add(new GenRoom { Kind = c.Kind, Floor = f, Unit = unit, X0 = x, X1 = x + c.W, Z0 = z0, Z1 = z1 });
                        x += c.W;
                    }
                    var living = placed[livingAt];
                    // the bedroom has the cells behind the bathroom; the living room the inner rows under an Over column
                    var (sz0, sz1) = Rows(2, ud);
                    placed[flat.Cols.FindIndex(c => c.Strip > 0)].More.Add((bathX, sz0, bathX + 2, sz1));
                    for (int i = 0; i < flat.Cols.Count; i++)
                        if (flat.Cols[i].Shape == ColShape.Over) { var (iz0, iz1) = Rows(0, 2); living.More.Add((placed[i].X0, iz0, placed[i].X1, iz1)); }
                    if (flat.Way == FlatWay.Vestibule)
                    {
                        // the vestibule: two cells of the living room's corridor row, on the side away from the bathroom; the
                        // living room keeps the rest of that row, and every row behind it
                        var (vz0, vz1) = Rows(0, 1); var (rz0, rz1) = Rows(1, ud);
                        int lx0 = living.X0, lx1 = living.X1;
                        bool bathWest = bathX < lx0;
                        int vx0 = bathWest ? lx1 - VestibuleCells : lx0;
                        rs.Add(new GenRoom { Kind = RoomKind.Hall, Floor = f, Unit = unit, Entry = true, X0 = vx0, X1 = vx0 + VestibuleCells, Z0 = vz0, Z1 = vz1 });
                        living.Z0 = rz0; living.Z1 = rz1;
                        if (lx1 - lx0 > VestibuleCells) living.More.Add(bathWest ? (lx0, vz0, lx1 - VestibuleCells, vz1) : (lx0 + VestibuleCells, vz0, lx1, vz1));
                    }
                    else living.Entry = true;
                    rs.AddRange(placed);
                    unit++;
                }
                int xs = 0;
                for (int i = 0; i <= south.Count; i++)
                {
                    // (an alcove beside the stairwell is the stairwell's: a lobby at the foot of the stairs, not a dead end
                    // the row's depth long off the corridor)
                    int ax = xs;
                    foreach (var a in alcS.Where(a => a.Gap == i)) { if (!(multi && i == stairAt)) corridor.More.Add((xs, 0, xs + a.Cells, dS)); xs += a.Cells; }
                    if (i == stairAt)
                    {
                        if (multi)
                        {
                            stair = new GenRoom { Kind = RoomKind.Stairwell, Floor = f, X0 = ax, X1 = xs + SW, Z0 = 0, Z1 = dS };
                            rs.Add(stair);
                            // the bay against an end wall of the stairwell, the landing column (if any) and an alcove taken in
                            // on the other side of it: a compound flight's landing stands in the corner of that end wall
                            bx0 = landingWest ? xs + SW - L : ax;
                        }
                        else if (SW > 0) corridor.More.Add((xs, 0, xs + SW, dS));   // one storey: a lobby, part of the corridor
                        xs += SW;
                    }
                    if (i == south.Count) break;
                    Place(south[i], xs, true);
                    xs += south[i].Width;
                }
                int xn = 0;
                for (int i = 0; i <= north.Count; i++)
                {
                    foreach (var a in alcN.Where(a => a.Gap == i)) { corridor.More.Add((xn, dS + 1, xn + a.Cells, D)); xn += a.Cells; }
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
            // a basement, as a house's (its stair room off the shared space, never out of a flat)
            PlanBasement(res, spec, grids);
            // walls, furniture -- no porch unless one was asked for (a canopy over the lobby door, then), and no chimney --
            // and inside a flat, now and then its living room open to its kitchen or its vestibule (OpenPlan: never the
            // corridor, never a party wall)
            BuildOut(res, grids, k1, L, spec, spec.Porches == PorchChoice.Auto ? PorchChoice.None : spec.Porches, chimney: false);
            SolveCorners(res.Walls, RoofShapes.AddParapets(res.Walls).Contains);   // last: parapets, as BuildingGen's (see there)
            // on a plot: what its rooms came out short of, said as the house fit says it
            if (fit && Shortfall(res) is var miss && miss.Count > 0)
            {
                int at = res.Notes.FindLastIndex(n => n.StartsWith("fitted the"));
                if (at >= 0) res.Notes[at] += $", but short of {string.Join(", ", miss.Select(x => x.What).Distinct())}";
            }
            if (turn) TurnHalf(res);
            return res;
        }
    }
}
