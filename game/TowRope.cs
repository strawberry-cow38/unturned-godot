using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // The visual tow rope between two vehicles' tow nodes -- a hemp-brown polyline drawn as thin stretched cylinder
    // segments, the exact technique the power Wire uses (Wire.cs). Unlike a wire it SAGS: when the two nodes are closer
    // than the rope's rest length the slack droops in a parabola; pulled taut it straightens. Purely cosmetic -- the
    // pull force lives in Vehicle.UpdateTow. Used as the live placement preview while roping AND as the committed rope
    // (owned by the towing vehicle, re-pointed every physics tick, freed on detach). Mirrors Wire but sags + is thicker.
    public partial class TowRope : Node3D
    {
        const float Radius = 0.032f;            // thicker than a wire (0.018) -- it's a rope, not a cable
        const int Segments = 10;                // sag resolution (polyline point-pairs)
        static readonly Color OkColor = new Color(0.42f, 0.30f, 0.16f);   // hemp brown ("tough, strong hemp")
        static readonly Color BadColor = new Color(0.90f, 0.20f, 0.15f);  // invalid endpoint (placement preview only)

        readonly List<MeshInstance3D> _segs = new();
        StandardMaterial3D _mat;

        public override void _Ready()
        {
            _mat = new StandardMaterial3D { AlbedoColor = OkColor, Metallic = 0f, Roughness = 0.9f };
        }

        // Draw the rope between two world points. restLen = the rope's natural length: any slack (dist < restLen) droops
        // as a parabola whose low point sags by (restLen - dist); taut/stretched draws a straight line with a hint of sag.
        public void SetEndpoints(Vector3 a, Vector3 b, float restLen, bool valid = true)
        {
            if (_mat != null) _mat.AlbedoColor = valid ? OkColor : BadColor;
            float dist = a.DistanceTo(b);
            float sag = Mathf.Max(0f, restLen - dist) * 0.5f + 0.06f;   // droop grows with slack; small baseline so a taut rope still reads as rope
            var pts = new Vector3[Segments + 1];
            for (int i = 0; i <= Segments; i++)
            {
                float t = (float)i / Segments;
                Vector3 p = a.Lerp(b, t);
                p.Y -= sag * 4f * t * (1f - t);   // parabola: 0 at the ends, max sag at the middle
                pts[i] = p;
            }
            while (_segs.Count < Segments)   // grow the segment pool
            {
                var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius, BottomRadius = Radius, Height = 1f, RadialSegments = 6 }, MaterialOverride = _mat, TopLevel = true };
                AddChild(mi); _segs.Add(mi);
            }
            for (int i = 0; i < _segs.Count; i++)
            {
                if (i >= Segments) { _segs[i].Visible = false; continue; }
                Vector3 p0 = pts[i], p1 = pts[i + 1], dir = p1 - p0;
                float len = dir.Length();
                if (len < 1e-4f) { _segs[i].Visible = false; continue; }
                _segs[i].Visible = true;
                // rotate the cylinder's local +Y along the segment, THEN stretch its local Y to the length (see Wire.cs).
                _segs[i].GlobalTransform = new Transform3D(RotateYTo(dir / len) * Basis.FromScale(new Vector3(1f, len, 1f)), (p0 + p1) * 0.5f);
            }
        }

        // orthonormal rotation mapping the mesh's +Y axis onto the unit direction u (axis-angle) -- copy of Wire.RotateYTo
        static Basis RotateYTo(Vector3 u)
        {
            float d = Vector3.Up.Dot(u);
            if (d > 0.9999f) return Basis.Identity;
            if (d < -0.9999f) return new Basis(Vector3.Right, Mathf.Pi);
            return new Basis(Vector3.Up.Cross(u).Normalized(), Mathf.Acos(Mathf.Clamp(d, -1f, 1f)));
        }
    }
}
