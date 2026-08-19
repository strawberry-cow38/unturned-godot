using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Harbor GANTRY CRANE vehicle (master 2026-08-19). Drives STRAIGHT on 16 wheels along the cross axis (local Z,
    // W/S). The TROLLEY (carriage = blue platform + gray machinery + block) slides along the beam (local X, A/D).
    // The HOIST -- a clone of the block on 4 corner ropes -- winches up/down (local Y) under the carriage. Built from
    // Harbor_0 split (weld+CC) into a static GANTRY + the movable TROLLEY; props are raw Z-up -> Y-up via Basis(X,-90).
    // Local axes: X = beam (long, wheels +-22.9), Z = drive/cross (width, +-8.4), Y = up.
    public partial class HarborCrane : Node3D
    {
        static readonly Vector3[] WheelOff = {
            new Vector3(-22.908f, -0.174f, 8.383f), new Vector3(-22.908f, -0.174f, 7.439f),
            new Vector3(-22.908f, -0.174f, -7.450f), new Vector3(-22.908f, -0.174f, -8.395f),
            new Vector3(-22.115f, -0.174f, 8.383f), new Vector3(-22.115f, -0.174f, 7.439f),
            new Vector3(-22.115f, -0.174f, -7.450f), new Vector3(-22.115f, -0.174f, -8.395f),
            new Vector3(22.098f, -0.174f, 8.383f), new Vector3(22.098f, -0.174f, 7.439f),
            new Vector3(22.098f, -0.174f, -7.450f), new Vector3(22.098f, -0.174f, -8.395f),
            new Vector3(22.890f, -0.174f, 8.383f), new Vector3(22.890f, -0.174f, 7.439f),
            new Vector3(22.890f, -0.174f, -7.450f), new Vector3(22.890f, -0.174f, -8.395f),
            // END-FRAME wheels: inner set cloned + shifted to the outer legs (+-22.5 -> +-49.5, master)
            new Vector3(-49.908f, -0.174f, 8.383f), new Vector3(-49.908f, -0.174f, 7.439f),
            new Vector3(-49.908f, -0.174f, -7.450f), new Vector3(-49.908f, -0.174f, -8.395f),
            new Vector3(-49.115f, -0.174f, 8.383f), new Vector3(-49.115f, -0.174f, 7.439f),
            new Vector3(-49.115f, -0.174f, -7.450f), new Vector3(-49.115f, -0.174f, -8.395f),
            new Vector3(49.098f, -0.174f, 8.383f), new Vector3(49.098f, -0.174f, 7.439f),
            new Vector3(49.098f, -0.174f, -7.450f), new Vector3(49.098f, -0.174f, -8.395f),
            new Vector3(49.890f, -0.174f, 8.383f), new Vector3(49.890f, -0.174f, 7.439f),
            new Vector3(49.890f, -0.174f, -7.450f), new Vector3(49.890f, -0.174f, -8.395f),
        };
        const float WheelRadius = 0.4f;
        const float MaxSpeed = 6f, Accel = 2f, Decel = 3f;
        const float TrolleySpeed = 12f, TrolleyMin = -52f, TrolleyMax = 30f;
        const float CarriageX = 11.5f;      // carriage centre along the beam (local X, before the slide offset)
        const float HoistSpeed = 4f, HoistRestY = 13f, HoistMax = 11f, CarriageAttachY = 14.3f;
        static readonly Basis Upright = new Basis(Vector3.Right, Mathf.DegToRad(-90f));
        static readonly Vector2[] RopeCorner = { new Vector2(0.9f, 3.0f), new Vector2(0.9f, -3.0f), new Vector2(-0.9f, 3.0f), new Vector2(-0.9f, -3.0f) };

        float _speed, _wheelSpin, _trolleyX, _hoistDrop;
        readonly List<MeshInstance3D> _wheels = new();
        readonly List<MeshInstance3D> _ropes = new();
        MeshInstance3D _trolley, _hoist;

        static Mesh Lm(string n) => ObjMesh.Load(ProjectSettings.GlobalizePath($"res://content/objects/{n}.obj"));
        static Material MakeMat(string tex)
        {
            var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            if (img.Load(ProjectSettings.GlobalizePath($"res://content/objects/{tex}_tex.png")) == Error.Ok) m.AlbedoTexture = ImageTexture.CreateFromImage(img);
            return m;
        }

        public static HarborCrane Spawn(Node parent, Vector3 pos, float yawDeg)
        {
            var c = new HarborCrane { Position = pos, RotationDegrees = new Vector3(0f, yawDeg, 0f) };
            parent.AddChild(c);
            c.Build();
            return c;
        }

        void Build()
        {
            AddToGroup("cranes");
            AddChild(new MeshInstance3D { Mesh = Lm("Harbor_0_gantry"), MaterialOverride = MakeMat("Harbor_0"), Basis = Upright });
            _trolley = new MeshInstance3D { Mesh = Lm("Harbor_0_trolley"), MaterialOverride = MakeMat("Harbor_0"), Transform = new Transform3D(Upright, Vector3.Zero) };
            AddChild(_trolley);
            var wm = Lm("Wheel_3"); var wmat = MakeMat("Wheel_3");
            foreach (var off in WheelOff)
            {
                var w = new MeshInstance3D { Mesh = wm, MaterialOverride = wmat, Transform = new Transform3D(Upright, off) };
                AddChild(w); _wheels.Add(w);
            }
            // HOIST: a clone of the block on 4 corner ropes, hanging under the carriage
            _hoist = new MeshInstance3D { Mesh = Lm("Harbor_0_hoistblk"), MaterialOverride = MakeMat("Harbor_0") };
            AddChild(_hoist);
            var ropeMat = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.10f, 0.08f), Roughness = 1f };
            for (int i = 0; i < 4; i++)
            {
                var r = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.07f, Height = 1f, RadialSegments = 6, Rings = 0 }, MaterialOverride = ropeMat };
                AddChild(r); _ropes.Add(r);
            }
            UpdateHoist();
        }

        /// <summary>W/S drives straight (local -Z); A/D slides the trolley along the beam (local X); hoistIn winches the block up/down.</summary>
        public void Drive(float throttle, float trolleyIn, float hoistIn, float dt)
        {
            float target = Mathf.Clamp(throttle, -1f, 1f) * MaxSpeed;
            float rate = Mathf.Abs(throttle) < 0.05f ? Decel : Accel;
            _speed = Mathf.MoveToward(_speed, target, rate * dt);
            GlobalPosition += -GlobalTransform.Basis.Z * (_speed * dt);
            SpinWheels(_speed * dt);
            if (Mathf.Abs(trolleyIn) > 0.05f)
            {
                _trolleyX = Mathf.Clamp(_trolleyX + Mathf.Clamp(trolleyIn, -1f, 1f) * TrolleySpeed * dt, TrolleyMin, TrolleyMax);
                if (_trolley != null) _trolley.Position = new Vector3(_trolleyX, 0f, 0f);
            }
            if (Mathf.Abs(hoistIn) > 0.05f)
                _hoistDrop = Mathf.Clamp(_hoistDrop + Mathf.Clamp(hoistIn, -1f, 1f) * HoistSpeed * dt, 0f, HoistMax);
            UpdateHoist();
        }

        // Position the hoist block under the carriage at the current drop, and stretch the 4 ropes from the carriage
        // corners down to the block's top corners.
        void UpdateHoist()
        {
            if (_hoist == null) return;
            float cx = CarriageX + _trolleyX;
            float hy = HoistRestY - _hoistDrop;
            _hoist.Transform = new Transform3D(Upright.Rotated(Vector3.Up, Mathf.DegToRad(90f)), new Vector3(cx, hy, 0f));
            for (int i = 0; i < _ropes.Count; i++)
            {
                Vector3 top = new Vector3(cx + RopeCorner[i].X, CarriageAttachY, RopeCorner[i].Y);
                Vector3 bot = new Vector3(cx + RopeCorner[i].X, hy + 0.25f, RopeCorner[i].Y);   // attach at the (shorter) block top
                PlaceRope(_ropes[i], top, bot);
            }
        }

        static void PlaceRope(MeshInstance3D rope, Vector3 a, Vector3 b)
        {
            if (!IsInstanceValid(rope)) return;
            Vector3 mid = (a + b) * 0.5f, d = b - a; float len = d.Length();
            if (len < 1e-3f) { rope.Visible = false; return; }
            rope.Visible = true;
            Vector3 y = d / len, x = y.Cross(Vector3.Right);
            if (x.LengthSquared() < 1e-4f) x = Vector3.Forward;
            x = x.Normalized(); Vector3 z = x.Cross(y).Normalized();
            rope.Transform = new Transform3D(new Basis(x, y, z).Scaled(new Vector3(1f, len, 1f)), mid);
        }

        void SpinWheels(float dist)
        {
            if (Mathf.Abs(dist) < 1e-5f) return;
            _wheelSpin -= dist / WheelRadius;
            var spun = new Basis(Vector3.Right, _wheelSpin) * Upright;
            foreach (var w in _wheels) if (IsInstanceValid(w)) w.Basis = spun;
        }

        public float SpeedMps => _speed;
        public string DisplayName => "Harbor Crane";
    }
}
