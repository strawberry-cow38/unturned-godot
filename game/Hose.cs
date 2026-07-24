using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A hose connecting two fluid ports (mirror of Wire): a Source port -> a Consumer port, drawn as a polyline of
    // thicker cylinder segments (a hose is fatter than a wire). Used both as the live preview while routing (last point
    // = the look-point free end) and as the committed hose. FluidNet reads Source/Consumer to build the flow graph.
    // Type-lock ("cannot mix fluids") is enforced when a hose is CREATED via the tool; a programmatic/demo hose trusts
    // the caller. Non-interactive once placed — you clear it by poking its ports (mirror of the wire tool).
    public partial class Hose : Node3D
    {
        public FluidPortNode Source, Consumer;
        public List<Vector3> Points = new();
        const float Radius = 0.045f;   // fatter than a wire (0.018) — reads as a hose
        static readonly Color OkColor = new Color(0.12f, 0.13f, 0.15f);   // dark rubber hose
        static readonly Color BadColor = new Color(0.90f, 0.20f, 0.15f);  // over-limit / mismatch

        readonly List<MeshInstance3D> _segs = new();
        StandardMaterial3D _mat;

        public override void _Ready()
        {
            AddToGroup("hoses");
            _mat = new StandardMaterial3D { AlbedoColor = OkColor, Metallic = 0.1f, Roughness = 0.7f };
        }

        public void SetPoints(List<Vector3> pts, bool valid)
        {
            Points = pts;
            _mat ??= new StandardMaterial3D { AlbedoColor = OkColor, Metallic = 0.1f, Roughness = 0.7f };
            _mat.AlbedoColor = valid ? OkColor : BadColor;
            int segCount = Mathf.Max(0, pts.Count - 1);
            while (_segs.Count < segCount)   // grow the segment pool
            {
                var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius, BottomRadius = Radius, Height = 1f, RadialSegments = 8 }, MaterialOverride = _mat, TopLevel = true };
                AddChild(mi); _segs.Add(mi);
            }
            for (int i = 0; i < _segs.Count; i++)
            {
                if (i >= segCount) { _segs[i].Visible = false; continue; }
                Vector3 a = pts[i], b = pts[i + 1], dir = b - a;
                float len = dir.Length();
                if (len < 1e-4f) { _segs[i].Visible = false; continue; }
                _segs[i].Visible = true;
                _segs[i].GlobalTransform = new Transform3D(RotateYTo(dir / len) * Basis.FromScale(new Vector3(1f, len, 1f)), (a + b) * 0.5f);
            }
        }

        public float TotalLength()
        {
            float s = 0f;
            for (int i = 0; i + 1 < Points.Count; i++) s += Points[i].DistanceTo(Points[i + 1]);
            return s;
        }

        // orthonormal rotation mapping the mesh's +Y axis onto the unit direction `u` (axis-angle, unambiguous)
        static Basis RotateYTo(Vector3 u)
        {
            float d = Vector3.Up.Dot(u);
            if (d > 0.9999f) return Basis.Identity;
            if (d < -0.9999f) return new Basis(Vector3.Right, Mathf.Pi);
            return new Basis(Vector3.Up.Cross(u).Normalized(), Mathf.Acos(Mathf.Clamp(d, -1f, 1f)));
        }
    }
}
