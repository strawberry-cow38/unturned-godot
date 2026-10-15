using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // THE ROOF IS CLOSED WHERE ITS SLOPES MEET. strawberry: "the top of roofs where the gables meet has a small gap".
    //
    // Each slope is a slab, thickness centred on its plane. Cut square to itself at the top, two slopes' slabs crossed
    // at the ridge's mid-plane and nowhere else: a V open to the sky above that line, t/cos(pitch) deep and
    // 2t sin(pitch) across -- 266 by 171 mm on the 0.5 m slab at 20 degrees -- and the two running into each other
    // below it. Every hip had the same V, and every valley its mirror under the roof. RoofShapes.PlumbFaces cuts every
    // edge but the eave plumb, so both faces of both slabs end on the vertical plane through the shared edge.
    //
    // What is measured is the SOLID, not the arithmetic that decides it: each surface is rebuilt here as WallSurface
    // builds it -- the trapezoid (RoofShapes.TaperPolygon), the plumb-cut faces (RoofShapes.PlumbFaces), their hull
    // (ConvexHull.Faces) -- put in the world through RainShelter.Frame, the same yaw/pitch frame production already
    // trusts for rain, and probed with vertical lines either side of every edge that is not an eave. The ideal it is
    // held to is the mid-plane polygons stood up plumb, half a thickness above and below: what two slabs meeting
    // without a gap or an overlap ARE. The in-engine twin (buildtool.roof_ridge_closed) probes the real mesh.
    //
    // Two controls keep a pass honest. Every case is measured a second time with the cut SQUARE (a zero shift, which is
    // the old geometry exactly) and must show the gap -- so a probe that misses the ridge, or a frame that is mirrored
    // and finds no joints, fails rather than passing on nothing. And the number of probes and joints is asserted, not
    // just printed.
    [TestFixture]
    public class RoofShapesJointTests
    {
        /// <summary>One roof surface as the solid it is built into.</summary>
        sealed class Slab
        {
            public Vector3 O, Ax, Ay, N;
            public float Half;
            public List<(float U, float V)> Mid, Front;
            public List<ConvexHull.Face> Solid;

            public Slab(WallPlan w, bool plumb)
            {
                O = new Vector3(w.X, w.Y, w.Z);
                RainShelter.Frame(w.Yaw, w.Pitch, out var ax, out var ay, out var n);
                Ax = new Vector3(ax.X, ax.Y, ax.Z); Ay = new Vector3(ay.X, ay.Y, ay.Z); N = new Vector3(n.X, n.Y, n.Z);
                Half = w.Thickness * 0.5f;
                Mid = RoofShapes.TaperPolygon(0f, 0f, w.Length, w.Height, w.Length, w.Height, w.InsetL0, w.InsetL1, w.InsetR0, w.InsetR1);
                // the world's vertical in the surface's frame, as WallSurface.PlumbShift reads it; zero = cut square
                float su = plumb ? Ax.Y / N.Y : 0f, sv = plumb ? Ay.Y / N.Y : 0f;
                Front = new List<(float U, float V)>();
                var back = new List<(float U, float V)>();
                RoofShapes.PlumbFaces(Mid, w.Height, Half, su, sv, Front, back);
                var pts = Front.Select(p => At(p.U, p.V, Half)).Concat(back.Select(p => At(p.U, p.V, -Half))).ToList();
                Solid = ConvexHull.Faces(pts);
                Plan = Mid.Select(m => { var q = At(m.U, m.V, 0f); return new Vector2(q.X, q.Z); }).ToList();
                float area = 0f;
                for (int i = 0; i < Plan.Count; i++) area += Plan[i].X * Plan[(i + 1) % Plan.Count].Y - Plan[(i + 1) % Plan.Count].X * Plan[i].Y;
                Wind = MathF.Sign(area);
            }

            readonly List<Vector2> Plan;
            readonly float Wind;

            public Vector3 At(float u, float v, float w) => O + u * Ax + v * Ay + w * N;

            /// <summary>Is the plan point under this surface's mid-plane polygon -- counting a millimetre past its edge as
            /// under it, the same allowance Vertical gives a line grazing a plumb face, so that where one roof stands over
            /// another's edge the two tests agree about which roofs a probe on that edge is under.</summary>
            public bool Over(float x, float z)
            {
                for (int i = 0; i < Plan.Count; i++)
                {
                    Vector2 a = Plan[i], b = Plan[(i + 1) % Plan.Count];
                    float len = Vector2.Distance(a, b);
                    if (len < 1e-4f) continue;
                    if (Wind * ((b.X - a.X) * (z - a.Y) - (b.Y - a.Y) * (x - a.X)) / len < -1e-3f) return false;
                }
                return true;
            }

            public float MidY(float x, float z) => O.Y - (N.X * (x - O.X) + N.Z * (z - O.Z)) / N.Y;

            /// <summary>Half the slab's vertical thickness: how far its faces stand above and below its plane.</summary>
            public float HalfVertical => Half / MathF.Abs(N.Y);

            /// <summary>Where a vertical line through the plan point is inside the solid, if it is.</summary>
            public (float Lo, float Hi)? Vertical(float x, float z)
            {
                float lo = float.NegativeInfinity, hi = float.PositiveInfinity;
                foreach (var f in Solid)
                {
                    float r = f.D - f.Normal.X * x - f.Normal.Z * z;
                    if (MathF.Abs(f.Normal.Y) < 1e-3f) { if (r < -1e-3f) return null; continue; }   // a plumb face
                    if (f.Normal.Y > 0f) hi = MathF.Min(hi, r / f.Normal.Y); else lo = MathF.Max(lo, r / f.Normal.Y);
                }
                return lo <= hi + 1e-4f ? (lo, hi) : null;
            }
        }

        /// <summary>The worst the roof does near its edges that are not eaves, in metres: open on top (Gap), standing proud
        /// of its top (Proud), open underneath (Under), hanging below its underside (Hangs). Edges: how many non-eave edges
        /// were probed; Joints: how many of them a second surface shares.</summary>
        internal sealed class Closure
        {
            public float Gap, Proud, Under, Hangs;
            public int Probes, Edges, Joints;
            public override string ToString() => $"gap {Gap * 1000f:0.0} mm, proud {Proud * 1000f:0.0}, under {Under * 1000f:0.0}, hangs {Hangs * 1000f:0.0} ({Edges} edges, {Joints} joints, {Probes} probes)";
        }

        /// <summary>How far an eave keeps a probe away. The eave is cut square on purpose, which differs from the plumb ideal
        /// by t sin(pitch) in plan -- 0.18 m at 45 degrees.</summary>
        const float EaveClear = 0.3f;

        internal static Closure Measure(IEnumerable<WallPlan> surfaces, bool plumb)
        {
            var slabs = surfaces.Select(w => new Slab(w, plumb)).ToList();
            var c = new Closure();
            // every edge in the world; an EAVE is the level edge at a surface's foot that no other surface shares -- a foot
            // that another surface's top meets (a band over two trapezoids, as the cross gable once was) is a joint, and
            // calling it an eave hid exactly the groove under it from this test
            var edges = new List<(int Slab, Vector3 A, Vector3 B, bool Foot)>();
            for (int s = 0; s < slabs.Count; s++)
            {
                var m = slabs[s].Mid;
                for (int i = 0; i < m.Count; i++)
                {
                    var (a, b) = (m[i], m[(i + 1) % m.Count]);
                    if (MathF.Abs(a.U - b.U) + MathF.Abs(a.V - b.V) < 0.05f) continue;
                    edges.Add((s, slabs[s].At(a.U, a.V, 0f), slabs[s].At(b.U, b.V, 0f), MathF.Abs(a.V) < 1e-3f && MathF.Abs(b.V) < 1e-3f));
                }
            }
            // shared with another surface: the same line, overlapping by more than a few centimetres
            bool Shared(int k)
            {
                var e = edges[k];
                var dir = Vector3.Normalize(e.B - e.A);
                float len = Vector3.Distance(e.A, e.B);
                return edges.Any(o => o.Slab != e.Slab
                    && Vector3.Cross(o.A - e.A, dir).Length() < 0.01f && Vector3.Cross(o.B - e.A, dir).Length() < 0.01f
                    && MathF.Min(len, MathF.Max(Vector3.Dot(o.A - e.A, dir), Vector3.Dot(o.B - e.A, dir)))
                       - MathF.Max(0f, MathF.Min(Vector3.Dot(o.A - e.A, dir), Vector3.Dot(o.B - e.A, dir))) > 0.05f);
            }
            var shared = Enumerable.Range(0, edges.Count).Select(Shared).ToList();
            var eaves = Enumerable.Range(0, edges.Count).Where(k => edges[k].Foot && !shared[k]).Select(k => edges[k]).ToList();
            static float PlanDist(Vector3 p, Vector3 a, Vector3 b)
            {
                var ab = new Vector2(b.X - a.X, b.Z - a.Z); var ap = new Vector2(p.X - a.X, p.Z - a.Z);
                float k = Math.Clamp(Vector2.Dot(ap, ab) / MathF.Max(1e-9f, ab.LengthSquared()), 0f, 1f);
                return (ap - ab * k).Length();
            }
            for (int ei = 0; ei < edges.Count; ei++)
            {
                var e = edges[ei];
                if (e.Foot && !shared[ei]) continue;
                c.Edges++;
                if (shared[ei]) c.Joints++;
                var dir = Vector3.Normalize(e.B - e.A);
                var across = Vector3.Normalize(new Vector3(-dir.Z, 0f, dir.X));
                for (int k = 1; k < 10; k++)
                {
                    var on = e.A + (e.B - e.A) * (k / 10f);
                    // not ON the edge: there a vertical line grazes the plumb face and the plan test is a coin toss,
                    // which reads as a whole slab missing wherever one roof stands over another's edge (a porch under
                    // a gable's overhang). 5 mm off it, the V a square cut leaves is still all but its full depth.
                    foreach (float d in new[] { -0.4f, -0.2f, -0.1f, -0.05f, -0.02f, -0.005f, 0.005f, 0.02f, 0.05f, 0.1f, 0.2f, 0.4f })
                    {
                        var p = on + across * d;
                        if (eaves.Any(o => PlanDist(p, o.A, o.B) < EaveClear)) continue;
                        float top = float.NegativeInfinity, bottom = float.PositiveInfinity;
                        foreach (var s in slabs.Where(s => s.Over(p.X, p.Z)))
                        {
                            float y = s.MidY(p.X, p.Z);
                            top = MathF.Max(top, y + s.HalfVertical); bottom = MathF.Min(bottom, y - s.HalfVertical);
                        }
                        if (float.IsInfinity(top)) continue;   // off the roof
                        float hi = float.NegativeInfinity, lo = float.PositiveInfinity;
                        foreach (var s in slabs)
                            if (s.Vertical(p.X, p.Z) is { } iv) { hi = MathF.Max(hi, iv.Hi); lo = MathF.Min(lo, iv.Lo); }
                        c.Probes++;
                        if (float.IsInfinity(hi)) { c.Gap = c.Under = float.PositiveInfinity; continue; }   // a hole right through
                        c.Gap = MathF.Max(c.Gap, top - hi); c.Proud = MathF.Max(c.Proud, hi - top);
                        c.Under = MathF.Max(c.Under, lo - bottom); c.Hangs = MathF.Max(c.Hangs, bottom - lo);
                    }
                }
            }
            return c;
        }

        static WallPlan Plan(RoofPlane p, float thickness) => new WallPlan
        {
            X = p.X, Y = p.Y, Z = p.Z, Yaw = p.YawDeg, Pitch = p.PitchDeg, Length = p.Length, Height = p.Height,
            Thickness = thickness, Kind = SurfaceKind.Roof, InsetL0 = p.InsetL0, InsetL1 = p.InsetL1, InsetR0 = p.InsetR0, InsetR1 = p.InsetR1,
        };

        internal const float Tol = 0.001f;   // a millimetre

        // EVERY SHAPE THE EDITOR AND THE GENERATOR CAN MAKE, at every pitch the editor offers, on a long, a square and a
        // deep footprint (the square hip is a pyramid; the deep one turns the ridge to run along Z).
        // BREAK IT: cut the top edge square again (PlumbFaces' `square` true at the top) -> a ridge V 259-354 mm deep;
        // cut the hip lines square (zero shift for slanted edges) -> the same V down every hip.
        [Test]
        public void EverySlopeMeetsTheNextWithNoGap()
        {
            var bad = new List<string>();
            int joints = 0, probes = 0;
            foreach (var kind in new[] { RoofKind.Gable, RoofKind.Hip, RoofKind.Shed })
                foreach (float pitch in new[] { 12f, 15f, 18f, 20f, 22f, 27f, 30f, 45f })
                    foreach (var (w, d) in new[] { (20f, 12f), (12f, 12f), (9f, 14f) })
                    {
                        var s = new RoofSpec { Kind = kind, MinX = 0f, MaxX = w, MinZ = 0f, MaxZ = d, TopY = 3f, PitchDeg = pitch, Thickness = 0.5f };
                        var planes = RoofShapes.Planes(s).Select(p => Plan(p, s.Thickness)).ToList();
                        var cut = Measure(planes, plumb: true);
                        var square = Measure(planes, plumb: false);
                        string name = $"{kind} {w}x{d} at {pitch}";
                        if (pitch == 20f && w == 20f) TestContext.Progress.WriteLine($"[roof-joints] {name}: plumb {cut}; square {square}");
                        joints += cut.Joints; probes += cut.Probes;
                        if (cut.Probes < 100) bad.Add($"{name}: only {cut.Probes} probes");
                        if (kind != RoofKind.Shed && cut.Joints < (kind == RoofKind.Gable ? 2 : 8)) bad.Add($"{name}: {cut.Joints} joints found");
                        if (cut.Gap > Tol || cut.Proud > Tol || cut.Under > Tol || cut.Hangs > Tol) bad.Add($"{name}: {cut}");
                        // the control: square, the V is there and this sees it -- t/cos(pitch) deep on the edge, and 5 mm off
                        // it (where the nearest probe stands) shallower by 5 mm / (sin cos), under a tenth of it at 12 degrees
                        float v = s.Thickness * 0.5f / MathF.Cos(pitch * MathF.PI / 180f);
                        if (square.Gap < v * 0.8f) bad.Add($"{name}: cut square, the probe sees only a {square.Gap * 1000f:0} mm gap (expected {v * 1000f:0})");
                    }
            TestContext.Progress.WriteLine($"[roof-joints] {joints} joints, {probes} probes over gable, hip and shed at 8 pitches; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // THE APEX ITSELF, in the terms of the complaint: the two slopes' top outer corners. Square they stood 2t sin(pitch)
        // apart across the ridge -- 171 mm at 20 degrees; plumb they are one line, t/cos(pitch) over the ridge's mid-plane.
        [Test]
        public void TheTwoSlopesOuterFacesShareTheirTopEdge()
        {
            foreach (float pitch in new[] { 15f, 20f, 22f, 45f })
            {
                var s = new RoofSpec { Kind = RoofKind.Gable, MinX = 0f, MaxX = 20f, MinZ = 0f, MaxZ = 12f, TopY = 3f, PitchDeg = pitch, Thickness = 0.5f };
                Vector3 TopMid(Slab sl)
                {
                    float vTop = sl.Front.Max(p => p.V);
                    var top = sl.Front.Where(p => p.V > vTop - 1e-3f).ToList();
                    return sl.At(top.Average(p => p.U), vTop, sl.Half);
                }
                var plumb = RoofShapes.Planes(s).Select(p => new Slab(Plan(p, 0.5f), true)).ToList();
                var square = RoofShapes.Planes(s).Select(p => new Slab(Plan(p, 0.5f), false)).ToList();
                float rad = pitch * MathF.PI / 180f, apex = s.TopY + s.Rise + 0.25f / MathF.Cos(rad);
                float apart = Vector3.Distance(TopMid(plumb[0]), TopMid(plumb[1]));
                float was = Vector3.Distance(TopMid(square[0]), TopMid(square[1]));
                TestContext.Progress.WriteLine($"[roof-apex] gable at {pitch}: outer top edges {apart * 1000f:0.0} mm apart (square: {was * 1000f:0.0}), at y {TopMid(plumb[0]).Y:0.000} vs the slopes' meeting {apex:0.000}");
                Assert.That(apart, Is.LessThan(Tol), $"at {pitch}: the outer faces meet at the ridge");
                Assert.That(TopMid(plumb[0]).Y, Is.EqualTo(apex).Within(Tol), $"at {pitch}: where the two outer faces' planes cross");
                Assert.That(was, Is.EqualTo(0.5f * MathF.Sin(rad)).Within(Tol), $"at {pitch}: the control -- square, they stand 2t sin(pitch) apart");
            }
        }
    }

    public partial class BuildingGenTests
    {
        // EVERY GENERATED ROOF IS CLOSED WHERE ITS SLOPES MEET (RoofShapesJointTests has the why): over the whole sweep --
        // a house's gable or hip, an L's or a T's cross gable (its main ridge, its arms' ridges and the valleys under them),
        // the sheds on lower floors leaning on the wall above, porch roofs, a block of flats' gable, turned and plot-fitted
        // buildings among them.
        // BREAK IT: restore the cross gable's band above the arm's ridge -> its eave, cut square, under the plumb tops of
        // the trapezoids below opens a groove UNDER the roof (~200 mm); cut everything square -> every ridge, hip, valley.
        [Test]
        public void RoofsAreClosedAtEveryRidgeHipAndValley()
        {
            var bad = new List<string>();
            int roofs = 0, cross = 0, hips = 0, sheds = 0, blocks = 0, probes = 0, squareOpen = 0;
            foreach (var (spec, res) in All())
            {
                var slopes = res.Walls.Where(w => w.Kind == SurfaceKind.Roof && MathF.Abs(w.Pitch + 90f) > 0.5f).ToList();
                if (slopes.Count == 0) continue;
                roofs++;
                if (spec.Kind == BuildingKind.Apartment) blocks++;
                if (res.Shape != FootprintShape.Rect && res.Floors == 1) cross++;
                if (res.Roof == RoofKind.Hip) hips++;
                if (slopes.Any(w => w.Y < res.Floors * Storey - 0.05f)) sheds++;
                var cut = RoofShapesJointTests.Measure(slopes, plumb: true);
                probes += cut.Probes;
                if (cut.Gap > RoofShapesJointTests.Tol || cut.Proud > RoofShapesJointTests.Tol || cut.Under > RoofShapesJointTests.Tol || cut.Hangs > RoofShapesJointTests.Tol)
                    bad.Add($"{Name(spec)} ({res.Roof}, {res.Shape}, {res.Floors} floors, {res.RoofPitch} deg): {cut}");
                // the control: cut square, the same probes find each of these open
                if (RoofShapesJointTests.Measure(slopes, plumb: false).Gap > 0.2f) squareOpen++;
                // and saved and loaded it is the same roof: the cut is the surface's own (a Roof, pitched), not a field the
                // save has to carry -- but a cross gable's negative inset does have to survive the trip
                var reread = RoofShapesJointTests.Measure(WallSave.Read(WallSave.Write(slopes).Split('\n')), plumb: true);
                if (reread.Gap > RoofShapesJointTests.Tol || reread.Proud > RoofShapesJointTests.Tol || reread.Under > RoofShapesJointTests.Tol
                    || reread.Hangs > RoofShapesJointTests.Tol || reread.Probes < cut.Probes * 0.9f)   // (the file keeps 0.1 mm, so a probe on an edge may fall either side of it)
                    bad.Add($"{Name(spec)}, saved and loaded: {reread}");
            }
            TestContext.Progress.WriteLine($"[gen-roof-joints] {roofs} pitched roofs ({cross} cross gables, {hips} hips, {sheds} with sheds, {blocks} blocks of flats), {probes} probes; cut square, {squareOpen} of them open; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(cross, Is.GreaterThan(10)); Assert.That(hips, Is.GreaterThan(10)); Assert.That(sheds, Is.GreaterThan(10)); Assert.That(blocks, Is.GreaterThan(5));
            Assert.That(squareOpen, Is.EqualTo(roofs), "the control: cut square, every one of them is open somewhere");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }
    }
}
