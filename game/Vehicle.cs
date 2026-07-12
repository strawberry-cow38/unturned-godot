using Godot;

namespace UnturnedGodot
{
    // Drivable vehicle. Source: InteractableVehicle + VehicleAsset (WheelCollider rig -> Godot VehicleBody3D +
    // VehicleWheel3D 1:1). Meshes ripped by tools/extract_vehicle_mesh.py; params + real _PaintColor from the .dat.
    public partial class Vehicle : VehicleBody3D
    {
        float _engineForce = 600f;                  // acceleration feel (calibrated: Unity WheelCollider torque doesn't map 1:1)
        float _steerMax = 28f, _steerMin = 14f;      // Steer_Max (at rest) .. Steer_Min (at full speed), degrees -- source .dat
        float _speedMax = 12.5f, _speedMin = -7f;    // Speed_Max fwd / Speed_Min reverse, m/s -- source .dat (directly usable)
        float _brakeForce = 32f;                     // Brake -- source .dat value
        float _steerTarget, _steerAngle, _steerTurnSpeed = 140f;   // steering smoothing: MoveTowards target at SteeringAngleTurnSpeed deg/s (source: SteerMax*5)
        bool _parked, _handbraking; float _spawnGrace = 2.5f;   // parked/handbraked + settled -> kinematic freeze (source isKinematic, no jitter); _spawnGrace lets a fresh car DROP to terrain first
        float _prevSpeed;   // last frame's speed, to detect a sudden drop = a crash (collision/ram damage)
        float _deadTimer = -1f; bool _exploded; CpuParticles3D _smoke, _fire; OmniLight3D _fireLight; MeshInstance3D _bodyMesh; AudioStreamPlayer3D _explosionAudio; Vector3 _firePos;   // damage/explosion (source askDamage/explode); _firePos = engine-bay local offset
        const float ExplodeDelay = 4f, SmokeHealth = 200f, HeavySmokeHealth = 100f;   // source EXPLODE=4s, SMOKE_1<200, SMOKE_0<100
        const float FootBrakeScale = 6f, HandbrakeScale = 13f;   // Godot Brake calibration (raw .dat Brake too weak, but 15/35 flipped the car onto its nose -- master); S foot-brake vs Space handbrake bite
        public bool Exploded => _exploded;
        VehicleWheel3D[] _wNodes; MeshInstance3D[] _wMeshes; float[] _wRoll, _wSign;   // wheels for visual spin
        Mesh _wheelMeshRef; Material _wheelMatRef; float _wheelR;   // kept so the wheels can fly off as debris on explode
        public static float GlobalMass = 900f;   // all vehicles share one mass (the source does: Rigidbody mass = 2.0 for every vehicle)
        float[] _gears; float _reverseGear, _shiftUpRpm; float _engineRpm = 1000f; int _gear = 1;   // engine RPM + gear sim
        AudioStreamPlayer3D _engineAudio; float _idlePitch = 1f, _maxPitch = 2f, _idleVol = 0.75f, _maxVol = 1f;   // EngineRPMSimple sound
        const float IdleRpm = 1000f, MaxRpm = 6000f;   // source EngineIdleRPM / EngineMaxRPM
        public float EngineRpm => _engineRpm;
        public string GearLabel => LinearVelocity.Dot(-GlobalTransform.Basis.Z) < -0.5f ? "R" : $"G{_gear}";   // gear read-out: R when reversing, else G<n>
        public float EngineRpmNorm => Mathf.Clamp((_engineRpm - IdleRpm) / (MaxRpm - IdleRpm), 0f, 1f);
        public int Gear => _gear;
        // vehicle status for the HUD (source InteractableVehicle): fuel drains while the engine's on; health = damage; battery = accessories
        public float Fuel, FuelMax, Health, HealthMax, Battery;
        public bool EngineOn; public string DisplayName;
        public Vector3 BodyExtents, BodyCenter;   // BoxCollider half-size + centre (local) -> zombies reach for the body SURFACE, not the centre
        const float FuelBurnRate = 2.05f, BatteryMax = 10000f;   // EEngine.CAR default fuelBurnRate/sec; battery full = 10000
        public float FuelNorm => FuelMax > 0f ? Fuel / FuelMax : 0f;
        public float HealthNorm => HealthMax > 0f ? Health / HealthMax : 0f;
        public float BatteryNorm => Battery / BatteryMax;
        Node3D _headlights; bool _headlightsOn; StandardMaterial3D _headlightMat;   // headlights ('L'): source "Headlights" node (2 spot + 1 omni) + emission + battery burn
        Node3D _taillights; bool _taillightsOn; StandardMaterial3D _taillightMat;   // running taillights: red glow while driven (source synchronizeTaillights = isDriven && canTurnOnLights)
        AudioStreamPlayer3D _hornAudio; float _hornCd;   // horn (LMB): one-shot the .dat HornAudioClip, 0.5s cooldown (source canUseHorn)
        Node3D _steerPivot; Vector3 _steerAxis;   // steering wheel model (source Objects/Steer): rotates by the steer angle around the disc normal
        const float BatteryBurnRate = 20f;   // source batteryBurnRate default (headlights drain while on, EBatteryMode.Burn)
        // Bumper roadkill (source Bumper.OnTriggerEnter + VehicleAsset ParseFloat defaults): a moving vehicle damages a
        // character its front bumper touches. dmg = floor(baseDamage * speed); speed = clamp(fwdVel * mult, -10, 10),
        // ignored below the threshold. None of the stock vehicles override these in their .dat, so the defaults hold.
        const float BumperMult = 1f, BumperThreshold = 3f, BumperZombieDmg = 15f, BumperPlayerDmg = 10f, BumperSelfMult = 1f;
        const float HornAlertRadius = 32f;   // source InteractableVehicle.tellHorn: AlertTool.alert(pos, 32) -> zombies within earshot investigate
        public bool HeadlightsOn => _headlightsOn;

        struct Spec
        {
            public string Body, Wheel, WheelTex, Palette;   // Palette = paintable palette; WheelTex = wheel albedo
            public string[] DefaultPaints;   // source .dat DefaultPaintColors (random on spawn); null + !RandomHueGray = unpainted white
            public bool RandomHueGray;       // source RandomHueOrGrayscale mode (quad/sedan/hatchback)
            public float WheelRadius, Engine, SteerMax, SteerMin, SpeedMax, SpeedMin, Brake;
            public Vector3 BoxSize, BoxCenter;   // source BoxCollider (Godot space: center Z negated)
            public float[] ForwardGears;   // .dat ForwardGearRatios (engine RPM = wheelRPM * ratio)
            public float ReverseGear, ShiftUpRpm;   // .dat ReverseGearRatio + GearShift_UpThresholdRPM
            public string Sound;   // engine loop ogg basename (source: the prefab's AudioSource m_audioClip)
            public float IdlePitch, MaxPitch, IdleVolume, MaxVolume;   // .dat EngineSound (EngineRPMSimple)
            public float Fuel, Health;   // .dat Fuel / Health capacities (HUD gauges)
            public string Name;   // display name (English.dat) for the HUD title
            public Vector3[] SpotPos; public Vector3 OmniPos;   // headlight spot beams + omni fill (prefab "Headlights", Godot space); null = no lights yet
            public Vector3[] TailPos;   // taillight spot positions (prefab "Taillights", rear, Godot space); null = emission-only
            public string Horn;   // .dat HornAudioClip ogg (one-shot on LMB)
            public Vector3 SteerPivot, SteerAxis;   // steering wheel model pivot (centroid) + rotation axis (disc normal); Zero = don't rotate
            public (float x, float y, float z, bool steer)[] Wheels;
            public (string txt, Color color)[] Parts;   // detail meshes (root-relative) with their real solid colours
        }

        static StandardMaterial3D SolidMat(Color c) =>
            new() { AlbedoColor = c, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        // billboarded smoke/fire particle burst (damage smoke = grey rising; explosion fire = orange emissive)
        static CpuParticles3D MakeSmoke(Color c, float life, float vel, int amount, bool fire)
        {
            var mat = new StandardMaterial3D
            {
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
                AlbedoColor = new Color(1f, 1f, 1f, fire ? 0.9f : 0.5f),
            };
            if (fire) { mat.EmissionEnabled = true; mat.Emission = new Color(1f, 0.4f, 0.05f); mat.EmissionEnergyMultiplier = 3f; }
            return new CpuParticles3D
            {
                Emitting = false, Amount = amount, Lifetime = life, Direction = Vector3.Up, Spread = 25f,
                InitialVelocityMin = vel * 0.6f, InitialVelocityMax = vel, Gravity = new Vector3(0f, 1.5f, 0f),
                ScaleAmountMin = 0.4f, ScaleAmountMax = 1.3f, Color = c, Mesh = new QuadMesh { Size = new Vector2(0.7f, 0.7f), Material = mat },
            };
        }

        // source Bumper.OnTriggerEnter: the front bumper roadkills a character it drives into. Damage scales with impact
        // speed (clamped at 10) x the base BumperZombieDamage; the vehicle takes a little self-damage per hit too.
        void OnBumperHit(Node3D body)
        {
            if (_exploded || _parked) return;
            if (body is ZombieController z && !z.Dead)
            {
                float fwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);       // signed forward speed (front = -Z)
                float speed = Mathf.Clamp(fwd * BumperMult, -10f, 10f);
                if (speed < BumperThreshold) return;                            // too slow to hurt (source threshold gate)
                float dmg = Mathf.Floor(BumperZombieDmg * speed);               // source DamageTool: floor(damage * times)
                z.DamageHit(dmg, z.GlobalPosition, -GlobalTransform.Basis.Z);   // knock the ragdoll forward
                TakeDamage(2f * BumperSelfMult);                                // source takeCrashDamage(2)
                GD.Print($"[ROADKILL] speed={speed:0.0} dmg={dmg} -> zombie HP {z.Health:0}");
            }
            // NOTE: source Bumper also roadkills Players ("Player" tag -> BumperPlayerDamage) and Animals (Animal on the
            // "Agent" tag -> BumperAnimalDamage) the same way. No player/animal targets share a scene in the port yet,
            // so only the zombie path is wired + tested here; add those branches when those entities co-exist.
        }

        public void TakeDamage(float amount)   // source askDamage: reduce health; at 0 the EXPLODE timer starts
        {
            if (_exploded || amount <= 0f) return;
            Health = Mathf.Max(0f, Health - amount);
            if (Health <= 0f && _deadTimer < 0f) _deadTimer = ExplodeDelay;
        }

        void Explode()   // source explode: launch up + spin, fire on, char the body, disable
        {
            _exploded = true;
            Freeze = false;   // unfreeze the parked/kinematic car so the wreck flies + tumbles
            ApplyCentralImpulse(Vector3.Up * 6000f);          // source min/maxExplosionForce (default straight up), calibrated for the Godot mass
            ApplyTorqueImpulse(new Vector3(2800f, 0f, 0f));   // source AddTorque(16,0,0)
            EngineOn = false;
            if (_fire != null) _fire.Emitting = true;
            if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 3f; }
            _explosionAudio?.Play();
            if (_bodyMesh != null) _bodyMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.05f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };   // charred wreck
            SpawnWheelDebris();
            ExplodeDamage();
        }

        // source InteractableVehicle explode: DamageTool.explode(pos, radius 8, playerDmg 200, zombieDmg 200, vehicleDmg 500).
        // The 500 vehicle damage easily blows a neighbouring car too -> a staggered chain reaction; 200 wipes a nearby horde.
        void ExplodeDamage()
        {
            const float R = 8f;
            Vector3 p = GlobalPosition;
            PlayerController.Local?.FlinchFromExplosion(p, 32f, 45f);   // big vehicle blast -> strong camera shake (src Bomb_0-like: radius 32 / mag 45)
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    float d = z.GlobalPosition.DistanceTo(p);
                    if (d <= R) z.DamageHit(200f * (1f - d / R), z.GlobalPosition, (z.GlobalPosition - p).Normalized());
                }
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && v != this && !v.Exploded)
                {
                    float d = v.GlobalPosition.DistanceTo(p);
                    if (d <= R) v.TakeDamage(500f * (1f - d / R));   // chain: 500 easily blows the next car too
                }
            foreach (var n in GetTree().GetNodesInGroup("players"))
                if (n is PlayerController pl)
                {
                    float d = pl.GlobalPosition.DistanceTo(p);
                    if (d <= R) pl.TakeDamage(200f * (1f - d / R));
                }
        }

        void SpawnWheelDebris()   // source canExplode: the wheels fly off when the vehicle blows up
        {
            if (_wNodes == null || _wheelMeshRef == null) return;
            Node scene = GetTree()?.CurrentScene ?? GetParent();
            if (scene == null) return;
            var rng = new RandomNumberGenerator(); rng.Randomize();
            for (int i = 0; i < _wNodes.Length; i++)
            {
                var pos = _wMeshes[i].GlobalPosition;
                var mat = (StandardMaterial3D)_wheelMatRef.Duplicate();   // per-debris material so the 10s fade doesn't touch the car's own wheels
                var rb = new WheelDebris { Mass = 18f, Mat = mat };       // lives ~10s, fades its last second, then despawns (master)
                rb.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = _wheelR } });
                rb.AddChild(new MeshInstance3D { Mesh = _wheelMeshRef, MaterialOverride = mat, Scale = _wMeshes[i].Scale });
                scene.AddChild(rb);
                rb.GlobalPosition = pos;
                var outward = pos - GlobalPosition; outward.Y = 0f;
                outward = outward.LengthSquared() > 0.01f ? outward.Normalized() : Vector3.Right;
                rb.ApplyCentralImpulse(outward * 45f + Vector3.Up * 55f + new Vector3(rng.RandfRange(-15f, 15f), 0f, rng.RandfRange(-15f, 15f)));
                rb.AngularVelocity = new Vector3(rng.RandfRange(-12f, 12f), rng.RandfRange(-12f, 12f), rng.RandfRange(-12f, 12f));
                _wMeshes[i].Visible = false;   // hide the wheel still on the car
            }
        }

        // Unturned paintable shading: body samples the palette, paintable texels tinted by _PaintColor.
        static ShaderMaterial PaintMat(string palette, Color paint)
        {
            var sh = new Shader { Code = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/vehicle_paint.gdshader")) };
            var m = new ShaderMaterial { Shader = sh };
            var img = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{palette}"));
            m.SetShaderParameter("palette", ImageTexture.CreateFromImage(img));
            var lin = paint.SrgbToLinear();   // ALBEDO is linear; the palette texels already come through source_color (sRGB->linear), but the raw paint Vector3 did not -> #437c44 rendered as a washed-out light green. Convert so it shows true deep forest (master: "our render is diff")
            m.SetShaderParameter("paint_color", new Vector3(lin.R, lin.G, lin.B));
            return m;
        }

        // Curated natural car colours for RandomHueOrGrayscale vehicles -- the source's random-hue goes neon, so master
        // wants a hand-picked natural set. Tuned for the CORRECTED (sRGB->linear) paint render -- the hexes are the lighter
        // tones master wants on-screen (the old darker set only looked right under the washed-out pre-fix render).
        static readonly string[] CarColors = { "#ececec", "#4c4c4c", "#c2c2c2", "#7d8288", "#b23a3a", "#425f9c", "#4f7d4f", "#c4b498", "#969b74" };

        // Source paint on spawn: unpainted -> white; List -> a random .dat DefaultPaintColor; the source's
        // RandomHueOrGrayscale is swapped for a random pick from the curated CarColors (natural, not neon).
        static Color SpawnPaint(Spec s, int variant)   // variant = the spawn's colour index (deterministic, per instance)
        {
            if (s.RandomHueGray)
                return new Color(CarColors[variant % CarColors.Length]);
            if (s.DefaultPaints != null && s.DefaultPaints.Length > 0)
                return new Color(s.DefaultPaints[variant % s.DefaultPaints.Length]);
            return Colors.White;   // no default paint -> unpainted white (e.g. the bus is #d4d4d4, near-white)
        }

        // Jeep.dat: Speed 12.5, steer 28, front-steered, torque 2.8. Godot space (front = -Z): X +-1.30, front Z -1.40.
        static readonly Spec _jeep = new()
        {
            Body = "jeep_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "jeep_palette.png",
            DefaultPaints = new[] { "#437c44" },   // master: PEI/Canada military = FOREST fixed (src Jeep/Humvee.dat list all 4 factions + random-pick, but master wants forest)
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 12.5f, SpeedMin = -7f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),   // source BoxCollider
            ForwardGears = new[] { 20f, 13.7f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Medium)
            Fuel = 2000f, Health = 600f, Name = "Jeep", Horn = "carhorn_04.ogg",
            SpotPos = new[] { new Vector3(-0.979f, 0.746f, -2.49f), new Vector3(0.979f, 0.746f, -2.49f) }, OmniPos = new Vector3(0f, 0.878f, -2.47f),   // source prefab Headlights (Z negated)
            TailPos = new[] { new Vector3(-0.979f, 0.746f, 2.48f), new Vector3(0.979f, 0.746f, 2.48f) },   // source prefab Taillights (rear, Z negated)
            SteerPivot = new Vector3(-0.464f, 1.018f, -0.922f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // steering wheel centroid + disc normal (PCA)
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("jeep_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // seats: dark grey (real _Color)
                ("jeep_steer.txt", new Color(0.28f, 0.23f, 0.14f)),        // steering wheel: dark brown
                ("jeep_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // headlights: cream
                ("jeep_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // taillights: red
            },
        };

        // Quad.dat: Speed 13.5, steer 32, front-steered, torque 4.8. X +-0.50, front Z -0.39 / rear 1.44, Y 0.20.
        static readonly Spec _quad = new()
        {
            Body = "quad_body.txt", Wheel = "quad_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "quad_palette.png",
            RandomHueGray = true,   // source RandomHueOrGrayscale -> our curated CarColors list
            WheelRadius = 0.45f, Engine = 520f, SteerMax = 32f, SteerMin = 16f, SpeedMax = 13.5f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.0f, 0.777f, 3.581f), BoxCenter = new Vector3(0f, 0.478f, 0.407f),   // source BoxCollider
            ForwardGears = new[] { 20f, 10f }, ReverseGear = 8f, ShiftUpRpm = 3000f,
            Sound = "engine_small.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Small)
            Fuel = 1000f, Health = 450f, Name = "Quad", Horn = "carhorn_01.ogg",
            SteerPivot = new Vector3(0f, 1.00f, -0.32f), SteerAxis = new Vector3(0f, 1f, 0f),   // handlebars: pivot at the prefab Steer node, yaw around vertical
            Wheels = new (float, float, float, bool)[]
            { (-0.50f, 0.20f, -0.39f, true), (0.50f, 0.20f, -0.39f, true), (-0.50f, 0.20f, 1.44f, false), (0.50f, 0.20f, 1.44f, false) },
            Parts = new (string, Color)[]
            {
                ("quad_steer.txt", new Color(0.15f, 0.15f, 0.15f)),        // handlebars: dark metal/rubber (turns with steering)
                ("quad_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("quad_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Bus.dat: Speed 12, steer 24->12, front-steered, torque 2.5. Long 4-wheeler, 10 seats.
        static readonly Spec _bus = new()
        {
            Body = "bus_body.txt", Wheel = "bus_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "bus_palette.png",
            DefaultPaints = new[] { "#d4d4d4" },   // source .dat: single near-white default
            WheelRadius = 0.6f, Engine = 780f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 12f, SpeedMin = -6f, Brake = 24f,
            BoxSize = new Vector3(3.0f, 1.018f, 7.964f), BoxCenter = new Vector3(0f, 0.361f, 0.281f),   // source BoxCollider
            ForwardGears = new[] { 20f, 14.6f }, ReverseGear = 12f, ShiftUpRpm = 4000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Large; bus MaxPitch 1.8)
            Fuel = 2250f, Health = 700f, Name = "Bus", Horn = "carhorn_04.ogg",
            Wheels = new (float, float, float, bool)[]
            { (-1.50f, 0.08f, -1.52f, true), (1.50f, 0.08f, -1.52f, true), (-1.50f, 0.08f, 2.69f, false), (1.50f, 0.08f, 2.69f, false) },
            Parts = new (string, Color)[]
            {
                ("bus_seats.txt", new Color(0.25f, 0.25f, 0.25f)),         // 10 grey seats
                ("bus_steer.txt", new Color(0.28f, 0.23f, 0.14f)),         // steering wheel brown
                ("bus_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),    // cream
                ("bus_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),    // red
            },
        };

        // Sedan.dat: Speed 16.5 (fastest so far), steer 28->14, front-steered, RandomHueOrGrayscale. 4-seat road car, ~6m long.
        static readonly Spec _sedan = new()
        {
            Body = "sedan_body.txt", Wheel = "sedan_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "sedan_palette.png",
            RandomHueGray = true,   // source RandomHueOrGrayscale -> our curated CarColors
            WheelRadius = 0.6f, Engine = 700f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 16.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.656f), BoxCenter = new Vector3(0f, 0.548f, -0.063f),   // source BoxCollider (Z negated)
            ForwardGears = new[] { 14f, 8.75f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1500f, Health = 600f, Name = "Sedan", Horn = "carhorn_02.ogg",
            SpotPos = new[] { new Vector3(-0.765f, 0.708f, -2.969f), new Vector3(0.765f, 0.708f, -2.969f) }, OmniPos = new Vector3(0f, 0.841f, -2.945f),   // prefab Headlights (Z neg)
            TailPos = new[] { new Vector3(-0.979f, 0.688f, 2.841f), new Vector3(0.979f, 0.688f, 2.841f) },   // prefab Taillights (rear, Z neg)
            SteerPivot = new Vector3(-0.464f, 0.894f, -1.416f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // steer centroid + disc normal (PCA)
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.62f, true), (1.30f, 0.25f, -1.62f, true), (-1.30f, 0.25f, 1.38f, false), (1.30f, 0.25f, 1.38f, false) },   // X +-1.30, front Z -1.62, rear 1.38
            Parts = new (string, Color)[]
            {
                ("sedan_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // 4 grey seats
                ("sedan_steer.txt", new Color(0.28f, 0.23f, 0.14f)),        // steering wheel brown
                ("sedan_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("sedan_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Hatchback.dat: Speed 15, steer 24->12, front-steered, RandomHueOrGrayscale. Compact 4-seat car (~5.5m).
        static readonly Spec _hatchback = new()
        {
            Body = "hatchback_body.txt", Wheel = "hatchback_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "hatchback_palette.png",
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 680f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 15f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.261f), BoxCenter = new Vector3(0f, 0.548f, -0.003f),
            ForwardGears = new[] { 14f, 8.75f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1500f, Health = 650f, Name = "Hatchback", Horn = "carhorn_01.ogg",
            SpotPos = new[] { new Vector3(-0.765f, 0.571f, -2.679f), new Vector3(0.765f, 0.571f, -2.679f) }, OmniPos = new Vector3(0f, 0.703f, -2.655f),
            TailPos = new[] { new Vector3(-0.979f, 0.738f, 2.677f), new Vector3(0.979f, 0.738f, 2.677f) },
            SteerPivot = new Vector3(-0.464f, 0.894f, -1.089f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.41f, true), (1.30f, 0.25f, -1.41f, true), (-1.30f, 0.25f, 1.39f, false), (1.30f, 0.25f, 1.39f, false) },
            Parts = new (string, Color)[]
            {
                ("hatchback_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("hatchback_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("hatchback_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("hatchback_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Humvee.dat: Speed 14, steer 24->12, front-steered, faction DefaultPaints (military, like the jeep). Heavy 4x4, brake 40.
        static readonly Spec _humvee = new()
        {
            Body = "humvee_body.txt", Wheel = "humvee_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "humvee_palette.png",
            DefaultPaints = new[] { "#437c44" },   // master: PEI/Canada military = FOREST fixed (src Jeep/Humvee.dat list all 4 factions + random-pick, but master wants forest)
            WheelRadius = 0.6f, Engine = 680f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 14f, SpeedMin = -6f, Brake = 40f,
            BoxSize = new Vector3(2.5f, 1.032f, 5.029f), BoxCenter = new Vector3(0f, 0.605f, -0.018f),
            ForwardGears = new[] { 20f, 12.56f }, ReverseGear = 8f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 2000f, Health = 550f, Name = "Humvee", Horn = "carhorn_03.ogg",
            SpotPos = new[] { new Vector3(-0.979f, 0.741f, -2.511f), new Vector3(0.979f, 0.741f, -2.511f) }, OmniPos = new Vector3(0f, 0.873f, -2.487f),
            TailPos = new[] { new Vector3(-0.979f, 0.738f, 2.548f), new Vector3(0.979f, 0.738f, 2.548f) },
            SteerPivot = new Vector3(-0.464f, 0.94f, -1.27f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("humvee_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("humvee_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("humvee_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("humvee_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Roadster.dat: Speed 19 (fastest!), steer 28->14, RandomHueOrGrayscale, its OWN horn. Fragile 2-seat sports car (Health 500).
        static readonly Spec _roadster = new()
        {
            Body = "roadster_body.txt", Wheel = "roadster_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "roadster_palette.png",
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 760f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 19f, SpeedMin = -5f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.656f), BoxCenter = new Vector3(0f, 0.548f, -0.063f),
            ForwardGears = new[] { 14f, 8f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1250f, Health = 500f, Name = "Roadster", Horn = "roadster_horn.ogg",
            SpotPos = new[] { new Vector3(-0.765f, 0.708f, -2.969f), new Vector3(0.765f, 0.708f, -2.969f) }, OmniPos = new Vector3(0f, 0.841f, -2.945f),
            TailPos = new[] { new Vector3(-0.979f, 0.688f, 2.841f), new Vector3(0.979f, 0.688f, 2.841f) },
            SteerPivot = new Vector3(-0.464f, 0.894f, -0.46f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.225f, -1.62f, true), (1.30f, 0.225f, -1.62f, true), (-1.30f, 0.225f, 1.38f, false), (1.30f, 0.225f, 1.38f, false) },
            Parts = new (string, Color)[]
            {
                ("roadster_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // 2 grey seats
                ("roadster_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("roadster_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("roadster_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        public static Vehicle BuildJeep(int variant = 0) => Build(_jeep, variant);
        public static Vehicle BuildQuad(int variant = 0) => Build(_quad, variant);
        public static Vehicle BuildBus(int variant = 0) => Build(_bus, variant);
        public static Vehicle BuildSedan(int variant = 0) => Build(_sedan, variant);
        public static Vehicle BuildHatchback(int variant = 0) => Build(_hatchback, variant);
        public static Vehicle BuildHumvee(int variant = 0) => Build(_humvee, variant);
        public static Vehicle BuildRoadster(int variant = 0) => Build(_roadster, variant);
        public static Vehicle BuildByName(string name, int variant = 0) => name switch { "quad" => BuildQuad(variant), "bus" => BuildBus(variant), "sedan" => BuildSedan(variant), "hatchback" => BuildHatchback(variant), "humvee" => BuildHumvee(variant), "roadster" => BuildRoadster(variant), _ => BuildJeep(variant) };

        static Vehicle Build(Spec s, int variant)
        {
            var v = new Vehicle { Mass = GlobalMass };   // source uses one constant mass (2.0) for ALL vehicles -> one global Godot mass
            v.CollisionLayer |= 1u << 5;   // bit 5 = "vehicle" so player bullets can raycast-hit it (see PlayerController.StepBullets)
            v.AddToGroup("vehicles");      // so NearestVehicle + explosion damage (grenades) find every vehicle, not just harness-grouped ones
            v._engineForce = s.Engine; v._steerMax = s.SteerMax; v._steerMin = s.SteerMin;
            v._speedMax = s.SpeedMax; v._speedMin = s.SpeedMin; v._brakeForce = s.Brake;
            v._steerTurnSpeed = s.SteerMax * 2f;   // master: ramp to full lock a LOT longer than source (source default = SteerMax*5 deg/s) -> slower turn-in
            v._gears = s.ForwardGears; v._reverseGear = s.ReverseGear; v._shiftUpRpm = s.ShiftUpRpm;
            v._idlePitch = s.IdlePitch; v._maxPitch = s.MaxPitch; v._idleVol = s.IdleVolume; v._maxVol = s.MaxVolume;
            v.FuelMax = v.Fuel = s.Fuel; v.HealthMax = v.Health = s.Health; v.Battery = BatteryMax; v.DisplayName = s.Name;

            var bodyMesh = ContentProvider.ParseObj($"res://content/{s.Body}");
            var paint = SpawnPaint(s, variant);   // the source spawn paint by variant: default-list / curated car colour / white
            Material bodyMat = s.Palette != null
                ? PaintMat(s.Palette, paint)
                : new StandardMaterial3D { AlbedoColor = paint, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            v._bodyMesh = new MeshInstance3D { Name = "Body", Mesh = bodyMesh, MaterialOverride = bodyMat };
            v.AddChild(v._bodyMesh);

            // source BoxCollider hull (Godot space), not the mesh AABB (which wrongly included the roll bar)
            v.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = s.BoxSize }, Position = s.BoxCenter });
            v.BodyExtents = s.BoxSize * 0.5f; v.BodyCenter = s.BoxCenter;   // for the zombie swipe-reach

            // front bumper trigger (source Bumper): a forward volume that roadkills characters (enemy layer bit 1) the
            // vehicle drives into. Trigger only -- the body's own mask ignores the enemy layer, so it plows through.
            var bumper = new Area3D { CollisionLayer = 0, CollisionMask = 1u << 1 };
            float frontZ = s.BoxCenter.Z - s.BoxSize.Z * 0.5f;   // front face (forward = -Z)
            bumper.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(s.BoxSize.X, s.BoxSize.Y, 0.8f) }, Position = new Vector3(s.BoxCenter.X, s.BoxCenter.Y, frontZ - 0.2f) });
            v.AddChild(bumper);
            bumper.BodyEntered += v.OnBumperHit;

            var wheelMesh = ContentProvider.ParseObj($"res://content/{s.Wheel}");
            Material wheelMat;
            if (s.WheelTex != null)   // real wheel albedo (tyre + rim), nearest-sampled like the game
            {
                var wimg = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{s.WheelTex}"));
                wheelMat = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(wimg), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            }
            else
                wheelMat = new StandardMaterial3D { AlbedoColor = new Color(0.09f, 0.09f, 0.10f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            int nw = s.Wheels.Length;
            v._wheelMeshRef = wheelMesh; v._wheelMatRef = wheelMat; v._wheelR = s.WheelRadius;   // for explosion debris
            v._wNodes = new VehicleWheel3D[nw]; v._wMeshes = new MeshInstance3D[nw]; v._wRoll = new float[nw]; v._wSign = new float[nw];
            for (int i = 0; i < nw; i++)
            {
                var (x, y, z, steer) = s.Wheels[i];
                var w = new VehicleWheel3D
                {
                    Position = new Vector3(x, y, z), UseAsSteering = steer, UseAsTraction = true,
                    WheelRadius = s.WheelRadius, WheelRestLength = 0.25f, SuspensionTravel = 0.25f,
                    // stiffer + higher max force so 900kg doesn't compress the suspension into a permanent SQUAT; more
                    // damping to settle without bounce; higher friction slip = more TRACTION (was sliding/understeering).
                    SuspensionStiffness = 55f, SuspensionMaxForce = 12000f, DampingCompression = 3.5f, DampingRelaxation = 4.2f, WheelFrictionSlip = 6.0f,
                };
                // left wheels: flip the mesh so the tread faces outward
                var mi = new MeshInstance3D { Mesh = wheelMesh, MaterialOverride = wheelMat, Scale = new Vector3(x < 0 ? -1f : 1f, 1f, 1f) };
                w.AddChild(mi);
                v.AddChild(w);
                v._wNodes[i] = w; v._wMeshes[i] = mi; v._wSign[i] = x < 0 ? -1f : 1f;
            }

            // Drop the centre of mass to just below the axle line so the car stops rolling on turns and pitching onto its
            // nose under braking (master). Godot's auto COM sat at the body-box centre (~0.6m up) -> top-heavy + tippy.
            float comY = 0f; foreach (var wl in s.Wheels) comY += wl.y; comY = comY / s.Wheels.Length - 0.2f;
            v.CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom;
            v.CenterOfMass = new Vector3(0f, comY, 0f);

            if (s.Parts != null)   // detail meshes with their real solid colours (seats grey, lights, steering brown)
                foreach (var (txt, color) in s.Parts)
                {
                    var pm = SolidMat(color);
                    var mi = new MeshInstance3D { Mesh = ContentProvider.ParseObj($"res://content/{txt}"), MaterialOverride = pm };
                    if (txt.Contains("steer") && s.SteerAxis != Vector3.Zero)   // wrap the steering wheel in a pivot at its centre so it can turn
                    {
                        v._steerPivot = new Node3D { Position = s.SteerPivot };
                        mi.Position = -s.SteerPivot;   // baked world verts render in place once the pivot sits at the centre
                        v._steerPivot.AddChild(mi);
                        v.AddChild(v._steerPivot);
                        v._steerAxis = s.SteerAxis.Normalized();
                    }
                    else v.AddChild(mi);
                    if (txt.Contains("headlight")) v._headlightMat = pm;   // capture so the lamp glows when the headlights are on
                    if (txt.Contains("taillight")) v._taillightMat = pm;   // capture so the taillight glows red while driving
                }

            if (s.SpotPos != null)   // headlights: source "Headlights" node -- 2 warm spot beams + 1 omni fill at the front, off until 'L'
            {
                var warm = new Color(0.97f, 0.96f, 0.83f);
                v._headlights = new Node3D { Visible = false };
                foreach (var p in s.SpotPos)
                    v._headlights.AddChild(new SpotLight3D { Position = p, SpotRange = 45f, SpotAngle = 25f, SpotAngleAttenuation = 1.3f, LightColor = warm, LightEnergy = 9f });
                v._headlights.AddChild(new OmniLight3D { Position = s.OmniPos + Vector3.Up * 0.5f, OmniRange = 28f, LightColor = warm, LightEnergy = 0.8f });   // dim soft fill (raised above the seats so it doesn't glare)
                v.AddChild(v._headlights);
            }

            if (s.TailPos != null)   // running taillights: dim red spots at the rear (aim +Z, backward), on while driving
            {
                var red = new Color(0.996f, 0f, 0f);
                v._taillights = new Node3D { Visible = false };
                foreach (var p in s.TailPos)
                    v._taillights.AddChild(new SpotLight3D { Position = p, RotationDegrees = new Vector3(0f, 180f, 0f), SpotRange = 6f, SpotAngle = 35f, LightColor = red, LightEnergy = 3f });
                v.AddChild(v._taillights);
            }

            if (s.Horn != null)   // horn: one-shot the .dat HornAudioClip (a shared CarHorn) on LMB
            {
                var hogg = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{s.Horn}"));
                v._hornAudio = new AudioStreamPlayer3D { Stream = hogg, UnitSize = 12f, MaxDistance = 90f, VolumeDb = 4f };
                v.AddChild(v._hornAudio);
            }

            if (s.Sound != null)   // EngineRPMSimple: a looping engine clip (the prefab AudioSource) whose pitch + volume ride the RPM
            {
                var ogg = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{s.Sound}"));
                ogg.Loop = true;
                v._engineAudio = new AudioStreamPlayer3D { Stream = ogg, UnitSize = 10f, MaxDistance = 80f, PitchScale = s.IdlePitch, VolumeDb = Mathf.LinearToDb(s.IdleVolume), Autoplay = true };
                v.AddChild(v._engineAudio);   // Autoplay starts the loop when the vehicle enters the scene tree
            }

            // damage smoke + explosion fire from the engine bay (source: smoke_0/1 at health thresholds, fire + Fire light on explode)
            var firePos = new Vector3(0f, 1.24f, -1.70f);   // source Fire node (0,1.238,1.703), Z negated
            v._firePos = firePos;   // remembered so the explosion plume can emit from the engine bay in world-space
            v._smoke = MakeSmoke(new Color(0.13f, 0.13f, 0.13f), 2.4f, 2.6f, 26, false);
            v._fire = MakeSmoke(new Color(1f, 0.55f, 0.12f), 0.7f, 4.5f, 30, true);
            v._smoke.Position = firePos; v._fire.Position = firePos;
            v.AddChild(v._smoke); v.AddChild(v._fire);
            v._fireLight = new OmniLight3D { Position = firePos, OmniRange = 8f, LightColor = new Color(1f, 0.55f, 0.2f), LightEnergy = 0f, Visible = false };
            v.AddChild(v._fireLight);
            v._explosionAudio = new AudioStreamPlayer3D { Stream = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath("res://content/explosion.ogg")), UnitSize = 20f, MaxDistance = 200f, VolumeDb = 6f };   // boom on explode
            v.AddChild(v._explosionAudio);
            v.Brake = s.Brake * HandbrakeScale; v._parked = true;   // spawns parked: brake on + freezes once settled so it holds ride height without jitter (released once driven)
            return v;
        }

        // throttle/brake/steer in [-1,1]; applies the source .dat handling: hard Speed_Max/Min caps + speed-dependent
        // steering (Steer_Max at rest -> Steer_Min at full speed), so the observable handling matches the game.
        public void Drive(float throttle, float steer, bool handbrake)
        {
            if (_exploded) { EngineForce = 0f; Steering = 0f; Brake = 0f; return; }   // a wrecked vehicle can't be driven
            _parked = false;
            float speed = LinearVelocity.Length();   // m/s (horizontal-ish while driving)
            float fwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);   // signed forward speed (front = -Z)
            // S while rolling FORWARD (or W while rolling backward) = a foot BRAKE, not an instant reverse -- real pedal feel
            bool footBrake = (throttle < 0f && fwd > 0.6f) || (throttle > 0f && fwd < -0.6f);
            float eng = footBrake ? 0f : throttle * _engineForce;
            if (throttle > 0f && speed >= _speedMax) eng = 0f;    // cap forward at Speed_Max (12.5)
            if (throttle < 0f && speed >= -_speedMin) eng = 0f;   // cap reverse at -Speed_Min (7)
            EngineForce = -eng;   // NEGATE: Godot drives this rig +Z for positive force, so W(throttle+1) was going backward
            float t = Mathf.Clamp(speed / _speedMax, 0f, 1f);
            // target steer angle (deg); NEGATE because Godot VehicleBody3D steers LEFT for positive (D(+1)=right). 28deg at rest -> 14 at full speed.
            _steerTarget = -steer * Mathf.Lerp(_steerMax, _steerMin, t);   // smoothed toward in _PhysicsProcess (not snapped) via the AnimatedSteeringAngle-style ramp -- master confirmed the raw angle is fine
            // SPACE = handbrake (locks hard); S-into-forward-motion = foot brake. Both far stronger than the old raw .dat Brake.
            _handbraking = handbrake;   // remembered so the car freezes (no jitter) when stopped with the handbrake held
            Brake = handbrake ? _brakeForce * HandbrakeScale : (footBrake ? _brakeForce * FootBrakeScale : 0f);
        }

        public void Park()   // driver left: smoothly damp to a stop + straighten (no hard-brake judder), then hold
        {
            _parked = true;
            EngineForce = 0f;
            _steerTarget = 0f;
            AngularVelocity = Vector3.Zero;
        }

        public float ForwardSpeedPct()   // source GetReplicatedForwardSpeedPercentageOfTargetSpeed: forward speed / top speed (0..1) for the DRIVING stealth radius
        {
            if (_speedMax <= 0f) return 0f;
            float fwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);   // signed: reversing clamps to 0 (quiet)
            return Mathf.Clamp(fwd / _speedMax, 0f, 1f);
        }

        public void Honk()   // source tellHorn: one-shot the horn; 0.5s cooldown (canUseHorn) + needs battery charge
        {
            if (_hornCd > 0f || Battery <= 0f || _hornAudio == null) return;
            _hornAudio.Play();
            _hornCd = 0.5f;
            GetTree().CallGroup("zombies", "OnGunshot", GlobalPosition, HornAlertRadius);   // source tellHorn AlertTool.alert(pos,32): the noise pulls nearby zombies to investigate (same broadcast the gunshot alert uses)
        }

        public void ToggleHeadlights() => SetHeadlights(!_headlightsOn);   // source tellHeadlights
        void SetHeadlights(bool on)
        {
            _headlightsOn = on && Battery > 0f;   // a dead battery can't power the lights
            if (_headlights != null) _headlights.Visible = _headlightsOn;
            if (_headlightMat != null)   // source: lamp emission = colour*2 when lit, off otherwise
            {
                _headlightMat.EmissionEnabled = _headlightsOn;
                if (_headlightsOn) { _headlightMat.Emission = new Color(0.97f, 0.96f, 0.83f); _headlightMat.EmissionEnergyMultiplier = 2f; }
            }
        }

        void SetTaillights(bool on)   // running taillights: red glow while driven (source: emission = colour*2)
        {
            _taillightsOn = on;
            if (_taillights != null) _taillights.Visible = on;
            if (_taillightMat != null)
            {
                _taillightMat.EmissionEnabled = on;
                if (on) { _taillightMat.Emission = new Color(0.56f, 0.13f, 0.13f); _taillightMat.EmissionEnergyMultiplier = 2f; }
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_wNodes == null) return;
            if (_spawnGrace > 0f) _spawnGrace -= (float)delta;   // spawn/world-init: stay DYNAMIC ~2.5s so a fresh car drops to fit terrain first
            // Middle ground (source: a parked / not-locally-driven vehicle goes isKinematic -- InteractableVehicle 1495/1517):
            // once a car has settled it FREEZES kinematic so it physically cannot jitter, and it's dynamic while driven. My
            // axis-lock hit a catch-22 -- the brake's own oscillation kept speed above the lock threshold, so it never locked.
            float hSpeed2 = new Vector3(LinearVelocity.X, 0f, LinearVelocity.Z).LengthSquared();
            bool wantHold = _parked ? (_spawnGrace <= 0f && hSpeed2 < 1.0f)   // parked: freeze once the settle grace is done + it's rolled to a stop
                                    : (_handbraking && hSpeed2 < 0.04f);      // handbraking WHILE driving: freeze only at ~zero (0.2 m/s), strong brake above
            if (wantHold && !Freeze) { LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero; FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic; Freeze = true; }
            else if (!wantHold && Freeze) Freeze = false;
            if (_parked && !Freeze) Brake = _brakeForce * HandbrakeScale;   // brake a rolling parked car down until it freezes
            for (int i = 0; i < _wNodes.Length; i++)   // visually spin each wheel mesh by its RPM (steer + suspension are on the node)
            {
                _wRoll[i] += _wNodes[i].GetRpm() * _wSign[i] * (Mathf.Tau / 60f) * (float)delta;
                _wMeshes[i].Rotation = new Vector3(_wRoll[i], 0f, 0f);
            }
            // engine RPM + gears (source InteractableVehicle): rpm = |avg wheel rpm| * gear ratio, idle-floored, then auto-shift
            float sum = 0f; foreach (var w in _wNodes) sum += Mathf.Abs(w.GetRpm());
            float avgWheelRpm = _wNodes.Length > 0 ? sum / _wNodes.Length : 0f;
            float ratio = (_gears != null && _gear >= 1 && _gear <= _gears.Length) ? _gears[_gear - 1] : 20f;
            float target = Mathf.Clamp(avgWheelRpm * ratio, IdleRpm, MaxRpm);
            _engineRpm = Mathf.Lerp(_engineRpm, target, Mathf.Min(1f, 8f * (float)delta));
            if (_gears != null && _gears.Length > 0)   // auto gearbox: up past the shift-up RPM, down well below it
            {
                if (_engineRpm > _shiftUpRpm && _gear < _gears.Length) _gear++;
                else if (_engineRpm < _shiftUpRpm * 0.45f && _gear > 1) _gear--;
            }
            if (_engineAudio != null)   // EngineRPMSimple: pitch + volume by RPM while running; silent when off (exited)
            {
                if (EngineOn)
                {
                    float n = EngineRpmNorm;
                    _engineAudio.PitchScale = Mathf.Lerp(_idlePitch, _maxPitch, n);
                    _engineAudio.VolumeDb = Mathf.LinearToDb(Mathf.Lerp(_idleVol, _maxVol, n));
                }
                else _engineAudio.VolumeDb = -80f;   // engine off -> kill the noise
            }
            if (EngineOn && Fuel > 0f)   // source simulateBurnFuel: burn fuelBurnRate/sec while the engine runs
                Fuel = Mathf.Max(0f, Fuel - FuelBurnRate * (float)delta);
            if (_headlightsOn)   // source: headlights burn the battery (EBatteryMode.Burn); die when it's empty
            {
                Battery = Mathf.Max(0f, Battery - BatteryBurnRate * (float)delta);
                if (Battery <= 0f) SetHeadlights(false);
            }
            bool tailWant = EngineOn && Battery > 0f;   // source synchronizeTaillights: taillights on while isDriven && canTurnOnLights
            if (tailWant != _taillightsOn) SetTaillights(tailWant);
            if (_hornCd > 0f) _hornCd -= (float)delta;
            // collision/ram damage (source isVulnerableToBumper): a sudden horizontal deceleration = a crash. Horizontal only, so the spawn drop doesn't count.
            float curSpeed = new Vector2(LinearVelocity.X, LinearVelocity.Z).Length();
            float decel = _prevSpeed - curSpeed;
            if (!_parked && !_exploded && _prevSpeed > 5f && decel > 200f * (float)delta) TakeDamage(decel * 20f);   // >200 m/s^2 = a crash (braking is ~8); full-speed hit ~250 dmg
            _prevSpeed = curSpeed;
            if (_smoke != null) _smoke.Emitting = _exploded || Health < SmokeHealth;   // source: smoke while damaged (< SMOKE_1 threshold 200)
            if (_exploded)   // master: explosion smoke/fire emits from the ENGINE bay (like the hurt smoke) but rises STRAIGHT UP -- world-space so the plume doesn't tilt with the tumbling wreck
            {
                var enginePos = ToGlobal(_firePos);   // engine-bay world position (rides the wreck); plume forced world-up via Rotation=0
                if (_smoke != null) { _smoke.TopLevel = true; _smoke.GlobalPosition = enginePos; _smoke.Rotation = Vector3.Zero; }
                if (_fire  != null) { _fire.TopLevel  = true; _fire.GlobalPosition  = enginePos; _fire.Rotation  = Vector3.Zero; }
            }
            if (_deadTimer > 0f) { _deadTimer -= (float)delta; if (_deadTimer <= 0f) Explode(); }   // source EXPLODE: 4s after health 0

            // steering smoothing (source: AnimatedSteeringAngle = MoveTowards(target, SteeringAngleTurnSpeed*dt)) -- no instant snap
            _steerAngle = Mathf.MoveToward(_steerAngle, _steerTarget, _steerTurnSpeed * (float)delta);
            Steering = Mathf.DegToRad(_steerAngle);
            if (_steerPivot != null) _steerPivot.Basis = new Basis(_steerAxis, Mathf.DegToRad(_steerAngle));   // steering wheel model turns 1:1 with the steer angle (source line 4020, AnimatedSteeringAngle)
        }
    }
}
