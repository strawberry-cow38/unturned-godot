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
        public override double TimeoutSimSeconds => 40;
        // Wind the wheel all the way, DERIVED from the animation's own duration rather than a fixed count.
        // It was 15 ticks when the travel was 1.1 s, and slowing the valve to 4.5 s (strawberry: "slow down
        // the valve animation by a lot") silently left the test asserting a half-turned wheel.
        static int ValveTicks => (int)(FluidContainer.ValveSeconds / .1f) + 4;

        // WHERE the ports are is now the art catalog's business: the four machines strawberry enlarged
        // carry their hose anchors further out, because their fittings deliberately did NOT grow with
        // them. Hard-coding the positions here would just be a second copy to forget to update -- what
        // is worth asserting is that the port and the modelled spigot agree, and the geometric half of
        // that is verify_fluid_art.py's "every anchor carries a spigot collar" check.
        static Vector3[] Anchors(DeployableDef def) => def.Id switch
        {
            9111 or 9119 or 9120 => new[] {FluidArt.Port(def,0,.7f,.55f)},
            9112 => new[] {FluidArt.Port(def,-.5f,.6f,0),FluidArt.Port(def,.5f,.6f,-.32f),FluidArt.Port(def,.5f,.6f,0),FluidArt.Port(def,.5f,.6f,.32f)},
            9113 => new[] {FluidArt.Port(def,-.5f,.6f,-.32f),FluidArt.Port(def,-.5f,.6f,0),FluidArt.Port(def,-.5f,.6f,.32f),FluidArt.Port(def,.5f,.6f,0)},
            9110 => new[] {FluidArt.Port(def,-.5f,.7f,0),FluidArt.Port(def,.5f,.7f,0)},
            _ => new[] {FluidArt.Port(def,-.5f,.6f,0),FluidArt.Port(def,.5f,.6f,0)},
        };

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var drops = new Node3D(); World.AddChild(drops);
            var floorBody = new StaticBody3D { Position = new Vector3(0,-.1f,14) };
            floorBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(20,.2f,4) } });
            drops.AddChild(floorBody);
            var dropped = new List<WorldItem>();
            for (ushort id=9110;id<=9121;id++)
            {
                var icon = InventoryUI.IconFor(id);
                T.Check($"{id}: inventory loads the rendered icon",icon?.GetWidth()==256 && icon.GetHeight()==256);
                var visual = WorldItem.BuildReplicaVisual(id,Colors.White);
                T.Check($"{id}: dropped and replica paths load an OBJ, not fallback",visual.Mesh is ArrayMesh);
                var size = visual.Mesh.GetAabb().Size;
                T.Check($"{id}: dropped model is within the measured portable-item envelope",size[(int)size.MaxAxisIndex()]<=.87221f);
                visual.Free();
                var item = WorldItem.Spawn(drops,new Item(id),new Vector3(-8+(id-9110)*1.4f,1.5f,14));
                dropped.Add(item);
                foreach (var node in item.GetChildren())
                    if (node is CollisionShape3D col && col.Shape is BoxShape3D box)
                        T.Check($"{id}: drop collider matches reduced mesh plus standard pickup margin",box.Size.IsEqualApprox(size*1.15f));
            }
            yield return Ticks(120);
            foreach (var item in dropped)
                T.Check($"{item.Item.id}: physical drop lands on the floor",item.GlobalPosition.Y>0 && item.GlobalPosition.Y<.9f);
            drops.QueueFree(); yield return Ticks(2);
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
                var anchors=Anchors(c.Def);
                T.Check($"{id}: port count matches authored topology",c.PortNodes.Count==anchors.Length);
                for (int i=0;i<anchors.Length;i++)
                {
                    var p=c.PortNodes[i];var outward=new Vector3(p.Position.X,0,p.Position.Z).Normalized();
                    var ray=PhysicsRayQueryParameters3D.Create(c.ToGlobal(p.Position+outward*.3f),c.ToGlobal(p.Position-outward*.02f),HosePort.PortLayer);
                    var hit=c.GetWorld3D().DirectSpaceState.IntersectRay(ray);
                    T.Check($"{id}: anchor {i} matches the art catalog and a ray resolves its HosePort",p.Position.IsEqualApprox(anchors[i]) && hit.Count>0 && hit["collider"].AsGodotObject()==p && p.Node==c.Ports[i]);
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
                if (c is IPowerDevice power)
                {
                    T.Check($"{id}: electrical ports still attached",power.PowerPorts.Count>0 && System.Linq.Enumerable.All(power.PowerPorts,p=>p.GetParent()==c));
                    foreach (var p in power.PowerPorts)
                    {
                        var ray=PhysicsRayQueryParameters3D.Create(c.ToGlobal(p.Position+Vector3.Back*.3f),
                            c.ToGlobal(p.Position-Vector3.Back*.02f),ConnectionPort.PortLayer);
                        var hit=c.GetWorld3D().DirectSpaceState.IntersectRay(ray);
                        T.Check($"{id}: {p.Role} is wireable from the shared panel face",
                            p.Position.IsEqualApprox(FluidElectricalPanel.Anchor(id,p.Role)) &&
                            hit.Count>0 && hit["collider"].AsGodotObject()==p);
                    }
                }
                if (c is FluidPump pump)
                {
                    var drum=pump.GetNode<MeshInstance3D>("PumpDrum");
                    T.Check("pump is powered AND flowing",pump.DriveActive);
                    float a0=pump.DebugPumpAngle;var b0=drum.Basis;
                    for(int i=0;i<6;i++)pump.HubTick(.03);
                    float a1=pump.DebugPumpAngle;
                    T.Check("driven coupling turns about its own axis",a1-a0>.01f && !drum.Basis.IsEqualApprox(b0));
                    T.Check("coupling local Y stays aligned with the visible Z shaft while spinning",
                        drum.Basis.Y.IsEqualApprox(Vector3.Back) && b0.Y.IsEqualApprox(drum.Basis.Y));
                    T.Check("LOD1 coupling follows the same transform",drum.GetNode<MeshInstance3D>("Lod1").GlobalTransform.IsEqualApprox(drum.GlobalTransform));
                    powerSource.TogglePower();PowerNet.Recompute(Tree);
                    // a loaded rotor has inertia: it must COAST rather than stop dead on the same tick
                    pump.HubTick(.03);
                    T.Check("unpowered coupling coasts, not an instant stop",pump.DebugPumpAngle>a1);
                    for(int i=0;i<40;i++)pump.HubTick(.03);
                    float a3=pump.DebugPumpAngle;pump.HubTick(.03);
                    T.Check("coupling comes to rest once spun down",Mathf.IsEqualApprox(a3,pump.DebugPumpAngle));
                }
                if (id==9115)
                {
                    var handle=c.GetNode<MeshInstance3D>("ValveHandle");
                    c.ToggleValve();c.HubTick(.1);
                    T.Check("valve handle moves through an intermediate angle",c.DebugValveAngle>0 && c.DebugValveAngle<FluidContainer.ValveTravel);
                    // wind it the rest of the way: a real gate valve is ~2.5 turns and takes ~1.1 s,
                    // where the old quarter turn was done in 0.2 s. Flow stops on the toggle either way.
                    for(int i=0;i<ValveTicks;i++)c.HubTick(.1);float before=outputs[0].Tank.Amount;
                    for(int i=0;i<10;i++)FluidNet.Tick(Tree,.1f);
                    T.Check("closed valve stops real flow and winds fully shut",Mathf.Abs(outputs[0].Tank.Amount-before)<.001f && Mathf.Abs(c.DebugValveAngle-FluidContainer.ValveTravel)<.001f);
                    // RED IN BOTH STATES (strawberry: "change the valve handle to be red"). The wheel
                    // used to swap palette cell to show closed; the turn shows it now, so assert the
                    // colour does NOT move -- otherwise reintroducing the swap would pass silently.
                    T.Check("handle stays on the red palette cell when closed",((StandardMaterial3D)handle.MaterialOverride).Uv1Offset.X==0f);
                    powerSource=Deployable.Spawn(stage,DeployableDef.Generator,new Vector3(-3,0,-4),0);
                    var control=new Wire {Source=powerSource.Ports.Find(p=>p.Kind==DeployableDef.PortKind.Output),Consumer=((IPowerDevice)c).PowerPorts[0]};
                    stage.AddChild(control);control.AddToGroup("wires");powerSource.TogglePower();PowerNet.Recompute(Tree);
                    c.HubTick(.03);for(int i=0;i<ValveTicks;i++)c.HubTick(.1); // trigger state is applied after the base animation tick
                    for(int i=0;i<10;i++)FluidNet.Tick(Tree,.1f);
                    T.Check("wired OPEN trigger reopens, turns and resumes flow",outputs[0].Tank.Amount>before && Mathf.Abs(c.DebugValveAngle)<.001f && ((StandardMaterial3D)handle.MaterialOverride).Uv1Offset.X==0);
                    control.Consumer=((IPowerDevice)c).PowerPorts[1];PowerNet.Recompute(Tree);c.HubTick(.03);for(int i=0;i<ValveTicks;i++)c.HubTick(.1);
                    T.Check("wired CLOSE trigger closes and turns the handle",c.Blocked && Mathf.Abs(c.DebugValveAngle-FluidContainer.ValveTravel)<.001f);
                }
                stage.QueueFree();yield return Ticks(2);
            }
        }
    }
}
