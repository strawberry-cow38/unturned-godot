using Godot;

namespace UnturnedGodot
{
    // An openable PROP door -- retail's InteractableObjectBinaryState (Binary_State objects: fridges,
    // cabinets, lockers), not a building Door. MVP: Fridge_0 only, SP-local, no power/owner/lock/MP -- see
    // Door.cs for the full-featured building door this steals its swing state machine from (_PhysicsProcess
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
        Material _leafMaterial; float _cull;
        bool _initialOpen;
        string _soundName;             // catalog-supplied AudioSource clip stem (e.g. "DoorHandle"/"HeavyMetalDoor"), content/sounds/<name>.wav -- null = silent door
        AudioStreamPlayer3D _audio;    // built once in _Ready if _soundName is set; replayed (not reloaded) on every Toggle -- retail plays the SAME clip for open + close, on toggle only, never on load/snap
        // retail legacy Animation clip easing curves (tools/extract_doors.py samples Root's "Open"/"Close"
        // clips for the Hinge bone's rotation-vs-time, normalized to (t_norm, frac) pairs where frac = angle /
        // finalAngle -- frac sweeps 0..~1 with the clip's own real overshoot, NOT a procedural smoothstep).
        // Either can be null (a prop with no sampled clip yet) -- SampleEasing falls back to smoothstep then.
        System.Collections.Generic.List<Vector2> _openCurve;
        System.Collections.Generic.List<Vector2> _closeCurve;

        Node3D _pivot;
        StandardMaterial3D _leafMatInstance;   // a PER-DOOR instance (Duplicate of the shared prop material) -- kept per-door so a leaf's own material state never bleeds across fridges that share the cached source material
        float _swing;                          // 0 = closed, 1 = fully open -- also doubles as the curve's t_norm (see SampleEasing)

        // Whole-PROP outline silhouette (the BODY mesh on OutlineOverlay's layer), built + assigned by
        // WorldBuilder.PlaceObject for a standalone doored prop. SetLookFocused toggles it alongside the leaf
        // glow, so looking anywhere on the prop highlights the WHOLE thing, not just the door (master). Null for
        // a container door (StoreShelf owns its own outline) or --doortest.
        public MeshInstance3D BodyOutline;
        MeshInstance3D _leafOutline;   // the swinging LEAF's own white outline (child of _pivot so it swings with the leaf); toggled by SetLookFocused so a doored prop highlights the WHOLE thing -- body outline + leaf outline together (master)

        public bool IsOpen { get; private set; }
        double _lastToggleSec = double.NegativeInfinity;
        const double CooldownSec = 0.35;   // re-toggle cooldown, like IOBS's interactabilityDelay gate (checkCanReset/isUsable) -- just enough to swallow key-repeat spam, not to block a mid-swing reversal

        // Other ObjectDoor leaves belonging to the SAME prop instance (including this one) -- e.g.
        // Wardrobe_0's Left/Right doors. Set by WorldBuilder.PlaceObject for a multi-leaf prop; null for
        // a single-leaf prop (Fridge_0), which never needs to sync anything. Toggling any one leaf syncs
        // every OTHER leaf in the group to the SAME open/closed target (a wardrobe opens both doors).
        System.Collections.Generic.List<ObjectDoor> _group;

        /// <summary>Wire this door into a set of siblings that open/close together. Pass the SAME list
        /// instance to every leaf of one prop (this leaf may or may not be included in it -- both are
        /// handled).</summary>
        public void SetGroup(System.Collections.Generic.List<ObjectDoor> group) => _group = group;

        /// <summary>Build a door on prop <paramref name="propXform"/> (the SAME placement Transform3D the
        /// prop's own body mesh uses -- pivot/leaf/collider are all expressed in that prop-local space, matching
        /// the catalog's coordinates). <paramref name="startOpen"/> is the --doortest UG_DOOR_OPEN hook: sets
        /// the leaf to its open pose on the FIRST frame with no animation to wait out. <paramref name="openCurve"/>
        /// / <paramref name="closeCurve"/> are the sampled retail easing curves (door_curves/*.txt via
        /// WorldBuilder.LoadDoorCurve); either may be null to fall back to a procedural smoothstep.</summary>
        public static ObjectDoor Spawn(Node parent, Transform3D propXform, Vector3 pivotLocal, Vector3 axisLocal,
            float angleDeg, float durationSec, Mesh leafMesh, Material leafMaterial, bool startOpen = false,
            System.Collections.Generic.List<Vector2> openCurve = null, System.Collections.Generic.List<Vector2> closeCurve = null,
            string soundName = null, float cullDistance = 0f)
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
                _openCurve = openCurve,
                _closeCurve = closeCurve,
                _soundName = soundName, _cull = cullDistance,
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
                var leafMi = new MeshInstance3D { Mesh = _leafMesh, MaterialOverride = _leafMatInstance, Position = -_pivotLocal }; if (_cull > 0f) { leafMi.VisibilityRangeEnd = _cull; leafMi.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled; }
                _pivot.AddChild(leafMi);
                // Whole-prop highlight: the leaf's OWN white outline silhouette on the OutlineOverlay layer, a child
                // of _pivot so it swings with the leaf. Hidden until SetLookFocused. Same recipe as StoreShelf._shelfGlow
                // (the body's outline) -- together they light up the entire prop, body + door, when looked at (master).
                _leafOutline = OutlineOverlay.MakeOutline(_leafMesh, new Transform3D(Basis.Identity, -_pivotLocal));
                _pivot.AddChild(_leafOutline);

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

            if (!string.IsNullOrEmpty(_soundName))
            {
                // Same runtime WAV loader the consumables use (PlayerController.LoadWavOneShot walks the RIFF
                // chunks rather than assuming a fixed 44-byte header, which the UnityPy-exported WAVs need).
                // Built ONCE here and replayed (not reloaded from disk) on every Toggle.
                var stream = PlayerController.LoadWavOneShot($"res://content/sounds/{_soundName}.wav");
                if (stream != null)
                {
                    // Retail InteractableObjectBinaryState plays this AudioSource at its authored m_Volume,
                    // which is 0.125 (linear) for BOTH DoorHandle and HeavyMetalDoor -- confirmed against
                    // every prop currently in doors.txt via tools/check_door_sounds.py. LinearToDb(0.125) ~= -18 dB.
                    _audio = new AudioStreamPlayer3D { Stream = stream, VolumeDb = Mathf.LinearToDb(0.125f), UnitSize = 6f };
                    AddChild(_audio);
                }
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
            SyncGroup();
            // Retail plays the door's AudioSource clip on every TOGGLE (open AND close, same clip) -- NOT on
            // load/snap (SetInitialState never touches _audio). Only THIS directly-toggled leaf plays: a
            // multi-leaf prop's other leaves are brought along by SyncGroup() above, which sets IsOpen
            // directly and never calls Play() -- so a double door fires exactly one sound per F-press, not two.
            _audio?.Play();
            return true;
        }

        /// <summary>Drive to an EXPLICIT open/closed target (no cooldown, no flip) -- for a door owned by
        /// something else (StoreShelf swings a container's door with its inventory open/close). Idempotent:
        /// a no-op if already in that state. Plays the audio + brings the multi-leaf group along only on a
        /// real change, so reopening a container mid-swing lands deterministically instead of a Toggle()
        /// flip that could refuse on the cooldown gate and desync door-vs-inventory.</summary>
        public void SetOpen(bool open)
        {
            if (IsOpen == open) return;
            IsOpen = open;
            SyncGroup();
            _audio?.Play();
        }

        /// <summary>Bring every OTHER door in this leaf's group to the SAME open/closed state THIS one
        /// just toggled to -- directly (no cooldown check, no recursive Toggle/SyncGroup call): the
        /// trigger already passed the cooldown gate, and re-entering Toggle on a sibling would try to
        /// sync the group AGAIN, including back to this door. A private setter write is fine here since
        /// C# access control is per-CLASS, not per-instance -- any ObjectDoor can set IsOpen on any other.</summary>
        void SyncGroup()
        {
            if (_group == null) return;
            foreach (var sib in _group)
            {
                if (sib == this || !IsInstanceValid(sib) || sib.IsOpen == IsOpen) continue;
                sib.IsOpen = IsOpen;
                sib._lastToggleSec = Time.GetTicksMsec() / 1000.0;
            }
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

        // Sample the retail clip's OWN easing (SampleEasing) instead of a procedural formula, then rotate the
        // pivot angle*frac about its local axis. NOTE (unverified -- flag for the render): the extractor's
        // raw/no-negate convention can still invert a rotation's handedness through the parent chain, so this
        // may swing the door INTO the fridge instead of outward. If so, flip the SIGN of the angle in doors.txt
        // (one number, no rebuild of the mesh/pivot) -- see tools/extract_doors.py's module docstring.
        void ApplySwing(float t)
        {
            if (_pivot == null) return;
            float frac = SampleEasing(t);
            _pivot.Basis = new Basis(_axis, Mathf.DegToRad(_angleDeg) * frac);
        }

        /// <summary>_swing (0=closed..1=open) doubles as the curve's t_norm: OPENING plays _openCurve directly
        /// (_swing counts 0-&gt;1 exactly matching that curve's own recorded t_norm -- see extract_doors.py's
        /// "ROLE open" derivation). CLOSING plays _closeCurve at (1-_swing): that curve's own t_norm=0 is
        /// "just started closing" (_swing was at 1) and t_norm=1 is "fully closed" (_swing=0), so as _swing
        /// falls 1-&gt;0 during a close, (1-_swing) correctly rises 0-&gt;1 through the close curve. Falls back to
        /// a plain smoothstep if this prop has no sampled clip for the current direction (graceful
        /// degradation, not a hard requirement that every door prop ships curve data).</summary>
        float SampleEasing(float swing)
        {
            bool opening = IsOpen;
            var curve = opening ? _openCurve : _closeCurve;
            float sampleAt = opening ? swing : (1f - swing);
            if (curve == null || curve.Count == 0) return swing * swing * (3f - 2f * swing);
            if (curve.Count == 1) return curve[0].Y;
            if (sampleAt <= curve[0].X) return curve[0].Y;
            int last = curve.Count - 1;
            if (sampleAt >= curve[last].X) return curve[last].Y;
            for (int i = 0; i < last; i++)
            {
                Vector2 a = curve[i], b = curve[i + 1];
                if (sampleAt >= a.X && sampleAt <= b.X)
                {
                    float span = b.X - a.X;
                    float u = span > 1e-6f ? (sampleAt - a.X) / span : 0f;
                    return Mathf.Lerp(a.Y, b.Y, u);
                }
            }
            return curve[last].Y;
        }

        /// <summary>Look-focus highlight (F-focus outline), matching Door.SetLookFocused.</summary>
        public void SetLookFocused(bool on)
        {
            // Same shared-static trap as StoreShelf: the outline shader tints from WorldItem.FocusColor, which every
            // focusable sets on gain and none clears on loss. A STANDALONE doored prop (a wardrobe placed outside the
            // container path) lit its silhouette without claiming the colour, so it wore the last item's rarity.
            // Harmless to set again when a StoreShelf already did -- both want white.
            OutlineOverlay.ShowOutline(on, Colors.White, BodyOutline, _leafOutline);   // whole prop = body + swinging leaf, one white claim (master: the door highlights the whole thing)
        }

        // --- test/debug seams ---
        public float DebugSwing => _swing;
        public float DebugSampleEasing(float swing) => SampleEasing(swing);
        public bool DebugHasAudio => _audio != null;   // test: the catalog's sound field resolved to a WAV that actually parsed
    }
}
