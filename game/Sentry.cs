using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    public enum ESentryMode { NEUTRAL, FRIENDLY, HOSTILE }   // source ItemSentryAsset.ESentryMode

    // Auto-turret / SENTRY GUN -- source InteractableSentry + ItemSentryAsset. A deployable that mounts a gun, scans
    // for targets within detectionRadius (line-of-sight gated), tracks + auto-fires the mounted gun on its firerate,
    // and sweeps its yaw when it has no target. Retail's ItemSentryAsset defaults: Detection_Radius 48, Target_Loss
    // 48*1.2, Sweep_Yaw 120 (half=60), Sweep_Period TAU. Modes NEUTRAL/FRIENDLY/HOSTILE + per-type Can_Target flags.
    //
    // Increment 1 (this file): the SP core -- acquire the nearest LOS-clear ZombieController in range, aim the head at
    // it, fire the gun's Zombie_Damage on the gun's firerate cadence (a LOS raycast stands in for the gun's ballistic
    // projectile -- faithful at a LOS-confirmed 48 m target where a 500 m/s bullet lands in ~0.1 s), lose it past
    // targetLossRadius, sweep when idle. Power gating (requiresPower -> the power net) + a storage-selected gun +
    // players/animals/vehicles as targets are the next increments.
    public partial class Sentry : Node3D
    {
        public GunDef Gun;                                  // the mounted gun (Zombie_Damage / Range / Firerate)
        public float DetectionRadius = 48f;                 // ItemSentryAsset.detectionRadius default
        public float TargetLossRadius = 48f * 1.2f;         // won't drop a target inside this (anti-flicker)
        public float SweepHalfYaw = Mathf.DegToRad(60f);    // Sweep_Yaw 120 deg / 2
        public float SweepPeriod = Mathf.Tau;               // seconds for a full left->right->left sweep
        public bool CanTargetZombies = true;
        public ESentryMode Mode = ESentryMode.HOSTILE;

        Node3D _head;                 // yaw+pitch turret head
        Node3D _muzzle;               // fire origin (barrel tip)
        MeshInstance3D _tracer;       // brief shot line
        float _tracerT;
        ZombieController _target;
        float _fireCd;                // seconds until the next shot is allowed
        float _sweepT;                // sweep phase clock
        float _baseYaw;               // the placed forward yaw the sweep oscillates around

        public static Sentry Spawn(Node parent, Vector3 pos, float yawDeg, GunDef gun)
        {
            var s = new Sentry { Gun = gun, Position = pos, RotationDegrees = new Vector3(0f, yawDeg, 0f) };
            parent.AddChild(s);
            return s;
        }

        public override void _Ready()
        {
            _baseYaw = 0f;   // head yaw is LOCAL to the sentry's placed yaw
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.23f, 0.25f), Metallic = 0f, Roughness = 0.9f };
            // base pedestal
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.4f, 0.6f, 0.4f) }, Position = new Vector3(0f, 0.3f, 0f), MaterialOverride = mat });
            // rotating head + barrel
            _head = new Node3D { Position = new Vector3(0f, 0.72f, 0f) };
            AddChild(_head);
            _head.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.3f, 0.24f, 0.3f) }, MaterialOverride = mat });
            _head.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.07f, 0.6f) }, Position = new Vector3(0f, 0.02f, -0.4f), MaterialOverride = mat });   // barrel points -Z (forward)
            _muzzle = new Node3D { Position = new Vector3(0f, 0.02f, -0.7f) };
            _head.AddChild(_muzzle);
            _tracer = new MeshInstance3D { Visible = false, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.85f, 0.3f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha } };
            AddChild(_tracer);
        }

        public override void _Process(double delta)
        {
            float dt = (float)delta;
            if (_fireCd > 0f) _fireCd -= dt;
            if (_tracerT > 0f) { _tracerT -= dt; if (_tracerT <= 0f && _tracer != null) _tracer.Visible = false; }

            AcquireOrKeepTarget();
            if (_target != null && GodotObject.IsInstanceValid(_target))
            {
                AimAt(_target.GlobalPosition + Vector3.Up * 1.0f, dt);
                if (_fireCd <= 0f && LineOfSightClear(_target)) { Fire(_target); _fireCd = (Gun?.Firerate ?? 8) / 50f; }   // firerate = sim ticks between shots; cooldown = ticks/50 s (matches UseableGun)
            }
            else Sweep(dt);
        }

        // Keep the current target while it's alive + inside targetLossRadius; otherwise scan for the nearest LOS-clear
        // ZombieController within detectionRadius (source ScanForTargets: nearest valid target with a clear ray).
        void AcquireOrKeepTarget()
        {
            if (_target != null && GodotObject.IsInstanceValid(_target) && !_target.Dead
                && _target.GlobalPosition.DistanceTo(GlobalPosition) <= TargetLossRadius && LineOfSightClear(_target))
                return;
            _target = null;
            if (!CanTargetZombies) return;
            float best = DetectionRadius;
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
            {
                if (n is not ZombieController z) continue;
                if (!GodotObject.IsInstanceValid(z) || z.Dead) continue;
                float d = z.GlobalPosition.DistanceTo(GlobalPosition);
                if (d > best) continue;
                if (!LineOfSightClear(z)) continue;
                best = d; _target = z;
            }
        }

        // A ray from the muzzle to the target's chest: clear iff nothing solid is hit before the target (source uses
        // RayMasks.BLOCK_SENTRY = world geometry). We raycast excluding the sentry itself and treat a hit that ISN'T the
        // target as an obstruction.
        bool LineOfSightClear(ZombieController z)
        {
            if (_muzzle == null) return false;
            Vector3 from = _muzzle.GlobalPosition, to = z.GlobalPosition + Vector3.Up * 1.0f;
            var q = PhysicsRayQueryParameters3D.Create(from, to);
            q.CollideWithAreas = false;   // the sentry's own meshes carry no collider, so nothing to exclude
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return true;   // nothing between the muzzle and the target
            var collider = hit["collider"].As<GodotObject>();
            return collider == z || (collider is Node cn && cn.IsAncestorOf(z));   // first thing hit is the target -> clear
        }

        void AimAt(Vector3 worldPos, float dt)
        {
            if (_head == null) return;
            Vector3 local = ToLocal(worldPos);
            float wantYaw = Mathf.Atan2(-local.X, -local.Z);                                  // face the target in the XZ plane (barrel is -Z)
            float wantPitch = Mathf.Atan2(local.Y - 0.72f, new Vector2(local.X, local.Z).Length());
            var r = _head.Rotation;
            r.Y = Mathf.LerpAngle(r.Y, wantYaw, 8f * dt);
            r.X = Mathf.LerpAngle(r.X, Mathf.Clamp(wantPitch, -0.6f, 0.6f), 8f * dt);
            _head.Rotation = r;
        }

        void Sweep(float dt)
        {
            if (_head == null) return;
            _sweepT += dt;
            float yaw = _baseYaw + Mathf.Sin(_sweepT / SweepPeriod * Mathf.Tau) * SweepHalfYaw;
            var r = _head.Rotation;
            r.Y = Mathf.LerpAngle(r.Y, yaw, 4f * dt);
            r.X = Mathf.LerpAngle(r.X, 0f, 4f * dt);
            _head.Rotation = r;
        }

        void Fire(ZombieController z)
        {
            float dmg = Gun?.ZombieDamage ?? 40f;
            Vector3 point = z.GlobalPosition + Vector3.Up * 1.0f;
            z.DamageHit(dmg, point, (point - _muzzle.GlobalPosition).Normalized());
            ShowTracer(_muzzle.GlobalPosition, point);
        }

        void ShowTracer(Vector3 a, Vector3 b)
        {
            if (_tracer == null) return;
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            im.SurfaceAddVertex(ToLocal(a)); im.SurfaceAddVertex(ToLocal(b));
            im.SurfaceEnd();
            _tracer.Mesh = im; _tracer.Visible = true; _tracerT = 0.05f;
        }
    }
}
