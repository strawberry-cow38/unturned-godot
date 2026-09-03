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
        const float TrolleySpeed = 5f, TrolleyMin = -52f, TrolleyMax = 30f;   // ~drive MaxSpeed so a/d matches w/s feel (master)
        const float CarriageX = 11.266f;    // carriage centre along the beam = the source-blocks X centre, so the hoist + ropes sit centred under the blocks (master)
        const float HoistSpeed = 5f, HoistRestY = 13f, HoistMax = 11.5f, CarriageAttachY = 14.3f;   // ~drive MaxSpeed so q/e matches w/s feel; HoistMax lets the block reach ground level
        static readonly Basis Upright = new Basis(Vector3.Right, Mathf.DegToRad(-90f));
        static readonly Vector2[] RopeCorner = { new Vector2(0.9f, 3.0f), new Vector2(0.9f, -3.0f), new Vector2(-0.9f, 3.0f), new Vector2(-0.9f, -3.0f) };

        float _speed, _wheelSpin, _trolleyX, _hoistDrop;
        readonly List<MeshInstance3D> _wheels = new();
        readonly List<MeshInstance3D> _ropes = new();
        readonly List<(Vector3 c, Vector3 h)> _frameBoxes = new();
        Rid _frameColliderRid, _trolleyColliderRid, _hoistColliderRid;   // the crane's OWN exact-mesh colliders (player/vehicles/loose containers hit these); the self-stop casts exclude whichever moves WITH that cast   // convex leg/beam boxes = the FRAME collider (drive axis), derived from the gantry mesh; portal openings left OPEN so you can drive over low stuff
        MeshInstance3D _trolley, _hoist;
        bool _magnetOn; RigidBody3D _held; Vector3 _heldOffset; Aabb _heldAabb; Vector3 _faceAtGrab;   // hoist ELECTROMAGNET (steal the skycrane's magnet -> lift a MagnetableContainer)
        const uint ObstacleMask = (1u << 0) | (1u << 5) | (1u << 6); // terrain/statics + vehicles + props: what STOPS the gantry/trolley when the hoist (or its load) hits it
        const uint GroundMask = (1u << 0) | (1u << 5) | (1u << 6); // + terrain: what stops the hoist DESCENT so a load is never pushed underground

        static Mesh Lm(string n) => ObjMesh.Load(ProjectSettings.GlobalizePath($"res://content/objects/{n}.obj"));
        static Material MakeMat(string tex)
        {
            var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            if (ContentProvider.LoadOk(img, ProjectSettings.GlobalizePath($"res://content/objects/{tex}_tex.png"))) m.AlbedoTexture = ImageTexture.CreateFromImage(img);
            return m;
        }

        public static HarborCrane Spawn(Node parent, Vector3 pos, float yawDeg)
        {
            var c = new HarborCrane { Position = pos, RotationDegrees = new Vector3(0f, yawDeg, 0f) };
            parent.AddChild(c);
            c.Build();
            c.ResetPhysicsInterpolation();   // don't smear from the origin on frame 1 (train parity)
            return c;
        }

        void Build()
        {
            AddToGroup("cranes");
            var gantryMesh = new MeshInstance3D { Mesh = Lm("Harbor_0_gantry"), MaterialOverride = MakeMat("Harbor_0"), Basis = Upright };
            AddChild(gantryMesh);
            gantryMesh.CreateTrimeshCollision();   // EXACT 1:1 frame collider -- concave trimesh on a STATIC child body IS allowed (esp. on Jolt); rides the slow crane so no tunnelling. Every bar, uniform, surface verified 1:1.
            _frameColliderRid = FirstStaticBodyRid(gantryMesh);
            ComputeFrameBoxes();   // (self-stop cast boxes only now)
            _trolley = new MeshInstance3D { Mesh = Lm("Harbor_0_trolley"), MaterialOverride = MakeMat("Harbor_0"), Transform = new Transform3D(Upright, Vector3.Zero) };
            AddChild(_trolley);
            _trolley.CreateTrimeshCollision();   // exact carriage collider
            _trolleyColliderRid = FirstStaticBodyRid(_trolley);
            var wm = Lm("Wheel_3"); var wmat = MakeMat("Wheel_3");
            foreach (var off in WheelOff)
            {
                var w = new MeshInstance3D { Mesh = wm, MaterialOverride = wmat, Transform = new Transform3D(Upright, off) };
                AddChild(w); _wheels.Add(w);
            }
            // HOIST: a clone of the block on 4 corner ropes, hanging under the carriage
            _hoist = new MeshInstance3D { Mesh = Lm("Harbor_0_hoistblk"), MaterialOverride = MakeMat("Harbor_0") };
            AddChild(_hoist);
            _hoist.CreateTrimeshCollision();   // hoist-block collider: a LOOSE container physically hits it (the HELD one is excluded from the casts + kinematic, so no fight)
            _hoistColliderRid = FirstStaticBodyRid(_hoist);
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
            float sfDrive = SafeFracWithFrame(driveMotion, ObstacleMask, CastEx(true, true, true));
            GlobalPosition += driveMotion * sfDrive;
            SpinWheels(_speed * dt * sfDrive);
            if (sfDrive < 0.999f) _speed = 0f;   // blocked -> the gantry stops rather than shoving through
            // trolley along the beam (local X)
            if (Mathf.Abs(trolleyIn) > 0.05f)
            {
                float want = Mathf.Clamp(_trolleyX + Mathf.Clamp(trolleyIn, -1f, 1f) * TrolleySpeed * dt, TrolleyMin, TrolleyMax);
                float d = want - _trolleyX;
                _trolleyX += d * SafeFrac(GlobalTransform.Basis.X * d, ObstacleMask, CastEx(false, true, true));   // frame left IN -> the hoist + its load stop against the legs/bars
                if (_trolley != null) _trolley.Position = new Vector3(_trolleyX, 0f, 0f);
            }
            // hoist winch (local Y); the descent stops on ground/props so a load is never pushed underground
            if (Mathf.Abs(hoistIn) > 0.05f)
            {
                float want = Mathf.Clamp(_hoistDrop + Mathf.Clamp(hoistIn, -1f, 1f) * HoistSpeed * dt, 0f, HoistMax);
                float d = want - _hoistDrop;                       // + = drop (down)
                _hoistDrop += d * SafeFrac(-GlobalTransform.Basis.Y * d, GroundMask, CastEx(true, true, true));   // exclude ALL of the crane's own colliders: the hoist travels vertically WITHIN its own structure by design (retracting up must not self-collide with the carriage). Still stops on external ground/props/containers (GroundMask, not a crane RID).
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
        public float HoistDrop => _hoistDrop;   // (test/telemetry)
        public float TrolleyX => _trolleyX;
        public bool HasHeld => _held != null && IsInstanceValid(_held);
        public void ToggleMagnet()
        {
            _magnetOn = !_magnetOn;
            if (!_magnetOn) ReleaseHeld();
        }
        void ReleaseHeld()
        {
            if (_held != null && IsInstanceValid(_held))
            {
                var deck = NearestEmptyDeckUnder(_held.GlobalPosition, 1.0f);   // snap ONLY when very close (<=1m XZ) to the deck centre, horizontal distance only; else drop
                if (deck != null && _held is MagnetableContainer heldMc)
                {
                    deck.Load(heldMc);   // Load re-centres + kinematically mounts it on the deck
                }
                else
                {
                    _held.Freeze = false;
                    _held.LinearVelocity = Vector3.Zero; _held.AngularVelocity = Vector3.Zero;   // kill residual kinematic velocity so it doesn't flick up on drop
                    _held.Sleeping = false;
                    _held.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Inherit;   // physics owns it again
                    _held.ResetPhysicsInterpolation();
                }
            }
            _held = null;
        }
        Train.FlatbedDeck NearestEmptyDeckUnder(Vector3 world, float xzTol)
        {
            Train.FlatbedDeck best = null; float bestD = xzTol;
            foreach (var n in GetTree().GetNodesInGroup("flatbeds"))
                if (n is Train.FlatbedDeck d && d.Empty)
                {
                    float dxz = new Vector2(world.X - d.GlobalPosition.X, world.Z - d.GlobalPosition.Z).Length();
                    if (dxz < bestD) { bestD = dxz; best = d; }
                }
            return best;
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
                            mc.DetachFromCarrier?.Invoke();   // if it's sitting on a flatbed, take it off the deck first
                            mc.Freeze = false; mc.Sleeping = false;
                            // SNAP to perfect alignment: square the container to the gantry (upright, yaw-aligned) and seat its
                            // roof magnet-point dead-centre on the coil face, so it hangs straight + centred, not cocked at the grab angle.
                            Vector3 curFwd = -mc.GlobalBasis.Z;   // keep the container facing roughly as it was: snap to the NEARER of the two aligned yaws (0 or 180), never a forced flip
                            Basis tgt = GlobalBasis;
                            if (curFwd.Dot(-tgt.Z) < 0f) tgt = tgt.Rotated(Vector3.Up, Mathf.Pi);
                            mc.GlobalBasis = tgt;
                            Vector3 seat = face - mc.MagnetPointWorld;
                            seat.Y = Mathf.Max(0f, seat.Y);   // centre X/Z + square, but NEVER push the container DOWN into the ground on connect (master); only lift it up to the coil
                            mc.GlobalPosition += seat;
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
        // The hoist assembly as SEPARATE boxes: block, rope cage (block-top->carriage), held container. Cast each on
        // its own and take the worst. A single MERGED AABB is huge (block high, container low) and shrinking it lifts
        // the leading face off the real geometry -- so a descent would miss the floor and drive the load underground.
        void AddHoistParts(List<Aabb> parts)
        {
            parts.Add(_hoist.GlobalTransform * _hoist.GetAabb());
            float cx = CarriageX + _trolleyX, hy = HoistRestY - _hoistDrop;
            var cage = new Aabb(new Vector3(cx - 0.9f, hy + 0.25f, -3f), new Vector3(1.8f, Mathf.Max(0.1f, CarriageAttachY - (hy + 0.25f)), 6f));
            parts.Add(GlobalTransform * cage);   // hoist + ropes collide with the horizontal beams as one unit
            if (_held != null && IsInstanceValid(_held)) { Aabb hb = _heldAabb; hb.Position += HoistFace - _faceAtGrab; parts.Add(hb); }   // the attached container is PART of the hoist
        }
        static Aabb WalkWorldAabb(Node n)
        {
            Aabb? acc = null;
            void W(Node k) { if (k is VisualInstance3D vi && vi.Visible) { var a = vi.GlobalTransform * vi.GetAabb(); acc = acc.HasValue ? acc.Value.Merge(a) : a; } foreach (var c in k.GetChildren()) W(c); }
            W(n);
            return acc ?? new Aabb();
        }
        static Rid FirstStaticBodyRid(Node n) { foreach (var c in n.GetChildren()) if (c is StaticBody3D sb) return sb.GetRid(); return default; }
        // Exclude whichever of the crane's own colliders MOVES with this cast (+ the held container always). What's NOT
        // excluded is HIT: e.g. the trolley/hoist casts leave the frame IN, so the hoist + its load stop against the frame.
        Godot.Collections.Array<Rid> CastEx(bool frame, bool trolley, bool hoist)
        {
            var a = new Godot.Collections.Array<Rid>();
            if (frame && _frameColliderRid.IsValid) a.Add(_frameColliderRid);
            if (trolley && _trolleyColliderRid.IsValid) a.Add(_trolleyColliderRid);
            if (hoist && _hoistColliderRid.IsValid) a.Add(_hoistColliderRid);
            if (_held != null && IsInstanceValid(_held)) a.Add(_held.GetRid());
            return a;
        }
        float CastBox(Vector3 center, Vector3 size, Basis basis, Vector3 motion, uint mask, Godot.Collections.Array<Rid> exclude)
        {
            if (motion.LengthSquared() < 1e-8f) return 1f;
            if (size.X < 0.05f || size.Y < 0.05f || size.Z < 0.05f) return 1f;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return 1f;
            float len = motion.Length();
            Vector3 dir = motion / len;
            const float margin = 1f;   // START the sweep this far BEHIND: cast_motion reports "free" from a start already
                                       // touching the obstacle, so holding the input TUNNELS through. Backing off keeps the
                                       // start clear (per-frame move << margin), then we subtract the back-off distance.
            var shape = new BoxShape3D { Size = size };
            var p = new PhysicsShapeQueryParameters3D { ShapeRid = shape.GetRid(), Transform = new Transform3D(basis, center - dir * margin), Motion = dir * (len + margin), CollisionMask = mask, CollideWithBodies = true, Exclude = exclude };
            float[] r = space.CastMotion(p);
            float safe = (r != null && r.Length > 0) ? r[0] : 1f;
            return Mathf.Clamp((safe * (len + margin) - margin) / len, 0f, 1f);
        }
        float SafeFrac(Vector3 motion, uint mask, Godot.Collections.Array<Rid> exclude)   // min over the hoist parts (each leading face on its real surface)
        {
            var parts = new List<Aabb>(); AddHoistParts(parts);
            float sf = 1f;
            foreach (var part in parts) sf = Mathf.Min(sf, CastBox(part.GetCenter(), part.Size * 0.97f, Basis.Identity, motion, mask, exclude));
            return sf;
        }
        float SafeFracWithFrame(Vector3 motion, uint mask, Godot.Collections.Array<Rid> exclude)   // + the frame boxes: the DRIVE moves the whole gantry
        {
            float sf = SafeFrac(motion, mask, exclude);
            Basis gb = GlobalTransform.Basis;
            foreach (var fb in _frameBoxes) sf = Mathf.Min(sf, CastBox(GlobalTransform * fb.c, fb.h * (2f * 0.95f), gb, motion, mask, exclude));
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
