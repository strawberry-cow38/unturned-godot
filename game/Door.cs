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
        Vector3 _hingeWorld;           // fixed jamb point the leaf swings about (world space)
        CollisionShape3D _barrier;     // what stops you walking through a closed door
        Vector3 _size = new Vector3(1.0f, 2.0f, 0.12f);
        float _closedYaw, _swing;      // _swing: 0 = shut, 1 = fully open
        StandardMaterial3D _leafMat;
        BoxShape3D _arcShape;
        PhysicsShapeQueryParameters3D _arcQuery;   // reused; see _Ready

        /// <summary>Doors are barricades: they can be broken down. Without this a locked door was an
        /// absolute wall, which is not a base defence so much as a permanent one.</summary>
        public float Health = 250f, HealthMax = 250f;

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

            // The collider MUST be a direct child of this StaticBody3D. It used to hang under an
            // intermediate hinge Node3D, and Godot only owns shapes parented straight to the body -- so
            // the door had no collision at all: bullets and bodies passed clean through it. The swing is
            // therefore done by moving the BODY around a fixed hinge point (see ApplySwing) rather than
            // rotating an inner node.
            _hingeWorld = GlobalPosition + (Basis.Identity * Vector3.Right).Rotated(Vector3.Up, Mathf.DegToRad(_closedYaw)) * (-_size.X * 0.5f);

            _leafMat = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.29f, 0.17f), Roughness = 0.9f };
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = _size },
                MaterialOverride = _leafMat,
                Position = new Vector3(0f, _size.Y * 0.5f, 0f),
            });

            // Reused across toggles: this codebase already paid for per-frame query allocations once
            // (the "GC dips"), and there is no reason to re-learn it.
            _arcShape = new BoxShape3D { Size = new Vector3(_size.X, _size.Y, _size.X) };
            _arcQuery = new PhysicsShapeQueryParameters3D
            {
                Shape = _arcShape,
                CollisionMask = (1u << 1) | (1u << 6),   // enemies + players; static world may touch it
                CollideWithBodies = true,
                Exclude = new Godot.Collections.Array<Rid> { GetRid() },
            };

            _barrier = new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = _size },
                Position = new Vector3(0f, _size.Y * 0.5f, 0f),
            };
            AddChild(_barrier);   // direct child: this is what makes the door solid at all

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

            // Every toggle is heard. This is why you do not casually open doors in a town. Emitted at the
            // HINGE, not GlobalPosition: the body now swings, so its origin is a moving target while the
            // doorway a listener should localise is fixed.
            if (IsInsideTree()) SoundBus.Emit(GetTree(), _hingeWorld, DoorLogic.ToggleLoudness);
            return true;
        }

        public bool TrySetLocked(ulong player, bool locked) => DoorLogic.TrySetLocked(ref _state, player, locked);

        /// <summary>Break it down. Returns true if this destroyed the door.</summary>
        public bool TakeDamage(float amount)
        {
            if (amount <= 0f || Health <= 0f) return false;
            Health -= amount;
            if (Health > 0f) return false;
            Health = 0f;
            QueueFree();
            return true;
        }

        public bool IsDestroyed => Health <= 0f;

        /// <summary>Look-focus highlight, matching what vehicles and deployables do -- without it nothing
        /// tells the player a door is interactable.</summary>
        public void SetLookFocused(bool on)
        {
            if (_leafMat == null) return;
            _leafMat.EmissionEnabled = on;
            _leafMat.Emission = new Color(0.35f, 0.30f, 0.12f);
        }

        /// <summary>Is something standing where the leaf would sweep? Asked of the physics world, because
        /// this is the one part of the decision the sim genuinely cannot answer.</summary>
        bool ArcBlocked()
        {
            if (!IsInsideTree()) return false;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return false;

            if (_arcQuery == null) return false;
            _arcQuery.Transform = new Transform3D(Basis.Identity, _hingeWorld + Vector3.Up * (_size.Y * 0.5f));
            return space.IntersectShape(_arcQuery, 1).Count > 0;
        }

        public override void _PhysicsProcess(double delta)
        {
            float want = _state.IsOpen ? 1f : 0f;
            if (Mathf.IsEqualApprox(_swing, want)) return;
            float step = (float)delta / SwingSeconds;
            _swing = Mathf.MoveToward(_swing, want, step);
            ApplySwing(_swing);
        }

        // Swing the whole BODY about the fixed hinge point, so its (direct-child) collider swings with it.
        void ApplySwing(float t)
        {
            _swing = t;
            if (!IsInsideTree()) return;
            float yaw = _closedYaw + SwingDegrees * t;
            var outward = Vector3.Right.Rotated(Vector3.Up, Mathf.DegToRad(yaw));
            GlobalPosition = _hingeWorld + outward * (_size.X * 0.5f);
            RotationDegrees = new Vector3(0f, yaw, 0f);
        }

        // --- test seams -------------------------------------------------------------------------------
        public void DebugSettleSwing() { ApplySwing(_state.IsOpen ? 1f : 0f); }
        public bool DebugBarrierDisabled => _barrier != null && _barrier.Disabled;
        /// <summary>The fixed jamb point the leaf swings about -- where the door "is" for a listener.</summary>
        public Vector3 DebugHinge => _hingeWorld;
        public float DebugSwing => _swing;
    }
}
