using Godot;

namespace UnturnedGodot
{
    // Free-fly editor camera, ported from Unturned's EditorMovement + EditorLook (SDG.Unturned, Edit/).
    // Source: WASD = camera-relative horizontal (input rotated by the camera, so forward follows the look
    // pitch), ascend/descend = WORLD-vertical, scroll = fly speed (default 32, x0.2 multiplicative per notch,
    // clamped 0.5..2048); mouse look yaw-on-body / pitch-on-cam, pitch clamped +-90. Source captures the mouse
    // whenever isFlying; we gate fly+look on HOLD-RMB so the released cursor can click the dashboard (the one
    // deviation -- Godot editor UX -- everything else is the source's values).
    public partial class EditorCamera : Camera3D
    {
        float _speed = 32f;                     // EditorMovement.speed default
        float _yaw, _pitch;                     // EditorLook yaw/pitch (folded into this cam's basis)
        const float Sensitivity = 0.12f;        // ControlsSettings.mouseAimSensitivity analog (deg / mouse pixel)
        bool _flying;                           // RMB held = EditorInteract.isFlying

        public override void _Ready()
        {
            Current = true;
            Fov = 60f;                          // OptionsSettings.DesiredVerticalFieldOfView
            var e = RotationDegrees; _yaw = e.Y; _pitch = e.X;
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.Right)
                {
                    _flying = mb.Pressed;
                    Input.MouseMode = _flying ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
                }
                else if (_flying && mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
                    _speed = Mathf.Clamp(_speed + 0.2f * _speed, 0.5f, 2048f);   // source: speed += mouse_z*0.2*speed
                else if (_flying && mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
                    _speed = Mathf.Clamp(_speed - 0.2f * _speed, 0.5f, 2048f);
            }
            else if (ev is InputEventMouseMotion mm && _flying)
            {
                _yaw -= mm.Relative.X * Sensitivity;
                _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * Sensitivity, -90f, 90f);   // EditorLook: pitch clamp +-90
                RotationDegrees = new Vector3(_pitch, _yaw, 0f);
            }
        }

        public override void _Process(double delta)
        {
            if (!_flying) return;
            float dt = (float)delta;
            var basis = GlobalTransform.Basis;
            Vector3 move = Vector3.Zero;
            if (Input.IsKeyPressed(Key.W)) move -= basis.Z;   // forward = -Z (camera-relative, includes pitch)
            if (Input.IsKeyPressed(Key.S)) move += basis.Z;
            if (Input.IsKeyPressed(Key.A)) move -= basis.X;
            if (Input.IsKeyPressed(Key.D)) move += basis.X;
            float h = (Input.IsKeyPressed(Key.E) ? 1f : 0f) - (Input.IsKeyPressed(Key.Q) ? 1f : 0f);   // ascend/descend, world-up
            GlobalPosition += move * _speed * dt + Vector3.Up * h * _speed * dt;
        }

        public float Speed => _speed;
        public bool Flying => _flying;   // RMB held (mouse captured) -- the Objects editor ignores LMB place/select while flying
    }
}
