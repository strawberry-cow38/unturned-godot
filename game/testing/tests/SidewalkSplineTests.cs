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
