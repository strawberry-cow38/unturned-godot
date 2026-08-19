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
        const float HoistSpeed = 6f, HoistRestY = 13f, HoistMax = 11.5f, CarriageAttachY = 14.3f;   // HoistMax lets the block reach ground level to bite a container
        static readonly Basis Upright = new Basis(Vector3.Right, Mathf.DegToRad(-90f));
        static readonly Vector2[] RopeCorner = { new Vector2(0.9f, 3.0f), new Vector2(0.9f, -3.0f), new Vector2(-0.9f, 3.0f), new Vector2(-0.9f, -3.0f) };

        float _speed, _wheelSpin, _trolleyX, _hoistDrop;
        readonly List<MeshInstance3D> _wheels = new();
        readonly List<MeshInstance3D> _ropes = new();
        readonly List<(Vector3 c, Vector3 h)> _frameBoxes = new();   // convex leg/beam boxes = the FRAME collider (drive axis), derived from the gantry mesh; portal openings left OPEN so you can drive over low stuff
        MeshInstance3D _trolley, _hoist;
        bool _magnetOn; RigidBody3D _held; Vector3 _heldOffset; Aabb _heldAabb; Vector3 _faceAtGrab;   // hoist ELECTROMAGNET (steal the skycrane's magnet -> lift a MagnetableContainer)
        const uint ObstacleMask = (1u << 0) | (1u << 5) | (1u << 6); // terrain/statics + vehicles + props: what STOPS the gantry/trolley when the hoist (or its load) hits it
        const uint GroundMask = (1u << 0) | (1u << 5) | (1u << 6); // + terrain: what stops the hoist DESCENT so a load is never pushed underground

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
            ComputeFrameBoxes();
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
            // gantry drive along local -Z; the hoist(+load) collision stops it at contact instead of clipping through
            float target = Mathf.Clamp(throttle, -1f, 1f) * MaxSpeed;
            float rate = Mathf.Abs(throttle) < 0.05f ? Decel : Accel;
            _speed = Mathf.MoveToward(_speed, target, rate * dt);
            Vector3 driveMotion = -GlobalTransform.Basis.Z * (_speed * dt);
            float sfDrive = SafeFracWithFrame(driveMotion, ObstacleMask);
            GlobalPosition += driveMotion * sfDrive;
            SpinWheels(_speed * dt * sfDrive);
            if (sfDrive < 0.999f) _speed = 0f;   // blocked -> the gantry stops rather than shoving through
            // trolley along the beam (local X)
            if (Mathf.Abs(trolleyIn) > 0.05f)
            {
                float want = Mathf.Clamp(_trolleyX + Mathf.Clamp(trolleyIn, -1f, 1f) * TrolleySpeed * dt, TrolleyMin, TrolleyMax);
                float d = want - _trolleyX;
                _trolleyX += d * SafeFrac(GlobalTransform.Basis.X * d, ObstacleMask);
                if (_trolley != null) _trolley.Position = new Vector3(_trolleyX, 0f, 0f);
            }
            // hoist winch (local Y); the descent stops on ground/props so a load is never pushed underground
            if (Mathf.Abs(hoistIn) > 0.05f)
            {
                float want = Mathf.Clamp(_hoistDrop + Mathf.Clamp(hoistIn, -1f, 1f) * HoistSpeed * dt, 0f, HoistMax);
                float d = want - _hoistDrop;                       // + = drop (down)
                _hoistDrop += d * SafeFrac(-GlobalTransform.Basis.Y * d, GroundMask);
            }
            UpdateHoist();
            UpdateMagnet();
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

        // ---- hoist electromagnet: energise (Shift) -> bite a MagnetableContainer at the block face -> lift it ----
        public bool MagnetOn => _magnetOn;
        public void ToggleMagnet()
        {
            _magnetOn = !_magnetOn;
            if (!_magnetOn) ReleaseHeld();
        }
        void ReleaseHeld()
        {
            if (_held != null && IsInstanceValid(_held))
            {
                _held.Freeze = false;
                _held.LinearVelocity = Vector3.Zero; _held.AngularVelocity = Vector3.Zero;   // kill residual kinematic velocity so it doesn't flick up on drop
                _held.Sleeping = false;
                _held.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Inherit;   // physics owns it again
                _held.ResetPhysicsInterpolation();
            }
            _held = null;
        }
        Vector3 HoistFace => _hoist != null && IsInstanceValid(_hoist) ? _hoist.GlobalPosition - GlobalTransform.Basis.Y * 0.35f : GlobalPosition;
        void UpdateMagnet()
        {
            if (_hoist == null) return;
            Vector3 face = HoistFace;
            if (_magnetOn && (_held == null || !IsInstanceValid(_held)))
            {
                var space = GetWorld3D()?.DirectSpaceState;
                if (space != null)
                {
                    Aabb hb = _hoist.GlobalTransform * _hoist.GetAabb();
                    var shape = new BoxShape3D { Size = hb.Size + new Vector3(0.5f, 0.5f, 0.5f) };   // the block's OWN volume + a small skin: connect only on CONTACT, never from range
                    var q = new PhysicsShapeQueryParameters3D { ShapeRid = shape.GetRid(), Transform = new Transform3D(Basis.Identity, hb.GetCenter()), CollisionMask = 1u << 6, CollideWithBodies = true };
                    foreach (var hit in space.IntersectShape(q, 8))
                    {
                        if (hit["collider"].Obj is MagnetableContainer mc && IsInstanceValid(mc))
                        {
                            mc.Freeze = false; mc.Sleeping = false;
                            mc.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic; mc.Freeze = true;
                            _heldOffset = mc.GlobalPosition - face; _heldAabb = WalkWorldAabb(mc); _faceAtGrab = face;
                            mc.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;   // we drive its transform each frame -> opt OUT of global physics-interp so it renders EXACTLY under the hoist (kills the follow-lag)
                            mc.ResetPhysicsInterpolation();
                            _held = mc;
                            break;
                        }
                    }
                }
            }
            if (_held != null && IsInstanceValid(_held)) _held.GlobalPosition = face + _heldOffset;   // ride the hoist (trolley + up/down)
        }

        // ---- kinematic collision: sweep the hoist(+load) box along a motion, return the safe fraction (0..1) ----
        Aabb HoistLoadAabb()
        {
            Aabb a = _hoist.GlobalTransform * _hoist.GetAabb();
            if (_held != null && IsInstanceValid(_held)) { Aabb hb = _heldAabb; hb.Position += HoistFace - _faceAtGrab; a = a.Merge(hb); }
            return a;
        }
        static Aabb WalkWorldAabb(Node n)
        {
            Aabb? acc = null;
            void W(Node k) { if (k is VisualInstance3D vi && vi.Visible) { var a = vi.GlobalTransform * vi.GetAabb(); acc = acc.HasValue ? acc.Value.Merge(a) : a; } foreach (var c in k.GetChildren()) W(c); }
            W(n);
            return acc ?? new Aabb();
        }
        float CastBox(Vector3 center, Vector3 size, Basis basis, Vector3 motion, uint mask)
        {
            if (motion.LengthSquared() < 1e-8f) return 1f;
            if (size.X < 0.05f || size.Y < 0.05f || size.Z < 0.05f) return 1f;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return 1f;
            var shape = new BoxShape3D { Size = size };
            var p = new PhysicsShapeQueryParameters3D { ShapeRid = shape.GetRid(), Transform = new Transform3D(basis, center), Motion = motion, CollisionMask = mask, CollideWithBodies = true };
            if (_held != null && IsInstanceValid(_held)) p.Exclude = new Godot.Collections.Array<Rid> { _held.GetRid() };
            float[] r = space.CastMotion(p);
            return (r != null && r.Length > 0) ? r[0] : 1f;
        }
        float SafeFrac(Vector3 motion, uint mask)   // hoist(+load) box only -- trolley/hoist move just the carriage
        {
            Aabb box = HoistLoadAabb();
            return CastBox(box.GetCenter(), box.Size * 0.9f, Basis.Identity, motion, mask);
        }
        float SafeFracWithFrame(Vector3 motion, uint mask)   // + the frame legs/beam: the DRIVE moves the whole gantry
        {
            float sf = SafeFrac(motion, mask);
            Basis gb = GlobalTransform.Basis;
            foreach (var fb in _frameBoxes) sf = Mathf.Min(sf, CastBox(GlobalTransform * fb.c, fb.h * (2f * 0.95f), gb, motion, mask));
            return sf;
        }
        // derive convex FRAME colliders from the gantry mesh: one top-beam box + up to 8 leg boxes (per portal X x Z-side),
        // leaving the portal OPENINGS open so low objects pass under the beam / between the legs as the gantry rolls over them.
        void ComputeFrameBoxes()
        {
            _frameBoxes.Clear();
            var mesh = Lm("Harbor_0_gantry");
            if (mesh == null || mesh.GetSurfaceCount() == 0) return;
            var vRaw = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (vRaw == null || vRaw.Length == 0) { GD.Print("[FRAMEBOX] no verts"); return; }
            Vector3 big = new Vector3(1e9f, 1e9f, 1e9f);
            Vector3 mn = Upright * vRaw[0], mx = mn;
            var verts = new Vector3[vRaw.Length];
            for (int i = 0; i < vRaw.Length; i++) { var v = Upright * vRaw[i]; verts[i] = v; mn = mn.Min(v); mx = mx.Max(v); }
            float topY = mx.Y, beamCut = topY - (topY - mn.Y) * 0.30f;
            float[] xc = { -49.5f, -22.5f, 22.5f, 49.5f };
            Vector3 bmn = big, bmx = -big; var lmn = new Vector3[8]; var lmx = new Vector3[8];
            for (int k = 0; k < 8; k++) { lmn[k] = big; lmx[k] = -big; }
            foreach (var v in verts)
            {
                if (v.Y >= beamCut) { bmn = bmn.Min(v); bmx = bmx.Max(v); continue; }
                int xi = 0; float bd = 1e9f;
                for (int j = 0; j < 4; j++) { float d = Mathf.Abs(v.X - xc[j]); if (d < bd) { bd = d; xi = j; } }
                int b = xi * 2 + (v.Z >= 0 ? 0 : 1);
                lmn[b] = lmn[b].Min(v); lmx[b] = lmx[b].Max(v);
            }
            float beamBottom = topY;
            if (bmx.X > bmn.X) { _frameBoxes.Add(((bmn + bmx) * 0.5f, (bmx - bmn) * 0.5f)); beamBottom = bmn.Y; }
            for (int k = 0; k < 8; k++)
            {
                if (lmx[k].X <= lmn[k].X) continue;
                Vector3 lo = lmn[k], hi = lmx[k];
                lo.Y += 1.2f;                         // lift the leg-box bottom off the ground so a horizontal drive-cast does not graze the flat terrain
                hi.Y = Mathf.Max(hi.Y, beamBottom);   // and extend the leg column UP to the beam so there is no open mid-height band
                if (lo.Y >= hi.Y) continue;
                _frameBoxes.Add(((lo + hi) * 0.5f, (hi - lo) * 0.5f));
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
