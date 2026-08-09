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
            T.Check("generator has 3 ports (output + 2 remote-triggers), spotlight has 2", gen.Ports.Count == 3 && spot.Ports.Count == 2);
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

    // Regression (master 2026-07-19): a splitter must place on open ground. Its clearance-sphere Offset was set
    // SMALLER than its Radius, so the sphere dipped into flat terrain and every spot read blocked/red -- unplaceable.
    public class DeploySplitterPlacement : GameTest
    {
        public override string Name => "deploy.splitter_placement";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            cam.Position = new Vector3(8f, 3f, -6f);
            cam.LookAt(new Vector3(8f, 0f, -6f), Vector3.Back);   // straight down at open ground
            var placer = new DeployablePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.Splitter2);
            yield return Ticks(2);   // let the ground collider register with the physics space
            T.Check("2-way splitter is VALID on open ground (clearance sphere clears terrain)", placer.Aim(cam));
            placer.SetDef(DeployableDef.Splitter4);
            yield return Ticks(1);
            T.Check("4-way splitter is VALID on open ground too", placer.Aim(cam));
        }
    }

    // Fuel + HP ride the item through pickup -> re-place (master): a damaged, half-fuelled generator picked up and
    // planted again comes back with the same HP + fuel, not reset to full. A fresh item (no state) still spawns full.
    public class DeployStatePersists : GameTest
    {
        public override string Name => "deploy.state_persists";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, Vector3.Zero, 0f);
            yield return Ticks(1);
            gen.Health = gen.HealthMax * 0.5f;   // damage to 50%
            gen.Fuel = gen.FuelMax * 0.3f;       // burn down to 30% fuel
            var item = SDG.Unturned.Assets.makeLoot(DeployableDef.Generator.Id);   // mimic PickupDeployable stamping the item
            item.quality = (byte)Mathf.RoundToInt(gen.Health / gen.HealthMax * 100f);
            item.fuelLevel = gen.Fuel;
            var gen2 = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(5f, 0f, 0f), 0f, item);   // re-place from that item
            yield return Ticks(1);
            T.Check($"HP restored to ~50% (got {gen2.Health / gen2.HealthMax:0.00})", Mathf.Abs(gen2.Health / gen2.HealthMax - 0.5f) < 0.02f);
            T.Check($"fuel restored to 30% (got {gen2.Fuel / gen2.FuelMax:0.00})", Mathf.Abs(gen2.Fuel / gen2.FuelMax - 0.3f) < 0.01f);
            var gen3 = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(10f, 0f, 0f), 0f);   // a fresh item (no state) -> full
            T.Check("a fresh generator still spawns full HP + fuel", Mathf.Abs(gen3.Health - gen3.HealthMax) < 0.01f && Mathf.Abs(gen3.Fuel - gen3.FuelMax) < 0.01f);
        }
    }

    // Bug 2 (master 2026-07-19): a generator that runs OUT of fuel must shut off (cooldown) and STAY off -- refuelling
    // alone must NOT auto-resume it; it needs a manual [F] restart. Then a poured can (deposit) refuels it.
    public class DeployGeneratorRunsDry : GameTest
    {
        public override string Name => "deploy.generator_runs_dry";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, Vector3.Zero, 0f);
            yield return Ticks(1);
            gen.TogglePower();      // switch it ON (spawns full-fuelled)
            yield return Ticks(1);
            T.Check("running while it has fuel", gen.IsPowered);
            // Drain to a sip and let it run dry -- WAIT ON THE OUTCOME, not a fixed tick count. Fuel burns in _Process
            // (per frame, scaled by delta), so how many ticks a fixed sip takes to empty depends on the box's frame rate:
            // a faster box empties it in fewer ticks. Ticks(120) baked that assumption in and flaked both ways -- a fast
            // box emptied 0.004 before the "still running" check even ran; a slow one never reached dry. Until() is safe.
            gen.Fuel = 0.002f;
            yield return Until(() => !gen.IsPowered, maxSimSeconds: 10);
            T.Check($"ran DRY -> stops producing (fuel {gen.Fuel:0.000})", gen.Fuel <= 0f && !gen.IsPowered);

            gen.Fuel = gen.FuelMax;    // refuel (as a poured can would) WITHOUT touching the switch
            yield return Ticks(2);
            T.Check("refuelled but does NOT auto-restart (needs manual [F])", !gen.IsPowered);

            if (gen.CanTogglePower) gen.TogglePower();   // manual restart
            yield return Ticks(2);
            T.Check("manual restart brings it back online", gen.IsPowered);
        }
    }

    // A3 (SP/MP-unify) regression: a materialized/direct grid-power SOURCE (Circuit_0 breaker box) must carry
    // its OWN "gridpower"-tagged interaction collider so the look-ray can focus + wire it. The world mesh's
    // collider is never tagged (only the dead SpawnEditorGridPower path did), so pre-fix the consuming SP boxes
    // rendered but were un-focusable/un-wireable = "grid power doesn't exist" (strawberry). Resolves EXACTLY as
    // PlayerController.cs:176 does: a child StaticBody3D on the small-prop look layer (1<<6) whose "gridpower"
    // meta returns the source. Teeth: pre-fix Materialize/Attach make no such collider -> both checks fail.
    public class GridSourceInteraction : GameTest
    {
        public override string Name => "deploy.grid_source_interaction";

        static bool HasTaggedCollider(GridPowerSource g)
        {
            foreach (var c in g.GetChildren())
                if (c is StaticBody3D body && (body.CollisionLayer & (1u << 6)) != 0
                    && body.HasMeta("gridpower") && body.GetMeta("gridpower").As<GridPowerSource>() == g)
                    return true;
            return false;
        }

        // The look-ray resolution PlayerController.cs:167-176 runs: cast on the small-prop look layer (1<<6) and
        // resolve the hit collider's "gridpower" meta. Verifies the collider is actually WHERE the box is (right
        // size/placement), not merely that a tagged node exists somewhere.
        GridPowerSource LookRayHit(Vector3 from, Vector3 boxPos)
        {
            var q = new PhysicsRayQueryParameters3D { From = from, To = boxPos + Vector3.Up * 0.95f, CollisionMask = 1u << 6 };
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return null;
            var col = hit["collider"].As<GodotObject>();
            return col is Node n && n.HasMeta("gridpower") ? n.GetMeta("gridpower").As<GridPowerSource>() : null;
        }

        public override IEnumerable<Step> Run()
        {
            // consume path: DeployableReplicaView.Materialize (what the SP-consume default + a joined client run)
            var matPos = new Vector3(-3f, 0f, 0f);
            var mat = GridPowerSource.Materialize(World, matPos, 0f, GridPowerSource.DefaultWatts, netId: 42);
            yield return Ticks(1);
            T.Check("consume (Materialize): grid box has its own gridpower-tagged look collider on layer 1<<6", HasTaggedCollider(mat));
            T.Check("consume (Materialize): a forward look-ray at the box RESOLVES the grid source (right size/placement)",
                    LookRayHit(matPos + new Vector3(0f, 1f, 4f), matPos) == mat);

            // pure-direct SP path: SpawnFixturesDirect does Attach + AddInteractionCollider
            var dirPos = new Vector3(3f, 0f, 0f);
            var dir = GridPowerSource.Attach(World, dirPos, Basis.Identity, GridPowerSource.PortLocal);
            dir.AddInteractionCollider();
            yield return Ticks(1);
            T.Check("direct (Attach + AddInteractionCollider): grid box has its own gridpower-tagged look collider", HasTaggedCollider(dir));
            T.Check("direct (Attach): a forward look-ray at the box RESOLVES the grid source",
                    LookRayHit(dirPos + new Vector3(0f, 1f, 4f), dirPos) == dir);
        }
    }

    // strawberry_cow 2026-08-09: "make the grid snap only on the axis we're moving the prop on, not global."
    // Pure maths, so this needs no camera, no viewport and no drag in flight.
    public class EditorSnapIsAxisRelative : GameTest
    {
        public override string Name => "editor.grid_snap_follows_the_drag_axis";

        public override IEnumerable<Step> Run()
        {
            // a prop sitting deliberately OFF the grid on every axis
            var start = new Vector3(2.3f, 5.7f, -1.4f);

            // dragged along X: X lands on the grid, Y and Z must not move at all. The old behaviour snapped
            // all three, so a nudge sideways also shifted the prop vertically -- on an axis you never touched.
            var alongX = EditorGizmo.SnapTranslate(start, Vector3.Right, 1.4f, start + Vector3.Right * 1.4f);
            T.Check($"X snaps to the grid ({alongX.X:0.00})", Mathf.Abs(alongX.X - Mathf.Round(alongX.X)) < 1e-3f);
            T.Check($"Y is untouched ({alongX.Y:0.00} vs {start.Y:0.00})", Mathf.Abs(alongX.Y - start.Y) < 1e-4f);
            T.Check($"Z is untouched ({alongX.Z:0.00} vs {start.Z:0.00})", Mathf.Abs(alongX.Z - start.Z) < 1e-4f);

            var alongY = EditorGizmo.SnapTranslate(start, Vector3.Up, 2.2f, start + Vector3.Up * 2.2f);
            T.Check($"dragging Y snaps only Y ({alongY.Y:0.00})",
                    Mathf.Abs(alongY.Y - Mathf.Round(alongY.Y)) < 1e-3f
                    && Mathf.Abs(alongY.X - start.X) < 1e-4f && Mathf.Abs(alongY.Z - start.Z) < 1e-4f);

            // a PLANE drag (two axes at once) snaps both of those and still leaves the third alone -- this
            // falls out of deciding per component, rather than needing its own case
            var diag = new Vector3(1f, 0f, 1f).Normalized();
            var onPlane = EditorGizmo.SnapTranslate(start, diag, 2f, start + diag * 2f);
            T.Check($"a plane drag snaps both its axes ({onPlane.X:0.00}, {onPlane.Z:0.00})",
                    Mathf.Abs(onPlane.X - Mathf.Round(onPlane.X)) < 1e-3f
                    && Mathf.Abs(onPlane.Z - Mathf.Round(onPlane.Z)) < 1e-3f);
            T.Check($"and leaves the axis it does not touch ({onPlane.Y:0.00})", Mathf.Abs(onPlane.Y - start.Y) < 1e-4f);

            // LOCAL space on a rotated prop: no axis is world-aligned, so snapping components would quantise
            // all three and be exactly the bug. Travel is quantised instead and the prop stays on its own axis.
            var tilted = new Vector3(0.5f, 0.6f, 0.62f).Normalized();
            var local = EditorGizmo.SnapTranslate(start, tilted, 2.4f, start + tilted * 2.4f);
            float travel = (local - start).Length();
            T.Check($"a rotated-axis drag quantises TRAVEL ({travel:0.00})", Mathf.Abs(travel - 2f) < 1e-3f);
            T.Check("and stays exactly on its own axis",
                    (local - start).Normalized().Dot(tilted) > 0.9999f);
            yield break;
        }
    }
}
