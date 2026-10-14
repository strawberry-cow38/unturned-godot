using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the wall décor (BuildingGen.Decor and DecorRecipes, FurnishDecor.cs): the same sweep as the rest of
    // BuildingGenTests, read off the generated walls, not the generator's plan of them. Its own numbers are looser than the
    // generator's: it keeps 0.6 m between hung pieces (0.3 in a group, 0.02 between cabinets in a run), this asks 0.25 (and
    // no overlap); it keeps what stands within 0.25 m below a piece's bottom less 0.1, this looks 0.15 m round (along the
    // wall, a fixture over a counter only over its own span, as the generator); it fills half a wall's run at most, this
    // allows half the room's length.
    public partial class BuildingGenTests
    {
        static bool SameWay(FurniturePlan a, FurniturePlan b) => MathF.Abs(((a.Yaw - b.Yaw) % 360f + 540f) % 360f - 180f) < 10f;
        /// <summary>Where a hung piece's back stands, across its wall: the coordinate of its back plane on the axis it faces.</summary>
        static float BackPlane(FurniturePlan m)
        {
            var (fx, fz) = m.Front; float y0 = FurnitureCatalog.Sizes[m.Name].Y0;
            return MathF.Abs(fx) > 0.5f ? m.X + fx * y0 : m.Z + fz * y0;
        }
        static float Width(FurniturePlan m) { var fp = m.Footprint; var (fx, _) = m.Front; return MathF.Abs(fx) > 0.5f ? fp.Z1 - fp.Z0 : fp.X1 - fp.X0; }

        /// <summary>Every rule a hung piece of wall décor breaks, over one building: `rowOf` says which pieces are décor and
        /// from which row (null: not décor). `tally`, by building kind and room kind, counts [pieces, rooms of a kind the
        /// recipes cover, rooms with at least one]; `rows` counts pieces per row.</summary>
        static List<string> DecorViolations(string label, BuildingGenResult res, Func<FurniturePlan, DecorRow> rowOf,
                                            Dictionary<(BuildingKind, RoomKind), int[]> tally, Dictionary<DecorRow, int> rows)
        {
            var bad = new List<string>();
            var decor = res.Furniture.Where(m => rowOf(m) != null).ToList();
            foreach (var g in res.Layout.Where(g => BuildingGen.DecorRecipes.ContainsKey(g.Kind)))
            {
                var t = tally.TryGetValue((res.Kind, g.Kind), out var a) ? a : tally[(res.Kind, g.Kind)] = new int[3];
                int n = decor.Count(m => m.Room == g);
                t[0] += n; t[1]++; if (n > 0) t[2]++;
            }
            bool Fixture(FurniturePlan m) => rowOf(m)?.Spot == DecorSpot.Over;
            bool Focal(FurniturePlan m) => rowOf(m)?.Focal ?? m.Name == "Television_0";
            for (int f = 0; f < res.Floors; f++)
            {
                var walls = WallsOn(res, f).ToList();
                var boxes = walls.Select(Box).ToList();
                bool WallAt(float x, float z) => boxes.Any(b => x > b.X0 + 0.005f && x < b.X1 - 0.005f && z > b.Z0 + 0.005f && z < b.Z1 - 0.005f);
                var stairs = res.Stairs.Where(st => st.Floor == f).Select(Foot)
                    .Concat(res.Walls.Where(w => w.Kind == SurfaceKind.Stairs && FloorOf(w) == f).Select(Plan)).Concat(Holes(res, f)).ToList();
                // a doorway's walk, 1.2 m out either side, and a hung door's leaf's quarter circle, as boxes
                var doorZones = new List<(float X0, float Z0, float X1, float Z1)>();
                foreach (var w in walls)
                {
                    var nrm = Normal(w);
                    foreach (var o in w.Openings.Where(o => o.V < 0.05f))
                    {
                        var p = At(w, o.U); var q = At(w, o.U1); float d = w.Thickness * 0.5f + 1.2f;
                        doorZones.Add((MathF.Min(p.X, q.X) - MathF.Abs(nrm.X) * d, MathF.Min(p.Z, q.Z) - MathF.Abs(nrm.Z) * d,
                                       MathF.Max(p.X, q.X) + MathF.Abs(nrm.X) * d, MathF.Max(p.Z, q.Z) + MathF.Abs(nrm.Z) * d));
                        // (out of the face the door opens to: DoorOpensFront, BuildingGenLayoutTests.LeafBox)
                        if (o.HasDoor && o.Archetype != BuildingGen.ArchGarage) doorZones.AddRange(LeafBoxes(w, o));
                    }
                }
                var here = res.Furniture.Where(m => m.Floor == f).ToList();
                var doorsOut = Doors(res, f).ToList();
                bool Outer(GenRoom g) => doorsOut.Any(d => (d.A == g && (d.B == null || (g.Unit >= 0 && d.B.Unit < 0))) || (d.B == g && (d.A == null || (g.Unit >= 0 && d.A.Unit < 0))));
                float ceiling = Storey - BuildingGen.Slab;
                foreach (var m in decor.Where(m => m.Floor == f))
                {
                    var row = rowOf(m);
                    rows[row] = rows.GetValueOrDefault(row) + 1;
                    string nm = $"{label}: {m.Name} at ({m.X:0.00},{m.Z:0.00}) yaw {m.Yaw:0} in {m.Room}";
                    var s = FurnitureCatalog.Sizes[m.Name];
                    var (fx, fz) = m.Front; var (rx, rz) = m.Right; var fp = m.Footprint;
                    float bottom = m.Y - f * Storey - s.H, top = m.Y - f * Storey + s.H;
                    if (!m.Mounted || m.Ceiling || m.On != null) { bad.Add($"{nm} is not hung on a wall"); continue; }
                    // in a room the row allows and its kind's recipe has a slot for, the house meeting its conditions
                    if (!row.Rooms.Contains(m.Room.Kind)) bad.Add($"{nm}: {row.Name} does not go in a {m.Room.Kind}");
                    if (!BuildingGen.DecorRecipes.TryGetValue(m.Room.Kind, out var recipe) || !recipe.Any(sl => row.Fills(sl.Slot))) bad.Add($"{nm}: no slot of a {m.Room.Kind} takes it");
                    bool home = res.Kind is BuildingKind.Residential or BuildingKind.Apartment;
                    if (row.Need.HasFlag(DecorNeed.Home) && !home) bad.Add($"{nm}: a home's, in a {res.Kind}");
                    if (row.Need.HasFlag(DecorNeed.Business) && res.Kind is not (BuildingKind.Commercial or BuildingKind.Industrial)) bad.Add($"{nm}: a business's, in a {res.Kind}");
                    if (m.Room.Kind == RoomKind.Bedroom)
                    {
                        var bed = here.FirstOrDefault(o => o.Room == m.Room && o.Name.StartsWith("Bed"));
                        if (row.Need.HasFlag(DecorNeed.DoubleBed) && bed?.Name != "Bed_0") bad.Add($"{nm}: a master bedroom's, over {bed?.Name ?? "no bed"}");
                        if (row.Need.HasFlag(DecorNeed.SingleBed) && (bed == null || bed.Name == "Bed_0")) bad.Add($"{nm}: a kid's room's, with {bed?.Name ?? "no bed"}");
                        if (row.Need.HasFlag(DecorNeed.Wardrobe) && !here.Any(o => o.Room == m.Room && o.Name.StartsWith("Wardrobe"))) bad.Add($"{nm}: wants a wardrobe");
                    }
                    if (row.Need.HasFlag(DecorNeed.OuterDoor) && !Outer(m.Room)) bad.Add($"{nm}: wants a door to outside");
                    // where its spot says: over one of its anchors, lined up with it and backed to the same wall; by a door
                    if (row.Spot == DecorSpot.Over && !here.Any(o => o.Room == m.Room && !o.Mounted && !o.Ceiling && o.On == null && row.Over.Any(o.Name.StartsWith)
                                                                     && MathF.Abs((o.X - m.X) * rx + (o.Z - m.Z) * rz) < 0.1f && (o.X - m.X) * fx + (o.Z - m.Z) * fz is > 0f and < 1.5f))
                        bad.Add($"{nm} is over none of {string.Join("/", row.Over)}");
                    if (row.Spot == DecorSpot.Door && !Doors(res, f).Any(d => (d.A == m.Room && MathF.Sqrt((d.PA.X - m.X) * (d.PA.X - m.X) + (d.PA.Z - m.Z) * (d.PA.Z - m.Z)) < BuildingGen.DecorDoorReach + 0.5f)
                                                                         || (d.B == m.Room && MathF.Sqrt((d.PB.X - m.X) * (d.PB.X - m.X) + (d.PB.Z - m.Z) * (d.PB.Z - m.Z)) < BuildingGen.DecorDoorReach + 0.5f)))
                        bad.Add($"{nm} is by no door of its room");
                    // its front in its own room: the room it says it is in is the one the floor in front of it belongs to
                    var ahead = (X: m.X + fx * (s.Y1 + 0.1f), Z: m.Z + fz * (s.Y1 + 0.1f));
                    if (RoomAt(res, f, ahead.X, ahead.Z) != m.Room) bad.Add($"{nm} faces into {RoomAt(res, f, ahead.X, ahead.Z)?.ToString() ?? "no room"}");
                    // its back flat on a wall of that room: along its whole width the wall within 0.15 m behind it, its back not
                    // in the wall, and the room's floor just in front of its back
                    var behindWalls = new HashSet<WallPlan>();
                    int samples = 0, backed = 0, inWall = 0, outRoom = 0;
                    for (float x = s.X0 + 0.05f; x < s.X1 - 0.04f; x += 0.1f)
                    {
                        samples++;
                        float px = m.X + rx * x + fx * s.Y0, pz = m.Z + rz * x + fz * s.Y0;
                        float bx = px - fx * 0.15f, bz = pz - fz * 0.15f;
                        bool hit = false;
                        for (int i = 0; i < walls.Count; i++)
                        {
                            var b = boxes[i];
                            if (bx >= b.X0 && bx <= b.X1 && bz >= b.Z0 && bz <= b.Z1) { hit = true; behindWalls.Add(walls[i]); }
                        }
                        if (hit) backed++;
                        if (WallAt(px, pz)) inWall++;
                        if (!m.Room.Holds(px + fx * 0.05f, pz + fz * 0.05f)) outRoom++;
                    }
                    if (samples == 0 || backed < samples) bad.Add($"{nm}: wall within 0.15 m behind {backed} of {samples} points along its back");
                    if (inWall > 0) bad.Add($"{nm}: its back is in the wall at {inWall} of {samples} points");
                    if (outRoom > 0) bad.Add($"{nm}: its back is off its room's floor at {outRoom} of {samples} points");
                    // not over a window, a doorway, a vent: no opening in the wall behind it across its width and its height
                    foreach (var w in behindWalls)
                    {
                        var d = Dir(w);
                        float s0 = (fp.X0 - w.X) * d.X + (fp.Z0 - w.Z) * d.Z, s1 = (fp.X1 - w.X) * d.X + (fp.Z1 - w.Z) * d.Z;
                        foreach (var o in w.Openings)
                        {
                            float v0 = w.Y + o.V - f * Storey, v1 = v0 + o.Height;
                            if (MathF.Min(o.U1, MathF.Max(s0, s1)) - MathF.Max(o.U, MathF.Min(s0, s1)) > 0f && v1 > bottom && v0 < top)
                                bad.Add($"{nm} hangs over an opening (archetype {o.Archetype}, {v0:0.00}-{v1:0.00} m)");
                        }
                    }
                    // nor near a window of its room at any height: beside it, over or under it, round a corner (WallPropsKeepOffFireplacesAndWindows)
                    foreach (var why in NearWindows(res, m)) bad.Add($"{nm} hangs {why}");
                    // nor on what open plan left of a wall between two rooms that share one space: its 0.3 m piers either side of the
                    // cut (the cut itself is an opening, above)
                    foreach (var w in behindWalls)
                    {
                        var d = Dir(w); var nrm = Normal(w);
                        float s0 = (fp.X0 - w.X) * d.X + (fp.Z0 - w.Z) * d.Z, s1 = (fp.X1 - w.X) * d.X + (fp.Z1 - w.Z) * d.Z;
                        foreach (var o in w.Openings.Where(o => o.Archetype == BuildingGen.ArchPorch && o.V < 0.05f))
                        {
                            var c = At(w, o.U + o.Width * 0.5f); float off = w.Thickness * 0.5f + 0.3f;
                            if (RoomAt(res, f, c.X + nrm.X * off, c.Z + nrm.Z * off) == null || RoomAt(res, f, c.X - nrm.X * off, c.Z - nrm.Z * off) == null) continue;
                            if (MathF.Min(o.U1 + BuildingGen.OpenPier, MathF.Max(s0, s1)) - MathF.Max(o.U - BuildingGen.OpenPier, MathF.Min(s0, s1)) > 0f)
                                bad.Add($"{nm} hangs on an open plan's pier");
                        }
                    }
                    if (doorZones.Any(z => Overlap(fp, z))) bad.Add($"{nm} hangs in a doorway's walk or a door's swing");
                    // off the stairs' wall
                    if (stairs.Any(st => Overlap(Grown(fp, BuildingGen.StairWallReach - 0.05f), st, 0f))) bad.Add($"{nm} hangs on a wall the stairs run along");
                    // at a height: over a sill, under the ceiling
                    if (bottom < 1.0f || top > ceiling - 0.05f) bad.Add($"{nm} spans {bottom:0.00}-{top:0.00} m");
                    // apart from every other hung piece, the clock and the television too: fixtures over a run of counters side by
                    // side, the rest not crowding
                    foreach (var o in here.Where(o => o != m && o.Mounted))
                    {
                        bool run = Fixture(m) && Fixture(o);
                        if (run ? Overlap(fp, o.Footprint, 0.005f) : Overlap(Grown(fp, 0.25f), o.Footprint, 0f)) { bad.Add($"{nm} {(run ? "overlaps" : "crowds")} {o.Name} at ({o.X:0.00},{o.Z:0.00})"); break; }
                    }
                    // nothing under it taller than its bottom less 0.1 (a piece on another: the top of the pair); no light hung into it.
                    // Under a fixture over a counter: along the wall, its own span (it abuts the fridge beside its counter)
                    bool fixture = Fixture(m);
                    var under = fixture ? (MathF.Abs(fx) > 0.5f ? (fp.X0 - 0.15f, fp.Z0, fp.X1 + 0.15f, fp.Z1) : (fp.X0, fp.Z0 - 0.15f, fp.X1, fp.Z1 + 0.15f)) : Grown(fp, 0.15f);
                    foreach (var o in here.Where(o => !o.Mounted && Overlap(under, o.Footprint, 0.005f)))
                    {
                        float oTop = o.Y - f * Storey + FurnitureCatalog.Sizes[o.Name].H;
                        if (o.Ceiling && o.Y - f * Storey - (o.Name.Contains("Pendant") ? 0.95f : 0.28f) < top) bad.Add($"{nm}: {o.Name} hangs into it");
                        if (!o.Ceiling && oTop > bottom - 0.1f) bad.Add($"{nm}: {o.Name} under it stands {oTop:0.00} m, its bottom {bottom:0.00}");
                    }
                }
                // a wall holds one big piece (the television is one), and its hung pieces, fixtures aside, take at most half of
                // it (here: of the room's length that way, which a wall's run never exceeds)
                foreach (var g in res.Layout.Where(g => g.Floor == f))
                {
                    var hung = here.Where(m => m.Mounted && m.Room == g).ToList();
                    // the room's hung pieces by wall line: turned the same way, their backs within 0.3 m across
                    var lines = new List<List<FurniturePlan>>();
                    foreach (var m in hung.OrderBy(m => ((int)MathF.Round(m.Yaw / 90f) % 4 + 4) % 4).ThenBy(BackPlane))
                        if (lines.Count > 0 && SameWay(lines[^1][^1], m) && MathF.Abs(BackPlane(lines[^1][^1]) - BackPlane(m)) < 0.3f) lines[^1].Add(m);
                        else lines.Add(new List<FurniturePlan> { m });
                    // two pieces on one wall: wall behind and the room's floor in front all the way between them, no jut
                    bool OneWall(FurniturePlan a, FurniturePlan b)
                    {
                        var (fx, fz) = a.Front; float y0 = FurnitureCatalog.Sizes[a.Name].Y0;
                        float ax = a.X + fx * y0, az = a.Z + fz * y0, bx = b.X + fx * FurnitureCatalog.Sizes[b.Name].Y0, bz = b.Z + fz * FurnitureCatalog.Sizes[b.Name].Y0;
                        float d = MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                        for (float t = 0f; t <= d; t += 0.1f)
                        {
                            float px = ax + (bx - ax) * t / Math.Max(d, 1e-3f), pz = az + (bz - az) * t / Math.Max(d, 1e-3f);
                            if (!boxes.Any(q => px - fx * 0.15f >= q.X0 && px - fx * 0.15f <= q.X1 && pz - fz * 0.15f >= q.Z0 && pz - fz * 0.15f <= q.Z1)) return false;
                            if (!g.Holds(px + fx * 0.3f, pz + fz * 0.3f)) return false;
                            // (a real jut between them -- 0.5 m deep or more, a chimney breast -- splits the wall: paintings either
                            // side of a chimney are the classic layout, two walls. The 0.1 m step where an outside wall runs on as
                            // a thinner partition does not reach 0.45 m out: still one wall)
                            if (WallAt(px + fx * 0.45f, pz + fz * 0.45f)) return false;
                        }
                        return true;
                    }
                    foreach (var l in lines)
                    {
                        if (!l.Any(m => rowOf(m) != null)) continue;
                        var focal = l.Where(Focal).ToList();
                        for (int i = 0; i < focal.Count; i++)
                            for (int j = i + 1; j < focal.Count; j++)
                                if (OneWall(focal[i], focal[j])) bad.Add($"{label}: {g}: {focal[i].Name} and {focal[j].Name} on one wall");
                        bool alongX = MathF.Abs(l[0].Front.Z) > 0.5f;
                        float len = (alongX ? g.Rects.Max(r => r.X1) - g.Rects.Min(r => r.X0) : g.Rects.Max(r => r.Z1) - g.Rects.Min(r => r.Z0)) * Cell;
                        float fill = l.Where(m => !Fixture(m)).Sum(Width);
                        if (fill > 0.5f * len + 0.01f) bad.Add($"{label}: {g}: {fill:0.0} m hung on a wall of a room {len:0.0} m that way ({string.Join(", ", l.Select(m => m.Name))})");
                    }
                }
            }
            return bad;
        }

        static string DecorTally(Dictionary<(BuildingKind, RoomKind), int[]> tally)
            => string.Join("; ", tally.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2)
                                      .Select(kv => $"{kv.Key.Item1} {kv.Key.Item2}: {kv.Value[0]} pieces, {kv.Value[2]} of {kv.Value[1]} rooms"));

        // S: the wall décor (VoX: "needs more wall doo dads like posters and art and clocks and coat hooks and stuff like
        // that", then of astraclaw's kit: "start integrating these intelligently into the interior proc gen system"). Every
        // hung piece of a Decor row is flat on a wall of its own room -- the wall within 0.15 m behind it all along its
        // width, its back not in the wall -- in a kind of room its row allows and its recipe has a slot for, in a house that
        // meets its conditions (a double bed's paintings, a single's posters, a business's whiteboard, hooks by a way out);
        // where its spot says (over the counter it serves, by a door); over no window, doorway or vent at its height, out of
        // every doorway's walk and door's swing, off the stairs' wall; above a sill and under the ceiling; not crowding
        // another hung piece (the clock, the television), cabinets in a run side by side; nothing under it taller than its
        // bottom less 0.1 m (the wall cabinets, 2.4 m up, off the 2.5 m fridge), nor a light hanging into it; one big piece
        // to a wall and half a wall at most. And it is there: every row hangs somewhere but one waiting on furniture the
        // furnisher does not place, and most of the rooms that can take a piece do.
        // BREAK IT: let a piece hang over what stands below it; over an opening; into the wall; in a room its row does not
        // list; two big pieces to a wall.
        [Test]
        public void WallDecorFollowsTheRules()
        {
            var table = BuildingGen.Decor;
            var byName = table.ToDictionary(r => r.Name);
            var bad = new List<string>();
            var tally = new Dictionary<(BuildingKind, RoomKind), int[]>(); var rows = new Dictionary<DecorRow, int>();
            int buildings = 0, cabinets = 0, cabinetsByFridge = 0, openCuts = 0, openRooms = 0, openRoomPieces = 0;
            var anchors = new HashSet<string>();
            foreach (var (spec, res) in All())
            {
                buildings++;
                bad.AddRange(DecorViolations(Name(spec), res, m => byName.GetValueOrDefault(m.Name), tally, rows));
                foreach (var m in res.Furniture.Where(m => !m.Mounted && !m.Ceiling && m.On == null)) anchors.Add(m.Name);
                // open plan (round 11): the cuts, and the décor of the rooms either side of one -- none of it on the cut or a pier
                var opened = new HashSet<GenRoom>();
                foreach (var o in res.OpenPlan) { openCuts++; opened.Add(o.A); opened.Add(o.B); }
                openRooms += opened.Count;
                openRoomPieces += res.Furniture.Count(m => opened.Contains(m.Room) && byName.ContainsKey(m.Name));
                // the cabinets, and how many of them have a fridge beside them (side by side is right; over it, the rule above)
                foreach (var c in res.Furniture.Where(m => m.Name.StartsWith("Wall_Cabinet")))
                {
                    cabinets++;
                    if (res.Furniture.Any(o => o.Room == c.Room && o.Name.StartsWith("Fridge") && SameWay(o, c) && Overlap(Grown(c.Footprint, 0.1f), o.Footprint, 0f))) cabinetsByFridge++;
                }
            }
            int pieces = rows.Values.Sum(), rooms = tally.Values.Sum(t => t[1]), with = tally.Values.Sum(t => t[2]);
            // a fixture's row waits on furniture the furnisher does not place yet (the grey cabinet on Counter_2): said, not failed
            var waiting = table.Where(r => r.Spot == DecorSpot.Over && !r.Over.Any(p => anchors.Any(a => a.StartsWith(p)))).ToList();
            TestContext.Progress.WriteLine($"[decor] {pieces} pieces hung in {with} of {rooms} rooms the recipes cover, over {buildings} buildings: {DecorTally(tally)}; "
                                           + $"by row: {string.Join(", ", table.Select(r => $"{r.Name} {rows.GetValueOrDefault(r)}"))}; {cabinets} wall cabinets, {cabinetsByFridge} of them beside a fridge; "
                                           + $"waiting on furniture: {(waiting.Count == 0 ? "none" : string.Join(", ", waiting.Select(r => r.Name)))}; "
                                           + $"{openCuts} open-plan cuts, {openRoomPieces} pieces in the {openRooms} rooms beside them; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            foreach (var r in table)
            {
                Assert.That(FurnitureCatalog.Sizes.ContainsKey(r.Name), $"{r.Name}: a live row with no Sizes entry");
                if (!waiting.Contains(r)) Assert.That(rows.GetValueOrDefault(r), Is.GreaterThan(0), $"{r.Name} never hangs anywhere");
            }
            Assert.That(waiting.Count, Is.LessThanOrEqualTo(1), "only the grey cabinet waits on furniture");
            Assert.That(openRoomPieces, Is.GreaterThan(0), "the open-plan rules were walked: décor hangs in rooms beside a cut");
            Assert.That(with, Is.GreaterThanOrEqualTo(rooms / 2), "most rooms that can take a piece have one");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // T: the décor draws nothing from the furniture's dice. The Furnisher's Rng is shared by the whole building, so a
        // single draw from it in one room's décor would move every later room's furniture; the décor seeds its own. Each
        // building is generated with no décor at all and with it, and every piece that is not décor (the clocks and the
        // televisions too) must come out identical: name, place, turn, floor, in the same order. The décor itself is held to
        // the rules above.
        // BREAK IT: draw once from the Furnisher's Rng in Decorate.
        [Test]
        public void WallDecorKeepsToItsOwnDraws()
        {
            var specs = new List<BuildingGenSpec>();
            foreach (var kind in new[] { BuildingKind.Residential, BuildingKind.Commercial, BuildingKind.Industrial, BuildingKind.Apartment })
                for (int seed = 0; seed < (kind == BuildingKind.Apartment ? 3 : 12); seed++)
                {
                    specs.Add(new BuildingGenSpec { Seed = seed, Kind = kind });
                    if (kind != BuildingKind.Apartment) specs.Add(new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 10, MaxFloors = 3, MaxRoomsPerFloor = 4 });
                }
            var bad = new List<string>();
            var tally = new Dictionary<(BuildingKind, RoomKind), int[]>(); var rows = new Dictionary<DecorRow, int>();
            int compared = 0, hung = 0;
            try
            {
                foreach (var spec in specs)
                {
                    BuildingGen.DecorOverride = Array.Empty<DecorRow>();
                    var plain = BuildingGen.Generate(spec);
                    var mine = new Dictionary<FurniturePlan, DecorRow>();
                    BuildingGen.DecorOverride = null;
                    BuildingGen.DecorTrace = (m, r) => mine[m] = r;
                    var dressed = BuildingGen.Generate(spec);
                    BuildingGen.DecorTrace = null;
                    hung += mine.Count;
                    var rest = dressed.Furniture.Where(m => !mine.ContainsKey(m)).ToList();
                    string Key(FurniturePlan m) => FormattableString.Invariant($"{m.Name} {m.X:R} {m.Y:R} {m.Z:R} {m.Yaw:R} {m.Floor} {m.Mounted} {m.Ceiling} {m.On?.Name}");
                    compared += plain.Furniture.Count;
                    if (rest.Count != plain.Furniture.Count) bad.Add($"{Name(spec)}: {plain.Furniture.Count} pieces without décor, {rest.Count} besides it");
                    else
                        for (int i = 0; i < rest.Count; i++)
                            if (Key(rest[i]) != Key(plain.Furniture[i])) { bad.Add($"{Name(spec)}: piece {i} {Key(plain.Furniture[i])} became {Key(rest[i])}"); break; }
                    // (every décor piece came through Decorate: none is in the dressed building untold)
                    int named = dressed.Furniture.Count(m => BuildingGen.Decor.Any(r => r.Name == m.Name));
                    if (named != mine.Count) bad.Add($"{Name(spec)}: {named} décor pieces by name, {mine.Count} told");
                    bad.AddRange(DecorViolations(Name(spec), dressed, m => mine.GetValueOrDefault(m), tally, rows));
                }
            }
            finally { BuildingGen.DecorOverride = null; BuildingGen.DecorTrace = null; }
            TestContext.Progress.WriteLine($"[decor-draws] {compared} pieces compared over {specs.Count} buildings, {hung} décor pieces hung besides: {DecorTally(tally)}; "
                                           + $"{bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(compared, Is.GreaterThan(1000));
            Assert.That(hung, Is.GreaterThan(500));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // V: the table and the recipes agree: every room a row lists has a slot that row fills, or the row would never hang
        // there and nothing would say so (the small paintings listed halls, whose recipe had no "wall" slot).
        // BREAK IT: drop the hall's "wall" slot.
        [Test]
        public void WallDecorRowsReachTheirRooms()
        {
            var bad = new List<string>();
            int pairs = 0;
            foreach (var r in BuildingGen.Decor)
                foreach (var k in r.Rooms)
                {
                    pairs++;
                    if (!BuildingGen.DecorRecipes.TryGetValue(k, out var recipe) || !recipe.Any(sl => r.Fills(sl.Slot))) bad.Add($"{r.Name} lists {k}, whose recipe has none of its slots ({r.Slot})");
                }
            TestContext.Progress.WriteLine($"[decor-reach] {pairs} row/room pairs over {BuildingGen.Decor.Count} rows; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        // U: the kit is registered under the GUIDs its manifest gave (astraclaw's wall_decor_manifest.json, approved by VoX
        // 2026-10-13, and the sunburst's sunburst_manifest.json). A layout saved by the generator stores each piece as a GUID and loads it back through guid_mesh.txt,
        // so the GUID is the contract: a piece registered under a fresh one passes every catalog check and loads as nothing.
        // BREAK IT: mint a fresh GUID for one row.
        [Test]
        public void WallDecorKitKeepsItsGuids()
        {
            var want = new Dictionary<string, string>
            {
                ["Painting_Landscape_0"] = "346af548d3dd44538c982498df0e9b90", ["Painting_Landscape_1"] = "0e4a100d9c944e369423e52e5c390ad0",
                ["Painting_Landscape_2"] = "f651e1b9289d4aa89359a5123b0ae040", ["Painting_Landscape_3"] = "9397e55c26724f4d96cd950d4d15304b",
                ["Painting_Portrait_0"] = "1036625e4c1b46f0b9ca36592c9745c0", ["Painting_Portrait_1"] = "daf247b3d81c46e2ba67c1a3510fc1cb",
                ["Painting_Small_0"] = "912c1f63ea464847944b5d47b2ee53bc", ["Painting_Small_1"] = "09cee6c96cfb4c4ea0bd347f24b1f5c4",
                ["Painting_Small_2"] = "2da2fd1f55e849c3801b1766e2d006fe", ["Photo_Frames_0"] = "f488b031ced247878e120e8be72d13e7",
                ["Poster_Tall_0"] = "215f4daf3a784d41b44411af042024ad", ["Poster_Tall_1"] = "4d48536130124f7ba62a2a92b66fb7fa",
                ["Poster_Tall_2"] = "8ace427204dc4c60a9a39665a863f710", ["Poster_Tall_3"] = "7dc13751f37645aab27a9a8d5197ccf9",
                ["Mirror_0"] = "74b1af9317bf4afbb495330d0dacc3ca", ["Coat_Hooks_0"] = "80e5677b4b714036bf145feab43564d4",
                ["Key_Rack_0"] = "e92d87f48d2d4275bc73f8dabc78b2c4", ["Wall_Shelf_0"] = "d3951a2f183445588a88deb8a658c327",
                ["Wall_Shelf_1"] = "7b96fbe15e8e49aa9470ee79db535889", ["Wall_Cabinet_0"] = "122e3aaa346640daabd06b73dc4c2fe1",
                ["Wall_Cabinet_1"] = "628034939a4c48b7b2cf3b85a6ecdc9b", ["Utensil_Rail_0"] = "66922cd52bac4dd196ea956c5f639b6b",
                ["Calendar_0"] = "1c9fa1cc1ace4d268b5c67ffdee6d227", ["Towel_Rail_0"] = "471d7fabd6e048ac9f65c15345b8620b",
                ["Medicine_Cabinet_0"] = "189e6e3aa286434aa1dc42f5b490f512", ["Whiteboard_0"] = "03a0c8aa49804a058fb175f3bca91944",
                ["Corkboard_0"] = "f0e010853da94220897d1732bc3b2c44", ["Certificate_0"] = "184611e0903f449a870760ffe08b6e89",
                ["Wall_Map_0"] = "91281d896ec949e584718b60edd53916", ["Pegboard_0"] = "2ea6760b40124eafa70f49436862b201",
                ["Antlers_0"] = "1c731cb8c97844c7a7c1ac9e405941dc", ["Wall_Sunburst_0"] = "ef0e6c14a98645a88623ba40c269a4d4",
            };
            string dir = Objects();
            Assert.That(dir, Is.Not.Null, "game/content/objects not found");
            // the lookup a loading placement makes: guid -> first name (EditorObjects._guidToName, TryAdd)
            var byGuid = new Dictionary<string, string>();
            foreach (var p in System.IO.File.ReadAllLines(System.IO.Path.Combine(dir, "guid_mesh.txt")).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2))
                byGuid.TryAdd(p[0], p[1]);
            var bad = want.Where(kv => byGuid.GetValueOrDefault(kv.Value) != kv.Key).Select(kv => $"{kv.Key}: its GUID {kv.Value} loads {byGuid.GetValueOrDefault(kv.Value) ?? "NOTHING"}").ToList();
            // and every live row is one of them, or the retail poster
            bad.AddRange(BuildingGen.Decor.Where(r => r.Name != "Poster_0" && !want.ContainsKey(r.Name)).Select(r => $"{r.Name}: a live row not in the kit"));
            TestContext.Progress.WriteLine($"[decor-guids] {want.Count} kit pieces held to their manifest GUIDs, {BuildingGen.Decor.Count} live rows; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }
    }
}
