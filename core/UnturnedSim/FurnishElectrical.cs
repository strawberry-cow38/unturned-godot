using System;
using System.Collections.Generic;
using System.Linq;
using static UnturnedSim.RoomKind;

namespace UnturnedSim
{
    public static partial class BuildingGen
    {
        /// <summary>Testing only: no switches or outlets -- the building as it was before them.</summary>
        [ThreadStatic] public static bool NoElectrical;

        /// <summary>A light switch's middle and an outlet's over their floor (astraclaw's handoff, notes/household_electrical_
        /// manifest.json: suggested origins -- the switch's raised from 1.4, strawberry: "position light switches a little
        /// higher on the wall"), and an outlet's, or a switch's, over a counter run -- its bottom 5 cm over a counter's 1.35 m
        /// worktop.</summary>
        public const float SwitchHeight = 1.6f, OutletHeight = 0.4f, OutletOverCounter = 1.67f;
        /// <summary>A room's second switch (two-way: RoomElectrical) at another doorway, at least this far from its first.</summary>
        public const float SecondSwitchApart = 2f;
        /// <summary>What one placed outlet supplies (strawberry, round 12: "the outlets should act as power io outlets 1500w
        /// each"): one rating a fixture, however many receptacles its plate shows.</summary>
        public const float OutletWatts = 1500f;
        /// <summary>A switch's near edge at most this far past DecorOpenGap off its doorway's edge: by the door.</summary>
        public const float SwitchByDoor = 0.6f, SwitchByDoorFar = 1.2f;
        /// <summary>A fixture over a basin keeps this far along the wall off its middle, where its tap stands.</summary>
        public const float SinkTap = 0.25f;
        /// <summary>The floor before a switch kept for whoever reaches for it, and the depth before either fixture in which
        /// nothing tall may stand (the handoff's front access, 0.8 m).</summary>
        public const float SwitchReach = 0.5f, ElectricalAccess = 0.8f;
        /// <summary>A piece taller than this standing before an outlet hides it (a wardrobe, a fridge, a shelf unit).</summary>
        public const float HidesOutlet = 1.2f;
        /// <summary>Outlets in one room at least this far apart.</summary>
        public const float OutletApart = 1.5f;

        /// <summary>How many outlets a room of each kind asks for; a kind not here gets none (a closet, a stairwell).</summary>
        public static readonly IReadOnlyDictionary<RoomKind, int> OutletsPerRoom = new Dictionary<RoomKind, int>
        {
            [Bedroom] = 2, [LivingRoom] = 2, [Kitchen] = 2, [Office] = 2, [Workshop] = 2,
            [DiningRoom] = 1, [Bathroom] = 1, [Hall] = 1, [Garage] = 1, [Shop] = 1, [BreakRoom] = 1, [Storage] = 1, [Warehouse] = 1,
        };

        /// <summary>A counter run's worktop, which an outlet goes over at OutletOverCounter.</summary>
        static bool CounterTop(string name) => name is "Counter_0" or "Counter_2" or "Corner_Cabinet_0_Solid";

        /// <summary>What an outlet goes near (the handoff: "near a desk/bedside/counter work area"): a bed's head, a table or
        /// desk, a couch, a counter run, the laundry, a workbench, a shop's tills and coolers.</summary>
        static bool OutletAnchor(string name) => name.StartsWith("Bed_") || name.StartsWith("Table_") || name.StartsWith("Couch_") || CounterTop(name)
                                                 || name is "Washer_0" or "Dryer_0" or "Craft_01" or "Checkout_0" or "Cooler_0" or "Files_0";

        sealed partial class Furnisher
        {
            /// <summary>The room's switches and outlets (astraclaw's Light_Switch_0 and Power_Outlet_0), and its electrical
            /// record: a switch by its door where it has ceiling lights -- two where it has two doorways (LightSwitches) -- and
            /// its outlets low by what it is used at (Outlets). Last of all, on the room's own draws: nothing else moves.</summary>
            public void Electrical()
            {
                var rng = OwnRng(0x5E1C);
                var rec = new RoomElectrical();
                // the room's ceiling lights: each by its place among the building's (Furniture order, which a save keeps)
                int k = 0;
                foreach (var m in _res.Furniture.Where(m => m.Ceiling))
                {
                    if (m.Room == _room) rec.Lights.Add(k);
                    k++;
                }
                var placed = new List<FurniturePlan>();
                // (what Fits tests against, read once: nothing is added to the building, moved or hung until the room's switches
                // and outlets are all chosen -- they go into Electrical below -- so the storey's pieces, vents, fixtures and
                // curtains are the same for every spot it is asked of)
                _fitOn = true;
                try
                {
                    if (rec.Lights.Count > 0) placed.AddRange(LightSwitches(rng));
                    placed.AddRange(Outlets(rng, OutletsPerRoom.GetValueOrDefault(_room.Kind)));
                }
                finally { _fitOn = false; _fit = null; }
                foreach (var m in placed)
                {
                    bool outlet = m.Name == "Power_Outlet_0";
                    // (the first switch's rocker down, any other's up: an odd number down, the lights on as generated)
                    rec.Add(new ElectricalFixture(outlet ? ElectricalKind.Outlet : ElectricalKind.Switch, _res.Electrical.Count, m.X, m.Y, m.Z, m.Yaw, outlet ? OutletWatts : 0f),
                            down: !outlet && rec.Switches.Count == 0);
                    _res.Electrical.Add(m);
                    FurnishTrace?.Invoke($"electrical {m}");
                }
                if (rec.HasElectrical) _room.Electrical = rec;
            }

            /// <summary>The room's light switches, by its doorways: the first on a wall of the room -- the doorway's own, or one
            /// round the corner from it -- its plate's near edge at most SwitchByDoor past DecorOpenGap off the nearest jamb, on
            /// the latch side first (a door folds back over its hinge's), by a door from a hall, a stairwell or outside first;
            /// off a window, a fireplace's wall, the stairs and every door's swing, with the floor before it clear (SwitchReach)
            /// and nothing tall before it. Else up to SwitchByDoorFar off, over whatever stands lower than it, or raised over a
            /// worktop or a basin. Then, where the room has another doorway, a second by it the same way, SecondSwitchApart off
            /// the first (strawberry: "two switches controlling one rooms light"; RoomElectrical's two-way rule). None where
            /// nothing will do: its ceiling lights stay on.</summary>
            List<FurniturePlan> LightSwitches(Rng rng)
            {
                const string name = "Light_Switch_0";
                var s = FurnitureCatalog.Sizes[name];
                float half = (s.X1 - s.X0) * 0.5f, bottom = SwitchHeight - s.H, top = SwitchHeight + s.H;
                var got = new List<FurniturePlan>();
                // the doorways' jambs, on the room's side of their walls
                var jambs = Jambs();
                if (jambs.Count == 0) return got;
                var cands = new List<(Face F, float T, double Score, float Off, int Door)>();
                foreach (var f in AllFaces())
                {
                    if (FireFace(f)) continue;
                    foreach (var (a, b) in Stretches(f, s.Y1 - s.Y0, bottom, top, null))
                        for (float t = a + half; t <= b - half + 1e-3f; t += 0.05f)
                        {
                            // how far its near edge is from the nearest jamb (along the face, or round a corner)
                            float px = f.Nx != 0 ? f.At : t, pz = f.Nx != 0 ? t : f.At;
                            var j = jambs.OrderBy(q => (q.X - px) * (q.X - px) + (q.Z - pz) * (q.Z - pz)).First();
                            float off = MathF.Max(0f, MathF.Sqrt((j.X - px) * (j.X - px) + (j.Z - pz) * (j.Z - pz)) - half - DecorOpenGap);
                            if (off > SwitchByDoorFar) continue;
                            // the latch side first (a door folds back over its hinge's), a door from a hall, a stairwell or outside first
                            cands.Add((f, t, -off - (j.Hinge ? 1.0 : 0.0) + (j.Main ? 1.0 : 0.0) + rng.Int(1000) * 1e-5, off, j.Door));
                        }
                }
                var ranked = cands.OrderByDescending(c => c.Score).ToList();
                var first = Seat(ranked, null);
                if (first == null) return got;
                got.Add(first.Value.M);
                // a second by another doorway (two-way switching: RoomElectrical), SecondSwitchApart from the first
                if (Seat(ranked.Where(c => c.Door != first.Value.Door).ToList(), first.Value.M) is { } second) got.Add(second.M);
                return got;

                // the best candidate that fits: first by the door with the floor before it clear (StandClear); then a little
                // further from it (SwitchByDoorFar), over whatever stands lower than it, or raised over a counter run or a basin
                // (OutletOverCounter, as an outlet there -- clear of a basin's tap, SinkTap either side of its middle): a
                // bathroom's door often opens beside its basin, a kitchen's beside its counters
                (FurniturePlan M, int Door)? Seat(List<(Face F, float T, double Score, float Off, int Door)> list, FurniturePlan apart)
                {
                    bool Far((float X, float Z, float Yaw) at) => apart == null || (at.X - apart.X) * (at.X - apart.X) + (at.Z - apart.Z) * (at.Z - apart.Z) >= SecondSwitchApart * SecondSwitchApart;
                    foreach (var (f, t, _, off, door) in list)
                    {
                        if (off > SwitchByDoor + 1e-3f) continue;
                        var at = AtFace(name, f, t);
                        if (at == null || !Far(at.Value)) continue;
                        if (!Fits(name, at.Value, bottom, top, SwitchReach, false)) continue;
                        if (!StandClear(name, at.Value.X, at.Value.Z, at.Value.Yaw, SwitchReach)) continue;
                        return (Fixture(name, at.Value, SwitchHeight), door);
                    }
                    foreach (var (f, t, _, _, door) in list)
                    {
                        var at = AtFace(name, f, t);
                        if (at == null || !Far(at.Value)) continue;
                        if (Fits(name, at.Value, bottom, top, SwitchReach, false)) return (Fixture(name, at.Value, SwitchHeight), door);
                        if (OverCounter(name, at.Value, true) && ClearOfTap(name, at.Value)
                            && Fits(name, at.Value, OutletOverCounter - s.H, OutletOverCounter + s.H, 0f, true, true)) return (Fixture(name, at.Value, OutletOverCounter), door);
                    }
                    return null;
                }
            }

            /// <summary>Is a fixture at `at` clear of every basin's tap (Counter_1's, at the middle of its back): SinkTap off its
            /// middle along the wall?</summary>
            bool ClearOfTap(string name, (float X, float Z, float Yaw) at)
            {
                var fp = FurnitureCatalog.Footprint(name, at.X, at.Z, at.Yaw);
                var (fx, fz) = FrontOf(at.Yaw);
                // (the basins right before it: within 0.4 m of its wall, along its plate's run grown by a basin's half-width)
                var zone = fx != 0 ? (fx < 0 ? fp.X0 - 0.4f : fp.X0, fp.Z0 - 0.8f, fx > 0 ? fp.X1 + 0.4f : fp.X1, fp.Z1 + 0.8f)
                                   : (fp.X0 - 0.8f, fz < 0 ? fp.Z0 - 0.4f : fp.Z0, fp.X1 + 0.8f, fz > 0 ? fp.Z1 + 0.4f : fp.Z1);
                foreach (var o in _mine.Where(o => o.Name == "Counter_1" && o.On == null && Overlaps(zone, o.Footprint)))
                {
                    float along = fx != 0 ? MathF.Max(fp.Z0 - o.Z, o.Z - fp.Z1) : MathF.Max(fp.X0 - o.X, o.X - fp.X1);
                    if (along < SinkTap) return false;
                }
                return true;
            }

            /// <summary>Up to n outlets, OutletApart apart: low on a wall (OutletHeight) where nothing stands before it, or over
            /// a counter run's worktop (OutletOverCounter) -- off a window, a fireplace's wall, the stairs and every door's
            /// swing, never behind anything tall -- nearest what the room is used at (OutletAnchor: a bed's head, a desk, a
            /// counter), else along its longest free wall.</summary>
            List<FurniturePlan> Outlets(Rng rng, int n)
            {
                const string name = "Power_Outlet_0";
                var got = new List<FurniturePlan>();
                if (n <= 0) return got;
                var s = FurnitureCatalog.Sizes[name];
                float half = (s.X1 - s.X0) * 0.5f;
                // what the room is used at: a bed by its head's two corners, the rest by their middles
                var anchors = new List<(float X, float Z)>();
                foreach (var m in _mine.Where(m => m.On == null && !m.Mounted && !m.Ceiling && OutletAnchor(m.Name)))
                {
                    if (!m.Name.StartsWith("Bed_")) { anchors.Add((m.X, m.Z)); continue; }
                    var b = FurnitureCatalog.Sizes[m.Name];
                    var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                    foreach (float x in new[] { b.X0 - 0.3f, b.X1 + 0.3f }) anchors.Add((m.X + rx * x + fx * (b.Y0 + 0.3f), m.Z + rz * x + fz * (b.Y0 + 0.3f)));
                }
                var cands = new List<(Face F, float T, float H, double Score)>();
                foreach (var f in AllFaces())
                {
                    if (FireFace(f)) continue;
                    foreach (var (h, over) in new[] { (OutletHeight, false), (OutletOverCounter, true) })
                        // (over a counter: its stretches judged from just over the worktop, so the counter itself is no cut)
                        foreach (var (a, b) in Stretches(f, s.Y1 - s.Y0, over ? FurnitureCatalog.Sizes["Counter_0"].H + 0.11f : h - s.H, h + s.H, null))
                        {
                            float mid = (a + b) * 0.5f;
                            for (float t = a + half; t <= b - half + 1e-3f; t += 0.1f)
                            {
                                var p = f.Nx != 0 ? (X: f.At, Z: t) : (X: t, Z: f.At);
                                double near = anchors.Count == 0 ? -0.2 * MathF.Abs(t - mid) : -anchors.Min(q => MathF.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z)));
                                cands.Add((f, t, h, near + rng.Int(1000) * 1e-5));
                            }
                        }
                }
                foreach (var (f, t, h, _) in cands.OrderByDescending(c => c.Score))
                {
                    if (got.Count >= n) break;
                    var at = AtFace(name, f, t);
                    if (at == null) continue;
                    var (x, z, _) = at.Value;
                    if (got.Any(o => (o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z) < OutletApart * OutletApart)) continue;
                    bool over = h == OutletOverCounter;
                    if (!Fits(name, at.Value, h - s.H, h + s.H, over ? 0f : DecorClear, over)) continue;
                    // over a counter only where a worktop is right before it, all along it
                    if (over && !OverCounter(name, at.Value)) continue;
                    got.Add(Fixture(name, at.Value, h));
                }
                return got;
            }

            /// <summary>Is a wall fixture at `at`, from `bottom` to `top` over its floor, clear: off every other hung piece by
            /// 0.1 m along the wall where their heights meet (the clock, the décor, a vent, another fixture), off a curtain and
            /// every door's swing (the floor plan's, LeafSweeps: both sides of a double-acting door), with nothing standing
            /// within `before` in front of it higher than its bottom, and nothing taller than HidesOutlet within
            /// ElectricalAccess? (`counter`: a counter run may stand before it, its worktop under the fixture.)</summary>
            bool Fits(string name, (float X, float Z, float Yaw) at, float bottom, float top, float before, bool counter, bool basin = false)
            {
                var fp = FurnitureCatalog.Footprint(name, at.X, at.Z, at.Yaw);
                float floorY = _room.Floor * Storey;
                var snap = _fitOn ? _fit ??= FitSnapshot() : FitSnapshot();
                foreach (var (o, o0, o1, ofp) in snap.Hung)
                    if (o1 > bottom - 0.05f && o0 < top + 0.05f && Overlaps(Grow(fp, 0.1f), ofp)) return false;
                foreach (var cfp in snap.Curtains) if (Overlaps(Grow(fp, 0.15f), cfp)) return false;
                if (_p.LeafRects.Any(r => Overlaps(Grow(fp, 0.05f), r))) return false;
                // what stands before it: right before it (its plate's width, `before` deep), and anything tall a little further
                var (fx, fz) = FrontOf(at.Yaw);
                // (`along`: past the plate's ends -- a piece beside it, a fridge by a doorway's switch, does not hide it)
                (float, float, float, float) Before(float depth, float along)
                {
                    float x0 = fp.X0 - along, z0 = fp.Z0 - along, x1 = fp.X1 + along, z1 = fp.Z1 + along;
                    return (fx < 0 ? x0 - depth : x0, fz < 0 ? z0 - depth : z0, fx > 0 ? x1 + depth : x1, fz > 0 ? z1 + depth : z1);
                }
                var near = Before(before, 0.05f); var far = Before(ElectricalAccess, -0.02f);
                foreach (var (o, topOf, h, ofp) in snap.Standing)
                {
                    if (counter && o.On == null && (CounterTop(o.Name) || (basin && o.Name == "Counter_1"))) continue;
                    if (before > 0f && topOf > bottom - 0.1f && Overlaps(near, ofp)) return false;
                    if (o.On == null && h > HidesOutlet && Overlaps(far, ofp)) return false;
                }
                return true;
            }

            /// <summary>What Fits tests a fixture against, on this storey: the hung pieces, vents and fixtures (each with its
            /// height's span over the floor and its footprint), the curtains' footprints, and the standing pieces (each with its
            /// top over the floor, its height and its footprint) -- in the order Fits used to read them off the lists.</summary>
            sealed class FitSet
            {
                public readonly List<(FurniturePlan O, float O0, float O1, (float X0, float Z0, float X1, float Z1) Fp)> Hung = new();
                public readonly List<(float X0, float Z0, float X1, float Z1)> Curtains = new();
                public readonly List<(FurniturePlan O, float Top, float H, (float X0, float Z0, float X1, float Z1) Fp)> Standing = new();
            }
            FitSet _fit; bool _fitOn;
            FitSet FitSnapshot()
            {
                var set = new FitSet();
                float floorY = _room.Floor * Storey;
                foreach (var o in Here().Concat(_res.HvacVents).Concat(_res.Electrical).Where(o => o.Floor == _room.Floor && o.Mounted))
                {
                    float h = FurnitureCatalog.Sizes[o.Name].H, o0 = o.Y - floorY - h, o1 = o.Y - floorY + h;
                    set.Hung.Add((o, o0, o1, o.Footprint));
                }
                foreach (var c in _res.RuntimeDecor.Where(c => c.Floor == _room.Floor && c.Gen.Kind == RuntimeDecorKind.Curtain)) set.Curtains.Add(c.Footprint);
                foreach (var o in Here().Where(o => o.Floor == _room.Floor && !o.Mounted && !o.Ceiling))
                {
                    float topOf = o.Y - floorY + FurnitureCatalog.Sizes[o.Name].H;
                    set.Standing.Add((o, topOf, FurnitureCatalog.Sizes[o.Name].H, o.Footprint));
                }
                return set;
            }

            /// <summary>Is a counter run's worktop right before a fixture at `at`, all along its plate (within 0.35 m of the
            /// wall), and lower than the fixture?</summary>
            bool OverCounter(string name, (float X, float Z, float Yaw) at, bool basin = false)
            {
                var fp = FurnitureCatalog.Footprint(name, at.X, at.Z, at.Yaw);
                var (fx, fz) = FrontOf(at.Yaw);
                for (float u = 0.05f; u < 0.95f; u += 0.1f)
                {
                    float px = fx != 0 ? fp.X0 + (fp.X1 - fp.X0) * 0.5f + fx * 0.35f : fp.X0 + (fp.X1 - fp.X0) * u;
                    float pz = fz != 0 ? fp.Z0 + (fp.Z1 - fp.Z0) * 0.5f + fz * 0.35f : fp.Z0 + (fp.Z1 - fp.Z0) * u;
                    if (!_mine.Any(o => o.On == null && (CounterTop(o.Name) || (basin && o.Name == "Counter_1")) && px > o.Footprint.X0 && px < o.Footprint.X1 && pz > o.Footprint.Z0 && pz < o.Footprint.Z1))
                        return false;
                }
                return true;
            }

            /// <summary>The ends of this room's doorways, on its side of their walls: each a point at the room's face of the
            /// wall, whether the door's leaf is hung on it (a pocket door's leaf slides in on that side too: HangDoors), whether
            /// the doorway is the room's way in from a hall, a stairwell or outside, and which doorway it is. A closet's doorway
            /// is none of them.</summary>
            List<(float X, float Z, bool Hinge, bool Main, int Door)> Jambs()
            {
                var l = new List<(float, float, bool, bool, int)>();
                int door = 0;
                foreach (var w in RoomWalls())
                {
                    var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                    var (nx, nz) = (-dz, dx);
                    foreach (var o in w.Openings.Where(o => o.V < 0.05f))
                    {
                        float u = o.U + o.Width * 0.5f, cx = w.X + dx * u, cz = w.Z + dz * u, d = w.Thickness * 0.5f;
                        foreach (int sgn in new[] { 1, -1 })
                        {
                            if (!_room.Holds(cx + sgn * nx * (d + 0.3f), cz + sgn * nz * (d + 0.3f))) continue;
                            var past = _p.Rooms.FirstOrDefault(g => g.Floor == _room.Floor && g.Holds(cx - sgn * nx * (d + 0.3f), cz - sgn * nz * (d + 0.3f)));
                            // (not by a closet's door: nobody switches a room's light on from the wardrobe)
                            if (past != null && past.Kind == Closet) continue;
                            bool main = past == null || past.Circulation, leaf = o.HasDoor && o.Archetype != ArchGarage;
                            foreach (var (ju, hinge) in new[] { (o.U, o.DoorHingeAtStart), (o.U1, !o.DoorHingeAtStart) })
                                l.Add((w.X + dx * ju + sgn * nx * d, w.Z + dz * ju + sgn * nz * d, leaf && hinge, main, door));
                            door++;
                        }
                    }
                }
                return l;
            }

            FurniturePlan Fixture(string name, (float X, float Z, float Yaw) at, float height)
                => new FurniturePlan { Name = name, X = at.X, Y = _room.Floor * Storey + height, Z = at.Z, Yaw = at.Yaw, Floor = _room.Floor, Room = _room, Mounted = true };
        }
    }
}
