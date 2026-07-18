using Godot;

namespace UnturnedGodot
{
    // Transform gizmo, ported from SDG.Unturned TransformHandles.
    //   TRANSLATE (EMode.Position/POSITION_AXIS): 3 axis arrows; drag projects the mouse ray onto the axis
    //     (MathfEx.ProjectRayOntoRay) and moves the target by the axis delta.
    //   ROTATE (EMode.Rotation): 3 axis rings; drag projects onto the ring tangent -> newAngle = dist*90/viewScale ->
    //     AngleAxis(newAngle, axis) * startRotation (source line 488-499).
    // Ctrl = snap (1u translate / 15deg rotate, source snapPositionInterval/snapRotationIntervalDegrees). G toggles
    // local/global (source pivotRotation). T cycles Translate<->Rotate. Scale handles are a follow-up.
    public partial class EditorGizmo : Node3D
    {
        public enum EMode { Translate, Rotate }
        const uint GizmoLayer = 1u << 8;
        const float RingR = 2.2f;   // ring radius, gizmo-local units

        readonly Camera3D _cam;
        Node3D _target;
        public EMode Mode = EMode.Translate;
        public bool LocalSpace;     // source pivotRotation: local = target axes, global = world

        int _dragAxis = -1;         // translate drag
        Vector3 _startPos, _dragDir;
        float _startDist;
        int _rotAxis = -1;          // rotate drag
        Basis _startBasis;
        Vector3 _rotEdge, _rotTangent, _rotAxisWorld;
        float _viewScale = 2f;      // ring world radius (rotate angle scale)

        readonly Rid[] _arrowRids = new Rid[3];
        readonly Node3D[] _arrows = new Node3D[3];
        readonly Node3D[] _rings = new Node3D[3];

        static readonly Vector3[] Axis = { Vector3.Right, Vector3.Up, new Vector3(0, 0, 1) };
        static readonly Color[] AxisCol = { new(0.92f, 0.22f, 0.22f), new(0.24f, 0.9f, 0.24f), new(0.34f, 0.45f, 1f) };

        public EditorGizmo(Camera3D cam) { _cam = cam; }
        public bool Dragging => _dragAxis >= 0 || _rotAxis >= 0;

        public override void _Ready()
        {
            TopLevel = true;
            for (int i = 0; i < 3; i++)
            {
                var mat = new StandardMaterial3D { AlbedoColor = AxisCol[i], ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true };
                // arrow (translate) -- pickable via collider
                var arrow = new StaticBody3D { CollisionLayer = GizmoLayer, CollisionMask = 0 };
                arrow.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.09f, Height = 2f }, MaterialOverride = mat, Position = new Vector3(0, 1f, 0) });
                arrow.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.32f, Height = 0.7f }, MaterialOverride = mat, Position = new Vector3(0, 2.35f, 0) });
                arrow.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.34f, Height = 3f }, Position = new Vector3(0, 1.35f, 0) });
                if (i == 0) arrow.RotationDegrees = new Vector3(0, 0, -90); else if (i == 2) arrow.RotationDegrees = new Vector3(90, 0, 0);
                AddChild(arrow); _arrows[i] = arrow; _arrowRids[i] = arrow.GetRid();
                // ring (rotate) -- visual torus; picked in code (plane-radius test), so its exact mesh orientation is cosmetic
                var ring = new Node3D();
                var rm = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = RingR - 0.05f, OuterRadius = RingR + 0.05f }, MaterialOverride = mat };
                if (i == 0) rm.RotationDegrees = new Vector3(0, 0, 90); else if (i == 2) rm.RotationDegrees = new Vector3(90, 0, 0);
                ring.AddChild(rm); AddChild(ring); _rings[i] = ring;
            }
            Visible = false;
            RefreshVis();
        }

        void RefreshVis() { for (int i = 0; i < 3; i++) { _arrows[i].Visible = Mode == EMode.Translate; _rings[i].Visible = Mode == EMode.Rotate; } }
        public void CycleMode() { Mode = Mode == EMode.Translate ? EMode.Rotate : EMode.Translate; RefreshVis(); }

        Basis SpaceBasis => LocalSpace && _target != null ? _target.GlobalTransform.Basis.Orthonormalized() : Basis.Identity;

        public void Attach(Node3D t) { _target = t; Visible = t != null; if (t != null) GlobalPosition = t.GlobalPosition; RefreshVis(); }

        public override void _Process(double d)
        {
            if (!Visible || _cam == null || _target == null) return;
            var pos = _target.GlobalPosition;
            float s = Mathf.Max(0.3f, pos.DistanceTo(_cam.GlobalPosition) * 0.07f);
            _viewScale = RingR * s;
            GlobalTransform = new Transform3D(SpaceBasis.Scaled(Vector3.One * s), pos);
        }

        public bool TryBeginDrag(Vector2 screen)
        {
            if (_target == null || !Visible) return false;
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            var pivot = _target.GlobalPosition;
            if (Mode == EMode.Translate)
            {
                var q = new PhysicsRayQueryParameters3D { From = from, To = from + dir * 5000f, CollisionMask = GizmoLayer };
                var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
                if (hit.Count == 0) return false;
                int axis = System.Array.IndexOf(_arrowRids, (Rid)hit["rid"]);
                if (axis < 0) return false;
                _dragAxis = axis; _startPos = pivot;
                _dragDir = (SpaceBasis * Axis[axis]).Normalized();
                _startDist = ProjectRayOntoRay(from, dir, _startPos, _dragDir);
                return true;
            }
            // rotate: pick the ring whose plane-hit radius is closest to the ring radius
            int best = -1; float bestErr = 0.4f * _viewScale;
            for (int i = 0; i < 3; i++)
            {
                var axisW = (SpaceBasis * Axis[i]).Normalized();
                var h = new Plane(axisW, pivot).IntersectsRay(from, dir);
                if (h == null) continue;
                float err = Mathf.Abs((((Vector3)h) - pivot).Length() - _viewScale);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            if (best < 0) return false;
            _rotAxis = best; _rotAxisWorld = (SpaceBasis * Axis[best]).Normalized(); _startBasis = _target.GlobalTransform.Basis;
            var hp = (Vector3)new Plane(_rotAxisWorld, pivot).IntersectsRay(from, dir);
            var outward = (hp - pivot).Normalized();
            _rotEdge = pivot + outward * _viewScale;
            _rotTangent = _rotAxisWorld.Cross(outward).Normalized();
            return true;
        }

        public void DragTo(Vector2 screen, bool snap)
        {
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            if (_dragAxis >= 0)
            {
                float delta = ProjectRayOntoRay(from, dir, _startPos, _dragDir) - _startDist;
                var pos = _startPos + _dragDir * delta;
                if (snap) pos = pos.Snapped(Vector3.One);
                _target.GlobalPosition = pos; GlobalPosition = pos;
            }
            else if (_rotAxis >= 0)
            {
                float ang = ProjectRayOntoRay(from, dir, _rotEdge, _rotTangent) * 90f / _viewScale;   // source: dist*90/viewScale
                if (snap) ang = Mathf.Round(ang / 15f) * 15f;                                          // snapRotationIntervalDegrees 15
                _target.GlobalTransform = new Transform3D(_startBasis.Rotated(_rotAxisWorld, Mathf.DegToRad(ang)), _target.GlobalPosition);
            }
        }

        public void EndDrag() { _dragAxis = -1; _rotAxis = -1; }

        // MathfEx.ProjectRayOntoRay: scalar distance along ray B (o2,d2) of its closest point to ray A (o1,d1)
        static float ProjectRayOntoRay(Vector3 o1, Vector3 d1, Vector3 o2, Vector3 d2)
        {
            var r = o1 - o2;
            float a = d1.Dot(d1), b = d1.Dot(d2), c = d2.Dot(d2), dd = d1.Dot(r), e = d2.Dot(r);
            float den = a * c - b * b;
            return Mathf.Abs(den) < 1e-6f ? 0f : (a * e - b * dd) / den;
        }
    }
}
