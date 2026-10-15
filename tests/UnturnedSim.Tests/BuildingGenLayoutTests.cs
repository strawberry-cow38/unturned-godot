using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // Round 12, the layout lane (strawberry): a room's layout alters its windows after the fact where it needs the wall,
    // decides which way its doors swing, and keeps the floor in front of a sink to stand at. Every check reads the WALLS
    // as generated -- the openings, their flags, where the furniture stands -- not the furnisher's own bookkeeping.
    public partial class BuildingGenTests
    {
        /// <summary>A hung door's leaf sweep as the game builds it: a quarter circle its own width round its hinge, out of
        /// the wall's front face when DoorOpensFront, else its back -- as a box across the whole opening.</summary>
        static (float X0, float Z0, float X1, float Z1) LeafBox(WallPlan w, WallOpening o, float from = 0f, float reach = float.NaN)
        {
            var n = Normal(w); float s = o.DoorOpensFront ? 1f : -1f;
            var p0 = At(w, o.U); var p1 = At(w, o.U1); float back = float.IsNaN(reach) ? w.Thickness * 0.5f + o.Width - 0.05f : reach;
            float ax = MathF.Min(p0.X, p1.X), az = MathF.Min(p0.Z, p1.Z), bx = MathF.Max(p0.X, p1.X), bz = MathF.Max(p0.Z, p1.Z);
            return (MathF.Min(ax + s * n.X * from, ax + s * n.X * back), MathF.Min(az + s * n.Z * from, az + s * n.Z * back),
                    MathF.Max(bx + s * n.X * from, bx + s * n.X * back), MathF.Max(bz + s * n.Z * from, bz + s * n.Z * back));
        }

        /// <summary>Every floor a hung door's leaf sweeps, as LeafBox: the side it opens to for a swing door, both sides for a
        /// double-acting one, none for a pocket door (it slides into its wall).</summary>
        static IEnumerable<(float X0, float Z0, float X1, float Z1)> LeafBoxes(WallPlan w, WallOpening o)
        {
            if (o.DoorAction == DoorAction.Pocket) yield break;
            yield return LeafBox(w, o);
            if (o.DoorAction == DoorAction.DoubleSwing) { var p = o; p.DoorOpensFront = !o.DoorOpensFront; yield return LeafBox(w, p); }
        }

        /// <summary>What stands in the way on floor f: the flights, landings and holes, and the tiles at the flights' ends.</summary>
        static List<(float X0, float Z0, float X1, float Z1)> StairsAndTiles(BuildingGenResult res, int f)
        {
            var l = res.Stairs.Where(st => st.Floor == f).Select(Foot)
                .Concat(res.Walls.Where(w => w.Kind == SurfaceKind.Stairs && FloorOf(w) == f).Select(Plan))
                .Concat(Holes(res, f)).ToList();
            foreach (var st in res.Stairs)
            {
                var (bx, bz) = st.Back; var (rx, rz) = st.Right;
                (float, float, float, float) T(float ox, float oz, float dir)
                {
                    float ex = ox + bx * Cell * dir + rx * st.Width, ez = oz + bz * Cell * dir + rz * st.Width;
                    return (MathF.Min(ox, ex), MathF.Min(oz, ez), MathF.Max(ox, ex), MathF.Max(oz, ez));
                }
                if (st.Floor == f && StartsOnFloor(st)) l.Add(T(st.X, st.Z, -1f));
                if (st.Floor == f - 1 && EndsOnFloor(st)) l.Add(T(st.X + bx * st.Run, st.Z + bz * st.Run, +1f));
            }
            return l;
        }

        static bool PrivateRoom(RoomKind k) => k is RoomKind.Bedroom or RoomKind.Bathroom or RoomKind.Closet or RoomKind.Office;

        // Door swing (strawberry: "allow room layouts to decide door swing direction"): every hung door opens INTO the
        // room it serves -- a bedroom's or a bathroom's into it, a room's off a hall or a stairwell into the room, a door to
        // outside into the building -- its leaf sweeping neither the stairs (nor the tile at a flight's end) nor a wall. A
        // preference is excused only where its side cannot take the leaf at all (stairs or a wall in the sweep), counted
        // and bounded. And the hinge does something: the leaf folds toward the nearer wall across its way.
        // BREAK IT: skip HangDoors (every door as it always hung: hinged at U1, opening to the wall's back).
        [Test]
        public void DoorsSwingIntoTheRoomsTheyServe()
        {
            var bad = new List<string>();
            int doors = 0, fronts = 0, starts = 0, intoPrivate = 0, offCirculation = 0, toOutside = 0, excused = 0, cornered = 0, others = 0;
            foreach (var (spec, res) in All())
                for (int f = 0; f < res.Floors; f++)
                {
                    var walls = WallsOn(res, f).ToList();
                    var stairs = StairsAndTiles(res, f);
                    foreach (var w in walls)
                        foreach (var o in w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage))
                        {
                            // (a door that swings both ways or slides has its own rules: DoorsSwingBothWaysOrSlideWhereTheyShould)
                            if (o.DoorAction != DoorAction.Swing) { others++; continue; }
                            doors++;
                            if (o.DoorOpensFront) fronts++;
                            if (o.DoorHingeAtStart) starts++;
                            var n = Normal(w); var c = At(w, o.U + o.Width * 0.5f); float d = w.Thickness * 0.5f + 0.3f;
                            GenRoom Side(bool front) { float s = front ? 1f : -1f; return RoomAt(res, f, c.X + s * n.X * d, c.Z + s * n.Z * d); }
                            // could a leaf open to this side at all: nothing of the stairs in its sweep, no wall. The leaf
                            // itself, as the game fits it: the clear span inside a 0.2 m frame at each jamb, turning about the
                            // wall's mid-plane (WallSurface.ClearOpeningSize, PlaceDoor) -- so it reaches its width less 0.4
                            // (only the wall that is there: what open plan cut out of one is open floor -- SolidBoxes)
                            var solid = walls.Where(x => x != w).SelectMany(SolidBoxes).ToList();
                            bool Takes(bool front) { var p = o; p.DoorOpensFront = front; var sw = LeafBox(w, p, w.Thickness * 0.5f + 0.05f, o.Width - 0.4f); return !stairs.Any(st => Overlap(sw, st)) && !solid.Any(b => Overlap(sw, b)); }
                            var into = Side(o.DoorOpensFront); var from = Side(!o.DoorOpensFront);
                            string at = $"{Name(spec)} floor {f}: the door {from?.Kind.ToString() ?? "outside"} -> {into?.Kind.ToString() ?? "outside"} at ({c.X:0.0},{c.Z:0.0})";
                            if (into == null) { bad.Add($"{at} opens out of the building"); continue; }
                            if (!Takes(o.DoorOpensFront)) bad.Add($"{at} sweeps the stairs or a wall");
                            if (from == null) toOutside++;
                            if (from != null && PrivateRoom(from.Kind) && !PrivateRoom(into.Kind))
                            { if (Takes(!o.DoorOpensFront)) bad.Add($"{at} opens out of the {from.Kind}"); else excused++; }
                            else if (from != null && into.Circulation && !from.Circulation)
                            { if (Takes(!o.DoorOpensFront)) bad.Add($"{at} opens into the {into.Kind}, not the {from.Kind}"); else excused++; }
                            if (PrivateRoom(into.Kind) && (from == null || !PrivateRoom(from.Kind))) intoPrivate++;
                            if (from != null && from.Circulation && !into.Circulation) offCirculation++;
                            // the hinge: where a wall stands across the leaf's way within a metre past one jamb and nothing
                            // within two past the other, the door hangs on the near one, so its open leaf rests against that wall
                            float Gap(bool atStart)
                            {
                                var dr = Dir(w); float sd = o.DoorOpensFront ? 1f : -1f, j = atStart ? o.U : o.U1, dir = atStart ? -1f : 1f;
                                for (float g = 0f; g <= 3f; g += 0.05f)
                                    foreach (float deep in new[] { 0.5f, 1.0f })
                                    {
                                        var p = At(w, j + dir * g); float px = p.X + sd * n.X * (w.Thickness * 0.5f + deep), pz = p.Z + sd * n.Z * (w.Thickness * 0.5f + deep);
                                        if (solid.Any(b => px >= b.X0 && px <= b.X1 && pz >= b.Z0 && pz <= b.Z1)) return g;
                                    }
                                return 3f;
                            }
                            float gs = Gap(true), ge = Gap(false);
                            if (MathF.Min(gs, ge) < 1f && MathF.Max(gs, ge) > 2f)
                            {
                                cornered++;
                                if (o.DoorHingeAtStart != (gs < ge)) bad.Add($"{at} hangs on its {(o.DoorHingeAtStart ? "start" : "end")} jamb, {(o.DoorHingeAtStart ? gs : ge):0.00} m from a wall across its way; the other is {(o.DoorHingeAtStart ? ge : gs):0.00} m from one");
                            }
                        }
                }
            TestContext.Progress.WriteLine($"[gen-swing] {doors} hung doors that swing one way ({others} more swing both ways or slide): {fronts} open to their wall's front, {starts} hinged at their start; {intoPrivate} into a private room, {offCirculation} off a hall or stairwell into a room, {toOutside} from outside in; {cornered} by a wall across one jamb, hung on that one; {excused} excused (the side they should open to cannot take a leaf); {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
            Assert.That(doors, Is.GreaterThan(2000));
            Assert.That(intoPrivate, Is.GreaterThan(500));
            Assert.That(offCirculation, Is.GreaterThan(300));
            // (both ways round, both jambs: a choice, not a constant)
            Assert.That(fronts, Is.InRange(doors / 10, doors - doors / 10));
            Assert.That(starts, Is.InRange(doors / 10, doors - doors / 10));
            Assert.That(excused, Is.LessThanOrEqualTo(doors / 50), "too many doors excused");
            Assert.That(cornered, Is.GreaterThan(200), "too few doors by a corner to measure the hinge");
        }

        // The swing is the opening's, so it saves: every combination of jamb and face writes and reads back, a door hung as
        // they always were writes no token at all (an old save is the same text), and a generated building's doors come
        // back from a save exactly as they were hung. And so does how it moves (round 12b): a door that swings both ways or
        // slides writes an "action" token and reads back as it was; a swing door writes none, so an old save -- which has
        // none -- reads back as swing doors; and a token it does not know is a swing door.
        // BREAK IT: drop the swing token from Write, or from Read; drop the action token from Write, or from Read.
        [Test]
        public void DoorSwingSurvivesASave()
        {
            var plan = new WallPlan { Length = 30f };
            foreach (var (start, front, u, act) in new[] { (false, false, 0.5f, DoorAction.Swing), (true, false, 4.5f, DoorAction.Swing), (false, true, 8.5f, DoorAction.Swing),
                                                             (true, true, 12.5f, DoorAction.Swing), (false, true, 16.5f, DoorAction.DoubleSwing), (true, false, 20.5f, DoorAction.Pocket),
                                                             (false, false, 24.5f, DoorAction.Pocket) })
                plan.Openings.Add(new WallOpening(u, 0f, 2.5f, 2.85f) { DoorProp = "Door_Pine", DoorHingeAtStart = start, DoorOpensFront = front, DoorAction = act });
            string text = WallSave.Write(new[] { plan });
            var back = WallSave.Read(text.Split('\n'))[0].Openings;
            Assert.That(back.Select(o => (o.DoorHingeAtStart, o.DoorOpensFront, o.DoorAction)), Is.EqualTo(plan.Openings.Select(o => (o.DoorHingeAtStart, o.DoorOpensFront, o.DoorAction))));
            Assert.That(text.Split('\n').Count(l => l.Contains(" swing ")), Is.EqualTo(5), "the default door writes no swing token");
            Assert.That(text.Split('\n').Count(l => l.Contains(" action ")), Is.EqualTo(3), "a swing door writes no action token");
            // a save from before the choice existed, and one from a newer editor
            Assert.That(WallSave.Read(new[] { WallSave.Header, "wall 0 0 0 0 9 0.5 0", "  open 1 0 2.5 2.85 999 0 0 0 0 0 0 Door_Pine 0 swing start front" })[0].Openings[0].DoorAction, Is.EqualTo(DoorAction.Swing));
            Assert.That(WallSave.Read(new[] { WallSave.Header, "wall 0 0 0 0 9 0.5 0", "  open 1 0 2.5 2.85 999 0 0 0 0 0 0 Door_Pine 0 action revolving" })[0].Openings[0].DoorAction, Is.EqualTo(DoorAction.Swing));

            int doors = 0, swung = 0, acted = 0;
            foreach (var (spec, res) in All().Where(x => x.Spec.Kind is BuildingKind.Residential or BuildingKind.Apartment).Take(60))
            {
                var walls = res.Walls.Where(w => w.Kind == SurfaceKind.Wall).ToList();
                var read = WallSave.Read(WallSave.Write(walls).Split('\n')).Where(w => w.Kind == SurfaceKind.Wall).ToList();
                Assert.That(read.Count, Is.EqualTo(walls.Count));
                for (int i = 0; i < walls.Count; i++)
                    for (int k = 0; k < walls[i].Openings.Count; k++)
                    {
                        var a = walls[i].Openings[k]; var b = read[i].Openings[k];
                        if (!a.HasDoor) continue;
                        doors++; if (a.DoorHingeAtStart || a.DoorOpensFront) swung++; if (a.DoorAction != DoorAction.Swing) acted++;
                        Assert.That((b.DoorHingeAtStart, b.DoorOpensFront, b.DoorAction), Is.EqualTo((a.DoorHingeAtStart, a.DoorOpensFront, a.DoorAction)), $"{Name(spec)}: a door's swing changed in a save");
                    }
            }
            TestContext.Progress.WriteLine($"[gen-swing-save] {doors} doors through a save, {swung} of them not hung the old way, {acted} swinging both ways or sliding");
            Assert.That(swung, Is.GreaterThan(50));
            Assert.That(acted, Is.GreaterThan(20));
        }

        // A sink has room to stand at (strawberry: "make sure the sink has adequate standing space in front of it"): the
        // floor 0.9 m in front of every sink -- a kitchen's, a bathroom's basin, a break room's -- across its whole width
        // is clear: no other piece, no wall, none of the stairs, and no door's leaf sweeping through it. 0.9 m, written
        // here rather than read off the generator: the player's 0.7 m capsule and 0.1 m either side (BuildingGen.SinkStand
        // has the why).
        // BREAK IT: make BuildingGen.Stand return 0 (a sink keeps no floor), or drop Put's leaf check.
        [Test]
        public void SinksHaveRoomToStandAt()
        {
            const float Stand = 0.9f;
            var bad = new List<string>();
            int sinks = 0, kitchens = 0, baths = 0;
            foreach (var (spec, res) in All())
                for (int f = 0; f < res.Floors; f++)
                {
                    var walls = WallsOn(res, f).ToList();
                    var stairs = StairsAndTiles(res, f);
                    var leaves = walls.SelectMany(w => w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage).SelectMany(o => LeafBoxes(w, o))).ToList();
                    var pieces = res.Furniture.Where(m => m.Floor == f && m.On == null && !m.Mounted && !m.Ceiling).ToList();
                    foreach (var s in pieces.Where(m => m.Name == "Counter_1"))
                    {
                        sinks++;
                        if (s.Room.Kind == RoomKind.Kitchen) kitchens++; else if (s.Room.Kind == RoomKind.Bathroom) baths++;
                        var sz = FurnitureCatalog.Sizes[s.Name]; var (fx, fz) = s.Front; var (rx, rz) = s.Right;
                        var p0 = (X: s.X + rx * sz.X0 + fx * (sz.Y1 + 0.03f), Z: s.Z + rz * sz.X0 + fz * (sz.Y1 + 0.03f));
                        var p1 = (X: s.X + rx * sz.X1 + fx * (sz.Y1 + Stand), Z: s.Z + rz * sz.X1 + fz * (sz.Y1 + Stand));
                        var zone = (MathF.Min(p0.X, p1.X), MathF.Min(p0.Z, p1.Z), MathF.Max(p0.X, p1.X), MathF.Max(p0.Z, p1.Z));
                        string at = $"{Name(spec)} floor {f}: the sink in {s.Room} at ({s.X:0.00},{s.Z:0.00})";
                        foreach (var o in pieces)
                            if (o != s && Overlap(zone, o.Footprint) && (MathF.Abs(o.Yaw % 90f) < 0.01f || FurnitureCatalog.Intersects(o, new FurniturePlan { Name = "Counter_1", X = (zone.Item1 + zone.Item3) * 0.5f, Z = (zone.Item2 + zone.Item4) * 0.5f, Yaw = s.Yaw })))
                            { bad.Add($"{at} has {o.Name} where you stand"); break; }
                        if (walls.Any(w => Overlap(zone, Box(w)))) bad.Add($"{at} has a wall where you stand");
                        if (stairs.Any(st => Overlap(zone, st))) bad.Add($"{at} has the stairs where you stand");
                        if (leaves.Any(l => Overlap(zone, l))) bad.Add($"{at} has a door swinging where you stand");
                    }
                }
            TestContext.Progress.WriteLine($"[gen-sink] {sinks} sinks ({kitchens} kitchens', {baths} bathrooms'), {Stand} m in front of each across its width; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
            Assert.That(kitchens, Is.GreaterThan(300));
            Assert.That(baths, Is.GreaterThan(200));
        }

        /// <summary>Every window a room's layout changed, held to the rules: changed IN ITS WALL (what the game, the save and
        /// the décor read), on that room's own stretch of it, still a window (as wide as MinWindow, glazed, the same kind,
        /// its head where it was when only its sill moved); one taken out leaves its room another.</summary>
        static void CheckWindowEdits(BuildingGenSpec spec, BuildingGenResult res, List<string> bad)
        {
            var outer = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && w.Thickness == BuildingGen.Ext).ToList();
            // a room's windows as the walls have them: glazed openings whose middle is a metre inside it
            List<WallOpening> Lights(GenRoom g) => outer.Where(w => FloorOf(w) == g.Floor).SelectMany(w => w.Openings.Where(o => o.Glazed)
                .Where(o => { var c = At(w, o.U + o.Width * 0.5f); var n = Normal(w); return g.Holds(c.X - n.X, c.Z - n.Z); })).ToList();
            // (a window changed twice -- narrowed for the bed, then taken out for the wardrobe -- has its first change in its
            // wall no longer: what the wall must have is each window's LAST state)
            bool Same(WallOpening a, WallOpening b) => MathF.Abs(a.U - b.U) < 1e-4f && MathF.Abs(a.V - b.V) < 1e-4f && MathF.Abs(a.Width - b.Width) < 1e-4f && MathF.Abs(a.Height - b.Height) < 1e-4f;
            for (int i = 0; i < res.WindowEdits.Count; i++)
            {
                var e = res.WindowEdits[i];
                bool later = e.After is WallOpening aa && res.WindowEdits.Skip(i + 1).Any(x => x.Wall == e.Wall && Same(x.Before, aa));
                string at = $"{Name(spec)}: {e}";
                if (e.What is not ("sill" or "slide" or "narrow" or "remove")) bad.Add($"{at}: what is '{e.What}'?");
                if (!res.Walls.Contains(e.Wall)) { bad.Add($"{at}: its wall is not the building's"); continue; }
                if (e.After is WallOpening a)
                {
                    // in the wall, exactly as recorded
                    if (!later && !e.Wall.Openings.Any(o => Same(o, a))) bad.Add($"{at}: the wall does not have it");
                    if (!a.Glazed || a.Archetype != e.Before.Archetype) bad.Add($"{at}: no longer the window it was");
                    if (a.Width < BuildingGen.MinWindow - 0.01f) bad.Add($"{at}: only {a.Width:0.00} m wide");
                    if (MathF.Abs(a.V1 - e.Before.V1) > 1e-3f && e.What == "sill") bad.Add($"{at}: its head moved");
                    // on the room's own stretch: both its ends a metre in are the room
                    var n = Normal(e.Wall);
                    foreach (float u in new[] { a.U + 0.05f, a.U1 - 0.05f })
                    { var p = At(e.Wall, u); if (!e.Room.Holds(p.X - n.X, p.Z - n.Z)) bad.Add($"{at}: off its room's wall at u {u:0.00}"); }
                }
                else if (Lights(e.Room).Count == 0) bad.Add($"{at}: took out its room's last window");
            }
            // and a save keeps every changed wall's windows as they now are (the wall is the one thing written)
            var changed = res.WindowEdits.Select(e => e.Wall).Distinct().ToList();
            if (changed.Count == 0) return;
            var back = WallSave.Read(WallSave.Write(changed).Split('\n'));
            for (int i = 0; i < changed.Count; i++)
                if (back.Count != changed.Count || back[i].Openings.Count != changed[i].Openings.Count
                    || !back[i].Openings.Zip(changed[i].Openings).All(x => Same(x.First, x.Second) && x.First.Glazed == x.Second.Glazed))
                    bad.Add($"{Name(spec)}: a wall with a changed window does not come back from a save as it is");
        }

        // Windows altered for the layout (strawberry: "allow room layout to alter windows after the fact if necessary", then
        // round 12b: "anything may alter a window's sil, but sink counters push up to a regular counter's height, not counting
        // its faucet"): nothing standing in front of a window -- in the 0.7 m before its glass, along the glass -- rises over
        // its sill: not a kitchen's run, nor a couch, a bed, a desk, a workbench, a washer. Counted at what stands in front of
        // the glass, read here off the meshes (WindowTopsMatchTheMeshes): a sink at a plain counter's top, its faucet left out;
        // a bed at its pillows, 1.20, unless its headboard is before the glass; a chair, pulled up to a table and
        // moved in use, not at all. A window whose tallest piece is a sink (or a plain counter, the same height) sits at that
        // and 0.1 -- 1.45, not over the faucet's 1.83. And every window a room changed keeps the rules (CheckWindowEdits).
        // BREAK IT: raise sills only over a kitchen's run (couches, beds, desks before the glass); count the sink's faucet.
        [Test]
        public void WindowsSitOverWhatStandsBeforeThem()
        {
            float counter = FurnitureCatalog.Sizes["Counter_0"].H;
            var bad = new List<string>();
            int edits = 0, sills = 0, slides = 0, narrows = 0, removes = 0, overGlass = 0, sinkWindows = 0, headboards = 0;
            var before = new Dictionary<string, int>();
            foreach (var (spec, res) in All())
            {
                var outer = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && w.Thickness == BuildingGen.Ext).ToList();
                foreach (var e in res.WindowEdits)
                {
                    edits++;
                    switch (e.What) { case "sill": sills++; break; case "slide": slides++; break; case "narrow": narrows++; break; case "remove": removes++; break; }
                }
                CheckWindowEdits(spec, res, bad);
                for (int f = 0; f < res.Floors; f++)
                {
                    var pieces = res.Furniture.Where(m => m.Floor == f && m.On == null && !m.Mounted && !m.Ceiling && !m.Name.StartsWith("Chair")).ToList();
                    foreach (var w in outer.Where(w => FloorOf(w) == f))
                        foreach (var o in w.Openings.Where(o => o.Glazed && o.V > 0.05f))
                        {
                            float sill = w.Y + o.V - f * Storey, tallest = 0f; string by = null;
                            foreach (float side in new[] { 1f, -1f })
                            {
                                var zone = SideBox(w, o, side > 0, w.Thickness * 0.5f, w.Thickness * 0.5f + 0.7f, 0.1f);
                                var dir = Dir(w);
                                foreach (var m in pieces)
                                {
                                    var fp = m.Footprint;
                                    if (!Overlap(zone, fp, 0.005f)) continue;
                                    // along the glass itself, not only its pad
                                    float a = float.MaxValue, b = float.MinValue;
                                    foreach (var (x, z) in new[] { (fp.X0, fp.Z0), (fp.X1, fp.Z0), (fp.X0, fp.Z1), (fp.X1, fp.Z1) })
                                    { float u = (x - w.X) * dir.X + (z - w.Z) * dir.Z; a = MathF.Min(a, u); b = MathF.Max(b, u); }
                                    if (b <= o.U + 0.01f || a >= o.U1 - 0.01f) continue;
                                    float top = FurnitureCatalog.Sizes[m.Name].H;
                                    if (m.Name == "Counter_1") top = counter;
                                    else if (m.Name is "Bed_0" or "Bed_1")
                                    {
                                        // its headboard: the 0.25 m at its head (-Y), along the wall
                                        var s0 = FurnitureCatalog.Sizes[m.Name]; var (rx, rz) = m.Right; var (fx, fz) = m.Front;
                                        float ha = float.MaxValue, hb = float.MinValue;
                                        foreach (var (x, y) in new[] { (s0.X0, s0.Y0), (s0.X1, s0.Y0), (s0.X0, s0.Y0 + 0.25f), (s0.X1, s0.Y0 + 0.25f) })
                                        { float u = (m.X + rx * x + fx * y - w.X) * dir.X + (m.Z + rz * x + fz * y - w.Z) * dir.Z; ha = MathF.Min(ha, u); hb = MathF.Max(hb, u); }
                                        bool head = hb > o.U + 0.01f && ha < o.U1 - 0.01f;
                                        if (head) headboards++;
                                        top = head ? s0.H : 1.20f;
                                    }
                                    overGlass++;
                                    before[m.Name] = before.GetValueOrDefault(m.Name) + 1;
                                    if (top > tallest + 1e-4f) { tallest = top; by = m.Name; }
                                    if (sill < top - 1e-3f) bad.Add($"{Name(spec)} floor {f}: {m} stands in front of glass down to {sill:0.00} m, its top at {top:0.00}");
                                }
                            }
                            // over a sink and plain counters only: a counter's height and 0.1, the faucet left out
                            if (by is "Counter_1" or "Counter_0" && MathF.Abs(tallest - counter) < 1e-3f && sill > 1.0f && sill < 2.5f)
                            {
                                if (by == "Counter_1") sinkWindows++;
                                if (MathF.Abs(sill - (counter + BuildingGen.SillOverCounter)) > 0.01f) bad.Add($"{Name(spec)} floor {f}: a window over a {by} sits at {sill:0.00}, not {counter + BuildingGen.SillOverCounter:0.00}");
                            }
                        }
                }
            }
            TestContext.Progress.WriteLine($"[gen-windows] {edits} windows altered ({sills} sills raised over what stands before them, {slides} slid, {narrows} narrowed, {removes} taken out); "
                                         + $"{overGlass} pieces before a window ({string.Join(", ", before.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}"))}), {headboards} beds with their headboard there; "
                                         + $"{sinkWindows} windows whose tallest piece is a sink, at a counter's height; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
            Assert.That(sills, Is.GreaterThan(300), "too few windows raised");
            Assert.That(overGlass, Is.GreaterThan(500), "too few pieces before a window to measure");
            Assert.That(before.GetValueOrDefault("Couch_0") + before.GetValueOrDefault("Couch_1"), Is.GreaterThan(100), "too few couches before a window to measure");
            Assert.That(before.GetValueOrDefault("Bed_1"), Is.GreaterThan(12), "too few beds before a window to measure");
            Assert.That(sinkWindows, Is.GreaterThan(30), "too few windows over a sink to measure");
        }

        // A bed or a wardrobe that wants the wall a window is on (strawberry: "allow room layout to alter windows after the
        // fact if necessary"): the window slides along its own stretch of wall, narrows, or goes where the room has another.
        // The sweep's buildings rarely need it -- their bedrooms have walls to spare -- so this takes the houses squeezed
        // onto town plots, where a bedroom is two cells with a window in most of its walls: there it happens, every change
        // keeps the rules (CheckWindowEdits), and the bedrooms get their wardrobes and keep their beds' heads off the glass
        // (on the generator before this, 72 of 229 of these bedrooms had no wardrobe and 8 beds had their head under a window).
        // BREAK IT: make PutMakingWay return null at once.
        [Test]
        public void WindowsMakeWayForABedOrAWardrobe()
        {
            var bad = new List<string>();
            int bedrooms = 0, wardrobes = 0, headsUnder = 0, ways = 0;
            var counts = new Dictionary<string, int>();
            foreach (var (pw, pd) in new[] { (4, 5), (5, 6), (4, 6) })
                for (int seed = 0; seed < 60; seed++)
                {
                    var spec = new BuildingGenSpec { Seed = seed, Kind = BuildingKind.Residential, MinRooms = 5, MaxFloors = 2, MaxRoomsPerFloor = 6, Width = pw, Depth = pd, FitPlot = true };
                    var res = BuildingGen.Generate(spec);
                    spec = res.Fitted ?? spec;
                    CheckWindowEdits(spec, res, bad);
                    foreach (var e in res.WindowEdits.Where(e => e.What != "sill")) { ways++; counts[$"{e.What} for {e.For}"] = counts.GetValueOrDefault($"{e.What} for {e.For}") + 1; }
                    var outer = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && w.Thickness == BuildingGen.Ext).ToList();
                    foreach (var g in res.Layout.Where(g => g.Kind == RoomKind.Bedroom))
                    {
                        bedrooms++;
                        var mine = res.Furniture.Where(m => m.Room == g && m.On == null && !m.Mounted && !m.Ceiling).ToList();
                        if (mine.Any(m => m.Name.StartsWith("Wardrobe"))) wardrobes++;
                        var bed = mine.FirstOrDefault(m => m.Name.StartsWith("Bed"));
                        if (bed == null) continue;
                        // its head: the strip 0.6 m behind it, into the wall -- against any window's glass there
                        var fp = bed.Footprint; var (fx, fz) = bed.Front;
                        var head = MathF.Abs(fx) > 0.5f ? (fx > 0 ? (fp.X0 - 0.6f, fp.Z0, fp.X0, fp.Z1) : (fp.X1, fp.Z0, fp.X1 + 0.6f, fp.Z1))
                                                       : (fz > 0 ? (fp.X0, fp.Z0 - 0.6f, fp.X1, fp.Z0) : (fp.X0, fp.Z1, fp.X1, fp.Z1 + 0.6f));
                        foreach (var w in outer.Where(w => FloorOf(w) == g.Floor))
                            foreach (var o in w.Openings.Where(o => o.Glazed && o.V > 0.05f && o.V < 2.5f))
                            {
                                var p = At(w, o.U); var q = At(w, o.U1); var n = Normal(w); float t = w.Thickness * 0.5f;
                                var glass = (MathF.Min(p.X, q.X) - MathF.Abs(n.X) * t, MathF.Min(p.Z, q.Z) - MathF.Abs(n.Z) * t, MathF.Max(p.X, q.X) + MathF.Abs(n.X) * t, MathF.Max(p.Z, q.Z) + MathF.Abs(n.Z) * t);
                                if (Overlap(head, glass, 0.02f)) headsUnder++;
                            }
                    }
                }
            TestContext.Progress.WriteLine($"[gen-makeway] {ways} windows made way ({string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}"))}); {wardrobes} of {bedrooms} bedrooms with a wardrobe, {headsUnder} beds' heads under a window; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
            Assert.That(bedrooms, Is.GreaterThan(150));
            Assert.That(ways, Is.GreaterThanOrEqualTo(8), "windows no longer make way for a bed or a wardrobe");
            Assert.That(bedrooms - wardrobes, Is.LessThanOrEqualTo(5));
            Assert.That(headsUnder, Is.LessThanOrEqualTo(4));
        }
    }
}
