using Godot;

namespace UnturnedGodot
{
    // The three window-barricade LOOKS (master 2026-09-01): planks nailed over the hole, a grille of vertical
    // metal bars, or one solid plate that covers the whole opening. Each is built PROCEDURALLY and SIZED TO THE
    // ACTUAL OPENING (w x h) at placement -- NOT a unit mesh stretched by a non-uniform node scale, which would
    // shear the haphazard planks and flatten the bars on a non-square window. Follows Deployable.BuildTurbine's
    // multi-part pattern: a root MeshInstance3D (surface 0 = the structure) with an optional accent child
    // (surface 1 = nails / rivets, a darker metal). Flat authoring frame: X = width, Y = thickness/depth,
    // Z = height; DeployableDef.StandBasis stands Z up into world +Y at spawn.
    public enum WindowBarricadeStyle { Planks, Bars, Plate }

    public static class WindowBarricadeMesh
    {
        // Build the panel for `style`, fitted to a `w` x `h` opening. `colliderSize` is the flat-frame box the
        // Deployable collider should take; `thickness` is the panel depth (drives the standoff off the wall face).
        public static MeshInstance3D Build(WindowBarricadeStyle style, float w, float h, out Vector3 colliderSize, out float thickness)
        {
            switch (style)
            {
                case WindowBarricadeStyle.Bars:  return BuildBars(w, h, out colliderSize, out thickness);
                case WindowBarricadeStyle.Plate: return BuildPlate(w, h, out colliderSize, out thickness);
                default:                         return BuildPlanks(w, h, out colliderSize, out thickness);
            }
        }

        // --- Planks: 3-6 horizontal boards nailed across the hole, each tilted/offset a little (a fixed, deterministic
        //     jitter -- NO RNG) so it reads as salvaged boards slapped on in a hurry; nail heads proud on both faces. ---
        static MeshInstance3D BuildPlanks(float w, float h, out Vector3 colliderSize, out float thickness)
        {
            const float t = 0.055f;         // board depth
            thickness = t + 0.03f;          // proud boards + nail heads -> a hair more standoff off the wall
            int n = Mathf.Clamp(Mathf.RoundToInt(h / 0.30f), 3, 5);
            float band = h / n;
            float boardH = band * 0.9f;     // chunky boards, small gaps between (dense coverage)
            float len = w * 1.08f;          // run past the opening sides (nailed onto the frame around the hole)
            // deterministic per-board jitter (index-keyed, so a re-place is byte-identical): a FEW degrees of wobble
            // (mostly horizontal -- boarded-up windows aren't zigzags), depth proudness, sideways slip, brightness tint.
            float[] tilt = { 2.2f, -1.6f, 2.8f, -2.0f, 1.4f };
            float[] dOff = { 0f, 0.014f, -0.006f, 0.010f, -0.004f };
            float[] xOff = { 0.02f, -0.03f, 0.015f, 0.035f, -0.02f };
            float[] tint = { 1.0f, 0.88f, 0.96f, 0.84f, 0.92f };

            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            var nails = new SurfaceTool(); nails.Begin(Mesh.PrimitiveType.Triangles);
            for (int i = 0; i < n; i++)
            {
                float z = -h * 0.5f + (i + 0.5f) * band;
                var rot = new Basis(Vector3.Up, Mathf.DegToRad(tilt[i]));   // tilt about the depth (Y) axis -> rotates in the X-Z face plane
                var c = new Vector3(xOff[i], dOff[i], z);
                float v = tint[i];
                st.SetColor(new Color(v, v, v));
                AddBox(st, c, new Vector3(len, t, boardH), rot);
                float endX = len * 0.5f - 0.10f;
                foreach (int end in new[] { -1, 1 })
                    foreach (int face in new[] { -1, 1 })
                        AddBox(nails, c + rot * new Vector3(end * endX, face * (t * 0.5f + 0.006f), 0f),
                               new Vector3(0.045f, 0.02f, 0.045f), rot);
            }
            colliderSize = new Vector3(len, thickness * 2f, h * 1.02f);
            var mi = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = WoodMat() };
            mi.AddChild(new MeshInstance3D { Mesh = nails.Commit(), MaterialOverride = DarkMetalMat() });
            return mi;
        }

        // --- Bars: a welded frame (top + bottom rail) with N vertical bars across the opening -- prison/security
        //     bars. Tough but there are GAPS (mid HP tier). Bar count scales with the opening width. ---
        static MeshInstance3D BuildBars(float w, float h, out Vector3 colliderSize, out float thickness)
        {
            const float t = 0.05f; thickness = t;
            const float barW = 0.035f, railH = 0.06f;
            int n = Mathf.Clamp(Mathf.RoundToInt(w / 0.20f), 3, 9);
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            AddBox(st, new Vector3(0f, 0f,  h * 0.5f - railH * 0.5f), new Vector3(w * 1.02f, t, railH), Basis.Identity);   // top rail
            AddBox(st, new Vector3(0f, 0f, -h * 0.5f + railH * 0.5f), new Vector3(w * 1.02f, t, railH), Basis.Identity);   // bottom rail
            float span = w - barW;   // keep the outermost bars inside the frame
            for (int i = 0; i < n; i++)
            {
                float fx = n == 1 ? 0.5f : i / (float)(n - 1);
                float x = -span * 0.5f + fx * span;
                AddBox(st, new Vector3(x, 0f, 0f), new Vector3(barW, t * 0.9f, h), Basis.Identity);
            }
            colliderSize = new Vector3(w * 1.02f, t, h);
            return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = BarMetalMat() };
        }

        // --- Plate: one solid slab covering the whole opening, with riveted studs round the border on both faces.
        //     Full coverage, no gaps -> the tankiest (top HP tier). ---
        static MeshInstance3D BuildPlate(float w, float h, out Vector3 colliderSize, out float thickness)
        {
            const float t = 0.05f; thickness = t;
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            AddBox(st, Vector3.Zero, new Vector3(w * 1.03f, t, h * 1.03f), Basis.Identity);
            var rv = new SurfaceTool(); rv.Begin(Mesh.PrimitiveType.Triangles);
            float mx = w * 0.5f - 0.08f, mz = h * 0.5f - 0.08f;
            Vector3[] pts = { new(-mx, 0f, -mz), new(mx, 0f, -mz), new(-mx, 0f, mz), new(mx, 0f, mz),
                              new(0f, 0f, -mz), new(0f, 0f, mz), new(-mx, 0f, 0f), new(mx, 0f, 0f) };
            foreach (var p in pts)
                foreach (int face in new[] { -1, 1 })
                    AddBox(rv, new Vector3(p.X, face * (t * 0.5f + 0.006f), p.Z), new Vector3(0.05f, 0.02f, 0.05f), Basis.Identity);
            colliderSize = new Vector3(w * 1.03f, t, h * 1.03f);
            var mi = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = PlateMetalMat() };
            mi.AddChild(new MeshInstance3D { Mesh = rv.Commit(), MaterialOverride = DarkMetalMat() });
            return mi;
        }

        // --- materials: double-sided (CullMode.Disabled, like every other deployable) so winding never hides a face;
        //     normals are authored per-face so lighting is correct regardless. ---
        static StandardMaterial3D WoodMat() => new()
        {
            AlbedoColor = new Color(0.55f, 0.39f, 0.24f), Roughness = 0.92f, Metallic = 0f,
            VertexColorUseAsAlbedo = true,   // per-board tint (SetColor) modulates the base wood -> no two boards match
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        static StandardMaterial3D BarMetalMat() => new()
        {
            AlbedoColor = new Color(0.32f, 0.33f, 0.36f), Roughness = 0.4f, Metallic = 0.6f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        static StandardMaterial3D PlateMetalMat() => new()
        {
            AlbedoColor = new Color(0.50f, 0.51f, 0.54f), Roughness = 0.55f, Metallic = 0.3f,   // low metallic: reads as gray steel under a plain color env (a high-metal flat plate goes near-black)
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        static StandardMaterial3D DarkMetalMat() => new()
        {
            AlbedoColor = new Color(0.13f, 0.13f, 0.14f), Roughness = 0.5f, Metallic = 0.7f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        // Append an axis-aligned box (rotated by `rot` about `c`) to the SurfaceTool, 6 faces, outward normals.
        static void AddBox(SurfaceTool st, Vector3 c, Vector3 size, Basis rot)
        {
            Vector3 h = size * 0.5f;
            Face(st, c, rot, new Vector3(h.X, -h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(h.X, -h.Y, h.Z), Vector3.Right);
            Face(st, c, rot, new Vector3(-h.X, -h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z), new Vector3(-h.X, h.Y, -h.Z), new Vector3(-h.X, -h.Y, -h.Z), Vector3.Left);
            Face(st, c, rot, new Vector3(-h.X, h.Y, -h.Z), new Vector3(-h.X, h.Y, h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(h.X, h.Y, -h.Z), Vector3.Up);
            Face(st, c, rot, new Vector3(-h.X, -h.Y, h.Z), new Vector3(-h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, h.Z), Vector3.Down);
            Face(st, c, rot, new Vector3(-h.X, -h.Y, h.Z), new Vector3(h.X, -h.Y, h.Z), new Vector3(h.X, h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z), Vector3.Back);
            Face(st, c, rot, new Vector3(h.X, -h.Y, -h.Z), new Vector3(-h.X, -h.Y, -h.Z), new Vector3(-h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z), Vector3.Forward);
        }

        // One quad (a,b,c,d CCW from outside) as two triangles, with a shared outward normal.
        static void Face(SurfaceTool st, Vector3 c, Basis rot, Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 n)
        {
            Vector3 wn = (rot * n).Normalized();
            Vector3 A = c + rot * a, B = c + rot * b, C = c + rot * cc, D = c + rot * d;
            V(st, A, wn, new Vector2(0f, 0f)); V(st, B, wn, new Vector2(1f, 0f)); V(st, C, wn, new Vector2(1f, 1f));
            V(st, A, wn, new Vector2(0f, 0f)); V(st, C, wn, new Vector2(1f, 1f)); V(st, D, wn, new Vector2(0f, 1f));
        }

        static void V(SurfaceTool st, Vector3 p, Vector3 n, Vector2 uv)
        {
            st.SetNormal(n); st.SetUV(uv); st.AddVertex(p);
        }
    }
}
