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
        /// <summary>Hung from the ceiling (a ceiling light), its origin at the ceiling's underside and the piece below it:
        /// no floor of its own, nothing it stands in the way of.</summary>
        public bool Ceiling;
        /// <summary>Built at load from these inputs (a curtain, a rug: game/RuntimeDecorGenerators) rather than read off an
        /// OBJ by Name: null for every prop. Such a piece is in BuildingGenResult.RuntimeDecor, never in Furniture -- it has
        /// no FurnitureCatalog size, and every rule that reads Furniture reads it as solid pieces of the catalog's.</summary>
        public RuntimeDecor Gen;
        public (float X, float Z) Front { get { var (c, s) = PlanTrig.CosSin(Yaw); return (-s, -c); } }
        public (float X, float Z) Right { get { var (c, s) = PlanTrig.CosSin(Yaw); return (c, -s); } }
        public (float X0, float Z0, float X1, float Z1) Footprint => Gen != null ? FurnitureCatalog.Footprint(Gen.Size, X, Z, Yaw) : FurnitureCatalog.Footprint(Name, X, Z, Yaw);
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
            ["Bed_2"] = new(-0.90f, 0.90f, -1.87f, 1.87f, 2.92f),          // bunk beds: a single's way in, a wardrobe's height
            ["Wardrobe_0"] = new(-1.03f, 1.03f, -0.38f, 0.50f, 2.60f),
            ["Wardrobe_1"] = new(-1.03f, 1.03f, -0.38f, 0.50f, 2.60f),
            ["Lamp_1"] = new(-0.37f, 0.37f, -0.37f, 0.37f, 2.30f),         // floor lamp
            // ceiling lights (astraclaw, from Lamp_1's shade): origin at the ceiling, the piece hanging below it -- so its
            // highest point is 0; the pendant drops 0.94 m, the flush mount 0.28
            ["Ceiling_Lamp_Pendant_0"] = new(-0.37f, 0.37f, -0.37f, 0.37f, 0f),
            ["Ceiling_Lamp_Flush_0"] = new(-0.37f, 0.37f, -0.37f, 0.37f, 0f),
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
            // the solid corner cabinet (astraclaw; strawberry: "the solid one"): a 1.75 m square, its back corner at -X/-Y,
            // the diagonal front facing +X/+Y, the counters' own 1.35 m (Corner_Cabinet_1_Solid is the grey, Counter_2's)
            ["Corner_Cabinet_0_Solid"] = new(-0.875f, 0.875f, -0.875f, 0.875f, 1.35f),
            ["Counter_1"] = new(-0.75f, 0.75f, -0.50f, 0.62f, 1.83f),       // the sink
            ["Disher_0"] = new(-0.75f, 0.75f, -0.75f, 0.62f, 1.50f),
            ["Tub_0"] = new(-0.85f, 0.85f, -1.67f, 1.60f, 1.73f),
            ["Washer_0"] = new(-0.75f, 0.75f, -0.75f, 0.75f, 1.50f),
            ["Dryer_0"] = new(-0.75f, 0.75f, -0.75f, 0.75f, 1.78f),
            ["Files_0"] = new(-0.39f, 0.39f, -0.47f, 0.56f, 2.14f),
            ["Computer_3"] = new(-0.65f, 0.65f, -0.11f, 0.23f, 1.18f),     // a monitor, on a desk
            ["Lamp_0"] = new(-0.15f, 0.25f, -0.15f, 0.15f, 0.88f),         // a desk lamp, on a desk
            ["Microwave_0"] = new(-0.68f, 0.68f, -0.35f, 0.39f, 0.80f),    // on a counter
            ["Coffee_0"] = new(-0.60f, 0.60f, -0.30f, 0.46f, 0.80f),       // a coffee machine, on a counter
            ["Toaster_0"] = new(-0.29f, 0.29f, -0.47f, 0.54f, 0.63f),      // on a counter
            ["Clock_0"] = new(-0.50f, 0.50f, 0.00f, 0.13f, 0.50f),         // hung on a wall: origin at its back's middle
            ["Poster_0"] = new(-1.50f, 1.50f, -0.10f, 0.10f, 0.75f),        // hung (FurnishDecor): centred in depth too, unlike the clock
            // astraclaw's wall decor kit (VoX approved 2026-10-13), hung by FurnishDecor: the Clock_0 convention -- back at
            // Y 0, centred across and up and down -- so H is the top over the origin and the bottom is -H
            ["Painting_Landscape_0"] = new(-1.000f, 1.000f, 0f, 0.080f, 0.650f),
            ["Painting_Landscape_1"] = new(-1.000f, 1.000f, 0f, 0.080f, 0.650f),
            ["Painting_Landscape_2"] = new(-1.000f, 1.000f, 0f, 0.080f, 0.650f),
            ["Painting_Landscape_3"] = new(-1.000f, 1.000f, 0f, 0.080f, 0.650f),
            ["Painting_Portrait_0"] = new(-0.550f, 0.550f, 0f, 0.080f, 0.750f),
            ["Painting_Portrait_1"] = new(-0.550f, 0.550f, 0f, 0.080f, 0.750f),
            ["Painting_Small_0"] = new(-0.450f, 0.450f, 0f, 0.060f, 0.350f),
            ["Painting_Small_1"] = new(-0.450f, 0.450f, 0f, 0.060f, 0.350f),
            ["Painting_Small_2"] = new(-0.450f, 0.450f, 0f, 0.060f, 0.350f),
            ["Photo_Frames_0"] = new(-0.800f, 0.800f, 0f, 0.050f, 0.400f),
            ["Poster_Tall_0"] = new(-0.500f, 0.500f, 0f, 0.020f, 0.750f),
            ["Poster_Tall_1"] = new(-0.500f, 0.500f, 0f, 0.020f, 0.750f),
            ["Poster_Tall_2"] = new(-0.500f, 0.500f, 0f, 0.020f, 0.750f),
            ["Poster_Tall_3"] = new(-0.500f, 0.500f, 0f, 0.020f, 0.750f),
            ["Mirror_0"] = new(-0.500f, 0.500f, 0f, 0.060f, 0.700f),
            // astraclaw's décor expansion (decor_expansion_manifest.json, measured off the OBJs): the full-height mirror, hung
            // like the rest -- so its 2.8 m runs from Height - 1.4 to Height + 1.4 -- but with floor to stand on before it
            // (DecorRow.Stand)
            ["Mirror_1"] = new(-0.600f, 0.600f, 0f, 0.090f, 1.400f),
            ["Coat_Hooks_0"] = new(-0.750f, 0.750f, 0f, 0.161f, 0.135f),
            ["Key_Rack_0"] = new(-0.200f, 0.200f, 0f, 0.081f, 0.144f),
            // (the expansion's thicker boards: 0.10 m, was 0.04 -- the same GUIDs, so the stock on the 1 rose with them)
            ["Wall_Shelf_0"] = new(-0.800f, 0.800f, 0f, 0.350f, 0.115f),
            ["Wall_Shelf_1"] = new(-0.800f, 0.800f, 0f, 0.350f, 0.370f),
            // and its four new shelves: a short utility one, a two-tier, twin cubbies, a picture ledge
            ["Wall_Shelf_2"] = new(-0.450f, 0.450f, 0f, 0.420f, 0.115f),
            ["Wall_Shelf_3"] = new(-0.850f, 0.850f, 0f, 0.360f, 0.425f),
            ["Wall_Shelf_4"] = new(-0.800f, 0.800f, 0f, 0.330f, 0.325f),
            ["Wall_Shelf_5"] = new(-1.050f, 1.050f, 0f, 0.220f, 0.080f),
            ["Wall_Cabinet_0"] = new(-0.750f, 0.750f, 0f, 0.520f, 0.500f),
            // Counter_0 hung upside down (tools/derive_prop.py --hang): turned over its front axis, backed at Y 0, centred up
            // and down -- 1.35 m deep, so H 0.675, its worktop now its underside
            ["Upper_Counter_0"] = new(-0.750f, 0.750f, 0f, 1.125f, 0.675f),
            ["Wall_Cabinet_1"] = new(-0.750f, 0.750f, 0f, 0.520f, 0.500f),
            ["Utensil_Rail_0"] = new(-0.600f, 0.600f, 0f, 0.100f, 0.250f),
            ["Calendar_0"] = new(-0.250f, 0.250f, 0f, 0.020f, 0.400f),
            ["Towel_Rail_0"] = new(-0.500f, 0.500f, 0f, 0.126f, 0.195f),
            ["Medicine_Cabinet_0"] = new(-0.400f, 0.400f, 0f, 0.221f, 0.500f),
            ["Whiteboard_0"] = new(-1.200f, 1.200f, 0f, 0.089f, 0.700f),
            ["Corkboard_0"] = new(-0.800f, 0.800f, 0f, 0.060f, 0.550f),
            ["Certificate_0"] = new(-0.300f, 0.300f, 0f, 0.040f, 0.400f),
            ["Wall_Map_0"] = new(-1.000f, 1.000f, 0f, 0.030f, 0.700f),
            ["Pegboard_0"] = new(-1.200f, 1.200f, 0f, 0.150f, 0.600f),
            ["Antlers_0"] = new(-0.453f, 0.453f, 0f, 0.483f, 0.410f),
            ["Wall_Sunburst_0"] = new(-0.600f, 0.600f, 0f, 0.121f, 0.600f),   // astraclaw's replacement for the dropped Coat_Hooks_1
            // the deer's head on its plaque (astraclaw, from the stock deer; approved by strawberry): 0.76 m out of the wall
            ["Deer_Wall_Trophy_0"] = new(-0.732f, 0.732f, 0f, 0.763f, 0.667f),
            // astraclaw's potted plants, the four strawberry approved (2026-10-12; Potted_Plant_Floor_0 was not): origin at the
            // middle of the pot's base. The two small ones go on a top, the two big ones on the floor (FurnishPlants). Twice
            // the size they were drawn (strawberry, round 12: "scale ALL four up by at least 2x"; the OBJs themselves,
            // tools/derive_prop.py --scale 2, so every consumer sees one size): the tall one's 3.40 m stands 0.35 m under a
            // storey's 3.75 m ceiling, so none needed capping
            ["Potted_Plant_Table_0"] = new(-0.322f, 0.322f, -0.278f, 0.278f, 0.802f),
            ["Potted_Flowers_0"] = new(-0.444f, 0.444f, -0.384f, 0.384f, 0.736f),
            ["Potted_Plant_Upright_0"] = new(-0.430f, 0.430f, -0.372f, 0.372f, 1.704f),
            ["Potted_Plant_Tall_0"] = new(-1.394f, 1.394f, -1.206f, 1.206f, 3.402f),     // its leaves 2.79 x 2.41 m across
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
        public static (float X0, float Z0, float X1, float Z1) Footprint(string name, float x, float z, float yaw) => Footprint(Sizes[name], x, z, yaw);
        /// <summary>The same, for extents of a piece's own (a runtime-built one's: RuntimeDecor.Size).</summary>
        public static (float X0, float Z0, float X1, float Z1) Footprint(Size s, float x, float z, float yaw)
        {
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
        /// <summary>How far a set may stand off the line straight out from the middle of its couch.</summary>
        public const float TvOffAxis = 0.3f;
        /// <summary>A set this close to a flight, a landing or a stair hole is on the wall the stairs run along.</summary>
        public const float StairWallReach = 1.0f;
        /// <summary>A bed is got into from a long side (strawberry: "double beds should require space on either long sides to
        /// get in/out of, single beds need at least one long side free"): a strip BedAccess deep beside it, from its foot to
        /// BedHeadRoom short of its head (the bedhead's sides are for a lamp), clear floor of its room that its doors can walk
        /// to -- both sides of a double, one of a single, kept clear of what comes after. 0.8 m because the player's capsule
        /// is 0.7 across (PlayerController: radius 0.35) and binds here, not the furniture: the props are about twice real
        /// size (Bed_1 is 2.0 x 3.9 m, a real single 1 x 1.9) while the player, 2 m tall, is about real size, so a real
        /// bedroom's 0.6 m would not let them through.</summary>
        public const float BedAccess = 0.8f, BedHeadRoom = 1.0f;
        public static bool IsDoubleBed(string name) => name == "Bed_0";

        /// <summary>Is plain counter c beside h, in its run: square the same way, their sides within 0.12 m.</summary>
        public static bool Beside(FurniturePlan c, FurniturePlan h)
        {
            if (c.On != null || h.On != null || MathF.Abs(((c.Yaw - h.Yaw) % 360f + 360f) % 360f) > 1f) return false;
            var a = c.Footprint; var b = h.Footprint;
            return a.X0 < b.X1 + 0.12f && a.X1 > b.X0 - 0.12f && a.Z0 < b.Z1 + 0.12f && a.Z1 > b.Z0 - 0.12f;
        }
        /// <summary>Has a kitchen's furniture a plain counter (Counter_0) beside its sink or its stove -- somewhere to put a
        /// pan down, not just appliances (strawberry: "require counters - not just sinks in kitchens")?</summary>
        public static bool Worktop(IEnumerable<FurniturePlan> kitchen)
        {
            var l = kitchen as IList<FurniturePlan> ?? kitchen.ToList();
            return l.Any(c => c.Name == "Counter_0" && l.Any(h => h.Name is "Counter_1" or "Oven_0" && Beside(c, h)));
        }
        /// <summary>Is a kitchen where its home eats -- a house's or a flat's with the dining table in it, or with no dining
        /// room of its own at all (strawberry: "houses without a dining room require at table with at least one chair in the
        /// kitchen"; a flat's dining room would be in its own unit)?</summary>
        public static bool EatsInKitchen(BuildingGenResult r, GenRoom kitchen)
            => r.KitchenDiner || (r.Kind is BuildingKind.Residential or BuildingKind.Apartment
                                  && !r.Layout.Any(g => g.Kind == RoomKind.DiningRoom && g.Unit == kitchen.Unit));
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
                var done = new List<Furnisher>();
                foreach (var room in res.Layout.Where(r => r.Floor == f))
                {
                    var fu = new Furnisher(res, fp, room, rng) { Seed = seed };
                    fu.Run();
                    done.Add(fu);
                }
                // the potted plants once the whole floor is furnished, then the wall décor: what the next room hangs later (a
                // television through a corner) is there to keep clear of, and a plant is floor furniture the décor keeps clear
                // of. Neither draws from rng (FurnishPlants, FurnishDecor).
                if (!NoPlants) foreach (var fu in done) fu.Plants();
                foreach (var fu in done) fu.Decorate();
                // and last the soft furnishings, built at load (FurnishTextiles): curtains at the windows, a rug under the couch
                // or the bed. After the décor, which never sees them -- a curtain belongs to its window, and is not a piece
                // the 0.6 m window rule keeps off it -- and on their own draws, so nothing else in the building moves.
                if (!NoRuntimeDecor) foreach (var fu in done) { fu.Curtains(); fu.Rugs(); }
            }
        }

        /// <summary>One floor at 0.1 m: what is solid (walls; stairs, landings and holes; furniture as it goes in), and
        /// what must stay clear but can be walked on (doorways, the tiles at the stairs) or seen through (windows).</summary>
        sealed partial class FloorPlan
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
            /// <summary>The walls' exact footprints: the grid is 0.1 m, and where a wall's line steps in thickness -- an L's
            /// outside wall running on as a partition, 0.1 m thinner -- a piece set flush to the thin part can clip the
            /// thick one between the cell centres.</summary>
            public readonly List<(float X0, float Z0, float X1, float Z1)> WallRects = new();
            /// <summary>The tiles at the flights' ends, exactly: kept clear on the grid too, but a piece's edge can stand up
            /// to half a cell into one between the cell centres (a couch 5 cm onto a switchback's foot tile).</summary>
            public readonly List<(float X0, float Z0, float X1, float Z1)> TileRects = new();
            readonly BuildingGenResult _res;

            public FloorPlan(BuildingGenResult res, int f)
            {
                _res = res; F = f;
                Ox = -1f; Oz = -1f;
                Nx = (int)MathF.Ceiling((res.CellsX * Cell + 2f) / Step);
                Nz = (int)MathF.Ceiling((res.CellsZ * Cell + 2f) / Step);
                State = new byte[Nx, Nz]; KeepDoor = new bool[Nx, Nz]; KeepTall = new bool[Nx, Nz];
                WinZones = new int[Nx, Nz]; Sight = new bool[Nx, Nz];
                const float M = WallOpenings.StairHoleMargin;
                int FloorOf(float y) => (int)MathF.Round(y / Storey);
                var walls = res.Walls.Where(w => w.Kind == SurfaceKind.Wall && FloorOf(w.Y) == f && w.Height > Storey - 0.01f).ToList();
                // (a wall left out between two rooms that share one space -- open plan's cut, BuildingGen.OpenPlan -- is floor
                // there, not wall: nothing stands with its back to it, and you walk across it; its piers and beam stay wall)
                foreach (var w in walls) foreach (var b in Solid(w)) { Fill(b, Wall); WallRects.Add(b); }
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
                        var foot = Tile(s.X, s.Z, bx, bz, rx, rz, -1f, s.Width);
                        Keep(foot, KeepDoor); TileRects.Add(foot);
                        Targets.Add((mx - bx * 0.5f, mz - bz * 0.5f, null));
                    }
                    if (s.Floor == f - 1 && MathF.Abs(s.Y + Rise(s) - f * Storey) < 0.01f)
                    {
                        float tx = s.X + bx * s.Run, tz = s.Z + bz * s.Run;
                        var top = Tile(tx, tz, bx, bz, rx, rz, +1f, s.Width);
                        Keep(top, KeepDoor); TileRects.Add(top);
                        Targets.Add((mx + bx * (s.Run + M + 0.4f), mz + bz * (s.Run + M + 0.4f), null));
                    }
                }
                // which way each door swings, decided here, before anything is furnished (HangDoors)
                HangDoors(walls);
                // doorways: 1.3 m either side of every opening down to the floor (a vehicle door: 6 m, its lane; a wide
                // open doorway is still a doorway), the
                // opening's width and a little; windows: 0.7 m in front of the glass
                foreach (var w in walls)
                {
                    var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                    var (dx, dz) = (wc, -ws);
                    var (nx, nz) = (ws, wc);
                    for (int k = 0; k < w.Openings.Count; k++)
                    {
                        var o = w.Openings[k];
                        bool door = o.V < 0.05f;
                        if (!door && o.V >= 2.5f) continue;   // a vent or a bathroom's slit: over the tallest piece's head
                        var zone = door ? OpeningZone(w, o, 0.3f, o.Archetype == ArchGarage ? 6f : 1.3f) : OpeningZone(w, o, 0.1f, 0.7f);
                        // (a window's zone counted, so the room it lights may move it: FurnishOpenings)
                        if (door) Keep(zone, KeepDoor); else AddPane(w, k, zone);
                        // a hung door's leaf swings through a quarter circle its own width round its hinge, into the room
                        // HangDoors chose (WallOpening.DoorOpensFront; Door_Pine opens 90 degrees): kept clear whole, as a box
                        // (strawberry: "have stairs block furniture in their footprint ... same for doors")
                        if (door && o.HasDoor && o.Archetype != ArchGarage)
                        {
                            var leaf = LeafSweep(w, o);
                            Keep(leaf, KeepDoor);
                            LeafRects.Add(leaf);
                        }
                        if (door)
                        {
                            float cx = w.X + dx * (o.U + o.Width * 0.5f), cz = w.Z + dz * (o.U + o.Width * 0.5f), d = w.Thickness * 0.5f + 0.5f;
                            Targets.Add((cx + nx * d, cz + nz * d, null));
                            Targets.Add((cx - nx * d, cz - nz * d, null));
                        }
                    }
                }
            }

            /// <summary>What a wall stands on: its length exactly (its ends already run on to its corners), half its
            /// thickness either side. Grown by the half thickness at its ends too, an L's inside corner had a phantom 0.35 m
            /// of outside wall poking past the partition's face into the room -- and a kitchen run lined up on it, 0.38 m
            /// off the real wall.</summary>
            static (float, float, float, float) Box(WallPlan w)
            {
                var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                float dx = wc, dz = -ws, t = w.Thickness * 0.5f;
                float ex = w.X + dx * w.Length, ez = w.Z + dz * w.Length;
                float tx = MathF.Abs(dz) * t, tz = MathF.Abs(dx) * t;   // across the wall only
                return (MathF.Min(w.X, ex) - tx, MathF.Min(w.Z, ez) - tz, MathF.Max(w.X, ex) + tx, MathF.Max(w.Z, ez) + tz);
            }
            /// <summary>The stretch [u0, u1] of a wall's length, as Box makes the whole of it.</summary>
            static (float, float, float, float) Box(WallPlan w, float u0, float u1)
            {
                var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                float dx = wc, dz = -ws, t = w.Thickness * 0.5f;
                float ax = w.X + dx * u0, az = w.Z + dz * u0, ex = w.X + dx * u1, ez = w.Z + dz * u1;
                float tx = MathF.Abs(dz) * t, tz = MathF.Abs(dx) * t;
                return (MathF.Min(ax, ex) - tx, MathF.Min(az, ez) - tz, MathF.Max(ax, ex) + tx, MathF.Max(az, ez) + tz);
            }
            /// <summary>What of a wall stands on the floor: all of it, but for an open plan's cut (an ArchPorch opening from
            /// the floor up, BuildingGen.OpenPlan) -- the wall left out between two rooms that share one space.</summary>
            static IEnumerable<(float, float, float, float)> Solid(WallPlan w)
            {
                var cuts = w.Openings.Where(o => o.Archetype == ArchPorch && o.V < 0.05f).Select(o => (A: o.U, B: o.U1)).OrderBy(c => c.A).ToList();
                if (cuts.Count == 0) { yield return Box(w); yield break; }
                float u = 0f;
                foreach (var (a, b) in cuts)
                {
                    if (a > u + 1e-3f) yield return Box(w, u, a);
                    u = MathF.Max(u, b);
                }
                if (w.Length > u + 1e-3f) yield return Box(w, u, w.Length);
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
        sealed partial class Furnisher
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
                    case RoomKind.BreakRoom: BreakRoom(); break;
                }
                Coats();
                Jitter();
                CeilingLights();
            }

            /// <summary>Ceiling lights (strawberry: "apply ceiling lights to ur layouts"): a pendant in the rooms you sit in
            /// -- over the table where there is one -- a flush mount in the rest; one to about every 9 m of each of the
            /// room's rectangles, at the middle of its share. Hung from the ceiling: the next floor's slab underside, a
            /// flat roof's, or under a pitched roof the roof itself over the spot. Not under a stair hole or over a
            /// flight, not in a wall.</summary>
            // (every 6 m hung four to seven pendants in one living room, a grid like an office's)
            const float LightEvery = 9f;
            /// <summary>How far the pendant hangs below the ceiling (Ceiling_Lamp_Pendant_0, astraclaw).</summary>
            const float PendantDrop = 0.94f;
            void CeilingLights()
            {
                bool pendant = _room.Kind is RoomKind.LivingRoom or RoomKind.DiningRoom or RoomKind.Bedroom or RoomKind.BreakRoom
                               || (_room.Kind == RoomKind.Kitchen && _res.KitchenDiner);
                string lamp = pendant ? "Ceiling_Lamp_Pendant_0" : "Ceiling_Lamp_Flush_0";
                int f = _room.Floor;
                bool top = f == _res.Floors - 1;
                float Ceil(float x, float z)
                {
                    if (!top) return (f + 1) * Storey - Slab;                               // the floor above's underside
                    if (_res.Roof == RoofKind.Flat) return (f + 1) * Storey;                 // the roof slab sits on the wall heads
                    float h = RoofTopAt(_res, x, z);
                    return float.IsNaN(h) ? (f + 1) * Storey : h;
                }
                var spots = new List<(float X, float Z)>();
                foreach (var q in _room.Rects)
                {
                    float x0 = q.X0 * Cell, x1 = q.X1 * Cell, z0 = q.Z0 * Cell, z1 = q.Z1 * Cell;
                    int nx = Math.Max(1, (int)MathF.Round((x1 - x0) / LightEvery)), nz = Math.Max(1, (int)MathF.Round((z1 - z0) / LightEvery));
                    for (int i = 0; i < nx; i++)
                        for (int j = 0; j < nz; j++)
                            spots.Add((x0 + (x1 - x0) * (i + 0.5f) / nx, z0 + (z1 - z0) * (j + 0.5f) / nz));
                }
                // over the dining table, the spot nearest it moves there
                var table = _mine.FirstOrDefault(m => m.Name is "Table_Wood_1" or "Table_Wood_0" && (_room.Kind is RoomKind.DiningRoom or RoomKind.Kitchen or RoomKind.BreakRoom));
                if (table != null && spots.Count > 0)
                {
                    int k = spots.Select((p, i) => (d: (p.X - table.X) * (p.X - table.X) + (p.Z - table.Z) * (p.Z - table.Z), i)).OrderBy(t => t.d).First().i;
                    spots[k] = (table.X, table.Z);
                }
                foreach (var (x, z) in spots)
                {
                    var fp = FurnitureCatalog.Footprint(lamp, x, z, 0f);
                    var grown = (fp.X0 - 0.6f, fp.Z0 - 0.6f, fp.X1 + 0.6f, fp.Z1 + 0.6f);
                    if (_p.StairRects.Any(r => Overlaps(grown, r)) || _p.WallRects.Any(r => Overlaps(fp, r))) continue;
                    // a pendant over something tall (bunk beds) would hang into it: a flush mount there
                    string here = lamp == "Ceiling_Lamp_Pendant_0" && _mine.Any(o => o.On == null && !o.Mounted && !o.Ceiling && Overlaps(fp, o.Footprint)
                                                                               && f * Storey + FurnitureCatalog.Sizes[o.Name].H > Ceil(x, z) - PendantDrop - 0.1f)
                                  ? "Ceiling_Lamp_Flush_0" : lamp;
                    var m = new FurniturePlan { Name = here, X = x, Y = Ceil(x, z), Z = z, Yaw = 0f, Floor = f, Room = _room, Ceiling = true };
                    _mine.Add(m);
                    _res.Furniture.Add(m);
                }
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
                    bool ok = Clear(m.Name, nx, nz, nyaw) && !HitsPiece(m.Name, nx, nz, nyaw, m) && !_p.StairRects.Any(r => Overlaps(after, r))
                              && !_p.TileRects.Any(r => Overlaps(after, r)) && !_p.WallRects.Any(r => Overlaps(after, r)) && !InSwing(after, m);
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

            /// <summary>Is a box in the swing of a door or drawer already in the room -- exactly: a sliver of one falls between
            /// the grid's cell centres, and a turned couch found the 3 cm in front of a fridge.</summary>
            bool InSwing((float X0, float Z0, float X1, float Z1) a, FurniturePlan except)
                => _mine.Any(o => o != except && o.On == null && !o.Mounted && SwingZone(o.Name, o.X, o.Z, o.Yaw) is { } sz && Overlaps(a, sz));

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
                    var c = AccessPoints(m).Select(p => Nearest(p.X, p.Z, 3)).Where(i => i >= 0).ToArray();
                    if (c.Length == 0) return false;
                    sides.Add(c);
                }
                // and a bed's way in: the middle of every strip it keeps clear beside it
                var musts = new List<int>();
                foreach (var (m, pts) in _mustReach)
                    foreach (var (px, pz) in pts)
                    {
                        var c = Nearest(px, pz);
                        if (c < 0) return false;
                        musts.Add(c);
                    }
                if (cells.Count == 0 && sides.Count > 0) cells.Add(sides[0][0]);
                if (cells.Count == 0 && musts.Count > 0) cells.Add(musts[0]);
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
                return want.Count == 0 && sides.All(c => c.Any(i => _seen[i] == _stamp)) && musts.All(i => _seen[i] == _stamp);
            }
            int[] _seen, _queue; int _stamp;
            /// <summary>The clear cell of this room nearest (x, z), within `reach` cells (0.4 m), as an index into the room's
            /// box; -1 if none.</summary>
            int Nearest(float x, float z, int reach = 4)
            {
                int i0 = _p.I(x) - _i0, j0 = _p.J(z) - _j0;
                for (int r = 0; r <= reach; r++)
                    for (int a = i0 - r; a <= i0 + r; a++)
                        for (int b = j0 - r; b <= j0 + r; b++)
                            if (a >= 0 && b >= 0 && a < _ni && b < _nj && _inRoom[a, b] && _p.State[_i0 + a, _j0 + b] == FloorPlan.Free) return a * _nj + b;
                return -1;
            }
            /// <summary>Must you be able to get to it? Not a chair, a lamp, a barrel or a crate, a potted plant -- nor a plain
            /// counter, worktop reached along its run (the one in an L's corner is a blind corner, as in any kitchen).</summary>
            static bool Access(string name) => !(name.StartsWith("Chair") || name.StartsWith("Lamp") || name.StartsWith("Barrel") || name.StartsWith("Crate")
                                                 || name.StartsWith("Cardboard") || name.StartsWith("Propane") || name.StartsWith("Pallet") || name.StartsWith("Coat")
                                                 || name.StartsWith("Potted") || name == "Counter_0");
            /// <summary>Where you might stand to use a piece: 0.4 m out from the middle of its front, or of either side (a
            /// workbench or a tub along a wall is used from its long side, a bed got into from one) -- with clear floor within
            /// 0.3 m of it (Connected). Half a metre out within 0.4 m reached cells a test 0.4 m out within 0.3 m does not:
            /// a rack wedged face to the wall between another rack and the end wall "had" a side through a cell past the
            /// other rack's corner.</summary>
            public static (float X, float Z)[] AccessPoints(FurniturePlan m)
            {
                var s = FurnitureCatalog.Sizes[m.Name]; var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                float mx = (s.X0 + s.X1) * 0.5f, my = (s.Y0 + s.Y1) * 0.5f;
                // a corner cabinet is used from its diagonal front: past its outer corner, the counters of the L either side
                if (m.Name.StartsWith("Corner_Cabinet"))
                    return new[] { (m.X + rx * (s.X1 + 0.3f) + fx * (s.Y1 + 0.3f), m.Z + rz * (s.X1 + 0.3f) + fz * (s.Y1 + 0.3f)) };
                return new[]
                {
                    (m.X + rx * mx + fx * (s.Y1 + 0.4f), m.Z + rz * mx + fz * (s.Y1 + 0.4f)),
                    (m.X + rx * (s.X1 + 0.4f) + fx * my, m.Z + rz * (s.X1 + 0.4f) + fz * my),
                    (m.X + rx * (s.X0 - 0.4f) + fx * my, m.Z + rz * (s.X0 - 0.4f) + fz * my),
                };
            }

            /// <summary>The strip in front of a piece its doors swing through, or where you stand to use it (a sink's:
            /// Stand), as a box; nothing when nothing opens.</summary>
            static (float X0, float Z0, float X1, float Z1)? SwingZone(string name, float x, float z, float yaw)
            {
                float d = MathF.Max(Swing(name), Stand(name));
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
            /// <summary>Points a piece needs walked to (the middles of a bed's access strips), checked by Connected.</summary>
            readonly Dictionary<FurniturePlan, List<(float X, float Z)>> _mustReach = new();

            /// <summary>A bed's two long sides' access strips (its right, then its left), as boxes: BedAccess deep, from its
            /// foot to BedHeadRoom short of its head. Square to the walls, as a bed always stands.</summary>
            static (float X0, float Z0, float X1, float Z1)[] BedSides(string name, float x, float z, float yaw)
            {
                var s = FurnitureCatalog.Sizes[name];
                var (c, sn) = PlanTrig.CosSin(yaw);
                var outp = new (float, float, float, float)[2];
                for (int k = 0; k < 2; k++)
                {
                    // across (the bed's +X, right) and along (its +Y, toward the foot), in its own frame
                    float a0 = k == 0 ? s.X1 + 0.02f : s.X0 - 0.02f - BedAccess, a1 = a0 + BedAccess, b0 = s.Y0 + BedHeadRoom, b1 = s.Y1;
                    // right (c, -sn) * a + front (-sn, -c) * b
                    float ax = c * a0, bx = c * a1, ay = -sn * b0, by = -sn * b1, az = -sn * a0, bz = -sn * a1, cy = -c * b0, dy = -c * b1;
                    outp[k] = (x + MathF.Min(ax, bx) + MathF.Min(ay, by), z + MathF.Min(az, bz) + MathF.Min(cy, dy),
                               x + MathF.Max(ax, bx) + MathF.Max(ay, by), z + MathF.Max(az, bz) + MathF.Max(cy, dy));
                }
                return outp;
            }
            /// <summary>Which of a bed's long sides at (x, z, yaw) are clear to get in at: this room's floor, nothing solid on
            /// it -- wall, stairs, hole, a piece -- (a doorway is fine: it is floor you walk on). The ones it keeps: both of a
            /// double, the one nearer the door of a single; null when it has too few.</summary>
            List<(float X0, float Z0, float X1, float Z1)> BedWay(string name, float x, float z, float yaw)
            {
                var ok = BedSides(name, x, z, yaw).Where(a => SwingClear(a) && !_p.StairRects.Any(r => Overlaps(a, r)) && !_p.WallRects.Any(r => Overlaps(a, r))
                                                              && !_mine.Any(o => o.On == null && !o.Mounted && !o.Ceiling && Overlaps(a, o.Footprint))).ToList();
                int need = IsDoubleBed(name) ? 2 : 1;
                if (ok.Count < need) return null;
                return ok.OrderBy(a => NearestDoor((a.X0 + a.X1) * 0.5f, (a.Z0 + a.Z1) * 0.5f)).Take(need).ToList();
            }

            /// <summary>Candidates best first, each score nudged by a seeded draw taken here, one per candidate in order (see
            /// Rng.Keyed: never inside a LINQ key selector).</summary>
            List<(float X, float Z, float Yaw)> Rank(IEnumerable<(float X, float Z, float Yaw)> cands, Func<float, float, float, double> score, Rng rng = null)
            {
                var l = new List<((float X, float Z, float Yaw) C, double S)>();
                foreach (var c in cands) l.Add((c, (score?.Invoke(c.X, c.Z, c.Yaw) ?? 0) + (rng ?? _rng).Int(1000) * 1e-4));
                return l.OrderByDescending(x => x.S).Select(x => x.C).ToList();
            }

            /// <summary>Put a piece at the first candidate (best score first) where it is clear, its doors can open, and
            /// it leaves the room walkable; null when none will do. (`rng`: the draws to rank by, the Furnisher's own by
            /// default.)</summary>
            FurniturePlan Put(string name, IEnumerable<(float X, float Z, float Yaw)> cands, Func<float, float, float, double> score = null, float y = 0f,
                              Rng rng = null)
            {
                var s = FurnitureCatalog.Sizes[name];
                var ranked = Rank(cands, score, rng);
                if (FurnishTrace != null)
                    FurnishTrace(System.FormattableString.Invariant($"put {name} in {_room}: {ranked.Count} candidates, first {(ranked.Count > 0 ? ranked[0].ToString() : "-")}"));
                foreach (var (x, z, yaw) in ranked)
                {
                    var fp = FurnitureCatalog.Footprint(name, x, z, yaw);
                    if (!Clear(name, x, z, yaw)) continue;
                    // the grid is 0.1 m: a sliver of overlap with a piece already in, or with the stairs, can fall between
                    // its cell centres
                    if (HitsPiece(name, x, z, yaw)) continue;
                    if (InSwing(fp, null)) continue;
                    if (_p.StairRects.Any(r => Overlaps(fp, r)) || _p.TileRects.Any(r => Overlaps(fp, r))) continue;
                    if (_p.WallRects.Any(r => Overlaps(fp, r))) continue;
                    var swing = SwingZone(name, x, z, yaw);
                    if (swing != null && (!SwingClear(swing.Value) || _mine.Any(o => o.On == null && !o.Mounted && Overlaps(swing.Value, o.Footprint)))) continue;
                    // where you stand at a sink, no door's leaf swings (a doorway's walk is fine: you stand in it)
                    if (Stand(name) > 0f && swing != null && _p.LeafRects.Any(r => Overlaps(swing.Value, r))) continue;
                    // a bed: a long side (both, a double's) to get in at
                    var way = name.StartsWith("Bed") ? BedWay(name, x, z, yaw) : null;
                    if (name.StartsWith("Bed") && way == null) continue;
                    var cells = Under(name, x, z, yaw).Where(c => _p.State[c.I, c.J] == FloorPlan.Free).ToList();
                    foreach (var (i, j) in cells) _p.State[i, j] = FloorPlan.Furn;
                    var m = new FurniturePlan { Name = name, X = x, Y = _room.Floor * Storey + y, Z = z, Yaw = yaw, Floor = _room.Floor, Room = _room };
                    _mine.Add(m);
                    if (way != null) _mustReach[m] = way.Select(a => ((a.X0 + a.X1) * 0.5f, (a.Z0 + a.Z1) * 0.5f)).ToList();
                    if (Connected())
                    {
                        _res.Furniture.Add(m);
                        // its swing stays clear, and a bed's way in: walkable, but nothing else stands in them
                        var keep = new List<(float, float, float, float)>();
                        if (swing != null) keep.Add(swing.Value);
                        if (way != null) keep.AddRange(way);
                        if (keep.Count > 0)
                        {
                            var held = new List<(int, int)>();
                            foreach (var a in keep)
                                foreach (var (i, j) in _p.Cells(a)) if (!_p.KeepDoor[i, j]) { _p.KeepDoor[i, j] = true; held.Add((i, j)); }
                            _reserved[m] = held;
                            foreach (var a in keep) Recount(a);
                        }
                        Recount(fp);
                        return m;
                    }
                    _mine.Remove(m); _mustReach.Remove(m);
                    foreach (var (i, j) in cells) _p.State[i, j] = FloorPlan.Free;
                }
                return null;
            }

            /// <summary>Hang a piece on a wall (a flat television): where its back meets wall with no window or door
            /// in that stretch, and nothing hung there already. No floor of its own. (`reach`: how far in front of it the
            /// window and doorway zones are looked for -- the wall décor looks only just past its face, so a garage's 6 m
            /// vehicle lane does not keep a poster off the side walls beside it. `refuse`: places it may not go, asked of
            /// the candidates once they are ranked, so the draws Rank takes are the same however many it refuses.)</summary>
            FurniturePlan Hang(string name, IEnumerable<(float X, float Z, float Yaw)> cands, Func<float, float, float, double> score = null, float height = MountHeight, Rng rng = null, float reach = 0.6f,
                               Func<float, float, float, bool> refuse = null)
            {
                var ranked = Rank(cands, score, rng);
                foreach (var (x, z, yaw) in ranked)
                {
                    if (refuse != null && refuse(x, z, yaw)) continue;
                    var fp = FurnitureCatalog.Footprint(name, x, z, yaw);
                    // the wall behind it: no window's glass and no doorway along its span
                    var (fx, fz) = FrontOf(yaw);
                    var strip = (fp.X0 - (fx < 0 ? reach : 0f), fp.Z0 - (fz < 0 ? reach : 0f), fp.X1 + (fx > 0 ? reach : 0f), fp.Z1 + (fz > 0 ? reach : 0f));
                    if (_p.Cells(strip).Any(c => _p.KeepTall[c.I, c.J] || (_p.KeepDoor[c.I, c.J] && !_reserved.Values.Any(l => l.Contains(c))))) continue;
                    if (_mine.Any(o => o.Mounted && Overlaps(fp, o.Footprint))) continue;
                    var m = new FurniturePlan { Name = name, X = x, Y = _room.Floor * Storey + height, Z = z, Yaw = yaw, Floor = _room.Floor, Room = _room, Mounted = true };
                    _mine.Add(m); _res.Furniture.Add(m);
                    return m;
                }
                return null;
            }

            static (float X0, float Z0, float X1, float Z1) Grow((float X0, float Z0, float X1, float Z1) a, float m) => (a.X0 - m, a.Z0 - m, a.X1 + m, a.Z1 + m);
            static bool Overlaps((float X0, float Z0, float X1, float Z1) a, (float X0, float Z0, float X1, float Z1) b)
                => a.X0 < b.X1 - 0.005f && a.X1 > b.X0 + 0.005f && a.Z0 < b.Z1 - 0.005f && a.Z1 > b.Z0 + 0.005f;

            /// <summary>Set a piece on another's top (a monitor on a desk, a till on a checkout): no floor of its own.</summary>
            FurniturePlan On(FurniturePlan host, string name, float along, float back, float yaw)
            {
                var (fx, fz) = host.Front; var (rx, rz) = host.Right;
                var m = new FurniturePlan
                {
                    Name = name, X = host.X + rx * along + fx * back, Z = host.Z + rz * along + fz * back, Y = host.Y + FurnitureCatalog.Sizes[host.Name].H,
                    Yaw = yaw, Floor = host.Floor, Room = _room, On = host,
                };
                _res.Furniture.Add(m);
                return m;
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
                            if (Flush(FurnitureCatalog.Footprint(name, c.Item1, c.Item2, yaw), nx, nz) >= 0.9 && (side != Side.Back || Backed(name, c.Item1, c.Item2, yaw))) yield return c;
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

            /// <summary>Is a piece's back against a real wall along 80% of it, within 0.14 m -- read off the walls themselves,
            /// not the grid: a grid cell a wall's end only touches counts as wall there, and a fridge at the end of a run
            /// stood 0.37 m past the wall's end, over the opening beside it.</summary>
            bool Backed(string name, float x, float z, float yaw)
            {
                var fp = FurnitureCatalog.Footprint(name, x, z, yaw);
                var (fx, fz) = FrontOf(yaw);
                const float Back = 0.14f;
                int n = 0, hit = 0;
                bool WallAt(float px, float pz) => _p.WallRects.Any(r => px >= r.X0 && px <= r.X1 && pz >= r.Z0 && pz <= r.Z1);
                if (MathF.Abs(fx) > 0.5f)
                {
                    float bx = fx > 0 ? fp.X0 - Back : fp.X1 + Back;
                    for (float pz = fp.Z0 + 0.05f; pz < fp.Z1; pz += 0.1f) { n++; if (WallAt(bx, pz)) hit++; }
                }
                else
                {
                    float bz = fz > 0 ? fp.Z0 - Back : fp.Z1 + Back;
                    for (float px = fp.X0 + 0.05f; px < fp.X1; px += 0.1f) { n++; if (WallAt(px, bz)) hit++; }
                }
                return n > 0 && hit >= 0.8f * n;
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
            /// it, the door in view across the room but not straight past its foot (the "coffin" position); and always
            /// a way in at a long side (both, a double's: BedAccess). Then a wardrobe, a lamp at the bedhead, a bookcase
            /// in a big room. The command position is a preference (strawberry: "feng shui is a suggestion not a
            /// requirement, always preffered where layout allows"): where the room will not have it, a single bed goes
            /// with the door past its foot, then under a window, then with its long side to a wall -- never no bed.</summary>
            void Bedroom()
            {
                float minSide = _room.Rects.Min(r => Math.Min(r.X1 - r.X0, r.Z1 - r.Z0)) * Cell;
                // a double where it fits, on a roll (strawberry: "bedrooms may be single or double bed")
                string bed = minSide >= 4.5f && _area >= 20f && _rng.Chance(60) ? "Bed_0" : "Bed_1";
                // now and then bunk beds, in a house with a bedroom besides this one
                if (bed == "Bed_1" && _res.Kind is BuildingKind.Residential or BuildingKind.Apartment
                    && _res.Layout.Count(g => g.Kind == RoomKind.Bedroom && g.Unit == _room.Unit) >= 2 && _rng.Chance(25)) bed = "Bed_2";
                // never the coffin (the door straight past its foot) while anywhere else will do -- the score alone let an
                // L-shaped room, every wall of which faced the door, pick one
                bool Coffin(string bd, (float X, float Z, float Yaw) c)
                {
                    var s0 = FurnitureCatalog.Sizes[bd]; var (fx, fz) = FrontOf(c.Yaw); var (rx, rz) = RightOf(c.Yaw);
                    return Doors().Any(d => { float vx = d.X - c.X, vz = d.Z - c.Z; return vx * fx + vz * fz > s0.Y1 && MathF.Abs(vx * rx + vz * rz) < (s0.X1 - s0.X0) * 0.5f; });
                }
                var b = Put(bed, Against(bed, Side.Back).Where(c => !UnderWindow(bed, c) && !Coffin(bed, c)), (x, z, yaw) => BedScore(bed, x, z, yaw));
                if (b == null && bed != "Bed_1") b = Put("Bed_1", Against("Bed_1", Side.Back).Where(c => !UnderWindow("Bed_1", c) && !Coffin("Bed_1", c)), (x, z, yaw) => BedScore("Bed_1", x, z, yaw));
                // the room's own window in the way of its head: the window makes way -- slides, narrows, or goes where the
                // room has another (strawberry: "allow room layout to alter windows after the fact if necessary") -- before
                // the bed gives up its head to a wall for the coffin or the window
                foreach (var bd in bed == "Bed_1" ? new[] { bed } : new[] { bed, "Bed_1" })
                    if (b == null) b = PutMakingWay(bd, Against(bd, Side.Back).Where(c => UnderWindow(bd, c) && !Coffin(bd, c)), (x, z, yaw) => BedScore(bd, x, z, yaw), head: true);
                if (b == null) b = Put("Bed_1", Against("Bed_1", Side.Back).Where(c => !UnderWindow("Bed_1", c)), (x, z, yaw) => BedScore("Bed_1", x, z, yaw));
                // (the preferences given up one at a time, the bed's score still ranking what is left)
                if (b == null) b = Put("Bed_1", Against("Bed_1", Side.Back), (x, z, yaw) => BedScore("Bed_1", x, z, yaw));
                if (b == null) b = Put("Bed_1", Against("Bed_1", Side.Left).Concat(Against("Bed_1", Side.Right)), (x, z, yaw) => BedScore("Bed_1", x, z, yaw));
                Func<float, float, float, double> wardrobe = (x, z, yaw) => NearestDoor(x, z) - (b != null && MathF.Abs(yaw - b.Yaw) < 1f ? 2 : 0);
                // (and where its windows leave it no wall, one of them makes way for it)
                if (Put("Wardrobe_0", Against("Wardrobe_0", Side.Back), wardrobe) == null) PutMakingWay("Wardrobe_0", Against("Wardrobe_0", Side.Back), wardrobe, head: false);
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
            /// nothing tall between. The couch moves until one fits. Then a lamp in a corner, a bookcase. Its back to a wall
            /// and the big couch are preferences (strawberry: "feng shui is a suggestion not a requirement"): where no set
            /// will go with the big one, the small one; where neither goes against a wall, one standing free in the room,
            /// facing the way in -- a living room is never left without its couch.</summary>
            void LivingRoom()
            {
                string couch = _area >= 18f ? "Couch_0" : "Couch_1";
                bool crtFirst = _rng.Chance(50);
                var ranked = Rank(Against(couch, Side.Back), CouchScore);
                // the seeded kind of set wherever the couch can go first, the other kind after; then the smaller couch
                FurniturePlan seat = null;
                foreach (var (sofa, cands) in couch == "Couch_0" ? new[] { ("Couch_0", ranked), ("Couch_1", (List<(float X, float Z, float Yaw)>)null) } : new[] { (couch, ranked) })
                {
                    var order = cands ?? Rank(Against(sofa, Side.Back), CouchScore);
                    foreach (bool crt in new[] { crtFirst, !crtFirst })
                    {
                        foreach (var cand in order)
                        {
                            var c = Put(sofa, new[] { cand });
                            if (c == null) continue;
                            if (crt ? Crt(c) : WallTv(c)) { seat = c; break; }
                            Remove(c);
                        }
                        if (seat != null) break;
                    }
                    if (seat != null) break;
                }
                if (seat == null)
                {
                    // no set will go anywhere: the couch at least, backed to a wall; and failing that standing free, facing
                    // the way in, with a set if one will go
                    var alone = Put(couch, ranked) ?? (couch == "Couch_0" ? Put("Couch_1", Against("Couch_1", Side.Back), CouchScore) : null);
                    if (alone == null)
                        foreach (var sofa in couch == "Couch_0" ? new[] { "Couch_0", "Couch_1" } : new[] { couch })
                            if ((alone = Put(sofa, Free(sofa), CouchScore)) != null) break;
                    if (alone != null && (crtFirst ? Crt(alone) || WallTv(alone) : WallTv(alone) || Crt(alone))) seat = alone;
                }
                if (seat != null)
                {
                    // keep the view: nothing tall goes down between the couch and its set afterwards
                    var set = _res.Furniture.Last(m => m.Name.StartsWith("Television"));
                    var (fx, fz) = seat.Front;
                    float x0 = seat.X + fx * 0.3f, z0 = seat.Z + fz * 0.3f, d = MathF.Sqrt((set.X - x0) * (set.X - x0) + (set.Z - z0) * (set.Z - z0));
                    for (float t = 0f; t <= d; t += 0.1f)
                    {
                        float x = x0 + (set.X - x0) * t / d, z = z0 + (set.Z - z0) * t / d;
                        foreach (var (i, j) in _p.Cells((x - 0.3f, z - 0.3f, x + 0.3f, z + 0.3f))) _p.KeepTall[i, j] = _p.Sight[i, j] = true;
                    }
                    Recount((MathF.Min(x0, set.X) - 0.4f, MathF.Min(z0, set.Z) - 0.4f, MathF.Max(x0, set.X) + 0.4f, MathF.Max(z0, set.Z) + 0.4f));
                }
                Put("Lamp_1", Against("Lamp_1", Side.Back).Where(p => Corner("Lamp_1", p)), (x, z, yaw) => NearestDoor(x, z));
                if (_area >= 24f) Put("Shelf_2", Against("Shelf_2", Side.Back), (x, z, yaw) => NearestDoor(x, z));
                Clock();
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
                    // straight ahead of it (strawberry: "tv needs alignment with the couch"), and not on a wall the stairs run
                    // along ("tvs shouldnt be allowed on walls occupied by stairs")
                    return ahead >= TvNear && ahead <= far && MathF.Abs(side) <= TvOffAxis && !ByStairs(name, p) && Sight(c.X + fx * 0.3f, c.Z + fz * 0.3f, p.X, p.Z, c, except);
                });
            }
            /// <summary>Is a piece against a wall the stairs stand along -- a flight, a landing or a hole within a metre of it?</summary>
            bool ByStairs(string name, (float X, float Z, float Yaw) p)
            {
                var fp = FurnitureCatalog.Footprint(name, p.X, p.Z, p.Yaw);
                var near = (fp.X0 - StairWallReach, fp.Z0 - StairWallReach, fp.X1 + StairWallReach, fp.Z1 + StairWallReach);
                return _p.StairRects.Any(r => Overlaps(near, r));
            }
            /// <summary>A flat television on the wall the couch faces -- never a fireplace's wall (strawberry, round 12: the
            /// wall décor's rule, FireFace, now the set's too), refused once ranked as the clock is.</summary>
            bool WallTv(FurniturePlan c)
            {
                var (rx, rz) = c.Right;
                return Hang("Television_0", Facing(c, "Television_0", WallTvReach), (x, z, _) => -MathF.Abs((x - c.X) * rx + (z - c.Z) * rz),
                            refuse: (x, z, yaw) => FireFace(BackFace("Television_0", x, z, yaw))) != null;
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

            /// <summary>Everywhere a piece might stand free in the room, square to it: every 0.25 m, each way round.</summary>
            IEnumerable<(float X, float Z, float Yaw)> Free(string name)
            {
                foreach (var r in _room.Rects)
                    for (float x = r.X0 * Cell; x <= r.X1 * Cell; x += 0.25f)
                        for (float z = r.Z0 * Cell; z <= r.Z1 * Cell; z += 0.25f)
                            for (int k = 0; k < 4; k++) yield return (x, z, k * 90f);
            }

            bool Corner(string name, (float X, float Z, float Yaw) p)
            {
                var fp = FurnitureCatalog.Footprint(name, p.X, p.Z, p.Yaw);
                return Flush(fp, 1, 0) > 0.6 || Flush(fp, -1, 0) > 0.6 ? Flush(fp, 0, 1) > 0.6 || Flush(fp, 0, -1) > 0.6 : false;
            }

            /// <summary>The table in the middle of the room's clear floor with a metre round it, and dining chairs
            /// (strawberry: "use chair 1 instead of chair 2 for the dining room") facing it: at a long table along its
            /// sides and one at each end, at the round one evenly round its rim (strawberry: "consider the shape of
            /// furniture when placing stuff around it (the kitchen table)"). Standing free in the middle is a preference
            /// ("feng shui is a suggestion not a requirement"): where no table will, one against a wall -- the long one
            /// along it, the small one at it -- seated on its open sides. A table seating fewer than `minChairs` is taken
            /// back out and the next place tried (a house that eats in its kitchen: "houses without a dining room require
            /// a table with at least one chair in the kitchen"). Null when no table goes in.</summary>
            FurniturePlan Dining(int minChairs = 0)
            {
                var (cx, cz) = Centre();
                bool big = _room.Rects.Min(r => Math.Min(r.X1 - r.X0, r.Z1 - r.Z0)) * Cell >= 6.5f;
                double Mid(float x, float z) => -Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                // the round one in a big room, else the long one; tight, the small square one, and then a little less room round
                // -- down to a chair's depth, in a small house's kitchen-diner with its walk to three doors across it; then
                // against a wall
                var ladder = new List<(string Table, Func<IEnumerable<(float X, float Z, float Yaw)>> Cands, Func<float, float, float, double> Score)>();
                foreach (var (table, room) in new[] { ("Table_Wood_1", 1.0f), ("Table_Wood_0", 1.0f), ("Table_Wood_3", 1.0f), ("Table_Wood_3", 0.8f), ("Table_Wood_3", 0.6f) })
                {
                    if (table == "Table_Wood_1" && !big) continue;
                    ladder.Add((table, () => Roomy(table, room), RoomyScore(table, cx, cz)));
                }
                ladder.Add(("Table_Wood_0", () => Against("Table_Wood_0", Side.Left).Concat(Against("Table_Wood_0", Side.Right)), (x, z, _) => Mid(x, z)));
                ladder.Add(("Table_Wood_3", () => Against("Table_Wood_3", Side.Back), (x, z, _) => Mid(x, z)));
                foreach (var (table, cands, score) in ladder)
                {
                    int tried = 0;
                    foreach (var c in Rank(cands(), score))
                    {
                        var t = Put(table, new[] { c });
                        if (t == null) continue;
                        var chairs = Seat(t);
                        if (chairs.Count >= minChairs) return t;
                        foreach (var m in chairs) Remove(m);
                        Remove(t);
                        if (++tried >= TableTries) break;
                    }
                }
                return null;
            }
            /// <summary>How many places a table is tried at, of each kind, for one that seats enough.</summary>
            const int TableTries = 12;

            /// <summary>Seat a table: each way of seating it in turn, actually placed (a chair the room's walk cannot spare is
            /// refused only then), keeping the first that seats everyone or else the one that seats the most.</summary>
            List<FurniturePlan> Seat(FurniturePlan t)
            {
                var best = new List<(float X, float Z, float Yaw)>(); int most = -1;
                foreach (var seats in Seatings(t))
                {
                    var placed = seats.Select(sp => (sp, m: Put(DiningChair, new[] { sp }))).Where(x => x.m != null).ToList();
                    if (placed.Count == seats.Count) return placed.Select(x => x.m).ToList();
                    if (placed.Count > most) { most = placed.Count; best = placed.Select(x => x.sp).ToList(); }
                    foreach (var (_, m) in placed) Remove(m);
                }
                return best.Select(sp => Put(DiningChair, new[] { sp })).Where(m => m != null).ToList();
            }

            const string DiningChair = "Chair_Wood_0";

            /// <summary>The ways of seating a table, best first, each a list of chairs facing it.</summary>
            List<List<(float X, float Z, float Yaw)>> Seatings(FurniturePlan t)
            {
                var ts = FurnitureCatalog.Sizes[t.Name]; var cs = FurnitureCatalog.Sizes[DiningChair];
                var seats = new List<(float, float, float)>();
                if (t.Name == "Table_Wood_1")
                {
                    // round: four round it (strawberry: "need less chairs at the round table"), on the diagonals first --
                    // they reach less far into the room -- then square
                    float r = ts.X1 + 0.05f + cs.Y1;
                    List<(float, float, float)> Ring(int n, float phase) => Enumerable.Range(0, n).Select(k =>
                    {
                        var (dz, dx) = PlanTrig.CosSin(t.Yaw + phase + k * 360f / n);
                        return (t.X + dx * r, t.Z + dz * r, YawFacing(-dx, -dz));
                    }).ToList();
                    return new[] { 45f, 0f }.Select(ph => Ring(4, ph).Select(c => ((float X, float Z, float Yaw))c).ToList()).ToList();
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
                return new List<List<(float X, float Z, float Yaw)>> { seats.Select(c => ((float X, float Z, float Yaw))c).ToList() };
            }

            // (to a thousandth of a degree: a platform's atan2f may differ in the last place, and the yaw is fed back in)
            static float YawFacing(float fx, float fz) => MathF.Round((MathF.Atan2(-fx, -fz) * 180f / MathF.PI + 360f) % 360f * 1000f) / 1000f % 360f;

            /// <summary>Put a free-standing piece as near (cx, cz) as it goes with `room` clear all round it.</summary>
            FurniturePlan PutWithRoom(string name, float room, float cx, float cz) => Put(name, Roomy(name, room), RoomyScore(name, cx, cz));

            /// <summary>Where a free-standing piece has `room` clear all round it.</summary>
            List<(float X, float Z, float Yaw)> Roomy(string name, float room)
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
                return c;
            }
            /// <summary>Nearest (cx, cz) first, lying along the room's length.</summary>
            Func<float, float, float, double> RoomyScore(string name, float cx, float cz)
            {
                var bb = (X0: _room.Rects.Min(r => r.X0) * Cell, Z0: _room.Rects.Min(r => r.Z0) * Cell, X1: _room.Rects.Max(r => r.X1) * Cell, Z1: _room.Rects.Max(r => r.Z1) * Cell);
                bool longX = bb.X1 - bb.X0 >= bb.Z1 - bb.Z0;
                return (x, z, yaw) => -Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)) + (yaw == (longX ? 90f : 0f) ? 0.5 : 0);
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
                bool freeFridge = _rng.Chance(40), tryL = _rng.Chance(55);
                // the seeded layouts first, then the plainer ones; each leaves the kitchen complete or as it found it
                var tries = new List<Func<bool>>();
                // (an L with the corner cabinet in its corner, at any corner and either fridge, before a plain one)
                if (tryL) tries.Add(() => Done(L(runs, freeFridge, true)) || Done(L(runs, !freeFridge, true)) || Done(L(runs, freeFridge)) || Done(L(runs, !freeFridge)));
                if (freeFridge) tries.Add(() => RunWithOwnFridge(runs));
                tries.Add(() => Done(Row(runs, new[] { "Fridge_0", "Counter_1", "Disher_0", "Oven_0" })));
                if (!freeFridge) tries.Add(() => RunWithOwnFridge(runs));
                if (!tryL) tries.Add(() => Done(L(runs, false, true)) || Done(L(runs, true, true)) || Done(L(runs, false)) || Done(L(runs, true)));
                tries.Add(() => Pair(runs));
                // and counter space to work on (strawberry: "require kitchens to have actual counter space", then "require
                // counters - not just sinks in kitchens"): a layout counts only with a plain counter beside its sink or its
                // stove (Worktop) and MinPrep plain counters in all, topped up beside the sink and the stove where the wall
                // goes on; the plainer layouts get their turn before any kitchen makes do with the one counter
                int want = MinPrep;
                bool Attempt(Func<bool> t)
                {
                    int n0 = _mine.Count;
                    if (!t()) return false;
                    if (TopUp(want) || want == 0) return true;
                    foreach (var m in _mine.Skip(n0).ToList()) Remove(m);
                    return false;
                }
                var three = new[] { "Counter_1", "Fridge_0", "Oven_0" };
                bool Full() => three.All(n => _mine.Any(m => m.Name == n));
                static float Dist(FurniturePlan p, float x, float z) => MathF.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
                // a kitchen too cut up by its doors for any run (a small house's, with three doors and a back door) still
                // gets what it is for: the sink with a counter beside it (or failing that the stove with one), then the
                // fridge and the stove, each on whatever wall it fits nearest the others -- a pace apart at least, the stove
                // never beside the sink, and the three inside a work triangle where the room allows (a preference: a kitchen
                // with all three a little far apart beats one without its stove)
                void Scatter(bool counter)
                {
                    if (counter && !_mine.Any(m => m.Name is "Counter_1" or "Oven_0")) _ = Row(runs, new[] { "Counter_1", "Counter_0" }) ?? Row(runs, new[] { "Oven_0", "Counter_0" });
                    foreach (bool triangle in new[] { true, false })
                        foreach (var a in three)
                        {
                            if (_mine.Any(m => m.Name == a)) continue;
                            var placed = three.Select(n => _mine.FirstOrDefault(m => m.Name == n)).Where(p => p != null).ToList();
                            float Round(float x, float z) => placed.Sum(p => Dist(p, x, z)) + (placed.Count == 2 ? Dist(placed[0], placed[1].X, placed[1].Z) : 0f);
                            // (water and fire apart, and the fridge off the stove's side, however far round the three are)
                            bool Clash((float X, float Z, float Yaw) c) => placed.Any(p => (a == "Oven_0") != (p.Name == "Oven_0")
                                && Overlaps(Grow(FurnitureCatalog.Footprint(a, c.X, c.Z, c.Yaw), 0.5f), p.Footprint));
                            var cands = Against(a, Side.Back).Where(c => placed.All(p => Dist(p, c.X, c.Z) >= 1.8f) && !Clash(c) && (!triangle || Round(c.X, c.Z) <= (placed.Count == 2 ? 13f : 6.5f))).ToList();
                            Put(a, cands, (x, z, _) => -Round(x, z));
                        }
                    if (!Worktop(_mine)) TopUp(1);
                }
                // the layouts with two counters, then with the one; then scattered with it; and where even that leaves the
                // kitchen without a fridge, sink or stove, those come first (the counter weighs less than any of them): the
                // layouts and the scatter again with the appliances alone, a counter beside them only if one still goes
                if (!tries.Any(Attempt))
                {
                    want = 1;
                    if (!tries.Any(Attempt))
                    {
                        Scatter(true);
                        if (!Full())
                        {
                            foreach (var m in _mine.ToList()) Remove(m);
                            want = 0;
                            if (!tries.Any(Attempt)) Scatter(false);
                            if (!Worktop(_mine)) TopUp(1);
                        }
                    }
                }
                // a house that eats in its kitchen (strawberry: "houses without a dining room require at table with at least
                // one chair in the kitchen"): the table, with a chair at it, where an island would stand -- and where it will
                // not go, the cook's extra counters and the dishwasher make way for it (the counter at the sink stays)
                if (EatsInKitchen(_res, _room))
                {
                    if (Dining(1) == null)
                    {
                        Trim();
                        if (Dining(1) != null) TopUp(MinPrep);
                    }
                }
                else
                {
                    // a big kitchen fills its floor (a 108 m2 one stood empty with every appliance in one corner): an
                    // island always from 54 m2, and a breakfast table as well from 72
                    if (_area >= 54f || (_area >= 30f && _rng.Chance(60))) Island();
                    if (_area >= 72f) Dining();
                }
                WindowsOverCounters();
                CounterTops();
                Clock();
            }

            const int MinPrep = 2;

            /// <summary>Plain counters (prep space) beside the run: first one beside the sink or the stove where the kitchen
            /// has none there (Worktop), then a Counter_0 next to the sink, stove, dishwasher, fridge or another counter, on
            /// the same wall line, until the kitchen has `want` of them. True when it has, one of them at the sink or
            /// stove.</summary>
            bool TopUp(int want)
            {
                int Prep() => _mine.Count(m => m.Name == "Counter_0");
                var cs = FurnitureCatalog.Sizes["Counter_0"];
                // the spots either side of a piece, on its wall line
                IEnumerable<(float X, float Z, float Yaw)> Sides(FurniturePlan p)
                {
                    var ps = FurnitureCatalog.Sizes[p.Name];
                    var (rx, rz) = RightOf(p.Yaw); var (fx, fz) = FrontOf(p.Yaw);
                    float back = ps.Y0 - cs.Y0;   // the counter's back on the same wall line as the piece's
                    foreach (float off in new[] { ps.X1 - cs.X0 + 0.04f, ps.X0 - cs.X1 - 0.04f })
                        if (Backed("Counter_0", p.X + rx * off + fx * back, p.Z + rz * off + fz * back, p.Yaw))   // (not on past its wall's end)
                            yield return (p.X + rx * off + fx * back, p.Z + rz * off + fz * back, p.Yaw);
                }
                if (!Worktop(_mine))
                    foreach (var p in _mine.Where(m => m.Name is "Counter_1" or "Oven_0" && m.On == null).OrderBy(m => m.Name == "Counter_1" ? 0 : 1).ToList())
                        if (Sides(p).Any(c => Put("Counter_0", new[] { c }) != null)) break;
                for (bool grew = true; grew && Prep() < want;)
                {
                    grew = false;
                    foreach (var p in _mine.Where(m => m.Name is "Counter_1" or "Oven_0" or "Disher_0" or "Fridge_0" or "Counter_0" && m.On == null).ToList())
                        foreach (var c in Sides(p))
                        {
                            if (Prep() >= want) return Worktop(_mine);
                            if (Put("Counter_0", new[] { c }) != null) grew = true;
                        }
                }
                // a run that fills its stretch of wall (doors at both ends) has nothing beside it: then a counter against
                // whatever wall is free, as near the sink and stove as there is one (within 6 m: across the room is not
                // counter space)
                var work = _mine.Where(m => m.Name is "Counter_1" or "Oven_0" or "Disher_0" or "Fridge_0").ToList();
                double Near(float x, float z) => work.Count == 0 ? 0 : -work.Min(m => MathF.Abs(m.X - x) + MathF.Abs(m.Z - z));
                while (Prep() < want && work.Count > 0
                       && Put("Counter_0", Against("Counter_0", Side.Back).Where(c => Near(c.X, c.Z) > -6.0), (x, z, _) => Near(x, z)) != null) { }
                return Prep() >= want && Worktop(_mine);
            }

            /// <summary>Take out what a kitchen can do without, to make room for its table: the dishwasher, and every plain
            /// counter but one beside the sink or stove.</summary>
            void Trim()
            {
                var keep = _mine.FirstOrDefault(c => c.Name == "Counter_0" && _mine.Any(h => h.Name is "Counter_1" or "Oven_0" && Beside(c, h)));
                foreach (var m in _mine.Where(m => m.On == null && (m.Name == "Disher_0" || (m.Name == "Counter_0" && m != keep))).ToList()) Remove(m);
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
            List<FurniturePlan> L(List<(int Nx, int Nz, float Face, float Lo, float Hi)> runs, bool freeFridge, bool corner = false)
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
                        // the corner cabinet in the corner first (strawberry: "add corner cabinets to the kitchen furniture
                        // pool"), both legs laid on from its sides; else the plain L, leg A's first piece in the corner
                        {
                            FurniturePlan cab = null;
                            if (corner && (cab = CornerCabinet(a, b)) == null) continue;   // (the next corner)
                            float off = CornerSide + 0.05f;
                            // (the cabinet IS the corner's counter: leg A starts at the sink beside it)
                            var legA = corner ? new List<string> { "Counter_1", "Disher_0" } : new List<string> { "Counter_0", "Counter_1", "Disher_0" };
                            if (!freeFridge) legA.Add("Fridge_0");
                            var placed = Leg(a, (endA < 0 ? a.Lo : a.Hi) - (corner ? endA * off : 0f), -endA, legA, corner ? 2 : 3);
                            if (placed == null) { if (cab != null) Remove(cab); continue; }
                            var cs = FurnitureCatalog.Sizes["Counter_0"];
                            float startB = (endB < 0 ? b.Lo : b.Hi) - endB * (corner ? off : cs.Y1 - cs.Y0 + 0.05f);
                            // (with the cabinet the corner is counter already: leg B needs only its stove, a counter after it
                            // where the wall goes on)
                            var legB = corner ? Leg(b, startB, -endB, new List<string> { "Oven_0", "Counter_0" }, 1)
                                              : Leg(b, startB, -endB, new List<string> { "Counter_0", "Oven_0", "Counter_0" }, 2);
                            var sink = placed.FirstOrDefault(m => m.Name == "Counter_1"); var oven = legB?.FirstOrDefault(m => m.Name == "Oven_0");
                            var fridge = placed.FirstOrDefault(m => m.Name == "Fridge_0");
                            if (legB != null && sink != null && oven != null && fridge == null && freeFridge)
                            {
                                // the fridge on a wall of its own, keeping the triangle
                                fridge = Put("Fridge_0", Against("Fridge_0", Side.Back).Where(c => Triangle(oven, sink, ("Fridge_0", c.X, c.Z, c.Yaw))), (x, z, _) => -MathF.Abs(D(x, z, sink) - 2.5f));
                                if (fridge != null) legB.Add(fridge);
                            }
                            if (legB != null && sink != null && oven != null && fridge != null && Triangle(fridge, sink, ("Oven_0", oven.X, oven.Z, oven.Yaw)))
                                return placed.Concat(legB).Concat(cab != null ? new[] { cab } : Array.Empty<FurniturePlan>()).ToList();
                            foreach (var m in (legB ?? new List<FurniturePlan>()).Concat(placed)) Remove(m);
                            if (cab != null) Remove(cab);
                        }
                    }
                return null;
            }

            const float CornerSide = 1.75f;
            /// <summary>The corner cabinet in the inside corner where runs a and b meet: its back corner in the room's, a back
            /// to each wall (its local -Y to one, -X to the other), the diagonal front to the room.</summary>
            FurniturePlan CornerCabinet((int Nx, int Nz, float Face, float Lo, float Hi) a, (int Nx, int Nz, float Face, float Lo, float Hi) b)
            {
                const string Name = "Corner_Cabinet_0_Solid";
                float cx = a.Nx != 0 ? a.Face : b.Face, cz = a.Nx != 0 ? b.Face : a.Face;
                float off = CornerSide * 0.5f + 0.03f;
                float x = cx + (a.Nx + b.Nx) * off, z = cz + (a.Nz + b.Nz) * off;
                for (int k = 0; k < 4; k++)
                {
                    float yaw = k * 90f;
                    var (fx, fz) = FrontOf(yaw); var (rx, rz) = RightOf(yaw);
                    // front into the room off one wall, right off the other: its back corner is then the room's
                    bool fits = (fx == a.Nx && fz == a.Nz && rx == b.Nx && rz == b.Nz) || (fx == b.Nx && fz == b.Nz && rx == a.Nx && rz == a.Nz);
                    if (fits) return Put(Name, new[] { (x, z, yaw) });
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
                    // (the run ends where its wall does: a fourth piece laid on past it stood over the opening beside it)
                    if (MathF.Min(o + rsign * sz.X0, o + rsign * sz.X1) < run.Lo - 0.01f || MathF.Max(o + rsign * sz.X0, o + rsign * sz.X1) > run.Hi + 0.01f) break;
                    var at = run.Nx != 0 ? (across, o, yaw) : (o, across, yaw);
                    if (!Backed(name, at.Item1, at.Item2, at.Item3)) break;
                    var m = Put(name, new[] { at });
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
                            // the island itself on clear floor (out of every door's and appliance's swing), and a 1 m working
                            // aisle round it to walls and furniture -- an aisle MAY hold an appliance's swing: that is where you
                            // stand to open the oven. (It was 1.1 m of fully clear floor, which stacked on the oven's swing and
                            // pushed every island 2 m off the run.)
                            var isl = (X0: MathF.Min(a.X0, b.X0), Z0: MathF.Min(a.Z0, b.Z0), X1: MathF.Max(a.X1, b.X1), Z1: MathF.Max(a.Z1, b.Z1));
                            var aisle = (isl.X0 - 1.0f, isl.Z0 - 1.0f, isl.X1 + 1.0f, isl.Z1 + 1.0f);
                            if (Clear(isl, 0f) && Clear(aisle, 0f) is var roomy
                                && (roomy || !_p.WallRects.Any(r => Overlaps(aisle, r)) && !_mine.Any(o => o.On == null && !o.Mounted && !o.Ceiling && Overlaps(aisle, o.Footprint))
                                             && _room.Rects.Any(r => aisle.Item1 >= r.X0 * Cell && aisle.Item2 >= r.Z0 * Cell && aisle.Item3 <= r.X1 * Cell && aisle.Item4 <= r.Z1 * Cell)))
                                cands.Add((x, z, yaw));
                        }
                // beside the kitchen's working run, not in the middle of the floor (strawberry: "force the island counter
                // to be in a more smarter spot, closer to the kitchen"): nearest the appliances and counters already
                // placed, lying along the nearest of them, across the 1 m working aisle checked above
                var work = _mine.Where(m => !m.Ceiling && (m.Name.StartsWith("Counter") || m.Name is "Fridge_0" or "Oven_0" or "Disher_0")).ToList();
                (float Gap, int Cross, float Centre) Score((float X, float Z, float Yaw) c)
                {
                    var (fx, fz) = FrontOf(c.Yaw);
                    var a = FurnitureCatalog.Footprint("Counter_0", c.X, c.Z, c.Yaw);
                    var b = FurnitureCatalog.Footprint("Counter_0", c.X - fx * (cs.Y0 * -2f), c.Z - fz * (cs.Y0 * -2f), c.Yaw + 180f);
                    var isl = (X0: MathF.Min(a.X0, b.X0), Z0: MathF.Min(a.Z0, b.Z0), X1: MathF.Max(a.X1, b.X1), Z1: MathF.Max(a.Z1, b.Z1));
                    float centre = (c.X - cx) * (c.X - cx) + (c.Z - cz) * (c.Z - cz);
                    if (work.Count == 0) return (0f, 0, centre);
                    var near = work.Select(m =>
                    {
                        var f = m.Footprint;
                        float dx = MathF.Max(0f, MathF.Max(f.X0 - isl.X1, isl.X0 - f.X1)), dz = MathF.Max(0f, MathF.Max(f.Z0 - isl.Z1, isl.Z0 - f.Z1));
                        return (d: MathF.Sqrt(dx * dx + dz * dz), m);
                    }).OrderBy(t => t.d).First();
                    // two counters back to back are 1.5 m wide but 2.2 m deep, so the island LIES ALONG the run when its
                    // counters face square to the run's (yaw 90 off), not the same way
                    int cross = (int)MathF.Round(((c.Yaw - near.m.Yaw) % 180f + 180f) % 180f) == 90 ? 0 : 1;
                    return (MathF.Round(near.d * 4f) / 4f, cross, centre);   // to the quarter metre, then parallel, then central
                }
                // lying along the run beats a few centimetres: among the spots within 0.75 m of the closest, one parallel
                // to the run first (an island pointing at the run reads as a barrier, not a workstation)
                var scored = cands.Select(c => (c, sc: Score(c))).ToList();
                float best = scored.Count > 0 ? scored.Min(t => t.sc.Gap) : 0f;
                foreach (var (x, z, yaw) in scored.OrderBy(t => t.sc.Gap <= best + 0.75f ? 0 : 1).ThenBy(t => t.sc.Cross)
                                                  .ThenBy(t => t.sc.Gap).ThenBy(t => t.sc.Centre).Select(t => t.c).Take(20))
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
                            if (!spots.All(sp => Clear(FurnitureCatalog.Footprint(sp.N, sp.P.Item1, sp.P.Item2, sp.P.Item3), FurnitureCatalog.Sizes[sp.N].H)
                                                 && Backed(sp.N, sp.P.Item1, sp.P.Item2, sp.P.Item3))) continue;
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
                                    var at = At(along, "Counter_0");
                                    // (not on past the wall's end: onto a partition 0.1 m thinner, it stood off its face)
                                    if (!Backed("Counter_0", at.Item1, at.Item2, at.Item3)) break;
                                    var m = Put("Counter_0", new[] { at });
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
                // a home's has a tub -- a house's or a flat's; a shop's or a factory's washroom just the basin
                bool home = _res.Kind is BuildingKind.Residential or BuildingKind.Apartment;
                if (home)
                    Put("Tub_0", Against("Tub_0", Side.Left).Concat(Against("Tub_0", Side.Right)), (x, z, yaw) => (Corner("Tub_0", (x, z, yaw)) ? 10 : 0) + NearestDoor(x, z));
                Put("Counter_1", Against("Counter_1", Side.Back), (x, z, yaw) => -NearestDoor(x, z));
                if (_area >= 14f && home) Put("Washer_0", Against("Washer_0", Side.Back), (x, z, yaw) => NearestDoor(x, z));
                WindowsOverCounters();
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
                    On(desk, "Lamp_0", 0.55f, -0.45f, (yaw + 180f) % 360f);    // a desk lamp at the chair's right hand
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
                    foreach (var b in held.Select(c => c.Item2 - _j0).Where(b => b >= 0 && b < _nj).Distinct()) Recount(b);
                }
                _mustReach.Remove(m);
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
                void Row(float b)
                {
                    float a = a0 + Edge + len * 0.5f;
                    for (int k = 0; a + len * 0.5f <= a1 - Edge + 1e-3f; k++)
                    {
                        Put("Shelf_1", new[] { longX ? (a, b, yaw) : (b, a, yaw) });
                        a += len + (k % 2 == 1 ? Aisle : 0f);
                    }
                }
                for (float b = b0 + Edge + deep * 0.5f; b + deep * 0.5f <= b1 - Edge + 1e-3f; b += deep + Aisle) Row(b);
                // A shallow shop can have the coolers' doors along one long wall and the back rooms' doors along the other,
                // their swings taking both rows that start from the edge and leaving the middle clear: there, fewer rows,
                // centred across the shop -- one, down its middle, at the least.
                int fit = (int)((b1 - b0 - 2 * Edge + Aisle) / (deep + Aisle));
                for (int n = fit; n >= 1 && !_mine.Any(m => m.Name == "Shelf_1"); n--)
                {
                    float span = n * deep + (n - 1) * Aisle, mid = (b0 + b1) * 0.5f;
                    for (int q = 0; q < n; q++) Row(mid - span * 0.5f + deep * 0.5f + q * (deep + Aisle));
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

            /// <summary>A business's staff room: a kitchenette -- the fridge and the sink side by side on a wall -- a table with
            /// chairs, and in a big one a couch.</summary>
            void BreakRoom()
            {
                var runs = new List<(int Nx, int Nz, float Face, float Lo, float Hi)>();
                foreach (var (nx, nz) in Fronts)
                    foreach (var (face, lo, hi) in Faces(nx, nz)) runs.Add((nx, nz, face, lo, hi));
                runs = _rng.Keyed(runs).OrderByDescending(x => x.Item.Hi - x.Item.Lo).ThenBy(x => x.Key).Select(x => x.Item).ToList();
                if (Row(runs, new[] { "Fridge_0", "Counter_1" }) == null)
                {
                    Put("Counter_1", Against("Counter_1", Side.Back));
                    Put("Fridge_0", Against("Fridge_0", Side.Back));
                }
                Dining();
                if (_area >= 30f) Put("Couch_1", Against("Couch_1", Side.Back), (x, z, yaw) => NearestDoor(x, z));
                WindowsOverCounters();
            }

            /// <summary>A home's coat stand by the way in (strawberry: "explore more props to place ie coat rack"): in a corner
            /// within 6 m of the front or back door of the room it opens into (a flat's: the room its door opens into off the
            /// corridor), and in a bedroom by its door -- where a corner is left once the room's own pieces are in.</summary>
            void Coats()
            {
                if (_res.Kind is not (BuildingKind.Residential or BuildingKind.Apartment) || _mine.Any(m => m.Name == "Coat_0")) return;
                var ways = new List<(float X, float Z)>();
                // (a block's own front and back doors open into its shared corridor, which is nobody's home)
                if (_res.Kind == BuildingKind.Residential)
                    foreach (var d in new[] { _res.Front, _res.Back })
                        if (d != null && d.A == _room && _room.Floor == 0) ways.Add(d.Middle);
                if ((_room.Unit >= 0 && _room.Entry) || _room.Kind == RoomKind.Bedroom) ways.AddRange(Doors());
                if (ways.Count == 0) return;
                double Near(float x, float z) => ways.Min(w => Math.Sqrt((w.X - x) * (w.X - x) + (w.Z - z) * (w.Z - z)));
                Put("Coat_0", Against("Coat_0", Side.Back).Where(p => Corner("Coat_0", p) && Near(p.X, p.Z) <= 6.0), (x, z, _) => -Near(x, z));
            }

            /// <summary>Small appliances on the kitchen's plain counters (a microwave, a coffee machine or a toaster), one to a
            /// counter, never on the last bare one: somewhere is left to work.</summary>
            void CounterTops()
            {
                var tops = _mine.Where(m => m.Name == "Counter_0" && m.On == null && !_res.Furniture.Any(t => t.On == m)).ToList();
                // the one at the sink or stove is the cook's: the others first
                tops = tops.OrderBy(c => _mine.Any(h => h.Name is "Counter_1" or "Oven_0" && Beside(c, h)) ? 1 : 0).ToList();
                var small = new[] { "Microwave_0", _rng.Chance(50) ? "Coffee_0" : "Toaster_0" };
                for (int k = 0; k < small.Length && k < tops.Count - 1; k++)
                {
                    // its back 5 cm off the counter's back edge, its front to the room as the counter's
                    var cs = FurnitureCatalog.Sizes["Counter_0"]; var ss = FurnitureCatalog.Sizes[small[k]];
                    On(tops[k], small[k], 0f, cs.Y0 + 0.05f - ss.Y0, tops[k].Yaw);
                }
            }

            /// <summary>A clock on a kitchen's or living room's wall, high, over nothing taller than a counter: on a stretch
            /// with no window or door (Hang), off the stairs' wall, nearest the middle of the room -- and, as the wall décor,
            /// off a fireplace's wall and DecorWindowGap from any window (HangRefused: refused once ranked, so the furniture's
            /// draws are the ones they were and every room after this one is furnished as it was).</summary>
            void Clock()
            {
                var (cx, cz) = Centre();
                // what may stand under it is judged as it was when the clock hung at ClockClearedAt: the spots that pass are
                // ranked on the Furnisher's shared draws, one each, so a looser test -- the higher clock clears a fridge -- would
                // hand every later room different draws. It only ever keeps off more than it needs to.
                float bottom = ClockClearedAt - FurnitureCatalog.Sizes["Clock_0"].H;
                Hang("Clock_0", Against("Clock_0", Side.Back).Where(p =>
                {
                    var fp = FurnitureCatalog.Footprint("Clock_0", p.X, p.Z, p.Yaw);
                    var below = (fp.X0 - 0.2f, fp.Z0 - 0.2f, fp.X1 + 0.2f, fp.Z1 + 0.2f);
                    return !ByStairs("Clock_0", p) && !_mine.Any(o => o.On == null && !o.Mounted && !o.Ceiling && FurnitureCatalog.Sizes[o.Name].H > bottom - 0.1f && Overlaps(below, o.Footprint));
                }), (x, z, _) => -Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)), ClockHeight, refuse: (x, z, yaw) => HangRefused("Clock_0", x, z, yaw));
            }
            /// <summary>A wall clock's middle stands this high (strawberry, round 12: "hang them much higher on the walls"): its
            /// 1 m face's top 0.15 m under the lowest ceiling a room has -- a storey under another, whose floor's slab comes
            /// down to OpeningTop, 3.75 m -- so 3.1, its face 2.6-3.6 m. It was 2.5, its middle at a standing player's eye.</summary>
            const float ClockHeight = OpeningTop - 0.15f - 0.5f;
            /// <summary>The height the clock's clearance over what stands under it is judged at (Clock): where it hung before.</summary>
            const float ClockClearedAt = 2.5f;

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
