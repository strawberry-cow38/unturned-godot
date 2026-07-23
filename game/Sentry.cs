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
    public partial class Sentry : Node3D, IPowerDevice
    {
        public GunDef Gun;                                  // the mounted gun (Zombie_Damage / Range / Firerate)
        public bool RequiresPower = true;                   // ItemSentryAsset.requiresPower default -- inert unless its port is fed
        public const float Watts = 50f;                     // a sentry sips power (consumer draw)
        public static readonly Vector3 PortLocal = new(0.22f, 0.3f, 0.18f);   // the Consumer port's local mount -- DeployableDef.Sentry mirrors this so the net schema + the node agree
        public uint NetId;                                  // MP: the server entity this view mirrors (0 = the SP/host authoritative node)
        public bool IsReplica;                              // MP: a client-side VIEW-ONLY replica -- renders + aims off the replicated zombies; the SERVER (ServerSentries) owns the scan/fire/DamageHit
        readonly System.Collections.Generic.List<ConnectionPort> _ports = new();
        ConnectionPort _powerPort;
        // IPowerDevice (a pure consumer, mirrors GasPump): the local PowerNet walks the "deployables" group + feeds ports.
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;                    // 0 for a direct SP/local sentry, the server NetId for a replica (Materialize) so an interactive wire routes over the wire -- mirrors GasPump (review H1). Was hardcoded 0 => RequestConnectWire rejects netId 0 -> no wire ever attaches -> every placed sentry stayed unpowered + inert.
        public System.Collections.Generic.IReadOnlyList<ConnectionPort> PowerPorts => _ports;
        public bool IsPowered => !RequiresPower || (_powerPort != null && GodotObject.IsInstanceValid(_powerPort) && _powerPort.Powered);
        public float DetectionRadius = 48f;                 // ItemSentryAsset.detectionRadius default
        public float TargetLossRadius = 48f * 1.2f;         // won't drop a target inside this (anti-flicker)
        public float SweepHalfYaw = Mathf.DegToRad(60f);    // Sweep_Yaw 120 deg / 2
        public float SweepPeriod = Mathf.Tau;               // seconds for a full left->right->left sweep
        public bool CanTargetZombies = true;
        public ESentryMode Mode = ESentryMode.NEUTRAL;   // source ItemSentryAsset default + vanilla Sentry.dat (Mode gates PLAYER targeting; zombies are gated by CanTargetZombies, so this doesn't change our zombie-only behavior)

        Node3D _head;                 // yaw+pitch turret head
        Node3D _muzzle;               // fire origin (barrel tip)
        MeshInstance3D _tracer;       // brief shot line
        float _tracerT;
        ZombieController _target;
        ulong _targetId;              // stable id (GetInstanceId) of the current target -- SentryTargeting keeps it across scans
        float _fireCd;                // seconds until the next shot is allowed
        float _sweepT;                // sweep phase clock
        float _baseYaw;               // the placed forward yaw the sweep oscillates around
        float _scanCd;                // ~10 Hz throttle on the acquire scan (source ScanForTargets cadence)
        readonly System.Collections.Generic.List<SentryTargeting.Candidate> _cands = new();          // reused per scan
        readonly System.Collections.Generic.Dictionary<ulong, ZombieController> _candNodes = new();  // id -> node, to resolve the chosen target back
        static UnityEngine.Vector3 ToU(Vector3 v) => new(v.X, v.Y, v.Z);   // Godot -> the sim helper's UnityEngine.Vector3
        static Vector3 ToG(UnityEngine.Vector3 v) => new(v.x, v.y, v.z);   // ...and back for the Godot raycast

        public static Sentry Spawn(Node parent, Vector3 pos, float yawDeg, GunDef gun)
        {
            var s = new Sentry { Gun = gun, Position = pos, RotationDegrees = new Vector3(0f, yawDeg, 0f) };
            parent.AddChild(s);
            return s;
        }

        // MP: the client's DeployableReplicaView calls this for a FixtureKind.Sentry entity -> a VIEW-ONLY turret that
        // renders + aims off the replicated zombies (Fire draws a tracer but NEVER DamageHit -- the server-side
        // ServerSentries owns the authoritative scan/fire/kill, running the SAME SentryTargeting). Cut 1 mounts a fixed
        // eaglefire (no gun-id on the wire yet -- a storage-selected gun is the follow-up). Mirrors OilPump.Materialize.
        public static Sentry Materialize(Node parent, Vector3 pos, float yawDegrees, uint netId)
        {
            var s = new Sentry { Gun = EaglefireGun(), Position = pos, RotationDegrees = new Vector3(0f, yawDegrees, 0f), NetId = netId, IsReplica = true };
            parent.AddChild(s);
            return s;
        }

        // the cut-1 fixed mount (until gun-id is on the wire): eaglefire, loaded once. On a replica the gun only drives
        // the aim's range clamp (GunRange) + the tracer cadence (Firerate); ZombieDamage is unused (no DamageHit). A
        // missing/failed load -> null -> the replica still aims (no clamp), so this never breaks materialization.
        static GunDef _eaglefire;
        static GunDef EaglefireGun()
        {
            if (_eaglefire != null) return _eaglefire;
            try { _eaglefire = GunDef.FromDatText(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/eaglefire.dat"))); }
            catch { _eaglefire = null; }
            return _eaglefire;
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

            // wire into the local power net as a consumer (source Requires_Power). Mirrors GasPump: a Consumer port in the
            // "deployables" group the PowerNet walks, + the lazily-spawned PowerManager. Unpowered -> IsPowered false -> inert.
            _powerPort = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = PortLocal, Watts = Watts }, "Sentry");
            AddChild(_powerPort);
            _ports.Add(_powerPort);
            AddToGroup("deployables");
            if (GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); GetParent().AddChild(pm); }
            PowerNet.MarkDirty();
        }

        public override void _Process(double delta)
        {
            float dt = (float)delta;
            if (_fireCd > 0f) _fireCd -= dt;
            if (_tracerT > 0f) { _tracerT -= dt; if (_tracerT <= 0f && _tracer != null) _tracer.Visible = false; }
            if (!IsPowered) return;   // an unpowered sentry is inert -- no scan, aim, fire, or sweep (source Requires_Power)

            AcquireOrKeepTarget(dt);
            if (_target != null && GodotObject.IsInstanceValid(_target))
            {
                AimAt(_target.GlobalPosition + Vector3.Up * AimHeight(_target), dt);
                if (_fireCd <= 0f && LineOfSightClear(_target)) { Fire(_target); _fireCd = (Gun?.Firerate ?? 8) / 50f * 3.33f; }   // source InteractableSentry: fireTime = firerateTicks/50 * 3.33 ("lower than normal firerate") -- a sentry fires 3.33x slower than a player holding the same gun
            }
            else Sweep(dt);
        }

        // The mounted gun's reach: source ScanForTargets clamps BOTH detection + target-loss to the weapon's range
        // (targetDistance = Min(detectionRadius, maxWeaponDistance)), so a short-range gun (shotgun) can't detect or
        // hitscan a zombie past where its bullet would actually reach. No gun -> no clamp.
        float GunRange => Gun != null ? Gun.Range : float.PositiveInfinity;

        // Aim/LOS/hit point offset per zombie speciality (source ScanForTargets switch: NORMAL 1.75, SPRINTER 1.0,
        // CRAWLER 0.25). Shared with the server via SentryTargeting.AimHeight(byte) so both sides hit the same point.
        static float AimHeight(ZombieController z) => SentryTargeting.AimHeight((byte)z.Speciality);

        // Pick the target through the SHARED SentryTargeting.ChooseTarget -- the SAME pure logic the server-side
        // ServerSentries runs, so the turret visibly tracks whatever the server actually shoots (MP_PLAN §3.1: keep the
        // current target while it's alive + inside targetLossRadius (clamped to gun range) + LOS-clear; else acquire the
        // nearest hunting, LOS-clear zombie inside detectionRadius that sits in the 60-degree forward arc). Throttled to
        // ~10 Hz (source ScanForTargets); between scans _target is kept + AimAt tracks it smoothly.
        // Candidates come from the live "zombies" nodes -- a loopback host / SP has the real brains; a REMOTE client's
        // puppets stay OUT of the group, so a remote replica finds none + sweeps (the kills still land server-side).
        void AcquireOrKeepTarget(float dt)
        {
            if (_target != null && (!GodotObject.IsInstanceValid(_target) || _target.Dead)) { _target = null; _targetId = 0; }
            if (_scanCd > 0f) { _scanCd -= dt; return; }     // between scans: keep _target (AimAt in _Process tracks it)
            _scanCd = 0.1f;
            if (!CanTargetZombies) { _target = null; _targetId = 0; return; }

            _cands.Clear(); _candNodes.Clear();
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
            {
                if (n is not ZombieController z || !GodotObject.IsInstanceValid(z) || z.Dead) continue;
                ulong id = z.GetInstanceId();
                _cands.Add(new SentryTargeting.Candidate(id, ToU(z.GlobalPosition), (byte)z.Speciality, z.IsHunting));
                _candNodes[id] = z;
            }
            Vector3 muzzle = _muzzle != null ? _muzzle.GlobalPosition : GlobalPosition;
            Vector3 aimFwd = _head != null ? -_head.GlobalTransform.Basis.Z : -GlobalTransform.Basis.Z;   // source aimTransform.forward
            ulong chosen = SentryTargeting.ChooseTarget(_cands, ToU(muzzle), ToU(aimFwd),
                DetectionRadius, TargetLossRadius, GunRange, (a, b) => WorldLosClear(ToG(a), ToG(b)), _targetId);
            _targetId = chosen;
            _target = chosen != 0 && _candNodes.TryGetValue(chosen, out var t) ? t : null;
        }

        // A ray from `from` to `to`: clear iff no WORLD geometry blocks it (source RayMasks.BLOCK_SENTRY = world only, so
        // a wall/terrain shields the target but OTHER zombies / their lingering corpse colliders never do -- that bug
        // left the last zombie of a cleared horde permanently un-targetable). This is the LOS-query SentryTargeting takes.
        bool WorldLosClear(Vector3 from, Vector3 to)
        {
            var q = PhysicsRayQueryParameters3D.Create(from, to, ZombieNav.WorldLayer);
            q.CollideWithAreas = false;
            return GetWorld3D().DirectSpaceState.IntersectRay(q).Count == 0;
        }

        // per-shot LOS re-check at fire time (muzzle -> the target's aim point)
        bool LineOfSightClear(ZombieController z) => _muzzle != null && WorldLosClear(_muzzle.GlobalPosition, z.GlobalPosition + Vector3.Up * AimHeight(z));

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
            Vector3 point = z.GlobalPosition + Vector3.Up * AimHeight(z);
            // the SP/host authoritative sentry applies the damage; a client REPLICA only draws the tracer -- the
            // server-side ServerSentries owns the DamageHit over the wire (a client must never apply server damage).
            if (!IsReplica) z.DamageHit(Gun?.ZombieDamage ?? 40f, point, (point - _muzzle.GlobalPosition).Normalized());
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
