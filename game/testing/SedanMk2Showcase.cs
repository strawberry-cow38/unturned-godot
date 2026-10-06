using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace UnturnedGodot
{
    // Actual runtime builders/physics/rigs and a real PlayerController camera, not the private art viewer.
    // --sedan-mk2-showcase=DIR. Explicit output DIRECTORY; no map/user-save writes.
    public partial class SedanMk2Showcase : Node3D
    {
        public string OutputDir;
        Camera3D _camera;
        Vehicle _old, _new;
        async Task Frames(int n) { for (int i=0;i<n;i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Physics(int n) { for (int i=0;i<n;i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }
        async Task Save(string name)
        {
            await Frames(1);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            string path = Path.Combine(OutputDir, name + ".png");
            var err = image.SavePng(path);
            if (err != Error.Ok) throw new IOException($"PNG write failed {err}: {path}");
            GD.Print($"[sedan-mk2] saved {path}");
        }
        async Task View(Vehicle car, string label, string view)
        {
            _camera.Current=true; _camera.Projection=Camera3D.ProjectionType.Orthogonal;
            Vector3 eye, target;
            switch(view)
            {
                case "side": _camera.Size=4.7f; eye=new(12,2.1f,0); target=new(0,1,0); break;
                case "front": _camera.Size=5.8f; eye=new(9,6,-11); target=new(0,1,0); break;
                case "rear": _camera.Size=5.8f; eye=new(9,6,11); target=new(0,1,0); break;
                case "engine": _camera.Size=2.5f; eye=new(4.6f,4.1f,-5.2f); target=new(0,.72f,-2.25f); break;
                default: _camera.Size=2.4f; eye=new(4.2f,3.8f,5); target=new(0,.76f,2.33f); break;
            }
            _camera.LookAtFromPosition(car.GlobalTransform*eye, car.GlobalTransform*target, car.GlobalTransform.Basis.Y);
            await Save(label+"_"+view);
        }
        public override async void _Ready()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDir)) throw new ArgumentException("output directory missing");
                OutputDir=Path.GetFullPath(OutputDir); Directory.CreateDirectory(OutputDir);
                GetWindow().Size=new Vector2I(1280,720);
                var env=new Godot.Environment { BackgroundMode=Godot.Environment.BGMode.Color,
                    BackgroundColor=new Color(.53f,.61f,.67f), AmbientLightSource=Godot.Environment.AmbientSource.Color,
                    AmbientLightColor=Colors.White, AmbientLightEnergy=.55f, TonemapMode=Godot.Environment.ToneMapper.Linear };
                AddChild(new WorldEnvironment { Environment=env });
                AddChild(new DirectionalLight3D { RotationDegrees=new Vector3(-50,-28,0),LightEnergy=.85f,ShadowEnabled=true });
                AddChild(new DirectionalLight3D { RotationDegrees=new Vector3(-20,160,0),LightEnergy=.30f });
                var ground=new StaticBody3D();
                ground.AddChild(new CollisionShape3D { Shape=new WorldBoundaryShape3D() });
                ground.AddChild(new MeshInstance3D { Mesh=new PlaneMesh { Size=new Vector2(70,70) },
                    MaterialOverride=new StandardMaterial3D {AlbedoColor=new Color(.38f,.43f,.40f),Roughness=1} });
                AddChild(ground);
                _old=Vehicle.BuildByName("sedan",5); _new=Vehicle.BuildByName("sedan_mk2",5);
                _old.Position=new Vector3(-5,1.2f,0); _new.Position=new Vector3(5,1.2f,0);
                AddChild(_old); AddChild(_new);
                _camera=new Camera3D { Current=true, Near=.05f,Far=100 }; AddChild(_camera);
                await Physics(100);
                foreach(var car in new[]{_old,_new})
                    GD.Print($"[sedan-mk2] {car.DisplayName}: grounded={car.DebugWheelNodes.Count(w=>w.IsInContact())}/4 rootY={car.Position.Y:F4} speed={car.LinearVelocity.Length():F4} seats={car.SeatCount} panels={car.AuthoredPanelRig?.Count??0}");
                if (System.Environment.GetEnvironmentVariable("UG_MK2_REMAINING") != "1")
                {
                    foreach(string v in new[]{"side","front","rear"}) await View(_old,"original",v);
                    foreach(string v in new[]{"side","front","rear"}) await View(_new,"mk2_closed",v);
                }
                var player=new PlayerController {Position=_new.Position+new Vector3(-4,1,0)}; AddChild(player);
                _camera.Current=true; player.Camera.Current=false;
                await Physics(3); player.EnterVehicle(_new,0); await Physics(5);
                if (!player.IsDriving || player.SeatIndex!=0) throw new InvalidOperationException("real driver boarding refused");
                _camera.Current=false; player.Camera.Current=true;
                GD.Print($"[sedan-mk2] actual driver eye={player.Camera.GlobalPosition} body={player.DebugSeatedBodyLocal} steering={_new.SteerPivotLocal}");
                await Save("mk2_driver_1p");
                if (!player.TryExitVehicle()) throw new InvalidOperationException("safe driver exit refused on clear floor");
                player.Visible=false; player.Camera.Current=false; _camera.Current=true;
                for(int i=0;i<4;i++) _new.AuthoredPanelRig.PulseSeat(i);
                _new.AuthoredPanelRig.SetCompartment(Vehicle.AccessKind.Hood,true);
                _new.AuthoredPanelRig.SetCompartment(Vehicle.AccessKind.Trunk,true);
                _new.AuthoredPanelRig.Tick(VehiclePanelRig.SwingSeconds); // runtime rig endpoint for inspection; UI lifecycle has L1 coverage
                foreach(string v in new[]{"side","front","rear","engine","trunk"}) await View(_new,"mk2_open",v);
                GD.Print("SEDAN_MK2_RUNTIME_SHOWCASE_SUCCESS");GetTree().Quit(0);
            }
            catch(Exception e) { GD.PrintErr("[sedan-mk2] FAILED "+e);GetTree().Quit(1); }
        }
    }
}
