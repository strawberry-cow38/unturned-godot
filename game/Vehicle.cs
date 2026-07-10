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
        VehicleWheel3D[] _wNodes; MeshInstance3D[] _wMeshes; float[] _wRoll, _wSign;   // wheels for visual spin
        public static float GlobalMass = 900f;   // all vehicles share one mass (the source does: Rigidbody mass = 2.0 for every vehicle)
        float[] _gears; float _reverseGear, _shiftUpRpm; float _engineRpm = 1000f; int _gear = 1;   // engine RPM + gear sim
        AudioStreamPlayer3D _engineAudio; float _idlePitch = 1f, _maxPitch = 2f, _idleVol = 0.75f, _maxVol = 1f;   // EngineRPMSimple sound
        const float IdleRpm = 1000f, MaxRpm = 6000f;   // source EngineIdleRPM / EngineMaxRPM
        public float EngineRpm => _engineRpm;
        public float EngineRpmNorm => Mathf.Clamp((_engineRpm - IdleRpm) / (MaxRpm - IdleRpm), 0f, 1f);
        public int Gear => _gear;
        // vehicle status for the HUD (source InteractableVehicle): fuel drains while the engine's on; health = damage; battery = accessories
        public float Fuel, FuelMax, Health, HealthMax, Battery;
        public bool EngineOn; public string DisplayName;
        const float FuelBurnRate = 2.05f, BatteryMax = 10000f;   // EEngine.CAR default fuelBurnRate/sec; battery full = 10000
        public float FuelNorm => FuelMax > 0f ? Fuel / FuelMax : 0f;
        public float HealthNorm => HealthMax > 0f ? Health / HealthMax : 0f;
        public float BatteryNorm => Battery / BatteryMax;

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
            public (float x, float y, float z, bool steer)[] Wheels;
            public (string txt, Color color)[] Parts;   // detail meshes (root-relative) with their real solid colours
        }

        static StandardMaterial3D SolidMat(Color c) =>
            new() { AlbedoColor = c, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        // Unturned paintable shading: body samples the palette, paintable texels tinted by _PaintColor.
        static ShaderMaterial PaintMat(string palette, Color paint)
        {
            var sh = new Shader { Code = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/vehicle_paint.gdshader")) };
            var m = new ShaderMaterial { Shader = sh };
            var img = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{palette}"));
            m.SetShaderParameter("palette", ImageTexture.CreateFromImage(img));
            m.SetShaderParameter("paint_color", new Vector3(paint.R, paint.G, paint.B));
            return m;
        }

        // Curated natural car colours for RandomHueOrGrayscale vehicles -- the source's random-hue goes neon, so master
        // wants a hand-picked natural set (white/black/silver/gunmetal/dark-red/navy/forest/tan/olive).
        static readonly string[] CarColors = { "#ececec", "#242424", "#c2c2c2", "#4a4d50", "#7a1f1f", "#24365e", "#2e4a2e", "#a69884", "#6b6f52" };

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
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // source .dat: Coalition / Desert / Forest / Russia
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 12.5f, SpeedMin = -7f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),   // source BoxCollider
            ForwardGears = new[] { 20f, 13.7f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Medium)
            Fuel = 2000f, Health = 600f, Name = "Jeep",
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
            Fuel = 1000f, Health = 450f, Name = "Quad",
            Wheels = new (float, float, float, bool)[]
            { (-0.50f, 0.20f, -0.39f, true), (0.50f, 0.20f, -0.39f, true), (-0.50f, 0.20f, 1.44f, false), (0.50f, 0.20f, 1.44f, false) },
            Parts = new (string, Color)[]
            {
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
            Fuel = 2250f, Health = 700f, Name = "Bus",
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

        public static Vehicle BuildJeep(int variant = 0) => Build(_jeep, variant);
        public static Vehicle BuildQuad(int variant = 0) => Build(_quad, variant);
        public static Vehicle BuildBus(int variant = 0) => Build(_bus, variant);
        public static Vehicle BuildByName(string name, int variant = 0) => name switch { "quad" => BuildQuad(variant), "bus" => BuildBus(variant), _ => BuildJeep(variant) };

        static Vehicle Build(Spec s, int variant)
        {
            var v = new Vehicle { Mass = GlobalMass };   // source uses one constant mass (2.0) for ALL vehicles -> one global Godot mass
            v._engineForce = s.Engine; v._steerMax = s.SteerMax; v._steerMin = s.SteerMin;
            v._speedMax = s.SpeedMax; v._speedMin = s.SpeedMin; v._brakeForce = s.Brake;
            v._gears = s.ForwardGears; v._reverseGear = s.ReverseGear; v._shiftUpRpm = s.ShiftUpRpm;
            v._idlePitch = s.IdlePitch; v._maxPitch = s.MaxPitch; v._idleVol = s.IdleVolume; v._maxVol = s.MaxVolume;
            v.FuelMax = v.Fuel = s.Fuel; v.HealthMax = v.Health = s.Health; v.Battery = BatteryMax; v.DisplayName = s.Name;

            var bodyMesh = ContentProvider.ParseObj($"res://content/{s.Body}");
            var paint = SpawnPaint(s, variant);   // the source spawn paint by variant: default-list / curated car colour / white
            Material bodyMat = s.Palette != null
                ? PaintMat(s.Palette, paint)
                : new StandardMaterial3D { AlbedoColor = paint, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            v.AddChild(new MeshInstance3D { Name = "Body", Mesh = bodyMesh, MaterialOverride = bodyMat });

            // source BoxCollider hull (Godot space), not the mesh AABB (which wrongly included the roll bar)
            v.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = s.BoxSize }, Position = s.BoxCenter });

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
            v._wNodes = new VehicleWheel3D[nw]; v._wMeshes = new MeshInstance3D[nw]; v._wRoll = new float[nw]; v._wSign = new float[nw];
            for (int i = 0; i < nw; i++)
            {
                var (x, y, z, steer) = s.Wheels[i];
                var w = new VehicleWheel3D
                {
                    Position = new Vector3(x, y, z), UseAsSteering = steer, UseAsTraction = true,
                    WheelRadius = s.WheelRadius, WheelRestLength = 0.25f, SuspensionTravel = 0.25f,
                    SuspensionStiffness = 30f, DampingCompression = 2.4f, DampingRelaxation = 3.0f, WheelFrictionSlip = 3.5f,
                };
                // left wheels: flip the mesh so the tread faces outward
                var mi = new MeshInstance3D { Mesh = wheelMesh, MaterialOverride = wheelMat, Scale = new Vector3(x < 0 ? -1f : 1f, 1f, 1f) };
                w.AddChild(mi);
                v.AddChild(w);
                v._wNodes[i] = w; v._wMeshes[i] = mi; v._wSign[i] = x < 0 ? -1f : 1f;
            }

            if (s.Parts != null)   // detail meshes with their real solid colours (seats grey, lights, steering brown)
                foreach (var (txt, color) in s.Parts)
                    v.AddChild(new MeshInstance3D { Mesh = ContentProvider.ParseObj($"res://content/{txt}"), MaterialOverride = SolidMat(color) });

            if (s.Sound != null)   // EngineRPMSimple: a looping engine clip (the prefab AudioSource) whose pitch + volume ride the RPM
            {
                var ogg = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{s.Sound}"));
                ogg.Loop = true;
                v._engineAudio = new AudioStreamPlayer3D { Stream = ogg, UnitSize = 10f, MaxDistance = 80f, PitchScale = s.IdlePitch, VolumeDb = Mathf.LinearToDb(s.IdleVolume), Autoplay = true };
                v.AddChild(v._engineAudio);   // Autoplay starts the loop when the vehicle enters the scene tree
            }
            return v;
        }

        // throttle/brake/steer in [-1,1]; applies the source .dat handling: hard Speed_Max/Min caps + speed-dependent
        // steering (Steer_Max at rest -> Steer_Min at full speed), so the observable handling matches the game.
        public void Drive(float throttle, float steer, bool braking)
        {
            float speed = LinearVelocity.Length();   // m/s (horizontal-ish while driving)
            float eng = throttle * _engineForce;
            if (throttle > 0f && speed >= _speedMax) eng = 0f;    // cap forward at Speed_Max (12.5)
            if (throttle < 0f && speed >= -_speedMin) eng = 0f;   // cap reverse at -Speed_Min (7)
            EngineForce = eng;
            float t = Mathf.Clamp(speed / _speedMax, 0f, 1f);
            Steering = Mathf.DegToRad(-steer * Mathf.Lerp(_steerMax, _steerMin, t));   // NEGATE: Godot VehicleBody3D steers LEFT for positive, so D(+1)=right needs -ve. 28deg at rest -> 14deg at full speed
            Brake = braking ? _brakeForce : 0f;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_wNodes == null) return;
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
            if (_engineAudio != null)   // EngineRPMSimple: pitch + volume lerp idle->max across the RPM band
            {
                float n = EngineRpmNorm;
                _engineAudio.PitchScale = Mathf.Lerp(_idlePitch, _maxPitch, n);
                _engineAudio.VolumeDb = Mathf.LinearToDb(Mathf.Lerp(_idleVol, _maxVol, n));
            }
            if (EngineOn && Fuel > 0f)   // source simulateBurnFuel: burn fuelBurnRate/sec while the engine runs
                Fuel = Mathf.Max(0f, Fuel - FuelBurnRate * (float)delta);
        }
    }
}
