using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>A piece of furniture: a retail prop (game/content/objects, by name) whose OBJ origin stands at (X, Y, Z) in
    /// building-local metres, turned as EditorObjects.Upright(Yaw) turns it -- its front (the OBJ's +Y: a bed's foot, a
    /// couch's seat, a fridge's door, a screen) facing (-sin Yaw, -cos Yaw), its width (the OBJ's +X) along
    /// (cos Yaw, -sin Yaw); the same frame as StairPlan's Back and Right. Y is the floor's top, or the top of whatever
    /// it stands on (a monitor on a desk).</summary>
    public sealed class FurniturePlan
    {
        public string Name;
        public float X, Y, Z, Yaw;
        public int Floor;
        public GenRoom Room;
        /// <summary>Set when it stands on another piece rather than on the floor.</summary>
        public FurniturePlan On;
        /// <summary>Hung on a wall (a flat television), its origin at the height it hangs: no floor of its own.</summary>
        public bool Mounted;
        public (float X, float Z) Front { get { var (c, s) = PlanTrig.CosSin(Yaw); return (-s, -c); } }
        public (float X, float Z) Right { get { var (c, s) = PlanTrig.CosSin(Yaw); return (c, -s); } }
        public (float X0, float Z0, float X1, float Z1) Footprint => FurnitureCatalog.Footprint(Name, X, Z, Yaw);
        public override string ToString() => $"{Name} at ({X:0.00},{Z:0.00}) yaw {Yaw:0} in {Room}";
    }

    /// <summary>The props the furnisher uses, with their OBJ extents (x across, y front-to-back, z up -- the files are
    /// Z-up and face +Y) read off game/content/objects/*.obj; FurnitureCatalogTests holds each to its file.</summary>
    public static class FurnitureCatalog
    {
        public readonly struct Size
        {
            public readonly float X0, X1, Y0, Y1, H;
            public Size(float x0, float x1, float y0, float y1, float h) { X0 = x0; X1 = x1; Y0 = y0; Y1 = y1; H = h; }
        }

        public static readonly IReadOnlyDictionary<string, Size> Sizes = new Dictionary<string, Size>
        {
            ["Bed_0"] = new(-1.52f, 1.52f, -1.94f, 1.94f, 1.67f),          // double; headboard at -Y
            ["Bed_1"] = new(-1.00f, 1.00f, -1.94f, 1.94f, 1.67f),          // single
            ["Wardrobe_0"] = new(-1.03f, 1.03f, -0.38f, 0.50f, 2.60f),
            ["Wardrobe_1"] = new(-1.03f, 1.03f, -0.38f, 0.50f, 2.60f),
            ["Lamp_1"] = new(-0.37f, 0.37f, -0.37f, 0.37f, 2.30f),         // floor lamp
            ["Coat_0"] = new(-0.26f, 0.32f, -0.32f, 0.26f, 1.99f),
            ["Couch_0"] = new(-1.50f, 1.50f, -0.75f, 0.78f, 1.50f),
            ["Couch_1"] = new(-1.10f, 1.10f, -0.75f, 0.78f, 1.50f),
            ["Television_1"] = new(-0.50f, 0.50f, -0.50f, 0.53f, 1.70f),   // a CRT set, screen to +Y, on a table
            ["Television_0"] = new(-1.88f, 1.88f, -0.10f, 0.13f, 1.00f),   // a flat one hung on a wall: origin at its middle
            ["Table_Wood_3"] = new(-0.88f, 0.88f, -0.84f, 0.84f, 1.19f),
            ["Shelf_2"] = new(-1.38f, 1.38f, -0.38f, 0.50f, 2.60f),        // bookcase
            ["Table_Wood_0"] = new(-0.94f, 0.94f, -1.25f, 1.25f, 1.19f),
            ["Table_Wood_1"] = new(-1.56f, 1.56f, -1.56f, 1.56f, 1.19f),
            ["Table_Wood_2"] = new(-0.88f, 0.88f, -0.84f, 0.84f, 1.19f),   // the desk: there is no desk mesh
            ["Table_Metal_0"] = new(-0.94f, 0.94f, -1.50f, 1.50f, 1.19f),  // a workbench
            ["Chair_Wood_0"] = new(-0.47f, 0.47f, -0.47f, 0.47f, 1.81f),   // the dining chair; back at -Y
            ["Chair_Wood_1"] = new(-0.47f, 0.47f, -0.55f, 0.47f, 1.76f),
            ["Fridge_0"] = new(-0.66f, 0.66f, -0.50f, 0.30f, 2.50f),
            ["Oven_0"] = new(-0.75f, 0.75f, -0.75f, 0.62f, 1.54f),          // the stove
            ["Counter_0"] = new(-0.75f, 0.75f, -0.50f, 0.62f, 1.35f),
            ["Counter_1"] = new(-0.75f, 0.75f, -0.50f, 0.62f, 1.83f),       // the sink
            ["Disher_0"] = new(-0.75f, 0.75f, -0.75f, 0.62f, 1.50f),
            ["Tub_0"] = new(-0.85f, 0.85f, -1.67f, 1.60f, 1.73f),
            ["Washer_0"] = new(-0.75f, 0.75f, -0.75f, 0.75f, 1.50f),
            ["Dryer_0"] = new(-0.75f, 0.75f, -0.75f, 0.75f, 1.78f),
            ["Files_0"] = new(-0.39f, 0.39f, -0.47f, 0.56f, 2.14f),
            ["Computer_3"] = new(-0.65f, 0.65f, -0.11f, 0.23f, 1.18f),     // a monitor, on a desk
            ["Crate_0"] = new(-0.62f, 0.62f, -0.62f, 0.62f, 1.25f),
            ["Crate_3"] = new(-0.94f, 0.94f, -0.94f, 0.94f, 1.88f),
            ["Barrel_0"] = new(-0.54f, 0.54f, -0.54f, 0.54f, 1.28f),
            ["Pallet_Wood_0"] = new(-1.47f, 1.48f, -1.50f, 1.50f, 0.27f),
            ["Cardboard_0"] = new(-0.43f, 0.43f, -0.28f, 0.28f, 0.43f),
            ["Shelf_0"] = new(-0.94f, 0.94f, -1.25f, 1.25f, 2.00f),         // an open rack, long along y
            ["Shelf_3"] = new(-0.75f, 0.76f, -1.07f, 1.07f, 1.77f),
            ["Propane_0"] = new(-0.25f, 0.25f, -0.25f, 0.25f, 0.58f),
            ["Shelf_1"] = new(-2.50f, 2.50f, -0.74f, 0.74f, 2.42f),         // a shop's gondola
            ["Checkout_0"] = new(-2.00f, 1.00f, -1.00f, 1.00f, 1.27f),      // origin off centre
            ["Register_0"] = new(-0.40f, 0.40f, -0.35f, 0.35f, 0.80f),      // a till, on the checkout
            ["Cooler_0"] = new(-0.75f, 0.75f, -0.50f, 0.50f, 2.50f),
            ["Ice_Box_0"] = new(-1.00f, 1.00f, -0.50f, 0.50f, 2.47f),
            ["Craft_01"] = new(-0.87f, 0.87f, -0.65f, 0.65f, 1.76f),
        };

        /// <summary>Props that are round in plan: a disc of this radius about the origin, however turned.</summary>
        public static readonly IReadOnlyDictionary<string, float> Round = new Dictionary<string, float> { ["Table_Wood_1"] = 1.5625f };

        /// <summary>Is plan point (px, pz) under the prop at (x, z) turned by yaw -- its true outline (a turned box, or a
        /// disc), not the axis-aligned box round it?</summary>
        public static bool Covers(string name, float x, float z, float yaw, float px, float pz)
        {
            float dx = px - x, dz = pz - z;
            if (Round.TryGetValue(name, out var r)) return dx * dx + dz * dz <= r * r;
            var s = Sizes[name];
            var (c, sn) = PlanTrig.CosSin(yaw);
            float mx = dx * c - dz * sn, my = -dx * sn - dz * c;   // along right (c, -sn) and front (-sn, -c)
            return mx >= s.X0 && mx <= s.X1 && my >= s.Y0 && my <= s.Y1;
        }

        /// <summary>Do two props' true outlines overlap by more than `eps`? Discs by their centres, a disc and a box by the
        /// box's nearest point, two boxes by separating axes.</summary>
        public static bool Intersects(FurniturePlan a, FurniturePlan b, float eps = 0.01f)
        {
            bool ra = Round.TryGetValue(a.Name, out var rA), rb = Round.TryGetValue(b.Name, out var rB);
            if (ra && rb) return MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)) < rA + rB - eps;
            if (ra || rb)
            {
                var (d, r) = ra ? (b, rA) : (a, rB); var c = ra ? a : b;
                var s = Sizes[d.Name]; var (rx, rz) = d.Right; var (fx, fz) = d.Front;
                float dx = c.X - d.X, dz = c.Z - d.Z;
                float mx = dx * rx + dz * rz, my = dx * fx + dz * fz;
                float qx = Math.Clamp(mx, s.X0, s.X1) - mx, qy = Math.Clamp(my, s.Y0, s.Y1) - my;
                return MathF.Sqrt(qx * qx + qy * qy) < r - eps;
            }
            var ca = Corners(a); var cb = Corners(b);
            foreach (var m in new[] { a, b })
                foreach (var (ax, az) in new[] { m.Right, m.Front })
                {
                    float a0 = ca.Min(p => p.X * ax + p.Z * az), a1 = ca.Max(p => p.X * ax + p.Z * az);
                    float b0 = cb.Min(p => p.X * ax + p.Z * az), b1 = cb.Max(p => p.X * ax + p.Z * az);
                    if (MathF.Min(a1, b1) - MathF.Max(a0, b0) <= eps) return false;
                }
            return true;
        }

        static (float X, float Z)[] Corners(FurniturePlan m)
        {
            var s = Sizes[m.Name]; var (rx, rz) = m.Right; var (fx, fz) = m.Front;
            return new[] { (s.X0, s.Y0), (s.X1, s.Y0), (s.X1, s.Y1), (s.X0, s.Y1) }.Select(q => (m.X + rx * q.Item1 + fx * q.Item2, m.Z + rz * q.Item1 + fz * q.Item2)).ToArray();
        }

        /// <summary>A prop's footprint in plan at (x, z) turned by yaw, as the axis-aligned box round it (exact at right
        /// angles; round it when turned or round -- <see cref="Covers"/> is the outline).</summary>
        public static (float X0, float Z0, float X1, float Z1) Footprint(string name, float x, float z, float yaw)
        {
            var s = Sizes[name];
            var (c, sn) = PlanTrig.CosSin(yaw);
            // right (c, -sn) * mx + front (-sn, -c) * my, at the box's corners
            float ax = c * s.X0, bx = c * s.X1, ay = -sn * s.Y0, by = -sn * s.Y1;   // x from mx, x from my
            float az = -sn * s.X0, bz = -sn * s.X1, cy = -c * s.Y0, dy = -c * s.Y1;  // z from mx, z from my
            return (x + MathF.Min(ax, bx) + MathF.Min(ay, by), z + MathF.Min(az, bz) + MathF.Min(cy, dy),
                    x + MathF.Max(ax, bx) + MathF.Max(ay, by), z + MathF.Max(az, bz) + MathF.Max(cy, dy));
        }
    }

    public static partial class BuildingGen
    {
        /// <summary>Taller than this, a piece may not stand in front of a window: past eye height it blinds it. A
        /// counter, a sink, a tub, a bed, a couch go under one; a fridge, a wardrobe, a bookcase, a rack do not.</summary>
        public const float TallFurniture = 1.85f;

        /// <summary>How far in front of a piece its doors or drawers swing out -- kept clear of everything but your feet
        /// (strawberry: "wardrobes need unblocked space in front of them for the doors to open"). 0: nothing opens.</summary>
        public static float Swing(string name) => name switch
        {
            "Wardrobe_0" or "Wardrobe_1" => 1.0f,
            "Fridge_0" or "Fridge_1" => 0.9f,
            "Oven_0" or "Disher_0" or "Washer_0" or "Dryer_0" or "Files_0" or "Cooler_0" or "Ice_Box_0" => 0.8f,
            _ => 0f,
        };
        /// <summary>A flat television hangs with its middle this high; a CRT stands on a table. How far a couch may be
        /// from each and still watch it (strawberry: "couch should have a sight line within a max distance of a tv set,
        /// different for both types").</summary>
        public const float MountHeight = 2.2f, WallTvReach = 8f, CrtReach = 4.5f, TvNear = 2f;
        /// <summary>The kitchen's working triangle, fridge to sink to stove and back, at most this far round. The real rule
        /// is 26 ft (7.9 m) for furniture this game builds about twice the size (a counter 1.5 m wide, a real one 0.6).</summary>
        public const float TriangleRound = 12.5f;

        /// <summary>Furnish every room (strawberry: "work on placement rules/guides for the furniture in the rooms. should
        /// try for good feng shui if possible"). Each kind of room has its pieces, most important first, each placed by its
        /// own rules; and everywhere: a piece stands wholly in its room on clear floor, its back to a wall where it has
        /// one; never in a doorway's swing (1.3 m in front of any door), on the stairs, on the tile at a flight's foot or
        /// top, or over a hole; nothing taller than <see cref="TallFurniture"/> in front of a window; and after every piece
        /// the room's doors, its stairs and the front of every piece in it are still joined by clear floor. Its own seed,
        /// so furnishing never moves a wall.</summary>
        /// <summary>Debugging only: told each placement decision as it is made (null: nothing is traced).</summary>
        public static Action<string> FurnishTrace;

        static void Furnish(BuildingGenResult res, int seed)
        {
            var rng = new Rng(seed ^ 0x51ED);
            for (int f = 0; f < res.Floors; f++)
            {
                var fp = new FloorPlan(res, f);
                foreach (var room in res.Layout.Where(r => r.Floor == f))
                    new Furnisher(res, fp, room, rng).Run();
            }
        }

        /// <summary>One floor at 0.1 m: what is solid (walls; stairs, landings and holes; furniture as it goes in), and
        /// what must stay clear but can be walked on (doorways, the tiles at the stairs) or seen through (windows).</summary>
        sealed class FloorPlan
        {
            public const float Step = 0.1f;
            public const byte Free = 0, Wall = 1, Furn = 2, Stair = 3;
            public readonly float Ox, Oz;
            public readonly int Nx, Nz, F;
            public readonly byte[,] State;
            public readonly bool[,] KeepDoor, KeepTall;
            public readonly List<(float X, float Z, GenRoom Room)> Targets = new();
            /// <summary>The stairs, landings and holes on this floor, exactly.</summary>
            public readonly List<(float X0, float Z0, float X1, float Z1)> StairRects = new();
            readonly BuildingGenResult _res;

            public FloorPlan(BuildingGenResult res, int f)
            {
                _res = res; F = f;
                Ox = -1f; Oz = -1f;
                Nx = (int)MathF.Ceiling((res.CellsX * Cell + 2f) / Step);
                Nz = (int)MathF.Ceiling((res.CellsZ * Cell + 2f) / Step);
                State = new byte[Nx, Nz]; KeepDoor = new bool[Nx, Nz]; KeepTall = new bool[Nx, Nz];
                const float M = WallOpenings.StairHoleMargin;
                int FloorOf(float y) => (int)MathF.Round(y / Storey);
                var walls = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && FloorOf(w.Y) == f && w.Height > Storey - 0.01f).ToList();
                foreach (var w in walls) Fill(Box(w), Wall);
                // the stairs: flights on this floor, the landings, and the holes in this floor
                float Rise(StairPlan s) => s.Rise > 0f ? s.Rise : Storey;
                foreach (var s in res.Stairs.Where(s => s.Floor == f)) Fill(Foot(s), Stair);
                foreach (var l in res.Walls.Where(w => w.Kind == SurfaceKind.Stairs && FloorOf(w.Y) == f)) Fill((l.X, l.Z - l.Height, l.X + l.Length, l.Z), Stair);
                foreach (var s in res.Stairs.Where(s => s.Floor == f - 1 && Rise(s) >= Storey - 0.01f)) Fill(Grow(Foot(s), M), Stair);
                foreach (var sl in res.Walls.Where(w => w.Kind == SurfaceKind.Floor && MathF.Abs(w.Y + w.Thickness * 0.5f - f * Storey) < 0.05f))
                    foreach (var o in sl.Openings) Fill((sl.X + o.U, sl.Z - o.V - o.Height, sl.X + o.U + o.Width, sl.Z - o.V), Stair);
                // the tile at a flight's foot and at its top stay clear, and are where its users stand
                foreach (var s in res.Stairs)
                {
                    var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                    float mx = s.X + rx * s.Width * 0.5f, mz = s.Z + rz * s.Width * 0.5f;
                    if (s.Floor == f && MathF.Abs(s.Y - f * Storey) < 0.01f)
                    {
                        Keep(Tile(s.X, s.Z, bx, bz, rx, rz, -1f, s.Width), KeepDoor);
                        Targets.Add((mx - bx * 0.5f, mz - bz * 0.5f, null));
                    }
                    if (s.Floor == f - 1 && MathF.Abs(s.Y + Rise(s) - f * Storey) < 0.01f)
                    {
                        float tx = s.X + bx * s.Run, tz = s.Z + bz * s.Run;
                        Keep(Tile(tx, tz, bx, bz, rx, rz, +1f, s.Width), KeepDoor);
                        Targets.Add((mx + bx * (s.Run + M + 0.4f), mz + bz * (s.Run + M + 0.4f), null));
                    }
                }
                // doorways: 1.3 m either side of every opening down to the floor (a vehicle door: 6 m, its lane; a wide
                // open doorway is still a doorway), the
                // opening's width and a little; windows: 0.7 m in front of the glass
                foreach (var w in walls)
                {
                    var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                    var (dx, dz) = (wc, -ws);
                    var (nx, nz) = (ws, wc);
                    foreach (var o in w.Openings)
                    {
                        bool door = o.V < 0.05f;
                        if (!door && o.V >= 2.5f) continue;   // a vent or a bathroom's slit: over the tallest piece's head
                        float pad = door ? 0.3f : 0.1f, deep = door ? (o.Archetype == ArchGarage ? 6f : 1.3f) : 0.7f;
                        float u0 = o.U - pad, u1 = o.U + o.Width + pad;
                        float ax = w.X + dx * u0, az = w.Z + dz * u0, bx2 = w.X + dx * u1, bz2 = w.Z + dz * u1;
                        float t = w.Thickness * 0.5f + deep;
                        var zone = (MathF.Min(MathF.Min(ax, bx2) - nx * t, MathF.Min(ax, bx2) + nx * t), MathF.Min(MathF.Min(az, bz2) - nz * t, MathF.Min(az, bz2) + nz * t),
                                    MathF.Max(MathF.Max(ax, bx2) - nx * t, MathF.Max(ax, bx2) + nx * t), MathF.Max(MathF.Max(az, bz2) - nz * t, MathF.Max(az, bz2) + nz * t));
                        Keep(zone, door ? KeepDoor : KeepTall);
                        if (door)
                        {
                            float cx = w.X + dx * (o.U + o.Width * 0.5f), cz = w.Z + dz * (o.U + o.Width * 0.5f), d = w.Thickness * 0.5f + 0.5f;
                            Targets.Add((cx + nx * d, cz + nz * d, null));
                            Targets.Add((cx - nx * d, cz - nz * d, null));
                        }
                    }
                }
            }

            static (float, float, float, float) Box(WallPlan w)
            {
                var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                float dx = wc, dz = -ws, t = w.Thickness * 0.5f;
                float ex = w.X + dx * w.Length, ez = w.Z + dz * w.Length;
                return (MathF.Min(w.X, ex) - t, MathF.Min(w.Z, ez) - t, MathF.Max(w.X, ex) + t, MathF.Max(w.Z, ez) + t);
            }
            static (float, float, float, float) Foot(StairPlan s)
            {
                var (bx, bz) = s.Back; var (rx, rz) = s.Right;
                float cx = s.X + bx * s.Run + rx * s.Width, cz = s.Z + bz * s.Run + rz * s.Width;
                return (MathF.Min(s.X, cx), MathF.Min(s.Z, cz), MathF.Max(s.X, cx), MathF.Max(s.Z, cz));
            }
            static (float, float, float, float) Grow((float X0, float Z0, float X1, float Z1) a, float m) => (a.X0 - m, a.Z0 - m, a.X1 + m, a.Z1 + m);
            static (float, float, float, float) Tile(float ox, float oz, float bx, float bz, float rx, float rz, float dir, float width)
            {
                float ex = ox + bx * Cell * dir + rx * width, ez = oz + bz * Cell * dir + rz * width;
                return (MathF.Min(ox, ex), MathF.Min(oz, ez), MathF.Max(ox, ex), MathF.Max(oz, ez));
            }

            public float Cx(int i) => Ox + (i + 0.5f) * Step;
            public float Cz(int j) => Oz + (j + 0.5f) * Step;
            public int I(float x) => Math.Clamp((int)MathF.Floor((x - Ox) / Step), 0, Nx - 1);
            public int J(float z) => Math.Clamp((int)MathF.Floor((z - Oz) / Step), 0, Nz - 1);
            /// <summary>The cells whose centres lie inside a box.</summary>
            public IEnumerable<(int I, int J)> Cells((float X0, float Z0, float X1, float Z1) a)
            {
                int i0 = Math.Max(0, (int)MathF.Ceiling((a.X0 - Ox) / Step - 0.5f)), i1 = Math.Min(Nx - 1, (int)MathF.Floor((a.X1 - Ox) / Step - 0.5f));
                int j0 = Math.Max(0, (int)MathF.Ceiling((a.Z0 - Oz) / Step - 0.5f)), j1 = Math.Min(Nz - 1, (int)MathF.Floor((a.Z1 - Oz) / Step - 0.5f));
                for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) yield return (i, j);
            }
            void Fill((float, float, float, float) a, byte v)
            {
                foreach (var (i, j) in Cells(a)) if (State[i, j] == Free || v == Wall) State[i, j] = v;
                if (v == Stair) StairRects.Add(a);
            }
            void Keep((float, float, float, float) a, bool[,] k) { foreach (var (i, j) in Cells(a)) k[i, j] = true; }
        }

        /// <summary>One room's furnishing: its recipe, run against the floor's plan.</summary>
        sealed class Furnisher
        {
            readonly BuildingGenResult _res; readonly FloorPlan _p; readonly GenRoom _room; readonly Rng _rng;
            readonly List<(float X, float Z)> _targets;
            readonly List<FurniturePlan> _mine = new();
            readonly float _area;

            // the room's box of cells, which of them are its own, and running sums of what a short piece and a tall one
            // may not stand on -- so a footprint is checked in four lookups, not cell by cell
            readonly int _i0, _j0, _ni, _nj;
            readonly bool[,] _inRoom;
            readonly int[,] _bad, _badTall;
            readonly Dictionary<(int, int), List<(float, float, float)>> _faces = new();

            public Furnisher(BuildingGenResult res, FloorPlan p, GenRoom room, Rng rng)
            {
                _res = res; _p = p; _room = room; _rng = rng;
                _targets = p.Targets.Where(t => room.Holds(t.X, t.Z)).Select(t => (t.X, t.Z)).ToList();
                _area = room.Cells * Cell * Cell;
                _i0 = Math.Max(0, p.I(room.Rects.Min(r => r.X0) * Cell) - 2); _j0 = Math.Max(0, p.J(room.Rects.Min(r => r.Z0) * Cell) - 2);
                int i1 = Math.Min(p.Nx - 1, p.I(room.Rects.Max(r => r.X1) * Cell) + 2), j1 = Math.Min(p.Nz - 1, p.J(room.Rects.Max(r => r.Z1) * Cell) + 2);
                _ni = i1 - _i0 + 1; _nj = j1 - _j0 + 1;
                _inRoom = new bool[_ni, _nj];
                foreach (var r in room.Rects)
                    foreach (var (i, j) in p.Cells((r.X0 * Cell, r.Z0 * Cell, r.X1 * Cell, r.Z1 * Cell)))
                        if (i >= _i0 && j >= _j0 && i < _i0 + _ni && j < _j0 + _nj) _inRoom[i - _i0, j - _j0] = true;
                _bad = new int[_nj, _ni + 1]; _badTall = new int[_nj, _ni + 1];
                for (int b = 0; b < _nj; b++) Recount(b);
            }

            /// <summary>Row b's running counts of the cells a short piece, and a tall one, may not stand on.</summary>
            void Recount(int b)
            {
                int j = _j0 + b;
                for (int a = 0; a < _ni; a++)
                {
                    int i = _i0 + a;
                    int bad = !_inRoom[a, b] || _p.State[i, j] != FloorPlan.Free || _p.KeepDoor[i, j] ? 1 : 0;
                    int tall = bad == 1 || _p.KeepTall[i, j] ? 1 : 0;
                    _bad[b, a + 1] = _bad[b, a] + bad;
                    _badTall[b, a + 1] = _badTall[b, a] + tall;
                }
            }
            void Recount((float X0, float Z0, float X1, float Z1) a)
            {
                int b0 = Math.Max(0, _p.J(a.Z0) - _j0 - 1), b1 = Math.Min(_nj - 1, _p.J(a.Z1) - _j0 + 1);
                for (int b = b0; b <= b1; b++) Recount(b);
            }
            bool InRoom(int i, int j) => i >= _i0 && j >= _j0 && i < _i0 + _ni && j < _j0 + _nj && _inRoom[i - _i0, j - _j0];

            static readonly (int X, int Z)[] Fronts = { (0, -1), (-1, 0), (0, 1), (1, 0) };   // yaw 0, 90, 180, 270
            static (int X, int Z) FrontOf(float yaw) => Fronts[((int)MathF.Round(yaw / 90f) % 4 + 4) % 4];
            static (int X, int Z) RightOf(float yaw) { var (fx, fz) = FrontOf(yaw); return (-fz, fx); }

            enum Side { Back, Left, Right }

            public void Run()
            {
                switch (_room.Kind)
                {
                    case RoomKind.Bedroom: Bedroom(); break;
                    case RoomKind.LivingRoom: LivingRoom(); break;
                    case RoomKind.DiningRoom: Dining(); break;
                    case RoomKind.Kitchen: Kitchen(); break;
                    case RoomKind.Bathroom: Bathroom(); break;
                    case RoomKind.Office: Office(); break;
                    case RoomKind.Closet: AlongWalls(new[] { "Wardrobe_1", "Shelf_3" }, 2); break;
                    case RoomKind.Storage: Storage(); break;
                    case RoomKind.Garage: Garage(); break;
                    case RoomKind.Warehouse: Warehouse(); break;
                    case RoomKind.Workshop: Workshop(); break;
                    case RoomKind.Shop: Shop(); break;
                    case RoomKind.Hall: Hall(); break;
                }
                Jitter();
            }

            /// <summary>What gets moved in use stands a little off square (strawberry: "allow furniture to have slight yaw
            /// rotation applied, any furniture that could realistically move during normal use. (tables chairs couches.)"):
            /// a chair turned up to 8 degrees and pushed back from its table up to 0.15 m, a table up to 4, a couch 2.5 --
            /// each only where it stays clear and the room stays walkable; nothing with something stood on it.</summary>
            void Jitter()
            {
                foreach (var m in _mine.ToList())
                {
                    if (m.On != null || m.Mounted || _res.Furniture.Any(t => t.On == m)) continue;
                    float turn = m.Name.StartsWith("Chair") ? 8f : m.Name.StartsWith("Table") ? 4f : m.Name.StartsWith("Couch") ? 2.5f : 0f;
                    if (turn == 0f) continue;
                    var s = FurnitureCatalog.Sizes[m.Name];
                    float d = (float)((_rng.Int(2001) - 1000) / 1000.0) * turn;
                    var (fx, fz) = m.Front;
                    // a couch against its wall swings out from it as it turns; a chair is pushed back
                    float push = m.Name.StartsWith("Couch") ? (s.X1 - s.X0) * 0.5f * MathF.Abs(PlanTrig.CosSin(d).Sin) + 0.01f
                               : m.Name.StartsWith("Chair") ? -_rng.Int(16) * 0.01f : 0f;
                    float nx = m.X + fx * push, nz = m.Z + fz * push, nyaw = (m.Yaw + d + 360f) % 360f;
                    var before = m.Footprint;
                    var old = Under(m.Name, m.X, m.Z, m.Yaw).Where(c => _p.State[c.I, c.J] == FloorPlan.Furn).ToList();
                    foreach (var (i, j) in old) _p.State[i, j] = FloorPlan.Free;
                    Recount(before);
                    var after = FurnitureCatalog.Footprint(m.Name, nx, nz, nyaw);
                    bool ok = Clear(m.Name, nx, nz, nyaw) && !HitsPiece(m.Name, nx, nz, nyaw, m) && !_p.StairRects.Any(r => Overlaps(after, r));
                    if (ok)
                    {
                        var (ox, oz, oyaw) = (m.X, m.Z, m.Yaw);
                        m.X = nx; m.Z = nz; m.Yaw = nyaw;
                        var cells = Under(m.Name, nx, nz, nyaw).Where(c => _p.State[c.I, c.J] == FloorPlan.Free).ToList();
                        foreach (var (i, j) in cells) _p.State[i, j] = FloorPlan.Furn;
                        if (Connected()) { Recount(after); continue; }
                        foreach (var (i, j) in cells) _p.State[i, j] = FloorPlan.Free;
                        m.X = ox; m.Z = oz; m.Yaw = oyaw;
                    }
                    foreach (var (i, j) in old) _p.State[i, j] = FloorPlan.Furn;
                    Recount(before);
                }
            }

            // ---- the primitives ----------------------------------------------------------------------------------

            /// <summary>Square to the walls and not round: its box is its outline.</summary>
            static bool Boxy(string name, float yaw) => !FurnitureCatalog.Round.ContainsKey(name) && MathF.Abs(yaw / 90f - MathF.Round(yaw / 90f)) < 1e-4f;

            /// <summary>The floor cells under a piece's outline.</summary>
            IEnumerable<(int I, int J)> Under(string name, float x, float z, float yaw)
            {
                var cells = _p.Cells(FurnitureCatalog.Footprint(name, x, z, yaw));
                return Boxy(name, yaw) ? cells : cells.Where(c => FurnitureCatalog.Covers(name, x, z, yaw, _p.Cx(c.I), _p.Cz(c.J)));
            }

            /// <summary>Clear floor under a piece's outline (see the box overload).</summary>
            bool Clear(string name, float x, float z, float yaw)
            {
                var fp = FurnitureCatalog.Footprint(name, x, z, yaw); float h = FurnitureCatalog.Sizes[name].H;
                if (Boxy(name, yaw)) return Clear(fp, h);
                var t = h > TallFurniture ? _badTall : _bad;
                int n = 0;
                foreach (var (i, j) in Under(name, x, z, yaw))
                {
                    int a = i - _i0, b = j - _j0;
                    if (a < 0 || b < 0 || a >= _ni || b >= _nj || t[b, a + 1] - t[b, a] != 0) return false;
                    n++;
                }
                return n > 0;
            }

            /// <summary>Does a piece at (x, z, yaw) run into one already in this room?</summary>
            bool HitsPiece(string name, float x, float z, float yaw, FurniturePlan except = null)
            {
                var me = new FurniturePlan { Name = name, X = x, Z = z, Yaw = yaw };
                var fp = me.Footprint;
                return _mine.Any(o => o != except && o.On == null && !o.Mounted && Overlaps(fp, o.Footprint)
                                      && (Boxy(name, yaw) && Boxy(o.Name, o.Yaw) || FurnitureCatalog.Intersects(me, o, 0f)));
            }

            /// <summary>Clear floor of this room under the whole footprint: inside the room, not solid, out of the
            /// doorways and stair tiles, and if tall out of the windows.</summary>
            bool Clear((float X0, float Z0, float X1, float Z1) a, float h)
            {
                int i0 = (int)MathF.Ceiling((a.X0 - _p.Ox) / FloorPlan.Step - 0.5f) - _i0, i1 = (int)MathF.Floor((a.X1 - _p.Ox) / FloorPlan.Step - 0.5f) - _i0;
                int j0 = (int)MathF.Ceiling((a.Z0 - _p.Oz) / FloorPlan.Step - 0.5f) - _j0, j1 = (int)MathF.Floor((a.Z1 - _p.Oz) / FloorPlan.Step - 0.5f) - _j0;
                if (i1 < i0 || j1 < j0) return false;
                if (i0 < 0 || j0 < 0 || i1 >= _ni || j1 >= _nj) return false;   // past the room's box: not in the room
                var t = h > TallFurniture ? _badTall : _bad;
                for (int b = j0; b <= j1; b++) if (t[b, i1 + 1] - t[b, i0] != 0) return false;
                return true;
            }

            /// <summary>Every door, stair end and piece front of the room still joined by clear floor.</summary>
            bool Connected()
            {
                // every door and stair end, and some side of every piece you use
                var cells = new List<int>();
                foreach (var (x, z) in _targets)
                {
                    var c = Nearest(x, z);
                    if (c < 0) return false;
                    cells.Add(c);
                }
                var sides = new List<int[]>();
                foreach (var m in _mine.Where(m => m.On == null && !m.Mounted && Access(m.Name)))
                {
                    var c = AccessPoints(m).Select(p => Nearest(p.X, p.Z)).Where(i => i >= 0).ToArray();
                    if (c.Length == 0) return false;
                    sides.Add(c);
                }
                if (cells.Count == 0 && sides.Count > 0) cells.Add(sides[0][0]);
                if (cells.Count == 0) return true;
                _stamp++;
                if (_seen == null || _seen.Length != _ni * _nj) { _seen = new int[_ni * _nj]; _queue = new int[_ni * _nj]; }
                int head = 0, tail = 0;
                _seen[cells[0]] = _stamp; _queue[tail++] = cells[0];
                var want = new HashSet<int>(cells); want.Remove(cells[0]);
                while (head < tail)
                {
                    int c = _queue[head++], a = c / _nj, b = c % _nj;
                    for (int k = 0; k < 4; k++)
                    {
                        int na = a + (k == 0 ? 1 : k == 1 ? -1 : 0), nb = b + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (na < 0 || nb < 0 || na >= _ni || nb >= _nj) continue;
                        int n = na * _nj + nb;
                        if (_seen[n] == _stamp || !_inRoom[na, nb] || _p.State[_i0 + na, _j0 + nb] != FloorPlan.Free) continue;
                        _seen[n] = _stamp; _queue[tail++] = n;
                        want.Remove(n);
                    }
                }
                return want.Count == 0 && sides.All(c => c.Any(i => _seen[i] == _stamp));
            }
            int[] _seen, _queue; int _stamp;
            /// <summary>The clear cell of this room nearest (x, z), within 0.4 m, as an index into the room's box; -1 if none.</summary>
            int Nearest(float x, float z)
            {
                int i0 = _p.I(x) - _i0, j0 = _p.J(z) - _j0;
                for (int r = 0; r <= 4; r++)
                    for (int a = i0 - r; a <= i0 + r; a++)
                        for (int b = j0 - r; b <= j0 + r; b++)
                            if (a >= 0 && b >= 0 && a < _ni && b < _nj && _inRoom[a, b] && _p.State[_i0 + a, _j0 + b] == FloorPlan.Free) return a * _nj + b;
                return -1;
            }
            /// <summary>Must you be able to get to it? Not a chair, a lamp, a barrel or a crate -- nor a plain counter, worktop
            /// reached along its run (the one in an L's corner is a blind corner, as in any kitchen).</summary>
            static bool Access(string name) => !(name.StartsWith("Chair") || name.StartsWith("Lamp") || name.StartsWith("Barrel") || name.StartsWith("Crate")
                                                 || name.StartsWith("Cardboard") || name.StartsWith("Propane") || name.StartsWith("Pallet") || name.StartsWith("Coat")
                                                 || name == "Counter_0");
            /// <summary>Where you might stand to use a piece: half a metre out from its front, or from either side (a
            /// workbench or a tub along a wall is used from its long side, a bed got into from one).</summary>
            public static (float X, float Z)[] AccessPoints(FurniturePlan m)
            {
                var s = FurnitureCatalog.Sizes[m.Name]; var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                float mx = (s.X0 + s.X1) * 0.5f, my = (s.Y0 + s.Y1) * 0.5f;
                return new[]
                {
                    (m.X + rx * mx + fx * (s.Y1 + 0.5f), m.Z + rz * mx + fz * (s.Y1 + 0.5f)),
                    (m.X + rx * (s.X1 + 0.5f) + fx * my, m.Z + rz * (s.X1 + 0.5f) + fz * my),
                    (m.X + rx * (s.X0 - 0.5f) + fx * my, m.Z + rz * (s.X0 - 0.5f) + fz * my),
                };
            }

            /// <summary>The strip in front of a piece its doors swing through, as a box; nothing when nothing opens.</summary>
            static (float X0, float Z0, float X1, float Z1)? SwingZone(string name, float x, float z, float yaw)
            {
                float d = Swing(name);
                if (d <= 0f) return null;
                var s = FurnitureCatalog.Sizes[name];
                var (c, sn) = PlanTrig.CosSin(yaw);
                // right (c, -sn), front (-sn, -c): the box from the front face out d, across the width
                float ax = x + c * s.X0 - sn * (s.Y1 + 0.02f), az = z - sn * s.X0 - c * (s.Y1 + 0.02f);
                float bx = x + c * s.X1 - sn * (s.Y1 + d), bz = z - sn * s.X1 - c * (s.Y1 + d);
                return (MathF.Min(ax, bx), MathF.Min(az, bz), MathF.Max(ax, bx), MathF.Max(az, bz));
            }
            /// <summary>Clear for a door to swing through: this room's floor, nothing solid in it (doorways are fine).</summary>
            bool SwingClear((float X0, float Z0, float X1, float Z1) a)
            {
                foreach (var (i, j) in _p.Cells(a)) if (!InRoom(i, j) || _p.State[i, j] != FloorPlan.Free) return false;
                return true;
            }
            readonly Dictionary<FurniturePlan, List<(int, int)>> _reserved = new();

            /// <summary>Candidates best first, each score nudged by a seeded draw taken here, one per candidate in order (see
            /// Rng.Keyed: never inside a LINQ key selector).</summary>
            List<(float X, float Z, float Yaw)> Rank(IEnumerable<(float X, float Z, float Yaw)> cands, Func<float, float, float, double> score)
            {
                var l = new List<((float X, float Z, float Yaw) C, double S)>();
                foreach (var c in cands) l.Add((c, (score?.Invoke(c.X, c.Z, c.Yaw) ?? 0) + _rng.Int(1000) * 1e-4));
                return l.OrderByDescending(x => x.S).Select(x => x.C).ToList();
            }

            /// <summary>Put a piece at the first candidate (best score first) where it is clear, its doors can open, and
            /// it leaves the room walkable; null when none will do.</summary>
            FurniturePlan Put(string name, IEnumerable<(float X, float Z, float Yaw)> cands, Func<float, float, float, double> score = null, float y = 0f)
            {
                var s = FurnitureCatalog.Sizes[name];
                var ranked = Rank(cands, score);
                if (FurnishTrace != null)
                    FurnishTrace(System.FormattableString.Invariant($"put {name} in {_room}: {ranked.Count} candidates, first {(ranked.Count > 0 ? ranked[0].ToString() : "-")}"));
                foreach (var (x, z, yaw) in ranked)
                {
                    var fp = FurnitureCatalog.Footprint(name, x, z, yaw);
                    if (!Clear(name, x, z, yaw)) continue;
                    // the grid is 0.1 m: a sliver of overlap with a piece already in, or with the stairs, can fall between
                    // its cell centres
                    if (HitsPiece(name, x, z, yaw)) continue;
                    if (_p.StairRects.Any(r => Overlaps(fp, r))) continue;
                    var swing = SwingZone(name, x, z, yaw);
                    if (swing != null && (!SwingClear(swing.Value) || _mine.Any(o => o.On == null && !o.Mounted && Overlaps(swing.Value, o.Footprint)))) continue;
                    var cells = Under(name, x, z, yaw).Where(c => _p.State[c.I, c.J] == FloorPlan.Free).ToList();
                    foreach (var (i, j) in cells) _p.State[i, j] = FloorPlan.Furn;
                    var m = new FurniturePlan { Name = name, X = x, Y = _room.Floor * Storey + y, Z = z, Yaw = yaw, Floor = _room.Floor, Room = _room };
                    _mine.Add(m);
                    if (Connected())
                    {
                        _res.Furniture.Add(m);
                        // its swing stays clear: walkable, but nothing else stands in it
                        if (swing != null)
                        {
                            var held = new List<(int, int)>();
                            foreach (var (i, j) in _p.Cells(swing.Value)) if (!_p.KeepDoor[i, j]) { _p.KeepDoor[i, j] = true; held.Add((i, j)); }
                            _reserved[m] = held;
                            Recount(swing.Value);
                        }
                        Recount(fp);
                        return m;
                    }
                    _mine.Remove(m);
                    foreach (var (i, j) in cells) _p.State[i, j] = FloorPlan.Free;
                }
                return null;
            }

            /// <summary>Hang a piece on a wall (a flat television): where its back meets wall with no window or door
            /// in that stretch, and nothing hung there already. No floor of its own.</summary>
            FurniturePlan Hang(string name, IEnumerable<(float X, float Z, float Yaw)> cands, Func<float, float, float, double> score = null)
            {
                var ranked = Rank(cands, score);
                foreach (var (x, z, yaw) in ranked)
                {
                    var fp = FurnitureCatalog.Footprint(name, x, z, yaw);
                    // the wall behind it: no window's glass and no doorway along its span
                    var (fx, fz) = FrontOf(yaw);
                    var strip = (fp.X0 - (fx < 0 ? 0.6f : 0f), fp.Z0 - (fz < 0 ? 0.6f : 0f), fp.X1 + (fx > 0 ? 0.6f : 0f), fp.Z1 + (fz > 0 ? 0.6f : 0f));
                    if (_p.Cells(strip).Any(c => _p.KeepTall[c.I, c.J] || (_p.KeepDoor[c.I, c.J] && !_reserved.Values.Any(l => l.Contains(c))))) continue;
                    if (_mine.Any(o => o.Mounted && Overlaps(fp, o.Footprint))) continue;
                    var m = new FurniturePlan { Name = name, X = x, Y = _room.Floor * Storey + MountHeight, Z = z, Yaw = yaw, Floor = _room.Floor, Room = _room, Mounted = true };
                    _mine.Add(m); _res.Furniture.Add(m);
                    return m;
                }
                return null;
            }

            static bool Overlaps((float X0, float Z0, float X1, float Z1) a, (float X0, float Z0, float X1, float Z1) b)
                => a.X0 < b.X1 - 0.005f && a.X1 > b.X0 + 0.005f && a.Z0 < b.Z1 - 0.005f && a.Z1 > b.Z0 + 0.005f;

            /// <summary>Set a piece on another's top (a monitor on a desk, a till on a checkout): no floor of its own.</summary>
            void On(FurniturePlan host, string name, float along, float back, float yaw)
            {
                var (fx, fz) = host.Front; var (rx, rz) = host.Right;
                _res.Furniture.Add(new FurniturePlan
                {
                    Name = name, X = host.X + rx * along + fx * back, Z = host.Z + rz * along + fz * back, Y = host.Y + FurnitureCatalog.Sizes[host.Name].H,
                    Yaw = yaw, Floor = host.Floor, Room = _room, On = host,
                });
            }

            /// <summary>Where a piece stands with one side flush to a wall of this room: for each heading, the runs of
            /// wall face the side would meet, and every 0.25 m along them.</summary>
            IEnumerable<(float X, float Z, float Yaw)> Against(string name, Side side)
            {
                var s = FurnitureCatalog.Sizes[name];
                const float Gap = 0.03f;
                for (int k = 0; k < 4; k++)
                {
                    float yaw = k * 90f;
                    var (fx, fz) = FrontOf(yaw); var (rx, rz) = RightOf(yaw);
                    // the face's normal into the room, and how far the origin stands off it
                    var (nx, nz, off) = side switch
                    {
                        Side.Back => (fx, fz, Gap - s.Y0),
                        Side.Left => (rx, rz, Gap - s.X0),
                        _ => (-rx, -rz, Gap + s.X1),
                    };
                    // the piece's extent along the face: across its width (a back to the wall) or its depth (a side)
                    int alongSign = side == Side.Back ? (nx != 0 ? rz : rx) : (nx != 0 ? fz : fx);
                    float e0 = side == Side.Back ? s.X0 : s.Y0, e1 = side == Side.Back ? s.X1 : s.Y1;
                    float lowEnd = MathF.Min(e0 * alongSign, e1 * alongSign), highEnd = MathF.Max(e0 * alongSign, e1 * alongSign);
                    foreach (var (face, lo, hi) in Faces(nx, nz))
                    {
                        // tight into either end of the run (a corner), then every 0.25 m along it
                        var ts = new List<float> { lo + Gap - lowEnd, hi - Gap - highEnd };
                        for (float t = lo; t <= hi + 1e-3f; t += 0.25f) ts.Add(t);
                        foreach (float t in ts)
                        {
                            var c = nx != 0 ? (face + nx * off, t, yaw) : (t, face + nz * off, yaw);
                            // backed by wall along all but a sliver of that side, not hanging off a wall's end
                            if (Flush(FurnitureCatalog.Footprint(name, c.Item1, c.Item2, yaw), nx, nz) >= 0.9) yield return c;
                        }
                    }
                }
            }

            /// <summary>Wall faces looking into this room along (nx, nz): the room's clear cells with a wall cell right
            /// behind them, gathered into straight runs -- (the face's coordinate across, the run's ends along).</summary>
            List<(float Face, float Lo, float Hi)> Faces(int nx, int nz)
            {
                if (_faces.TryGetValue((nx, nz), out var cached)) return cached;
                var hits = new Dictionary<int, List<int>>();
                for (int i = _i0; i < _i0 + _ni; i++)
                    for (int j = _j0; j < _j0 + _nj; j++)
                    {
                        int bi = i - nx, bj = j - nz;
                        if (bi < 0 || bj < 0 || bi >= _p.Nx || bj >= _p.Nz) continue;
                        if (_p.State[bi, bj] != FloorPlan.Wall || _p.State[i, j] == FloorPlan.Wall) continue;
                        if (!InRoom(i, j)) continue;
                        int key = nx != 0 ? i : j, along = nx != 0 ? j : i;
                        if (!hits.TryGetValue(key, out var l)) hits[key] = l = new List<int>();
                        l.Add(along);
                    }
                var runs = new List<(float, float, float)>();
                foreach (var (key, l) in hits)
                {
                    l.Sort();
                    float face = nx != 0 ? _p.Ox + (nx > 0 ? key : key + 1) * FloorPlan.Step : _p.Oz + (nz > 0 ? key : key + 1) * FloorPlan.Step;
                    int a = 0;
                    for (int b = 1; b <= l.Count; b++)
                        if (b == l.Count || l[b] != l[b - 1] + 1)
                        {
                            float o = nx != 0 ? _p.Oz : _p.Ox;
                            runs.Add((face, o + l[a] * FloorPlan.Step, o + (l[b - 1] + 1) * FloorPlan.Step));
                            a = b;
                        }
                }
                _faces[(nx, nz)] = runs;
                return runs;
            }

            /// <summary>How much of one side of a footprint has wall right behind it, 0..1.</summary>
            double Flush((float X0, float Z0, float X1, float Z1) a, int nx, int nz)
            {
                // the strip 0.15 m beyond the side facing (-nx, -nz)
                (float, float, float, float) strip = nx > 0 ? (a.X0 - 0.15f, a.Z0, a.X0, a.Z1) : nx < 0 ? (a.X1, a.Z0, a.X1 + 0.15f, a.Z1)
                                                   : nz > 0 ? (a.X0, a.Z0 - 0.15f, a.X1, a.Z0) : (a.X0, a.Z1, a.X1, a.Z1 + 0.15f);
                int n = 0, wall = 0;
                foreach (var (i, j) in _p.Cells(strip)) { n++; if (_p.State[i, j] == FloorPlan.Wall) wall++; }
                return n == 0 ? 0 : wall / (double)n;
            }

            /// <summary>The doors of this room, as points just inside it.</summary>
            List<(float X, float Z)> Doors() => _targets;

            double NearestDoor(float x, float z) => Doors().Count == 0 ? 0 : Doors().Min(d => Math.Sqrt((d.X - x) * (d.X - x) + (d.Z - z) * (d.Z - z)));

            /// <summary>A piece of the given kinds along the walls, up to n of them, far from the doors first.</summary>
            void AlongWalls(string[] names, int n, Side side = Side.Back)
            {
                for (int k = 0; k < n; k++)
                {
                    var name = names[k % names.Length];
                    if (Put(name, Against(name, side), (x, z, yaw) => NearestDoor(x, z)) == null) break;
                }
            }

            // ---- the rooms ---------------------------------------------------------------------------------------

            /// <summary>The bed in the command position: its head to a solid wall with no door in it and no window over
            /// it, the door in view across the room but not straight past its foot (the "coffin" position), room to
            /// get in at a side; then a wardrobe, a lamp at the bedhead, a bookcase in a big room.</summary>
            void Bedroom()
            {
                float minSide = _room.Rects.Min(r => Math.Min(r.X1 - r.X0, r.Z1 - r.Z0)) * Cell;
                // a double where it fits, on a roll (strawberry: "bedrooms may be single or double bed")
                string bed = minSide >= 4.5f && _area >= 20f && _rng.Chance(60) ? "Bed_0" : "Bed_1";
                var b = Put(bed, Against(bed, Side.Back).Where(c => !UnderWindow(bed, c)), (x, z, yaw) => BedScore(bed, x, z, yaw));
                if (b == null && bed == "Bed_0") b = Put("Bed_1", Against("Bed_1", Side.Back).Where(c => !UnderWindow("Bed_1", c)), (x, z, yaw) => BedScore("Bed_1", x, z, yaw));
                Put("Wardrobe_0", Against("Wardrobe_0", Side.Back), (x, z, yaw) => NearestDoor(x, z) - (b != null && MathF.Abs(yaw - b.Yaw) < 1f ? 2 : 0));
                if (b != null)
                {
                    // a lamp at either side of the bedhead, against the same wall
                    var s = FurnitureCatalog.Sizes[b.Name]; var (rx, rz) = b.Right; var (fx, fz) = b.Front;
                    float back = s.Y0 + 0.37f + 0.03f;
                    var spots = new[] { s.X1 + 0.45f, s.X0 - 0.45f }.Select(a => (b.X + rx * a + fx * back, b.Z + rz * a + fz * back, b.Yaw));
                    Put("Lamp_1", spots);
                }
                if (_area >= 30f) Put("Shelf_2", Against("Shelf_2", Side.Back), (x, z, yaw) => NearestDoor(x, z));
            }

            /// <summary>Is the wall behind a piece's back a window there (a bed's head under one: the chi runs out of it)?</summary>
            bool UnderWindow(string name, (float X, float Z, float Yaw) c)
            {
                var fp = FurnitureCatalog.Footprint(name, c.X, c.Z, c.Yaw);
                var (fx, fz) = FrontOf(c.Yaw);
                var behind = fx != 0 ? (fx > 0 ? (fp.X0 - 0.8f, fp.Z0, fp.X0, fp.Z1) : (fp.X1, fp.Z0, fp.X1 + 0.8f, fp.Z1))
                                     : (fz > 0 ? (fp.X0, fp.Z0 - 0.8f, fp.X1, fp.Z0) : (fp.X0, fp.Z1, fp.X1, fp.Z1 + 0.8f));
                return _p.Cells(behind).Any(k => _p.KeepTall[k.I, k.J]);
            }

            double BedScore(string bed, float x, float z, float yaw)
            {
                var s = FurnitureCatalog.Sizes[bed];
                var (fx, fz) = FrontOf(yaw); var (rx, rz) = RightOf(yaw);
                double score = 0;
                foreach (var (dx0, dz0) in Doors())
                {
                    float vx = dx0 - x, vz = dz0 - z;
                    float ahead = vx * fx + vz * fz, across = vx * rx + vz * rz, dist = MathF.Sqrt(vx * vx + vz * vz);
                    if (ahead > 0 && MathF.Abs(across) < s.X1 + 0.4f) score -= 100;            // coffin: the door past its foot
                    else if (ahead > 0) score += 20;                                            // the door in view
                    if (ahead < s.Y0 + 0.5f && MathF.Abs(across) < s.X1 + 1.5f) score -= 50;    // a door beside its head
                    score += Math.Min(dist, 8f);
                }
                // its head on a wall that is not a window
                var fp = FurnitureCatalog.Footprint(bed, x, z, yaw);
                var head = fx != 0 ? (fx > 0 ? (fp.X0 - 0.8f, fp.Z0, fp.X0, fp.Z1) : (fp.X1, fp.Z0, fp.X1 + 0.8f, fp.Z1))
                                   : (fz > 0 ? (fp.X0, fp.Z0 - 0.8f, fp.X1, fp.Z0) : (fp.X0, fp.Z1, fp.X1, fp.Z1 + 0.8f));
                if (_p.Cells(head).Any(c => _p.KeepTall[c.I, c.J])) score -= 40;
                // a side to get in at
                foreach (int sgn in new[] { -1, 1 })
                {
                    var side = rx != 0 ? (sgn * rx > 0 ? (fp.X1, fp.Z0, fp.X1 + 0.7f, fp.Z1) : (fp.X0 - 0.7f, fp.Z0, fp.X0, fp.Z1))
                                       : (sgn * rz > 0 ? (fp.X0, fp.Z1, fp.X1, fp.Z1 + 0.7f) : (fp.X0, fp.Z0 - 0.7f, fp.X1, fp.Z0));
                    if (Clear(side, 0f)) score += 10;
                }
                return score;
            }

            /// <summary>A couch with its back to a solid wall, looking toward the way in, and a television it can watch
            /// (strawberry: "living room should have a tv (either wall mounted or a crt on top of a table), couch should have
            /// a sight line within a max distance of a tv set, different for both types"): a flat one hung on the wall it
            /// faces, up to <see cref="WallTvReach"/> away, or a CRT on a table up to <see cref="CrtReach"/> -- facing it,
            /// nothing tall between. The couch moves until one fits. Then a lamp in a corner, a bookcase.</summary>
            void LivingRoom()
            {
                string couch = _area >= 18f ? "Couch_0" : "Couch_1";
                bool crtFirst = _rng.Chance(50);
                var ranked = Rank(Against(couch, Side.Back), CouchScore);
                // the seeded kind of set wherever the couch can go first, the other kind after
                FurniturePlan seat = null;
                foreach (bool crt in new[] { crtFirst, !crtFirst })
                {
                    foreach (var cand in ranked)
                    {
                        var c = Put(couch, new[] { cand });
                        if (c == null) continue;
                        if (crt ? Crt(c) : WallTv(c)) { seat = c; break; }
                        Remove(c);
                    }
                    if (seat != null) break;
                }
                if (seat == null) Put(couch, ranked);   // no set will go anywhere: the couch at least
                else
                {
                    // keep the view: nothing tall goes down between the couch and its set afterwards
                    var set = _res.Furniture.Last(m => m.Name.StartsWith("Television"));
                    var (fx, fz) = seat.Front;
                    float x0 = seat.X + fx * 0.3f, z0 = seat.Z + fz * 0.3f, d = MathF.Sqrt((set.X - x0) * (set.X - x0) + (set.Z - z0) * (set.Z - z0));
                    for (float t = 0f; t <= d; t += 0.1f)
                    {
                        float x = x0 + (set.X - x0) * t / d, z = z0 + (set.Z - z0) * t / d;
                        foreach (var (i, j) in _p.Cells((x - 0.3f, z - 0.3f, x + 0.3f, z + 0.3f))) _p.KeepTall[i, j] = true;
                    }
                    Recount((MathF.Min(x0, set.X) - 0.4f, MathF.Min(z0, set.Z) - 0.4f, MathF.Max(x0, set.X) + 0.4f, MathF.Max(z0, set.Z) + 0.4f));
                }
                Put("Lamp_1", Against("Lamp_1", Side.Back).Where(p => Corner("Lamp_1", p)), (x, z, yaw) => NearestDoor(x, z));
                if (_area >= 24f) Put("Shelf_2", Against("Shelf_2", Side.Back), (x, z, yaw) => NearestDoor(x, z));
            }

            /// <summary>Places on the wall the couch faces, facing it, `near`..`far` in front of it and nearly square to it,
            /// with nothing tall in the way.</summary>
            IEnumerable<(float X, float Z, float Yaw)> Facing(FurniturePlan c, string name, float far, FurniturePlan except = null)
            {
                var (fx, fz) = c.Front; var (rx, rz) = c.Right;
                float yaw = (c.Yaw + 180f) % 360f;
                return Against(name, Side.Back).Where(p => MathF.Abs(p.Yaw - yaw) < 1f).Where(p =>
                {
                    float ahead = (p.X - c.X) * fx + (p.Z - c.Z) * fz, side = (p.X - c.X) * rx + (p.Z - c.Z) * rz;
                    // in view: no more than a third of the way off to one side
                    return ahead >= TvNear && ahead <= far && MathF.Abs(side) <= MathF.Max(1f, ahead * 0.33f) && Sight(c.X + fx * 0.3f, c.Z + fz * 0.3f, p.X, p.Z, c, except);
                });
            }
            bool WallTv(FurniturePlan c)
            {
                var (rx, rz) = c.Right;
                return Hang("Television_0", Facing(c, "Television_0", WallTvReach), (x, z, _) => -MathF.Abs((x - c.X) * rx + (z - c.Z) * rz)) != null;
            }
            bool Crt(FurniturePlan c)
            {
                var (rx, rz) = c.Right; var (fx, fz) = c.Front;
                // on a table against the wall it faces, or standing out in the room in front of it
                float yaw = (c.Yaw + 180f) % 360f;
                var stands = Facing(c, "Table_Wood_2", CrtReach).Concat(
                    Enumerable.Range(0, 9).Select(k => TvNear + 0.5f + k * 0.25f).Where(d => d <= CrtReach)
                              .Select(d => (c.X + fx * d, c.Z + fz * d, yaw)));
                var table = Put("Table_Wood_2", stands, (x, z, _) => -MathF.Abs((x - c.X) * rx + (z - c.Z) * rz) - 0.1f * MathF.Abs((x - c.X) * fx + (z - c.Z) * fz));
                if (table == null) return false;
                On(table, "Television_1", 0f, 0f, table.Yaw);   // its screen to the couch, as the table faces it
                return true;
            }
            /// <summary>Nothing between two points but floor and what is low enough to see over (a table, a chair).</summary>
            bool Sight(float x0, float z0, float x1, float z1, params FurniturePlan[] except)
            {
                float d = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                for (float t = 0.5f; t < d - 0.5f; t += 0.2f)
                {
                    float x = x0 + (x1 - x0) * t / d, z = z0 + (z1 - z0) * t / d;
                    if (_p.State[_p.I(x), _p.J(z)] == FloorPlan.Wall) return false;
                    foreach (var o in _mine)
                    {
                        if (o.On != null || o.Mounted || except.Contains(o) || FurnitureCatalog.Sizes[o.Name].H <= 1.3f) continue;
                        var f = o.Footprint;
                        if (x > f.X0 && x < f.X1 && z > f.Z0 && z < f.Z1) return false;
                    }
                }
                return true;
            }

            double CouchScore(float x, float z, float yaw)
            {
                var (fx, fz) = FrontOf(yaw);
                double score = 0;
                foreach (var (dx0, dz0) in Doors())
                {
                    float vx = dx0 - x, vz = dz0 - z, d = MathF.Sqrt(vx * vx + vz * vz) + 1e-3f;
                    float facing = (vx * fx + vz * fz) / d;
                    score += facing > -0.2f ? 10 * facing : -30;                       // the way in in view, never behind it
                }
                float reach = Reach(x, z, fx, fz);
                score += reach >= 2.5f && reach <= 9f ? 20 : -10;                     // a television's distance across (as it is placed)
                return score;
            }

            /// <summary>How far clear floor runs from (x, z) along (fx, fz) before a wall.</summary>
            float Reach(float x, float z, float fx, float fz)
            {
                for (float d = 0.2f; d < 30f; d += FloorPlan.Step)
                {
                    int i = _p.I(x + fx * d), j = _p.J(z + fz * d);
                    if (_p.State[i, j] == FloorPlan.Wall) return d;
                }
                return 30f;
            }

            bool Corner(string name, (float X, float Z, float Yaw) p)
            {
                var fp = FurnitureCatalog.Footprint(name, p.X, p.Z, p.Yaw);
                return Flush(fp, 1, 0) > 0.6 || Flush(fp, -1, 0) > 0.6 ? Flush(fp, 0, 1) > 0.6 || Flush(fp, 0, -1) > 0.6 : false;
            }

            /// <summary>The table in the middle of the room's clear floor with a metre round it, and dining chairs
            /// (strawberry: "use chair 1 instead of chair 2 for the dining room") facing it: at a long table along its
            /// sides and one at each end, at the round one evenly round its rim (strawberry: "consider the shape of
            /// furniture when placing stuff around it (the kitchen table)").</summary>
            void Dining()
            {
                var (cx, cz) = Centre();
                bool big = _room.Rects.Min(r => Math.Min(r.X1 - r.X0, r.Z1 - r.Z0)) * Cell >= 6.5f;
                string table = big ? "Table_Wood_1" : "Table_Wood_0";
                var t = PutWithRoom(table, 1.0f, cx, cz);
                if (t == null && big) t = PutWithRoom(table = "Table_Wood_0", 1.0f, cx, cz);
                if (t == null) return;
                foreach (var seat in Seats(t)) Put(DiningChair, new[] { seat });
            }

            const string DiningChair = "Chair_Wood_0";

            /// <summary>Where the chairs go round a table, each facing it.</summary>
            List<(float X, float Z, float Yaw)> Seats(FurniturePlan t)
            {
                var ts = FurnitureCatalog.Sizes[t.Name]; var cs = FurnitureCatalog.Sizes[DiningChair];
                var seats = new List<(float, float, float)>();
                if (t.Name == "Table_Wood_1")
                {
                    // round: six round it if they all fit, else four; turned whichever way more of them are clear
                    float r = ts.X1 + 0.05f + cs.Y1;
                    List<(float, float, float)> Ring(int n, float phase) => Enumerable.Range(0, n).Select(k =>
                    {
                        var (dz, dx) = PlanTrig.CosSin(t.Yaw + phase + k * 360f / n);
                        return (t.X + dx * r, t.Z + dz * r, YawFacing(-dx, -dz));
                    }).ToList();
                    int Fits(List<(float X, float Z, float Yaw)> ring) => ring.Count(c => Clear(DiningChair, c.X, c.Z, c.Yaw) && !HitsPiece(DiningChair, c.X, c.Z, c.Yaw));
                    var six = new[] { 0f, 30f }.Select(ph => Ring(6, ph)).FirstOrDefault(ring => Fits(ring) == 6);
                    return six ?? new[] { 0f, 45f }.Select(ph => Ring(4, ph)).OrderByDescending(Fits).First();
                }
                var (fx, fz) = t.Front; var (rx, rz) = t.Right;
                // along each long side, a chair every metre and a bit; at each end one
                float half = (ts.Y1 - ts.Y0) * 0.5f;
                int per = Math.Max(1, (int)((ts.Y1 - ts.Y0) / 1.1f));
                foreach (int sgn in new[] { 1, -1 })
                {
                    float o = sgn > 0 ? ts.X1 + 0.05f + cs.Y1 : ts.X0 - 0.05f - cs.Y1;
                    float yaw = YawFacing(-sgn * rx, -sgn * rz);
                    for (int k = 0; k < per; k++)
                    {
                        float a = -half + (k + 0.5f) * (ts.Y1 - ts.Y0) / per;
                        seats.Add((t.X + rx * o + fx * a, t.Z + rz * o + fz * a, yaw));
                    }
                    float e = sgn > 0 ? ts.Y1 + 0.05f + cs.Y1 : ts.Y0 - 0.05f - cs.Y1;
                    seats.Add((t.X + fx * e, t.Z + fz * e, YawFacing(-sgn * fx, -sgn * fz)));
                }
                return seats;
            }

            // (to a thousandth of a degree: a platform's atan2f may differ in the last place, and the yaw is fed back in)
            static float YawFacing(float fx, float fz) => MathF.Round((MathF.Atan2(-fx, -fz) * 180f / MathF.PI + 360f) % 360f * 1000f) / 1000f % 360f;

            /// <summary>Put a free-standing piece as near (cx, cz) as it goes with `room` clear all round it.</summary>
            FurniturePlan PutWithRoom(string name, float room, float cx, float cz)
            {
                var c = new List<(float, float, float)>();
                var bb = (X0: _room.Rects.Min(r => r.X0) * Cell, Z0: _room.Rects.Min(r => r.Z0) * Cell, X1: _room.Rects.Max(r => r.X1) * Cell, Z1: _room.Rects.Max(r => r.Z1) * Cell);
                bool longX = bb.X1 - bb.X0 >= bb.Z1 - bb.Z0;
                for (float x = bb.X0; x <= bb.X1; x += 0.25f)
                    for (float z = bb.Z0; z <= bb.Z1; z += 0.25f)
                        foreach (float yaw in longX ? new[] { 90f, 0f } : new[] { 0f, 90f })
                        {
                            var fp = FurnitureCatalog.Footprint(name, x, z, yaw);
                            var around = (fp.X0 - room, fp.Z0 - room, fp.X1 + room, fp.Z1 + room);
                            if (Clear(around, 0f)) c.Add((x, z, yaw));
                        }
                return Put(name, c, (x, z, yaw) => -Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)) + (yaw == (longX ? 90f : 0f) ? 0.5 : 0));
            }

            (float, float) Centre()
            {
                double sx = 0, sz = 0; int n = 0;
                foreach (var r in _room.Rects)
                    for (int i = _p.I(r.X0 * Cell); i <= _p.I(r.X1 * Cell); i++)
                        for (int j = _p.J(r.Z0 * Cell); j <= _p.J(r.Z1 * Cell); j++)
                            if (_p.State[i, j] == FloorPlan.Free && !_p.KeepDoor[i, j] && InRoom(i, j)) { sx += _p.Cx(i); sz += _p.Cz(j); n++; }
                return n == 0 ? ((_room.X0 + _room.X1) * 0.5f * Cell, (_room.Z0 + _room.Z1) * 0.5f * Cell) : ((float)(sx / n), (float)(sz / n));
            }

            /// <summary>The kitchen (strawberry: "the kitchen layouts can be more dynamic, with islands, or counters that span
            /// multiple walls. fridge can be freestanding from the counter row if it decides to"): an L round a corner -- the
            /// sink on one leg, the stove on the other -- or one run of fridge, sink, dishwasher and stove; now and then the
            /// fridge on a wall of its own; in a big kitchen an island. Always the sink and the stove apart (water and fire),
            /// the fridge off the stove's side, and the three a few steps from each other.</summary>
            void Kitchen()
            {
                var runs = new List<(int Nx, int Nz, float Face, float Lo, float Hi)>();
                foreach (var (nx, nz) in Fronts)
                    foreach (var (face, lo, hi) in Faces(nx, nz)) runs.Add((nx, nz, face, lo, hi));
                runs = _rng.Keyed(runs).OrderByDescending(x => x.Item.Hi - x.Item.Lo).ThenBy(x => x.Key).Select(x => x.Item).ToList();
                bool freeFridge = _rng.Chance(30), tryL = _rng.Chance(55);
                // the seeded layouts first, then the plainer ones; each leaves the kitchen complete or as it found it
                var tries = new List<Func<bool>>();
                if (tryL) tries.Add(() => Done(L(runs, freeFridge)) || Done(L(runs, !freeFridge)));
                if (freeFridge) tries.Add(() => RunWithOwnFridge(runs));
                tries.Add(() => Done(Row(runs, new[] { "Fridge_0", "Counter_1", "Disher_0", "Oven_0" })));
                if (!freeFridge) tries.Add(() => RunWithOwnFridge(runs));
                if (!tryL) tries.Add(() => Done(L(runs, false)) || Done(L(runs, true)));
                tries.Add(() => Pair(runs));
                foreach (var t in tries) if (t()) break;
                if (!_mine.Any(m => m.Name == "Oven_0")) Put("Oven_0", Against("Oven_0", Side.Back));
                // a kitchen-diner's table stands where an island would
                if (_res.KitchenDiner) Dining();
                else if (_area >= 30f && _rng.Chance(60)) Island();
            }

            /// <summary>Did a layout put in all three of fridge, sink and stove? If not, take out whatever it did.</summary>
            bool Done(List<FurniturePlan> work)
            {
                if (work == null) return false;
                if (new[] { "Fridge_0", "Counter_1", "Oven_0" }.All(n => _mine.Any(m => m.Name == n))) return true;
                foreach (var m in work) Remove(m);
                return false;
            }

            /// <summary>Sink, dishwasher and stove in a run; the fridge on a wall of its own near the sink, off the
            /// stove's side, keeping the triangle.</summary>
            bool RunWithOwnFridge(List<(int Nx, int Nz, float Face, float Lo, float Hi)> runs)
            {
                var work = Row(runs, new[] { "Counter_1", "Disher_0", "Oven_0" });
                if (work == null) return false;
                var sink = work.FirstOrDefault(m => m.Name == "Counter_1"); var oven = work.FirstOrDefault(m => m.Name == "Oven_0");
                var fr = Put("Fridge_0", Against("Fridge_0", Side.Back).Where(c => Triangle(oven, sink, ("Fridge_0", c.X, c.Z, c.Yaw))),
                             (x, z, _) => -MathF.Abs(D(x, z, sink) - 2.5f));
                if (fr != null) return true;
                foreach (var m in work) Remove(m);
                return false;
            }

            /// <summary>The fridge and the sink side by side, the stove on a wall nearby, the dishwasher by the sink.</summary>
            bool Pair(List<(int Nx, int Nz, float Face, float Lo, float Hi)> runs)
            {
                var pair = Row(runs, new[] { "Fridge_0", "Counter_1" });
                if (pair == null) return false;
                var sink = pair.First(m => m.Name == "Counter_1"); var fr = pair.First(m => m.Name == "Fridge_0");
                var oven = Put("Oven_0", Against("Oven_0", Side.Back).Where(c => Triangle(fr, sink, ("Oven_0", c.X, c.Z, c.Yaw))), (x, z, _) => -MathF.Abs(D(x, z, sink) - 2.5f));
                if (oven == null) { foreach (var m in pair) Remove(m); return false; }
                Put("Disher_0", Against("Disher_0", Side.Back).Where(c => D(c.X, c.Z, sink) < 1.6f));
                return true;
            }

            static float D(float x, float z, FurniturePlan o) => MathF.Sqrt((x - o.X) * (x - o.X) + (z - o.Z) * (z - o.Z));
            /// <summary>Would the third of fridge, sink and stove (a name and a place) keep the working triangle -- legs of
            /// at least 1.3 m, all three under 9.5 m round -- and keep off the side of what it must not touch?</summary>
            static bool Triangle(FurniturePlan a, FurniturePlan b, (string Name, float X, float Z, float Yaw) c)
            {
                var fp = FurnitureCatalog.Footprint(c.Name, c.X, c.Z, c.Yaw);
                var near = (fp.X0 - 0.5f, fp.Z0 - 0.5f, fp.X1 + 0.5f, fp.Z1 + 0.5f);
                foreach (var o in new[] { a, b })
                {
                    if (o == null) continue;
                    bool clash = (c.Name == "Oven_0" && (o.Name == "Counter_1" || o.Name == "Fridge_0")) || (c.Name == "Fridge_0" && o.Name == "Oven_0");
                    if (clash && Overlaps(near, o.Footprint)) return false;
                    if (D(c.X, c.Z, o) < 1.3f) return false;
                }
                if (a != null && b != null && D(c.X, c.Z, a) + D(c.X, c.Z, b) + D(a.X, a.Z, b) > TriangleRound) return false;
                if (a == null && b != null && D(c.X, c.Z, b) > 5.5f) return false;
                return true;
            }

            /// <summary>An L round an inside corner of the room: a counter in the corner, the sink and the dishwasher on
            /// that leg (the fridge closing it, unless it stands free); the other leg starting past the first's depth with a
            /// counter and the stove.</summary>
            List<FurniturePlan> L(List<(int Nx, int Nz, float Face, float Lo, float Hi)> runs, bool freeFridge)
            {
                const float Near = 0.15f;
                foreach (var a in runs)
                    foreach (var b in runs)
                    {
                        if ((a.Nx != 0) == (b.Nx != 0)) continue;   // perpendicular
                        // b's face stands at one end of a, and a's face at one end of b: an inside corner
                        int endA = MathF.Abs(b.Face - a.Lo) < Near ? -1 : MathF.Abs(b.Face - a.Hi) < Near ? 1 : 0;
                        int endB = MathF.Abs(a.Face - b.Lo) < Near ? -1 : MathF.Abs(a.Face - b.Hi) < Near ? 1 : 0;
                        if (endA == 0 || endB == 0) continue;
                        var legA = new List<string> { "Counter_0", "Counter_1", "Disher_0" };
                        if (!freeFridge) legA.Add("Fridge_0");
                        var placed = Leg(a, endA < 0 ? a.Lo : a.Hi, -endA, legA, 3);
                        if (placed == null) continue;
                        var cs = FurnitureCatalog.Sizes["Counter_0"];
                        float startB = (endB < 0 ? b.Lo : b.Hi) - endB * (cs.Y1 - cs.Y0 + 0.05f);
                        var legB = Leg(b, startB, -endB, new List<string> { "Counter_0", "Oven_0", "Counter_0" }, 2);
                        var sink = placed.FirstOrDefault(m => m.Name == "Counter_1"); var oven = legB?.FirstOrDefault(m => m.Name == "Oven_0");
                        var fridge = placed.FirstOrDefault(m => m.Name == "Fridge_0");
                        if (legB != null && sink != null && oven != null && fridge == null && freeFridge)
                        {
                            // the fridge on a wall of its own, keeping the triangle
                            fridge = Put("Fridge_0", Against("Fridge_0", Side.Back).Where(c => Triangle(oven, sink, ("Fridge_0", c.X, c.Z, c.Yaw))), (x, z, _) => -MathF.Abs(D(x, z, sink) - 2.5f));
                            if (fridge != null) legB.Add(fridge);
                        }
                        if (legB != null && sink != null && oven != null && fridge != null && Triangle(fridge, sink, ("Oven_0", oven.X, oven.Z, oven.Yaw)))
                            return placed.Concat(legB).ToList();
                        foreach (var m in (legB ?? new List<FurniturePlan>()).Concat(placed)) Remove(m);
                    }
                return null;
            }

            /// <summary>Pieces side by side along a wall run from `start`, laid in direction `dir` (+1/-1 along the run's
            /// axis); null (and nothing left standing) when fewer than `need` of them go in.</summary>
            List<FurniturePlan> Leg((int Nx, int Nz, float Face, float Lo, float Hi) run, float start, int dir, List<string> names, int need)
            {
                float yaw = YawFacing(run.Nx, run.Nz);
                var (rx, rz) = RightOf(yaw);
                int rsign = run.Nx != 0 ? rz : rx, n = run.Nx != 0 ? run.Nx : run.Nz;
                var placed = new List<FurniturePlan>();
                float c = start + dir * 0.03f;
                foreach (var name in names)
                {
                    var sz = FurnitureCatalog.Sizes[name];
                    bool fwd = dir * rsign > 0;
                    float o = fwd ? c - rsign * sz.X0 : c - rsign * sz.X1;
                    float far = fwd ? o + rsign * sz.X1 : o + rsign * sz.X0;
                    float across = run.Face + n * (0.03f - sz.Y0);
                    var m = Put(name, new[] { run.Nx != 0 ? (across, o, yaw) : (o, across, yaw) });
                    if (m == null) break;
                    placed.Add(m);
                    c = far + dir * 0.04f;
                }
                if (placed.Count >= need) return placed;
                foreach (var m in placed) Remove(m);
                return null;
            }

            /// <summary>Two counters back to back in the middle of the kitchen with 1.1 m clear all round.</summary>
            void Island()
            {
                var cs = FurnitureCatalog.Sizes["Counter_0"];
                var (cx, cz) = Centre();
                var cands = new List<(float, float, float)>();
                var bb = (X0: _room.Rects.Min(r => r.X0) * Cell, Z0: _room.Rects.Min(r => r.Z0) * Cell, X1: _room.Rects.Max(r => r.X1) * Cell, Z1: _room.Rects.Max(r => r.Z1) * Cell);
                for (float x = bb.X0; x <= bb.X1; x += 0.25f)
                    for (float z = bb.Z0; z <= bb.Z1; z += 0.25f)
                        foreach (float yaw in new[] { 0f, 90f })
                        {
                            var (fx, fz) = FrontOf(yaw);
                            var a = FurnitureCatalog.Footprint("Counter_0", x, z, yaw);
                            var b = FurnitureCatalog.Footprint("Counter_0", x - fx * (cs.Y0 * -2f), z - fz * (cs.Y0 * -2f), yaw + 180f);
                            var both = (MathF.Min(a.X0, b.X0) - 1.1f, MathF.Min(a.Z0, b.Z0) - 1.1f, MathF.Max(a.X1, b.X1) + 1.1f, MathF.Max(a.Z1, b.Z1) + 1.1f);
                            if (Clear(both, 0f)) cands.Add((x, z, yaw));
                        }
                foreach (var (x, z, yaw) in cands.OrderBy(c => (c.Item1 - cx) * (c.Item1 - cx) + (c.Item2 - cz) * (c.Item2 - cz)).Take(20))
                {
                    var (fx, fz) = FrontOf(yaw);
                    var one = Put("Counter_0", new[] { (x, z, yaw) });
                    if (one == null) continue;
                    var two = Put("Counter_0", new[] { (x - fx * (cs.Y0 * -2f), z - fz * (cs.Y0 * -2f), (yaw + 180f) % 360f) });
                    if (two != null) return;
                    Remove(one);
                }
            }

            /// <summary>Lay `names` side by side along one wall run, either way round, at the first place along the longest
            /// run that takes them all (and then counters at both ends while there is wall); null when none does.</summary>
            List<FurniturePlan> Row(List<(int Nx, int Nz, float Face, float Lo, float Hi)> runs, string[] names)
            {
                float width = names.Sum(n => FurnitureCatalog.Sizes[n].X1 - FurnitureCatalog.Sizes[n].X0) + 0.04f * names.Length;
                foreach (var run in runs.Where(r => r.Hi - r.Lo >= width))
                {
                    float yaw = YawFacing(run.Nx, run.Nz);
                    var (rx, rz) = RightOf(yaw);
                    int dir = run.Nx != 0 ? rz : rx;
                    int nAcross = run.Nx != 0 ? run.Nx : run.Nz;
                    (float, float, float) At(float along, string n)
                    {
                        float across = run.Face + nAcross * (0.03f - FurnitureCatalog.Sizes[n].Y0);
                        return run.Nx != 0 ? (across, along, yaw) : (along, across, yaw);
                    }
                    // the run's near end in the direction the pieces are laid, and every 0.25 m from it
                    float near = dir > 0 ? run.Lo : run.Hi, far = dir > 0 ? run.Hi : run.Lo;
                    foreach (var order in _rng.Keyed(new[] { names, names.Reverse().ToArray() }).OrderBy(x => x.Key).Select(x => x.Item).ToList())
                        for (float c0 = near; (far - c0) * dir >= width - 1e-3f; c0 += dir * 0.25f)
                        {
                            // all of it clear first, then each piece in
                            var spots = new List<(string N, (float, float, float) P)>();
                            float c = c0;
                            foreach (var n in order)
                            {
                                var sz = FurnitureCatalog.Sizes[n];
                                float along = c - dir * sz.X0 + dir * 0.02f;
                                spots.Add((n, At(along, n)));
                                c = along + dir * (sz.X1 + 0.02f);
                            }
                            if (!spots.All(sp => Clear(FurnitureCatalog.Footprint(sp.N, sp.P.Item1, sp.P.Item2, sp.P.Item3), FurnitureCatalog.Sizes[sp.N].H))) continue;
                            var placed = new List<FurniturePlan>();
                            foreach (var sp in spots)
                            {
                                var m = Put(sp.N, new[] { sp.P });
                                if (m == null) break;
                                placed.Add(m);
                            }
                            if (placed.Count < spots.Count) { foreach (var m in placed) Remove(m); continue; }
                            // counters on along both ends while the wall lasts -- returned with the row, so a caller that
                            // gives up on it takes them out too (left standing, they ate the wall every later layout needed)
                            var ends = new List<FurniturePlan>();
                            foreach (int end in new[] { 1, -1 })
                            {
                                var edge = end > 0 ? placed[^1] : placed[0];
                                var es = FurnitureCatalog.Sizes[edge.Name];
                                float cur = (run.Nx != 0 ? edge.Z : edge.X) + end * dir * ((end > 0 ? es.X1 : -es.X0) + 0.02f);
                                for (int k = 0; k < 2; k++)
                                {
                                    var cs = FurnitureCatalog.Sizes["Counter_0"];
                                    float along = cur + end * dir * ((end > 0 ? -cs.X0 : cs.X1) + 0.02f);
                                    var m = Put("Counter_0", new[] { At(along, "Counter_0") });
                                    if (m == null) break;
                                    ends.Add(m);
                                    cur = along + end * dir * ((end > 0 ? cs.X1 : -cs.X0) + 0.02f);
                                }
                            }
                            return placed.Concat(ends).ToList();
                        }
                }
                return null;
            }

            /// <summary>A house's: a tub along a wall, in a corner where it goes; a sink at a wall; in a big one, a washer.</summary>
            void Bathroom()
            {
                // a house's has a tub; a shop's or a factory's washroom just the basin
                if (_res.Kind == BuildingKind.Residential)
                    Put("Tub_0", Against("Tub_0", Side.Left).Concat(Against("Tub_0", Side.Right)), (x, z, yaw) => (Corner("Tub_0", (x, z, yaw)) ? 10 : 0) + NearestDoor(x, z));
                Put("Counter_1", Against("Counter_1", Side.Back), (x, z, yaw) => -NearestDoor(x, z));
                if (_area >= 14f && _res.Kind == BuildingKind.Residential) Put("Washer_0", Against("Washer_0", Side.Back), (x, z, yaw) => NearestDoor(x, z));
            }

            /// <summary>The desk in the command position: the chair's back to a wall, facing the door across the desk;
            /// a monitor on it; a filing cabinet and a bookcase along the walls.</summary>
            void Office()
            {
                var cs = FurnitureCatalog.Sizes["Chair_Wood_1"]; var ds = FurnitureCatalog.Sizes["Table_Wood_2"];
                // where the chair can stand with its back to a wall, best facing the door
                foreach (var (x, z, yaw) in Rank(Against("Chair_Wood_1", Side.Back), DeskScore).Take(60))
                {
                    var (fx, fz) = FrontOf(yaw);
                    float d = cs.Y1 + 0.05f - ds.Y0;
                    var desk = Put("Table_Wood_2", new[] { (x + fx * d, z + fz * d, yaw) });
                    if (desk == null) continue;
                    var chair = Put("Chair_Wood_1", new[] { (x, z, yaw) });
                    if (chair == null) { Remove(desk); continue; }
                    On(desk, "Computer_3", 0f, 0.35f, (yaw + 180f) % 360f);   // its screen to the chair
                    break;
                }
                Put("Files_0", Against("Files_0", Side.Back), (x, z, yaw) => NearestDoor(x, z));
                if (_area >= 18f) Put("Shelf_2", Against("Shelf_2", Side.Back), (x, z, yaw) => NearestDoor(x, z));
            }

            double DeskScore(float x, float z, float yaw)
            {
                var (fx, fz) = FrontOf(yaw);
                double score = 0;
                foreach (var (dx0, dz0) in Doors())
                {
                    float vx = dx0 - x, vz = dz0 - z, d = MathF.Sqrt(vx * vx + vz * vz) + 1e-3f;
                    score += 30 * (vx * fx + vz * fz) / d;                            // facing the door
                }
                return score;
            }

            void Remove(FurniturePlan m)
            {
                if (!m.Mounted && m.On == null)
                    foreach (var (i, j) in Under(m.Name, m.X, m.Z, m.Yaw)) if (_p.State[i, j] == FloorPlan.Furn) _p.State[i, j] = FloorPlan.Free;
                if (_reserved.TryGetValue(m, out var held))
                {
                    foreach (var (i, j) in held) _p.KeepDoor[i, j] = false;
                    _reserved.Remove(m);
                    if (SwingZone(m.Name, m.X, m.Z, m.Yaw) is { } sz) Recount(sz);
                }
                foreach (var top in _res.Furniture.Where(t => t.On == m).ToList()) _res.Furniture.Remove(top);
                _mine.Remove(m); _res.Furniture.Remove(m);
                Recount(m.Footprint);
            }

            void Storage()
            {
                AlongWalls(new[] { "Shelf_3" }, 2, Side.Left);
                foreach (var n in new[] { "Crate_0", "Cardboard_0", "Barrel_0" })
                    Put(n, Against(n, Side.Back).Where(p => Corner(n, p)), (x, z, yaw) => NearestDoor(x, z));
            }

            /// <summary>A rack and a laundry along the side walls, barrels and a gas bottle in the corners -- and the
            /// lane in from the vehicle door kept clear (its doorway runs 6 m in).</summary>
            void Garage()
            {
                AlongWalls(new[] { "Shelf_0" }, 1, Side.Left);
                if (_res.Kind == BuildingKind.Residential) { Put("Washer_0", Against("Washer_0", Side.Back)); Put("Dryer_0", Against("Dryer_0", Side.Back)); }
                foreach (var n in new[] { "Barrel_0", "Barrel_0", "Propane_0" })
                    Put(n, Against(n, Side.Back).Where(p => Corner(n, p)), (x, z, yaw) => NearestDoor(x, z));
            }

            /// <summary>Racks along the walls, pallets of crates in the middle with aisles between, barrels.</summary>
            void Warehouse()
            {
                AlongWalls(new[] { "Shelf_0" }, 4, Side.Left);
                for (int k = 0; k < 3; k++)
                {
                    var (cx, cz) = Centre();
                    var pal = PutWithRoom("Pallet_Wood_0", 1.6f, cx, cz);
                    if (pal == null) break;
                    On(pal, "Crate_3", 0f, 0f, pal.Yaw);
                }
                Put("Barrel_0", Against("Barrel_0", Side.Back).Where(p => Corner("Barrel_0", p)));
            }

            /// <summary>A workbench at a wall with a crafting bench beside it, a rack, barrels.</summary>
            void Workshop()
            {
                var bench = Put("Table_Metal_0", Against("Table_Metal_0", Side.Left).Concat(Against("Table_Metal_0", Side.Right)), (x, z, yaw) => NearestDoor(x, z));
                Put("Craft_01", Against("Craft_01", Side.Back), (x, z, yaw) => bench == null ? 0 : -Math.Sqrt((x - bench.X) * (x - bench.X) + (z - bench.Z) * (z - bench.Z)));
                AlongWalls(new[] { "Shelf_0" }, 1, Side.Left);
                Put("Barrel_0", Against("Barrel_0", Side.Back).Where(p => Corner("Barrel_0", p)));
            }

            /// <summary>The checkout by the street door, the clerk's back to the room, a till on it; coolers along the
            /// back wall; gondolas in rows with aisles you can pass in.</summary>
            void Shop()
            {
                var front = _res.Front;
                var doors = Doors();
                if (front != null && front.A == _room && doors.Count > 0)
                {
                    var (fdx, fdz) = front.Middle;
                    // facing the door, 2-5 m in from it
                    var (dcx, dcz) = doors.OrderBy(d => (d.X - fdx) * (d.X - fdx) + (d.Z - fdz) * (d.Z - fdz)).First();
                    var cands = new List<(float, float, float)>();
                    for (float x = dcx - 6f; x <= dcx + 6f; x += 0.25f)
                        for (float z = dcz - 6f; z <= dcz + 6f; z += 0.25f)
                            foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
                            {
                                float d = MathF.Sqrt((x - dcx) * (x - dcx) + (z - dcz) * (z - dcz));
                                if (d < 2.5f || d > 6f) continue;
                                var (fx, fz) = FrontOf(yaw);
                                if (((dcx - x) * fx + (dcz - z) * fz) / d < 0.6f) continue;
                                cands.Add((x, z, yaw));
                            }
                    var co = Put("Checkout_0", cands, (x, z, yaw) => -MathF.Abs(MathF.Sqrt((x - dcx) * (x - dcx) + (z - dcz) * (z - dcz)) - 3.5f));
                    if (co != null) On(co, "Register_0", -0.5f, 0f, (co.Yaw + 180f) % 360f);
                }
                for (int k = 0; k < 2; k++) Put("Cooler_0", Against("Cooler_0", Side.Back), (x, z, yaw) => NearestDoor(x, z));
                Gondolas();
            }

            /// <summary>Gondolas in rows down the shop's length, every row starting from the same line and every gondola
            /// end to end with the next, two to a run before a cross aisle (strawberry: "store shelves should try to align
            /// to other shelves"): aisles of 1.8 m between rows and a walk of 1.6 m round the room's edge.</summary>
            void Gondolas()
            {
                var gs = FurnitureCatalog.Sizes["Shelf_1"];
                float len = gs.X1 - gs.X0, deep = gs.Y1 - gs.Y0;
                const float Aisle = 1.8f, Edge = 1.6f;
                var r = _room.Rects.OrderByDescending(q => (q.X1 - q.X0) * (q.Z1 - q.Z0)).First();
                float x0 = r.X0 * Cell, x1 = r.X1 * Cell, z0 = r.Z0 * Cell, z1 = r.Z1 * Cell;
                bool longX = x1 - x0 >= z1 - z0;
                float a0 = longX ? x0 : z0, a1 = longX ? x1 : z1, b0 = longX ? z0 : x0, b1 = longX ? z1 : x1;
                float yaw = longX ? 0f : 90f;   // its length along the shop's
                for (float b = b0 + Edge + deep * 0.5f; b + deep * 0.5f <= b1 - Edge + 1e-3f; b += deep + Aisle)
                {
                    float a = a0 + Edge + len * 0.5f;
                    for (int k = 0; a + len * 0.5f <= a1 - Edge + 1e-3f; k++)
                    {
                        Put("Shelf_1", new[] { longX ? (a, b, yaw) : (b, a, yaw) });
                        a += len + (k % 2 == 1 ? Aisle : 0f);
                    }
                }
                // one with no partner in another row (its neighbours would not fit) is taken back out: the columns line up
                var all = _mine.Where(m => m.Name == "Shelf_1").ToList();
                float Along(FurniturePlan g) => longX ? g.X : g.Z; float Across(FurniturePlan g) => longX ? g.Z : g.X;
                if (all.Select(Across).Distinct().Count() > 1)
                {
                    var loose = all.Where(g => !all.Any(o => MathF.Abs(Across(o) - Across(g)) > 0.5f && MathF.Abs(Along(o) - Along(g)) < 0.05f)).ToList();
                    if (loose.Count == all.Count)
                    {
                        // no two rows share a column: keep the fullest row alone
                        var keep = all.GroupBy(g => MathF.Round(Across(g), 1)).OrderByDescending(g => g.Count()).First().ToList();
                        loose = all.Except(keep).ToList();
                    }
                    foreach (var g in loose) Remove(g);
                }
            }

            /// <summary>A coat stand by the front door, if the hall has it.</summary>
            void Hall()
            {
                var front = _res.Front;
                if (front == null || front.A != _room || _room.Floor != 0) return;
                var (fx0, fz0) = front.Middle;
                Put("Coat_0", Against("Coat_0", Side.Back).Where(p => Corner("Coat_0", p) && MathF.Sqrt((p.X - fx0) * (p.X - fx0) + (p.Z - fz0) * (p.Z - fz0)) <= 6f),
                    (x, z, yaw) => -Math.Sqrt((x - fx0) * (x - fx0) + (z - fz0) * (z - fz0)));
            }
        }
    }
}
