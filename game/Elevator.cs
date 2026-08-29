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
            e.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = ab.Size }, Position = ab.Position + ab.Size * 0.5f });
            e.BaseLift = -ab.Position.Y;   // node Y needed to sit the car's base on the ground
            e.SetMeta(HitMeta, e);   // the look-ray hits this body -> reads the meta -> this Elevator
            return e;
        }

        public override void _Ready() { LatchBase(); }

        // Fix the bottom stop to wherever we were spawned, the FIRST time we're readied OR called -- so it never matters
        // whether Position was set before AddChild or a Call arrives before _Ready ran.
        void LatchBase() { if (!_latched) { _baseY = GlobalPosition.Y; _targetY = _baseY; _latched = true; } }

        /// <summary>F pressed while looking at the car: flip the destination between the bottom and top stops.</summary>
        public void Call() { LatchBase(); _up = !_up; _targetY = _baseY + (_up ? Travel : 0f); }

        public override void _PhysicsProcess(double delta)
        {
            var p = GlobalPosition;
            if (Mathf.Abs(p.Y - _targetY) < 0.0005f) return;   // parked at a stop -> nothing to do
            float ny = Mathf.MoveToward(p.Y, _targetY, MoveSpeed * (float)delta);
            GlobalPosition = new Vector3(p.X, ny, p.Z);
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
