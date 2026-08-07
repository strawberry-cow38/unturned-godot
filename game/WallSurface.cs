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
        public Color Tint => WallMaterials.At(MaterialId).Wall;
        public Color TrimTint => WallMaterials.At(MaterialId).Reveal;
        public bool ShowTrim = true;

        /// <summary>Trim sits proud of BOTH faces and never scales with the opening -- widen a garage and the
        /// jambs move apart at constant thickness. Scaling the frame with the hole is what makes a parametric
        /// editor look like a stretched sprite.</summary>
        public const float TrimProfile = WallOpenings.TrimProfile;   // 0.20, retail-measured
        public const float TrimProud = 0.035f;                       // how far the bar stands off each face

        MeshInstance3D _mesh, _trimMesh;
        StaticBody3D _body;          // wall solids: layer 0, the layer player movement collides against
        StaticBody3D _trimBody;      // trim: layer 6 (props) -- bullets and look-rays hit it, movement does not,
                                     // so a doorframe is shootable without snagging you on every doorway
        readonly List<CollisionShape3D> _shapes = new();

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
                AddWallBox(st, s, solids, t);
            // GenerateNormals BEFORE Index: indexing welds vertices, and welding before normals exist lights
            // the mesh as one smooth blob instead of crisp box faces.
            st.GenerateNormals();
            st.Index();
            _mesh.Mesh = st.Commit();
            _mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = Tint, Roughness = 0.95f };

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
                _trimMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = TrimTint, Roughness = 0.9f };
            }
            else _trimMesh.Mesh = null;

            // collision: one box per solid. Because the solids ARE the partition, the hole in the collider is
            // exactly the hole you can see -- the see-through-but-not-walk-through class of bug is impossible.
            foreach (var sh in _shapes) { sh.QueueFree(); }
            _shapes.Clear();
            foreach (var s in solids)
            {
                var cs = new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = new Vector3(s.Width, s.Height, Thickness) },
                    Position = new Vector3((s.U0 + s.U1) * 0.5f, (s.V0 + s.V1) * 0.5f, 0f),
                };
                _body.AddChild(cs);
                _shapes.Add(cs);
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
