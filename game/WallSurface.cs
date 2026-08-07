using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot
{
    // One wall: a rectangle plus a list of openings, materialised into boxes.
    //
    // The wall is GENERATED from its data, never cut. Rebuild() is called on every change -- including every
    // frame of a drag -- because a wall is a handful of boxes and regenerating is cheaper than reconciling.
    // That is what removes the bake step: the geometry you drag IS the final geometry, so a preview can never
    // disagree with a result. (Preview/result divergence is not hypothetical in this repo -- a barricade ghost
    // once lay on its side while the placed object stood upright, because the two had drifted apart.)
    //
    // The mesh node and the body are created ONCE and reused, so the Rid stays stable for picking and Jolt
    // sees shape swaps rather than body churn.
    public partial class WallSurface : Node3D
    {
        public float Length = 6f;                       // along local +X
        public float Height = WallOpenings.DoorHeight;  // along local +Y
        public float Thickness = WallOpenings.DefaultThickness;   // 0.70 exterior, 0.50 for partitions
        public readonly List<WallOpening> Openings = new();

        /// <summary>Which retail palette this wall wears. A "material" on these buildings is nothing but a
        /// palette -- there are no textures, only eight flat colours per model -- so the editor picks an id and
        /// the wall and its reveal take two MEASURED texels from it.
        ///
        /// The reveal IS a contrasting frame, and obviously so once the texture is sampled the way the engine
        /// samples it: Post_0 is orange trim on grey, Fire_0 white on red, Police_0 blue on tan. An earlier
        /// pass concluded the opposite because it read the palette without the V-flip that ObjMesh.cs applies
        /// to these same textures, which lands one row low -- every building came back a shade of brown.</summary>
        public int MaterialId;

        /// <summary>What this surface is for. Defaults and labels only -- Rebuild() never reads it. A floor is
        /// this same rectangle pitched flat, which is why there is no FloorSurface: the partition, the
        /// collider, the reveal lining and the bake all work already, and a stairwell is an opening.</summary>
        public SurfaceKind Kind = SurfaceKind.Wall;
        /// <summary>Paint this surface in a specific palette texel instead of the palette's wall colour.
        /// -1 (the default) means "the wall colour". One retail building is one PALETTE, not one colour.</summary>
        public int Texel = -1;
        public Color Tint => Texel >= 0 && Texel < 8 ? WallMaterials.At(MaterialId).Texels[Texel]
                                                     : WallMaterials.At(MaterialId).Wall;
        public Color TrimTint => WallMaterials.At(MaterialId).Reveal;
        public bool ShowTrim = true;

        /// <summary>How far this wall's top rises to a central peak, for a gable end. 0 = a flat top.
        ///
        /// ADDITIVE: the wall stays a rectangle and the partition never sees this, because a gable end really
        /// is a normal wall with a triangle sitting on it -- that is how retail builds them, and it keeps the
        /// one boundary shape the whole tool relies on. Making the boundary a pentagon instead would put a
        /// special case through Solids, the collider and every test that leans on them.</summary>
        public float GableRise;

        /// <summary>Trapezoid edges: how far this surface is set in from its left and right sides, at the
        /// BASE (…0) and at the TOP (…1), straight-line between. All zero -- the default, and every wall,
        /// floor and rectangular roof -- takes the plain box path below untouched.
        ///
        /// This exists because a cross-wing roof slope is not a rectangle. On House_00 the two 14-degree
        /// planes are trapezoids at 0.77 fill: one edge runs 5.10 m in at the eave to 0.10 m at the ridge,
        /// dead linear, which is the valley where the wing meets the main roof. Emitted as their bounding
        /// rectangles they overshot that valley by a quarter of their area each. A hip end is the same
        /// primitive with both top insets meeting.</summary>
        public float InsetL0, InsetL1, InsetR0, InsetR1;
        public bool Tapered => InsetL0 > 0.02f || InsetL1 > 0.02f || InsetR0 > 0.02f || InsetR1 > 0.02f;

        /// <summary>Trim sits proud of BOTH faces and never scales with the opening -- widen a garage and the
        /// jambs move apart at constant thickness. Scaling the frame with the hole is what makes a parametric
        /// editor look like a stretched sprite.</summary>
        public const float TrimProfile = WallOpenings.TrimProfile;   // 0.20, retail-measured
        public const float TrimProud = 0.035f;                       // how far the bar stands off each face

        MeshInstance3D _mesh, _trimMesh;
        // Materials are made ONCE and recoloured. Rebuild() runs every frame of a drag, so allocating a
        // StandardMaterial3D per call hands the GC two new resources per wall per frame for a colour that
        // almost never changes.
        StandardMaterial3D _mat, _trimMat;
        StaticBody3D _body;          // wall solids: layer 0, the layer player movement collides against
        StaticBody3D _trimBody;      // trim: layer 6 (props) -- bullets and look-rays hit it, movement does not,
                                     // so a doorframe is shootable without snagging you on every doorway
        readonly List<CollisionShape3D> _shapes = new();
        readonly List<CollisionShape3D> _trimShapes = new();

        public override void _Ready()
        {
            _mesh = new MeshInstance3D { Name = "Mesh" };
            AddChild(_mesh);
            _trimMesh = new MeshInstance3D { Name = "TrimMesh" };
            AddChild(_trimMesh);
            _body = new StaticBody3D { Name = "Solids", CollisionLayer = 1u << 0, CollisionMask = 0 };
            AddChild(_body);
            _trimBody = new StaticBody3D { Name = "Trim", CollisionLayer = 1u << 6, CollisionMask = 0 };
            AddChild(_trimBody);
            Rebuild();
        }

        public Rid BodyRid => _body != null ? _body.GetRid() : default;

        /// <summary>Regenerate mesh + collision from the current data. Safe to call every frame.</summary>
        public void Rebuild()
        {
            if (_mesh == null) return;
            var solids = WallOpenings.Solids(Length, Height, Openings);

            // Two meshes, two materials -- walls and trim are genuinely different surfaces, and a plain
            // AlbedoColor is what the rest of the repo uses. (Vertex colours needed the material to opt in and
            // silently rendered everything white, which is a bad way to find out your trim is invisible.)
            float t = Thickness * 0.5f;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetSmoothGroup(uint.MaxValue);     // = flat. See AddTrim's note; a box wants creased corners.
            foreach (var s in solids)
                if (Tapered) AddTaperedSolid(st, s, t);
                else AddWallBox(st, s, solids, t);
            // GenerateNormals BEFORE Index: indexing welds vertices, and welding before normals exist lights
            // the mesh as one smooth blob instead of crisp box faces.
            if (GableRise > WallOpenings.Eps) AddGableCap(st, t);
            st.GenerateNormals();
            st.Index();
            _mesh.Mesh = st.Commit();
            _mat ??= new StandardMaterial3D { Roughness = 0.95f };
            _mat.AlbedoColor = Tint;
            _mesh.MaterialOverride = _mat;

            if (ShowTrim && Openings.Count > 0)
            {
                var tt = new SurfaceTool();
                tt.Begin(Mesh.PrimitiveType.Triangles);
                // FLAT, explicitly. SurfaceTool's default smooth group averages the normals of every face
                // meeting at a position, so an indexed pile of boxes lights as one rounded shell: the jamb of
                // a window bulges and necks like a turned spindle. On a 0.20 bar that is not subtle, and it
                // survives a shadows-off render, which is what rules out the obvious suspect.
                tt.SetSmoothGroup(uint.MaxValue);
                foreach (var o in Openings) AddTrim(tt, o);
                tt.GenerateNormals();
                tt.Index();
                _trimMesh.Mesh = tt.Commit();
                _trimMat ??= new StandardMaterial3D { Roughness = 0.9f };
                _trimMat.AlbedoColor = TrimTint;
                _trimMesh.MaterialOverride = _trimMat;
            }
            else _trimMesh.Mesh = null;

            // collision: one box per solid. Because the solids ARE the partition, the hole in the collider is
            // exactly the hole you can see -- the see-through-but-not-walk-through class of bug is impossible.
            // Reused, not respawned. QueueFree defers to the end of the frame, so freeing and re-adding every
            // shape each Rebuild leaves a drag running with two sets of colliders live at once -- and the
            // stale set is what a ray can still hit for the rest of that frame.
            int want = solids.Count + (GableRise > WallOpenings.Eps ? 1 : 0);
            while (_shapes.Count > want)
            {
                var last = _shapes[_shapes.Count - 1];
                _shapes.RemoveAt(_shapes.Count - 1);
                last.QueueFree();
            }
            while (_shapes.Count < want)
            {
                var cs = new CollisionShape3D();
                _body.AddChild(cs);
                _shapes.Add(cs);
            }
            for (int i = 0; i < solids.Count; i++)
            {
                var s = solids[i];
                if (!Tapered)
                {
                    if (_shapes[i].Shape is not BoxShape3D box) _shapes[i].Shape = box = new BoxShape3D();
                    box.Size = new Vector3(s.Width, s.Height, Thickness);
                    _shapes[i].Position = new Vector3((s.U0 + s.U1) * 0.5f, (s.V0 + s.V1) * 0.5f, 0f);
                    continue;
                }
                // A box around a trapezoid is solid where the mesh is not, which is the see-through-but-
                // not-walk-through bug this partition exists to make impossible. Same polygon as the mesh.
                var poly = ClipToTaper(s);
                if (poly.Count < 3) { _shapes[i].Shape = null; continue; }
                var pts = new Vector3[poly.Count * 2];
                for (int k = 0; k < poly.Count; k++)
                {
                    pts[k] = new Vector3(poly[k].X, poly[k].Y, -Thickness * 0.5f);
                    pts[k + poly.Count] = new Vector3(poly[k].X, poly[k].Y, Thickness * 0.5f);
                }
                if (_shapes[i].Shape is not ConvexPolygonShape3D hull) _shapes[i].Shape = hull = new ConvexPolygonShape3D();
                hull.Points = pts;
                _shapes[i].Position = Vector3.Zero;     // the points are already in surface space
            }
            RebuildTrimCollision();

            if (GableRise > WallOpenings.Eps)
            {
                // A convex hull of the prism's six corners, NOT a box: a box round a gable fills the two
                // triangles of air beside the peak, and you would collide with a roof corner that is not there.
                float t2 = Thickness * 0.5f;
                var gcs = _shapes[solids.Count];
                if (gcs.Shape is not ConvexPolygonShape3D hull) gcs.Shape = hull = new ConvexPolygonShape3D();
                hull.Points = new[]
                {
                    new Vector3(0f, Height, -t2), new Vector3(Length, Height, -t2), new Vector3(Length * 0.5f, Height + GableRise, -t2),
                    new Vector3(0f, Height, t2), new Vector3(Length, Height, t2), new Vector3(Length * 0.5f, Height + GableRise, t2),
                };
                gcs.Position = Vector3.Zero;
            }
        }

        /// <summary>The triangular prism that turns a flat-topped wall into a gable end: apex over the middle
        /// of the run, base along the wall's head. Emitted as its own faces rather than by reshaping the wall,
        /// so nothing downstream has to know a wall can be non-rectangular.</summary>
        void AddGableCap(SurfaceTool st, float t)
        {
            float x0 = 0f, x1 = Length, mid = Length * 0.5f;
            float y0 = Height, y1 = Height + GableRise;
            Vector3 A = new(x0, y0, -t), B = new(x1, y0, -t), P = new(mid, y1, -t);   // back face
            Vector3 C = new(x0, y0, t), D = new(x1, y0, t), Q = new(mid, y1, t);      // front face

            // Godot treats CLOCKWISE as front-facing, so each face is emitted in the order that reads
            // anticlockwise from outside -- the same reversal AddBoxFaces does, for the same reason.
            Tri(st, P, B, A);        // -Z gable triangle
            Tri(st, C, D, Q);        // +Z gable triangle
            Quad(st, A, C, Q, P);    // left slope
            Quad(st, P, Q, D, B);    // right slope
            // no bottom face: it sits flush on the wall head and would z-fight the wall's own top
        }

        static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
        { st.AddVertex(a); st.AddVertex(b); st.AddVertex(c); }

        static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        { Tri(st, a, b, c); Tri(st, a, c, d); }

        /// <summary>Colliders for the frames, on layer 6.
        ///
        /// _trimBody existed and was documented as "a doorframe is shootable without snagging you on every
        /// doorway" -- and nothing ever added a shape to it, so it was a dead node and editor-drawn frames
        /// were not shootable at all. Worse, a BAKED building's frames are solid (the prop path trimeshes the
        /// whole render mesh), so the same building behaved differently before and after baking.
        ///
        /// Layer 6 is the props layer: bullets and look-rays hit it, player movement does not.</summary>
        void RebuildTrimCollision()
        {
            var boxes = new List<(Vector3 Min, Vector3 Max)>();
            if (ShowTrim)
                foreach (var o in Openings)
                {
                    float t = Thickness * 0.5f + TrimProud, w = TrimProfile;
                    float u0 = o.U, u1 = o.U1, v0 = o.V, v1 = o.V1;
                    bool sill = o.V > WallOpenings.Eps;
                    float vb = sill ? v0 : v0;
                    boxes.Add((new Vector3(u0, vb, -t), new Vector3(u0 + w, v1, t)));
                    boxes.Add((new Vector3(u1 - w, vb, -t), new Vector3(u1, v1, t)));
                    boxes.Add((new Vector3(u0, v1 - w, -t), new Vector3(u1, v1, t)));
                    if (sill) boxes.Add((new Vector3(u0, v0, -t), new Vector3(u1, v0 + w, t)));
                }

            while (_trimShapes.Count > boxes.Count)
            {
                var last = _trimShapes[_trimShapes.Count - 1];
                _trimShapes.RemoveAt(_trimShapes.Count - 1);
                last.QueueFree();
            }
            while (_trimShapes.Count < boxes.Count)
            {
                var cs = new CollisionShape3D();
                _trimBody.AddChild(cs);
                _trimShapes.Add(cs);
            }
            for (int i = 0; i < boxes.Count; i++)
            {
                var (mn, mx) = boxes[i];
                if (_trimShapes[i].Shape is not BoxShape3D b) _trimShapes[i].Shape = b = new BoxShape3D();
                b.Size = mx - mn;
                _trimShapes[i].Position = (mn + mx) * 0.5f;
            }
        }

        void AddTrim(SurfaceTool st, WallOpening o)
        {
            // The frame LINES THE REVEAL -- it sits inside the hole spanning the wall thickness, not as a bar
            // on the face. That is what retail does: the dominant loose panel in every building measured is a
            // strip the length of an opening edge by the wall thickness (0.70), i.e. a reveal lining.
            //
            // A bar on the face leaves the wall's own cut faces exposed inside the frame -- a pale band on all
            // four sides of every opening, which is exactly what it looked like.
            // Every lining is grown by BURY past the hole edge so it INTERPENETRATES the wall, and the four
            // linings run edge to edge so they interpenetrate each other at the corners. Sized to meet exactly
            // instead, each lining's outer face lands on the wall's jamb face at the same depth -- coplanar
            // duplicates, which z-fight into a bowtie down the middle of the jamb that reads as broken frame
            // geometry. Overlap is free here: the surfaces that intersect are buried, and the two meshes are
            // one flat colour each, so there is nothing for the seam to show.
            float t = Thickness * 0.5f + TrimProud, w = TrimProfile;
            const float BURY = 0.01f;
            float u0 = o.U, u1 = o.U1, v0 = o.V, v1 = o.V1;
            bool sill = o.V > WallOpenings.Eps;                    // floor-pinned openings have none
            float vb = sill ? v0 - BURY : v0;
            AddBox(st, new Vector3(u0 - BURY, vb, -t), new Vector3(u0 + w, v1 + BURY, t));      // left lining
            AddBox(st, new Vector3(u1 - w, vb, -t), new Vector3(u1 + BURY, v1 + BURY, t));      // right lining
            AddBox(st, new Vector3(u0 - BURY, v1 - w, -t), new Vector3(u1 + BURY, v1 + BURY, t)); // head
            if (sill)
                AddBox(st, new Vector3(u0 - BURY, v0 - BURY, -t), new Vector3(u1 + BURY, v0 + w, t));
        }

        /// <summary>The left and right cut lines, as u for a given v.</summary>
        float CutL(float v) => Mathf.Lerp(InsetL0, InsetL1, Height > WallOpenings.Eps ? v / Height : 0f);
        float CutR(float v) => Length - Mathf.Lerp(InsetR0, InsetR1, Height > WallOpenings.Eps ? v / Height : 0f);

        /// <summary>One solid of the partition, clipped to the trapezoid and extruded.
        ///
        /// Kept entirely separate from AddWallBox rather than generalising it: the box path runs for every
        /// wall in the game and knows which faces to omit where solids abut, and none of that needed to
        /// change to put a slanted edge on a roof.</summary>
        void AddTaperedSolid(SurfaceTool st, WallSolid s, float t)
        {
            var poly = ClipToTaper(s);
            if (poly.Count < 3) return;
            for (int i = 1; i + 1 < poly.Count; i++)          // +Z face
            {
                Tri(st, new Vector3(poly[0].X, poly[0].Y, t), new Vector3(poly[i].X, poly[i].Y, t),
                        new Vector3(poly[i + 1].X, poly[i + 1].Y, t));
                Tri(st, new Vector3(poly[0].X, poly[0].Y, -t), new Vector3(poly[i + 1].X, poly[i + 1].Y, -t),
                        new Vector3(poly[i].X, poly[i].Y, -t));
            }
            for (int i = 0; i < poly.Count; i++)              // the rim
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                Quad(st, new Vector3(a.X, a.Y, -t), new Vector3(b.X, b.Y, -t),
                         new Vector3(b.X, b.Y, t), new Vector3(a.X, a.Y, t));
            }
        }

        /// <summary>A solid rectangle clipped by the two cut lines. Sutherland-Hodgman against two
        /// half-planes; the result is convex, so a fan triangulates it and a convex hull collides it.</summary>
        List<Vector2> ClipToTaper(WallSolid s)
        {
            var poly = new List<Vector2>
            {
                new(s.U0, s.V0), new(s.U1, s.V0), new(s.U1, s.V1), new(s.U0, s.V1),
            };
            // keep u >= CutL(v), then u <= CutR(v)
            poly = ClipHalfPlane(poly, p => p.X - CutL(p.Y));
            poly = ClipHalfPlane(poly, p => CutR(p.Y) - p.X);
            return poly;
        }

        static List<Vector2> ClipHalfPlane(List<Vector2> poly, System.Func<Vector2, float> keep)
        {
            var outp = new List<Vector2>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
                float da = keep(a), db = keep(b);
                if (da >= 0f) outp.Add(a);
                if ((da >= 0f) != (db >= 0f))
                {
                    float f = da / (da - db);
                    if (float.IsFinite(f)) outp.Add(a.Lerp(b, Mathf.Clamp(f, 0f, 1f)));
                }
            }
            return outp;
        }

        static void AddWallBox(SurfaceTool st, WallSolid s, List<WallSolid> all, float t)
        {
            bool left = !Abuts(all, s, -1, 0), right = !Abuts(all, s, 1, 0);
            bool down = !Abuts(all, s, 0, -1), up = !Abuts(all, s, 0, 1);
            AddBoxFaces(st, new Vector3(s.U0, s.V0, -t), new Vector3(s.U1, s.V1, t),
                        front: true, back: true, minU: left, maxU: right, minV: down, maxV: up);
        }

        /// <summary>Is another solid flush against this side, covering it completely?</summary>
        static bool Abuts(List<WallSolid> all, WallSolid s, int du, int dv)
        {
            const float E = 1e-3f;
            foreach (var o in all)
            {
                if (du != 0)
                {
                    float mine = du < 0 ? s.U0 : s.U1, theirs = du < 0 ? o.U1 : o.U0;
                    if (Mathf.Abs(mine - theirs) > E) continue;
                    if (o.V0 <= s.V0 + E && o.V1 >= s.V1 - E) return true;
                }
                else
                {
                    float mine = dv < 0 ? s.V0 : s.V1, theirs = dv < 0 ? o.V1 : o.V0;
                    if (Mathf.Abs(mine - theirs) > E) continue;
                    if (o.U0 <= s.U0 + E && o.U1 >= s.U1 - E) return true;
                }
            }
            return false;
        }

        static void AddBox(SurfaceTool st, Vector3 a, Vector3 b)
            => AddBoxFaces(st, a, b, true, true, true, true, true, true);

        static void AddBoxFaces(SurfaceTool st, Vector3 a, Vector3 b,
                                bool front, bool back, bool minU, bool maxU, bool minV, bool maxV)
        {
            Vector3[] v =
            {
                new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z),
                new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z),
            };
            var tris = new List<int[]>();
            if (back)  { tris.Add(new[]{0,3,2}); tris.Add(new[]{0,2,1}); }   // -Z
            if (front) { tris.Add(new[]{4,5,6}); tris.Add(new[]{4,6,7}); }   // +Z
            if (minU)  { tris.Add(new[]{0,4,7}); tris.Add(new[]{0,7,3}); }   // -X
            if (maxU)  { tris.Add(new[]{1,2,6}); tris.Add(new[]{1,6,5}); }   // +X
            if (minV)  { tris.Add(new[]{0,1,5}); tris.Add(new[]{0,5,4}); }   // -Y
            if (maxV)  { tris.Add(new[]{3,7,6}); tris.Add(new[]{3,6,2}); }   // +Y
            // Godot treats CLOCKWISE as front-facing. The index table below is wound counter-clockwise-outward
            // (right-hand rule, outward normals), so emit each triangle REVERSED -- otherwise every face is
            // culled when seen from outside and lit from within, which reads as the whole thing being inside out.
            foreach (var tri in tris)
                for (int k = 2; k >= 0; k--)
                    st.AddVertex(v[tri[k]]);
        }

        // ---- wall space <-> world -------------------------------------------------------------------
        // ONE projection pair, used by every caller. A second copy that disagrees on the sign of U is the
        // mirror bug that makes openings jump when the camera crosses the wall.

        public Vector3 UVToWorld(float u, float v) => ToGlobal(new Vector3(u, v, 0f));

        public bool WorldToUV(Vector3 world, out float u, out float v)
        {
            var l = ToLocal(world);
            u = l.X; v = l.Y;
            return u >= -WallOpenings.Eps && u <= Length + WallOpenings.Eps
                && v >= -WallOpenings.Eps && v <= Height + WallOpenings.Eps;
        }

        /// <summary>Where a camera ray meets this wall's plane, in wall space. Takes an explicit ray so it is
        /// testable without a camera or a mouse.</summary>
        public bool RayToUV(Vector3 from, Vector3 dir, out float u, out float v)
        {
            u = v = 0f;
            Vector3 n = GlobalTransform.Basis.Z.Normalized();
            float denom = n.Dot(dir);
            if (Mathf.Abs(denom) < 1e-6f) return false;
            float dist = n.Dot(GlobalPosition - from) / denom;
            if (dist < 0f) return false;
            var hit = from + dir * dist;
            WorldToUV(hit, out u, out v);
            return true;
        }

        public int OpeningAt(float u, float v)
        {
            for (int i = 0; i < Openings.Count; i++)
            {
                var o = Openings[i];
                if (u >= o.U && u <= o.U1 && v >= o.V && v <= o.V1) return i;
            }
            return -1;
        }
    }
}
