using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // A hinged door. The DECISION half -- who may open it, and when -- lives engine-free in
    // SDG.Unturned.DoorLogic and is L0-tested there; this node owns the parts that genuinely need an
    // engine: the leaf that swings, the collider that stops you while it is shut, and the noise it makes.
    //
    // The noise matters more than it looks: a door toggle alerts at DoorLogic.ToggleLoudness, so opening
    // one inside a POI pulls zombies onto you. That routes through the same SoundBus a gunshot uses, so
    // the new zombie sim hears a door exactly the way it hears a rifle.
    public partial class Door : StaticBody3D
    {
        public const float SwingDegrees = 90f;
        const float SwingSeconds = 0.45f;

        DoorLogic.DoorState _state;
        Node3D _hinge;                 // the leaf pivots about this
        CollisionShape3D _barrier;     // what stops you walking through a closed door
        Vector3 _size = new Vector3(1.0f, 2.0f, 0.12f);
        float _closedYaw, _swing;      // _swing: 0 = shut, 1 = fully open

        public bool IsOpen => _state.IsOpen;
        public bool IsLocked => _state.Locked;
        public ulong Owner => _state.Owner;
        public ulong Group { get => _state.Group; set => _state.Group = value; }
        public DoorRefusal LastRefusal { get; private set; }

        /// <summary>Build a door standing at <paramref name="basePos"/>, facing <paramref name="yawDeg"/>.</summary>
        public static Door Spawn(Node parent, Vector3 basePos, float yawDeg, ulong owner, Vector3? size = null)
        {
            var d = new Door();
            if (size.HasValue) d._size = size.Value;
            d._state = new DoorLogic.DoorState { Owner = owner, LastToggled = double.NegativeInfinity };
            d._closedYaw = yawDeg;
            parent.AddChild(d);
            d.GlobalPosition = basePos;
            return d;
        }

        public override void _Ready()
        {
            CollisionLayer = 1 << 0;   // world geometry: blocks movement AND line of sight, like a wall
            CollisionMask = 0;

            _hinge = new Node3D();
            AddChild(_hinge);
            // Pivot at the jamb, not the middle -- a door hinged at its centre reads as a turnstile.
            _hinge.Position = new Vector3(-_size.X * 0.5f, 0f, 0f);

            var leaf = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = _size },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.29f, 0.17f), Roughness = 0.9f },
                Position = new Vector3(_size.X * 0.5f, _size.Y * 0.5f, 0f),
            };
            _hinge.AddChild(leaf);

            _barrier = new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = _size },
                Position = new Vector3(_size.X * 0.5f, _size.Y * 0.5f, 0f),
            };
            _hinge.AddChild(_barrier);

            RotationDegrees = new Vector3(0f, _closedYaw, 0f);
            ApplySwing(0f);
        }

        /// <summary>Try to open or close. Returns false and sets <see cref="LastRefusal"/> if the door
        /// will not move -- the caller turns that into a prompt rather than silence.</summary>
        public bool TryToggle(ulong player, ulong group, double now)
        {
            bool blocked = _state.IsOpen && ArcBlocked();   // only CLOSING can trap someone
            if (!DoorLogic.CanToggle(_state, player, group, now, blocked, out var why))
            {
                LastRefusal = why;
                return false;
            }
            LastRefusal = DoorRefusal.None;
            _state = DoorLogic.Toggle(_state, now);

            // Every toggle is heard. This is why you do not casually open doors in a town.
            if (IsInsideTree()) SoundBus.Emit(GetTree(), GlobalPosition, DoorLogic.ToggleLoudness);
            return true;
        }

        public bool TrySetLocked(ulong player, bool locked) => DoorLogic.TrySetLocked(ref _state, player, locked);

        /// <summary>Is something standing where the leaf would sweep? Asked of the physics world, because
        /// this is the one part of the decision the sim genuinely cannot answer.</summary>
        bool ArcBlocked()
        {
            if (!IsInsideTree()) return false;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return false;

            var shape = new BoxShape3D { Size = new Vector3(_size.X, _size.Y, _size.X) };
            var q = new PhysicsShapeQueryParameters3D
            {
                Shape = shape,
                Transform = new Transform3D(Basis.Identity, _hinge.GlobalPosition + Vector3.Up * (_size.Y * 0.5f)),
                CollisionMask = (1u << 1) | (1u << 6),   // enemies + players; static world is allowed to touch it
                CollideWithBodies = true,
                Exclude = new Godot.Collections.Array<Rid> { GetRid() },
            };
            return space.IntersectShape(q, 1).Count > 0;
        }

        public override void _PhysicsProcess(double delta)
        {
            float want = _state.IsOpen ? 1f : 0f;
            if (Mathf.IsEqualApprox(_swing, want)) return;
            float step = (float)delta / SwingSeconds;
            _swing = Mathf.MoveToward(_swing, want, step);
            ApplySwing(_swing);
        }

        void ApplySwing(float t)
        {
            _swing = t;
            if (_hinge != null) _hinge.RotationDegrees = new Vector3(0f, SwingDegrees * t, 0f);
            // The collider rides the leaf, so a fully open door stops blocking the gap it used to fill.
            if (_barrier != null) _barrier.Disabled = t > 0.85f;
        }

        // --- test seams -------------------------------------------------------------------------------
        public void DebugSettleSwing() { ApplySwing(_state.IsOpen ? 1f : 0f); }
        public bool DebugBarrierDisabled => _barrier != null && _barrier.Disabled;
        public float DebugSwing => _swing;
    }
}
