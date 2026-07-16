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
        public const uint WireLayer = 1u << 9;   // committed wires collide here so the wire tool's look-ray can pick them (phase 5 manage)
        public ConnectionPort Source, Consumer;
        public List<Vector3> Points = new();
        const float Radius = 0.018f;
        static readonly Color OkColor = new Color(0.06f, 0.06f, 0.07f);   // very dark grey / black (strawberry)
        static readonly Color BadColor = new Color(0.90f, 0.20f, 0.15f);  // over-limit / invalid

        readonly List<MeshInstance3D> _segs = new();
        StandardMaterial3D _mat;
        StaticBody3D _body;   // look-select collider (built on commit); null while a wire is just a routing preview

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

        // Build (or rebuild) the look-select collider from the current Points -- a fatter capsule per segment on
        // WireLayer (easier to aim at than the hair-thin visual). Called when the wire is committed to a consumer.
        public void BuildInteractBody()
        {
            if (_body == null) { _body = new StaticBody3D { CollisionLayer = WireLayer, CollisionMask = 0 }; AddChild(_body); }
            foreach (var c in _body.GetChildren()) c.QueueFree();
            for (int i = 0; i + 1 < Points.Count; i++)
            {
                Vector3 a = Points[i], b = Points[i + 1], dir = b - a; float len = dir.Length();
                if (len < 1e-3f) continue;
                var cs = new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.07f, Height = len } };   // capsule axis = local Y
                _body.AddChild(cs);
                cs.GlobalTransform = new Transform3D(RotateYTo(dir / len), (a + b) * 0.5f);   // set after entering the tree
            }
        }

        // Drop the collider (a wire picked back up for re-routing is a preview again, not selectable).
        public void FreeInteractBody() { _body?.QueueFree(); _body = null; }

        // look-at highlight for phase-5 manage (brighten the near-black wire so you can see which one you're targeting)
        public void SetHighlighted(bool on)
        {
            if (_mat == null) return;
            _mat.EmissionEnabled = on;
            _mat.Emission = new Color(0.55f, 0.72f, 1f);
            _mat.EmissionEnergyMultiplier = on ? 0.8f : 0f;
        }

        // Index of the segment (Points[i]..Points[i+1]) nearest to a world point -- which section the look-ray hit.
        public int NearestSegment(Vector3 p)
        {
            int best = 0; float bestD = float.MaxValue;
            for (int i = 0; i + 1 < Points.Count; i++)
            {
                Vector3 a = Points[i], ab = Points[i + 1] - a;
                float t = ab.LengthSquared() < 1e-8f ? 0f : Mathf.Clamp(ab.Dot(p - a) / ab.LengthSquared(), 0f, 1f);
                float d = p.DistanceSquaredTo(a + ab * t);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
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
