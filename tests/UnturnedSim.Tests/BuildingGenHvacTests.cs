using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // Round 12c: astraclaw's HVAC covers, a floor register and a wall grille to a room, with a per-room HVAC record a
    // temperature system can read (the lead's briefs, for strawberry). Read off the generated walls and pieces, with numbers
    // of the tests' own.
    public partial class BuildingGenTests
    {
        /// <summary>A storey's wall centrelines as the editor hands RoomEnclosure a storey (EditorBuildings.PlanOfFloorAt).</summary>
        static List<RoomEnclosure.PlanSegment> PlanOf(BuildingGenResult res, int f)
            => WallsOn(res, f).Select((w, i) => { var a = At(w, 0f); var b = At(w, w.Length); return new RoomEnclosure.PlanSegment(a.X, a.Z, b.X, b.Z, i, w.Thickness); }).ToList();

        // AE: the HVAC vents (astraclaw's covers; the lead's brief): a floor register and a wall grille to a room of the kinds
        // that get them, each where it fits. A register flat on its floor, its long side along a wall: under one of the room's
        // windows, or else against a wall with no room behind it; wholly on the room's floor and off its walls, on no piece,
        // rug, stair, flight's end or door's swing. A grille high on a wall with another room behind it, its top 0.3 m under
        // the ceiling over it; 0.5 m off a window at any height, over no doorway, 0.25 m off every hung piece and curtain,
        // off a fireplace's wall and the stairs'. And the room's HVAC record says so: an HVAC room is one with a vent, its
        // record lists exactly its vents -- each by its Id, the vent's place in BuildingGenResult.HvacVents -- floor and wall
        // apart, its label in Rooms carries the same record, and HvacAt finds it from a point in the room.
        // BREAK IT: let a register stand on a rug; let a grille hang on an outside wall; number the vents from 1.
        [Test]
        public void HvacVentsFollowTheRules()
        {
            var bad = new List<string>();
            int rooms = 0, hvacRooms = 0, registers = 0, underWindow = 0, outside = 0, grilles = 0, queried = 0, labels = 0;
            foreach (var (spec, res) in All())
            {
                // the records against the vents, both ways
                var seen = new HashSet<int>();
                foreach (var g in res.Layout)
                {
                    bool takes = BuildingGen.HvacRooms.Contains(g.Kind);
                    if (takes) rooms++;
                    if (g.Hvac == null) continue;
                    if (!takes) bad.Add($"{Name(spec)}: {g}, a {g.Kind}, has HVAC");
                    if (!g.Hvac.HasHvac) bad.Add($"{Name(spec)}: {g} has an empty HVAC record");
                    hvacRooms++;
                    foreach (var v in g.Hvac.Vents)
                    {
                        if (v.Id < 0 || v.Id >= res.HvacVents.Count || !seen.Add(v.Id)) { bad.Add($"{Name(spec)}: {g}: vent id {v.Id} is no vent, or another room's"); continue; }
                        var m = res.HvacVents[v.Id];
                        string want = v.Kind == HvacVentKind.FloorRegister ? "Floor_Vent_0" : "Wall_Vent_0";
                        if (m.Name != want || m.Room != g || MathF.Abs(m.X - v.X) > 1e-3f || MathF.Abs(m.Y - v.Y) > 1e-3f || MathF.Abs(m.Z - v.Z) > 1e-3f || MathF.Abs(m.Yaw - v.Yaw) > 1e-3f)
                            bad.Add($"{Name(spec)}: {g}: its record's {v} is {m}");
                    }
                    if (g.Hvac.FloorRegisters.Count > 1 || g.Hvac.WallGrilles.Count > 1) bad.Add($"{Name(spec)}: {g} has {g.Hvac}");
                    var label = res.Rooms.Where(d => d.Floor == g.Floor && g.Holds(d.X, d.Z)).ToList();
                    if (label.Count != 1 || !ReferenceEquals(label[0].Hvac, g.Hvac)) bad.Add($"{Name(spec)}: {g}'s label carries {(label.Count == 1 ? label[0].Hvac?.ToString() ?? "no HVAC" : $"{label.Count} labels")}");
                    else labels++;
                    // the query, from the middle of each of its rectangles
                    foreach (var r in g.Rects)
                    {
                        queried++;
                        float x = (r.X0 + r.X1) * 0.5f * Cell, z = (r.Z0 + r.Z1) * 0.5f * Cell;
                        if (!ReferenceEquals(res.HvacAt(x, z, g.Floor), g.Hvac)) bad.Add($"{Name(spec)}: HvacAt({x:0.0},{z:0.0}) on floor {g.Floor} is not {g}'s");
                    }
                }
                foreach (var g in res.Layout.Where(g => g.Hvac == null))
                    if (res.HvacAt((g.X0 + g.X1) * 0.5f * Cell, (g.Z0 + g.Z1) * 0.5f * Cell, g.Floor) != null) bad.Add($"{Name(spec)}: HvacAt in {g}, a room with no vents, found some");
                if (seen.Count != res.HvacVents.Count) bad.Add($"{Name(spec)}: {res.HvacVents.Count} vents, {seen.Count} in the rooms' records");

                for (int f = 0; f < res.Floors; f++)
                {
                    var walls = WallsOn(res, f).ToList();
                    var boxes = walls.Select(Box).ToList();
                    var stairs = StairsAndTiles(res, f);
                    // (the stairs' wall: the flights, landings and holes, as the wall décor's -- not a flight's end tile, floor)
                    var flights = res.Stairs.Where(st => st.Floor == f).Select(Foot).Concat(res.Walls.Where(w => w.Kind == SurfaceKind.Stairs && FloorOf(w) == f).Select(Plan)).Concat(Holes(res, f)).ToList();
                    // (every floor a leaf sweeps, LeafBoxes: both sides of a double-acting door, none of a pocket door's)
                    var leaves = walls.SelectMany(w => w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage).SelectMany(o => LeafBoxes(w, o))).ToList();
                    var standing = res.Furniture.Where(o => o.Floor == f && o.On == null && !o.Mounted && !o.Ceiling).ToList();
                    var rugs = res.RuntimeDecor.Where(o => o.Floor == f && o.Gen.Kind == RuntimeDecorKind.Rug).ToList();
                    var curtains = res.RuntimeDecor.Where(o => o.Floor == f && o.Gen.Kind == RuntimeDecorKind.Curtain).ToList();
                    var hung = res.Furniture.Where(o => o.Floor == f && o.Mounted).ToList();
                    var fires = Fireplaces(res);
                    foreach (var m in res.HvacVents.Where(m => m.Floor == f))
                    {
                        string nm = $"{Name(spec)}: {m}";
                        var fp = m.Footprint; var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                        var s = FurnitureCatalog.Sizes[m.Name];
                        if (m.Name == "Floor_Vent_0")
                        {
                            registers++;
                            if (m.Mounted || MathF.Abs(m.Y - f * Storey) > 1e-3f) bad.Add($"{nm} is not on its floor");
                            for (float x = fp.X0 + 0.02f; x < fp.X1; x += 0.1f)
                                for (float z = fp.Z0 + 0.02f; z < fp.Z1; z += 0.1f)
                                    if (RoomAt(res, f, x, z) != m.Room || boxes.Any(b => x > b.X0 && x < b.X1 && z > b.Z0 && z < b.Z1)) { bad.Add($"{nm} is off its room's floor at ({x:0.00},{z:0.00})"); goto placed; }
                            placed:
                            foreach (var o in standing.Where(o => Overlap(fp, o.Footprint))) bad.Add($"{nm} is under {o}");
                            foreach (var o in rugs.Where(o => Overlap(fp, o.Footprint))) bad.Add($"{nm} is under a rug");
                            if (stairs.Any(q => Overlap(fp, q))) bad.Add($"{nm} is on the stairs or a flight's end");
                            if (leaves.Any(q => Overlap(fp, q))) bad.Add($"{nm} is in a door's swing");
                            // its back against a wall (0.6 m at most behind its back edge), and that wall: a window's, or outside
                            float bx = m.X + fx * s.Y0, bz = m.Z + fz * s.Y0;
                            WallPlan wall = null;
                            for (float d = 0.05f; d <= 0.6f && wall == null; d += 0.05f)
                                wall = walls.FirstOrDefault(w => Box(w) is var b && bx - fx * d > b.X0 && bx - fx * d < b.X1 && bz - fz * d > b.Z0 && bz - fz * d < b.Z1);
                            if (wall == null) { bad.Add($"{nm} has no wall behind it"); continue; }
                            var wd = Dir(wall);
                            float u = (m.X - wall.X) * wd.X + (m.Z - wall.Z) * wd.Z;
                            if (wall.Openings.Any(o => o.V >= 0.05f && o.Glazed && u >= o.U - 0.05f && u <= o.U1 + 0.05f)) underWindow++;
                            else if (RoomAt(res, f, bx - fx * (wall.Thickness + 0.6f), bz - fz * (wall.Thickness + 0.6f)) == null) outside++;
                            else bad.Add($"{nm}: neither under a window nor against an outside wall");
                        }
                        else
                        {
                            grilles++;
                            if (!m.Mounted) bad.Add($"{nm} is not hung");
                            float top = m.Y - f * Storey + s.H, bottom = top - 2f * s.H;
                            float over = new[] { (fp.X0, fp.Z0), (fp.X1, fp.Z0), (fp.X0, fp.Z1), (fp.X1, fp.Z1) }.Min(q => CeilingAt(res, f, q.Item1, q.Item2)) - f * Storey;
                            if (MathF.Abs(over - top - 0.3f) > 0.05f) bad.Add($"{nm}: its top {over - top:0.00} m under the ceiling");
                            // (and high on the wall, whatever the ceiling computes to: its top 3.3-4.0 m up -- under a storey's 3.75 m
                            // slab, or a top storey's 4.25 m wall heads)
                            if (top < 3.3f || top > 4.0f) bad.Add($"{nm}: its top {top:0.00} m up");
                            // another room behind it, all along its width
                            for (float a = s.X0 + 0.05f; a <= s.X1 - 0.05f + 1e-3f; a += (s.X1 - s.X0 - 0.1f) * 0.5f)
                            {
                                float px = m.X + rx * a - fx * 0.9f, pz = m.Z + rz * a - fz * 0.9f;
                                var behind = RoomAt(res, f, px, pz);
                                if (behind == null || behind == m.Room) { bad.Add($"{nm} is on a wall with {(behind == null ? "the outside" : "its own room")} behind it"); break; }
                            }
                            foreach (var why in NearWindows(res, m)) bad.Add($"{nm} hangs {why}");
                            foreach (var w in walls)
                            {
                                var d = Dir(w);
                                if (!Overlap(Grown(fp, 0.15f), Box(w), 0f)) continue;
                                float s0 = (fp.X0 - w.X) * d.X + (fp.Z0 - w.Z) * d.Z, s1 = (fp.X1 - w.X) * d.X + (fp.Z1 - w.Z) * d.Z;
                                foreach (var o in w.Openings.Where(o => o.V < 0.05f))
                                    if (MathF.Min(o.U1, MathF.Max(s0, s1)) - MathF.Max(o.U, MathF.Min(s0, s1)) > 0f && o.V + o.Height > bottom) bad.Add($"{nm} hangs over a doorway");
                            }
                            foreach (var o in hung.Where(o => Overlap(Grown(fp, 0.25f), o.Footprint, 0f))) bad.Add($"{nm} crowds {o}");
                            foreach (var o in curtains.Where(o => Overlap(Grown(fp, 0.25f), o.Footprint, 0f))) bad.Add($"{nm} crowds a curtain");
                            foreach (var fire in fires.Where(fire => OnFireWall(m, fire, false))) bad.Add($"{nm} is on a fireplace's wall");
                            if (flights.Any(q => Overlap(Grown(fp, BuildingGen.StairWallReach - 0.05f), q, 0f))) bad.Add($"{nm} hangs on a wall the stairs run along");
                        }
                    }
                }
            }
            foreach (var k in bad.GroupBy(b => System.Text.RegularExpressions.Regex.Replace(b.Substring(b.IndexOf(':') + 1), @"[-0-9.,()]+|seed \d+.*?\):", "#").Split(" in ").Last()).OrderByDescending(g => g.Count()).Take(12))
                TestContext.Progress.WriteLine($"[hvac]   {k.Count(),5} x {k.First()}");
            TestContext.Progress.WriteLine($"[hvac] {hvacRooms} HVAC rooms of the {rooms} that take vents, {labels} labels carrying their records; {registers} floor registers "
                                           + $"({underWindow} under a window, {outside} against an outside wall), {grilles} wall grilles; HvacAt asked {queried} times; "
                                           + $"{bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(hvacRooms, Is.GreaterThan(rooms * 3 / 4), "most rooms that take vents have them");
            Assert.That(new[] { underWindow, outside, grilles }, Has.All.GreaterThan(100));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // AF: the vents move nothing: each building generated with them and without (BuildingGen.NoHvac), its furniture, its
        // curtains and rugs come out identical; the record is what is added.
        // BREAK IT: draw once from the Furnisher's shared Rng in Vents.
        [Test]
        public void HvacMovesNothing()
        {
            var bad = new List<string>();
            int compared = 0, vents = 0;
            string Key(FurniturePlan m) => FormattableString.Invariant($"{m.Name} {m.X:R} {m.Y:R} {m.Z:R} {m.Yaw:R} {m.Floor} {m.Mounted} {m.Ceiling} {m.On?.Name} {m.Gen}");
            try
            {
                foreach (var kind in new[] { BuildingKind.Residential, BuildingKind.Apartment, BuildingKind.Commercial })
                    for (int seed = 0; seed < (kind == BuildingKind.Apartment ? 3 : 10); seed++)
                    {
                        var spec = new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = seed % 2 == 0 ? 0 : 10, MaxFloors = seed % 2 == 0 ? 2 : 3, MaxRoomsPerFloor = seed % 2 == 0 ? 6 : 4 };
                        BuildingGen.NoHvac = true;
                        var plain = BuildingGen.Generate(spec);
                        BuildingGen.NoHvac = false;
                        var vented = BuildingGen.Generate(spec);
                        var a = plain.Furniture.Concat(plain.RuntimeDecor).Select(Key).ToList(); var b = vented.Furniture.Concat(vented.RuntimeDecor).Select(Key).ToList();
                        compared += a.Count; vents += vented.HvacVents.Count;
                        if (plain.HvacVents.Count != 0 || plain.Layout.Any(g => g.Hvac != null)) bad.Add($"{Name(spec)}: vents with them off");
                        if (!a.SequenceEqual(b)) bad.Add($"{Name(spec)}: the pieces moved: {a.Zip(b).FirstOrDefault(t => t.First != t.Second)}");
                    }
            }
            finally { BuildingGen.NoHvac = false; }
            TestContext.Progress.WriteLine($"[hvac-draws] {compared} pieces compared, {vents} vents besides; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(compared, Is.GreaterThan(1000));
            Assert.That(vents, Is.GreaterThan(200));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // AG: the HVAC record survives a save -- on its room's label, as the curtains' inputs do on their pieces' lines -- and a
        // saved layout answers the query: RoomDesignations.HvacAt, from the rooms RoomEnclosure finds in the storey's walls
        // (as the editor finds them, EditorBuildings.HvacAt), gives each HVAC room's record back from a point inside it, and
        // nothing from a room without one. A label with no vents saves the line it always did; a save from before loads
        // every label with no record; a block that will not read loses the record, not the label.
        // BREAK IT: drop the hvac block on write; read a wall grille as a floor register; let HvacAt answer for the room
        // a point is NOT in.
        [Test]
        public void HvacRecordSurvivesASave()
        {
            var res = BuildingGen.Generate(new BuildingGenSpec { Seed = 3, Kind = BuildingKind.Residential, MinRooms = 8, MinFloors = 2, MaxFloors = 3, MaxRoomsPerFloor = 4 });
            Assert.That(res.Rooms.Count(d => d.Hvac != null), Is.GreaterThan(3), "the case has HVAC rooms");
            Assert.That(res.Rooms.Any(d => d.Hvac == null), "and rooms without");
            Assert.That(res.Floors, Is.GreaterThan(1), "on more than one storey");
            string text = WallSave.Write(res.Walls, res.Kind, res.Rooms, res.DoorMarks, res.Props);
            WallSave.Read(text.Split('\n'), out _, out var rooms, out _, out var props);
            Assert.That(rooms.Count, Is.EqualTo(res.Rooms.Count));
            for (int i = 0; i < rooms.Count; i++)
            {
                var (a, b) = (res.Rooms[i], rooms[i]);
                if (a.Hvac == null) { Assert.That(b.Hvac, Is.Null, $"room {i} {a.Kind} has no record"); continue; }
                Assert.That(b.Hvac, Is.Not.Null, $"room {i} {a.Kind} keeps its record");
                Assert.That(b.Hvac.FloorRegisters.Count == a.Hvac.FloorRegisters.Count && b.Hvac.WallGrilles.Count == a.Hvac.WallGrilles.Count, $"room {i}: {b.Hvac} for {a.Hvac}");
                foreach (var (p, q) in a.Hvac.Vents.Zip(b.Hvac.Vents))
                {
                    Assert.That(q.Kind == p.Kind && q.Id == p.Id && MathF.Abs(q.X - p.X) < 1e-3f && MathF.Abs(q.Y - p.Y) < 1e-3f && MathF.Abs(q.Z - p.Z) < 1e-3f && MathF.Abs(q.Yaw - p.Yaw) < 1e-3f, $"room {i}: {q} for {p}");
                    // the vent its id names is the vent prop saved there
                    var vent = props.Where(m => m.Name is "Floor_Vent_0" or "Wall_Vent_0").ElementAt(q.Id);
                    Assert.That(MathF.Abs(vent.X - q.X) < 1e-3f && MathF.Abs(vent.Z - q.Z) < 1e-3f, $"vent {q.Id} is the prop at ({vent.X:0.00},{vent.Z:0.00})");
                }
            }
            var lines = text.Split('\n').Where(l => l.StartsWith("room ")).ToList();
            Assert.That(lines.Where(l => !l.Contains(" hvac ")).All(l => l.Split(' ').Length <= 5), "a label with no vents saves the line it always did");
            // the query over the saved layout, from the storeys' own walls
            int asked = 0, found = 0;
            for (int f = 0; f < res.Floors; f++)
            {
                var found0 = RoomEnclosure.Find(PlanOf(res, f));
                foreach (var g in res.Layout.Where(g => g.Floor == f))
                {
                    float x = (g.X0 + g.X1) * 0.5f * Cell, z = (g.Z0 + g.Z1) * 0.5f * Cell;
                    asked++;
                    var h = RoomDesignations.HvacAt(found0, rooms, x, z, f);
                    if (g.Hvac == null) Assert.That(h, Is.Null, $"{g}: no HVAC, none found");
                    else
                    {
                        Assert.That(h, Is.Not.Null, $"{g}: its record found from ({x:0.0},{z:0.0})");
                        Assert.That(h.Vents.Select(v => v.Id), Is.EqualTo(g.Hvac.Vents.Select(v => v.Id)), $"{g}: its own record");
                        Assert.That(RoomDesignations.HasHvacAt(found0, rooms, x, z, f));
                        found++;
                    }
                }
            }
            TestContext.Progress.WriteLine($"[hvac-save] {rooms.Count(d => d.Hvac != null)} of {rooms.Count} labels carry a record through the save; HvacAt over the saved layout asked {asked} times, {found} records found");
            Assert.That(found, Is.GreaterThan(3));
            // a save from before, and blocks that will not read
            WallSave.Read(new[] { "room Kitchen 3 4", "room Bedroom 3 4 1 hvac 2 floor 0 3 4.25 2 90 wall 1 3.5 7.4 4 180", "room Hall 1 1 hvac 1 vent 0 1 0 1 0",
                                  "room Office 2 2 2 hvac 2 floor 0 1 0 1 0", "room LivingRoom 5 5 hvac 1 wall x 1 2 3 0" },
                          out _, out var old);
            Assert.That(old.Select(d => d.Kind), Is.EqualTo(new[] { RoomKind.Kitchen, RoomKind.Bedroom, RoomKind.Hall, RoomKind.Office, RoomKind.LivingRoom }), "every label kept");
            Assert.That(old.Select(d => d.Floor), Is.EqualTo(new[] { 0, 1, 0, 2, 0 }), "and its storey");
            Assert.That(old.Count(d => d.Hvac != null), Is.EqualTo(1), "only the readable block a record");
            Assert.That(old[1].Hvac.FloorRegisters.Single(), Is.EqualTo(new HvacVent(HvacVentKind.FloorRegister, 0, 3f, 4.25f, 2f, 90f)));
            Assert.That(old[1].Hvac.WallGrilles.Single(), Is.EqualTo(new HvacVent(HvacVentKind.WallGrille, 1, 3.5f, 7.4f, 4f, 180f)));
        }
    }
}
