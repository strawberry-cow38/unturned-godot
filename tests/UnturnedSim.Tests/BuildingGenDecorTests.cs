using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the wall décor (BuildingGen.Decor, FurnishDecor.cs): the same sweep as the rest of BuildingGenTests, read off
    // the generated walls, not the generator's plan of them. Its own numbers are looser than the generator's: it keeps
    // 0.3 m between hung pieces, this asks 0.05; it keeps what stands within 0.25 m below a piece's bottom less 0.1, this
    // looks 0.15 m round.
    public partial class BuildingGenTests
    {
        /// <summary>Every rule a hung piece of wall décor breaks, over one building: `rowOf` says which pieces are décor and
        /// from which row (null: not décor). `tally`, by building kind and room kind, counts [pieces, rooms the table lists
        /// that kind for, rooms with at least one]; `rows` counts pieces per row.</summary>
        static List<string> DecorViolations(string label, BuildingGenResult res, Func<FurniturePlan, DecorRow> rowOf, IReadOnlyCollection<DecorRow> table,
                                            Dictionary<(BuildingKind, RoomKind), int[]> tally, Dictionary<DecorRow, int> rows)
        {
            var bad = new List<string>();
            var decor = res.Furniture.Where(m => rowOf(m) != null).ToList();
            foreach (var g in res.Layout.Where(g => table.Any(r => r.Rooms.Contains(g.Kind))))
            {
                var t = tally.TryGetValue((res.Kind, g.Kind), out var a) ? a : tally[(res.Kind, g.Kind)] = new int[3];
                int n = decor.Count(m => m.Room == g);
                t[0] += n; t[1]++; if (n > 0) t[2]++;
            }
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
                        if (o.HasDoor && o.Archetype != BuildingGen.ArchGarage)
                        {
                            float back = w.Thickness * 0.5f + o.Width - 0.05f;
                            doorZones.Add((MathF.Min(MathF.Min(p.X, q.X), MathF.Min(p.X, q.X) - nrm.X * back), MathF.Min(MathF.Min(p.Z, q.Z), MathF.Min(p.Z, q.Z) - nrm.Z * back),
                                           MathF.Max(MathF.Max(p.X, q.X), MathF.Max(p.X, q.X) - nrm.X * back), MathF.Max(MathF.Max(p.Z, q.Z), MathF.Max(p.Z, q.Z) - nrm.Z * back)));
                        }
                    }
                }
                var here = res.Furniture.Where(m => m.Floor == f).ToList();
                float ceiling = f * Storey + Storey - BuildingGen.Slab;
                foreach (var m in decor.Where(m => m.Floor == f))
                {
                    var row = rowOf(m);
                    rows[row] = rows.GetValueOrDefault(row) + 1;
                    string nm = $"{label}: {m.Name} at ({m.X:0.00},{m.Z:0.00}) yaw {m.Yaw:0} in {m.Room}";
                    var s = FurnitureCatalog.Sizes[m.Name];
                    var (fx, fz) = m.Front; var (rx, rz) = m.Right; var fp = m.Footprint;
                    float bottom = m.Y - f * Storey - s.H, top = m.Y - f * Storey + s.H;
                    if (!m.Mounted || m.Ceiling || m.On != null) { bad.Add($"{nm} is not hung on a wall"); continue; }
                    // in a room the row allows, the house meeting its conditions
                    if (!row.Rooms.Contains(m.Room.Kind)) bad.Add($"{nm}: {row.Name} does not go in a {m.Room.Kind}");
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
                    if (row.Need.HasFlag(DecorNeed.OuterDoor) && !Doors(res, f).Any(d => (d.A == m.Room && d.B == null) || (d.B == m.Room && d.A == null)))
                        bad.Add($"{nm}: wants a door to outside");
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
                    if (doorZones.Any(z => Overlap(fp, z))) bad.Add($"{nm} hangs in a doorway's walk or a door's swing");
                    // off the stairs' wall
                    if (stairs.Any(st => Overlap(Grown(fp, BuildingGen.StairWallReach - 0.05f), st, 0f))) bad.Add($"{nm} hangs on a wall the stairs run along");
                    // at a height: over a sill, under the ceiling
                    if (bottom < 1.0f || top > ceiling - f * Storey - 0.05f) bad.Add($"{nm} spans {bottom:0.00}-{top:0.00} m");
                    // apart from every other hung piece, the clock and the television too: not touching
                    foreach (var o in here.Where(o => o != m && o.Mounted))
                        if (Overlap(Grown(fp, 0.05f), o.Footprint, 0f)) { bad.Add($"{nm} touches {o.Name} at ({o.X:0.00},{o.Z:0.00})"); break; }
                    // nothing under it taller than its bottom less 0.1 (a piece on another: the top of the pair); no light hung into it
                    foreach (var o in here.Where(o => !o.Mounted && Overlap(Grown(fp, 0.15f), o.Footprint, 0f)))
                    {
                        float oTop = o.Y - f * Storey + FurnitureCatalog.Sizes[o.Name].H;
                        if (o.Ceiling && o.Y - f * Storey - (o.Name.Contains("Pendant") ? 0.95f : 0.28f) < top) bad.Add($"{nm}: {o.Name} hangs into it");
                        if (!o.Ceiling && oTop > bottom - 0.1f) bad.Add($"{nm}: {o.Name} under it stands {oTop:0.00} m, its bottom {bottom:0.00}");
                    }
                }
            }
            return bad;
        }

        static string DecorTally(Dictionary<(BuildingKind, RoomKind), int[]> tally)
            => string.Join("; ", tally.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2)
                                      .Select(kv => $"{kv.Key.Item1} {kv.Key.Item2}: {kv.Value[0]} pieces, {kv.Value[2]} of {kv.Value[1]} rooms"));

        // S: the wall décor (VoX: "needs more wall doo dads like posters and art and clocks and coat hooks and stuff like
        // that"). Every piece of a live Decor row is hung, flat on a wall of its own room -- the wall within 0.15 m behind it
        // all along its width, its back not in the wall -- in a kind of room its row allows and a house that meets its
        // conditions; over no window, doorway or vent at its height, out of every doorway's walk and door's swing, off the
        // stairs' wall; above a sill and under the ceiling; not touching another hung piece (the clock, the television);
        // nothing under it taller than its bottom less 0.1 m, nor a light hanging into it. And it is there: every live row
        // hangs somewhere, and most of the rooms that can take one do.
        // BREAK IT: let a piece hang over what stands below it; over an opening; into the wall; in a room its row does not
        // list; touching the next one.
        [Test]
        public void WallDecorFollowsTheRules()
        {
            var table = BuildingGen.Decor;
            var byName = table.ToDictionary(r => r.Name);
            var bad = new List<string>();
            var tally = new Dictionary<(BuildingKind, RoomKind), int[]>(); var rows = new Dictionary<DecorRow, int>();
            int buildings = 0;
            foreach (var (spec, res) in All())
            {
                buildings++;
                bad.AddRange(DecorViolations(Name(spec), res, m => byName.GetValueOrDefault(m.Name), table, tally, rows));
            }
            int pieces = rows.Values.Sum(), rooms = tally.Values.Sum(t => t[1]), with = tally.Values.Sum(t => t[2]);
            TestContext.Progress.WriteLine($"[decor] {pieces} pieces hung in {with} of {rooms} rooms the table has rows for, over {buildings} buildings: {DecorTally(tally)}; "
                                           + $"by row: {string.Join(", ", table.Select(r => $"{r.Name} {rows.GetValueOrDefault(r)}"))}; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            foreach (var r in table)
            {
                Assert.That(FurnitureCatalog.Sizes.ContainsKey(r.Name), $"{r.Name}: a live row with no Sizes entry");
                Assert.That(rows.GetValueOrDefault(r), Is.GreaterThan(0), $"{r.Name} never hangs anywhere");
            }
            Assert.That(with, Is.GreaterThanOrEqualTo(rooms / 2), "most rooms that can take a piece have one");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // T: the décor draws nothing from the furniture's dice. The Furnisher's Rng is shared by the whole building, so a
        // single draw from it in one room's décor would move every later room's furniture; the décor seeds its own. Each
        // building is generated with no décor at all and with a busy table -- stand-in pieces (meshes in the catalog now)
        // through every kind of room, every spot (a free wall, over a piece, by a door), groups, every condition -- and every
        // piece that is not décor (the clocks and televisions too) must come out identical: name, place, turn, floor, in the
        // same order. The busy pieces are held to the same rules as the live ones, so the rows waiting on astraclaw's
        // models have had their spots walked before they go live.
        // BREAK IT: draw once from the Furnisher's Rng in Decorate.
        [Test]
        public void WallDecorKeepsToItsOwnDraws()
        {
            var every = Enum.GetValues(typeof(RoomKind)).Cast<RoomKind>().ToArray();
            var busy = new[]
            {
                new DecorRow("Poster_0", every, Height: 2.55f, Weight: 6, Max: 2, Over: new[] { "Couch", "Bed_0", "Table_Metal_0" }, Beside: new[] { "Wardrobe" }),
                new DecorRow("Clock_0", In(RoomKind.Kitchen, RoomKind.BreakRoom, RoomKind.Bathroom), Height: 2.8f, Max: 6, Spot: DecorSpot.Over, Over: new[] { "Counter_0", "Counter_1" }),
                new DecorRow("Clock_0", In(RoomKind.Hall, RoomKind.Garage, RoomKind.Kitchen, RoomKind.LivingRoom), Height: 2.0f, Weight: 8, Need: DecorNeed.OuterDoor, Spot: DecorSpot.Door),
                new DecorRow("Clock_0", every, Height: 2.4f, Weight: 6, Max: 2, Group: 3, Over: new[] { "Couch", "Bed" }),
                new DecorRow("Clock_0", In(RoomKind.Bedroom), Height: 2.65f, Weight: 6, Need: DecorNeed.SingleBed, Over: new[] { "Bed_1" }),
                new DecorRow("Clock_0", In(RoomKind.Bedroom, RoomKind.Hall), Height: 2.6f, Weight: 6, Need: DecorNeed.Wardrobe | DecorNeed.Home, Beside: new[] { "Wardrobe" }),
                new DecorRow("Poster_0", In(RoomKind.Office, RoomKind.BreakRoom), Height: 2.5f, Weight: 6, Need: DecorNeed.Business),
            };
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
                    BuildingGen.DecorOverride = busy;
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
                    bad.AddRange(DecorViolations(Name(spec), dressed, m => mine.GetValueOrDefault(m), busy, tally, rows));
                }
            }
            finally { BuildingGen.DecorOverride = null; BuildingGen.DecorTrace = null; }
            TestContext.Progress.WriteLine($"[decor-draws] {compared} pieces compared over {specs.Count} buildings, {hung} busy pieces hung besides: {DecorTally(tally)}; "
                                           + $"by row: {string.Join(", ", busy.Select((r, i) => $"#{i} {r.Name} {r.Spot} {rows.GetValueOrDefault(r)}"))}; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(compared, Is.GreaterThan(1000));
            foreach (var r in busy) Assert.That(rows.GetValueOrDefault(r), Is.GreaterThan(0), $"busy row {r.Name} {r.Spot} never hung");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        static RoomKind[] In(params RoomKind[] k) => k;
    }
}
