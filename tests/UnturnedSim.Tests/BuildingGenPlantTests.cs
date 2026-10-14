using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for round 12's décor rules and the potted plants (FurnishDecor.cs, FurnishPlants.cs), read off the generated walls.
    // The test's own numbers are looser than the generator's: it keeps a hung piece 0.6 m from a window along the grid's
    // face of the wall, this asks 0.5 of the real geometry; a floor plant 0.1 m off other pieces, this 0.05. The rooms are
    // walked by a body the player's 0.7 m across, which the generator does not walk at all: its plants stand in corners and
    // by couches, out of the ways through, and this is what says so.
    public partial class BuildingGenTests
    {
        /// <summary>How near a window a hung piece may come, in plan, at any height (BuildingGen.DecorWindowGap less the
        /// grid's slack).</summary>
        const float WindowBerth = 0.5f;

        /// <summary>A room's windows -- openings off the floor in the walls of its floor, the room on one side -- each as its
        /// jambs on the room's face of the wall.</summary>
        static IEnumerable<(WallPlan W, WallOpening O, (float X, float Z) P0, (float X, float Z) P1)> RoomWindows(BuildingGenResult r, int f, GenRoom g)
        {
            foreach (var w in WallsOn(r, f))
            {
                var n = Normal(w); float t = w.Thickness * 0.5f;
                foreach (var o in w.Openings.Where(o => o.V >= 0.05f))
                {
                    var c = At(w, o.U + o.Width * 0.5f);
                    foreach (int sg in new[] { 1, -1 })
                        if (g.Holds(c.X + sg * n.X * (t + 0.3f), c.Z + sg * n.Z * (t + 0.3f)))
                        {
                            var a = At(w, o.U); var b = At(w, o.U1);
                            yield return (w, o, (a.X + sg * n.X * t, a.Z + sg * n.Z * t), (b.X + sg * n.X * t, b.Z + sg * n.Z * t));
                        }
                }
            }
        }

        static float SegDist((float X, float Z) a0, (float X, float Z) a1, (float X, float Z) b0, (float X, float Z) b1)
        {
            // (ZombieSpatial's point to segment, in plan)
            static float PtSeg((float X, float Z) p, (float X, float Z) s0, (float X, float Z) s1)
                => MathF.Sqrt(SDG.Unturned.ZombieSpatial.SqrDistanceToSegment(new UnityEngine.Vector3(p.X, 0f, p.Z), new UnityEngine.Vector3(s0.X, 0f, s0.Z),
                                                                              new UnityEngine.Vector3(s1.X - s0.X, 0f, s1.Z - s0.Z)));
            return new[] { PtSeg(a0, b0, b1), PtSeg(a1, b0, b1), PtSeg(b0, a0, a1), PtSeg(b1, a0, a1) }.Min();
        }

        /// <summary>A hung piece's back edge in plan, end to end.</summary>
        static ((float X, float Z) A, (float X, float Z) B) BackEdge(FurniturePlan m)
        {
            var s = FurnitureCatalog.Sizes[m.Name]; var (fx, fz) = m.Front; var (rx, rz) = m.Right;
            return ((m.X + rx * s.X0 + fx * s.Y0, m.Z + rz * s.X0 + fz * s.Y0), (m.X + rx * s.X1 + fx * s.Y0, m.Z + rz * s.X1 + fz * s.Y0));
        }

        /// <summary>The windows a hung piece comes nearer than WindowBerth to, at any height.</summary>
        static IEnumerable<string> NearWindows(BuildingGenResult res, FurniturePlan m)
        {
            var (a, b) = BackEdge(m);
            foreach (var (w, o, p0, p1) in RoomWindows(res, m.Floor, m.Room))
            {
                float d = SegDist(a, b, p0, p1);
                if (d < WindowBerth) yield return $"{d:0.00} m from a window (archetype {o.Archetype}, {o.V:0.00}-{o.V1:0.00} m up)";
            }
        }

        /// <summary>The fireplaces: a chimney breast is a full-storey wall on the ground floor ChimneyDeep thick with an
        /// opening at its foot -- the fire. Each as its box, its long axis (D), its normal into the room (N), the room it
        /// stands in (Own), and the rooms whose wall it is: that one, and any open to it (open plan: one wall runs on across
        /// the cut).</summary>
        static List<((float X0, float Z0, float X1, float Z1) Box, (float X, float Z) D, (float X, float Z) N, GenRoom Own, HashSet<GenRoom> Rooms)> Fireplaces(BuildingGenResult res)
        {
            var l = new List<((float, float, float, float), (float, float), (float, float), GenRoom, HashSet<GenRoom>)>();
            var walls = WallsOn(res, 0).ToList();
            foreach (var w in walls.Where(w => MathF.Abs(w.Thickness - BuildingGen.ChimneyDeep) < 0.01f && w.Openings.Any(o => o.V < 0.1f && o.Height < 1.5f)))
            {
                var box = Box(w); var c = At(w, w.Length * 0.5f); var n = Normal(w); float half = w.Thickness * 0.5f;
                bool WallAt(float x, float z) => walls.Any(q => q != w && Box(q) is var b && x >= b.X0 && x <= b.X1 && z >= b.Z0 && z <= b.Z1);
                foreach (int sg in new[] { 1, -1 })
                {
                    var into = (X: n.X * sg, Z: n.Z * sg);
                    if (!WallAt(c.X - into.X * (half + 0.15f), c.Z - into.Z * (half + 0.15f)) || WallAt(c.X + into.X * (half + 0.15f), c.Z + into.Z * (half + 0.15f))) continue;
                    var room = RoomAt(res, 0, c.X + into.X * (half + 0.3f), c.Z + into.Z * (half + 0.3f));
                    if (room == null) continue;
                    var rooms = new HashSet<GenRoom> { room };
                    foreach (var o in res.OpenPlan.Where(o => o.Floor == 0 && (o.A == room || o.B == room))) rooms.Add(o.A == room ? o.B : o.A);
                    l.Add((box, Dir(w), into, room, rooms));
                }
            }
            return l;
        }

        /// <summary>Is a hung piece on a fireplace's wall: the house wall the breast stands against (either side of it),
        /// the breast's front, or a cheek? (`fixture`: a fixture over a counter, which in a room open to the fireplace's goes
        /// with its counters.)</summary>
        static bool OnFireWall(FurniturePlan m, ((float X0, float Z0, float X1, float Z1) Box, (float X, float Z) D, (float X, float Z) N, GenRoom Own, HashSet<GenRoom> Rooms) fire, bool fixture)
        {
            if (!fire.Rooms.Contains(m.Room) || (fixture && m.Room != fire.Own)) return false;
            var (a, b) = BackEdge(m);
            var (fx, fz) = m.Front;
            float cx = (fire.Box.X0 + fire.Box.X1) * 0.5f, cz = (fire.Box.Z0 + fire.Box.Z1) * 0.5f;
            float half = MathF.Min(fire.Box.X1 - fire.Box.X0, fire.Box.Z1 - fire.Box.Z0) * 0.5f, len = MathF.Max(fire.Box.X1 - fire.Box.X0, fire.Box.Z1 - fire.Box.Z0) * 0.5f;
            float q = ((a.X + b.X) * 0.5f - cx) * fire.N.X + ((a.Z + b.Z) * 0.5f - cz) * fire.N.Z;
            float u0 = (a.X - cx) * fire.D.X + (a.Z - cz) * fire.D.Z, u1 = (b.X - cx) * fire.D.X + (b.Z - cz) * fire.D.Z;
            if (fx * fire.N.X + fz * fire.N.Z > 0.9f)
                return MathF.Abs(q + half) < 0.25f || (MathF.Abs(q - half) < 0.25f && MathF.Min(u0, u1) < len && MathF.Max(u0, u1) > -len);
            if (MathF.Abs(fx * fire.D.X + fz * fire.D.Z) > 0.9f)
            {
                // a cheek: its back at the breast's end, along the breast's depth
                float q0 = (a.X - cx) * fire.N.X + (a.Z - cz) * fire.N.Z, q1 = (b.X - cx) * fire.N.X + (b.Z - cz) * fire.N.Z;
                return MathF.Abs(MathF.Abs(u0) - len) < 0.25f && MathF.Min(q0, q1) < half && MathF.Max(q0, q1) > -half;
            }
            return false;
        }

        // W: a hung prop keeps off a fireplace's wall and away from the windows (strawberry, 2026-10-13: "prevent wall props
        // placing on fireplace walls", "increase wall prop no-go-zones around windows"). No wall décor and no clock on any of
        // a fireplace's wall -- the house wall it stands against on both sides of it, the breast over the fire, its cheeks --
        // in its room or one open to it (but for upper cabinets and rails over a kitchen's counters open to it: they go with
        // the counters); and none within WindowBerth of a window of its room, in plan, at any height: beside
        // it, over or under it (a towel rail under a bathroom's slit), round a corner from it. The fireplaces are found off
        // the walls, not from the generator's Chimney footprint.
        // BREAK IT: let the décor onto a FireFace; let the clock hang where HangRefused says no; put DecorWindowGap back to
        // 0.15 m at the piece's height only.
        [Test]
        public void WallPropsKeepOffFireplacesAndWindows()
        {
            var byName = BuildingGen.Decor.ToDictionary(r => r.Name);
            var bad = new List<string>();
            int houses = 0, fires = 0, fireRooms = 0, fireRoomPieces = 0, props = 0, clocks = 0, nearWindow = 0, fireTvs = 0;
            foreach (var (spec, res) in All())
            {
                var fp = Fireplaces(res);
                if (fp.Count > 0) houses++;
                fires += fp.Count;
                var hung = res.Furniture.Where(m => m.Mounted && (byName.ContainsKey(m.Name) || m.Name == "Clock_0")).ToList();
                // (and the flat television, round 12: off the fireplace's wall too -- it keeps the floor plan's window zones, not
                // the décor's 0.6 m, so it is not in the window check below)
                var tvs = res.Furniture.Where(m => m.Mounted && m.Name == "Television_0").ToList();
                foreach (var fire in fp)
                {
                    fireRooms += fire.Rooms.Count;
                    fireRoomPieces += hung.Count(m => fire.Rooms.Contains(m.Room));
                    fireTvs += tvs.Count(m => fire.Rooms.Contains(m.Room));
                    foreach (var m in hung.Concat(tvs).Where(m => OnFireWall(m, fire, byName.TryGetValue(m.Name, out var row) && row.Spot == DecorSpot.Over)))
                        bad.Add($"{Name(spec)}: {m} is on the fireplace's wall");
                }
                foreach (var m in hung)
                {
                    props++; if (m.Name == "Clock_0") clocks++;
                    foreach (var why in NearWindows(res, m)) { nearWindow++; bad.Add($"{Name(spec)}: {m} hangs {why}"); }
                }
            }
            TestContext.Progress.WriteLine($"[wall-props] {props} hung props ({clocks} clocks) over {All().Count} buildings; {fires} fireplaces in {houses} houses, "
                                           + $"{fireRoomPieces} props and {fireTvs} wall televisions in the {fireRooms} rooms whose wall one is; {nearWindow} within {WindowBerth} m of a window; "
                                           + $"{bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(houses, Is.GreaterThan(30), "fireplaces were there to keep off");
            Assert.That(fireRoomPieces, Is.GreaterThan(houses), "the rooms with a fireplace still hang their props, on their other walls");
            Assert.That(clocks, Is.GreaterThan(500));
            Assert.That(fireTvs, Is.GreaterThan(10), "wall televisions hang in rooms with a fireplace, on their other walls");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // W2: the flat television keeps off a fireplace's wall too (strawberry, round 12: "it now falls under the fireplace
        // rule"). The sweep never puts one there -- 162 wall televisions in 765 fireplace houses, a search of 800 specs found
        // one, and it is this house: its set hung on the house wall the breast stands against. Here it goes elsewhere, and
        // the living room still has a set it can watch (Facing: on a wall, or a CRT on a table).
        // BREAK IT: drop WallTv's refuse.
        [Test]
        public void WallTelevisionKeepsOffTheFireplace()
        {
            var spec = new BuildingGenSpec { Seed = 113, Kind = BuildingKind.Residential, Chimney = ChimneyChoice.Yes, MinRooms = 7, MaxFloors = 1, MaxRoomsPerFloor = 8 };
            var res = BuildingGen.Generate(spec);
            var fires = Fireplaces(res);
            var living = res.Layout.Single(g => g.Kind == RoomKind.LivingRoom);
            Assert.That(fires.Any(f => f.Rooms.Contains(living)), "the case's living room has the fireplace");
            var sets = res.Furniture.Where(m => m.Room == living && m.Name.StartsWith("Television")).ToList();
            TestContext.Progress.WriteLine($"[tv-fire] {Name(spec)}: {string.Join(", ", sets)}");
            Assert.That(sets.Count, Is.EqualTo(1), "a set to watch");
            foreach (var m in sets.Where(m => m.Mounted)) Assert.That(fires.Any(f => OnFireWall(m, f, false)), Is.False, $"{m} is on the fireplace's wall");
        }

        /// <summary>The floor of a room as a body walks it -- blocked at 0.1 m by walls, stairs and holes, other rooms and
        /// the pieces in `pieces`; a body stands on a cell when no blocked cell's middle is within `r` of its middle, each
        /// way (r 0.3: the cells it covers are 0.7 m across, the player's capsule) -- gathered into the parts joined to one
        /// another: the part at each of `points` (the nearest clear cell's, within half a metre; -1: none).</summary>
        static int[] WalkParts(BuildingGenResult res, GenRoom g, IEnumerable<FurniturePlan> pieces, List<(float X, float Z)> points, float r)
        {
            const float G = 0.1f;
            float x0 = g.Rects.Min(q => q.X0) * Cell - 0.5f, z0 = g.Rects.Min(q => q.Z0) * Cell - 0.5f;
            int nx = (int)((g.Rects.Max(q => q.X1) * Cell + 0.5f - x0) / G), nz = (int)((g.Rects.Max(q => q.Z1) * Cell + 0.5f - z0) / G);
            var blocked = new bool[nx, nz];
            var boxes = WallsOn(res, g.Floor).SelectMany(SolidBoxes).ToList();
            var stairs = res.Stairs.Where(st => st.Floor == g.Floor).Select(Foot).Concat(Holes(res, g.Floor)).ToList();
            var here = pieces.ToList();
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    float x = x0 + (i + 0.5f) * G, z = z0 + (j + 0.5f) * G;
                    blocked[i, j] = !g.Holds(x, z) || boxes.Any(b => x >= b.X0 && x <= b.X1 && z >= b.Z0 && z <= b.Z1) || stairs.Any(b => x >= b.X0 && x <= b.X1 && z >= b.Z0 && z <= b.Z1)
                                    || here.Any(m => FurnitureCatalog.Covers(m.Name, m.X, m.Z, m.Yaw, x, z));
                }
            int k = (int)MathF.Floor(r / G + 1e-3f);
            bool Stands(int i, int j)
            {
                for (int a = i - k; a <= i + k; a++)
                    for (int b = j - k; b <= j + k; b++)
                        if (a < 0 || b < 0 || a >= nx || b >= nz || blocked[a, b]) return false;
                return true;
            }
            var part = new int[nx, nz];
            for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++) part[i, j] = -1;
            int parts = 0;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    if (part[i, j] >= 0 || !Stands(i, j)) continue;
                    var q = new Queue<(int, int)>(); q.Enqueue((i, j)); part[i, j] = parts;
                    while (q.Count > 0)
                    {
                        var (a, b) = q.Dequeue();
                        foreach (var (c, d) in new[] { (a + 1, b), (a - 1, b), (a, b + 1), (a, b - 1) })
                            if (c >= 0 && d >= 0 && c < nx && d < nz && part[c, d] < 0 && Stands(c, d)) { part[c, d] = parts; q.Enqueue((c, d)); }
                    }
                    parts++;
                }
            return points.Select(p =>
            {
                int pi = (int)((p.X - x0) / G), pj = (int)((p.Z - z0) / G);
                for (int rr = 0; rr <= 5; rr++)
                    for (int a = pi - rr; a <= pi + rr; a++)
                        for (int b = pj - rr; b <= pj + rr; b++)
                            if ((Math.Abs(a - pi) == rr || Math.Abs(b - pj) == rr) && a >= 0 && b >= 0 && a < nx && b < nz && part[a, b] >= 0) return part[a, b];
                return -1;
            }).ToArray();
        }

        // X: the potted plants (strawberry: "integrate the potted plant props astra made a while ago"), the four she approved
        // and never Potted_Plant_Floor_0. The upright and the tall one stand on the floor, square, their backs to a wall, in a
        // corner or just beside the couch; out of every window's and the fire's zone, from under anything hung lower than
        // their tops, off every other piece, and never closing a way through the room a player had (the doorways joined to
        // each other as they were). The table plant and the flowers stand on a kitchen counter's or a table's top, at its
        // height, within its edges, off what else is on it, and out of the windows' zones too (not on a counter under one). None to two a room, in the rooms the recipes have; and all four
        // appear, on the floor and on tops both. (Doorways, swings, stairs, tiles, walls and overlaps: FurnitureFollowsTheRules.)
        // BREAK IT: let a floor plant stand in front of a window (or the fire); drop the clearance; let a floor plant stand
        // anywhere in the room, not only in a corner or by the couch (the ways through close); place Potted_Plant_Floor_0.
        [Test]
        public void PottedPlantsStandWhereTheyShould()
        {
            var floorPlants = new HashSet<string> { "Potted_Plant_Tall_0", "Potted_Plant_Upright_0" };
            var topPlants = new HashSet<string> { "Potted_Plant_Table_0", "Potted_Flowers_0" };
            var hosts = new HashSet<string> { "Counter_0", "Table_Wood_0", "Table_Wood_1", "Table_Wood_3" };
            Assert.That(FurnitureCatalog.Sizes.ContainsKey("Potted_Plant_Floor_0"), Is.False, "the plant strawberry did not approve is not in the catalog");
            var bad = new List<string>();
            var names = new Dictionary<string, int>(); var kinds = new Dictionary<RoomKind, int>();
            int plants = 0, beside = 0, corner = 0, onTops = 0, walked = 0, twos = 0, closed = 0;
            foreach (var (spec, res) in All())
            {
                var pots = res.Furniture.Where(m => m.Name.StartsWith("Potted")).ToList();
                foreach (var g in pots.GroupBy(m => m.Room))
                {
                    if (g.Count() > 2) bad.Add($"{Name(spec)}: {g.Key} has {g.Count()} plants");
                    if (g.Count() == 2) twos++;
                    if (!BuildingGen.PlantRecipes.ContainsKey(g.Key.Kind)) bad.Add($"{Name(spec)}: a plant in a {g.Key.Kind}");
                    kinds[g.Key.Kind] = kinds.GetValueOrDefault(g.Key.Kind) + g.Count();
                }
                // out of the windows' zones -- and the fire's: an opening off the floor under a vent's height, across it and 0.25 m
                // either side of it (round 12: "widen it slightly"; the generator's PlantWindowGap is 0.3), 0.6 m in
                void InFrontOfWindow(FurniturePlan m, string nm)
                {
                    foreach (var w in WallsOn(res, m.Floor))
                    {
                        var n = Normal(w); float t = w.Thickness * 0.5f + 0.6f;
                        foreach (var o in w.Openings.Where(o => o.V >= 0.05f && o.V < 2.5f))
                        {
                            var p = At(w, o.U - 0.25f); var q = At(w, o.U1 + 0.25f);
                            var zone = (MathF.Min(MathF.Min(p.X, q.X) - n.X * t, MathF.Min(p.X, q.X) + n.X * t), MathF.Min(MathF.Min(p.Z, q.Z) - n.Z * t, MathF.Min(p.Z, q.Z) + n.Z * t),
                                        MathF.Max(MathF.Max(p.X, q.X) - n.X * t, MathF.Max(p.X, q.X) + n.X * t), MathF.Max(MathF.Max(p.Z, q.Z) - n.Z * t, MathF.Max(p.Z, q.Z) + n.Z * t));
                            if (Overlap(m.Footprint, zone, 0.005f)) bad.Add($"{nm} stands in front of {(o.Glazed ? "a window" : $"an opening (archetype {o.Archetype}, {o.V:0.00} m up)")}");
                        }
                    }
                }
                foreach (var m in pots)
                {
                    plants++;
                    names[m.Name] = names.GetValueOrDefault(m.Name) + 1;
                    string nm = $"{Name(spec)}: {m}";
                    int f = m.Floor;
                    var s = FurnitureCatalog.Sizes[m.Name];
                    if (topPlants.Contains(m.Name))
                    {
                        onTops++;
                        if (m.On == null || m.Mounted) { bad.Add($"{nm}, a small plant, is on no top"); continue; }
                        if (!hosts.Contains(m.On.Name)) bad.Add($"{nm} is on a {m.On.Name}");
                        if (MathF.Abs(m.Y - (m.On.Y + FurnitureCatalog.Sizes[m.On.Name].H)) > 0.01f) bad.Add($"{nm} floats or sinks on its {m.On.Name}");
                        // within the top's edges, in the host's frame
                        var hs = FurnitureCatalog.Sizes[m.On.Name]; var (hfx, hfz) = m.On.Front; var (hrx, hrz) = m.On.Right;
                        float along = (m.X - m.On.X) * hrx + (m.Z - m.On.Z) * hrz, back = (m.X - m.On.X) * hfx + (m.Z - m.On.Z) * hfz;
                        var corners = new[] { (along + s.X0, back + s.Y0), (along + s.X1, back + s.Y0), (along + s.X0, back + s.Y1), (along + s.X1, back + s.Y1) };
                        bool within = FurnitureCatalog.Round.TryGetValue(m.On.Name, out var rad)
                            ? corners.All(q => MathF.Sqrt(q.Item1 * q.Item1 + q.Item2 * q.Item2) <= rad)
                            : corners.All(q => q.Item1 >= hs.X0 && q.Item1 <= hs.X1 && q.Item2 >= hs.Y0 && q.Item2 <= hs.Y1);
                        if (!within) bad.Add($"{nm} hangs over its {m.On.Name}'s edge");
                        foreach (var o in res.Furniture.Where(o => o != m && o.On == m.On))
                            if (Overlap(m.Footprint, o.Footprint, 0.005f)) bad.Add($"{nm} stands in the {o.Name} on its {m.On.Name}");
                        InFrontOfWindow(m, nm);
                        continue;
                    }
                    if (!floorPlants.Contains(m.Name)) { bad.Add($"{nm}: not an approved plant"); continue; }
                    if (m.On != null || m.Mounted || m.Ceiling || MathF.Abs(m.Y - f * Storey) > 0.01f) { bad.Add($"{nm}, a floor plant, is not on the floor"); continue; }
                    if (MathF.Abs(m.Yaw / 90f - MathF.Round(m.Yaw / 90f)) > 1e-4f) bad.Add($"{nm} stands off square");
                    var fp = m.Footprint;
                    var walls = WallsOn(res, f).SelectMany(SolidBoxes).ToList();
                    bool WallAt(float x, float z) => walls.Any(b => x >= b.X0 && x <= b.X1 && z >= b.Z0 && z <= b.Z1);
                    // its back to a wall, and a side too or the couch beside it
                    var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                    bool backed = Enumerable.Range(0, 9).Count(k => WallAt(m.X + rx * (s.X0 + (s.X1 - s.X0) * (k + 0.5f) / 9) + fx * (s.Y0 - 0.15f), m.Z + rz * (s.X0 + (s.X1 - s.X0) * (k + 0.5f) / 9) + fz * (s.Y0 - 0.15f))) >= 7;
                    if (!backed) bad.Add($"{nm} has no wall at its back");
                    bool cornered = Enumerable.Range(0, 9).Count(k => WallAt(m.X + rx * (s.X1 + 0.15f) + fx * (s.Y0 + (s.Y1 - s.Y0) * (k + 0.5f) / 9), m.Z + rz * (s.X1 + 0.15f) + fz * (s.Y0 + (s.Y1 - s.Y0) * (k + 0.5f) / 9))) >= 5
                                    || Enumerable.Range(0, 9).Count(k => WallAt(m.X + rx * (s.X0 - 0.15f) + fx * (s.Y0 + (s.Y1 - s.Y0) * (k + 0.5f) / 9), m.Z + rz * (s.X0 - 0.15f) + fz * (s.Y0 + (s.Y1 - s.Y0) * (k + 0.5f) / 9))) >= 5;
                    var couch = res.Furniture.FirstOrDefault(c => c.Room == m.Room && c.Name.StartsWith("Couch") && SameWay(c, m)
                                                                  && Overlap(Grown(fp, BuildingGen.PlantBeside + 0.05f), c.Footprint, 0f));
                    if (cornered) corner++; else if (couch != null) beside++; else bad.Add($"{nm} is in no corner and beside no couch");
                    InFrontOfWindow(m, nm);
                    // nothing hung lower than its top over it; off every other piece
                    foreach (var o in res.Furniture.Where(o => o.Floor == f && o.Mounted && Overlap(Grown(fp, 0.05f), o.Footprint, 0f)))
                        if (o.Y - FurnitureCatalog.Sizes[o.Name].H < f * Storey + s.H + 0.05f) bad.Add($"{nm} stands under {o}");
                    foreach (var o in res.Furniture.Where(o => o != m && o.Floor == f && o.On == null && !o.Mounted && !o.Ceiling))
                        if (FurnitureCatalog.Intersects(m, o, -0.05f)) { bad.Add($"{nm} crowds {o}"); break; }
                }
                // the ways through: every room with a floor plant, its doorways joined with the plants as without them
                foreach (var g in pots.Where(m => m.On == null).Select(m => m.Room).Distinct())
                {
                    walked++;
                    var doors = Doors(res, g.Floor).SelectMany(d => new[] { (d.A, d.PA), (d.B, d.PB) }).Where(d => d.Item1 == g).Select(d => d.Item2).ToList();
                    if (doors.Count < 2) continue;
                    var rest = res.Furniture.Where(o => o.Room == g && o.On == null && !o.Mounted && !o.Ceiling && !o.Name.StartsWith("Potted")).ToList();
                    var without = WalkParts(res, g, rest, doors, 0.3f);
                    var with = WalkParts(res, g, rest.Concat(pots.Where(p => p.Room == g && p.On == null)), doors, 0.3f);
                    for (int i = 0; i < doors.Count; i++)
                        for (int j = i + 1; j < doors.Count; j++)
                            if (without[i] >= 0 && without[i] == without[j] && (with[i] < 0 || with[i] != with[j]))
                            {
                                closed++;
                                bad.Add($"{Name(spec)}: the plants in {g} close the way from ({doors[i].X:0.0},{doors[i].Z:0.0}) to ({doors[j].X:0.0},{doors[j].Z:0.0})");
                            }
                }
            }
            TestContext.Progress.WriteLine($"[plants] {plants} plants over {All().Count} buildings: {string.Join(", ", names.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))}; "
                                           + $"by room: {string.Join(", ", kinds.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))}; {onTops} on tops, {corner} in corners, {beside} beside a couch; "
                                           + $"{twos} rooms with two; {walked} rooms walked, {closed} ways closed; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(names.ContainsKey("Potted_Plant_Floor_0"), Is.False);
            foreach (var n in floorPlants.Concat(topPlants)) Assert.That(names.GetValueOrDefault(n), Is.GreaterThan(50), $"{n} is placed");
            Assert.That(beside, Is.GreaterThan(20), "plants stand beside couches");
            Assert.That(corner, Is.GreaterThan(200), "plants stand in corners");
            Assert.That(walked, Is.GreaterThan(500));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }

        // Y: the plants draw nothing from the furniture's dice (the Furnisher's Rng, shared by the whole building: one draw
        // would move every later room's furniture). Each building is furnished with no plants and with them, both without
        // the wall décor (which rightly keeps clear of a plant), and every piece that is not a plant must come out identical:
        // name, place, turn, floor, in the same order.
        // BREAK IT: draw once from the Furnisher's Rng in Plants.
        [Test]
        public void PottedPlantsKeepToTheirOwnDraws()
        {
            var bad = new List<string>();
            int compared = 0, plants = 0, buildings = 0;
            try
            {
                BuildingGen.DecorOverride = Array.Empty<DecorRow>();
                foreach (var kind in new[] { BuildingKind.Residential, BuildingKind.Commercial, BuildingKind.Apartment })
                    for (int seed = 0; seed < (kind == BuildingKind.Apartment ? 3 : 12); seed++)
                        foreach (var spec in new[] { new BuildingGenSpec { Seed = seed, Kind = kind }, new BuildingGenSpec { Seed = seed, Kind = kind, MinRooms = 10, MaxFloors = 3, MaxRoomsPerFloor = 4 } })
                        {
                            buildings++;
                            BuildingGen.NoPlants = true;
                            var plain = BuildingGen.Generate(spec);
                            BuildingGen.NoPlants = false;
                            var potted = BuildingGen.Generate(spec);
                            var rest = potted.Furniture.Where(m => !m.Name.StartsWith("Potted")).ToList();
                            plants += potted.Furniture.Count - rest.Count;
                            string Key(FurniturePlan m) => FormattableString.Invariant($"{m.Name} {m.X:R} {m.Y:R} {m.Z:R} {m.Yaw:R} {m.Floor} {m.Mounted} {m.Ceiling} {m.On?.Name}");
                            compared += plain.Furniture.Count;
                            if (plain.Furniture.Any(m => m.Name.StartsWith("Potted"))) bad.Add($"{Name(spec)}: a plant with the plants off");
                            if (rest.Count != plain.Furniture.Count) bad.Add($"{Name(spec)}: {plain.Furniture.Count} pieces without plants, {rest.Count} besides them");
                            else
                                for (int i = 0; i < rest.Count; i++)
                                    if (Key(rest[i]) != Key(plain.Furniture[i])) { bad.Add($"{Name(spec)}: piece {i} {Key(plain.Furniture[i])} became {Key(rest[i])}"); break; }
                        }
            }
            finally { BuildingGen.DecorOverride = null; BuildingGen.NoPlants = false; }
            TestContext.Progress.WriteLine($"[plant-draws] {compared} pieces compared over {buildings} buildings, {plants} plants besides; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(compared, Is.GreaterThan(1000));
            Assert.That(plants, Is.GreaterThan(50));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }
    }
}
