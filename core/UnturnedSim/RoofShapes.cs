using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>What shape a roof is. Presentation of a SPEC, not a new kind of geometry -- every roof below
    /// comes out as ordinary WallSurfaces, the same way stairs are treads and a room is four walls.</summary>
    public enum RoofKind
    {
        /// <summary>One slab lying on the wall heads.</summary>
        Flat,
        /// <summary>Two slopes meeting at a ridge; the walls across the ridge close it with gable ends.</summary>
        Gable,
        /// <summary>Four slopes, no gable ends -- the two ends are triangles and the two sides trapezoids
        /// whose top edge is the ridge. A square footprint degenerates to a pyramid, correctly.</summary>
        Hip,
        /// <summary>One slope, rising from its eave on one side to its high edge on the side named by
        /// RoofSpec.High -- a lean-to against a taller wall, or a shed roof of its own. The walls along the slope
        /// are closed by a band and a right triangle each, the high wall by a band (ShedInfill).</summary>
        Shed,
    }

    /// <summary>A side of a roof's footprint: north is +Z, east +X.</summary>
    public enum RoofSide { North, East, South, West }

    /// <summary>Everything a roof IS. A roof is fully determined by this, which is the point: modifying one
    /// means changing a number here and rebuilding, rather than hunting down the surfaces it turned into.
    ///
    /// strawberry_cow asked for roof types and for the ability to modify placed roofs, and those are one job:
    /// roofs were emitted straight into surfaces at draw time, so nothing recorded that six surfaces and two
    /// raised walls were ONE roof, and there was nothing to modify. Adding shapes on top of that would have
    /// multiplied the thing that was already wrong.</summary>
    public struct RoofSpec
    {
        public RoofKind Kind;
        /// <summary>The EAVE footprint -- already grown past the walls by whatever overhang applies. The
        /// caller owns that decision because it differs by kind (retail flat roofs sit flush, pitched ones
        /// overhang) and this module should not be a second place that knows it.</summary>
        public float MinX, MaxX, MinZ, MaxZ;
        /// <summary>Wall head height: where the eaves sit.</summary>
        public float TopY;
        public float PitchDeg;
        public float Thickness;
        public int Material;
        public int Texel;
        /// <summary>Shed only: the side the roof rises to.</summary>
        public RoofSide High;

        public float SpanX => MaxX - MinX;
        public float SpanZ => MaxZ - MinZ;
        /// <summary>A roof's ridge runs the LONG way, as a roof does -- except a shed's, whose high edge runs along
        /// the side it rises to.</summary>
        public bool RidgeAlongX => Kind == RoofKind.Shed ? High is RoofSide.North or RoofSide.South : SpanX >= SpanZ;
        /// <summary>The horizontal run of one slope: half the SHORT span, or a shed's whole span across its slope.</summary>
        public float HalfRun => Kind == RoofKind.Shed ? (RidgeAlongX ? SpanZ : SpanX) : (RidgeAlongX ? SpanZ : SpanX) * 0.5f;
        public float Rise => Kind == RoofKind.Flat ? 0f
                           : HalfRun * MathF.Tan(MathF.PI / 180f * Clamped);
        /// <summary>Sloped length from eave to ridge -- the surface's own height up the slope.</summary>
        public float Slope => HalfRun / MathF.Cos(MathF.PI / 180f * Clamped);
        public float Clamped => Math.Clamp(PitchDeg, MinPitch, MaxPitch);

        public const float MinPitch = 1f;
        /// <summary>Past about 70 the slope length runs away from the footprint and the "roof" is a spire.</summary>
        public const float MaxPitch = 70f;
    }

    /// <summary>One surface of a roof, in exactly the arguments the editor spawns a WallSurface with. Keeping
    /// the output in spawn arguments rather than in a shape of its own is what lets the geometry be decided
    /// here, engine-free and under L0, while the editor stays a thin caller.</summary>
    public struct RoofPlane
    {
        public float X, Y, Z;
        public float YawDeg;
        /// <summary>Node pitch: -90 lies flat, and the roof's pitch is added to that.</summary>
        public float PitchDeg;
        public float Length, Height;
        /// <summary>Trapezoid edges. A hip's sides inset to the ridge and its ends inset to a point.</summary>
        public float InsetL0, InsetL1, InsetR0, InsetR1;
    }

    public static class RoofShapes
    {
        /// <summary>The surfaces this roof is made of.
        ///
        /// Yaw convention matches the editor's: 0 runs +X, 90 runs -Z, 180 runs -X, 270 runs +Z. A pitched
        /// plane's local +X stays HORIZONTAL along the eave, which is why the trapezoid insets below are
        /// plain horizontal distances and not measured up the slope.</summary>
        public static List<RoofPlane> Planes(RoofSpec s)
        {
            var planes = new List<RoofPlane>();
            if (s.SpanX <= 0.01f || s.SpanZ <= 0.01f) return planes;

            if (s.Kind == RoofKind.Flat)
            {
                // Lying on the wall heads, thickness centred, so its walking surface is the head itself.
                planes.Add(new RoofPlane
                {
                    X = s.MinX, Y = s.TopY + s.Thickness * 0.5f, Z = s.MaxZ,
                    YawDeg = 0f, PitchDeg = -90f, Length = s.SpanX, Height = s.SpanZ,
                });
                return planes;
            }

            float pitchNode = s.Clamped - 90f;
            float slope = s.Slope;
            float half = s.HalfRun;

            // A shed is one of a gable's two slopes, run the whole span: its eave on the low side, rising to High.
            if (s.Kind == RoofKind.Shed)
            {
                planes.Add(s.High switch
                {
                    RoofSide.North => Slope1(s.MaxX, s.TopY, s.MinZ, 180f, s.SpanX, slope, pitchNode, 0f),
                    RoofSide.South => Slope1(s.MinX, s.TopY, s.MaxZ, 0f, s.SpanX, slope, pitchNode, 0f),
                    RoofSide.East => Slope1(s.MinX, s.TopY, s.MinZ, -90f, s.SpanZ, slope, pitchNode, 0f),
                    _ => Slope1(s.MaxX, s.TopY, s.MaxZ, 90f, s.SpanZ, slope, pitchNode, 0f),
                });
                return planes;
            }

            // The two long slopes. For a gable they are the whole roof; for a hip they are trapezoids whose
            // top edge is the ridge, shortened by one run at each end.
            float hipInset = s.Kind == RoofKind.Hip ? half : 0f;
            if (s.RidgeAlongX)
            {
                planes.Add(Slope1(s.MinX, s.TopY, s.MaxZ, 0f, s.SpanX, slope, pitchNode, hipInset));
                planes.Add(Slope1(s.MaxX, s.TopY, s.MinZ, 180f, s.SpanX, slope, pitchNode, hipInset));
            }
            else
            {
                planes.Add(Slope1(s.MinX, s.TopY, s.MinZ, -90f, s.SpanZ, slope, pitchNode, hipInset));
                planes.Add(Slope1(s.MaxX, s.TopY, s.MaxZ, 90f, s.SpanZ, slope, pitchNode, hipInset));
            }

            if (s.Kind != RoofKind.Hip) return planes;

            // The two hipped ends: triangles, inset from both sides to a point at the ridge end. They rise at
            // the same pitch over the same run as the sides, so they share the slope length exactly -- which
            // is what makes the four planes meet along the ridge instead of near it.
            if (s.RidgeAlongX)
            {
                planes.Add(Triangle(s.MinX, s.TopY, s.MinZ, -90f, s.SpanZ, slope, pitchNode));
                planes.Add(Triangle(s.MaxX, s.TopY, s.MaxZ, 90f, s.SpanZ, slope, pitchNode));
            }
            else
            {
                planes.Add(Triangle(s.MinX, s.TopY, s.MaxZ, 0f, s.SpanX, slope, pitchNode));
                planes.Add(Triangle(s.MaxX, s.TopY, s.MinZ, 180f, s.SpanX, slope, pitchNode));
            }
            return planes;
        }

        static RoofPlane Slope1(float x, float y, float z, float yaw, float len, float slope,
                                float pitchNode, float inset)
            => new RoofPlane
            {
                X = x, Y = y, Z = z, YawDeg = yaw, Length = len, Height = slope, PitchDeg = pitchNode,
                InsetL1 = inset, InsetR1 = inset,
            };

        static RoofPlane Triangle(float x, float y, float z, float yaw, float len, float slope, float pitchNode)
            => new RoofPlane
            {
                X = x, Y = y, Z = z, YawDeg = yaw, Length = len, Height = slope, PitchDeg = pitchNode,
                InsetL1 = len * 0.5f, InsetR1 = len * 0.5f,
            };

        /// <summary>How high a gable end has to rise on a wall of this length under this roof.
        ///
        /// It is set by the WALL's own half-length, NOT by the roof footprint's -- those differ the moment
        /// the roof overhangs, and using the footprint's rise made the triangle steeper than the roof above
        /// it: measured on a 9 m wall at 20 deg with a 0.75 m overhang, 3.01 deg steeper, meeting the roof
        /// only at the apex and opening a 0.27 m wedge of daylight down both sloped edges.</summary>
        /// PURE GEOMETRY -- it does NOT re-check the roof kind. WallGetsGable is the one gate, and asking
        /// again here was a second and third copy of it: a mutation that made a hip claim gable ends stayed
        /// green, because these two quietly returned zero and cancelled it out. Three statements of one rule
        /// hid the bug AND hid the test's blindness to it.
        public static float GableRiseForWall(RoofSpec s, float wallLength)
            => wallLength * 0.5f * MathF.Tan(MathF.PI / 180f * s.Clamped);

        /// <summary>Straight band between a wall's top and its gable triangle, when the roof reaches further
        /// out than the wall does. Zero when the roof does not overhang that wall. Gated by WallGetsGable,
        /// like the rise above.</summary>
        public static float GableBandForWall(RoofSpec s, float wallLength)
            => MathF.Max(0f, s.Rise - GableRiseForWall(s, wallLength));

        /// <summary>THE GATE. Does a wall running this way get a gable end under this roof? Only walls ACROSS
        /// the ridge do -- putting a peak on all four is the classic wrong-looking roof -- and a hip has none
        /// at all, because its own end planes close it. Every caller of the two above goes through here
        /// first; they do not repeat this test.</summary>
        public static bool WallGetsGable(RoofSpec s, bool wallRunsAlongX)
            => s.Kind == RoofKind.Gable && wallRunsAlongX != s.RidgeAlongX;

        /// <summary>What closes a shed roof against one wall (its mid-plane from (x0, z0) to (x1, z1)): a straight band
        /// on the wall's head, Band high, and on that a right triangle rising by Triangle to the wall's high end --
        /// the end at u = Length when HighAtEnd, else at u = 0. Both follow the roof's underside exactly: the height
        /// of the slope over a point is its distance from the eave times the pitch's tangent, so a wall across the
        /// slope gets a band of that height and no triangle, and a wall along it gets the height at its low end as a
        /// band and the rest as a triangle. Zero for a wall outside the footprint or a roof that is not a shed.</summary>
        public static (float Band, float Triangle, bool HighAtEnd) ShedInfill(RoofSpec s, float x0, float z0, float x1, float z1)
        {
            if (s.Kind != RoofKind.Shed) return (0f, 0f, false);
            float tan = MathF.Tan(MathF.PI / 180f * s.Clamped);
            float Up(float x, float z) => s.High switch
            {
                RoofSide.North => z - s.MinZ, RoofSide.South => s.MaxZ - z,
                RoofSide.East => x - s.MinX, _ => s.MaxX - x,
            } * tan;
            bool In(float x, float z) => x >= s.MinX - 0.01f && x <= s.MaxX + 0.01f && z >= s.MinZ - 0.01f && z <= s.MaxZ + 0.01f;
            if (!In(x0, z0) || !In(x1, z1)) return (0f, 0f, false);   // not under this roof
            float h0 = Up(x0, z0), h1 = Up(x1, z1);
            return (MathF.Min(h0, h1), MathF.Abs(h1 - h0), h1 > h0);
        }

        /// <summary>The two faces of one solid of a PITCHED roof surface -- `front` at +halfThick, `back` at -halfThick,
        /// both in the surface's own (u, v) -- with every edge but the eave cut PLUMB: along the vertical plane through
        /// that edge of the mid-plane polygon, rather than square to the slab.
        ///
        /// strawberry: "the top of roofs where the gables meet has a small gap". Each slope was a slab cut square to
        /// itself, so at a ridge the two cuts crossed at the mid-plane and nowhere else: above it a V open to the sky,
        /// t/cos(pitch) deep and 2t sin(pitch) across the top -- 266 mm by 171 mm on a 0.5 m slab at 20 degrees -- along
        /// every ridge and every hip; below it the two slabs ran into each other. Two slopes of one pitch meet on the
        /// vertical plane through their shared mid-plane edge, which is where a carpenter cuts a rafter at the ridge (the
        /// plumb cut) and at a hip or a valley: cut there, both faces of both slabs end on the same line, and the slabs
        /// neither part nor overlap. The same cut stands a shed's high edge flush against the wall it leans on.
        ///
        /// The EAVE stays square, on purpose: it meets nothing, and squaring it is what it has always looked like. So
        /// does any other level edge below the top -- the head or sill of an opening -- because the slab on its far side
        /// is cut square too. A square edge is tested where it is; a plumb one where the point straight below or above
        /// it crosses the mid-plane: a point at depth w lies plumb over (u - w*shiftU, v - w*shiftV) -- the shift being
        /// the surface's own vertical, (up.u, up.v) / up.w in its frame, which the caller reads off the transform it
        /// actually built rather than having it re-derived here from a sign convention (a roof read off a retail mesh
        /// leans the other way from a generated one).
        ///
        /// `mid` is the mid-plane polygon (the solid's rectangle, clipped to the surface's trapezoid), convex. Only its
        /// real edges cut: the rectangle side a hip's cut line meets at the eave corner is a vertex, not an edge, so the
        /// face may run past it -- out to where the hip's outer line actually is.</summary>
        public static void PlumbFaces(IReadOnlyList<(float U, float V)> mid, float height, float halfThick,
                                      float shiftU, float shiftV,
                                      List<(float U, float V)> front, List<(float U, float V)> back)
        {
            front.Clear(); back.Clear();
            if (mid == null || mid.Count < 3) return;
            float cu = 0f, cv = 0f, u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            foreach (var (u, v) in mid)
            {
                cu += u; cv += v;
                u0 = MathF.Min(u0, u); u1 = MathF.Max(u1, u); v0 = MathF.Min(v0, v); v1 = MathF.Max(v1, v);
            }
            cu /= mid.Count; cv /= mid.Count;
            // Far enough out that nothing but the edges below can bound the face.
            float m = (MathF.Abs(shiftU) + MathF.Abs(shiftV)) * halfThick + 1f;
            foreach (var (face, w) in new[] { (front, halfThick), (back, -halfThick) })
            {
                var poly = new List<(float U, float V)> { (u0 - m, v0 - m), (u1 + m, v0 - m), (u1 + m, v1 + m), (u0 - m, v1 + m) };
                for (int i = 0; i < mid.Count && poly.Count > 0; i++)
                {
                    var a = mid[i];
                    var b = mid[(i + 1) % mid.Count];
                    float eu = b.U - a.U, ev = b.V - a.V;
                    if (eu * eu + ev * ev < 1e-10f) continue;   // a corner the clip doubled, not an edge
                    bool level = MathF.Abs(ev) < 1e-4f;
                    bool square = level && MathF.Abs(a.V - height) > 1e-3f;
                    float side = MathF.Sign(eu * (cv - a.V) - ev * (cu - a.U));
                    float du = square ? 0f : w * shiftU, dv = square ? 0f : w * shiftV;
                    poly = ClipHalfPlane(poly, p => side * (eu * (p.V - dv - a.V) - ev * (p.U - du - a.U)));
                }
                face.AddRange(poly);
            }
        }

        /// <summary>One solid of a surface (its rectangle u0..u1 by v0..v1) clipped to the surface's trapezoid: keep u from the
        /// left cut line to the right one, each straight from its inset at the base (…0) to its inset at the top (…1) of a
        /// surface `height` tall. Convex. The one definition of a tapered surface's outline: WallSurface meshes and collides
        /// it, and an inset that puts a cut line outside the rectangle leaves the rectangle's side as the edge there.</summary>
        public static List<(float U, float V)> TaperPolygon(float u0, float v0, float u1, float v1, float length, float height,
                                                           float insetL0, float insetL1, float insetR0, float insetR1)
        {
            float At(float a, float b, float v) => a + (b - a) * (height > WallOpenings.Eps ? v / height : 0f);
            var poly = new List<(float U, float V)> { (u0, v0), (u1, v0), (u1, v1), (u0, v1) };
            poly = ClipHalfPlane(poly, p => p.U - At(insetL0, insetL1, p.V));
            return ClipHalfPlane(poly, p => length - At(insetR0, insetR1, p.V) - p.U);
        }

        static List<(float U, float V)> ClipHalfPlane(List<(float U, float V)> poly, Func<(float U, float V), float> keep)
        {
            var outp = new List<(float U, float V)>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                float da = keep(a), db = keep(b);
                if (da >= 0f) outp.Add(a);
                if ((da >= 0f) != (db >= 0f))
                {
                    float f = da / (da - db);
                    if (float.IsFinite(f))
                    {
                        f = Math.Clamp(f, 0f, 1f);
                        outp.Add((a.U + (b.U - a.U) * f, a.V + (b.V - a.V) * f));
                    }
                }
            }
            return outp;
        }

        /// <summary>Height of the ridge above the eaves. Zero for a flat roof.</summary>
        public static float RidgeHeight(RoofSpec s) => s.Kind == RoofKind.Flat ? 0f : s.Rise;

        /// <summary>How far a flat roof's parapet stands over the roof's surface. Measured off the retail flat-roofed
        /// buildings (their meshes read as BuildingImport reads them): 1.00 m on Cafe_0/1, Clothes_0/1, Diner_0, Hardware_0,
        /// Guns_0, Bank_0, Fire_0, Medic_0/2, Mall_0, Apartment_1/2, Police_1 and the top roofs of Office_0..3 -- the
        /// exceptions are false fronts (Grocer_0 2.01 on three sides, Diner_1 2.13, Post_0 1.5, part of Medic_1 2.0), and
        /// Diner_2, Mechanic_0 and Apartment_3 have none.</summary>
        public const float ParapetHeight = 1.0f;

        /// <summary>Stand a parapet round every flat roof in `walls`, the way retail builds one (strawberry: "in retail
        /// unturned, flat roof buildings have a short border wall stub around the 'roof'").
        ///
        /// Retail's parapet is the outside wall carried on up: on every model measured its outer face is the facade's own
        /// plane from the footing to the parapet's top, its inner face the wall's own inner plane, so it is exactly the
        /// wall's thickness (0.50 there), flush on both sides, painted the wall's texel inside and out and on its flat top
        /// -- no coping, no cap, no inset -- and the roof runs between the inner faces, ParapetHeight under its top. So
        /// here: on each wall whose head a flat roof sits on, with its outer face on that roof's edge, a wall of its
        /// thickness and paint from its head to ParapetHeight over the roof, along the stretch of it under that roof; and
        /// the roof slab gives the parapets their strip, so that the two do not fill the same space (the slab's edge and
        /// the parapet's face would fight in one plane) and the facade reads one colour to the top, as retail's does.
        ///
        /// Every tier of a stepped building is its own slab on its own walls, so each gets one; where a lower roof meets
        /// the wall of a taller part there is no wall under that edge whose head the roof sits on -- the taller wall
        /// stands on it, and runs on up -- so that edge gets none. Engine-free and placement-only: nothing here moves a
        /// wall, a room, a door or a piece of furniture; it adds parapets and shrinks the slabs they stand in. Returns the
        /// parapets, laid along the roof's edges and no further: where one meets a taller part, joining it there is the
        /// caller's corner pass.</summary>
        public static List<WallPlan> AddParapets(List<WallPlan> walls)
        {
            var parapets = new List<WallPlan>();
            var replaced = new List<(WallPlan Slab, List<WallPlan> With)>();
            foreach (var s in walls)
            {
                if (s.Kind != SurfaceKind.Roof || MathF.Abs(s.Pitch + 90f) > 0.01f || MathF.Abs(s.Yaw) > 0.01f || s.Openings.Count > 0) continue;
                float x0 = s.X, x1 = s.X + s.Length, z0 = s.Z - s.Height, z1 = s.Z;
                float bottom = s.Y - s.Thickness * 0.5f, top = s.Y + s.Thickness * 0.5f;
                var strips = new List<(float X0, float Z0, float X1, float Z1)>();
                foreach (var w in walls)
                {
                    if (w.Kind != SurfaceKind.Wall || w.Pitch != 0f || MathF.Abs(w.Y + w.Height - bottom) > 0.01f) continue;
                    var (c, sn) = PlanTrig.CosSin(w.Yaw);
                    float dx = c, dz = -sn, h = w.Thickness * 0.5f;
                    bool alongX = MathF.Abs(dz) < 1e-3f;
                    if (!alongX && MathF.Abs(dx) > 1e-3f) continue;               // not square to the roof
                    float d = alongX ? dx : dz, a = alongX ? w.X : w.Z, b = a + d * w.Length;
                    float lo = MathF.Max(MathF.Min(a, b), alongX ? x0 : z0), hi = MathF.Min(MathF.Max(a, b), alongX ? x1 : z1);
                    if (hi - lo < 0.05f) continue;                                 // not under this roof
                    // its outer face on one of the roof's edges, the wall itself inside: an outside wall the roof sits on
                    float line = alongX ? w.Z : w.X, e0 = alongX ? z0 : x0, e1 = alongX ? z1 : x1;
                    if (MathF.Abs(line - (e0 + h)) > 0.01f && MathF.Abs(line - (e1 - h)) > 0.01f) continue;
                    float u = MathF.Min((lo - a) / d, (hi - a) / d);
                    parapets.Add(new WallPlan
                    {
                        X = w.X + dx * u, Y = bottom, Z = w.Z + dz * u, Yaw = w.Yaw, Length = hi - lo,
                        Height = top - bottom + ParapetHeight, Thickness = w.Thickness, Kind = SurfaceKind.Wall,
                        Material = w.Material, Texel = w.Texel,
                    });
                    strips.Add(alongX ? (lo, line - h, hi, line + h) : (line - h, lo, line + h, hi));
                }
                if (strips.Count > 0) replaced.Add((s, Less(s, strips)));
            }
            foreach (var (slab, with) in replaced)
            {
                int i = walls.IndexOf(slab);
                walls.RemoveAt(i);
                walls.InsertRange(i, with);
            }
            walls.AddRange(parapets);
            return parapets;
        }

        /// <summary>A flat slab less some rectangles of it, as rectangles: the grid every cut's edges make, the cells no cut
        /// covers, run together along X in each band and down Z where a band's run carries on from the one before.</summary>
        static List<WallPlan> Less(WallPlan s, List<(float X0, float Z0, float X1, float Z1)> cut)
        {
            float x0 = s.X, x1 = s.X + s.Length, z0 = s.Z - s.Height, z1 = s.Z;
            static List<float> Breaks(float lo, float hi, IEnumerable<float> at)
            {
                var all = at.Where(v => v > lo && v < hi).Append(lo).Append(hi).OrderBy(v => v).ToList();
                var outp = new List<float>();
                foreach (var v in all) if (outp.Count == 0 || v - outp[^1] > 1e-3f) outp.Add(v);
                outp[^1] = hi;
                return outp;
            }
            var xs = Breaks(x0, x1, cut.SelectMany(q => new[] { q.X0, q.X1 }));
            var zs = Breaks(z0, z1, cut.SelectMany(q => new[] { q.Z0, q.Z1 }));
            var rects = new List<(float X0, float Z0, float X1, float Z1)>();
            var open = new List<(float X0, float X1, float Z0)>();
            for (int j = 0; j + 1 < zs.Count; j++)
            {
                float zm = (zs[j] + zs[j + 1]) * 0.5f;
                var runs = new List<(float X0, float X1)>();
                for (int i = 0; i + 1 < xs.Count; i++)
                {
                    float xm = (xs[i] + xs[i + 1]) * 0.5f;
                    if (cut.Any(q => xm > q.X0 && xm < q.X1 && zm > q.Z0 && zm < q.Z1)) continue;
                    if (runs.Count > 0 && runs[^1].X1 == xs[i]) runs[^1] = (runs[^1].X0, xs[i + 1]);
                    else runs.Add((xs[i], xs[i + 1]));
                }
                var next = new List<(float X0, float X1, float Z0)>();
                foreach (var o in open)
                    if (runs.Contains((o.X0, o.X1))) next.Add(o);
                    else rects.Add((o.X0, o.Z0, o.X1, zs[j]));
                foreach (var r in runs)
                    if (!open.Any(o => o.X0 == r.X0 && o.X1 == r.X1)) next.Add((r.X0, r.X1, zs[j]));
                open = next;
            }
            foreach (var o in open) rects.Add((o.X0, o.Z0, o.X1, zs[^1]));
            return rects.Select(r => new WallPlan
            {
                X = r.X0, Y = s.Y, Z = r.Z1, Yaw = 0f, Pitch = -90f, Length = r.X1 - r.X0, Height = r.Z1 - r.Z0,
                Thickness = s.Thickness, Kind = s.Kind, Material = s.Material, Texel = s.Texel,
                MaterialBack = s.MaterialBack, TexelBack = s.TexelBack,
            }).ToList();
        }

        /// <summary>Length of the ridge line. A hip shortens it by one run at each end, so a square hip has
        /// no ridge at all and is a pyramid -- which is correct, not a degenerate case to guard against.</summary>
        public static float RidgeLength(RoofSpec s)
        {
            float along = s.RidgeAlongX ? s.SpanX : s.SpanZ;
            return s.Kind == RoofKind.Hip ? MathF.Max(0f, along - 2f * s.HalfRun) : along;
        }
    }
}
