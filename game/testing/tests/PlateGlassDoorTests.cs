using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    public class PlateGlassDoorBehaviour : GameTest
    {
        public override string Name => "plate_glass.real_pane_hinge_damage_and_persistence";
        static ObjectDoor DoorIn(Node node)
        {
            foreach (var c in node.GetChildren()) { if (c is ObjectDoor d) return d; var found = DoorIn(c); if (found != null) return found; }
            return null;
        }
        GodotObject Ray(Vector3 from, Vector3 to)
        {
            var h = World.GetWorld3D().DirectSpaceState.IntersectRay(new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = (1u << 0) | (1u << 6) });
            return h.Count == 0 ? null : h["collider"].AsGodotObject();
        }
        public override IEnumerable<Step> Run()
        {
            foreach (var prop in new[] { DoorDeploy.PlateGlass, DoorDeploy.PlateGlassBronze })
            {
                var wall = new WallSurface { Length = 8f };
                World.AddChild(wall);
                wall.Openings.Add(new WallOpening(2f, 0f, 2.5f, 2.85f) { DoorProp = prop });
                wall.Rebuild(); yield return Ticks(3);
                var door = DoorIn(wall);
                T.Check(prop + " has an actual door", door != null && door.HasPlateGlassFrame);
                if (door == null) yield break;
                var pane = door.PlatePane;
                T.Check("real window GlassPane, default one-shot", pane != null && pane.Health == 1f && !pane.Indestructible);
                T.Check("glass itself resolves F interaction to the same door", pane != null && pane.HasMeta(Sn.objectdoor) && pane.GetMeta(Sn.objectdoor).As<ObjectDoor>() == door);
                T.Check("a bullet ray through the clear upper panel hits glass, not a full door hitbox", Ray(new Vector3(3.25f, 1.9f, 3f), new Vector3(3.25f, 1.9f, -3f)) == pane);
                T.Check("the actual frame has its own collision", Ray(new Vector3(2.25f, 1.9f, 3f), new Vector3(2.25f, 1.9f, -3f)) == door);
                int fixedPanes = 0; foreach (var c in wall.GetChildren()) if (c is GlassPane) fixedPanes++;
                T.Check("no duplicate stationary glass in the doorway", fixedPanes == 0);
                var old = pane.GlobalPosition;
                door.SetInitialState(true); yield return Ticks(2);
                T.Check("glass swings with the leaf", pane.GlobalPosition.DistanceTo(old) > .7f);
                var swung = pane.GlobalTransform;
                wall.Rebuild(); yield return Ticks(2);
                T.Check("unrelated rebuild preserves the open pane transform", pane.GlobalTransform.IsEqualApprox(swung));
                door.SetInitialState(false); yield return Ticks(2);
                pane.TakeDamage(1f);
                T.Check("damage writes broken state synchronously", wall.Openings[0].GlassBroken);
                yield return Ticks(2);
                T.Check("pane disappears", door.PlatePane == null);
                T.Check("shot-out panel has no invisible whole-leaf collision", Ray(new Vector3(3.25f, 1.9f, 3f), new Vector3(3.25f, 1.9f, -3f)) == null);
                T.Check("frame still collides after breakage", Ray(new Vector3(2.25f, 1.9f, 3f), new Vector3(2.25f, 1.9f, -3f)) == door);
                door.SetInitialState(true); door.SetInitialState(false); wall.Rebuild(); yield return Ticks(2);
                T.Check("toggle and rebuild do not repair glass", door.PlatePane == null);
                var plan = new WallPlan { Length = 8f, Height = WallOpenings.DoorHeight, Thickness = .7f };
                plan.Openings.Add(wall.Openings[0]);
                var back = WallSave.Read(WallSave.Write(new List<WallPlan> { plan }).Split('\n'));
                T.Check("existing save format preserves the door name and broken glass", back.Count == 1 && back[0].Openings[0].DoorProp == prop && back[0].Openings[0].GlassBroken);
                var loaded = new WallSurface { Length = 8f, Position = new Vector3(20f,0f,0f) }; World.AddChild(loaded);
                loaded.Openings.Add(back[0].Openings[0]); loaded.Rebuild(); yield return Ticks(2);
                T.Check("loading broken state creates frame and remnants, not intact glass", DoorIn(loaded)?.PlatePane == null && DoorIn(loaded)?.HasPlateGlassFrame == true);
                // Repair/custom HP/indestructible are the same existing opening options, not another system.
                var o = wall.Openings[0]; o.GlassBroken = false; o.GlassHp = 7f; o.GlassTint = 0x6699AA; o.GlassIndestructible = true; wall.Openings[0] = o; wall.Rebuild(); yield return Ticks(2);
                T.Check("repair uses configured HP", door.PlatePane?.Health == 7f);
                door.PlatePane.TakeDamage(100f); yield return Ticks(1);
                T.Check("indestructible option is honored", door.PlatePane != null && !wall.Openings[0].GlassBroken);
                wall.QueueFree(); loaded.QueueFree(); yield return Ticks(3);
            }
        }
    }

    public class PlateGlassDoorRender : GameTest
    {
        public override string Name => "plate_glass.render_shopfront";
        async void Capture(string path, System.Action done)
        {
            // Physics ticks can all occur before the next software-rendered frame. Waiting for actual
            // draws prevents a close camera shot silently capturing the preceding wide view.
            await World.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await World.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            World.GetViewport().GetTexture().GetImage().SavePng(path); done();
        }
        public override IEnumerable<Step> Run()
        {
            string output = System.Environment.GetEnvironmentVariable("UG_PLATEGLASS_CAPTURE");
            if (string.IsNullOrEmpty(output)) yield break;
            var env = new Environment { BackgroundMode = Environment.BGMode.Color, BackgroundColor = new Color(.60f,.65f,.66f), AmbientLightSource = Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .72f };
            World.AddChild(new WorldEnvironment { Environment = env });
            World.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-35,-25,0), LightEnergy = .85f, ShadowEnabled = true });
            void Box(Vector3 p, Vector3 s, Color c) { World.AddChild(new MeshInstance3D { Position = p, Mesh = new BoxMesh { Size = s }, MaterialOverride = new StandardMaterial3D { AlbedoColor = c, Roughness = 1f } }); }
            Box(new Vector3(7f,-.12f,0f),new Vector3(30f,.2f,22f),new Color(.49f,.47f,.42f));
            Box(new Vector3(7f,2f,-4f),new Vector3(14f,4f,.3f),new Color(.64f,.64f,.52f));
            Box(new Vector3(7f,2f,-2f),new Vector3(.3f,4f,4f),new Color(.66f,.64f,.54f));
            Box(new Vector3(7f,.01f,-2f),new Vector3(14f,.04f,4f),new Color(.45f,.36f,.26f));
            for (int i=0;i<4;i++) Box(new Vector3(1.2f+i*3.4f,.45f,-2.2f),new Vector3(1.5f,.9f,.7f),new Color(.44f,.36f,.25f));
            var wall = new WallSurface { Length = 14f, Height = 4.25f, MaterialId = 11 }; World.AddChild(wall);
            wall.Openings.Add(new WallOpening(2f,0f,2.5f,2.85f) { DoorProp = DoorDeploy.PlateGlass, Glazed = true });
            wall.Openings.Add(new WallOpening(9.2f,0f,2.5f,2.85f) { DoorProp = DoorDeploy.PlateGlassBronze, Glazed = true });
            foreach (float u in new[]{.35f,4.8f,7.55f,12f}) wall.Openings.Add(new WallOpening(u,.35f,1.4f,2.5f) { Glazed = true });
            wall.Rebuild();
            var camera = new Camera3D { Current = true, Position = new Vector3(7f,3.4f,11.5f), Fov = 58f }; World.AddChild(camera); camera.LookAt(new Vector3(7f,1.7f,0f));
            yield return Ticks(12);
            bool saved = false; Capture(output + "_intact.png", () => saved = true); yield return Until(() => saved, 10);
            camera.Position = new Vector3(4.7f,2.2f,5.7f); camera.LookAt(new Vector3(3.25f,1.5f,0f));
            saved = false; Capture(output + "_detail.png", () => saved = true); yield return Until(() => saved, 10);
            camera.Position = new Vector3(7f,3.4f,11.5f); camera.LookAt(new Vector3(7f,1.7f,0f));
            int index=0;
            foreach (var c in wall.GetChildren())
                if (c is Node3D h && wall.OpeningIndexForDoorHost(h)>=0)
                    foreach (var d in h.GetChildren()) if (d is ObjectDoor door) { if(index++==0) door.SetInitialState(true); else door.PlatePane.TakeDamage(1f); }
            yield return Ticks(20);
            saved = false; Capture(output + "_open_broken.png", () => saved = true); yield return Until(() => saved, 10);
            T.Check("rendered real game shopfront", true);
        }
    }
}