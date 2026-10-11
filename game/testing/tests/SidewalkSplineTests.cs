using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The sidewalk spline (strawberry 2026-10-11: "make sidewalks its own highly configurable spline
    /// that can attach to any road surface").
    ///
    /// ⚠ "CONFIGURABLE" IS THE CLAIM UNDER TEST, so the checks drive the dials and measure the geometry that
    /// comes out. A ribbon builder that ignored its width argument would still produce a perfectly good-looking
    /// pavement, and every "did it build" check would pass.</summary>
    public sealed class SidewalkSplineTests : GameTest
    {
        public override string Name => "editor.sidewalk_spline";
        public override double TimeoutSimSeconds => 30;

        static (List<Vector3> pts, List<Vector3> outward) Straight(int n, float step)
        {
            var p = new List<Vector3>(); var o = new List<Vector3>();
            for (int i = 0; i < n; i++) { p.Add(new Vector3(i * step, 0f, 0f)); o.Add(Vector3.Forward); }
            return (p, o);
        }

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);
            var (pts, outward) = Straight(12, 1.5f);

            // ---- 1. THE DIALS ACTUALLY REACH THE GEOMETRY.
            var narrow = EditorSidewalk.BuildRibbon(pts, outward, 1.0f, 0.4f, 0.6f);
            var wide = EditorSidewalk.BuildRibbon(pts, outward, 3.0f, 0.4f, 0.6f);
            T.Check("a ribbon builds", narrow != null && wide != null);
            if (narrow == null || wide == null) yield break;
            var na = narrow.Mesh.GetAabb(); var wa = wide.Mesh.GetAabb();
            // Outward is -Z here, so WIDTH shows up on the Z extent.
            T.Check($"width is honoured: 3 m is wider than 1 m ({wa.Size.Z:0.00} vs {na.Size.Z:0.00})",
                    wa.Size.Z > na.Size.Z + 1.5f);

            var lowKerb = EditorSidewalk.BuildRibbon(pts, outward, 2f, 0.15f, 0.6f);
            var highKerb = EditorSidewalk.BuildRibbon(pts, outward, 2f, 0.80f, 0.6f);
            T.Check($"kerb height is honoured ({highKerb.Mesh.GetAabb().End.Y:0.00} > {lowKerb.Mesh.GetAabb().End.Y:0.00})",
                    highKerb.Mesh.GetAabb().End.Y > lowKerb.Mesh.GetAabb().End.Y + 0.5f);

            // ---- 2. THE PROFILE IS A PAVEMENT, not a flat strip: the walking surface sits ABOVE road level
            // (y=0, where the kerb foot is) and the skirt reaches BELOW it.
            var mid = EditorSidewalk.BuildRibbon(pts, outward, 2f, 0.4f, 0.6f);
            var ma = mid.Mesh.GetAabb();
            T.Check($"the surface stands proud of the road ({ma.End.Y:0.00} m above 0)", ma.End.Y > 0.35f);
            T.Check($"...and the skirt reaches below it ({ma.Position.Y:0.00} m)", ma.Position.Y < -0.4f);
            T.Check($"...over the full run ({ma.Size.X:0.0} m of a {11 * 1.5f:0.0} m path)", ma.Size.X > 15f);

            // ---- 2b. THE SKIRT FOLLOWS FALLING GROUND. ⭐ THIS IS THE BUG MASTER SAW ("the kerb is floating"):
            // the kerb foot has to sit at ROAD height because it meets the asphalt, so the only edge that can
            // chase the ground is the outer one. A fixed bite is enough on a flat verge and leaves daylight
            // under the slab the moment the bank falls away faster than the bite -- here 3 m over the run
            // against a 0.6 m skirt.
            // ⚠ THE CONTROL IS THE POINT: the SAME path with NO sampler must FAIL to reach, otherwise this
            // check passes whether the sampler is consulted or quietly ignored.
            System.Func<float, float, float> falling = (x, z) => -x * 0.18f;      // drops ~3 m across 16.5 m
            var follow = EditorSidewalk.BuildRibbon(pts, outward, 2f, 0.4f, 0.6f, "Follow", falling);
            var fa = follow.Mesh.GetAabb();
            float deepest = falling(pts[pts.Count - 1].X, 0f);
            T.Check($"the skirt reaches the real ground where it falls away ({fa.Position.Y:0.00} m, ground {deepest:0.00} m)",
                    fa.Position.Y <= deepest);
            T.Check($"...which the fixed-depth skirt does NOT -- the control floats ({ma.Position.Y:0.00} m vs ground {deepest:0.00} m)",
                    ma.Position.Y > deepest + 1f);

            // ⭐ AND THE WALKING SURFACE MUST NOT MOVE. Only the skirt's floor is allowed to chase the ground --
            // if following the terrain also dragged the surface or the kerb down, the pavement would stop meeting
            // the road, which is a worse bug than the float and one a render of the far edge would not show.
            T.Check($"following the ground leaves the walking surface exactly where it was ({fa.End.Y:0.000} vs {ma.End.Y:0.000})",
                    Mathf.Abs(fa.End.Y - ma.End.Y) < 0.001f);

            // RISING ground must not punch the pavement up through its own walking surface: the floor is the
            // LOWER of the two candidates, so a bank climbing past the kerb still buries, never lifts.
            var rising = EditorSidewalk.BuildRibbon(pts, outward, 2f, 0.4f, 0.6f, "Rise", (x, z) => 5f);
            T.Check($"rising ground never lifts the skirt above the fixed bite ({rising.Mesh.GetAabb().Position.Y:0.00} m)",
                    rising.Mesh.GetAabb().Position.Y <= -0.55f);

            // ---- 2c. BOTH SIDES ARE ACTUALLY VISIBLE. ⚠⚠ THE GAP THAT LET A HALF-MISSING PAVEMENT SHIP:
            // every check above measures the ribbon's SHAPE, and a back-facing ribbon has exactly the right
            // shape. strawberry saw it immediately -- "the left side of the road's sidewalk is upside down
            // inside out or whatever. its invisible" -- because the two sides are mirrors of each other and
            // one fixed vertex order can only be right for one of them. So: the walking surface must face UP,
            // on BOTH sides, which is a claim about the normals and not about the bounding box.
            // ⚠ "FACES UP" IS A CW-FRONT CLAIM HERE. This project winds CW for front faces, so the walking
            // surface a player sees has a CCW-computed cross product pointing DOWN. Writing the check the
            // intuitive way round failed on the side that has always rendered correctly -- which is the test
            // being wrong about the convention, not the mesh. The RIGHT ribbon is the reference: it is the one
            // that shipped visible, so whatever it does is front-facing, and the mirrored side must match it.
            float FrontFraction(MeshInstance3D m)
            {
                var f = m.Mesh.GetFaces(); int front = 0, n = 0;
                for (int i = 0; i + 2 < f.Length; i += 3)
                {
                    var nrm = (f[i + 1] - f[i]).Cross(f[i + 2] - f[i]);
                    if (nrm.LengthSquared() < 1e-9f) continue;
                    var u = nrm.Normalized();
                    if (Mathf.Abs(u.Y) > 0.5f) { n++; if (u.Y < 0f) front++; }   // CW-front: level faces read -Y
                }
                return n == 0 ? 0f : (float)front / n;
            }
            var rightO2 = new List<Vector3>(); var leftO2 = new List<Vector3>();
            for (int i = 0; i < pts.Count; i++) { rightO2.Add(Vector3.Forward); leftO2.Add(Vector3.Back); }
            var rib = EditorSidewalk.BuildRibbon(pts, rightO2, 2f, 0.4f, 0.6f, "R");
            var lib = EditorSidewalk.BuildRibbon(pts, leftO2, 2f, 0.4f, 0.6f, "L");
            float ru = FrontFraction(rib), lu2 = FrontFraction(lib);
            T.Check($"the right ribbon's walking surface is front-facing ({ru * 100f:0}% of its level faces)", ru > 0.9f);
            T.Check($"...and so is the MIRRORED one, the side that went invisible ({lu2 * 100f:0}%)", lu2 > 0.9f);
            T.Check($"...the two sides agree with each other ({ru * 100f:0}% vs {lu2 * 100f:0}%)", Mathf.Abs(ru - lu2) < 0.05f);

            // ---- 3. IT IS ONE CONTINUOUS RIBBON. ⭐ This is why a generated strip was chosen over tiled props:
            // a tiled spline has to back every unit off by SplineTiling.OverlapFor to stop rigid tiles splaying
            // at a bend, and a ribbon simply shares its vertices. A CURVED path must therefore produce the same
            // triangle count as a straight one of equal point count -- no joints, nothing dropped at the bends.
            var cp = new List<Vector3>(); var co = new List<Vector3>();
            for (int i = 0; i < 12; i++)
            {
                float a = i * 0.18f;
                cp.Add(new Vector3(Mathf.Cos(a) * 20f, 0f, Mathf.Sin(a) * 20f));
                co.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
            }
            var curved = EditorSidewalk.BuildRibbon(cp, co, 2f, 0.4f, 0.6f);
            T.Check("a curved run builds", curved != null);
            T.Check($"...with the same triangle count as a straight one -- no joint to splay ({curved.Mesh.GetFaces().Length / 3} vs {mid.Mesh.GetFaces().Length / 3})",
                    curved.Mesh.GetFaces().Length == mid.Mesh.GetFaces().Length);

            // ---- 4. REFUSALS. A two-point stub is not a pavement, and mismatched arrays are a caller bug that
            // must not produce silent garbage geometry.
            T.Check("a path too short to be a pavement is refused",
                    EditorSidewalk.BuildRibbon(new List<Vector3> { Vector3.Zero }, new List<Vector3> { Vector3.Forward }, 2f, 0.4f, 0.6f) == null);
            T.Check("mismatched point/normal counts are refused",
                    EditorSidewalk.BuildRibbon(pts, new List<Vector3> { Vector3.Forward }, 2f, 0.4f, 0.6f) == null);

            foreach (var m in new[] { narrow, wide, lowKerb, highKerb, mid, curved }) m?.QueueFree();
        }
    }
}
