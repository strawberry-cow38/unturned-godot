using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // Round 12b, the layout follow-up (strawberry, answering round 12's questions): "anything may alter a window's sil, but
    // sink counters push up to a regular counter's height, not counting its faucet. limit 1 wardrobe per bedroom. doors can
    // swing either way, some may swing both ways, swinging away from the player when they open them", then "sliding pocket
    // doors too". As round 12's checks, these read the WALLS and the furniture as generated, and hold them to rules written
    // out here rather than asked of the generator.
    public partial class BuildingGenTests
    {
        /// <summary>A box of floor on one side of a door's wall: across its opening (and `pad` past either jamb), from `from`
        /// to `reach` off the wall's mid-plane, on its front face when `front`, else its back.</summary>
        static (float X0, float Z0, float X1, float Z1) SideBox(WallPlan w, WallOpening o, bool front, float from, float reach, float pad)
        {
            var n = Normal(w); float s = front ? 1f : -1f;
            var p0 = At(w, o.U - pad); var p1 = At(w, o.U1 + pad);
            float ax = MathF.Min(p0.X, p1.X), az = MathF.Min(p0.Z, p1.Z), bx = MathF.Max(p0.X, p1.X), bz = MathF.Max(p0.Z, p1.Z);
            return (MathF.Min(ax + s * n.X * from, ax + s * n.X * reach), MathF.Min(az + s * n.Z * from, az + s * n.Z * reach),
                    MathF.Max(bx + s * n.X * from, bx + s * n.X * reach), MathF.Max(bz + s * n.Z * from, bz + s * n.Z * reach));
        }

        // strawberry's doors that swing both ways, as this lane reads her: the doors food and stock go through. A home's
        // kitchen, dining room and living room; a shop floor and its back of house (stock room, back hall, warehouse,
        // workshop); an industrial building's working rooms with each other. Written here, not asked of the generator.
        static bool SwingsBothWays(BuildingKind building, RoomKind a, RoomKind b)
        {
            static bool Serving(RoomKind k) => k is RoomKind.Kitchen or RoomKind.DiningRoom or RoomKind.LivingRoom;
            static bool Back(RoomKind k) => k is RoomKind.Storage or RoomKind.Hall or RoomKind.Warehouse or RoomKind.Workshop;
            static bool Working(RoomKind k) => k is RoomKind.Warehouse or RoomKind.Workshop or RoomKind.Storage or RoomKind.Garage;
            if (building is BuildingKind.Residential or BuildingKind.Apartment) return Serving(a) && Serving(b);
            return (a == RoomKind.Shop && Back(b)) || (b == RoomKind.Shop && Back(a)) || (building == BuildingKind.Industrial && Working(a) && Working(b));
        }

        // Doors that swing both ways, and pocket doors (round 12b). A double-acting door is between two rooms of one home or
        // business that SwingsBothWays names -- never a bedroom's, a bathroom's, a closet's or an office's, never a flat's front
        // door or a stairwell's -- and both its sides take the leaf: no stairs, no wall in either sweep, and no piece standing
        // in either. A pocket door is a door inside one home or business where a swing would be cramped: a closet's, or one
        // whose open leaf (its width less a 0.2 m frame each side) would sweep the stairs or a wall, leave less than 0.8 m (the
        // player's capsule and 0.1) past its edge to walk by, or sweep across another doorway or another door's leaf -- and its
        // wall takes the leaf: solid for the leaf's width past the jamb it slides to, inside the wall's own length, with no
        // other opening (or its frame) there and no wall meeting it there.
        // BREAK IT: DoubleActing false for every pair (none swing both ways); PocketFits true always (a leaf slid into a wall
        // across it); a pocket door for every inside door (most are not cramped); keep one sweep of a double door.
        [Test]
        public void DoorsSwingBothWaysOrSlideWhereTheyShould()
        {
            var bad = new List<string>();
            int doubles = 0, doublesHome = 0, doublesBusiness = 0, pockets = 0, closetPockets = 0, crampedPockets = 0, leftSwinging = 0;
            var pairs = new Dictionary<string, int>();
            foreach (var (spec, res) in All())
                for (int f = 0; f < res.Floors; f++)
                {
                    var walls = WallsOn(res, f).ToList();
                    var stairs = StairsAndTiles(res, f);
                    var pieces = res.Furniture.Where(m => m.Floor == f && m.On == null && !m.Mounted && !m.Ceiling).ToList();
                    // every doorway's walk a metre out either side, and every leaf that swings: what a leaf may not sweep across
                    var ways = walls.SelectMany(w => w.Openings.Where(o => o.V < 0.05f).Select(o => (W: w, O: o, Box: (SideBox(w, o, true, -(w.Thickness * 0.5f + 1f), w.Thickness * 0.5f + 1f, 0f))))).ToList();
                    var leaves = walls.SelectMany(w => w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage).SelectMany(o => LeafBoxes(w, o).Select(b => (W: w, O: o, Box: b)))).ToList();
                    foreach (var w in walls)
                    {
                        var solid = walls.Where(x => x != w).SelectMany(SolidBoxes).ToList();
                        foreach (var o in w.Openings.Where(o => o.V < 0.05f && o.HasDoor && o.Archetype != BuildingGen.ArchGarage))
                        {
                            var n = Normal(w); var c = At(w, o.U + o.Width * 0.5f); float d = w.Thickness * 0.5f + 0.3f, t = w.Thickness * 0.5f, leafW = o.Width - 0.4f;
                            var fr = RoomAt(res, f, c.X + n.X * d, c.Z + n.Z * d); var bk = RoomAt(res, f, c.X - n.X * d, c.Z - n.Z * d);
                            string at = $"{Name(spec)} floor {f}: the {o.DoorAction} door {fr?.Kind.ToString() ?? "outside"} | {bk?.Kind.ToString() ?? "outside"} at ({c.X:0.0},{c.Z:0.0})";
                            bool inside = fr != null && bk != null && fr.Unit == bk.Unit && fr.Kind != RoomKind.Stairwell && bk.Kind != RoomKind.Stairwell;
                            bool Other((WallPlan W, WallOpening O, (float, float, float, float) Box) x) => x.W != w || MathF.Abs(x.O.U - o.U) > 0.01f;
                            // the open leaf on one side: no stairs, no wall
                            bool Takes(bool front)
                            {
                                var lf = SideBox(w, o, front, t + 0.05f, leafW, 0f);
                                return !stairs.Any(st => Overlap(lf, st)) && !solid.Any(b => Overlap(SideBox(w, o, front, t + 0.05f, leafW, -0.04f), b));
                            }
                            // cramped, read generously (a little wider than the generator reads it, and against EVERY other
                            // door's leaf rather than those hung first), so every door the generator found cramped is
                            bool Cramped(bool front)
                            {
                                var pass = SideBox(w, o, front, t + 0.02f, leafW + 0.8f, 0.06f);
                                if (stairs.Any(st => Overlap(pass, st)) || solid.Any(b => Overlap(SideBox(w, o, front, t + 0.02f, leafW + 0.8f, -0.04f), b))) return true;
                                var sweep = SideBox(w, o, front, t + 0.02f, t + o.Width, 0.06f);
                                var wide = SideBox(w, o, front, t + 0.02f, t + o.Width + 0.06f, 0.12f);   // (the generator pads both leaves)
                                return ways.Any(x => Other(x) && Overlap(sweep, x.Box)) || leaves.Any(x => Other(x) && Overlap(wide, x.Box));
                            }
                            // the wall past a jamb: inside its length, no other opening or its frame, no wall meeting it
                            bool Pocket(bool start)
                            {
                                float a = start ? o.U - leafW : o.U1, b = start ? o.U : o.U1 + leafW;
                                if (a < 0f || b > w.Length) return false;
                                if (w.Openings.Any(x => MathF.Abs(x.U - o.U) > 0.01f && x.U < b + 0.2f && x.U1 > a - 0.2f)) return false;
                                var p = o; p.U = a; p.Width = b - a;
                                var cavity = SideBox(w, p, true, -(t + 0.05f), t + 0.05f, 0f);
                                return !solid.Any(x => Overlap(cavity, x, 0f));
                            }
                            if (o.DoorAction == DoorAction.DoubleSwing)
                            {
                                doubles++;
                                if (res.Kind is BuildingKind.Residential or BuildingKind.Apartment) doublesHome++; else doublesBusiness++;
                                if (!inside) { bad.Add($"{at}: swings both ways, but is not a door inside one home or business"); continue; }
                                string pair = string.Join("-", new[] { fr.Kind, bk.Kind }.OrderBy(k => k.ToString()));
                                pairs[$"{res.Kind} {pair}"] = pairs.GetValueOrDefault($"{res.Kind} {pair}") + 1;
                                if (PrivateRoom(fr.Kind) || PrivateRoom(bk.Kind)) bad.Add($"{at}: swings both ways into a private room");
                                if (!SwingsBothWays(res.Kind, fr.Kind, bk.Kind)) bad.Add($"{at}: swings both ways between rooms food and stock are not carried between");
                                foreach (bool front in new[] { true, false })
                                {
                                    if (!Takes(front)) bad.Add($"{at}: its leaf cannot open to the {(front ? "front" : "back")}");
                                    var sw = SideBox(w, o, front, 0f, t + o.Width - 0.05f, 0f);
                                    var m = pieces.FirstOrDefault(m => Overlap(sw, m.Footprint));
                                    if (m != null) bad.Add($"{at}: {m.Name} stands where it swings to the {(front ? "front" : "back")}");
                                }
                            }
                            else if (o.DoorAction == DoorAction.Pocket)
                            {
                                pockets++;
                                if (!inside) { bad.Add($"{at}: slides, but is not a door inside one home or business"); continue; }
                                if (!Pocket(o.DoorHingeAtStart)) bad.Add($"{at}: its wall cannot take the leaf past its {(o.DoorHingeAtStart ? "start" : "end")} jamb");
                                bool closet = fr.Kind == RoomKind.Closet || bk.Kind == RoomKind.Closet;
                                if (closet) closetPockets++;
                                else if (Cramped(o.DoorOpensFront)) crampedPockets++;
                                else bad.Add($"{at}: slides, though it could swing into the {(o.DoorOpensFront ? fr : bk).Kind} with room to spare");
                            }
                            else if (inside && (fr.Kind == RoomKind.Closet || bk.Kind == RoomKind.Closet || Cramped(o.DoorOpensFront)) && (Pocket(true) || Pocket(false))
                                     && !SwingsBothWays(res.Kind, fr.Kind, bk.Kind))
                                leftSwinging++;   // (read generously above: told, not held)
                        }
                    }
                }
            TestContext.Progress.WriteLine($"[gen-door-action] {doubles} doors swing both ways ({doublesHome} in homes, {doublesBusiness} in businesses: {string.Join(", ", pairs.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}"))}); "
                                         + $"{pockets} pocket doors ({closetPockets} a closet's, {crampedPockets} where a swing was cramped); {leftSwinging} doors swing where a pocket might have gone by this test's wider reading; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
            Assert.That(doublesHome, Is.GreaterThan(100), "too few doors swing both ways in homes");
            Assert.That(doublesBusiness, Is.GreaterThan(10), "too few doors swing both ways in businesses");
            Assert.That(pockets, Is.GreaterThan(10), "too few pocket doors");
            Assert.That(closetPockets, Is.GreaterThan(2));
            Assert.That(crampedPockets, Is.GreaterThan(5));
        }

        // One wardrobe to a bedroom (strawberry, round 12b: "limit 1 wardrobe per bedroom"). No bedroom over the sweep has
        // two -- and none had before either: the bedroom's recipe asks for one. So the limit is held as the FURNISHER's, which
        // is where it was asked for: with every room asking for two more wardrobes once its recipe is done
        // (BuildingGen.FurnishAlso, put like any piece), a bedroom still has one, while a living room asked the same gets one
        // -- the asking works, and in a bedroom the limit is what says no.
        // BREAK IT: drop Put's wardrobe limit.
        [Test]
        public void OneWardrobeToABedroom()
        {
            var bad = new List<string>();
            int bedrooms = 0, withOne = 0;
            foreach (var (spec, res) in All())
                foreach (var g in res.Layout.Where(g => g.Kind == RoomKind.Bedroom))
                {
                    bedrooms++;
                    int k = res.Furniture.Count(m => m.Room == g && m.Name.StartsWith("Wardrobe"));
                    if (k == 1) withOne++;
                    if (k > 1) bad.Add($"{Name(spec)}: {g} has {k} wardrobes");
                }
            int askedBedrooms = 0, livings = 0, livingWardrobes = 0;
            try
            {
                BuildingGen.FurnishAlso = g => new[] { "Wardrobe_0", "Wardrobe_1" };
                foreach (var spec in Specs().Where(s => s.Kind == BuildingKind.Residential && !s.FitPlot).Take(40))
                {
                    var res = BuildingGen.Generate(spec);
                    foreach (var g in res.Layout)
                    {
                        int k = res.Furniture.Count(m => m.Room == g && m.Name.StartsWith("Wardrobe"));
                        if (g.Kind == RoomKind.Bedroom) { askedBedrooms++; if (k > 1) bad.Add($"{Name(spec)}, asked for more: {g} has {k} wardrobes"); }
                        if (g.Kind == RoomKind.LivingRoom) { livings++; livingWardrobes += k; }
                    }
                }
            }
            finally { BuildingGen.FurnishAlso = null; }
            TestContext.Progress.WriteLine($"[gen-wardrobe] {withOne} of {bedrooms} bedrooms with one wardrobe, none with more; asked for two more, {askedBedrooms} bedrooms kept to one while {livings} living rooms took {livingWardrobes}; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
            Assert.That(withOne, Is.GreaterThan(bedrooms * 9 / 10));
            Assert.That(askedBedrooms, Is.GreaterThan(40));
            Assert.That(livingWardrobes, Is.GreaterThan(livings / 2), "the extra ask placed nothing anywhere: the bedroom limit would be untested");
        }

        // What a sill is raised over, held to the meshes rather than to the generator's constants: a sink's worktop is a
        // plain counter's height (Counter_1's highest point in its front half, where you stand, is Counter_0's top; only the
        // faucet at its back stands higher), and a bed stands no higher than BedFoot past BedHeadboard from its head -- its
        // pillows, 1.195 -- and its full height inside it.
        // BREAK IT: a BedFoot under the pillows (1.15, what a first reading of the mesh said); a BedHeadboard too thin (0.24).
        [Test]
        public void WindowTopsMatchTheMeshes()
        {
            string dir = Objects();
            Assert.That(dir, Is.Not.Null, "game/content/objects not found");
            List<float[]> V(string name) => File.ReadLines(Path.Combine(dir, name + ".obj")).Where(l => l.StartsWith("v ")).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(3)
                                                   .Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
            var sink = V("Counter_1");
            float worktop = sink.Where(p => p[1] > 0f).Max(p => p[2]), top = sink.Max(p => p[2]);
            TestContext.Progress.WriteLine($"[gen-window-tops] sink worktop {worktop:0.000} (Counter_0 {FurnitureCatalog.Sizes["Counter_0"].H:0.000}), faucet {top:0.000}");
            Assert.That(worktop, Is.EqualTo(FurnitureCatalog.Sizes["Counter_0"].H).Within(0.015f));
            Assert.That(top, Is.GreaterThan(worktop + 0.3f), "the sink has no faucet to leave out");
            foreach (var bed in new[] { "Bed_0", "Bed_1" })
            {
                var v = V(bed); float y0 = v.Min(p => p[1]);
                float past = v.Where(p => p[1] > y0 + BuildingGen.BedHeadboard).Max(p => p[2]), head = v.Where(p => p[1] <= y0 + BuildingGen.BedHeadboard).Max(p => p[2]);
                TestContext.Progress.WriteLine($"[gen-window-tops] {bed}: {head:0.000} at its head, {past:0.000} past its headboard");
                Assert.That(past, Is.LessThanOrEqualTo(BuildingGen.BedFoot), $"{bed} stands over BedFoot past its headboard");
                Assert.That(past, Is.GreaterThan(BuildingGen.BedFoot - 0.03f), $"{bed}: BedFoot is not its mattress and foot");
                Assert.That(head, Is.EqualTo(FurnitureCatalog.Sizes[bed].H).Within(0.015f), $"{bed}'s headboard is not inside BedHeadboard");
            }
        }
    }
}
