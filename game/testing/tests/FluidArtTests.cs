using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // These tests exercise the item placement factory, raycastable physical ports,
    // solver flow, and actual transforms. A compile-only test cannot detect a lost
    // pump drum, mismatched ghost basis, or an invisible ParseObj result.
    public sealed class FluidArtTests : GameTest
    {
        public override string Name => "fluid.art_placements_and_flow";
        public override double TimeoutSimSeconds => 30;

        static Vector3[] Anchors(ushort id) => id switch
        {
            9111 or 9119 or 9120 => new[] {new Vector3(0,.7f,.55f)},
            9112 => new[] {new Vector3(-.5f,.6f,0),new Vector3(.5f,.6f,-.32f),new Vector3(.5f,.6f,.32f)},
            9113 => new[] {new Vector3(-.5f,.6f,-.32f),new Vector3(-.5f,.6f,.32f),new Vector3(.5f,.6f,0)},
            9110 => new[] {new Vector3(-.5f,.7f,0),new Vector3(.5f,.7f,0)},
            _ => new[] {new Vector3(-.5f,.6f,0),new Vector3(.5f,.6f,0)},
        };

        public override IEnumerable<Step> Run()
        {
            foreach (ushort id in new ushort[] {9110,9111,9114,9115,9116,9117,9121,9112,9113,9119,9120})
            {
                var stage=new Node3D();World.AddChild(stage);
                var def=DeployableDef.ById(id);
                float floor=id==9119 ? DeployableDef.SeaLevel-1 : 0;
                var ground=new StaticBody3D { Position=new Vector3(0,floor-.1f,6) };
                ground.AddChild(new CollisionShape3D {Shape=new BoxShape3D {Size=new Vector3(3,.2f,3)}});
                stage.AddChild(ground);
                var camera=new Camera3D {Position=new Vector3(0,floor+1.75f,9)};stage.AddChild(camera);
                camera.LookAt(new Vector3(0,floor,6));
                var placer=new BarricadePlacer {YawOffset=37};stage.AddChild(placer);placer.SetDef(def);
                yield return Ticks(2);
                T.Check($"{id}: real placement aim valid on floor (inlet at 1 m water depth)",placer.Aim(camera));
                var c=(FluidContainer)FluidDeploy.SpawnFor(def,stage,placer.Point,placer.Yaw);
                T.Check($"{id}: factory places at the ghost contact and yaw",c.Position.IsEqualApprox(placer.Point) && Mathf.Abs(c.RotationDegrees.Y-placer.Yaw)<.001f);
                placer.SetGhostVisible(false);
                c.Position=new Vector3(0,2,0); // isolate the following flow graph from the placement ground
                var ghost=Deployable.BuildMesh(def,out Aabb previewBounds);
                var bounds=FluidArt.Bounds(def);
                T.Check($"{id}: authored ghost, Y-up, ground Y=0",!def.ProcBox && def.Upright && ghost.Mesh is ArrayMesh && Mathf.Abs(previewBounds.Position.Y)<.000001f);
                T.Check($"{id}: def size = collision envelope",def.Size.IsEqualApprox(bounds.Size));
                var collider=c.GetNode<CollisionShape3D>("FluidCollider");
                T.Check($"{id}: placed collider matches catalog",((BoxShape3D)collider.Shape).Size.IsEqualApprox(bounds.Size) && collider.Position.IsEqualApprox(bounds.GetCenter()));
                T.Check($"{id}: ghost lies in collider",bounds.Grow(.00001f).Encloses(previewBounds));
                var mat=(StandardMaterial3D)ghost.MaterialOverride;
                T.Check($"{id}: actual 2x2 palette with nearest filtering",mat.AlbedoTexture?.GetWidth()==2 && mat.AlbedoTexture.GetHeight()==2 && mat.TextureFilter==BaseMaterial3D.TextureFilterEnum.Nearest);
                ghost.Free();
                var body=c.GetNode<MeshInstance3D>("FluidBody");
                var lod=body.GetNode<MeshInstance3D>("Lod1");
                T.Check($"{id}: both runtime meshes loaded and LOD split has no gap",body.Mesh is ArrayMesh && lod.Mesh is ArrayMesh && body.VisibilityRangeEnd>0 && Mathf.Abs(body.VisibilityRangeEnd-lod.VisibilityRangeBegin)<.00001f);
                yield return Ticks(2); // let physical HosePort bodies register with the space
                var anchors=Anchors(id);
                T.Check($"{id}: port count retained",c.PortNodes.Count==anchors.Length);
                for (int i=0;i<anchors.Length;i++)
                {
                    var p=c.PortNodes[i];var outward=new Vector3(p.Position.X,0,p.Position.Z).Normalized();
                    var ray=PhysicsRayQueryParameters3D.Create(c.ToGlobal(p.Position+outward*.3f),c.ToGlobal(p.Position-outward*.02f),HosePort.PortLayer);
                    var hit=c.GetWorld3D().DirectSpaceState.IntersectRay(ray);
                    T.Check($"{id}: anchor {i} unchanged and ray resolves its HosePort",p.Position.IsEqualApprox(anchors[i]) && hit.Count>0 && hit["collider"].AsGodotObject()==p && p.Node==c.Ports[i]);
                }
                Deployable powerSource=null;
                if (c is FluidPump || c is FluidPurifier)
                {
                    powerSource=Deployable.Spawn(stage,DeployableDef.Generator,new Vector3(-3,0,-4),0);
                    var wire=new Wire {Source=powerSource.Ports.Find(p=>p.Kind==DeployableDef.PortKind.Output),Consumer=((IPowerDevice)c).PowerPorts[0]};
                    stage.AddChild(wire);wire.AddToGroup("wires");
                    powerSource.TogglePower();PowerNet.Recompute(Tree);
                    T.Check($"{id}: real wired generator supplies the power inlet",((IPowerDevice)c).PowerPorts[0].Powered);
                }
                var inputs=new List<FluidContainer>();var outputs=new List<FluidContainer>();
                FluidType input=def.Fluid==FluidRole.Transformer ? def.FluidType : FluidType.Water;
                FluidType output=def.Fluid==FluidRole.Transformer ? def.FluidOut : FluidType.Water;
                void Connect(FluidContainer a,int ai,FluidContainer b,int bi)
                {stage.AddChild(new Hose {Source=a.Ports[ai],Consumer=b.Ports[bi]});}
                for (int i=0;i<c.Ports.Count;i++)
                {
                    if (c.Ports[i].Kind==FluidPortKind.Consumer)
                    {
                        var src=FluidContainer.Make(FluidRole.Source,new FluidTank(input,20000,20000,WaterQuality.Tainted),125);
                        src.Position=new Vector3(-3,4,i);stage.AddChild(src);inputs.Add(src);Connect(src,0,c,i);
                    }
                    else
                    {
                        var dst=FluidContainer.Make(FluidRole.Storage,new FluidTank(output,20000,0),40);
                        dst.Position=new Vector3(3,0,i);stage.AddChild(dst);outputs.Add(dst);
                        if (id==9119)
                        {
                            var lift=FluidPump.Make();lift.DebugForcePower=true;lift.Position=new Vector3(1,3,0);stage.AddChild(lift);
                            Connect(c,i,lift,0);Connect(lift,1,dst,0);
                        }
                        else Connect(c,i,dst,0);
                    }
                }
                for (int i=0;i<30;i++) FluidNet.Tick(Tree,.1f);
                bool received=outputs.Count>0 ? outputs.TrueForAll(t=>t.Tank.Amount>1) : inputs.TrueForAll(t=>t.Tank.Amount<20000);
                T.Check($"{id}: placed device actually moves/consumes fluid on every connected branch",received);
                if (id==9117)T.Check("sluice output still dirty",outputs.TrueForAll(t=>t.Tank.Quality==WaterQuality.Dirty));
                if (id==9121)T.Check("purifier output still clean",outputs.TrueForAll(t=>t.Tank.Quality==WaterQuality.Clean));
                if (c is IPowerDevice power)T.Check($"{id}: electrical ports still attached",power.PowerPorts.Count>0 && System.Linq.Enumerable.All(power.PowerPorts,p=>p.GetParent()==c));
                if (c is FluidPump pump)
                {
                    var drum=pump.GetNode<MeshInstance3D>("PumpDrum");var rest=new Vector3(0,1.25f,0);
                    T.Check("pump is powered AND flowing",pump.DriveActive);
                    pump.HubTick(.03);var moved=drum.Position;
                    pump.HubTick(.03);
                    T.Check("named drum vibrates between ticks",moved.DistanceTo(rest)>.001f && drum.Position.DistanceTo(moved)>.001f);
                    T.Check("lower drum follows same transform",drum.GetNode<MeshInstance3D>("Lod1").GlobalPosition.IsEqualApprox(drum.GlobalPosition));
                    powerSource.TogglePower();PowerNet.Recompute(Tree);pump.HubTick(.03);
                    T.Check("unpowered drum returns to rest",drum.Position.IsEqualApprox(rest));
                }
                if (id==9115)
                {
                    var handle=c.GetNode<MeshInstance3D>("ValveHandle");
                    c.ToggleValve();c.HubTick(.1);
                    T.Check("valve handle moves through an intermediate angle",handle.Rotation.Y>0 && handle.Rotation.Y<Mathf.Pi/2);
                    c.HubTick(.1);float before=outputs[0].Tank.Amount;
                    for(int i=0;i<10;i++)FluidNet.Tick(Tree,.1f);
                    T.Check("closed valve stops real flow and finishes quarter turn",Mathf.Abs(outputs[0].Tank.Amount-before)<.001f && Mathf.Abs(handle.Rotation.Y-Mathf.Pi/2)<.001f);
                    T.Check("closed handle samples red palette cell",((StandardMaterial3D)handle.MaterialOverride).Uv1Offset.X==.5f);
                    powerSource=Deployable.Spawn(stage,DeployableDef.Generator,new Vector3(-3,0,-4),0);
                    var control=new Wire {Source=powerSource.Ports.Find(p=>p.Kind==DeployableDef.PortKind.Output),Consumer=((IPowerDevice)c).PowerPorts[0]};
                    stage.AddChild(control);control.AddToGroup("wires");powerSource.TogglePower();PowerNet.Recompute(Tree);
                    c.HubTick(.03);c.HubTick(.2); // trigger state is applied after the base animation tick
                    for(int i=0;i<10;i++)FluidNet.Tick(Tree,.1f);
                    T.Check("wired OPEN trigger reopens, turns and resumes flow",outputs[0].Tank.Amount>before && Mathf.Abs(handle.Rotation.Y)<.001f && ((StandardMaterial3D)handle.MaterialOverride).Uv1Offset.X==0);
                    control.Consumer=((IPowerDevice)c).PowerPorts[1];PowerNet.Recompute(Tree);c.HubTick(.03);c.HubTick(.2);
                    T.Check("wired CLOSE trigger closes and turns the handle",c.Blocked && Mathf.Abs(handle.Rotation.Y-Mathf.Pi/2)<.001f);
                }
                stage.QueueFree();yield return Ticks(2);
            }
        }
    }
}
