using Godot;

namespace UnturnedGodot
{
    // Drivable vehicle. Source: InteractableVehicle + VehicleAsset (WheelCollider rig -> Godot VehicleBody3D +
    // VehicleWheel3D 1:1). Jeep params from Jeep.dat: Speed_Max 12.5, Steer 28 deg (low) .. 14 (high), front-wheel
    // steered (WheelConfigurations: IsColliderSteered true,true,false,false), EngineMaxTorque 2.8, Brake 32.
    public partial class Vehicle : VehicleBody3D
    {
        float _engineForce = 600f;   // scaled to Godot units (torque 2.8 is Unity WheelCollider Nm; tuned by feel)
        float _maxSteerDeg = 28f;
        float _brakeForce = 12f;

        // Jeep wheel layout (Godot space, front = -Z). Unity local X=+-1.30, Z=+-1.40 (front Z+ -> Godot -Z), Y=+0.25.
        static readonly (float x, float y, float z, bool steer)[] _jeepWheels =
        {
            (-1.30f, 0.25f, -1.40f, true),   // front-left  (steered)
            ( 1.30f, 0.25f, -1.40f, true),   // front-right (steered)
            (-1.30f, 0.25f,  1.40f, false),  // rear-left
            ( 1.30f, 0.25f,  1.40f, false),  // rear-right
        };

        public static Vehicle BuildJeep()
        {
            var v = new Vehicle { Mass = 900f };

            var bodyMat = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.45f, 0.30f), Metallic = 0f, Roughness = 0.9f };
            var body = new MeshInstance3D { Name = "Body", Mesh = ContentProvider.ParseObj("res://content/jeep_body.txt"), MaterialOverride = bodyMat };
            v.AddChild(body);

            // collision hull ~ body bbox (x +-1.26, y -0.27..2.13, z +-2.52)
            var col = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2.4f, 1.4f, 5.0f) }, Position = new Vector3(0f, 0.6f, 0f) };
            v.AddChild(col);

            var wheelMat = new StandardMaterial3D { AlbedoColor = new Color(0.09f, 0.09f, 0.10f), Metallic = 0f, Roughness = 1f };
            var wheelMesh = ContentProvider.ParseObj("res://content/jeep_wheel.txt");
            foreach (var (x, y, z, steer) in _jeepWheels)
            {
                var w = new VehicleWheel3D
                {
                    Position = new Vector3(x, y, z),
                    UseAsSteering = steer,
                    UseAsTraction = true,          // 4wd for grip; refine to rear-drive later
                    WheelRadius = 0.6f,
                    WheelRestLength = 0.25f,
                    SuspensionTravel = 0.25f,
                    SuspensionStiffness = 30f,
                    DampingCompression = 2.4f,
                    DampingRelaxation = 3.0f,
                    WheelFrictionSlip = 3.5f,
                };
                // left wheels: flip the mesh so the tread faces outward
                var wm = new MeshInstance3D { Mesh = wheelMesh, MaterialOverride = wheelMat, Scale = new Vector3(x < 0 ? -1f : 1f, 1f, 1f) };
                w.AddChild(wm);
                v.AddChild(w);
            }
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
