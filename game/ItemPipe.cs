using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // An industrial ITEM pipe (v56, strawberry 2026-10-06): an Out item port -> an In item port, drawn along the
    // route the player clicked. Used both as the live preview while routing (last point = the free end) and as the
    // committed pipe the replica view builds from the server's PipeConnected fact.
    //
    // It has to read as a different thing from a wire (0.018 m flat cable) and a hose (0.045 m dark rubber), so it
    // is a fat (0.065 m) dull-steel tube with a ball joint at every bend and a flange collar where it meets a socket
    // -- plumbing, not cable. Non-interactive, like both of those: you manage it by poking its ports.
    public partial class ItemPipe : Node3D
    {
        public uint NetId;               // the server pipe this mirrors (0 = a local preview)
        public ItemPortNode Src, Dst;    // committed ends (null while previewing)
        public List<Vector3> Points = new();

        const float Radius = 0.065f, JointRadius = 0.085f, FlangeRadius = 0.095f, FlangeLen = 0.06f;
        static readonly Color SteelColor = new Color(0.33f, 0.34f, 0.36f);
        static readonly Color BadColor = new Color(0.90f, 0.20f, 0.15f);   // over the node/length budget, or no legal target

        readonly List<MeshInstance3D> _segs = new();
        readonly List<MeshInstance3D> _joints = new();
        MeshInstance3D _flangeA, _flangeB;
        StandardMaterial3D _mat;

        StandardMaterial3D Mat() => _mat ??= new StandardMaterial3D { AlbedoColor = SteelColor, Metallic = 0.85f, Roughness = 0.42f };

        public void SetPoints(List<Vector3> pts, bool valid)
        {
            Points = pts;
            Mat().AlbedoColor = valid ? SteelColor : BadColor;
            int segCount = Mathf.Max(0, pts.Count - 1);
            while (_segs.Count < segCount)
            {
                var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius, BottomRadius = Radius, Height = 1f, RadialSegments = 10 }, MaterialOverride = Mat(), TopLevel = true };
                AddChild(mi); _segs.Add(mi);
            }
            int jointCount = Mathf.Max(0, pts.Count - 2);   // one ball per interior node
            while (_joints.Count < jointCount)
            {
                var mi = new MeshInstance3D { Mesh = new SphereMesh { Radius = JointRadius, Height = JointRadius * 2f, RadialSegments = 10, Rings = 5 }, MaterialOverride = Mat(), TopLevel = true };
                AddChild(mi); _joints.Add(mi);
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
            for (int i = 0; i < _joints.Count; i++)
            {
                if (i >= jointCount) { _joints[i].Visible = false; continue; }
                _joints[i].Visible = true;
                _joints[i].GlobalTransform = new Transform3D(Basis.Identity, pts[i + 1]);
            }
            if (pts.Count >= 2)
            {
                _flangeA ??= MakeFlange();
                _flangeB ??= MakeFlange();
                PlaceFlange(_flangeA, pts[0], pts[1]);
                PlaceFlange(_flangeB, pts[pts.Count - 1], pts[pts.Count - 2]);
            }
        }

        MeshInstance3D MakeFlange()
        {
            var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = FlangeRadius, BottomRadius = FlangeRadius, Height = FlangeLen, RadialSegments = 12 }, MaterialOverride = Mat(), TopLevel = true };
            AddChild(mi);
            return mi;
        }

        static void PlaceFlange(MeshInstance3D f, Vector3 at, Vector3 toward)
        {
            Vector3 d = toward - at;
            if (d.LengthSquared() < 1e-8f) { f.Visible = false; return; }
            d = d.Normalized();
            f.Visible = true;
            f.GlobalTransform = new Transform3D(RotateYTo(d), at + d * (FlangeLen * 0.5f));
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
