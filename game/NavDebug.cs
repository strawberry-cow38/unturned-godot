using Godot;

namespace UnturnedGodot
{
    // Debug VISUALS for the zombie nav rework (master's verify screenshot): a translucent floor overlay of a baked
    // navmesh, and a wireframe of a zombie's vision cone. Rendering-only -- no gameplay effect.
    public static class NavDebug
    {
        // Translucent colored overlay of a baked NavigationMesh (its polygons, fan-triangulated), lifted just above
        // the floor so it reads as "navmesh coverage" in a screenshot.
        public static MeshInstance3D NavmeshOverlay(NavigationMesh nm, Color color)
        {
            var verts = nm.GetVertices();
            int pc = nm.GetPolygonCount();
            if (verts.Length == 0 || pc == 0) return null;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int i = 0; i < pc; i++)
            {
                int[] poly = nm.GetPolygon(i);
                for (int k = 1; k + 1 < poly.Length; k++)   // triangle fan
                {
                    st.AddVertex(verts[poly[0]]);
                    st.AddVertex(verts[poly[k]]);
                    st.AddVertex(verts[poly[k + 1]]);
                }
            }
            var mat = new StandardMaterial3D
            {
                AlbedoColor = color,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                EmissionEnabled = true,
                Emission = new Color(color.R, color.G, color.B),
                EmissionEnergyMultiplier = 0.4f,   // glow so it reads over the terrain regardless of light
            };
            return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, Position = new Vector3(0f, 0.3f, 0f) };
        }

        // Wireframe of a vision cone: apex at the eye, axis along local -Z (forward), opening to halfAngle at range.
        // Child of the zombie -> rotates with its facing. (Phase 2's zombie sight; drawn here to verify it visually.)
        public static MeshInstance3D ConeWire(float range, float halfAngleDeg, Color color, float eyeY = 1.5f)
        {
            var im = new ImmediateMesh();
            var mat = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            const int seg = 28;
            float ha = Mathf.DegToRad(halfAngleDeg);
            var apex = new Vector3(0f, eyeY, 0f);
            var rim = new Vector3[seg];
            for (int i = 0; i < seg; i++)
            {
                float a = i / (float)seg * Mathf.Tau;
                var dir = new Vector3(Mathf.Cos(a) * Mathf.Sin(ha), Mathf.Sin(a) * Mathf.Sin(ha), -Mathf.Cos(ha));
                rim[i] = apex + dir * range;
            }
            im.SurfaceBegin(Mesh.PrimitiveType.Lines, mat);
            for (int i = 0; i < seg; i++)
            {
                if (i % 4 == 0) { im.SurfaceAddVertex(apex); im.SurfaceAddVertex(rim[i]); }   // a few apex spokes
                im.SurfaceAddVertex(rim[i]); im.SurfaceAddVertex(rim[(i + 1) % seg]);          // base rim loop
            }
            im.SurfaceEnd();
            return new MeshInstance3D { Mesh = im, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        }
    }
}
