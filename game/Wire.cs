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
                _segs[i].Visible = true;
                Vector3 a = pts[i], b = pts[i + 1], dir = b - a;
                float len = dir.Length();
                if (len < 1e-4f) { _segs[i].Visible = false; continue; }
                _segs[i].GlobalTransform = new Transform3D(AlignY(dir / len).Scaled(new Vector3(1f, len, 1f)), (a + b) * 0.5f);   // cylinder's +Y axis is its length
            }
        }

        public float TotalLength()
        {
            float s = 0f;
            for (int i = 0; i + 1 < Points.Count; i++) s += Points[i].DistanceTo(Points[i + 1]);
            return s;
        }

        // basis whose +Y points along `y` (unit); the cylinder mesh is authored along +Y
        static Basis AlignY(Vector3 y)
        {
            Vector3 x = Mathf.Abs(y.Dot(Vector3.Right)) < 0.95f ? y.Cross(Vector3.Right).Normalized() : y.Cross(Vector3.Forward).Normalized();
            Vector3 z = x.Cross(y).Normalized();
            return new Basis(x, y, z);
        }
    }
}
