using Godot;

namespace UnturnedGodot
{
    // An openable PROP door -- retail's InteractableObjectBinaryState (Binary_State objects: fridges,
    // cabinets, lockers), not a building Door. MVP: Fridge_0 only, SP-local, no power/owner/lock/MP -- see
    // Door.cs for the full-featured building door this steals its swing-EASING technique from (_PhysicsProcess
    // + Mathf.MoveToward a 0..1 _swing toward target). Unlike Door, there is no whole-body swing: a retail
    // Binary_State door leaf is a SINGLE-BONE skinned rig (one "Hinge" bone, weight 1.0 on every vertex), which
    // is a RIGID transform -- reproduced here as a pivoted MeshInstance3D rather than real Godot skeletal
    // animation. Mesh + hinge pivot/axis/angle/duration come from tools/extract_doors.py's doors.txt catalog.
    //
    // Node shape: ObjectDoor (this, a StaticBody3D so a direct-child CollisionShape3D actually collides --
    // Door.cs's own lesson: a shape parented under an intermediate Node3D is NOT owned by the body) ->
    // _pivot (Node3D at the hinge point) -> leaf MeshInstance3D (offset by -pivot, so pivot.Basis=Identity
    // reproduces the leaf's own extracted rest position exactly, and rotating pivot.Basis swings it around
    // the hinge like `rotated = R * (point - pivot) + pivot`).
    public partial class ObjectDoor : StaticBody3D
    {
        // catalog-supplied, set by Spawn() before this enters the tree
        Vector3 _pivotLocal;
        Vector3 _axis = Vector3.Back;
        float _angleDeg = 90f;
        float _duration = 0.5f;
        Mesh _leafMesh;
        Material _leafMaterial;
        bool _initialOpen;

        Node3D _pivot;
        StandardMaterial3D _leafMatInstance;   // a PER-DOOR instance (Duplicate of the shared prop material) so SetLookFocused's emission glow doesn't light up every fridge that shares the cached material
        float _swing;                          // 0 = closed, 1 = fully open

        public bool IsOpen { get; private set; }
        double _lastToggleSec = double.NegativeInfinity;
        const double CooldownSec = 0.35;   // re-toggle cooldown, like IOBS's interactabilityDelay gate (checkCanReset/isUsable) -- just enough to swallow key-repeat spam, not to block a mid-swing reversal

        /// <summary>Build a door on prop <paramref name="propXform"/> (the SAME placement Transform3D the
        /// prop's own body mesh uses -- pivot/leaf/collider are all expressed in that prop-local space, matching
        /// the catalog's coordinates). <paramref name="startOpen"/> is the --doortest UG_DOOR_OPEN hook: sets
        /// the leaf to its open pose on the FIRST frame with no animation to wait out.</summary>
        public static ObjectDoor Spawn(Node parent, Transform3D propXform, Vector3 pivotLocal, Vector3 axisLocal,
            float angleDeg, float durationSec, Mesh leafMesh, Material leafMaterial, bool startOpen = false)
        {
            var d = new ObjectDoor
            {
                Transform = propXform,
                _pivotLocal = pivotLocal,
                _axis = axisLocal.LengthSquared() > 1e-6f ? axisLocal.Normalized() : Vector3.Back,
                _angleDeg = angleDeg,
                _duration = Mathf.Max(0.05f, durationSec),
                _leafMesh = leafMesh,
                _leafMaterial = leafMaterial,
                _initialOpen = startOpen,
            };
            parent.AddChild(d);
            return d;
        }

        public override void _Ready()
        {
            // Small-prop LOOK-FOCUS layer (bit 6) -- already in PlayerController's look-ray mask (mirrors
            // GasPump.AddInteractionCollider's dedicated hit box). NOT the world/LOS layer: the prop's own
            // placed-mesh collider (built by WorldBuilder.PlaceObject) already blocks movement over the whole
            // fridge footprint, so this collider exists purely so a look-ray resolves to THIS node for F.
            CollisionLayer = 1u << 6;
            CollisionMask = 0;

            _pivot = new Node3D { Position = _pivotLocal };
            AddChild(_pivot);

            if (_leafMesh != null)
            {
                _leafMatInstance = (_leafMaterial as StandardMaterial3D)?.Duplicate() as StandardMaterial3D
                    ?? new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.6f, 0.6f) };
                // Ripped mesh: MUST disable backface culling or it renders inside-out under real (non-ambient)
                // lighting -- same rule every other extracted prop material follows (WorldBuilder.MatFor).
                _leafMatInstance.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                var leafMi = new MeshInstance3D { Mesh = _leafMesh, MaterialOverride = _leafMatInstance, Position = -_pivotLocal };
                _pivot.AddChild(leafMi);

                // A fixed (non-swinging) box collider sized from the leaf's OWN closed-pose AABB -- which is
                // already in ObjectDoor-local space (leafMi sits at ObjectDoor-local origin at rest: pivot at
                // +pivotLocal, leaf offset -pivotLocal, sum zero), so no extra offset math is needed here.
                var aabb = _leafMesh.GetAabb();
                var size = aabb.Size.Abs();
                AddChild(new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = new Vector3(Mathf.Max(size.X, 0.15f), Mathf.Max(size.Y, 0.15f), Mathf.Max(size.Z, 0.15f)) },
                    Position = aabb.GetCenter(),
                });
            }

            if (_initialOpen) SetInitialState(true);
        }

        /// <summary>Flip open/closed, gated by <see cref="CooldownSec"/>. Returns false (no-op) if still
        /// cooling down from the last toggle -- the caller (PlayerController) just drops a refused tap rather
        /// than queuing it, matching how a slow real appliance door ignores a second yank mid-swing.</summary>
        public bool Toggle()
        {
            double now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastToggleSec < CooldownSec) return false;
            _lastToggleSec = now;
            IsOpen = !IsOpen;
            return true;
        }

        /// <summary>Test/render hook (--doortest UG_DOOR_OPEN=1): jump straight to the open (or closed) pose,
        /// no eased animation to wait out. Safe to call before this is added to the tree (Spawn does it via
        /// the startOpen ctor param -> _Ready), or any time afterward.</summary>
        public void SetInitialState(bool open)
        {
            IsOpen = open;
            _swing = open ? 1f : 0f;
            ApplySwing(_swing);
        }

        public override void _PhysicsProcess(double delta)
        {
            float want = IsOpen ? 1f : 0f;
            if (Mathf.IsEqualApprox(_swing, want)) return;
            float step = (float)delta / _duration;
            _swing = Mathf.MoveToward(_swing, want, step);
            ApplySwing(_swing);
        }

        // Smoothstep on _swing for the eased feel the retail clip samples show (per-step deltas 3.05deg,
        // 9.86deg, 18.07deg... -- slow in, slow out, not linear), then rotate the pivot angle*eased about its
        // local axis. NOTE (unverified -- flag for the render): the extractor's raw/no-negate convention can
        // still invert a rotation's handedness through the parent chain, so this may swing the door INTO the
        // fridge instead of outward. If so, flip the SIGN of the angle in doors.txt (one number, no rebuild
        // of the mesh/pivot) -- see tools/extract_doors.py's module docstring.
        void ApplySwing(float t)
        {
            if (_pivot == null) return;
            float eased = t * t * (3f - 2f * t);
            _pivot.Basis = new Basis(_axis, Mathf.DegToRad(_angleDeg) * eased);
        }

        /// <summary>Look-focus highlight (F-focus outline), matching Door.SetLookFocused.</summary>
        public void SetLookFocused(bool on)
        {
            if (_leafMatInstance == null) return;
            _leafMatInstance.EmissionEnabled = on;
            _leafMatInstance.Emission = new Color(0.35f, 0.30f, 0.12f);
        }

        // --- test/debug seams ---
        public float DebugSwing => _swing;
    }
}
