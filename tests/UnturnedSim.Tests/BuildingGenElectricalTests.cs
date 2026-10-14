using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // Round 12, household electrical (strawberry: "light switches, and outlets. the outlets should act as power io outlets
    // 1500w each. light switches should function to switch on/off the ceiling lights within a room"; astraclaw's art and
    // placement notes, notes/household_electrical_manifest.json). Read off the generated walls and pieces, with numbers of
    // the test's own -- over the sweep's first seeds only (strawberry's standing rule: no suite runs; this is the one
    // targeted check of the new rules).
    public partial class BuildingGenTests
    {
        // AH: a switch hangs by a doorway of its own room, on its wall, at 1.4 m (1.67 over a worktop), clear of every door's
        // swing (both sides of a double-acting door) and every window; an outlet low at 0.4 m with nothing before it, or at
        // 1.67 over a worktop, never behind anything tall, 1.5 m from the room's next; and the room's record says so -- its
        // switches and outlets by Id (their place in BuildingGenResult.Electrical), each outlet 1500 W, its ceiling lights
        // exactly its own Ceiling_Lamp pieces by their place among the building's -- and survives a save, its state too.
        // BREAK IT: let a switch hang in a door's swing; give a room the next room's lights; drop the elec block from a save.
        [Test]
        public void ElectricalFixturesFollowTheRules()
        {
            var bad = new List<string>();
            int buildings = 0, lit = 0, switched = 0, switches = 0, outlets = 0, overCounter = 0, saved = 0;
            foreach (var spec in Specs().Where(s => s.Seed < 4))
            {
                var res = BuildingGen.Generate(spec);
                buildings++;
                var lamps = res.Furniture.Where(m => m.Ceiling).ToList();
                for (int f = 0; f < res.Floors; f++)
                {
                    var walls = WallsOn(res, f).ToList();
                    var leaves = walls.SelectMany(w => w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage).SelectMany(o => LeafBoxes(w, o))).ToList();
                    foreach (var m in res.Electrical.Where(m => m.Floor == f))
                    {
                        string nm = $"{Name(spec)}: {m}";
                        var fp = m.Footprint;
                        float h = m.Y - f * Storey;
                        if (!m.Mounted) bad.Add($"{nm} is not hung");
                        if (leaves.Any(l => Overlap(fp, l))) bad.Add($"{nm} is in a door's swing");
                        foreach (var w in NearWindows(res, m)) bad.Add($"{nm} is {w}");
                        if (RoomAt(res, f, m.X + m.Front.X * 0.3f, m.Z + m.Front.Z * 0.3f) != m.Room) bad.Add($"{nm} does not face into its room");
                        if (m.Name == "Light_Switch_0")
                        {
                            switches++;
                            if (MathF.Abs(h - BuildingGen.SwitchHeight) > 0.01f && MathF.Abs(h - BuildingGen.OutletOverCounter) > 0.01f) bad.Add($"{nm} hangs at {h:0.00} m");
                            // by a doorway of its room: within 2 m of one of its jambs (the generator keeps 1.2 past 0.15 off it)
                            var jambs = walls.SelectMany(w => w.Openings.Where(o => o.V < 0.05f).SelectMany(o => new[] { At(w, o.U), At(w, o.U1) })).ToList();
                            if (!jambs.Any(j => MathF.Sqrt((j.X - m.X) * (j.X - m.X) + (j.Z - m.Z) * (j.Z - m.Z)) < 2.0f)) bad.Add($"{nm} is by no doorway");
                        }
                        else
                        {
                            outlets++;
                            if (MathF.Abs(h - BuildingGen.OutletHeight) > 0.01f && MathF.Abs(h - BuildingGen.OutletOverCounter) > 0.01f) bad.Add($"{nm} hangs at {h:0.00} m");
                            if (h > 1f) overCounter++;
                            var (fx, fz) = m.Front;
                            (float, float, float, float) before = (fx < 0 ? fp.X0 - 0.8f : fp.X0, fz < 0 ? fp.Z0 - 0.8f : fp.Z0, fx > 0 ? fp.X1 + 0.8f : fp.X1, fz > 0 ? fp.Z1 + 0.8f : fp.Z1);
                            // (what is tall and comes up past its bottom: a worktop under an outlet over it hides nothing)
                            float bottom = h - FurnitureCatalog.Sizes[m.Name].H;
                            foreach (var o in res.Furniture.Where(o => o.Floor == f && o.On == null && !o.Mounted && !o.Ceiling && FurnitureCatalog.Sizes[o.Name].H > BuildingGen.HidesOutlet
                                                                       && FurnitureCatalog.Sizes[o.Name].H > bottom && Overlap(before, o.Footprint, 0.03f)))
                                bad.Add($"{nm} is hidden behind {o.Name}");
                            foreach (var o in res.Electrical.Where(o => o != m && o.Room == m.Room && o.Name == m.Name))
                                if (MathF.Sqrt((o.X - m.X) * (o.X - m.X) + (o.Z - m.Z) * (o.Z - m.Z)) < BuildingGen.OutletApart - 0.01f) bad.Add($"{nm} is {o} 's neighbour");
                        }
                    }
                }
                // the records
                foreach (var g in res.Layout)
                {
                    var own = lamps.Select((m, k) => (m, k)).Where(q => q.m.Room == g).Select(q => q.k).ToList();
                    if (own.Count > 0) lit++;
                    var e = g.Electrical;
                    var placed = res.Electrical.Select((m, k) => (m, k)).Where(q => q.m.Room == g).ToList();
                    if (e == null) { if (placed.Count > 0) bad.Add($"{Name(spec)}: {g} has fixtures and no record"); continue; }
                    if (e.Switches.Count > 0) switched++;
                    if (!e.Lights.SequenceEqual(own)) bad.Add($"{Name(spec)}: {g}'s lights are [{string.Join(",", e.Lights)}], its own [{string.Join(",", own)}]");
                    if (!e.LightsOn) bad.Add($"{Name(spec)}: {g}'s lights are generated off");
                    var ids = e.Fixtures.Select(v => v.Id).OrderBy(i => i).ToList();
                    if (!ids.SequenceEqual(placed.Select(q => q.k))) bad.Add($"{Name(spec)}: {g}'s record names fixtures [{string.Join(",", ids)}], it has [{string.Join(",", placed.Select(q => q.k))}]");
                    foreach (var v in e.Fixtures.Where(v => v.Id >= 0 && v.Id < res.Electrical.Count))
                    {
                        var m = res.Electrical[v.Id];
                        if (MathF.Abs(m.X - v.X) > 1e-3f || MathF.Abs(m.Z - v.Z) > 1e-3f || (v.Kind == ElectricalKind.Switch) != (m.Name == "Light_Switch_0")) bad.Add($"{Name(spec)}: {g}'s {v.Kind} #{v.Id} is {m}");
                        if (v.Kind == ElectricalKind.Outlet && v.Watts != 1500f) bad.Add($"{Name(spec)}: {g}'s outlet #{v.Id} is {v.Watts} W");
                    }
                    if (!res.Rooms.Any(d => d.Floor == g.Floor && d.Kind == g.Kind && g.Holds(d.X, d.Z) && d.Electrical == e)) bad.Add($"{Name(spec)}: {g}'s label does not carry its record");
                }
                // a save keeps every room's record, the lights' state with it
                var flip = res.Rooms.FirstOrDefault(d => d.Electrical != null && d.Electrical.Switches.Count > 0).Electrical;
                if (flip != null) flip.LightsOn = false;
                string text = WallSave.Write(res.Walls, res.Kind, res.Rooms, res.DoorMarks, res.Props);
                WallSave.Read(text.Split('\n'), out _, out var rooms, out _, out var props);
                if (props.Count(p => p.Name is "Light_Switch_0" or "Power_Outlet_0") != res.Electrical.Count) bad.Add($"{Name(spec)}: the save lost fixtures");
                foreach (var (a, b) in res.Rooms.Zip(rooms))
                {
                    if (a.Electrical == null) { if (b.Electrical != null) bad.Add($"{Name(spec)}: a record from nowhere"); continue; }
                    saved++;
                    var x = a.Electrical; var y = b.Electrical;
                    static bool Same(ElectricalFixture u, ElectricalFixture v) => u.Kind == v.Kind && u.Id == v.Id && MathF.Abs(u.X - v.X) < 0.01f && MathF.Abs(u.Y - v.Y) < 0.01f
                                                                                 && MathF.Abs(u.Z - v.Z) < 0.01f && MathF.Abs(u.Yaw - v.Yaw) < 0.01f && u.Watts == v.Watts;
                    if (y == null || y.LightsOn != x.LightsOn || !y.Lights.SequenceEqual(x.Lights) || y.Fixtures.Count() != x.Fixtures.Count()
                        || y.Fixtures.Zip(x.Fixtures).Any(t => !Same(t.First, t.Second)))
                        bad.Add($"{Name(spec)}: {a.Kind}'s record did not survive the save: {x} -> {y?.ToString() ?? "none"}");
                }
                if (flip != null) flip.LightsOn = true;
            }
            TestContext.Progress.WriteLine($"[electrical] {buildings} buildings; {switched} of {lit} rooms with ceiling lights have a switch ({switches} switches), {outlets} outlets "
                                           + $"({overCounter} over a worktop); {saved} records through a save; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(switched, Is.GreaterThan(lit * 9 / 10), "nine rooms in ten with ceiling lights have a switch");
            Assert.That(new[] { switches, outlets, overCounter, saved }, Has.All.GreaterThan(20));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }
    }
}
