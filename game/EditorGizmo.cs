using Godot;

namespace UnturnedGodot
{
    // Translate gizmo, ported from SDG.Unturned TransformHandles (EMode.Position / POSITION_AXIS). Three axis arrows
    // (X/Y/Z) at the selection pivot; click an arrow and drag to move the target along that axis. The drag projects
    // the mouse ray onto the axis with MathfEx.ProjectRayOntoRay (closest point between the mouse ray and the axis
    // ray) EXACTLY like the source, and moves the target by the axis delta. Ctrl = 1-unit grid snap (source
    // snapPositionInterval 1.0). Rotation/scale handle modes are follow-ups; this is the move master flagged.
    public partial class EditorGizmo : Node3D
    {
        const uint GizmoLayer = 1u << 8;
        readonly Camera3D _cam;
        Node3D _target;
        int _dragAxis = -1;          // 0=X 1=Y 2=Z; -1 = not dragging
        Vector3 _startPos;
        Vector3 _dragDir;            // the world-space axis captured at drag start (fixed for the drag)
        float _startDist;
        public bool LocalSpace;      // source pivotRotation: true = the target's local axes, false = world axes
        readonly Rid[] _arrowRids = new Rid[3];

        static readonly Vector3[] Axis = { Vector3.Right, Vector3.Up, new Vector3(0, 0, 1) };   // world X, Y, Z
        static readonly Color[] AxisCol = { new(0.92f, 0.22f, 0.22f), new(0.24f, 0.9f, 0.24f), new(0.34f, 0.45f, 1f) };

        public EditorGizmo(Camera3D cam) { _cam = cam; }
        public bool Dragging => _dragAxis >= 0;

        public override void _Ready()
        {
            TopLevel = true;   // sit in world space at the target, independent of any parent transform
            for (int i = 0; i < 3; i++)
            {
                var body = new StaticBody3D { CollisionLayer = GizmoLayer, CollisionMask = 0 };
                var mat = new StandardMaterial3D { AlbedoColor = AxisCol[i], ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true };   // draw over the prop so the handle's always grabbable
                body.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.09f, Height = 2f }, MaterialOverride = mat, Position = new Vector3(0, 1f, 0) });      // shaft
                body.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.32f, Height = 0.7f }, MaterialOverride = mat, Position = new Vector3(0, 2.35f, 0) });    // head
                body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.34f, Height = 3f }, Position = new Vector3(0, 1.35f, 0) });   // fat pick capsule along the arrow
                if (i == 0) body.RotationDegrees = new Vector3(0, 0, -90);   // default +Y cylinder -> +X
                else if (i == 2) body.RotationDegrees = new Vector3(90, 0, 0);   // +Y -> +Z
                AddChild(body);
                _arrowRids[i] = body.GetRid();
            }
            Visible = false;
        }

        public void Attach(Node3D target)
        {
            _target = target;
            Visible = target != null;
            if (target != null) GlobalPosition = target.GlobalPosition;
        }

        // source pivotRotation: local = the target's (orthonormalized) rotation, global = world axes
        Basis SpaceBasis => LocalSpace && _target != null ? _target.GlobalTransform.Basis.Orthonormalized() : Basis.Identity;

        public override void _Process(double delta)   // sit at the target, ~constant on-screen size, oriented per space
        {
            if (!Visible || _cam == null || _target == null) return;
            var pos = _target.GlobalPosition;
            float s = Mathf.Max(0.3f, pos.DistanceTo(_cam.GlobalPosition) * 0.07f);
            GlobalTransform = new Transform3D(SpaceBasis.Scaled(Vector3.One * s), pos);
        }

        // returns true if the click grabbed an axis handle (so the caller skips place/select)
        public bool TryBeginDrag(Vector2 screen)
        {
            if (_target == null || !Visible) return false;
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            var q = new PhysicsRayQueryParameters3D { From = from, To = from + dir * 5000f, CollisionMask = GizmoLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            int axis = System.Array.IndexOf(_arrowRids, (Rid)hit["rid"]);
            if (axis < 0) return false;
            _dragAxis = axis;
            _startPos = _target.GlobalPosition;
            _dragDir = (SpaceBasis * Axis[axis]).Normalized();   // capture the world-space axis (local or global) for this drag
            _startDist = ProjectRayOntoRay(from, dir, _startPos, _dragDir);
            return true;
        }

        public void DragTo(Vector2 screen, bool snap)
        {
            if (_dragAxis < 0 || _target == null) return;
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            float delta = ProjectRayOntoRay(from, dir, _startPos, _dragDir) - _startDist;
            var pos = _startPos + _dragDir * delta;
            if (snap) pos = pos.Snapped(Vector3.One);   // 1u grid (source snapPositionInterval)
            _target.GlobalPosition = pos;
            GlobalPosition = pos;
        }

        public void EndDrag() => _dragAxis = -1;

        // MathfEx.ProjectRayOntoRay: the scalar distance along axis (o2,d2) of the point on that axis ray closest to
        // the mouse ray (o1,d1) -- the standard closest-points-between-two-lines solution.
        static float ProjectRayOntoRay(Vector3 o1, Vector3 d1, Vector3 o2, Vector3 d2)
        {
            var r = o1 - o2;
            float a = d1.Dot(d1), b = d1.Dot(d2), c = d2.Dot(d2), d = d1.Dot(r), e = d2.Dot(r);
            float denom = a * c - b * b;
            return Mathf.Abs(denom) < 1e-6f ? 0f : (a * e - b * d) / denom;
        }
    }
}
