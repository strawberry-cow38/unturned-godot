using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A power wire: a polyline of world points (source output -> node points -> consumer input), drawn as thin
    // cylinder segments. Used both as the live preview while wiring (last point = the look-point free end) and as
    // the committed wire once it terminates on a consumer. Power propagation (phase 4) + management (phase 5) read
    // Source/Consumer/Points off it.
    public partial class Wire : Node3D
    {
        public ConnectionPort Source, Consumer;
        public List<Vector3> Points = new();
        const float Radius = 0.018f;
        static readonly Color OkColor = new Color(0.55f, 0.35f, 0.12f);   // copper
        static readonly Color BadColor = new Color(0.90f, 0.20f, 0.15f);  // over-limit / invalid

        readonly List<MeshInstance3D> _segs = new();
        StandardMaterial3D _mat;

        public override void _Ready()
        {
            _mat = new StandardMaterial3D { AlbedoColor = OkColor, Metallic = 0.2f, Roughness = 0.6f };
        }

        public void SetPoints(List<Vector3> pts, bool valid)
        {
            Points = pts;
            if (_mat != null) _mat.AlbedoColor = valid ? OkColor : BadColor;
            int segCount = Mathf.Max(0, pts.Count - 1);
            while (_segs.Count < segCount)   // grow the segment pool
            {
                var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius, BottomRadius = Radius, Height = 1f, RadialSegments = 6 }, MaterialOverride = _mat, TopLevel = true };
                AddChild(mi); _segs.Add(mi);
            }
            for (int i = 0; i < _segs.Count; i++)
            {
                if (i >= segCount) { _segs[i].Visible = false; continue; }
                Vector3 a = pts[i], b = pts[i + 1], dir = b - a;
                float len = dir.Length();
                if (len < 1e-4f) { _segs[i].Visible = false; continue; }
                _segs[i].Visible = true;
                // rotate the cylinder's local +Y to point along the segment, THEN stretch its local Y to the length.
                // `rot * FromScale` scales in the cylinder's OWN frame (Basis.Scaled would scale in world axes -> wrong).
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
