using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // Round 12c, the décor expansion (strawberry, through astraclaw's handoffs): the new shelves, the full-height mirror and
    // the deer's head registered and hung; curtains and rugs built at load (game/RuntimeDecorGenerators) placed by the
    // generator (FurnishTextiles.cs) and saved with what they are built from. Every check reads the walls, the openings and
    // the furniture as generated -- not the generator's own reasons for them -- with its own, looser, numbers.
    public partial class BuildingGenTests
    {
        /// <summary>A storey's ceiling over a point: the floor above's underside, a flat roof's (it sits on the wall heads), or
        /// under a pitched roof the lower of the wall heads and its underside (its plane less half its thickness along the
        /// slope: a surface is built about its plane) -- a top storey has no ceiling under its roof but its walls' heads.</summary>
        static float CeilingAt(BuildingGenResult r, int f, float x, float z)
        {
            if (f < r.Floors - 1) return (f + 1) * Storey - BuildingGen.Slab;
            if (r.Roof == RoofKind.Flat) return (f + 1) * Storey;
            float h = BuildingGen.RoofTopAt(r, x, z);
            return float.IsNaN(h) ? (f + 1) * Storey : MathF.Min((f + 1) * Storey, h - BuildingGen.Slab * 0.5f / MathF.Cos(r.RoofPitch * MathF.PI / 180f));
        }

        /// <summary>A curtain's run along its wall and out of it, as it hangs: its rail end to end, from the wall to its
        /// tieback; and its heights, hem to the rail's caps, over its floor.</summary>
        static (float Hem, float ClothTop, float Top) CurtainSpan(FurniturePlan c)
        {
            float rail = c.Y - c.Floor * Storey;
            float clothTop = rail - RuntimeDecor.CurtainRailOverCloth;
            return (clothTop - c.Gen.Height, clothTop, rail + RuntimeDecor.CurtainCapOverRail);
        }

        // W: curtains (astraclaw's runtime factory, placed by the generator): each at a glazed window of a living room,
        // bedroom or dining room of a home, its back on the wall the window is in, facing into its room, its fabric over the
        // whole opening and 0.05-0.2 m past each jamb, the rail and its fittings on that room's wall all along; its rail over
        // the window's head where the ceiling leaves room -- the cloth's top at the head or above it -- and its caps under the
        // ceiling (a storey under another has its window's head AT the ceiling: the rail there is as high as it goes); down
        // to the floor, to the sill's apron, or to just over what stands in front of the window, never lower than what stands
        // there; into no piece standing, hung or hanging from the ceiling, no stair, no door's leaf, nor (down to the floor)
        // onto a doorway's walk. And they are there, each kind of drop and both states.
        // BREAK IT: let a curtain hang into what stands in front of its window; let its rail into the floor above; let it
        // run past the wall's end; hang one in a kitchen.
        [Test]
        public void CurtainsHangAtTheirWindows()
        {
            var bad = new List<string>();
            int curtains = 0, full = 0, sill = 0, over = 0, above = 0, ceilinged = 0, open = 0, windows = 0, rooms = 0, roomsWith = 0;
            foreach (var (spec, res) in All())
            {
                bool home = res.Kind is BuildingKind.Residential or BuildingKind.Apartment;
                foreach (var g in res.Layout.Where(g => home && BuildingGen.CurtainRooms.ContainsKey(g.Kind)))
                {
                    rooms++;
                    if (res.RuntimeDecor.Any(c => c.Room == g && c.Gen.Kind == RuntimeDecorKind.Curtain)) roomsWith++;
                }
                for (int f = 0; f < res.Floors; f++)
                {
                    var walls = WallsOn(res, f).ToList();
                    var boxes = walls.Select(Box).ToList();
                    windows += walls.Sum(w => w.Openings.Count(o => o.Glazed && o.Archetype is BuildingGen.ArchWindow or BuildingGen.ArchTallWindow));
                    var stairs = StairsAndTiles(res, f);
                    var leaves = walls.SelectMany(w => w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage).Select(o => LeafBox(w, o))).ToList();
                    // a doorway's walk: 1.2 m out of either face, across the opening
                    var walks = new List<(float X0, float Z0, float X1, float Z1)>();
                    foreach (var w in walls)
                        foreach (var o in w.Openings.Where(o => o.V < 0.05f))
                        {
                            var n = Normal(w); var p = At(w, o.U); var q = At(w, o.U1); float d = w.Thickness * 0.5f + 1.2f;
                            walks.Add((MathF.Min(p.X, q.X) - MathF.Abs(n.X) * d, MathF.Min(p.Z, q.Z) - MathF.Abs(n.Z) * d, MathF.Max(p.X, q.X) + MathF.Abs(n.X) * d, MathF.Max(p.Z, q.Z) + MathF.Abs(n.Z) * d));
                        }
                    var here = res.Furniture.Where(m => m.Floor == f).ToList();
                    var mine = res.RuntimeDecor.Where(c => c.Floor == f && c.Gen.Kind == RuntimeDecorKind.Curtain).ToList();
                    foreach (var c in mine)
                    {
                        curtains++;
                        if (c.Gen.Open) open++;
                        string nm = $"{Name(spec)}: curtain at ({c.X:0.00},{c.Z:0.00}) yaw {c.Yaw:0} in {c.Room}";
                        if (!home || !BuildingGen.CurtainRooms.ContainsKey(c.Room.Kind)) bad.Add($"{nm}: not a home's living room, bedroom or dining room");
                        if (c.Gen.Version != RuntimeDecor.CurrentVersion || c.Gen.Width < RuntimeDecor.CurtainMin || c.Gen.Height < RuntimeDecor.CurtainMin || !RuntimeDecor.CurtainTints.Contains(c.Gen.Style))
                            bad.Add($"{nm}: built from {c.Gen}");
                        var (fx, fz) = c.Front; var (rx, rz) = c.Right;
                        // the window it hangs at: in a wall behind it, square to it, its middle within the fabric's run
                        float half = c.Gen.Width * 0.5f;
                        WallPlan win = null; WallOpening wo = default;
                        foreach (var w in walls)
                        {
                            var d = Dir(w);
                            if (MathF.Abs(d.X * rx + d.Z * rz) < 0.99f) continue;
                            foreach (var o in w.Openings.Where(o => o.V >= 0.05f))
                            {
                                var mid = At(w, o.U + o.Width * 0.5f);
                                float along = (mid.X - c.X) * rx + (mid.Z - c.Z) * rz, back = (mid.X - c.X) * fx + (mid.Z - c.Z) * fz;
                                if (MathF.Abs(along) < half && back < 0f && back > -w.Thickness * 0.5f - 0.1f) { win = w; wo = o; }
                            }
                        }
                        if (win == null) { bad.Add($"{nm}: at no window"); continue; }
                        if (!wo.Glazed || wo.Archetype is not (BuildingGen.ArchWindow or BuildingGen.ArchTallWindow)) bad.Add($"{nm}: at an opening of archetype {wo.Archetype}");
                        // its back on that wall's face toward its room, facing into the room
                        var wd = Dir(win);
                        var n0 = Normal(win);
                        float faceOff = ((c.X - win.X) * n0.X + (c.Z - win.Z) * n0.Z);
                        if (MathF.Abs(MathF.Abs(faceOff) - win.Thickness * 0.5f) > 0.05f) bad.Add($"{nm}: its back {MathF.Abs(faceOff) - win.Thickness * 0.5f:0.00} m off the wall's face");
                        if (RoomAt(res, f, c.X + fx * 0.5f, c.Z + fz * 0.5f) != c.Room) bad.Add($"{nm}: faces into {RoomAt(res, f, c.X + fx * 0.5f, c.Z + fz * 0.5f)?.ToString() ?? "no room"}");
                        // over the whole opening, 0.05-0.2 m past each jamb
                        float cu = (c.X - win.X) * wd.X + (c.Z - win.Z) * wd.Z;
                        float past0 = wo.U - (cu - half), past1 = cu + half - wo.U1;
                        if (past0 < 0.05f - 1e-3f || past1 < 0.05f - 1e-3f || past0 > 0.2f + 1e-3f || past1 > 0.2f + 1e-3f) bad.Add($"{nm}: its fabric runs {past0:0.00} and {past1:0.00} m past the jambs");
                        // the rail and its fittings on its room's wall all along: wall within 0.15 m behind, the room's floor in front
                        float reach = half + RuntimeDecor.CurtainRailOver;
                        for (float a = -reach + 0.03f; a <= reach - 0.03f; a += 0.1f)
                        {
                            float px = c.X + rx * a, pz = c.Z + rz * a;
                            if (!boxes.Any(b => px - fx * 0.15f >= b.X0 && px - fx * 0.15f <= b.X1 && pz - fz * 0.15f >= b.Z0 && pz - fz * 0.15f <= b.Z1)) { bad.Add($"{nm}: no wall behind its rail {a:0.00} m along"); break; }
                            if (boxes.Any(b => px + fx * 0.05f > b.X0 + 0.005f && px + fx * 0.05f < b.X1 - 0.005f && pz + fz * 0.05f > b.Z0 + 0.005f && pz + fz * 0.05f < b.Z1 - 0.005f)) { bad.Add($"{nm}: its rail in a wall {a:0.00} m along"); break; }
                            if (RoomAt(res, f, px + fx * 0.2f, pz + fz * 0.2f) != c.Room) { bad.Add($"{nm}: its rail over {RoomAt(res, f, px + fx * 0.2f, pz + fz * 0.2f)?.ToString() ?? "no room"} {a:0.00} m along"); break; }
                        }
                        // the rail: over the head where the ceiling leaves room, the caps under the ceiling
                        var (hem, clothTop, top) = CurtainSpan(c);
                        float sillAt = win.Y + wo.V - f * Storey, head = sillAt + wo.Height;
                        float ceiling = new[] { -reach, 0f, reach }.Min(a => CeilingAt(res, f, c.X + rx * a + fx * 0.14f, c.Z + rz * a + fz * 0.14f)) - f * Storey;
                        if (top > ceiling - 0.01f) bad.Add($"{nm}: its rail's caps at {top:0.00} m, the ceiling at {ceiling:0.00}");
                        if (clothTop >= head - 1e-3f) above++;
                        else if (top >= ceiling - 0.05f) ceilinged++;
                        else bad.Add($"{nm}: its cloth starts {head - clothTop:0.00} m under the window's head with room over it (rail caps {top:0.00}, ceiling {ceiling:0.00})");
                        // its drop: to the floor, to the sill's apron, or to just over what stands in front -- in the window's lower third
                        if (hem < 0.1f) full++;
                        else if (MathF.Abs(hem - (sillAt - 0.1f)) < 0.02f) sill++;
                        else if (hem > sillAt - 0.1f && hem <= sillAt + wo.Height / 3f + 0.01f) over++;
                        else bad.Add($"{nm}: its hem at {hem:0.00} m, the sill at {sillAt:0.00}");
                        // into nothing: its box (the rail's run, out of the wall to the tieback, hem to caps) against every piece
                        var fp = c.Footprint;
                        foreach (var o in here)
                        {
                            float y = o.Y - f * Storey, h = FurnitureCatalog.Sizes[o.Name].H;
                            var (h0, h1) = o.Mounted ? (y - h, y + h) : o.Ceiling ? (y - (o.Name.Contains("Pendant") ? 0.95f : 0.28f), y) : (y, y + h);
                            if (h1 > hem && h0 < top && Overlap(fp, o.Footprint)) bad.Add($"{nm}: into {o.Name} at ({o.X:0.00},{o.Z:0.00}), {h0:0.00}-{h1:0.00} m, its hem at {hem:0.00}");
                        }
                        foreach (var o in mine.Where(o => o != c && Overlap(fp, o.Footprint))) bad.Add($"{nm}: into another curtain at ({o.X:0.00},{o.Z:0.00})");
                        if (stairs.Any(s => Overlap(fp, s))) bad.Add($"{nm}: over the stairs");
                        if (leaves.Any(l => Overlap(fp, l))) bad.Add($"{nm}: in a door's swing");
                        if (hem < 0.1f && walks.Any(l => Overlap(fp, l, 0.05f))) bad.Add($"{nm}: down to the floor in a doorway's walk");
                    }
                }
            }
            TestContext.Progress.WriteLine($"[curtains] {curtains} curtains at {windows} glazed windows, in {roomsWith} of {rooms} rooms that take them: {full} to the floor, {sill} to the sill, "
                                           + $"{over} over what stands in front; {above} with the rail over the window, {ceilinged} at the ceiling (a storey under another); {open} open; "
                                           + $"{bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(curtains, Is.GreaterThan(500));
            Assert.That(new[] { full, sill, over, above, ceilinged, open, curtains - open }, Has.All.GreaterThan(10), "every kind of drop and both states, both rails");
            Assert.That(roomsWith, Is.GreaterThan(rooms / 2), "most rooms that take curtains have them");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // X: rugs (astraclaw's runtime factory): in a home's living room under the front of the couch, in a bedroom under the
        // bed or beside it; flat on its floor, square to the room, wholly on its own room's floor and 0.15 m off every wall
        // (the generator keeps 0.2), off the stairs, a flight's ends and every door's swing, under no piece but the one it goes
        // with (and a living room's set's table). And they are there, in every pattern.
        // BREAK IT: let a rug run under a door's swing; into a wall; under the bookcase; drop the anchor.
        [Test]
        public void RugsLieUnderTheirPieces()
        {
            var bad = new List<string>();
            int rugs = 0, under = 0, beside = 0, rooms = 0, roomsWith = 0;
            var styles = new Dictionary<string, int>();
            foreach (var (spec, res) in All())
            {
                bool home = res.Kind is BuildingKind.Residential or BuildingKind.Apartment;
                foreach (var g in res.Layout.Where(g => home && BuildingGen.RugRooms.ContainsKey(g.Kind)))
                {
                    rooms++;
                    if (res.RuntimeDecor.Any(c => c.Room == g && c.Gen.Kind == RuntimeDecorKind.Rug)) roomsWith++;
                }
                for (int f = 0; f < res.Floors; f++)
                {
                    var boxes = WallsOn(res, f).Select(Box).ToList();
                    var stairs = StairsAndTiles(res, f);
                    var leaves = WallsOn(res, f).SelectMany(w => w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage).Select(o => LeafBox(w, o))).ToList();
                    foreach (var r in res.RuntimeDecor.Where(c => c.Floor == f && c.Gen.Kind == RuntimeDecorKind.Rug))
                    {
                        rugs++;
                        styles[r.Gen.Style] = styles.GetValueOrDefault(r.Gen.Style) + 1;
                        string nm = $"{Name(spec)}: rug at ({r.X:0.00},{r.Z:0.00}) {r.Gen.Width:0.00} x {r.Gen.Height:0.00} in {r.Room}";
                        if (!home || !BuildingGen.RugRooms.ContainsKey(r.Room.Kind)) bad.Add($"{nm}: not a home's living room or bedroom");
                        if (MathF.Abs(r.Y - f * Storey) > 1e-3f) bad.Add($"{nm}: at {r.Y - f * Storey:0.00} m, not on its floor");
                        if (MathF.Abs(r.Yaw / 90f - MathF.Round(r.Yaw / 90f)) > 1e-3f) bad.Add($"{nm}: turned {r.Yaw:0.0}, not square to the room");
                        if (r.Gen.Width < 0.8f || r.Gen.Height < 0.8f || !RuntimeDecor.RugStyles.Contains(r.Gen.Style) || r.Gen.Version != RuntimeDecor.CurrentVersion) bad.Add($"{nm}: built from {r.Gen}");
                        var fp = r.Footprint;
                        // wholly on its own room's floor, 0.15 m off every wall
                        var g15 = Grown(fp, 0.15f);
                        for (float x = g15.X0; x <= g15.X1 + 1e-3f; x += 0.1f)
                            for (float z = g15.Z0; z <= g15.Z1 + 1e-3f; z += 0.1f)
                            {
                                if (RoomAt(res, f, x, z) != r.Room) { bad.Add($"{nm}: within 0.15 m of {RoomAt(res, f, x, z)?.ToString() ?? "no room"} at ({x:0.00},{z:0.00})"); goto next; }
                                if (boxes.Any(b => x > b.X0 && x < b.X1 && z > b.Z0 && z < b.Z1)) { bad.Add($"{nm}: within 0.15 m of a wall at ({x:0.00},{z:0.00})"); goto next; }
                            }
                        next:
                        if (stairs.Any(s => Overlap(fp, s))) bad.Add($"{nm}: on the stairs or a flight's end");
                        if (leaves.Any(l => Overlap(fp, l))) bad.Add($"{nm}: under a door's swing");
                        // under its piece, or beside it; under nothing else standing
                        var anchor = res.Furniture.FirstOrDefault(m => m.Room == r.Room && m.On == null && !m.Mounted && !m.Ceiling && m.Name.StartsWith(r.Room.Kind == RoomKind.Bedroom ? "Bed" : "Couch"));
                        if (anchor == null) { bad.Add($"{nm}: no {(r.Room.Kind == RoomKind.Bedroom ? "bed" : "couch")} to go with"); continue; }
                        var set = res.Furniture.Where(m => m.Room == r.Room && m.On != null && m.Name.StartsWith("Television")).Select(m => m.On).ToHashSet();
                        if (Overlap(fp, anchor.Footprint)) under++;
                        else if (r.Room.Kind == RoomKind.Bedroom && Overlap(Grown(fp, 0.1f), anchor.Footprint, 0f)) beside++;
                        else bad.Add($"{nm}: neither under nor beside its {anchor.Name}");
                        foreach (var o in res.Furniture.Where(o => o.Floor == f && !o.Mounted && !o.Ceiling && o.On == null && o != anchor && !set.Contains(o) && Overlap(fp, o.Footprint)))
                            bad.Add($"{nm}: under {o.Name} at ({o.X:0.00},{o.Z:0.00})");
                    }
                }
            }
            TestContext.Progress.WriteLine($"[rugs] {rugs} rugs in {roomsWith} of {rooms} rooms that take them: {under} under their piece, {beside} beside it; "
                                           + $"patterns {string.Join(", ", styles.OrderBy(k => k.Key).Select(kv => $"{kv.Key} {kv.Value}"))}; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(rugs, Is.GreaterThan(300));
            Assert.That(roomsWith, Is.GreaterThan(rooms / 2), "most rooms that take a rug have one");
            Assert.That(RuntimeDecor.RugStyles.All(s => styles.GetValueOrDefault(s) > 10), "every pattern is laid");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // Y: the curtains and rugs draw nothing from the furniture's dice and move nothing: each building generated with them
        // and without (BuildingGen.NoRuntimeDecor), every piece of Furniture -- floor pieces, the clocks, the décor -- comes out
        // identical, in the same order; and none of them is in Furniture, which every rule reads as solid catalog props.
        // BREAK IT: draw once from the Furnisher's shared Rng in Curtains; add the rugs to Furniture.
        [Test]
        public void RuntimeDecorMovesNothing()
        {
            var specs = new List<BuildingGenSpec>();
            foreach (var kind in new[] { BuildingKind.Residential, BuildingKind.Apartment, BuildingKind.Commercial })
                for (int seed = 0; seed < (kind == BuildingKind.Apartment ? 3 : 10); seed++)
                {
                    specs.Add(new BuildingGenSpec { Seed = seed, Kind = kind });
                    specs.Add(new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 10, MaxFloors = 3, MaxRoomsPerFloor = 4 });
                }
            var bad = new List<string>();
            int compared = 0, runtime = 0;
            string Key(FurniturePlan m) => FormattableString.Invariant($"{m.Name} {m.X:R} {m.Y:R} {m.Z:R} {m.Yaw:R} {m.Floor} {m.Mounted} {m.Ceiling} {m.On?.Name}");
            try
            {
                foreach (var spec in specs)
                {
                    BuildingGen.NoRuntimeDecor = true;
                    var plain = BuildingGen.Generate(spec);
                    BuildingGen.NoRuntimeDecor = false;
                    var dressed = BuildingGen.Generate(spec);
                    compared += plain.Furniture.Count; runtime += dressed.RuntimeDecor.Count;
                    if (plain.RuntimeDecor.Count != 0) bad.Add($"{Name(spec)}: {plain.RuntimeDecor.Count} curtains and rugs with them off");
                    if (dressed.Furniture.Count != plain.Furniture.Count) bad.Add($"{Name(spec)}: {plain.Furniture.Count} pieces without curtains and rugs, {dressed.Furniture.Count} with");
                    else
                        for (int i = 0; i < plain.Furniture.Count; i++)
                            if (Key(plain.Furniture[i]) != Key(dressed.Furniture[i])) { bad.Add($"{Name(spec)}: piece {i} {Key(plain.Furniture[i])} became {Key(dressed.Furniture[i])}"); break; }
                    if (dressed.Furniture.Any(m => m.Gen != null)) bad.Add($"{Name(spec)}: a runtime piece in Furniture");
                    if (dressed.RuntimeDecor.Any(m => m.Gen == null)) bad.Add($"{Name(spec)}: a catalog prop among the runtime pieces");
                }
            }
            finally { BuildingGen.NoRuntimeDecor = false; }
            TestContext.Progress.WriteLine($"[runtime-decor-draws] {compared} pieces compared over {specs.Count} buildings, {runtime} curtains and rugs besides; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(compared, Is.GreaterThan(1000));
            Assert.That(runtime, Is.GreaterThan(100));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // Z: a curtain or a rug is its inputs -- there is no mesh on disk -- so a save keeps them all and a load gets them back:
        // kind, version, both sizes, open or closed, tint or pattern, where and which way. A plain prop saves exactly the line
        // it always did; a save from before them loads every piece as a prop; a block that cannot be built from (a kind by its
        // number, a size that is not one) drops that piece and keeps the building.
        // BREAK IT: drop the gen block on write; read the open flag from the wrong token; write the kind as its number.
        [Test]
        public void RuntimeDecorSurvivesASave()
        {
            var res = BuildingGen.Generate(new BuildingGenSpec { Seed = 16, Kind = BuildingKind.Residential });
            Assert.That(res.RuntimeDecor.Count(m => m.Gen.Kind == RuntimeDecorKind.Curtain), Is.GreaterThan(1), "the case has curtains");
            Assert.That(res.RuntimeDecor.Any(m => m.Gen.Kind == RuntimeDecorKind.Rug), "and a rug");
            Assert.That(res.RuntimeDecor.Select(m => m.Gen.Open).Distinct().Count(), Is.EqualTo(2), "and curtains open and closed");
            string text = WallSave.Write(res.Walls, res.Kind, res.Rooms, res.DoorMarks, res.Props);
            WallSave.Read(text.Split('\n'), out _, out _, out _, out var props);
            var want = res.Furniture.Concat(res.RuntimeDecor).Concat(res.HvacVents).Concat(res.Electrical).ToList();
            Assert.That(props.Count, Is.EqualTo(want.Count), "every piece comes back");
            for (int i = 0; i < want.Count; i++)
            {
                var (p, m) = (props[i], want[i]);
                Assert.That(p.Name == m.Name && MathF.Abs(p.X - m.X) < 1e-3f && MathF.Abs(p.Y - m.Y) < 1e-3f && MathF.Abs(p.Z - m.Z) < 1e-3f && MathF.Abs(p.Yaw - m.Yaw) < 1e-3f, $"piece {i} {m} where it was");
                if (m.Gen == null) { Assert.That(p.Gen, Is.Null, $"{m.Name} stays a prop"); continue; }
                Assert.That(p.Gen, Is.Not.Null, $"{m.Name} keeps what it is built from");
                Assert.That((p.Gen.Kind, p.Gen.Version, p.Gen.Open, p.Gen.Style), Is.EqualTo((m.Gen.Kind, m.Gen.Version, m.Gen.Open, m.Gen.Style)), $"{m.Name} {i}");
                Assert.That(MathF.Abs(p.Gen.Width - m.Gen.Width) < 1e-3f && MathF.Abs(p.Gen.Height - m.Gen.Height) < 1e-3f, $"{m.Name} {i}: {p.Gen} for {m.Gen}");
            }
            // a plain prop's line is the one it always was: six fields; a runtime piece's carries its kind by NAME
            var lines = text.Split('\n').Where(l => l.StartsWith("prop ")).ToList();
            Assert.That(lines.Where(l => !l.Contains(" gen ")).All(l => l.Split(' ').Length == 6), "a plain prop saves its six fields, as before");
            Assert.That(lines.Count(l => l.Contains(" gen curtain ")) + lines.Count(l => l.Contains(" gen rug ")), Is.EqualTo(res.RuntimeDecor.Count), "the kind written by name");
            // a save from before: every piece a prop; and blocks that cannot be built from drop just their piece
            WallSave.Read(new[] { "prop Couch_0 1 0 2 90", "prop Curtain 1 3.89 2 0 gen 0 1 3 3 1 Linen", "prop Curtain 1 3.89 2 0 gen curtain 1 x 3 1 Linen",
                                  "prop Rug 1 0 2 0 gen rug 1 3 -2 0 Diamond", "prop Rug 1 0 2 0 gen carpet 1 3 2 0 Diamond", "prop Rug 4 0 5 90 gen rug 1 3.6 2.6 0 Checker" },
                          out _, out _, out _, out var old);
            Assert.That(old.Select(p => p.Name), Is.EqualTo(new[] { "Couch_0", "Rug" }), "the old prop, and the one buildable rug");
            Assert.That(old[0].Gen, Is.Null);
            Assert.That(old[1].Gen, Is.EqualTo(new RuntimeDecor(RuntimeDecorKind.Rug, 1, 3.6f, 2.6f, false, "Checker")));
        }

        // AA: astraclaw's expansion is registered under the GUIDs its manifests gave (decor_expansion_manifest.json,
        // notes/deer_wall_trophy_manifest.json) -- minting none: a layout stores a piece by GUID -- with its OBJ and texture
        // on disk, and the catalog's extents the manifest's bounds (FurnitureCatalogMatchesTheMeshes holds them to the OBJs
        // themselves); and every one hangs somewhere in the sweep.
        // BREAK IT: mint a fresh GUID for one; leave Wall_Shelf_1's extents at the old board's.
        [Test]
        public void DecorExpansionKeepsItsManifests()
        {
            string dir = Objects();
            Assert.That(dir, Is.Not.Null, "game/content/objects not found");
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir)));
            var byGuid = new Dictionary<string, string>();
            foreach (var p in File.ReadAllLines(Path.Combine(dir, "guid_mesh.txt")).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2))
                byGuid.TryAdd(p[0], p[1]);
            var bad = new List<string>();
            var rows = new List<(string Name, System.Text.Json.JsonElement Row)>();
            using var expansion = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "decor_expansion_manifest.json")));
            foreach (var p in expansion.RootElement.EnumerateObject()) rows.Add((p.Name, p.Value));
            using var deer = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "notes", "deer_wall_trophy_manifest.json")));
            rows.Add((deer.RootElement.GetProperty("name").GetString(), deer.RootElement));
            foreach (var (name, row) in rows)
            {
                string guid = row.GetProperty("guid").GetString();
                if (byGuid.GetValueOrDefault(guid) != name) bad.Add($"{name}: its manifest GUID {guid} loads {byGuid.GetValueOrDefault(guid) ?? "NOTHING"}");
                if (!File.Exists(Path.Combine(dir, name + ".obj"))) bad.Add($"{name}.obj is missing");
                if (!File.Exists(Path.Combine(dir, row.GetProperty("texture").GetString()))) bad.Add($"{name}: its texture is missing");
                if (!FurnitureCatalog.Sizes.TryGetValue(name, out var s)) { bad.Add($"{name}: no catalog extents"); continue; }
                var b = row.GetProperty("bounds_native");
                float F(int i, int j) => b[i][j].GetSingle();
                if (MathF.Abs(s.X0 - F(0, 0)) > 0.002f || MathF.Abs(s.X1 - F(1, 0)) > 0.002f || MathF.Abs(s.Y0 - F(0, 1)) > 0.002f || MathF.Abs(s.Y1 - F(1, 1)) > 0.002f || MathF.Abs(s.H - F(1, 2)) > 0.002f)
                    bad.Add($"{name}: catalog ({s.X0}, {s.X1}, {s.Y0}, {s.Y1}, {s.H}), manifest ({F(0, 0)}, {F(1, 0)}, {F(0, 1)}, {F(1, 1)}, {F(1, 2)})");
                // (hung pieces: centred up and down on the origin, the back at Y 0, the front +Y)
                if (MathF.Abs(F(0, 2) + F(1, 2)) > 0.002f || MathF.Abs(F(0, 1)) > 1e-4f) bad.Add($"{name}: not backed at Y 0 and centred in Z");
                if (!BuildingGen.Decor.Any(r => r.Name == name)) bad.Add($"{name}: no décor row hangs it");
            }
            TestContext.Progress.WriteLine($"[decor-expansion] {rows.Count} pieces held to their manifests; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(rows.Count, Is.EqualTo(10));
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        // AB: the full-height mirror has floor to stand on before it: BedAccess (the player's capsule's way) of its room's floor
        // in front of its face, across its width, no piece standing in it, off the stairs and every door's swing; and nothing
        // standing out of a wall more than 0.3 m hangs low enough to walk into -- the deer's head, 0.76 m out, over a standing
        // player's head (SDG.Unturned.PlayerMovementDef.HEIGHT_STAND).
        // BREAK IT: drop the mirror's stand check; hang the deer at the handoff's 2.1 m.
        [Test]
        public void TallMirrorAndTrophyKeepTheirSpace()
        {
            var byName = BuildingGen.Decor.ToDictionary(r => r.Name);
            var bad = new List<string>();
            int mirrors = 0, deep = 0, trophies = 0;
            foreach (var (spec, res) in All())
                for (int f = 0; f < res.Floors; f++)
                {
                    var stairs = StairsAndTiles(res, f);
                    // (every floor a leaf sweeps, LeafBoxes: both sides of a double-acting door, none of a pocket door's)
                    var leaves = WallsOn(res, f).SelectMany(w => w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage).SelectMany(o => LeafBoxes(w, o))).ToList();
                    var standing = res.Furniture.Where(o => o.Floor == f && !o.Mounted && !o.Ceiling && o.On == null).ToList();
                    foreach (var m in res.Furniture.Where(m => m.Floor == f && m.Mounted && byName.ContainsKey(m.Name)))
                    {
                        var row = byName[m.Name]; var s = FurnitureCatalog.Sizes[m.Name];
                        string nm = $"{Name(spec)}: {m.Name} at ({m.X:0.00},{m.Z:0.00}) yaw {m.Yaw:0} in {m.Room}";
                        if (s.Y1 - s.Y0 > 0.3f)
                        {
                            deep++;
                            if (m.Name.StartsWith("Deer")) trophies++;
                            float bottom = m.Y - f * Storey - s.H;
                            if (bottom < SDG.Unturned.PlayerMovementDef.HEIGHT_STAND) bad.Add($"{nm}: {s.Y1 - s.Y0:0.00} m out of the wall from {bottom:0.00} m up, a player is {SDG.Unturned.PlayerMovementDef.HEIGHT_STAND} m");
                        }
                        if (row.Stand <= 0f) continue;
                        mirrors++;
                        var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                        float ax = m.X + rx * s.X0 + fx * s.Y1, az = m.Z + rz * s.X0 + fz * s.Y1, bx = m.X + rx * s.X1 + fx * (s.Y1 + 0.75f), bz = m.Z + rz * s.X1 + fz * (s.Y1 + 0.75f);
                        var zone = (MathF.Min(ax, bx), MathF.Min(az, bz), MathF.Max(ax, bx), MathF.Max(az, bz));
                        for (float x = zone.Item1 + 0.05f; x < zone.Item3; x += 0.1f)
                            for (float z = zone.Item2 + 0.05f; z < zone.Item4; z += 0.1f)
                                if (RoomAt(res, f, x, z) != m.Room) { bad.Add($"{nm}: the floor before it is {RoomAt(res, f, x, z)?.ToString() ?? "no room"} at ({x:0.00},{z:0.00})"); goto stood; }
                        stood:
                        foreach (var o in standing.Where(o => Overlap(zone, o.Footprint))) bad.Add($"{nm}: {o.Name} stands before it");
                        if (stairs.Any(q => Overlap(zone, q))) bad.Add($"{nm}: the stairs before it");
                        if (leaves.Any(q => Overlap(zone, q))) bad.Add($"{nm}: a door swings before it");
                    }
                }
            TestContext.Progress.WriteLine($"[mirror-trophy] {mirrors} full-height mirrors with floor before them; {deep} pieces standing 0.3 m or more out of a wall ({trophies} deer heads) over a player's head; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(mirrors, Is.GreaterThan(20));
            Assert.That(trophies, Is.GreaterThan(3));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }
    }
}
