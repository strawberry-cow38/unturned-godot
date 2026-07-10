using Godot;

namespace UnturnedGodot
{
    // Drivable vehicle. Source: InteractableVehicle + VehicleAsset (WheelCollider rig -> Godot VehicleBody3D +
    // VehicleWheel3D 1:1). Meshes ripped by tools/extract_vehicle_mesh.py; params + real _PaintColor from the .dat.
    public partial class Vehicle : VehicleBody3D
    {
        float _engineForce = 600f;
        float _maxSteerDeg = 28f;
        float _brakeForce = 12f;

        struct Spec
        {
            public string Body, Wheel, WheelTex, Palette;   // Palette = paintable palette; WheelTex = wheel albedo
            public Color Paint;
            public float WheelRadius, Mass, Engine, SteerDeg, Brake;
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
            WheelRadius = 0.6f, Mass = 900f, Engine = 600f, SteerDeg = 28f, Brake = 12f,
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
            Body = "quad_body.txt", Wheel = "quad_wheel.txt", Paint = new Color(0.525f, 0.755f, 0.353f),
            WheelRadius = 0.45f, Mass = 500f, Engine = 520f, SteerDeg = 32f, Brake = 10f,
            Wheels = new (float, float, float, bool)[]
            { (-0.50f, 0.20f, -0.39f, true), (0.50f, 0.20f, -0.39f, true), (-0.50f, 0.20f, 1.44f, false), (0.50f, 0.20f, 1.44f, false) },
        };

        public static Vehicle BuildJeep() => Build(_jeep);
        public static Vehicle BuildQuad() => Build(_quad);
        public static Vehicle BuildByName(string name) => name == "quad" ? BuildQuad() : BuildJeep();

        static Vehicle Build(Spec s)
        {
            var v = new Vehicle { Mass = s.Mass };
            v._engineForce = s.Engine; v._maxSteerDeg = s.SteerDeg; v._brakeForce = s.Brake;

            var bodyMesh = ContentProvider.ParseObj($"res://content/{s.Body}");
            // paintable palette shader (panels take the paint colour, exhaust/lights keep theirs); fallback = solid
            Material bodyMat = s.Palette != null
                ? PaintMat(s.Palette, s.Paint)
                : new StandardMaterial3D { AlbedoColor = s.Paint, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            v.AddChild(new MeshInstance3D { Name = "Body", Mesh = bodyMesh, MaterialOverride = bodyMat });

            var aabb = bodyMesh.GetAabb();   // chassis collision hull from the body extent
            v.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = aabb.Size }, Position = aabb.GetCenter() });

            var wheelMesh = ContentProvider.ParseObj($"res://content/{s.Wheel}");
            Material wheelMat;
            if (s.WheelTex != null)   // real wheel albedo (tyre + rim), nearest-sampled like the game
            {
                var wimg = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{s.WheelTex}"));
                wheelMat = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(wimg), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            }
            else
                wheelMat = new StandardMaterial3D { AlbedoColor = new Color(0.09f, 0.09f, 0.10f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            foreach (var (x, y, z, steer) in s.Wheels)
            {
                var w = new VehicleWheel3D
                {
                    Position = new Vector3(x, y, z), UseAsSteering = steer, UseAsTraction = true,
                    WheelRadius = s.WheelRadius, WheelRestLength = 0.25f, SuspensionTravel = 0.25f,
                    SuspensionStiffness = 30f, DampingCompression = 2.4f, DampingRelaxation = 3.0f, WheelFrictionSlip = 3.5f,
                };
                // left wheels: flip the mesh so the tread faces outward
                w.AddChild(new MeshInstance3D { Mesh = wheelMesh, MaterialOverride = wheelMat, Scale = new Vector3(x < 0 ? -1f : 1f, 1f, 1f) });
                v.AddChild(w);
            }

            if (s.Parts != null)   // detail meshes with their real solid colours (seats grey, lights, steering brown)
                foreach (var (txt, color) in s.Parts)
                    v.AddChild(new MeshInstance3D { Mesh = ContentProvider.ParseObj($"res://content/{txt}"), MaterialOverride = SolidMat(color) });
            return v;
        }

        // throttle/brake/steer in [-1,1]; drives the traction + steering wheels via VehicleBody3D.
        public void Drive(float throttle, float steer, bool braking)
        {
            EngineForce = throttle * _engineForce;
            Steering = Mathf.DegToRad(steer * _maxSteerDeg);
            Brake = braking ? _brakeForce : 0f;
        }
    }
}
