using System;
using System.Collections.Generic;

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

        /// <summary>Height of the ridge above the eaves. Zero for a flat roof.</summary>
        public static float RidgeHeight(RoofSpec s) => s.Kind == RoofKind.Flat ? 0f : s.Rise;

        /// <summary>Length of the ridge line. A hip shortens it by one run at each end, so a square hip has
        /// no ridge at all and is a pyramid -- which is correct, not a degenerate case to guard against.</summary>
        public static float RidgeLength(RoofSpec s)
        {
            float along = s.RidgeAlongX ? s.SpanX : s.SpanZ;
            return s.Kind == RoofKind.Hip ? MathF.Max(0f, along - 2f * s.HalfRun) : along;
        }
    }
}
