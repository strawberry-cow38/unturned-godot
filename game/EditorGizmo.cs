using Godot;

namespace UnturnedGodot
{
    // Transform gizmo, ported from SDG.Unturned TransformHandles.
    //   TRANSLATE (POSITION_AXIS): 3 axis arrows; drag projects the mouse ray onto the axis (ProjectRayOntoRay), move
    //     by the axis delta.
    //   ROTATE (ROTATION): 3 axis rings; drag projects onto the ring tangent -> newAngle = dist*90/viewScale ->
    //     AngleAxis(newAngle, axis) * startRotation (source :488-499).
    //   SCALE (SCALE_AXIS / SCALE_UNIFORM): 3 axis stalks + a center cube; drag projects onto the scale world-dir ->
    //     dist = (proj - initial)/viewScale -> newScale = one + localDir*dist (guards dist=-1 -> scale 0) (source :507-529).
    // Ctrl = snap (1u translate/scale, 15deg rotate). G toggles local/global (pivotRotation). T cycles the mode.
    public partial class EditorGizmo : Node3D
    {
        public enum EMode { Translate, Rotate, Scale }
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
        int _scaleIdx = -1;         // scale drag (0-2 = axis, 3 = uniform)
        Vector3 _scaleStart, _scaleWorldDir, _scaleLocalDir;
        Basis _scaleRotBasis;
        float _scaleInitDist;
        float _viewScale = 2f;      // ring/handle world radius (rotate + scale distance scale)

        readonly Rid[] _arrowRids = new Rid[3];
        readonly Node3D[] _arrows = new Node3D[3];
        readonly Node3D[] _rings = new Node3D[3];
        readonly Rid[] _scaleRids = new Rid[4];   // 0-2 axis, 3 uniform
        readonly Node3D[] _scaleH = new Node3D[4];

        static readonly Vector3[] Axis = { Vector3.Right, Vector3.Up, new Vector3(0, 0, 1) };
        static readonly Color[] AxisCol = { new(0.92f, 0.22f, 0.22f), new(0.24f, 0.9f, 0.24f), new(0.34f, 0.45f, 1f) };

        public EditorGizmo(Camera3D cam) { _cam = cam; }
        public bool Dragging => _dragAxis >= 0 || _rotAxis >= 0 || _scaleIdx >= 0;

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
                // scale handle (per-axis) -- stalk + cube tip, pickable
                var sh = new StaticBody3D { CollisionLayer = GizmoLayer, CollisionMask = 0 };
                sh.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 1.9f }, MaterialOverride = mat, Position = new Vector3(0, 0.95f, 0) });
                sh.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.42f, 0.42f, 0.42f) }, MaterialOverride = mat, Position = new Vector3(0, 1.95f, 0) });
                sh.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f, 0.6f, 0.5f) }, Position = new Vector3(0, 1.9f, 0) });
                if (i == 0) sh.RotationDegrees = new Vector3(0, 0, -90); else if (i == 2) sh.RotationDegrees = new Vector3(90, 0, 0);
                AddChild(sh); _scaleH[i] = sh; _scaleRids[i] = sh.GetRid();
            }
            // uniform scale handle -- center cube
            var uni = new StaticBody3D { CollisionLayer = GizmoLayer, CollisionMask = 0 };
            var umat = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.9f, 0.45f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true };
            uni.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.5f, 0.5f) }, MaterialOverride = umat });
            uni.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.55f, 0.55f, 0.55f) } });
            AddChild(uni); _scaleH[3] = uni; _scaleRids[3] = uni.GetRid();
            Visible = false;
            RefreshVis();
        }

        void RefreshVis()
        {
            for (int i = 0; i < 3; i++) { _arrows[i].Visible = Mode == EMode.Translate; _rings[i].Visible = Mode == EMode.Rotate; _scaleH[i].Visible = Mode == EMode.Scale; }
            _scaleH[3].Visible = Mode == EMode.Scale;
        }
        public void CycleMode() { Mode = (EMode)(((int)Mode + 1) % 3); RefreshVis(); }

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
                int axis = PickCollider(from, dir, _arrowRids);
                if (axis < 0) return false;
                _dragAxis = axis; _startPos = pivot;
                _dragDir = (SpaceBasis * Axis[axis]).Normalized();
                _startDist = ProjectRayOntoRay(from, dir, _startPos, _dragDir);
                return true;
            }
            if (Mode == EMode.Scale)
            {
                int idx = PickCollider(from, dir, _scaleRids);
                if (idx < 0) return false;
                _scaleIdx = idx;
                _scaleRotBasis = _target.GlobalTransform.Basis.Orthonormalized();
                _scaleStart = _target.GlobalTransform.Basis.Scale;
                if (idx < 3) { _scaleLocalDir = Axis[idx]; _scaleWorldDir = (SpaceBasis * Axis[idx]).Normalized(); }
                else   // uniform: drag along the camera-facing plane (source SCALE_UNIFORM)
                {
                    _scaleLocalDir = Vector3.One;
                    var camF = -_cam.GlobalTransform.Basis.Z;
                    var h = new Plane(camF, pivot).IntersectsRay(from, dir);
                    _scaleWorldDir = h != null ? (((Vector3)h) - pivot).Normalized() : (SpaceBasis * Vector3.Right).Normalized();
                }
                _scaleInitDist = ProjectRayOntoRay(from, dir, pivot, _scaleWorldDir);
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

        int PickCollider(Vector3 from, Vector3 dir, Rid[] rids)
        {
            var q = new PhysicsRayQueryParameters3D { From = from, To = from + dir * 5000f, CollisionMask = GizmoLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            return hit.Count == 0 ? -1 : System.Array.IndexOf(rids, (Rid)hit["rid"]);
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
            else if (_scaleIdx >= 0)
            {
                float dist = (ProjectRayOntoRay(from, dir, _target.GlobalPosition, _scaleWorldDir) - _scaleInitDist) / _viewScale;   // source :509-510
                if (snap) dist = Mathf.Round(dist);
                if (Mathf.Abs(dist + 1f) < 0.001f) return;   // source: don't let a scale axis hit 0
                var f = Vector3.One + _scaleLocalDir * dist;
                var ns = new Vector3(Mathf.Max(0.01f, _scaleStart.X * f.X), Mathf.Max(0.01f, _scaleStart.Y * f.Y), Mathf.Max(0.01f, _scaleStart.Z * f.Z));
                _target.GlobalTransform = new Transform3D(_scaleRotBasis * Basis.FromScale(ns), _target.GlobalPosition);
            }
        }

        public void EndDrag() { _dragAxis = -1; _rotAxis = -1; _scaleIdx = -1; }

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
