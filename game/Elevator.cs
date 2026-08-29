using Godot;

namespace UnturnedGodot
{
    /// <summary>An interactive elevator/lift built from the Elevator_0 prop. Look at it and press Interact (F) and the
    /// car rides between a bottom and a top stop; an AnimatableBody3D with SyncToPhysics carries whatever CharacterBody
    /// stands on it. Focus + F mirrors the TV/lamp fixtures: a HitMeta on the body so PlayerController's look-ray finds
    /// it, SetLookFocused draws the white look outline, Call() toggles the destination. Master 2026-08-29: "theres an
    /// elevator prop -- wire it to move up/down on an interaction."</summary>
    public partial class Elevator : AnimatableBody3D
    {
        public static readonly StringName HitMeta = "elevator_hit";   // the look-ray finds THIS Elevator off its own body's meta (mirrors TVDevice.HitMeta)
        const float Travel = 4.0f;    // metres between the down (bottom) and up (top) stops
        const float MoveSpeed = 1.6f; // vertical m/s -- an elevator, not a rocket
        float _baseY, _targetY;       // bottom-stop world Y, and where we're currently heading
        bool _up;                     // false = at/heading to bottom, true = top
        bool _latched;                // has _baseY been fixed to the spawn Y yet?
        MeshInstance3D _mi;
        public float BaseLift;        // raise the node this far (metres) so the stood-up car's base rests on the ground
        MeshInstance3D _topBox, _rope;   // cosmetic winch: gray box fixed at the top of travel + a black rope down to the car
        Vector3 _cableTopLocal;          // node-local point the rope attaches to (car top-centre)
        float _anchorY;                  // world Y the fixed top box hangs the rope from
        public bool AutoCycle;           // demo: auto-reverse at each stop after a dwell (for the ride video)
        public float SpeedMul = 1f;      // demo: >1 speeds the car up (e.g. the fast up/down GIF)
        public float DwellTime = 1.5f;   // demo: dwell at each stop before reversing
        public float CarTopY;            // node-local Y of the car roof (a demo rider stands here)
        float _dwell = 1f;

        /// <summary>Assemble the elevator from Elevator_0.obj (+ its vertex-colour texture), a box collider off the mesh
        /// AABB, and the HitMeta tag. The caller positions it; _Ready latches that as the bottom stop.</summary>
        public static Elevator Build()
        {
            var e = new Elevator { SyncToPhysics = true };   // move it in _PhysicsProcess -> riders on top are carried by the solver
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var mesh = ObjMesh.Load(dir + "Elevator_0.obj");
            if (mesh == null) { GD.Print("[elevator] no Elevator_0.obj mesh"); return e; }
            var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
            string tp = dir + "Elevator_0_tex.png";
            if (Godot.FileAccess.FileExists(tp)) { var img = Image.LoadFromFile(tp); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            // ORIENT: the extracted Elevator_0.obj is tipped (measured AABB 5.0 x 4.4 x 3.9). Master's steer: the
            // machinery housing that sat on the +Z 'front' face belongs on TOP -- RotX -90 rolls +Z up to +Y so the
            // motor/winch sits atop the ~5 x 4.4 m car. Master 2026-08-29: "machinery on the front should be on top".
            var standUp = new Basis(Vector3.Right, -Mathf.Pi * 0.5f);
            e._mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Basis = standUp };
            e.AddChild(e._mi);
            Aabb ab = new Transform3D(standUp, Vector3.Zero) * mesh.GetAabb();   // bounds AFTER standing up
            // HOLLOW collider: a thin FLOOR slab (the car IS a hollow mesh, not a facade -- the 360 showed a doorway +
            // interior). A rider stands INSIDE on this floor and rides with the car, instead of on the roof off a solid
            // box. Walls are left to the mesh for now (add wall colliders if players walk out). Master 2026-08-29.
            const float FloorT = 0.25f;
            e.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(ab.Size.X, FloorT, ab.Size.Z) },
                Position = new Vector3(ab.Position.X + ab.Size.X * 0.5f, ab.Position.Y + FloorT * 0.5f, ab.Position.Z + ab.Size.Z * 0.5f) });
            e.BaseLift = -ab.Position.Y;   // node Y needed to sit the car's base on the ground
            e._cableTopLocal = new Vector3(ab.Position.X + ab.Size.X * 0.5f, ab.Position.Y + ab.Size.Y, ab.Position.Z + ab.Size.Z * 0.5f);   // top-centre of the car -> the rope hangs from here
            e.CarTopY = ab.Position.Y + ab.Size.Y;   // car roof height (node-local)
            e.SetMeta(HitMeta, e);   // the look-ray hits this body -> reads the meta -> this Elevator
            return e;
        }

        public override void _Ready() { LatchBase(); BuildCable(); }

        // Fix the bottom stop to wherever we were spawned, the FIRST time we're readied OR called -- so it never matters
        // whether Position was set before AddChild or a Call arrives before _Ready ran.
        void LatchBase() { if (!_latched) { _baseY = GlobalPosition.Y; _targetY = _baseY; _latched = true; } }

        /// <summary>F pressed while looking at the car: flip the destination between the bottom and top stops.</summary>
        public void Call() { LatchBase(); _up = !_up; _targetY = _baseY + (_up ? Travel : 0f); }

        public override void _PhysicsProcess(double delta)
        {
            var p = GlobalPosition;
            if (Mathf.Abs(p.Y - _targetY) < 0.0005f)   // parked at a stop
            {
                if (AutoCycle) { _dwell -= (float)delta; if (_dwell <= 0f) { _dwell = DwellTime; Call(); } }   // demo: dwell, then reverse
                return;
            }
            float ny = Mathf.MoveToward(p.Y, _targetY, MoveSpeed * SpeedMul * (float)delta);
            GlobalPosition = new Vector3(p.X, ny, p.Z);
            UpdateCable();
        }

        // COSMETIC winch (master 2026-08-29 "add a black rope with a gray box at the top of the lift's travel. purely
        // cosmetic."): a gray housing fixed at the top of travel + a black rope from it down to the car's top. Both are
        // TopLevel so they DON'T ride with the car; the rope is restretched between the (moving) car top and the (fixed)
        // box each physics tick, so it spools shorter as the car rises. No collision, no function.
        void BuildCable()
        {
            if (_topBox != null) return;
            float carTopUp = _baseY + Travel + _cableTopLocal.Y;   // world Y of the car's top when fully raised
            _anchorY = carTopUp + 0.5f;
            _topBox = new MeshInstance3D {
                Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.9f, 1.3f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.57f), Roughness = 0.9f },
                TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_topBox);
            _topBox.GlobalPosition = new Vector3(GlobalPosition.X + _cableTopLocal.X, _anchorY + 0.45f, GlobalPosition.Z + _cableTopLocal.Z);
            _rope = new MeshInstance3D {
                Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 1f, RadialSegments = 6 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.Black, Roughness = 1f },
                TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_rope);
            UpdateCable();
        }

        void UpdateCable()
        {
            if (_rope == null) return;
            Vector3 top = GlobalPosition + _cableTopLocal;   // node isn't rotated (only its mesh child is), so a plain add gives the world car-top
            float len = Mathf.Max(0.02f, _anchorY - top.Y);
            _rope.GlobalPosition = new Vector3(top.X, (top.Y + _anchorY) * 0.5f, top.Z);
            _rope.Scale = new Vector3(1f, len, 1f);   // CylinderMesh height 1 -> Y-scale = rope length
        }

        /// <summary>Whole-car white look outline on gain, same affordance the TV/lamp/monitor use (an interaction with
        /// no outline reads as scenery). Toggles the OutlineOverlay mask layer on the mesh.</summary>
        public void SetLookFocused(bool on)
        {
            if (_mi == null || !IsInstanceValid(_mi)) return;
            _mi.Layers = on ? (_mi.Layers | OutlineOverlay.OutlineLayer) : (_mi.Layers & ~OutlineOverlay.OutlineLayer);
        }
    }
}
