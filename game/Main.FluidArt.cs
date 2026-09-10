using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot
{
    public partial class Main
    {
        // tools/shot.py owns rendering invocation and capture. This scene uses the
        // real item-placement factory, with retail props on the same lit ground.
        void BuildFluidArtScene(string mode)
        {
            if (System.Environment.GetEnvironmentVariable("UG_FLUIDICON") == "1")
            { BuildFluidIconScene(ushort.Parse(mode)); return; }
            GetWindow().Mode = Window.ModeEnum.Windowed;
            GetWindow().Size = mode == "gallery" ? new Vector2I(1600,1000)
                : mode == "flow" ? new Vector2I(1280,800) : new Vector2I(900,900);
            if (System.Environment.GetEnvironmentVariable("UG_FLUIDUNDERSIDE") != "1") AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(40, 40) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(.32f, .36f, .30f), Roughness = 1f },
            });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -40, 0), ShadowEnabled = true });
            AddChild(new WorldEnvironment { Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.50f,.66f,.86f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White, AmbientLightEnergy = .65f,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            }});
            void Label(string text, Vector3 at)
            {
                AddChild(new Label3D { Text = text, Position = at, FontSize = 48, PixelSize = mode == "gallery" ? .005f : .0015f,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Modulate = Colors.White, NoDepthTest = true });
            }
            FluidContainer Place(ushort id, Vector3 at)
            {
                var c = (FluidContainer)FluidDeploy.SpawnFor(DeployableDef.ById(id), this, at, 0);
                // Fixed scene labels leave the silhouettes clear in a comparison
                // gallery. The live plumbing scene retains the real status bars.
                if (mode != "flow")
                    foreach (var child in c.GetChildren()) if (child is InfoBillboard info) info.SetActive(false);
                if (mode != "flow" && System.Environment.GetEnvironmentVariable("UG_FLUIDLOD1") != "1")
                    foreach (var child in c.GetChildren())
                        if (child is MeshInstance3D mi)
                        {
                            mi.VisibilityRangeEnd = 1000;
                            if (mi.GetNodeOrNull<MeshInstance3D>("Lod1") is { } lod) lod.VisibilityRangeBegin = 1000;
                        }
                return c;
            }
            void Reference(string name, Vector3 at)
            {
                string dir = ProjectSettings.GlobalizePath("res://content/objects/");
                var mesh = ObjMesh.Load(dir + name + ".obj");
                var basis = new Basis(Vector3.Right, -Mathf.Pi / 2f);
                var bounds = new Transform3D(basis, Vector3.Zero) * mesh.GetAabb();
                AddChild(new MeshInstance3D { Mesh = mesh, Basis = basis, Position = at - Vector3.Up * bounds.Position.Y,
                    MaterialOverride = new StandardMaterial3D { AlbedoTexture = ContentProvider.TextureCached(dir + name + "_tex.png"),
                        Roughness = 1, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
                Label(name + " (reference)", at + new Vector3(0,-.02f,.8f));
            }
            var cam = new Camera3D { Current = true, Projection = Camera3D.ProjectionType.Orthogonal };
            AddChild(cam);
            if (mode == "gallery")
            {
                ushort[] ids = {9110,9111,9114,9115,9116,9117,9121,9112,9113,9119,9120};
                for (int i = 0; i < ids.Length; i++)
                {
                    var pos = new Vector3((i % 6 - 2.5f) * 2.1f, 0, i / 6 * 3.8f);
                    var c = Place(ids[i], pos);
                    Label(ids[i] + " " + c.Def.Name.Replace("Fluid ", ""), pos + new Vector3(0,-.02f,.85f));
                }
                Reference("Barrel_0", new Vector3(-4.2f,0,-3.4f));
                Reference("Barrel_1", new Vector3(-2.1f,0,-3.4f));
                Reference("Generator_0", new Vector3(0,0,-3.4f));
                Reference("Propane_0", new Vector3(2.1f,0,-3.4f));
                cam.Size = 15.3f; cam.Position = new Vector3(9,12,17); cam.LookAt(new Vector3(0,.6f,.6f));
                Log.Print("[fluidart] gallery: 11 placed devices, 4 retail references; LOD0 forced for comparison");
            }
            else if (mode == "flow")
            {
                var src = Place(9111,new Vector3(-4,0,0));
                var pump = (FluidPump)Place(9114,new Vector3(-1.5f,0,0));
                var valve = Place(9115,new Vector3(1,1,0));
                var dst = Place(9110,new Vector3(3.5f,1.5f,0));
                foreach (var (x,height) in new[] {(1f,1f),(3.5f,1.5f)})
                    AddChild(new MeshInstance3D { Mesh=new BoxMesh {Size=new Vector3(1,height,1)},
                        Position=new Vector3(x,height/2,0),MaterialOverride=new StandardMaterial3D {AlbedoColor=new Color(.36f,.36f,.36f),Roughness=1} });
                void Connect(FluidContainer a, int ai, FluidContainer b, int bi)
                {
                    var h = new Hose { Source = a.Ports[ai], Consumer = b.Ports[bi] }; AddChild(h);
                    h.SetPoints(new List<Vector3> {a.PortNodes[ai].GlobalPosition,b.PortNodes[bi].GlobalPosition},true);
                }
                Connect(src,0,pump,0);Connect(pump,1,valve,0);Connect(valve,1,dst,0);
                // The scene bypasses PlayerController.AdoptFluidType on mouse
                // completion; use its same graph resolver for the receiving tank.
                dst.Tank.Type = FluidNet.ResolveNetType(GetTree(),valve.PortNodes[1],new HashSet<FluidContainer>());
                Deployable.InstantRampForTests = true;
                var gen = Deployable.Spawn(this,DeployableDef.Generator,new Vector3(-1.5f,0,-3),0);
                var wire = new Wire { Source = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output), Consumer = pump.PowerPorts[0] };
                AddChild(wire);
                wire.AddToGroup("wires");
                wire.SetPoints(new List<Vector3> {wire.Source.GlobalPosition,wire.Consumer.GlobalPosition},true);
                gen.TogglePower();PowerNet.Recompute(GetTree());
                for (int i=0;i<20;i++) FluidNet.Tick(GetTree(),.1f);
                pump.HubTick(.03);
                Log.Print($"[fluidart] flow: power={pump.IsPowered}, drive={pump.DriveActive}, received={dst.Tank.Amount}, drum={pump.GetNode<MeshInstance3D>("PumpDrum").Position}");
                cam.Size=11;cam.Position=new Vector3(6,7,12);cam.LookAt(new Vector3(0,1.1f,0));
            }
            else if (ushort.TryParse(mode,out ushort id) && DeployableDef.ById(id)?.Fluid != null)
            {
                var c=Place(id,Vector3.Zero);
                if (id==9115 && System.Environment.GetEnvironmentVariable("UG_FLUIDCLOSED")=="1")
                {c.ToggleValve();c.HubTick(.2);}
                bool low=System.Environment.GetEnvironmentVariable("UG_FLUIDLOD1")=="1";
                var bs=FluidArt.Bounds(c.Def).Size;
                // FRAME FROM THE BOUNDS, not a fixed 2.5. The enlarged machines outgrew the hard-coded
                // size and came back cropped -- the refinery is 2.78 m tall now.
                cam.Size=Mathf.Max(Mathf.Max(bs.X,bs.Z),bs.Y)*1.35f+.4f;
                // UG_FLUIDANGLE orbits the camera so a prop can be shot from several sides. strawberry
                // 2026-09-10: "give astra multiple angles of its props" -- one three-quarter view hides
                // exactly the seams and open ends that need looking at.
                float az=Mathf.DegToRad(float.TryParse(System.Environment.GetEnvironmentVariable("UG_FLUIDANGLE"),
                    System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var a)?a:35f);
                float el=Mathf.DegToRad(float.TryParse(System.Environment.GetEnvironmentVariable("UG_FLUIDELEV"),
                    System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var e)?e:28f);
                var target=new Vector3(0,bs.Y/2,0);
                var dir=new Vector3(Mathf.Sin(az)*Mathf.Cos(el),Mathf.Sin(el),Mathf.Cos(az)*Mathf.Cos(el)).Normalized();
                cam.Position=target+dir*(low ? c.GetNode<MeshInstance3D>("FluidBody").VisibilityRangeEnd+10 : 8f);
                cam.LookAt(target);
                Label(c.Def.Name + (low ? " — LOD1" : "") + $"  {Mathf.RoundToInt(Mathf.RadToDeg(az))}°",new Vector3(0,-.15f,.55f));
                Log.Print($"[fluidart] placed {id}: {c.PortNodes.Count} hose ports, mesh={c.GetNode<MeshInstance3D>("FluidBody").Mesh.GetAabb()}");
            }
        }

        // Render the actual manifest model through the dropped-item loader. shot.py trims only
        // transparent pixels and downsamples this render to the inventory's 256 px convention.
        void BuildFluidIconScene(ushort id)
        {
            GetWindow().Mode = Window.ModeEnum.Windowed;
            GetWindow().Size = new Vector2I(512,512);
            GetViewport().TransparentBg = true;
            var visual = WorldItem.BuildReplicaVisual(id, Colors.White);
            if (visual.Mesh is not ArrayMesh) throw new InvalidOperationException($"No authored item mesh for {id}");
            // WorldItem defaults to two-sided retail materials. Back culling here also makes the
            // rendered icons a check of our own clockwise-front meshes.
            var material = (StandardMaterial3D)visual.MaterialOverride.Duplicate();
            material.CullMode = BaseMaterial3D.CullModeEnum.Back;
            visual.MaterialOverride = material;
            AddChild(visual);
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55,-40,0) });
            AddChild(new WorldEnvironment { Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0,0,0,0),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White, AmbientLightEnergy = .65f,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            }});
            var bounds = visual.Mesh.GetAabb();
            var cam = new Camera3D { Current = true, Projection = Camera3D.ProjectionType.Orthogonal,
                Size = bounds.Size.Length()*1.15f };
            AddChild(cam);
            cam.Position = bounds.GetCenter()+new Vector3(3,2.4f,4).Normalized()*4;
            cam.LookAt(bounds.GetCenter());
            Log.Print($"[fluidart] icon {id}: manifest model {bounds}, transparent render");
        }
    }
}
