using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>
    /// BASEMENTS (strawberry, 2026-10-15: "anything can get a basement, they can be specificially asked for by the building
    /// plot, or specifically rejected, if neither specified, its just a random chance for one. a mixture of different types
    /// and styles of basement. basements can spawn with an outdoor hatch, or with an indoor staircase, usually in its own
    /// self-contained room with just the staircase").
    ///
    /// A basement is storey -1 under the whole ground floor, the editor's own negative storey (EditorBuildings.ActiveFloor:
    /// FloorY = floor x StoreyHeight): its floor at -Storey, its outside walls the ground floor's outline carried down a
    /// storey -- concrete through, inside and out, as a foundation wall is, but in a finished one, which is the wall colour
    /// through -- on footings the rest of the way down. A full storey, not a lower one: every per-storey rule in the furnisher and
    /// the editor reads a storey's walls and floor at a multiple of Storey, so its ceiling is any storey's (3.75 m clear).
    /// No windows: the ground is 0.25 m under the ground floor, so the wall shows no more than that above it.
    ///
    /// Its STYLE (BasementStyle): bare concrete with odds and ends and the laundry in the open; a store, racks and pallets; a
    /// finished rec room; a workshop -- the last three with a utility room for the laundry in a corner. Its WAY DOWN
    /// (BasementAccess): a flight from a room of its own -- three cells by one, carved from the end of a ground-floor room
    /// you walk through or store things in, holding nothing but the flight and the landing at its top -- or a HATCH, a
    /// flight down from the ground beside the house in a walled well along an outside wall, under a cellar door thrown open,
    /// to a door in the basement wall. Both are flights by the generator's own rules: the storey's run (StairDefaultRun), a
    /// full tile clear at the foot, the landing at the top.
    ///
    /// All of it decided on streams of its own, so a building that gets none is the building it was.
    /// </summary>
    public static partial class BuildingGen
    {
        /// <summary>Percent of buildings with a basement when the plot says neither way.</summary>
        public const int BasementChance = 35;
        /// <summary>Percent of basements reached by a flight inside (the rest: a hatch outside).</summary>
        public const int BasementStairsChance = 65;
        /// <summary>The ground outside, building-local: the editor stands a building's floor GroundClearance over it.</summary>
        public const float Grade = -0.25f;
        /// <summary>The hatch's well: clear across (a flight and StairHoleMargin either side), its retaining walls' thickness,
        /// the landing at the foot of its flight (the door into the basement opens off it), how far its walls and cover stand
        /// over the ground, and the opening at the top of the flight the cellar door covers -- as far down the flight as a
        /// 2 m player needs before the cover over the rest clears his head (2.5 m under it at its edge).</summary>
        public const float WellClear = StairWidth + 2 * WallOpenings.StairHoleMargin, WellWall = 0.3f, WellLanding = Cell,
                           WellLip = 0.1f, HatchOpen = 4.0f, HatchLeaf = 0.12f;
        /// <summary>A door's frame stands this far past its opening on the wall's faces (WallSurface.TrimProfile): a door kept
        /// off a corner by less puts its frame into the wall across it.</summary>
        const float DoorFrame = 0.2f;

        /// <summary>The rooms a stair room may be carved from, in order: circulation and stores first, then rooms you walk
        /// through -- never a kitchen, a bathroom, a bedroom, a closet or a garage (its car), each of which needs all of itself.</summary>
        static readonly RoomKind[] StairHosts =
        {
            RoomKind.Hall, RoomKind.Storage, RoomKind.Workshop, RoomKind.Warehouse, RoomKind.DiningRoom, RoomKind.LivingRoom,
            RoomKind.Office, RoomKind.Shop, RoomKind.BreakRoom,
        };

        /// <summary>The flight down from a stair room: its entry cell (the landing) and the way it runs from there to the far
        /// end, where its foot is, one storey down.</summary>
        internal struct StairStrip { public int X, Z, DX, DZ; }

        /// <summary>A hatch's well: against the outside wall on lattice line Line (AlongX: of constant z), whose outer face is
        /// on the side E (+1: +z or +x) -- its clear stretch along that wall from A0 for WellLanding + a flight's run, the
        /// flight climbing toward Up (+1: increasing along) from the landing at the other end.</summary>
        internal struct HatchSpot { public bool AlongX; public int Line, E, Up; public float A0; }

        static BasementStyle StyleOf(BuildingKind kind, int roll) => kind switch
        {
            // a house's: all four; a business's or a block's: a store, or bare
            BuildingKind.Residential => roll < 30 ? BasementStyle.Unfinished : roll < 55 ? BasementStyle.Storage : roll < 85 ? BasementStyle.Finished : BasementStyle.Workshop,
            BuildingKind.Industrial => roll < 40 ? BasementStyle.Unfinished : roll < 70 ? BasementStyle.Storage : BasementStyle.Workshop,
            _ => roll < 40 ? BasementStyle.Unfinished : BasementStyle.Storage,
        };

        /// <summary>Whether this building gets a basement, of what style and reached how -- and if so its rooms on floor -1,
        /// and its way down: a stair room carved out of the ground floor (with its door), or a hatch's well against an outside
        /// wall. After the ground floor's doors are decided and before anything is built (BuildOut builds it: BuildBasement).
        /// The rooms' labels are drawn again, as the stair room took cells from the room it was carved from.</summary>
        static void PlanBasement(BuildingGenResult res, BuildingGenSpec spec, List<Grid> grids)
        {
            var brng = new Rng(unchecked(spec.Seed * 7919 + 0x4241));
            // (every draw made whatever the spec asks, so asking for one thing does not move the others)
            bool roll = brng.Chance(BasementChance);
            int styleRoll = brng.Int(100);
            bool stairsRoll = brng.Chance(BasementStairsChance);
            if (spec.Basement == BasementChoice.Forbid || (spec.Basement == BasementChoice.Auto && !roll)) return;
            var style = spec.BasementStyle != BasementStyle.Auto ? spec.BasementStyle : StyleOf(res.Kind, styleRoll);
            var want = spec.BasementAccess != BasementAccess.Auto ? spec.BasementAccess : stairsRoll ? BasementAccess.Stairs : BasementAccess.Hatch;
            var ground = grids[0];
            StairStrip? strip = null; HatchSpot? hatch = null;
            if (want == BasementAccess.Stairs) strip = CarveStairRoom(res, ground, brng);
            if (strip == null) hatch = PickHatch(res, ground, brng);
            if (strip == null && hatch == null && want == BasementAccess.Hatch) strip = CarveStairRoom(res, ground, brng);
            if (strip == null && hatch == null)
            {
                if (spec.Basement == BasementChoice.Require) res.Notes.Add("a basement was asked for, but no way down fitted: no room could give up a stair room, and no outside wall had the room for a hatch");
                return;
            }
            if (want == BasementAccess.Stairs && strip == null) res.Notes.Add("no room on the ground floor could give up a stair room: the basement is reached by a hatch");
            if (want == BasementAccess.Hatch && hatch == null) res.Notes.Add("no outside wall had the room for a hatch: the basement is reached by a stair room");
            res.Basement = style;
            res.BasementAccess = strip != null ? BasementAccess.Stairs : BasementAccess.Hatch;
            res.BasementFlight = strip;
            res.Hatch = hatch;
            LayBasement(res, ground, brng);
            // what has to come out of the ground: the basement to its walls' outer faces, and the well
            float he = Ext * 0.5f;
            res.Dig.Clear();
            if (res.Shape == FootprintShape.Rect) res.Dig.Add((-he, -he, res.CellsX * Cell + he, res.CellsZ * Cell + he));
            else
            {
                var (wx0, zj, wx1, wz1) = res.Wing;
                res.Dig.Add((-he, -he, res.CellsX * Cell + he, zj * Cell + he));
                res.Dig.Add((wx0 * Cell - he, zj * Cell + he, wx1 * Cell + he, wz1 * Cell + he));
            }
            if (hatch is HatchSpot h)
            {
                res.HatchWell = WellRect(h);
                res.Dig.Add(res.HatchWell);
                res.HatchLeafRect = LeafRect(h);
            }
            res.Rooms.Clear();
            foreach (var r in res.Layout)
                res.Rooms.Add(new RoomDesignation(r.Kind, (r.X0 + r.X1) * 0.5f * Cell, (r.Z0 + r.Z1) * 0.5f * Cell, r.Floor));
        }

        /// <summary>A stair room: three cells by one in the end of a row at the edge of a host room (StairHosts, a plain
        /// rectangle on the ground floor, two cells deep at the least), clear of the stair bay and of every door the host
        /// has; the flight down from its landing at one end to its foot at the other, where the ground floor goes on past
        /// it -- the basement there is the tile clear at the foot. Its door is in its long side at the landing, off the host,
        /// as wide as the landing allows (the hole down starts past it). Up against an outside wall where it can be, so the
        /// flight runs down beside the foundation; the host keeps half its cells and, if it needs daylight, an outside wall.</summary>
        static StairStrip? CarveStairRoom(BuildingGenResult res, Grid ground, Rng rng)
        {
            var bay = res.Bay;
            bool InBay(int x, int z) => res.Floors > 1 && bay.X1 > bay.X0 && x >= bay.X0 && x < bay.X1 && z >= bay.Z0 && z < bay.Z1;
            var cands = new List<(GenRoom Host, (int X, int Z)[] Cells, StairStrip S, bool AlongX, int Row, bool LowRow, int Rank, bool Outer)>();
            foreach (var h in res.Layout.Where(g => g.Floor == 0 && g.More.Count == 0 && (res.Kind != BuildingKind.Apartment || g.Unit < 0)))
            {
                int rank = Array.IndexOf(StairHosts, h.Kind);
                if (rank < 0) continue;
                foreach (bool alongX in new[] { true, false })
                {
                    int len = alongX ? h.X1 - h.X0 : h.Z1 - h.Z0, dep = alongX ? h.Z1 - h.Z0 : h.X1 - h.X0;
                    if (len < 3 || dep < 2) continue;
                    foreach (bool lowRow in new[] { true, false })
                    foreach (bool lowEnd in new[] { true, false })
                    foreach (bool fromLow in new[] { true, false })
                    {
                        int row = alongX ? (lowRow ? h.Z0 : h.Z1 - 1) : (lowRow ? h.X0 : h.X1 - 1);
                        int s0 = alongX ? (lowEnd ? h.X0 : h.X1 - 3) : (lowEnd ? h.Z0 : h.Z1 - 3);
                        (int X, int Z) C(int i) => alongX ? (s0 + i, row) : (row, s0 + i);
                        var cells = new[] { C(0), C(1), C(2) };
                        if (cells.Any(c => InBay(c.X, c.Z))) continue;
                        // the foot's tile, a storey down: the ground floor goes on past the far end
                        var past = C(fromLow ? 3 : -1);
                        if (ground.At(past.X, past.Z) == null) continue;
                        // no door of the host's on the strip's edges: they would open into the stair room
                        bool Edged(GenDoor d)
                        {
                            float a0 = d.At - d.Width * 0.5f, a1 = d.At + d.Width * 0.5f;
                            foreach (var (x, z) in cells)
                            {
                                if (d.AlongX && (d.Line == z || d.Line == z + 1) && a1 > x * Cell + 0.01f && a0 < (x + 1) * Cell - 0.01f) return true;
                                if (!d.AlongX && (d.Line == x || d.Line == x + 1) && a1 > z * Cell + 0.01f && a0 < (z + 1) * Cell - 0.01f) return true;
                            }
                            return false;
                        }
                        if (res.Doors.Any(d => d.Floor == 0 && (d.A == h || d.B == h) && Edged(d))) continue;
                        var left = new List<(int X, int Z)>();
                        for (int x = h.X0; x < h.X1; x++) for (int z = h.Z0; z < h.Z1; z++) if (!cells.Contains((x, z))) left.Add((x, z));
                        if (left.Count < Math.Max(3, h.Cells / 2)) continue;
                        bool Outside((int X, int Z) c) => ground.At(c.X - 1, c.Z) == null || ground.At(c.X + 1, c.Z) == null || ground.At(c.X, c.Z - 1) == null || ground.At(c.X, c.Z + 1) == null;
                        if (Habitable(h.Kind) && !left.Any(Outside)) continue;
                        int across = lowRow ? -1 : 1;
                        bool outer = alongX ? cells.All(c => ground.At(c.X, c.Z + across) == null) : cells.All(c => ground.At(c.X + across, c.Z) == null);
                        var c0 = C(fromLow ? 0 : 2);
                        int dir = fromLow ? 1 : -1;
                        cands.Add((h, cells, new StairStrip { X = c0.X, Z = c0.Z, DX = alongX ? dir : 0, DZ = alongX ? 0 : dir }, alongX, row, lowRow, rank, outer));
                    }
                }
            }
            if (cands.Count == 0) return null;
            var pick = rng.Keyed(cands).OrderBy(c => c.Item.Rank).ThenBy(c => c.Item.Outer ? 0 : 1).ThenBy(c => c.Key).First().Item;
            var host = pick.Host;
            var strip = pick.S;
            // the host keeps the rest of its cells: its biggest rectangle, and the rest of the strip's row beside it
            var keep = new HashSet<(int, int)>();
            for (int x = host.X0; x < host.X1; x++) for (int z = host.Z0; z < host.Z1; z++) if (!pick.Cells.Contains((x, z))) keep.Add((x, z));
            var rects = RectsOf(keep);
            (host.X0, host.Z0, host.X1, host.Z1) = rects[0];
            host.More.Clear(); host.More.AddRange(rects.Skip(1));
            var room = new GenRoom
            {
                Kind = RoomKind.Stairwell, Floor = 0, Unit = host.Unit,
                X0 = pick.Cells.Min(c => c.X), Z0 = pick.Cells.Min(c => c.Z), X1 = pick.Cells.Max(c => c.X) + 1, Z1 = pick.Cells.Max(c => c.Z) + 1,
            };
            ground.Paint(host); ground.Paint(room);
            res.Layout.Add(room);
            res.BasementStairs = room;
            // its door: in the long side it shares with the host, at the landing -- off the wall at the landing's end by the
            // door's frame, and stopping short of the hole the flight goes down through
            int dirAlong = strip.DX + strip.DZ;
            int entry = (pick.AlongX ? strip.X : strip.Z) + (dirAlong > 0 ? 0 : 1);   // the lattice line at the landing's end
            var before = pick.AlongX ? (strip.X - strip.DX, strip.Z) : (strip.X, strip.Z - strip.DZ);
            float h0 = ground.At(before.Item1, before.Item2) == null ? Ext * 0.5f : Inner * 0.5f;
            float holeAt = StairStripTop - WallOpenings.StairHoleMargin;   // how far along from `entry` the hole starts
            float d0 = h0 + DoorFrame, d1 = holeAt - 0.05f;
            float mid = entry * Cell + dirAlong * (d0 + d1) * 0.5f;
            res.Doors.Add(new GenDoor
            {
                Floor = 0, A = host, B = room, AlongX = pick.AlongX, Line = pick.LowRow ? pick.Row + 1 : pick.Row, At = mid, Width = d1 - d0,
            });
            return strip;
        }

        /// <summary>How far along a stair room from the end its landing is at the flight's top end stands: the flight's foot
        /// is StairHoleMargin off the far wall's face (a partition: the floor goes on past it), so the hole the editor cuts
        /// over it stops at that face; the flight runs back from there.</summary>
        const float StairStripFoot = 3 * Cell - Inner * 0.5f - WallOpenings.StairHoleMargin;
        static float StairStripTop => StairStripFoot - WallOpenings.StairDefaultRun(Storey);

        /// <summary>Where a hatch's well goes: an outside wall's stretch of the ground floor long enough for it, clear of the
        /// building's corners by a metre, of the porch any outside door may get (as long as AddPorches makes one, either side
        /// of the door) and of a garage's or warehouse's walls (its vehicle door and the lane to it); its outer faces clear of
        /// the building. Off the front door's wall where it can be, then off the back door's.</summary>
        static HatchSpot? PickHatch(BuildingGenResult res, Grid ground, Rng rng)
        {
            float he = Ext * 0.5f, alongNeed = WellLanding + WallOpenings.StairDefaultRun(Storey + Grade);
            var outsideDoors = res.Doors.Where(d => d.Floor == 0 && d.B == null).ToList();
            var cands = new List<(HatchSpot H, int Pref)>();
            foreach (var run in Runs(ground).Where(r => r.B == null))
            {
                if (run.A.Kind is RoomKind.Garage or RoomKind.Warehouse) continue;
                var face = ground.Out(run.AlongX, run.Line, run.S0);
                int e = face is Sides.North or Sides.East ? 1 : -1;
                int pref = outsideDoors.Any(d => d.Role == DoorRole.Front && d.AlongX == run.AlongX && d.Line == run.Line) ? 2
                         : outsideDoors.Any(d => d.AlongX == run.AlongX && d.Line == run.Line) ? 1 : 0;
                float lo = run.S0 * Cell + 1f + WellWall, hi = run.S1 * Cell - 1f - WellWall - alongNeed;
                for (float a0 = lo; a0 <= hi + 1e-3f; a0 += 0.5f)
                    foreach (int up in new[] { 1, -1 })
                    {
                        var h = new HatchSpot { AlongX = run.AlongX, Line = run.Line, E = e, Up = up, A0 = a0 };
                        float mid = a0 + alongNeed * 0.5f;
                        // a porch is up to four cells, centred on its door
                        if (outsideDoors.Any(d => d.AlongX == run.AlongX && d.Line == run.Line && MathF.Abs(d.At - mid) < 2 * Cell + alongNeed * 0.5f + WellWall + 0.5f)) continue;
                        // the well and the cellar door thrown open beside it, clear of the building
                        var well = WellRect(h); var leaf = LeafRect(h);
                        bool Clear((float X0, float Z0, float X1, float Z1) q) => !Enumerable.Range(0, res.CellsX).Any(x => Enumerable.Range(0, res.CellsZ).Any(z =>
                            ground.At(x, z) != null && q.X0 < (x + 1) * Cell + he + 0.3f && q.X1 > x * Cell - he - 0.3f && q.Z0 < (z + 1) * Cell + he + 0.3f && q.Z1 > z * Cell - he - 0.3f
                            // (but for the wall it stands against: the well's inner edge is that wall's outer face)
                            && !(run.AlongX ? z == (e > 0 ? run.Line - 1 : run.Line) : x == (e > 0 ? run.Line - 1 : run.Line))));
                        if (!Clear(well) || !Clear(leaf)) continue;
                        cands.Add((h, pref));
                    }
            }
            if (cands.Count == 0) return null;
            return rng.Keyed(cands).OrderBy(c => c.Item.Pref).ThenBy(c => c.Key).First().Item.H;
        }

        /// <summary>A point of a well's frame -- `along` its wall line, `out` from the wall's outer face -- in plan metres.</summary>
        static (float X, float Z) WellAt(HatchSpot h, float along, float outFrom)
        {
            float across = h.Line * Cell + h.E * (Ext * 0.5f + outFrom);
            return h.AlongX ? (along, across) : (across, along);
        }
        static (float X0, float Z0, float X1, float Z1) Box((float X, float Z) a, (float X, float Z) b)
            => (MathF.Min(a.X, b.X), MathF.Min(a.Z, b.Z), MathF.Max(a.X, b.X), MathF.Max(a.Z, b.Z));
        static float WellLength => WellLanding + WallOpenings.StairDefaultRun(Storey + Grade);
        /// <summary>A well to its retaining walls' outer faces, from the house wall's outer face.</summary>
        static (float X0, float Z0, float X1, float Z1) WellRect(HatchSpot h)
            => Box(WellAt(h, h.A0 - WellWall, 0f), WellAt(h, h.A0 + WellLength + WellWall, WellClear + WellWall));
        /// <summary>The cellar door, thrown open: hinged on the well's outer wall over the top of the flight, lying on the
        /// ground beyond it.</summary>
        static (float X0, float Z0, float X1, float Z1) LeafRect(HatchSpot h)
        {
            float top = h.Up > 0 ? h.A0 + WellLength : h.A0, open = top - h.Up * HatchOpen;
            return Box(WellAt(h, top, WellClear + WellWall), WellAt(h, open, WellClear + WellWall + WellClear));
        }

        /// <summary>The basement's rooms on floor -1, under every cell of the ground floor: one big room -- a bare basement's
        /// or a store's (Storage), a rec room (LivingRoom), a workshop -- and, but in a bare one, a utility room for the
        /// laundry two cells by two in a corner, as far from the way in as it can be. The big room holds the way in: the cells
        /// under the stair room and its foot's tile, or the cells inside the hatch's door. Then the doors: the utility room's,
        /// in the middle of a side it shares with the big room, and the hatch's, in the outside wall at the well's landing.</summary>
        static void LayBasement(BuildingGenResult res, Grid ground, Rng rng)
        {
            var cells = new HashSet<(int X, int Z)>();
            for (int x = 0; x < res.CellsX; x++) for (int z = 0; z < res.CellsZ; z++) if (ground.At(x, z) != null) cells.Add((x, z));
            // the cells the way in needs in the big room, and where it comes in
            var way = new HashSet<(int X, int Z)>();
            (float X, float Z) comeIn;
            if (res.BasementFlight is StairStrip s)
            {
                for (int i = -1; i <= 3; i++) way.Add((s.X + s.DX * i, s.Z + s.DZ * i));
                comeIn = ((s.X + s.DX * 3 + 0.5f) * Cell, (s.Z + s.DZ * 3 + 0.5f) * Cell);
            }
            else
            {
                var h = res.Hatch.Value;
                float door = HatchDoorAt(h);
                int inRow = h.E > 0 ? h.Line - 1 : h.Line;
                for (int c = (int)MathF.Floor((door - 2.5f) / Cell); c * Cell < door + 2.5f; c++)
                    foreach (int k in new[] { 0, -h.E })
                        way.Add(h.AlongX ? (c, inRow + k) : (inRow + k, c));
                comeIn = h.AlongX ? (door, (inRow + 0.5f) * Cell) : ((inRow + 0.5f) * Cell, door);
            }
            way.IntersectWith(cells);
            bool bare = res.Basement == BasementStyle.Unfinished;
            HashSet<(int X, int Z)> util = null;
            if (!bare)
            {
                var opts = new List<(HashSet<(int X, int Z)> Block, int Outer, float Far)>();
                foreach (var (x0, z0) in cells)
                {
                    var block = new HashSet<(int X, int Z)> { (x0, z0), (x0 + 1, z0), (x0, z0 + 1), (x0 + 1, z0 + 1) };
                    if (!block.IsSubsetOf(cells) || block.Overlaps(way)) continue;
                    var rest = new HashSet<(int X, int Z)>(cells); rest.ExceptWith(block);
                    if (rest.Count < 4 || !Connected(rest)) continue;
                    // a corner: outside on two of its sides
                    int outer = (block.All(c => !cells.Contains((c.X - 1, c.Z)) || block.Contains((c.X - 1, c.Z))) ? 1 : 0)
                              + (block.All(c => !cells.Contains((c.X + 1, c.Z)) || block.Contains((c.X + 1, c.Z))) ? 1 : 0)
                              + (block.All(c => !cells.Contains((c.X, c.Z - 1)) || block.Contains((c.X, c.Z - 1))) ? 1 : 0)
                              + (block.All(c => !cells.Contains((c.X, c.Z + 1)) || block.Contains((c.X, c.Z + 1))) ? 1 : 0);
                    if (outer < 1) continue;
                    // its door's side must be the big room's whole length: a side that is neither outside nor all big room is no use
                    if (UtilityDoor(block, cells) == null) continue;
                    float cx = (x0 + 1) * Cell, cz = (z0 + 1) * Cell;
                    opts.Add((block, outer, MathF.Sqrt((cx - comeIn.X) * (cx - comeIn.X) + (cz - comeIn.Z) * (cz - comeIn.Z))));
                }
                // a corner, far from the way in (to the nearest metre, the rest seeded)
                var best = rng.Keyed(opts).OrderByDescending(o => o.Item.Outer).ThenByDescending(o => MathF.Round(o.Item.Far)).ThenBy(o => o.Key).Select(o => o.Item.Block).FirstOrDefault();
                if (best != null && res.Hatch is HatchSpot hs)
                {
                    // and not meeting the hatch's wall within reach of its door: a partition's end in the door's frame
                    float door = HatchDoorAt(hs);
                    bool Meets(HashSet<(int X, int Z)> b) => b.Any(c => hs.AlongX ? (hs.E > 0 ? c.Z == hs.Line - 1 : c.Z == hs.Line) && MathF.Min(MathF.Abs(c.X * Cell - door), MathF.Abs((c.X + 1) * Cell - door)) < 2.5f
                                                                                   : (hs.E > 0 ? c.X == hs.Line - 1 : c.X == hs.Line) && MathF.Min(MathF.Abs(c.Z * Cell - door), MathF.Abs((c.Z + 1) * Cell - door)) < 2.5f);
                    best = rng.Keyed(opts.Where(o => !Meets(o.Block))).OrderByDescending(o => o.Item.Outer).ThenByDescending(o => MathF.Round(o.Item.Far)).ThenBy(o => o.Key).Select(o => o.Item.Block).FirstOrDefault();
                }
                util = best;
            }
            var mainCells = new HashSet<(int X, int Z)>(cells);
            if (util != null) mainCells.ExceptWith(util);
            var mainKind = res.Basement switch { BasementStyle.Finished => RoomKind.LivingRoom, BasementStyle.Workshop => RoomKind.Workshop, _ => RoomKind.Storage };
            var main = RoomOf(mainKind, mainCells);
            res.Layout.Add(main);
            if (util != null)
            {
                var u = RoomOf(RoomKind.Storage, util);
                res.Layout.Add(u);
                res.BasementUtility = u;
                var (alongX, line, at) = UtilityDoor(util, cells).Value;
                res.Doors.Add(new GenDoor { Floor = -1, A = main, B = u, AlongX = alongX, Line = line, At = at });
            }
            if (res.Hatch is HatchSpot hh)
                res.Doors.Add(new GenDoor { Floor = -1, A = main, B = null, AlongX = hh.AlongX, Line = hh.Line, At = HatchDoorAt(hh) });
            GenRoom RoomOf(RoomKind k, HashSet<(int X, int Z)> cs)
            {
                var rects = RectsOf(cs);
                var g = new GenRoom { Kind = k, Floor = -1, X0 = rects[0].X0, Z0 = rects[0].Z0, X1 = rects[0].X1, Z1 = rects[0].Z1 };
                g.More.AddRange(rects.Skip(1));
                return g;
            }
        }

        /// <summary>Where a utility room's door goes: the middle of the first of its sides (north, east, south, west) that is
        /// the big room's along its whole length -- as a lattice line and metres along it -- or null.</summary>
        static (bool AlongX, int Line, float At)? UtilityDoor(HashSet<(int X, int Z)> block, HashSet<(int X, int Z)> cells)
        {
            int x0 = block.Min(c => c.X), x1 = block.Max(c => c.X) + 1, z0 = block.Min(c => c.Z), z1 = block.Max(c => c.Z) + 1;
            bool Big(int x, int z) => cells.Contains((x, z)) && !block.Contains((x, z));
            if (Enumerable.Range(x0, x1 - x0).All(x => Big(x, z1))) return (true, z1, (x0 + x1) * 0.5f * Cell);
            if (Enumerable.Range(z0, z1 - z0).All(z => Big(x1, z))) return (false, x1, (z0 + z1) * 0.5f * Cell);
            if (Enumerable.Range(x0, x1 - x0).All(x => Big(x, z0 - 1))) return (true, z0, (x0 + x1) * 0.5f * Cell);
            if (Enumerable.Range(z0, z1 - z0).All(z => Big(x0 - 1, z))) return (false, x0, (z0 + z1) * 0.5f * Cell);
            return null;
        }

        /// <summary>The hatch's door in the basement wall: the middle of the well's landing, metres along the wall's line.</summary>
        static float HatchDoorAt(HatchSpot h) => h.Up > 0 ? h.A0 + WellLanding * 0.5f : h.A0 + WellLength - WellLanding * 0.5f;

        static bool Connected(HashSet<(int X, int Z)> cs)
        {
            if (cs.Count == 0) return false;
            var seen = new HashSet<(int X, int Z)>();
            var todo = new Stack<(int X, int Z)>();
            todo.Push(cs.First());
            while (todo.Count > 0)
            {
                var c = todo.Pop();
                if (!cs.Contains(c) || !seen.Add(c)) continue;
                todo.Push((c.X + 1, c.Z)); todo.Push((c.X - 1, c.Z)); todo.Push((c.X, c.Z + 1)); todo.Push((c.X, c.Z - 1));
            }
            return seen.Count == cs.Count;
        }

        /// <summary>A set of cells as rectangles, biggest first: each the largest that fits in what is left (ties to the
        /// lowest z, then x), so a room's first rectangle -- where its label goes -- is the bulk of it.</summary>
        static List<(int X0, int Z0, int X1, int Z1)> RectsOf(HashSet<(int X, int Z)> cells)
        {
            var left = new HashSet<(int X, int Z)>(cells);
            var outp = new List<(int X0, int Z0, int X1, int Z1)>();
            while (left.Count > 0)
            {
                (int X0, int Z0, int X1, int Z1) best = default; int bestArea = 0;
                foreach (var (x0, z0) in left.OrderBy(c => c.Z).ThenBy(c => c.X))
                {
                    int maxX = x0;
                    while (left.Contains((maxX, z0))) maxX++;
                    for (int x1 = maxX; x1 > x0; x1--)
                    {
                        int z1 = z0;
                        while (Enumerable.Range(x0, x1 - x0).All(x => left.Contains((x, z1)))) z1++;
                        int area = (x1 - x0) * (z1 - z0);
                        if (area > bestArea) { bestArea = area; best = (x0, z0, x1, z1); }
                    }
                }
                outp.Add(best);
                for (int x = best.X0; x < best.X1; x++) for (int z = best.Z0; z < best.Z1; z++) left.Remove((x, z));
            }
            return outp;
        }

        /// <summary>Build the basement planned: its outside walls (the ground floor's outline a storey down, concrete through
        /// but in a finished one) on footings the rest of the way down; its partitions and their doors, and
        /// the hatch's door; its floor; the flight down from the stair room; and the hatch -- the well's walls, its floor, its
        /// flight, the cover over the landing and the cellar door thrown open. Every wall laid joined, as BuildWalls' are
        /// once the corner solve has run, so the editor's own solve finds nothing to grow. Each pass of BuildOut.</summary>
        static void BuildBasement(BuildingGenResult res)
        {
            if (!res.HasBasement) return;
            int W = res.CellsX, D = res.CellsZ;
            var gb = new Grid(W, D, 0, W);
            foreach (var r in res.Layout.Where(r => r.Floor == -1)) gb.Paint(r);
            float y = -Storey, he = Ext * 0.5f;
            bool finished = res.Basement == BasementStyle.Finished;
            var tex = FloorTexels.TryGetValue(res.Palette ?? "", out var ft) ? ft : (Floor: -1, Tile: -1, Concrete: -1);
            int bare = finished ? -1 : tex.Concrete;
            var walls = new List<WallPlan>();
            var outs = Outline(gb, y);
            // (one colour through, not a back face of its own: a wall's ends and top wear its FRONT face's colour
            // (WallSurface), and at an L's inside corner an outside wall's end faces into the basement -- strawberry saw
            // it, "the concrete color misses a face on the ends of walls". So concrete through, outside too, as a
            // foundation wall is: the strip that shows over the ground and the hatch well's house side with it. A
            // finished one is the wall colour through, its rooms plastered.)
            foreach (var o in outs) { o.W.Texel = bare; walls.Add(o.W); }
            bool Partition(float t) => t > 0f && t < Ext;
            var inner = new List<(bool AlongX, int Line, int S0, int S1, WallPlan W)>();
            for (int z = 1; z < D; z++)
                for (int x = 0; x < W;)
                {
                    if (!Partition(gb.H(x, z))) { x++; continue; }
                    int s0 = x;
                    while (x < W && Partition(gb.H(x, z))) x++;
                    inner.Add((true, z, s0, x, Wall(s0 * Cell, z * Cell, x * Cell, z * Cell, y, Inner)));
                }
            for (int x = 1; x < W; x++)
                for (int z = 0; z < D;)
                {
                    if (!Partition(gb.V(x, z))) { z++; continue; }
                    int s0 = z;
                    while (z < D && Partition(gb.V(x, z))) z++;
                    inner.Add((false, x, s0, z, Wall(x * Cell, s0 * Cell, x * Cell, z * Cell, y, Inner)));
                }
            foreach (var q in inner) { q.W.Texel = bare; walls.Add(q.W); }
            float U(WallPlan w, float at) => w.Yaw switch { 0f => at - w.X, 180f => w.X - at, 270f => at - w.Z, _ => w.Z - at };
            foreach (var d in res.Doors.Where(d => d.Floor == -1))
            {
                if (d.B == null)
                {
                    var w = outs.First(o => o.AlongX == d.AlongX && o.Line == d.Line && o.A0 <= d.At + 0.01f && d.At <= o.A1 + 0.01f).W;
                    w.Openings.Add(Make(ArchDoor, U(w, d.At), d.Width));
                    continue;
                }
                var iw = inner.First(q => q.AlongX == d.AlongX && q.Line == d.Line && q.S0 * Cell <= d.At && d.At <= q.S1 * Cell);
                iw.W.Openings.Add(Make(ArchDoor, d.At - iw.S0 * Cell, d.Width));
            }
            SolveCorners(walls);
            res.Walls.AddRange(walls);
            // footings under the outside walls, the rest of the way down
            foreach (var w in outs.Select(o => o.W))
                res.Walls.Add(new WallPlan { X = w.X, Y = -WallOpenings.FoundationDepth, Z = w.Z, Yaw = w.Yaw, Length = w.Length,
                                             Height = WallOpenings.FoundationDepth - Storey, Thickness = w.Thickness, Kind = SurfaceKind.Foundation, Texel = bare });
            // the floor: concrete, or a finished one's the building's floor colour -- and a cover over each room that is not
            int floorTexel = finished ? tex.Floor : tex.Concrete;
            if (res.Shape != FootprintShape.Rect)
                foreach (var sl in Slabs(res, y, SurfaceKind.Floor)) { sl.Texel = floorTexel; res.Walls.Add(sl); }
            else
            {
                const float si = SlabInset;
                var slab = SlabPlan(-he + si, W * Cell + he - si, -he + si, D * Cell + he - si, y, SurfaceKind.Floor);
                slab.Texel = floorTexel;
                res.Walls.Add(slab);
            }
            foreach (var room in res.Layout.Where(r => r.Floor == -1))
            {
                int tx = FloorTexelFor(res.Palette, room.Kind);
                if (tx < 0 || tx == floorTexel) continue;
                foreach (var q in room.Rects)
                {
                    var cover = SlabPlan(q.X0 * Cell, q.X1 * Cell, q.Z0 * Cell, q.Z1 * Cell, y + 0.02f, SurfaceKind.Floor);
                    cover.Thickness = 0.04f; cover.Y = y; cover.Texel = tx;
                    res.Walls.Add(cover);
                }
            }
            // the flight down from the stair room: from the landing at its top, at the entry end, to its foot a storey down
            // StairStripFoot along the strip, as wide as the room less StairHoleMargin either side (the hole the editor cuts
            // over it then stops at the room's walls)
            if (res.BasementFlight is StairStrip s)
            {
                bool alongX = s.DX != 0;
                int dir = s.DX + s.DZ;
                int entry = (alongX ? s.X : s.Z) + (dir > 0 ? 0 : 1), row = alongX ? s.Z : s.X;
                var ground = new Grid(W, D, 0, W);
                foreach (var r in res.Layout.Where(r => r.Floor == 0)) ground.Paint(r);
                // (each side's wall at its thickest along the room: an outside wall's stretch of it, or a partition's)
                float Half(int k) => Enumerable.Range(0, 3).Max(i => (alongX ? ground.At(s.X + s.DX * i, row + k) : ground.At(row + k, s.Z + s.DZ * i)) == null ? Ext * 0.5f : Inner * 0.5f);
                float lowFace = row * Cell + Half(-1), highFace = (row + 1) * Cell - Half(1);
                float m = WallOpenings.StairHoleMargin, width = highFace - lowFace - 2 * m;
                float foot = entry * Cell + dir * StairStripFoot;
                res.Stairs.Add(Flight(alongX, -dir, foot, lowFace + m, width, y, WallOpenings.StairDefaultRun(Storey), 0f));
            }
            if (res.Hatch is HatchSpot h) BuildHatch(res, h, tex);
        }

        /// <summary>A flight climbing along X (or Z) toward `climb` (+1: increasing), its foot at `foot` along that axis and its
        /// width from `side` across it (the lower edge): the StairPlan, whose origin is the foot corner its width runs right
        /// from (yaw 270 climbs +X, 90 -X, 180 +Z, 0 -Z).</summary>
        static StairPlan Flight(bool alongX, int climb, float foot, float side, float width, float y, float run, float rise)
        {
            if (alongX)
                return climb > 0 ? new StairPlan { X = foot, Z = side, Yaw = 270f, Y = y, Run = run, Width = width, Rise = rise, Floor = -1 }
                                 : new StairPlan { X = foot, Z = side + width, Yaw = 90f, Y = y, Run = run, Width = width, Rise = rise, Floor = -1 };
            return climb > 0 ? new StairPlan { X = side + width, Z = foot, Yaw = 180f, Y = y, Run = run, Width = width, Rise = rise, Floor = -1 }
                             : new StairPlan { X = side, Z = foot, Yaw = 0f, Y = y, Run = run, Width = width, Rise = rise, Floor = -1 };
        }

        /// <summary>The hatch: a well against the house's outside wall, its retaining walls concrete from the basement's floor
        /// to WellLip over the ground -- the long one parallel to the house, an end wall at each end from the house wall's
        /// middle out -- a floor at the basement's, the flight up from the landing at one end to the ground at the other, a
        /// cover over the landing and the flight's foot (the house wall to the well's outer face, flush with the walls' tops),
        /// and over the flight's top HatchOpen, the opening, with the cellar door that covers it thrown open onto the ground
        /// beyond the long wall. The door into the basement is the main room's (LayBasement), at the landing.</summary>
        static void BuildHatch(BuildingGenResult res, HatchSpot h, (int Floor, int Tile, int Concrete) tex)
        {
            float y = -Storey, top = Grade + WellLip, wallH = top - y, len = WellLength, run = WallOpenings.StairDefaultRun(Storey + Grade);
            const float hair = 0.001f;   // past the face the editor's corner solve would grow it to, never short of it
            WallPlan Retaining((float X, float Z) a, (float X, float Z) b)
            {
                var w = Wall(a.X, a.Z, b.X, b.Z, y, WellWall);
                w.Height = wallH; w.Texel = tex.Concrete;
                return w;
            }
            // the long wall, out to the end walls' outer faces; the end walls, from the house wall's middle to its outer face
            res.Walls.Add(Retaining(WellAt(h, h.A0 - WellWall - hair, WellClear + WellWall * 0.5f), WellAt(h, h.A0 + len + WellWall + hair, WellClear + WellWall * 0.5f)));
            foreach (float at in new[] { h.A0 - WellWall * 0.5f, h.A0 + len + WellWall * 0.5f })
                res.Walls.Add(Retaining(WellAt(h, at, -Ext * 0.5f), WellAt(h, at, WellClear + WellWall + hair)));
            // its floor, the basement's
            var (fx0, fz0, fx1, fz1) = Box(WellAt(h, h.A0, 0f), WellAt(h, h.A0 + len, WellClear));
            var floor = SlabPlan(fx0, fx1, fz0, fz1, y, SurfaceKind.Floor);
            floor.Texel = tex.Concrete;
            res.Walls.Add(floor);
            // the flight: from the landing up to the ground at the open end
            float foot = h.Up > 0 ? h.A0 + WellLanding : h.A0 + len - WellLanding;
            var (sx, sz) = WellAt(h, 0f, WallOpenings.StairHoleMargin);
            var (ex, ez) = WellAt(h, 0f, WallOpenings.StairHoleMargin + StairWidth);
            float side = h.AlongX ? MathF.Min(sz, ez) : MathF.Min(sx, ex);
            var flight = Flight(h.AlongX, h.Up, foot, side, StairWidth, y, run, Storey + Grade);
            flight.Concrete = true;
            res.Stairs.Add(flight);
            // the cover, from the landing's end to the opening
            float openAt = (h.Up > 0 ? h.A0 + len : h.A0) - h.Up * HatchOpen;
            var (cx0, cz0, cx1, cz1) = Box(WellAt(h, h.Up > 0 ? h.A0 - WellWall : h.A0 + len + WellWall, 0f), WellAt(h, openAt, WellClear + WellWall));
            var cover = SlabPlan(cx0, cx1, cz0, cz1, top, SurfaceKind.Floor);
            cover.Thickness = WellWall; cover.Y = top - WellWall * 0.5f; cover.Texel = tex.Concrete;
            res.Walls.Add(cover);
            // the cellar door, thrown open onto the ground
            var (lx0, lz0, lx1, lz1) = LeafRect(h);
            var leaf = SlabPlan(lx0, lx1, lz0, lz1, Grade + HatchLeaf, SurfaceKind.Floor);
            leaf.Thickness = HatchLeaf; leaf.Y = Grade + HatchLeaf * 0.5f; leaf.Texel = tex.Floor;
            res.Walls.Add(leaf);
        }
    }
}
