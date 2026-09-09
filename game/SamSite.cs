using Godot;

namespace UnturnedGodot
{
    // A SURFACE-TO-AIR MISSILE SITE (strawberry 2026-09-09: "make a functional SAM site. locks onto any heli within
    // its radius and launches a barrage of rockets that home onto the heli. launches 6 missiles, delay between each
    // one and a longer delay while it reloads").
    //
    // Four states, and they are a state MACHINE rather than a pile of timers because the barrage and the reload are
    // the same clock read two ways: a shot every ShotDelay until the rack is empty, then nothing for ReloadDelay.
    // Written as independent timers, "6 missiles" and "a longer delay while it reloads" drift apart the moment the
    // target is lost mid-barrage -- which is exactly when it matters.
    //
    //   Idle       nothing in range. The launcher parks level.
    //   Tracking   a heli is in range; the head turns to it and keeps turning. Fires when the rack is loaded.
    //   Firing     one missile per ShotDelay from the next loaded tube.
    //   Reloading  the tubes come BACK. This is the only state with a visible tell, and it is deliberate: a site
    //              with six tubes showing is a site that can shoot you, and that should be readable at a glance.
    //
    // The missiles do the hard part (SamMissile homes); the launcher only has to point somewhere sensible, which is
    // why the head is rate-limited rather than snapping. A turret that instantly faces its target reads as a cursor,
    // not a machine.
    public partial class SamSite : Node3D
    {
        public const float Radius = 260f;          // lock range, metres
        public const int   Rack = 6;               // "launches 6 missiles"
        public const float ShotDelay = 0.55f;      // "delay between each one"
        public const float ReloadDelay = 9f;       // "a longer delay while it reloads"
        public const float YawRateDeg = 90f;       // how fast the head slews
        public const float PitchRateDeg = 60f;
        public const float MinPitchDeg = -5f, MaxPitchDeg = 82f;

        public enum State { Idle, Tracking, Firing, Reloading }
        public State Mode { get; private set; } = State.Idle;
        public int Loaded { get; private set; } = Rack;      // tubes still holding a missile
        public Vehicle Target { get; private set; }          // the heli currently locked
        public int Fired { get; private set; }               // lifetime count, for the harness/tests

        /// <summary>Where the tubes are actually pointing, in world space. Exposed because an aim derivation is a
        /// SIGN, and a sign is not something to reason about twice -- the yaw here was written inverted first time
        /// and looks entirely plausible in a screenshot, because a launcher facing the mirror bearing is still a
        /// launcher pointing somewhere. The suite measures it against known bearings instead.</summary>
        public Vector3 AimDirection => _pitch != null ? -_pitch.GlobalTransform.Basis.Z : Vector3.Forward;

        Node3D _yaw, _pitch;
        readonly MeshInstance3D[] _tubes = new MeshInstance3D[Rack];
        readonly Node3D[] _muzzles = new Node3D[Rack];
        float _timer;         // seconds until the next shot / the end of the reload
        float _yawDeg, _pitchDeg;

        public override void _Ready()
        {
            AddToGroup("samsites");
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            Build();
        }

        public override void _ExitTree() => TickHub.RemoveProcess(this);

        // No retail SAM asset exists in the rip, so the launcher is built from primitives: a plinth, a mast, a
        // slewing head and two rows of three tubes. Deliberately blocky -- this is the shape of the thing, and a
        // shape that reads from 200 m is worth more here than detail nobody will be close enough to see.
        void Build()
        {
            var dark = new StandardMaterial3D { AlbedoColor = new Color(0.19f, 0.21f, 0.19f), Metallic = 0.2f, Roughness = 0.75f };
            var olive = new StandardMaterial3D { AlbedoColor = new Color(0.26f, 0.30f, 0.22f), Metallic = 0.15f, Roughness = 0.8f };
            var tube = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.34f, 0.30f), Metallic = 0.25f, Roughness = 0.6f };

            AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.7f, BottomRadius = 2.0f, Height = 0.7f }, MaterialOverride = dark, Position = new Vector3(0f, 0.35f, 0f) });

            _yaw = new Node3D { Position = new Vector3(0f, 0.7f, 0f) };
            AddChild(_yaw);
            _yaw.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.55f, BottomRadius = 0.75f, Height = 1.3f }, MaterialOverride = olive, Position = new Vector3(0f, 0.65f, 0f) });

            _pitch = new Node3D { Position = new Vector3(0f, 1.3f, 0f) };
            _yaw.AddChild(_pitch);
            _pitch.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.9f, 0.5f, 1.2f) }, MaterialOverride = olive });

            // Two rows of three, and the muzzle markers are CHILDREN of the pitch node rather than positions worked
            // out at launch: the missile then leaves the tube it came from at whatever attitude the head is holding,
            // with no second copy of the layout maths to drift.
            for (int i = 0; i < Rack; i++)
            {
                float x = (i % 3 - 1) * 0.62f, y = (i < 3 ? 0.34f : -0.02f);
                var t = new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.14f, BottomRadius = 0.14f, Height = 2.2f },
                    MaterialOverride = tube,
                    Position = new Vector3(x, y, 0f),
                    RotationDegrees = new Vector3(90f, 0f, 0f),   // cylinders stand on Y; lay it along the head's -Z
                };
                _pitch.AddChild(t);
                _tubes[i] = t;
                var m = new Node3D { Position = new Vector3(x, y, -1.2f) };
                _pitch.AddChild(m);
                _muzzles[i] = m;
            }
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess

        public void HubProcess(double delta)
        {
            float dt = (float)delta;
            Target = PickTarget();

            // AIM WHENEVER THERE IS SOMETHING TO AIM AT, including mid-reload. A launcher that stops tracking while
            // it reloads has to re-acquire from parked every nine seconds, and against something as fast as a heli
            // that means the first missile of every barrage is thrown at where the target used to be.
            if (Target != null && IsInstanceValid(Target)) AimAt(Target.GlobalPosition, dt);
            else Park(dt);   // no target: keep the bearing, bring the tubes back down

            _timer -= dt;
            switch (Mode)
            {
                case State.Idle:
                case State.Tracking:
                    Mode = Target == null ? State.Idle : State.Tracking;
                    if (Target != null && Loaded > 0) { Mode = State.Firing; _timer = 0f; }
                    break;

                case State.Firing:
                    // LOSING THE TARGET DOES NOT ABORT THE BARRAGE, it pauses it. The missiles already in the air
                    // are still homing, and a heli that ducks behind a hill for a second has not escaped a launcher
                    // that is still holding six tubes.
                    if (Loaded <= 0) { Mode = State.Reloading; _timer = ReloadDelay; break; }
                    if (Target == null) { Mode = State.Tracking; break; }
                    if (_timer <= 0f) { Launch(Target); _timer = ShotDelay; }
                    break;

                case State.Reloading:
                    if (_timer <= 0f)
                    {
                        Loaded = Rack;
                        foreach (var t in _tubes) if (t != null) t.Visible = true;
                        Mode = Target == null ? State.Idle : State.Tracking;
                    }
                    break;
            }
        }

        /// <summary>The nearest live helicopter inside the radius. Helis only -- a SAM site is not a sentry, and
        /// "any heli within its radius" is the whole rule.</summary>
        Vehicle PickTarget()
        {
            var tree = GetTree();
            if (tree == null) return null;
            Vehicle best = null;
            float bestD = Radius * Radius;
            foreach (var n in tree.GetNodesInGroup("vehicles"))
            {
                if (n is not Vehicle v || !v.IsHeli || v.Exploded || !IsInstanceValid(v)) continue;
                float d = GlobalPosition.DistanceSquaredTo(v.GlobalPosition);
                if (d < bestD) { bestD = d; best = v; }
            }
            return best;
        }

        /// <summary>Idle: hold the last bearing and lower the tubes to level. Written directly rather than by
        /// feeding AimAt a point reconstructed from _yawDeg -- that round-trip has to invert the yaw convention
        /// exactly, and getting it a sign out makes an idle launcher creep round on its own.</summary>
        void Park(float dt)
        {
            _pitchDeg += Mathf.Clamp(0f - _pitchDeg, -PitchRateDeg * dt, PitchRateDeg * dt);
            _pitch.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
        }

        /// <summary>Slew the head toward a world point, rate-limited on both axes.</summary>
        void AimAt(Vector3 world, float dt)
        {
            var to = world - _pitch.GlobalPosition;
            // atan2(-x, -z), NOT atan2(x, -z). A Y-rotation of t in Godot points the node's forward (-Z) at
            // (-sin t, 0, -cos t), so solving for t gives BOTH components negated -- get the X sign wrong and the
            // launcher tracks the mirror image of its target, which looks like a plausible turret right up until
            // you notice it is facing the wrong side of the map.
            float wantYaw = Mathf.RadToDeg(Mathf.Atan2(-to.X, -to.Z));
            float flat = new Vector2(to.X, to.Z).Length();
            float wantPitch = Mathf.Clamp(Mathf.RadToDeg(Mathf.Atan2(to.Y, Mathf.Max(0.01f, flat))), MinPitchDeg, MaxPitchDeg);

            // Through the SHORT way round: a target crossing behind the launcher is 359 degrees away by subtraction
            // and 1 degree away in reality, and the difference is a head that spins the wrong way for four seconds.
            float dy = Mathf.Wrap(wantYaw - _yawDeg, -180f, 180f);
            _yawDeg = Mathf.Wrap(_yawDeg + Mathf.Clamp(dy, -YawRateDeg * dt, YawRateDeg * dt), -180f, 180f);
            _pitchDeg += Mathf.Clamp(wantPitch - _pitchDeg, -PitchRateDeg * dt, PitchRateDeg * dt);

            _yaw.RotationDegrees = new Vector3(0f, _yawDeg, 0f);
            _pitch.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
        }

        void Launch(Vehicle at)
        {
            int i = Rack - Loaded;   // 0..5, so the top row empties left-to-right and then the bottom one does
            if (i < 0 || i >= Rack) return;
            var muzzle = _muzzles[i];
            if (_tubes[i] != null) _tubes[i].Visible = false;
            Loaded--;
            Fired++;
            var m = new SamMissile { Target = at };
            GetParent()?.AddChild(m);
            m.GlobalPosition = muzzle.GlobalPosition;
            m.Fire(-_pitch.GlobalTransform.Basis.Z);   // out of the tube along the head's facing; SamMissile takes over from there
        }

        /// <summary>Drop a site on the ground at a world point (console `sam`, the render harness).</summary>
        public static SamSite Spawn(Node world, Terrain terr, Vector3 at)
        {
            if (world == null) return null;
            var s = new SamSite();
            world.AddChild(s);
            float y = terr != null ? terr.SampleHeight(at.X, at.Z) : at.Y;
            s.GlobalPosition = new Vector3(at.X, y, at.Z);
            return s;
        }
    }
}
