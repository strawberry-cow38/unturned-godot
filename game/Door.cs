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
        public new ulong Owner => _state.Owner;   // `new`: intentionally shadows Node.Owner (the owning player's Steam ID, not the scene-tree owner)
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

        /// <summary>Server id of this door, 0 for a singleplayer/local one. Non-zero means the swing is the
        /// server's decision and arrives through <see cref="ApplyReplicatedToggle"/>. Setting it enrols the
        /// door in the lookup an inbound DoorState uses to find which node the server meant.</summary>
        public uint NetId
        {
            get => _netId;
            set
            {
                if (_netId != 0) _byNetId.Remove(_netId);
                _netId = value;
                if (value != 0) _byNetId[value] = this;
            }
        }
        uint _netId;

        static readonly System.Collections.Generic.Dictionary<uint, Door> _byNetId
            = new System.Collections.Generic.Dictionary<uint, Door>();

        /// <summary>Find the door the server is talking about. Misses are normal and not an error: a door
        /// outside this client's world (or already broken down locally) simply has nothing to swing.</summary>
        public static bool TryGetByNetId(uint netId, out Door door)
        {
            if (_byNetId.TryGetValue(netId, out door) && IsInstanceValid(door)) return true;
            _byNetId.Remove(netId);   // the node died without clearing its id (QueueFree from a break)
            door = null;
            return false;
        }

        /// <summary>Tests and world rebuilds share one static table; this stops one world inheriting the
        /// previous one's doors.</summary>
        public static void ResetNetIds() => _byNetId.Clear();

        // Keep the lookup honest across the node lifecycle, the way Bed keeps its claim table honest: a
        // broken-down door drops out on the way out, and a REPARENTED one puts itself back (an _ExitTree
        // that only removed would silently make a re-added door uncommandable). TryGetByNetId's validity
        // check is the backstop for a free that skips these, not the plan.
        public override void _EnterTree() { if (_netId != 0) _byNetId[_netId] = this; }
        public override void _ExitTree()
        {
            if (_netId != 0 && _byNetId.TryGetValue(_netId, out var held) && held == this) _byNetId.Remove(_netId);
        }

        /// <summary>The server says this door is now open/closed. Applied unconditionally -- no rule check,
        /// no cooldown, no arc test: the server already made the decision and second-guessing it here is how
        /// a replica ends up disagreeing with the world everyone else sees. Still makes the noise, so a door
        /// opened by another player pulls zombies on THIS client's sim exactly as it does on the server's.</summary>
        public void ApplyReplicatedToggle(bool open)
        {
            if (_state.IsOpen == open) return;
            _state.IsOpen = open;   // _PhysicsProcess eases the leaf toward the new target on its own
            if (IsInsideTree()) SoundBus.Emit(GetTree(), _hingeWorld, DoorLogic.ToggleLoudness);
        }

        /// <summary>The server says the lock state changed (ownership is unchanged -- only the bolt).</summary>
        public void ApplyReplicatedLocked(bool locked) => _state.Locked = locked;

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
            using var _prof = Prof.Scope("Door.phys");
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
