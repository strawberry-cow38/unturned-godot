using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The window barricade (master 2026-08-31): a deployable that snaps INTO a building-editor window opening, one per
    // inside/outside face, placeable ONLY when the reticle is on a window. A window HOLE has no collider, so the placer
    // UV-projects the camera ray onto live WallSurface nodes ("walls" group) instead of raycasting. This drives that
    // real path: front/back face detection, the one-per-face slot gate, and that a floor-pinned DOOR opening is rejected.
    public sealed class WindowBarricadeTests : GameTest
    {
        public override string Name => "barricade.window_snap";
        public override double TimeoutSimSeconds => 30;

        static Camera3D CamLookingAt(Node parent, Vector3 pos, Vector3 target)
        {
            var cam = new Camera3D();
            parent.AddChild(cam);          // AddChild BEFORE LookAt (LookAt needs the node in the tree)
            cam.GlobalPosition = pos;
            cam.LookAt(target, Vector3.Up);
            return cam;
        }

        public override IEnumerable<Step> Run()
        {
            var wall = new WallSurface { Length = 6f, Height = 3f, Thickness = 0.5f };
            wall.Openings.Add(new UnturnedSim.WallOpening(0.5f, 0f, 1.5f, 2.5f));    // opening 0: a DOOR -- floor-pinned (V=0), NOT a window
            wall.Openings.Add(new UnturnedSim.WallOpening(2.5f, 1.0f, 1.5f, 1.5f));  // opening 1: a WINDOW -- sill at V=1.0
            World.AddChild(wall);
            yield return Ticks(2);   // _Ready -> Rebuild + AddToGroup("walls")

            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.WindowBarricade);
            Vector3 winCentre = wall.UVToWorld(2.5f + 0.75f, 1.0f + 0.75f);   // opening 1 centre in world space

            // aim at the WINDOW from the +Z (front) face
            var camFront = CamLookingAt(World, winCentre + Vector3.Back * 3f, winCentre);   // Vector3.Back = +Z
            yield return Ticks(1);
            bool v1 = placer.Aim(camFront);
            T.Check($"snaps to the window from the front (opening {placer.SnappedOpening}, valid={v1})", v1 && placer.SnappedOpening == 1);
            T.Check($"...on the +Z (front) face (face={placer.SnappedFace})", placer.SnappedFace == 1);

            // place it on the front face
            var b1 = Barricade.PlaceInWindow(wall, 1, 1, DeployableDef.WindowBarricade);
            yield return Ticks(1);
            T.Check("the barricade spawned as a child of the wall", b1 != null && b1.GetParent() == wall);
            T.Check("...and is stamped with its slot (opening 1, +Z)", b1 != null && b1.HasMeta("ug_wb_opening") && b1.GetMeta("ug_wb_opening").AsInt32() == 1);

            // the front slot is now TAKEN -> aiming there again is INVALID
            yield return Ticks(1);
            bool v2 = placer.Aim(camFront);
            T.Check("re-aiming the same (front) face is now INVALID -- slot filled", !v2);

            // but the BACK (inside) face is still free
            var camBack = CamLookingAt(World, winCentre + Vector3.Forward * 3f, winCentre);   // Vector3.Forward = -Z
            yield return Ticks(1);
            bool v3 = placer.Aim(camBack);
            T.Check($"the opposite (inside) face is still placeable (valid={v3}, face={placer.SnappedFace})", v3 && placer.SnappedFace == -1);

            // aiming at the DOORWAY (opening 0, floor-pinned + empty) is now ALSO barricadable (master 2026-09-01: doors too)
            Vector3 doorCentre = wall.UVToWorld(0.5f + 0.75f, 1.25f);
            var camDoor = CamLookingAt(World, doorCentre + Vector3.Back * 3f, doorCentre);
            yield return Ticks(1);
            bool v4 = placer.Aim(camDoor);
            T.Check($"an empty doorway is barricadable too, not just windows (opening {placer.SnappedOpening}, valid={v4})", v4 && placer.SnappedOpening == 0);

            placer.QueueFree();
            if (b1 != null && GodotObject.IsInstanceValid(b1)) b1.QueueFree();
        }
    }

    // The BAKED path: a window barricade snapping to a WindowOpeningMarker. A baked building has NO WallSurface at
    // runtime, so the placer projects onto markers (loaded from the opening sidecar into the "window_openings" group).
    // Same front/back face + one-per-face slot behaviour as the live-wall path.
    public sealed class WindowBarricadeBakedTests : GameTest
    {
        public override string Name => "barricade.window_snap_baked";
        public override double TimeoutSimSeconds => 30;

        static Camera3D CamLookingAt(Node parent, Vector3 pos, Vector3 target)
        {
            var cam = new Camera3D();
            parent.AddChild(cam);
            cam.GlobalPosition = pos;
            cam.LookAt(target, Vector3.Up);
            return cam;
        }

        public override IEnumerable<Step> Run()
        {
            var prop = new Node3D();   // stand-in for a placed baked-building prop root
            World.AddChild(prop);
            var marker = new WindowOpeningMarker { HalfWidth = 0.75f, HalfHeight = 0.75f, HalfThickness = 0.25f };
            prop.AddChild(marker);     // identity basis: +Z normal, +X width, +Y height
            marker.Position = new Vector3(0f, 1.75f, 0f);
            yield return Ticks(2);     // _Ready -> AddToGroup("window_openings")

            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.WindowBarricade);
            Vector3 centre = marker.GlobalPosition;

            var camFront = CamLookingAt(World, centre + Vector3.Back * 3f, centre);   // +Z side
            yield return Ticks(1);
            bool v1 = placer.Aim(camFront);
            T.Check($"snaps to the BAKED marker, not a wall (valid={v1}, marker={placer.SnappedMarker != null}, wall={placer.SnappedWall != null})", v1 && placer.SnappedMarker == marker && placer.SnappedWall == null);
            T.Check($"...on the +Z face ({placer.SnappedFace})", placer.SnappedFace == 1);

            var b1 = Barricade.PlaceInWindowMarker(marker, 1, placer.Point, placer.Yaw, placer.WindowScale, DeployableDef.WindowBarricade);
            yield return Ticks(1);
            T.Check("the barricade spawned under the marker's prop root", b1 != null && b1.GetParent() == prop);

            yield return Ticks(1);
            bool v2 = placer.Aim(camFront);
            T.Check("re-aiming the same face is now INVALID -- baked slot filled", !v2);

            var camBack = CamLookingAt(World, centre + Vector3.Forward * 3f, centre);   // -Z side
            yield return Ticks(1);
            bool v3 = placer.Aim(camBack);
            T.Check($"the opposite face is still placeable ({placer.SnappedFace})", v3 && placer.SnappedFace == -1);

            placer.QueueFree(); prop.QueueFree();
        }
    }

    // Hold-F removal (master): a window barricade is a Deployable, so the look-at ray (world layer) finds it and hold-F
    // picks it up like any deployable -- inherited, no new code. This pins the two things that could still bite: the
    // look-at ray actually RESOLVES the placed panel to a Deployable, and removing it FREES the slot (re-boardable).
    public sealed class WindowBarricadeRemoveTests : GameTest
    {
        public override string Name => "barricade.window_remove";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var wall = new WallSurface { Length = 6f, Height = 3f, Thickness = 0.5f };
            wall.Openings.Add(new UnturnedSim.WallOpening(2.5f, 1.0f, 1.5f, 1.5f));   // a window
            World.AddChild(wall);
            yield return Ticks(2);

            var b1 = Barricade.PlaceInWindow(wall, 0, 1, DeployableDef.WindowBarricade);
            yield return Ticks(2);
            T.Check("the +Z slot is filled after placing", BarricadePlacer.SlotFilled(wall, 0, 1));

            // the look-at ray that drives F (world layer) must resolve the panel to a Deployable -> hold-F target
            Vector3 bpos = b1.GlobalPosition;
            var rq = PhysicsRayQueryParameters3D.Create(bpos + Vector3.Back * 2f, bpos + Vector3.Forward * 0.5f);
            rq.CollisionMask = 1u << 0;
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(rq);
            T.Check("the look-at ray resolves the panel to a Deployable (so F picks it up)", hit.Count > 0 && hit["collider"].As<GodotObject>() is Deployable);

            b1.Pickup();   // exactly what hold-F triggers (PickupDeployable -> Deployable.Pickup)
            yield return Ticks(2);
            T.Check("...and after removal the +Z slot is FREE again (re-boardable)", !BarricadePlacer.SlotFilled(wall, 0, 1));
        }
    }

    // The three STYLES (master 2026-09-01): wooden planks / metal bars / metal plate, each a distinct procedural
    // mesh built to the opening, with HP tiered off its type. Pins the tier order + registration (so a fat-fingered
    // HP or a missing ById case is caught) and that every style actually places -- on a WIDE window, which the old
    // unit-box node-scale would have sheared/flattened but the built-to-size mesh handles.
    public sealed class WindowBarricadeStylesTests : GameTest
    {
        public override string Name => "barricade.window_styles";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var planks = DeployableDef.WindowBarricade; var bars = DeployableDef.WindowBars; var plate = DeployableDef.WindowPlate;
            T.Check($"planks = wooden, HP {planks.Health} (flimsiest)", planks.WindowStyle == WindowBarricadeStyle.Planks && Mathf.IsEqualApprox(planks.Health, 150f));
            T.Check($"bars = metal grille, HP {bars.Health} (mid)", bars.WindowStyle == WindowBarricadeStyle.Bars && Mathf.IsEqualApprox(bars.Health, 300f));
            T.Check($"plate = solid metal, HP {plate.Health} (tankiest)", plate.WindowStyle == WindowBarricadeStyle.Plate && Mathf.IsEqualApprox(plate.Health, 500f));
            T.Check("HP strictly tiers off type: planks < bars < plate", planks.Health < bars.Health && bars.Health < plate.Health);
            T.Check("all three resolve via ById (equip/deploy can reach them)", DeployableDef.ById(9122) == planks && DeployableDef.ById(9123) == bars && DeployableDef.ById(9124) == plate);

            foreach (var def in new[] { planks, bars, plate })
            {
                var wall = new WallSurface { Length = 4f, Height = 3f, Thickness = 0.5f };
                wall.Openings.Add(new UnturnedSim.WallOpening(1.0f, 1.0f, 2.0f, 1.4f));   // a WIDE (2.0 x 1.4) window -> a non-square fit
                World.AddChild(wall);
                yield return Ticks(2);
                var b = Barricade.PlaceInWindow(wall, 0, 1, def);
                yield return Ticks(1);
                T.Check($"{def.Name} places into the window (mesh built + parented + a barricade)", b != null && b.GetParent() == wall && b.IsInGroup("barricades"));
                wall.QueueFree();
                yield return Ticks(1);
            }
        }
    }

    // Barricading DOORS (master 2026-09-01): the barricade now snaps to doored openings too, and a door with a
    // barricade on EITHER face is "barricaded" -> PlayerController blocks its open + reddens the outline. This pins
    // the runtime facts behind that: the placer accepts a doored opening, and the door->opening link
    // (WallSurface.OpeningIndexForDoorHost) + SlotFilled together identify a barricaded door (what ObjectDoorBarricaded uses).
    public sealed class WindowBarricadeDoorTests : GameTest
    {
        public override string Name => "barricade.door_barricade";
        public override double TimeoutSimSeconds => 30;

        static ObjectDoor FindDoor(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is ObjectDoor d) return d;
                var r = FindDoor(c);
                if (r != null) return r;
            }
            return null;
        }

        static Camera3D CamLookingAt(Node parent, Vector3 pos, Vector3 target)
        {
            var cam = new Camera3D();
            parent.AddChild(cam);
            cam.GlobalPosition = pos;
            cam.LookAt(target, Vector3.Up);
            return cam;
        }

        public override IEnumerable<Step> Run()
        {
            float dh = UnturnedSim.WallOpenings.DoorHeight;
            var wall = new WallSurface { Length = 8f, Height = dh, Thickness = 0.5f };
            wall.Openings.Add(new UnturnedSim.WallOpening(1.0f, 1.0f, 1.5f, 1.5f));                                  // opening 0: a window
            wall.Openings.Add(new UnturnedSim.WallOpening(4.0f, 0f, 2.5f, dh - 0.5f) { DoorProp = "Door_Pine" });   // opening 1: a DOORED doorway
            World.AddChild(wall);
            yield return Ticks(3);   // _Ready -> Rebuild + RebuildDoors (spawns the door) + AddToGroup("walls")

            // 1) the placer now SNAPS to the doored opening (filter relaxed -- doors are barricadable, not just windows)
            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.WindowBarricade);
            Vector3 dCentre = wall.UVToWorld(4.0f + 1.25f, (dh - 0.5f) * 0.5f);
            var cam = CamLookingAt(World, dCentre + Vector3.Back * 3f, dCentre);
            yield return Ticks(1);
            bool vDoor = placer.Aim(cam);
            T.Check($"the placer snaps to the DOORED opening (valid={vDoor}, opening={placer.SnappedOpening})", vDoor && placer.SnappedOpening == 1);

            // 2) place a barricade on the +Z face of the doored opening; SlotFilled sees it on that face only
            var b = Barricade.PlaceInWindow(wall, 1, 1, DeployableDef.WindowBarricade);
            yield return Ticks(1);
            T.Check("a barricade sits on the doored opening's +Z face", b != null && BarricadePlacer.SlotFilled(wall, 1, 1));
            T.Check("...and the -Z (inside) face is still free", !BarricadePlacer.SlotFilled(wall, 1, -1));

            // 3) the door->opening link: the spawned door maps back to opening 1 (what ObjectDoorBarricaded resolves)
            var od = FindDoor(wall);
            T.Check("a functional door spawned in the doored opening", od != null);
            if (od != null && od.GetParent() is Node3D host)
                T.Check($"the door host maps back to its opening index ({wall.OpeningIndexForDoorHost(host)} == 1)", wall.OpeningIndexForDoorHost(host) == 1);

            // 4) tearing the barricade off frees the door again (re-openable)
            if (b != null) { b.Pickup(); yield return Ticks(2); }
            T.Check("removing the barricade frees the door (+Z slot clear)", !BarricadePlacer.SlotFilled(wall, 1, 1));

            placer.QueueFree();
        }
    }
}
