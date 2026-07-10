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

        struct Spec
        {
            public string Body, Wheel, WheelTex, Palette;   // Palette = paintable palette; WheelTex = wheel albedo
            public Color Paint;
            public float WheelRadius, Mass, Engine, SteerMax, SteerMin, SpeedMax, SpeedMin, Brake;
            public Vector3 BoxSize, BoxCenter;   // source BoxCollider (Godot space: center Z negated)
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

        // Jeep.dat: Speed 12.5, steer 28, front-steered, torque 2.8. Godot space (front = -Z): X +-1.30, front Z -1.40.
        static readonly Spec _jeep = new()
        {
            Body = "jeep_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "jeep_palette.png", Paint = new Color(0.854f, 0.858f, 0.078f),
            WheelRadius = 0.6f, Mass = 900f, Engine = 600f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 12.5f, SpeedMin = -7f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),   // source BoxCollider
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
            Body = "quad_body.txt", Wheel = "quad_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "quad_palette.png", Paint = new Color(0.525f, 0.755f, 0.353f),
            WheelRadius = 0.45f, Mass = 500f, Engine = 520f, SteerMax = 32f, SteerMin = 16f, SpeedMax = 13.5f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.0f, 0.777f, 3.581f), BoxCenter = new Vector3(0f, 0.478f, 0.407f),   // source BoxCollider
            Wheels = new (float, float, float, bool)[]
            { (-0.50f, 0.20f, -0.39f, true), (0.50f, 0.20f, -0.39f, true), (-0.50f, 0.20f, 1.44f, false), (0.50f, 0.20f, 1.44f, false) },
            Parts = new (string, Color)[]
            {
                ("quad_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("quad_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        public static Vehicle BuildJeep() => Build(_jeep);
        public static Vehicle BuildQuad() => Build(_quad);
        public static Vehicle BuildByName(string name) => name == "quad" ? BuildQuad() : BuildJeep();

        static Vehicle Build(Spec s)
        {
            var v = new Vehicle { Mass = s.Mass };
            v._engineForce = s.Engine; v._steerMax = s.SteerMax; v._steerMin = s.SteerMin;
            v._speedMax = s.SpeedMax; v._speedMin = s.SpeedMin; v._brakeForce = s.Brake;

            var bodyMesh = ContentProvider.ParseObj($"res://content/{s.Body}");
            // paintable palette shader (panels take the paint colour, exhaust/lights keep theirs); fallback = solid
            Material bodyMat = s.Palette != null
                ? PaintMat(s.Palette, s.Paint)
                : new StandardMaterial3D { AlbedoColor = s.Paint, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
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
            Steering = Mathf.DegToRad(steer * Mathf.Lerp(_steerMax, _steerMin, t));   // 28deg at rest -> 14deg at full speed
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
        }
    }
}
