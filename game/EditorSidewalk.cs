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

        /// <summary>DROPPED KERBS (strawberry 2026-10-12: "add support for dipped kerbs with ur sidewalk
        /// tool"). Each entry is a run along the ribbon -- metres from its start, and how long -- where the
        /// kerb falls to DipKerb so a wheelchair, a pram or a car crossing a driveway can get up it. The
        /// WALKING SURFACE DOES NOT MOVE: a dropped kerb lowers the KERB, not the pavement, so the slab stays
        /// level and the drop is taken by the kerb face alone. Lowering the surface too would be a ramp down
        /// into the road, which is a different thing and wrong at a crossing.</summary>
        public readonly List<(float at, float length)> Dips = new();
        /// <summary>The lip left at the bottom of a dropped kerb. ⚠ NOT ZERO: a flush kerb gives a white cane
        /// nothing to find, and the pavement and the road would meet in a single coplanar edge.</summary>
        public float DipKerb = 0.04f;
        /// <summary>How long the kerb takes to fall into a dip and climb back out.</summary>
        public float DipRamp = 0.9f;

        /// <summary>Drop the kerb across `length` metres starting `at` metres along the ribbon.</summary>
        public void AddDip(float at, float length) { if (length > 0.05f) Dips.Add((at, length)); }

        /// <summary>The kerb's height `d` metres along a ribbon, given the dips asked for.</summary>
        public float KerbAt(float d)
        {
            float h = KerbHeight;
            foreach (var (at, len) in Dips)
            {
                if (d <= at - DipRamp || d >= at + len + DipRamp) continue;
                // inside the flat of the dip, or on one of its two ramps
                float f = d < at ? (at - d) / DipRamp : d > at + len ? (d - at - len) / DipRamp : 0f;
                h = Mathf.Min(h, Mathf.Lerp(DipKerb, KerbHeight, Mathf.Clamp(f, 0f, 1f)));
            }
            return h;
        }

        public enum Side { Left, Right, Both }

        /// <summary>The fixed-depth control, as a FIELD rather than a bare env read, so a single process can
        /// render both treatments over ONE road layout. ⚠ It has to be: this harness's roads are not reproduced
        /// between launches (measured -- the road checksum moves while the ground checksum does not), so a
        /// before/after taken as two launches is two different worlds and cannot show a change.</summary>
        public static bool NoGround = System.Environment.GetEnvironmentVariable("UG_WALKNOGROUND") == "1";

        /// <summary>The float the demo camera AIMS for (m). ⚠ NOT the worst one: the worst float in this
        /// harness is wherever a randomly-drawn road crosses a gully, 8 m up a bare cliff, where a 0.4 m kerb is
        /// invisible and the drop is a ROAD problem rather than a pavement one. A float you can actually see at
        /// eye height is about a metre.</summary>
        public static float FloatTarget = 1.2f;
        public static Vector3 PickAt; public static Vector3 PickOut; public static float PickFloat;
        /// <summary>The Width of the instance that picked PickAt, so a demo camera can aim at the slab's OUTER
        /// edge (where the daylight is) rather than at its centreline.</summary>
        public static float DemoWidth = 2f;

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
                                                 System.Func<float, float, float> groundY = null,
                                                 IReadOnlyList<float> kerbPerPoint = null)
        {
            if (pts == null || pts.Count < 2 || outward == null || outward.Count != pts.Count) return null;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            // Four rails: kerb bottom (at road level), kerb top, back edge top, back edge bottom (skirt).
            // ⚠ WINDING IS CW-FRONT in this project (see the Godot traps note): a counter-clockwise quad here
            // renders inside-out and reads as a hole in the pavement rather than as a flipped face.
            // ⚠⚠ THE WINDING DEPENDS ON WHICH SIDE OF THE ROAD THIS IS (strawberry 2026-10-11: "the left side
            // of the road's sidewalk is upside down inside out or whatever. its invisible"). The two sides are
            // mirror images -- the left one's outward normal points the opposite way -- so one fixed vertex
            // order produces a front-facing ribbon on one side and a back-facing one on the other, and under
            // cull_back the back-facing one simply is not there. Nothing errors; half the pavement is missing.
            // So the order is decided PER SEGMENT from the geometry itself: with the walking surface's normal
            // required to point UP, (along x outward) says which way round the quad has to go. Self-correcting,
            // so a caller handing this a ribbon in any orientation still gets a visible one.
            bool flip = false;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                if (flip) { (a, b, c, d) = (d, c, b, a); }
                st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
                st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
            }

            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Vector3 p0 = pts[i], p1 = pts[i + 1];
                Vector3 o0 = outward[i].Normalized(), o1 = outward[i + 1].Normalized();
                // ⚠ THE KERB IS PER POINT, so a dropped kerb can fall away under a surface that does not
                // move. Both ends of the segment get their own height or the quad twists.
                float k0h = kerbPerPoint != null && i < kerbPerPoint.Count ? kerbPerPoint[i] : kerbH;
                float k1h = kerbPerPoint != null && i + 1 < kerbPerPoint.Count ? kerbPerPoint[i + 1] : kerbH;
                Vector3 up = Vector3.Up * k0h, up1 = Vector3.Up * k1h;
                // Which way round this segment's quads must go, measured rather than assumed.
                var along = p1 - p0;
                flip = along.Cross(o0).Y < 0f;

                Vector3 k0 = p0, k1 = p1;                               // kerb foot, at road level
                Vector3 t0 = p0 + up, t1 = p1 + up1;                    // kerb top (front of the walking surface)
                // ⚠ THE SURFACE KEEPS THE FULL KERB HEIGHT even where the kerb is dropped -- the pavement is
                // level and the dip is a chamfer in its front face. Taking the dipped height here instead
                // would tip the whole slab into the gutter.
                Vector3 b0 = p0 + o0 * width + Vector3.Up * kerbH, b1 = p1 + o1 * width + Vector3.Up * kerbH;
                // The skirt's FLOOR: the lower of "a fixed bite below the surface" and "the real ground here",
                // so it buries into a rising bank and still reaches down a falling one. Minus a few cm so it
                // ends INSIDE the ground rather than coplanar with it, which would z-fight along the whole run.
                // ⚠ MEASURED FROM ROAD LEVEL (p0.Y), not from the surface: dropping the kerbH term here makes
                // the no-ground fallback SHALLOWER than the skirt asked for, which floats worse rather than
                // better. The ribbon test rejects that, which is how this was caught.
                float f0 = p0.Y - skirt, f1 = p1.Y - skirt;
                if (groundY != null)
                {
                    f0 = Mathf.Min(f0, groundY(b0.X, b0.Z) - 0.05f);
                    f1 = Mathf.Min(f1, groundY(b1.X, b1.Z) - 0.05f);
                }
                Vector3 s0 = new Vector3(b0.X, f0, b0.Z), s1 = new Vector3(b1.X, f1, b1.Z);

                // ⚠⚠ A SMOOTH GROUP PER STRIP, so normals average ALONG the run but never across the kerb's
                // top edge (strawberry 2026-10-12: "theres a lighting bug with the sidewalks between the
                // straights and the curves?"). Flat per-face normals light a tessellated curve as a row of
                // discrete facets, and where that meets a straight run -- one face, one normal -- the two
                // disagree at the join. Smoothing along each strip makes both tangent-aligned, so they agree
                // at the seam even though they are separate meshes; keeping the strips in SEPARATE groups
                // keeps the kerb's 90-degree edge hard, which one blanket smoothing pass would round off into
                // a sausage.
                st.SetSmoothGroup(0); Quad(k0, t0, t1, k1);     // the kerb face, standing against the road
                st.SetSmoothGroup(1); Quad(t0, b0, b1, t1);     // the surface you walk on
                st.SetSmoothGroup(2); Quad(b0, s0, s1, b1);     // the outer skirt, so no daylight under the far edge
            }
            // ⚠⚠ CAP BOTH ENDS. A ribbon is three strips and nothing closes the rectangle they leave at each
            // tip, so every pavement that stops -- which is every one of them, trimmed back at a junction --
            // ends in an open box you can see straight into. At a mouth that reads as a dark wedge between
            // the road and the kerb, which is what master kept reporting as "big gaps between the road
            // spline and the merged mesh area". Exactly the fault the ROADS had at their junction ends, in a
            // different mesh: an unclosed slab is a hole whatever is holding it up.
            if (pts.Count >= 2)
            {
                void Cap(int i, bool front)
                {
                    Vector3 o = outward[i].Normalized(), p0 = pts[i];
                    float kh = kerbPerPoint != null && i < kerbPerPoint.Count ? kerbPerPoint[i] : kerbH;
                    Vector3 k = p0, t = p0 + Vector3.Up * kh, b = p0 + o * width + Vector3.Up * kerbH;
                    float f = p0.Y - skirt;
                    if (groundY != null) f = Mathf.Min(f, groundY(b.X, b.Z) - 0.05f);
                    Vector3 sq = new Vector3(b.X, f, b.Z);
                    st.SetSmoothGroup(uint.MaxValue);      // a flat cap: never averaged into its neighbours
                    if (front) { st.AddVertex(k); st.AddVertex(t); st.AddVertex(b); st.AddVertex(k); st.AddVertex(b); st.AddVertex(sq); }
                    else { st.AddVertex(k); st.AddVertex(b); st.AddVertex(t); st.AddVertex(k); st.AddVertex(sq); st.AddVertex(b); }
                }
                Cap(0, true); Cap(pts.Count - 1, false);
            }
            st.Index();              // weld within a smooth group: GenerateNormals averages over shared verts
            st.GenerateNormals();
            var mesh = st.Commit();
            if (mesh == null || mesh.GetSurfaceCount() == 0) return null;
            return new MeshInstance3D { Name = name, Mesh = mesh };
        }

        /// <summary>Lay pavement along an existing road, which is the "attach to any road surface" half.
        /// ⚠ EACH SIDE IS EVALUATED SEPARATELY, never one centreline offset twice: the two edges of a bend are
        /// different lengths, and reusing one list crowds the inside kerb. Block_Road learned this the same way.</summary>
        public int LayAlongRoad(Node parent, RoadField roads, int road, Side side, List<Node3D> into = null,
                                float trimStart = 0f, float trimEnd = 0f)
        {
            if (parent == null || roads == null) return 0;
            float total = roads.RoadLength(road);
            float half = roads.RoadHalfWidth(road);
            if (total < Step * 2f || half <= 0f) return 0;
            float off = Mathf.Max(0.1f, half + RoadGap);
            // ⚠ THE TRIM EXISTS FOR JUNCTIONS. Four ribbons running to the ends of four roads that meet at one
            // crossroads all arrive in the middle of the intersection and intersect each other. Held back by
            // the widest road meeting that end plus a corner radius, they stop at the kerb line instead -- and
            // the gap left behind IS the corner. A span shorter than the two trims is all intersection and
            // gets no pavement at all, which is correct: there is no pavement there to walk on.
            float from = Mathf.Max(0f, trimStart), to = total - Mathf.Max(0f, trimEnd);
            if (to - from < Step * 2f) return 0;

            int n = Mathf.FloorToInt((to - from) / Step);
            var leftP = new List<Vector3>(n + 1); var leftO = new List<Vector3>(n + 1);
            var rightP = new List<Vector3>(n + 1); var rightO = new List<Vector3>(n + 1);
            var kerbs = new List<float>(n + 1);
            // ⚠ THE LAST SAMPLE LANDS ON `to`, NOT ON THE LAST WHOLE STEP. floor() leaves the ribbon up to a
            // full Step (1.5 m) short of where it was asked to end -- invisible on an open run, and at a
            // junction it is a notch between this pavement and the corner piece that starts exactly at `to`.
            for (int i = 0; i <= n + 1; i++)
            {
                float d = i <= n ? from + i * Step : to;
                if (i == n + 1 && d - (from + n * Step) < 0.05f) break;   // already there
                if (!roads.EvaluateAlong(road, d, out var p, out var t)) return 0;
                var r = new Vector3(t.Z, 0f, -t.X).Normalized();   // the spline's RIGHT, same convention as Block_Road
                rightP.Add(p + r * off); rightO.Add(r);            // kerb faces the road, surface runs outward
                leftP.Add(p - r * off); leftO.Add(-r);
                kerbs.Add(KerbAt(d));
            }

            int made = 0;
            if (side != Side.Left) made += Emit(parent, rightP, rightO, $"Sidewalk_R{road}", into, kerbs);
            if (side != Side.Right) made += Emit(parent, leftP, leftO, $"Sidewalk_L{road}", into, kerbs);
            Log.Print($"[sidewalk] road {road}: {made} ribbon(s) over {to - from:0.#} m of {total:0.#}"
                    + $" (trim {from:0.#}/{total - to:0.#}), {Width:0.00} m wide, kerb {KerbHeight:0.00} m");
            return made;
        }

        int Emit(Node parent, List<Vector3> pts, List<Vector3> outward, string name, List<Node3D> into, List<float> kerbs = null)
        {
            // The live terrain, so the skirt buries instead of floating. Null in a bare harness, which is why
            // BuildRibbon still takes a fixed fallback depth rather than requiring one.
            // UG_WALKNOGROUND=1 restores the old fixed-depth skirt, so the fix can be photographed against
            // itself at one camera. A visual bug wants a visual control.
            bool noGround = NoGround;
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
                    // ⚠⚠ DAYLIGHT, not surface-height-above-ground. The first version of this measured
                    // (surface - ground), which is 1.0 m larger than the hole you can see: the fixed skirt
                    // already hangs KerbHeight + Skirt below the surface, so nothing is visible until the drop
                    // exceeds that. Quoting the un-named number would have claimed a 4.24 m gap where 3.24 m of
                    // air existed -- and it sent the demo camera to a spot with 0.2 m of daylight, which
                    // photographed as "no change" and nearly got the fix called a no-op. Name what it is OF.
                    float drop = (pts[i].Y + KerbHeight) - probe(outer.X, outer.Z);   // surface above real ground
                    float gap = drop - (KerbHeight + Skirt);                          // the air actually under the slab
                    if (gap > worst) { worst = gap; worstPt = pts[i]; }
                    // ...and separately, the most PHOTOGENIC one: closest to FloatTarget, where the slab hangs
                    // by about a person's knee height rather than by a cliff.
                    // ⚠ AND IT HAS TO BE SOMEWHERE YOU COULD STAND. Five framings of this came back with the
                    // lens buried in a bank or the slab filling the frame: picking purely on the number put the
                    // camera inside the hillside the pavement was cut into. So require OPEN GROUND outside --
                    // the terrain 6 m and 14 m out must agree to within a couple of metres -- which is the same
                    // thing a person means by "a spot you can see it from".
                    var o6 = pts[i] + outward[i].Normalized() * 6f;
                    var o14 = pts[i] + outward[i].Normalized() * 14f;
                    bool open = Mathf.Abs(probe(o14.X, o14.Z) - probe(o6.X, o6.Z)) < 2.0f;
                    if (open && (PickAt == Vector3.Zero || Mathf.Abs(gap - FloatTarget) < Mathf.Abs(PickFloat - FloatTarget)))
                    { PickAt = pts[i]; PickOut = outward[i].Normalized(); PickFloat = gap; DemoWidth = Width; }
                    sampled++;
                }
            var mi = BuildRibbon(pts, outward, Width, KerbHeight, Skirt, name, ground, kerbs);
            Log.Print($"[sidewalk] {name}: worst DAYLIGHT under the slab {worst:0.00} m over {sampled} pts "
                    + $"(= drop below the surface minus the {KerbHeight + Skirt:0.00} m the kerb+skirt already covers)");
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
