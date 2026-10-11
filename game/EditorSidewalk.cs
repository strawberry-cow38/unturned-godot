using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>A sidewalk as its own spline, laid against any road surface (strawberry 2026-10-11: "make
    /// sidewalks its own highly configurable spline that can attach to any road surface").
    ///
    /// ⭐ GENERATED RIBBON, NOT TILED PROPS, and that is the configurability. Every other spline in this editor
    /// lays a fixed unit end to end -- Block_Road's 3 m barrier, the rail's sleeper -- which fixes the width and
    /// the height at whatever the artist modelled. A pavement whose width and kerb height are dials cannot be a
    /// prop, so this extrudes a cross-section instead.
    ///
    /// ⭐⭐ AND IT SIDESTEPS THE TILE WEDGE ENTIRELY. Rigid tiles meeting at a turn splay apart, which is why
    /// SplineTiling.OverlapFor exists and why every tiled spline here backs each unit off by
    /// min(halfWidth*|turn|, pitch/2). A continuous ribbon shares its vertices between segments, so there is no
    /// joint to open -- the geometry simply bends. That removes the single most bug-prone part of the other
    /// splines rather than reimplementing it.
    ///
    /// The PROFILE is taken from the real kit rather than invented: ObjMesh.SplitKerb measured the retail
    /// pieces at a kerb standing 0.4 m proud of a carriageway at z = 0, so that is the default rise here.</summary>
    public partial class EditorSidewalk : Node3D
    {
        /// <summary>Walking surface width, metres. The retail kit's pavement is about 2 m; the dial exists
        /// because a town high street and a suburban lane want different ones.</summary>
        public float Width = 2.0f;
        /// <summary>How far the kerb stands proud of the road. 0.4 m is MEASURED off the retail pieces
        /// (SplitKerb: palette faces span z -1.0 .. +0.4 against a carriageway at 0).</summary>
        public float KerbHeight = 0.4f;
        /// <summary>How far the kerb face sits back from the road's own edge. Small and positive so the
        /// pavement meets the asphalt without z-fighting along the join.</summary>
        public float RoadGap = 0.05f;
        /// <summary>Skirt dropped below the walking surface, so a pavement on uneven ground does not show
        /// daylight under its outer edge. The kit does the same thing -- its palette faces reach z = -1.0.</summary>
        public float Skirt = 0.6f;

        public enum Side { Left, Right, Both }

        /// <summary>Where the pavement stands highest above the real ground, and by how much. The harness aims
        /// its camera here: a fix for floating geometry has to be photographed where it floated, and the first
        /// re-render after this fix looked unchanged purely because the camera sat on a flat stretch.</summary>
        public static float WorstFloat; public static Vector3 WorstAt;

        /// <summary>Metres between evaluated points. Short enough that a bend reads as a curve rather than a
        /// polygon; it costs vertices, not draw calls, because the whole run is one mesh.</summary>
        public const float Step = 1.5f;

        /// <summary>Build one pavement ribbon along a point list and return the node, or null if the path is
        /// too short to be a pavement. `pts` is the line the KERB FACE runs along; the walking surface is built
        /// outward from it on `outward`.</summary>
        /// <param name="groundY">Optional: the real ground height under a world XZ. ⚠⚠ WITHOUT THIS THE KERB
        /// FLOATS, and that is exactly what shipped first. The kerb foot sits at ROAD height -- it has to, it
        /// meets the asphalt -- and the outer edge was then dropped a FIXED 0.6 m. Roads sit on embankments and
        /// the ground falls away beside them, so anywhere the drop exceeded 0.6 m the pavement hung in the air
        /// with daylight under it (master, on the first render: "the kerb is floating"). The skirt now reaches
        /// the actual ground at each point instead of guessing a depth.</param>
        public static MeshInstance3D BuildRibbon(IReadOnlyList<Vector3> pts, IReadOnlyList<Vector3> outward,
                                                 float width, float kerbH, float skirt, string name = "Sidewalk",
                                                 System.Func<float, float, float> groundY = null)
        {
            if (pts == null || pts.Count < 2 || outward == null || outward.Count != pts.Count) return null;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            // Four rails: kerb bottom (at road level), kerb top, back edge top, back edge bottom (skirt).
            // ⚠ WINDING IS CW-FRONT in this project (see the Godot traps note): a counter-clockwise quad here
            // renders inside-out and reads as a hole in the pavement rather than as a flipped face.
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
                st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
            }

            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Vector3 p0 = pts[i], p1 = pts[i + 1];
                Vector3 o0 = outward[i].Normalized(), o1 = outward[i + 1].Normalized();
                Vector3 up = Vector3.Up * kerbH;

                Vector3 k0 = p0, k1 = p1;                               // kerb foot, at road level
                Vector3 t0 = p0 + up, t1 = p1 + up;                     // kerb top (front of the walking surface)
                Vector3 b0 = p0 + o0 * width + up, b1 = p1 + o1 * width + up;   // back of the walking surface
                // The skirt's FLOOR: the lower of "a fixed bite below the surface" and "the real ground here",
                // so it buries into a rising bank and still reaches down a falling one. Minus a few cm so it
                // ends INSIDE the ground rather than coplanar with it, which would z-fight along the whole run.
                float f0 = b0.Y - skirt, f1 = b1.Y - skirt;
                if (groundY != null)
                {
                    f0 = Mathf.Min(f0, groundY(b0.X, b0.Z) - 0.05f);
                    f1 = Mathf.Min(f1, groundY(b1.X, b1.Z) - 0.05f);
                }
                Vector3 s0 = new Vector3(b0.X, f0, b0.Z), s1 = new Vector3(b1.X, f1, b1.Z);

                Quad(k0, t0, t1, k1);     // the kerb face, standing against the road
                Quad(t0, b0, b1, t1);     // the surface you walk on
                Quad(b0, s0, s1, b1);     // the outer skirt, so no daylight under the far edge
            }
            st.GenerateNormals();
            var mesh = st.Commit();
            if (mesh == null || mesh.GetSurfaceCount() == 0) return null;
            return new MeshInstance3D { Name = name, Mesh = mesh };
        }

        /// <summary>Lay pavement along an existing road, which is the "attach to any road surface" half.
        /// ⚠ EACH SIDE IS EVALUATED SEPARATELY, never one centreline offset twice: the two edges of a bend are
        /// different lengths, and reusing one list crowds the inside kerb. Block_Road learned this the same way.</summary>
        public int LayAlongRoad(Node parent, RoadField roads, int road, Side side, List<Node3D> into = null)
        {
            if (parent == null || roads == null) return 0;
            float total = roads.RoadLength(road);
            float half = roads.RoadHalfWidth(road);
            if (total < Step * 2f || half <= 0f) return 0;
            float off = Mathf.Max(0.1f, half + RoadGap);

            int n = Mathf.FloorToInt(total / Step);
            var leftP = new List<Vector3>(n + 1); var leftO = new List<Vector3>(n + 1);
            var rightP = new List<Vector3>(n + 1); var rightO = new List<Vector3>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                if (!roads.EvaluateAlong(road, i * Step, out var p, out var t)) return 0;
                var r = new Vector3(t.Z, 0f, -t.X).Normalized();   // the spline's RIGHT, same convention as Block_Road
                rightP.Add(p + r * off); rightO.Add(r);            // kerb faces the road, surface runs outward
                leftP.Add(p - r * off); leftO.Add(-r);
            }

            int made = 0;
            if (side != Side.Left) made += Emit(parent, rightP, rightO, $"Sidewalk_R{road}", into);
            if (side != Side.Right) made += Emit(parent, leftP, leftO, $"Sidewalk_L{road}", into);
            Log.Print($"[sidewalk] road {road}: {made} ribbon(s) over {total:0.#} m, {Width:0.00} m wide, kerb {KerbHeight:0.00} m");
            return made;
        }

        int Emit(Node parent, List<Vector3> pts, List<Vector3> outward, string name, List<Node3D> into)
        {
            // The live terrain, so the skirt buries instead of floating. Null in a bare harness, which is why
            // BuildRibbon still takes a fixed fallback depth rather than requiring one.
            // UG_WALKNOGROUND=1 restores the old fixed-depth skirt, so the fix can be photographed against
            // itself at one camera. A visual bug wants a visual control.
            bool noGround = System.Environment.GetEnvironmentVariable("UG_WALKNOGROUND") == "1";
            System.Func<float, float, float> ground =
                (!noGround && Terrain.Active != null) ? (x, z) => Terrain.Active.SampleHeight(x, z) : null;
            // ⚠ INSTRUMENTED after master said "the kerb is floating" and the re-render looked identical --
            // which could mean the fix worked and this stretch is flat, or that it never fired. Reporting the
            // actual drop distinguishes those two, where another look at the picture cannot.
            float worst = 0f; int sampled = 0; var worstPt = Vector3.Zero;
            var probe = Terrain.Active != null ? (System.Func<float, float, float>)((x, z) => Terrain.Active.SampleHeight(x, z)) : null;
            if (probe != null)
                for (int i = 0; i < pts.Count; i++)
                {
                    var outer = pts[i] + outward[i].Normalized() * Width;
                    float gap = (pts[i].Y + KerbHeight) - probe(outer.X, outer.Z);   // surface height above real ground
                    if (gap > worst) { worst = gap; worstPt = pts[i]; }
                    sampled++;
                }
            var mi = BuildRibbon(pts, outward, Width, KerbHeight, Skirt, name, ground);
            Log.Print($"[sidewalk] {name}: worst surface-above-ground {worst:0.00} m over {sampled} pts "
                    + $"(fixed skirt was {Skirt:0.00} -- anything beyond that WOULD have floated)");
            if (worst > WorstFloat) { WorstFloat = worst; WorstAt = worstPt; }
            if (mi == null) return 0;
            parent.AddChild(mi);
            // A pavement is something you STAND ON, so it needs a collider and a surface -- without the tag it
            // reads as the unlabelled concrete default, which is right by luck here and wrong the moment
            // somebody builds a wooden boardwalk off the same spline.
            var body = new StaticBody3D { Name = name + "_body", CollisionLayer = 1u << 0 };
            body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
            body.AddChild(new CollisionShape3D { Shape = mi.Mesh.CreateTrimeshShape() });
            mi.AddChild(body);
            into?.Add(mi);
            return 1;
        }
    }
}
