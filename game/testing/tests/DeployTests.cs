using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The UG_DEPLOYDMG stage states as assertions (the smoke/fire/wreck LOOKS stay a visual-golden scene): each
    // DebugStage jumps the generator to a damage stage; verify the state the visuals hang off. A generator wreck is
    // a burning salvageable husk (stays in the tree) with its port cubes retired -- unlike the shattering spotlight.
    public class DeployDamageStages : GameTest
    {
        public override string Name => "deploy.damage_stages";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, Vector3.Zero, 0f);
            yield return Ticks(1);
            T.Check("fresh: full health", Mathf.Abs(gen.Health - gen.HealthMax) < 0.01f);
            T.Check("fresh: not on fire, not a wreck", !gen.OnFire && !gen.IsWreck);

            gen.DebugStage("smoke");
            T.Check($"smoke stage: health 40% (got {gen.Health / gen.HealthMax:0.00})", Mathf.Abs(gen.Health - gen.HealthMax * 0.4f) < 0.01f);
            gen.DebugStage("heavy");
            T.Check($"heavy stage: health 15% (got {gen.Health / gen.HealthMax:0.00})", Mathf.Abs(gen.Health - gen.HealthMax * 0.15f) < 0.01f);

            gen.DebugStage("fire");
            T.Check("fire stage: on fire, not yet a wreck", gen.OnFire && !gen.IsWreck);

            gen.DebugStage("wreck");
            T.Check("wreck stage: a burning husk", gen.IsWreck && gen.WreckOnFire);
            bool portsRetired = true;
            foreach (var p in gen.Ports) if (p.Usable || p.Visible) portsRetired = false;
            T.Check("wreck: port cubes retired (invisible, unusable)", portsRetired);
            yield return Ticks(2);
            T.Check("generator husk stays in the tree (salvage target)", GodotObject.IsInstanceValid(gen));
        }
    }

    // The UG_WIREARROWS state (the geometry/colour stays a visual check): every port carries an in/out arrow that
    // is hidden until the wire tool asks for it via SetArrowState.
    public class DeployPortArrows : GameTest
    {
        public override string Name => "deploy.port_arrows";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-2f, 0f, 0f), 0f);
            var spot = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(2f, 0f, 0f), 0f);
            yield return Ticks(1);
            T.Check("generator has 1 port, spotlight has 2", gen.Ports.Count == 1 && spot.Ports.Count == 2);
            bool allHidden = true, allShown = true;
            foreach (var d in new[] { gen, spot })
                foreach (var p in d.Ports) if (p.DebugArrowVisible) allHidden = false;
            T.Check("arrows hidden by default", allHidden);
            foreach (var d in new[] { gen, spot })
                foreach (var p in d.Ports) p.SetArrowState(true, true);
            foreach (var d in new[] { gen, spot })
                foreach (var p in d.Ports) if (!p.DebugArrowVisible) allShown = false;
            T.Check("arrows shown once the wire tool asks", allShown);
            foreach (var p in gen.Ports) p.SetArrowState(false, true);
            T.Check("arrows hide again", !gen.Ports[0].DebugArrowVisible);
        }
    }

    // The [DEPLOYPROBE] scripted aim check from --deploytest: the placement raycast + wall/overlap logic that the
    // interactive hold->aim path can't headless-test. Open ground = valid; the sky = invalid; occupied ground = invalid.
    public class DeployPlacerAim : GameTest
    {
        public override string Name => "deploy.placer_aim";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            cam.Position = new Vector3(8f, 3f, -6f);
            cam.LookAt(new Vector3(8f, 0f, -6f), Vector3.Back);   // straight down at open ground
            var placer = new DeployablePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.Generator);
            yield return Ticks(2);   // let the ground collider register with the physics space

            T.Check("open ground computes a VALID placement", placer.Aim(cam));
            T.Check($"hit point on the ground under the camera (got {placer.Point})",
                Mathf.Abs(placer.Point.Y) < 0.05f && Mathf.Abs(placer.Point.X - 8f) < 0.5f && Mathf.Abs(placer.Point.Z + 6f) < 0.5f);

            cam.LookAt(cam.Position + Vector3.Up * 5f, Vector3.Forward);   // aim at nothing within range
            T.Check("aiming at the sky is INVALID", !placer.Aim(cam));

            // a tall box BESIDE the aim point: the down-ray still hits ground at (8,0,-6), but the clearance sphere
            // (radius 0.5 at offset 0.75) intersects the box -> rejected (src OverlapSphere BLOCK_BARRICADE)
            var box = new StaticBody3D { CollisionLayer = 1 << 0 };
            box.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1f, 3f, 1f) } });
            World.AddChild(box);
            box.GlobalPosition = new Vector3(8.6f, 1.5f, -6f);
            cam.LookAt(new Vector3(8f, 0f, -6f), Vector3.Back);
            yield return Ticks(2);
            T.Check("obstacle in the clearance sphere is INVALID", !placer.Aim(cam));

            // a vertical face: the ray hits the box SIDE (normal.y < 0.01) -> wall -> rejected
            cam.Position = new Vector3(6.5f, 1.5f, -6f);
            cam.LookAt(new Vector3(8.6f, 1.5f, -6f), Vector3.Up);
            T.Check("aiming at a wall is INVALID", !placer.Aim(cam));
        }
    }
}
