using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The deployable meshes are authored FLAT and stood up by DeployableDef.StandBasis with StandRotX = +90
    // (DeployableDef.cs:67,385): local -Z -> world +Y (visual up), local +Y -> +Z at yaw 0 (the "front"). So in the
    // stood-up basis, visual-up = -Column2 and facing = Column1.
    static class BarricadeAxes
    {
        public static Vector3 VisualUp(Basis b) => -b.Column2;   // where the mesh's top points, in world
        public static Vector3 Facing(Basis b) => b.Column1;      // where the mesh faces, in world
    }

    // BarricadePlacer accepts the surfaces the ground DeployablePlacer rejects (walls / ceilings — it bails on
    // normal.y < 0.01, DeployablePlacer.cs:61) and orients each of the three real mount families faithfully
    // (UseableBarricade's per-EBuild branches):
    //   Floor  — upward surfaces only, upright, free yaw           (mount basis == StandBasis, parity w/ ground path)
    //   Wall   — near-vertical only, UPRIGHT + yaw faces out       (visual-up == world-up, facing == wall normal)
    //   Sticky — any surface, lies fully flush                     (visual-up == the surface normal)
    public class BarricadeSurfacePlacement : GameTest
    {
        public override string Name => "barricade.surface_placement";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.Generator);

            // a vertical wall + an overhead slab, both in the "structures" group (where StructureManager parents pieces)
            var wall = new StaticBody3D { CollisionLayer = 1 << 0 };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1f, 4f, 4f) } });
            World.AddChild(wall);
            wall.GlobalPosition = new Vector3(10f, 2f, -6f);
            wall.AddToGroup("structures");
            var ceil = new StaticBody3D { CollisionLayer = 1 << 0 };
            ceil.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4f, 1f, 4f) } });
            World.AddChild(ceil);
            ceil.GlobalPosition = new Vector3(8f, 5f, -6f);
            ceil.AddToGroup("structures");
            yield return Ticks(2);   // let the colliders register with the physics space

            // WALL mode on the wall face: valid, stays upright, and yaws to face straight out of the wall.
            placer.Mount = BarricadeMount.Wall;
            cam.Position = new Vector3(8f, 2f, -6f);
            cam.LookAt(new Vector3(9.5f, 2f, -6f), Vector3.Up);
            T.Check("wall mode: a vertical structure face is VALID (the ground placer rejects it)", placer.Aim(cam));
            T.Check($"wall: normal is horizontal (got {placer.Normal})", Mathf.Abs(placer.Normal.Y) < 0.05f);
            var wb = BarricadePlacer.MountBasis(BarricadeMount.Wall, placer.Normal, placer.Yaw);
            T.Check("wall: barricade stays UPRIGHT (visual-up = world up)", BarricadeAxes.VisualUp(wb).Dot(Vector3.Up) > 0.99f);
            T.Check("wall: barricade FACES OUT of the wall (facing = wall normal)", BarricadeAxes.Facing(wb).Dot(placer.Normal) > 0.99f);
            // a wall-mount type refuses a floor (|normal.y| not < 0.1).
            cam.Position = new Vector3(8f, 3f, -6f);
            cam.LookAt(new Vector3(8f, 0f, -6f), Vector3.Back);
            T.Check("wall mode REJECTS a floor", !placer.Aim(cam));

            // FLOOR mode on the ground: valid, and orientation is pixel-identical to the old ground StandBasis.
            placer.Mount = BarricadeMount.Floor;
            T.Check("floor mode: open ground is VALID", placer.Aim(cam));
            T.Check("floor: barricade stands upright (visual-up = world up)",   // falsifiable: fails if Floor stopped standing the mesh up
                BarricadeAxes.VisualUp(BarricadePlacer.MountBasis(BarricadeMount.Floor, placer.Normal, placer.Yaw)).Dot(Vector3.Up) > 0.99f);
            cam.Position = new Vector3(8f, 2f, -6f);
            cam.LookAt(new Vector3(9.5f, 2f, -6f), Vector3.Up);
            T.Check("floor mode REJECTS a wall", !placer.Aim(cam));

            // STICKY mode on the ceiling underside: valid on anything, lies fully flush (visual-up follows the normal).
            placer.Mount = BarricadeMount.Sticky;
            cam.Position = new Vector3(8f, 2.5f, -6f);
            cam.LookAt(new Vector3(8f, 4.4f, -6f), Vector3.Forward);
            T.Check("sticky mode: an overhead face is VALID", placer.Aim(cam));
            T.Check($"sticky: normal points down (got {placer.Normal})", placer.Normal.Dot(Vector3.Down) > 0.9f);
            T.Check("sticky: lies FLUSH (visual-up follows the down normal)",
                BarricadeAxes.VisualUp(BarricadePlacer.MountBasis(BarricadeMount.Sticky, placer.Normal, placer.Yaw)).Dot(placer.Normal) > 0.999f);

            // sky: nothing within range -> invalid for any mount.
            cam.Position = new Vector3(20f, 3f, -6f);
            cam.LookAt(new Vector3(20f, 8f, -6f), Vector3.Forward);
            T.Check("sky: aiming at nothing is INVALID", !placer.Aim(cam));
        }
    }

    // AlignUpTo (the Sticky flush rotation) + YawFacing (the Wall facing yaw) are the two orientation primitives.
    public class BarricadeOrientationPrimitives : GameTest
    {
        public override string Name => "barricade.orientation_primitives";
        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);   // suspend once -- a no-yield coroutine completes inside the host's StartNext (was a masked NRE)
            foreach (var n in new[] { Vector3.Up, Vector3.Down, Vector3.Right, Vector3.Forward, new Vector3(1f, 1f, 0f).Normalized() })
                T.Check($"AlignUpTo maps up onto {n}", BarricadePlacer.AlignUpTo(n).Column1.Dot(n) > 0.999f);
            T.Check("AlignUpTo floor case is exactly identity", BarricadePlacer.AlignUpTo(Vector3.Up) == Basis.Identity);
            // a StandBasis yawed by YawFacing(n) must FACE out along n (its facing == n) for any horizontal n.
            foreach (var n in new[] { Vector3.Right, Vector3.Left, Vector3.Forward, Vector3.Back, new Vector3(1f, 0f, 1f).Normalized() })
                T.Check($"YawFacing makes the front face {n}",
                    BarricadeAxes.Facing(DeployableDef.StandBasis(BarricadePlacer.YawFacing(n))).Dot(n) > 0.999f);
            // Upright-authored defs (wind turbine) skip the flat stand-up: their authored up (local +Y) stays world-up.
            // Falsifiable: with the old MountBasis (StandBasis for everything) this is a horizontal facing -> ~0.
            T.Check("upright def stays vertical (not stood-up sideways)",
                BarricadePlacer.MountBasis(BarricadeMount.Floor, Vector3.Up, 25f, upright: true).Column1.Dot(Vector3.Up) > 0.99f);
            yield break;
        }
    }

    // Barricade.PlaceOnSurface reuses Deployable's whole lifecycle but re-seats it for the mount family: full HP from
    // the def, tagged both "barricades" and "deployables", and (Wall) stands upright facing out, hugging the wall with
    // its box collider rotated to match.
    public class BarricadeSpawnWall : GameTest
    {
        public override string Name => "barricade.spawn_wall";
        public override IEnumerable<Step> Run()
        {
            var point = new Vector3(5f, 2f, 0f);
            var n = new Vector3(-1f, 0f, 0f);                       // a wall facing -X
            float yaw = BarricadePlacer.YawFacing(n);              // face out of the wall
            var d = Barricade.PlaceOnSurface(World, DeployableDef.Generator, point, n, yaw, BarricadeMount.Wall);
            yield return Ticks(1);
            T.Check("barricade spawned with full def HP", Mathf.Abs(d.Health - DeployableDef.Generator.Health) < 0.01f && d.Health > 0f);
            T.Check("tagged as both a barricade and a deployable", d.IsInGroup("barricades") && d.IsInGroup("deployables"));
            T.Check("wall barricade stays upright (collider tilts with the node)", BarricadeAxes.VisualUp(d.Basis).Dot(Vector3.Up) > 0.99f);
            T.Check("wall barricade faces out of the wall", BarricadeAxes.Facing(d.Basis).Dot(n) > 0.99f);
            T.Check($"seated hugging the wall near the mount point (got {d.GlobalPosition})", (d.GlobalPosition - point).Length() < 0.6f);
        }
    }

    // The barricade def carries its own mount family, so PlaceOnSurface/SetDef default to it (no explicit mount arg).
    public class BarricadeDefMount : GameTest
    {
        public override string Name => "barricade.def_mount";
        public override IEnumerable<Step> Run()
        {
            T.Check("MetalBarricade is a Wall-mount barricade", DeployableDef.MetalBarricade.Mount == BarricadeMount.Wall);
            T.Check("a ground deployable defaults to Floor mount", DeployableDef.Generator.Mount == BarricadeMount.Floor);

            // PlaceOnSurface with NO explicit mount uses the def's mount -> a MetalBarricade on a wall is upright + facing out.
            var n = new Vector3(0f, 0f, 1f);
            float yaw = BarricadePlacer.YawFacing(n);
            var d = Barricade.PlaceOnSurface(World, DeployableDef.MetalBarricade, new Vector3(0f, 2f, 0f), n, yaw);   // no mount arg
            yield return Ticks(1);
            T.Check("def-mounted wall barricade is upright", BarricadeAxes.VisualUp(d.Basis).Dot(Vector3.Up) > 0.99f);
            T.Check("def-mounted wall barricade faces out", BarricadeAxes.Facing(d.Basis).Dot(n) > 0.99f);
            T.Check("spawned with the def HP", Mathf.Abs(d.Health - DeployableDef.MetalBarricade.Health) < 0.01f);

            // the placer adopts the mount family from the def too
            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.MetalBarricade);
            T.Check("SetDef adopts the def's mount family", placer.Mount == BarricadeMount.Wall);

            // the DeployablePlacer-compatible Freeze(point, yaw) overload makes the placer a drop-in (normal defaults up)
            placer.Freeze(new Vector3(3f, 0f, 3f), 90f);
            T.Check("Freeze(point, yaw) drop-in: valid + up normal + committed point/yaw",
                placer.Valid && placer.Normal.Dot(Vector3.Up) > 0.99f && (placer.Point - new Vector3(3f, 0f, 3f)).Length() < 0.001f && Mathf.Abs(placer.Yaw - 90f) < 0.001f);
        }
    }

    // BarricadePlayground drives the interactive --barricadeplay sandbox: its place/cycle logic (the input events wire
    // straight onto these methods). Raw input can't be driven headless, so we exercise the methods directly.
    public class BarricadePlaygroundLogic : GameTest
    {
        public override string Name => "barricade.playground";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            var wall = new StaticBody3D { CollisionLayer = 1 << 0 };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1f, 4f, 4f) } });
            World.AddChild(wall);
            wall.GlobalPosition = new Vector3(6f, 2f, 0f);
            wall.AddToGroup("structures");
            cam.Position = new Vector3(4f, 2f, 0f);
            cam.LookAt(new Vector3(5.5f, 2f, 0f), Vector3.Up);   // aim at the wall's -X face

            var pg = new BarricadePlayground();
            World.AddChild(pg);
            pg.Setup(cam, new[] { DeployableDef.MetalBarricade, DeployableDef.Generator });
            yield return Ticks(2);   // let the wall collider register with the physics space
            pg.Aim();                // headless has no render frames, so drive the aim directly (as _Process would live)

            T.Check("default def is the first (MetalBarricade), Wall mount from the def", pg.Current == DeployableDef.MetalBarricade && pg.Placer.Mount == BarricadeMount.Wall);
            T.Check("the ghost is on a valid wall surface", pg.Placer.Valid);
            var placed = pg.PlaceCurrent();
            yield return Ticks(1);
            T.Check("PlaceCurrent (LMB) drops a barricade on the wall", placed != null && GodotObject.IsInstanceValid(placed) && placed.IsInGroup("barricades"));

            pg.CycleDef(1);
            T.Check("cycling the def switches to the generator (Floor mount)", pg.Current == DeployableDef.Generator && pg.Placer.Mount == BarricadeMount.Floor);
            var m0 = pg.Placer.Mount;
            pg.CycleMount();
            T.Check("cycling the mount family changes it", pg.Placer.Mount != m0);
            float y0 = pg.Placer.YawOffset;
            pg.Rotate();
            T.Check("rotate bumps the yaw offset by 90", Mathf.Abs((pg.Placer.YawOffset - y0) - 90f) < 0.01f);
        }
    }

    // The CanAttach seam (tinyclaw's integration review): the structure hook and the no-stack default are BOTH gates,
    // not either/or, and the hook must ABSTAIN (true) off-structure rather than refuse. The old code exercised none of
    // this -- every other test leaves CanAttach null -- so this wires a MOCK hook to cover it.
    public class BarricadeAttachSeam : GameTest
    {
        public override string Name => "barricade.attach_seam";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            var wall = new StaticBody3D { CollisionLayer = 1 << 0 };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1f, 4f, 4f) } });
            World.AddChild(wall);
            wall.GlobalPosition = new Vector3(6f, 2f, 0f);
            wall.AddToGroup("structures");
            // a stand-in already-placed deployable (the no-stack target)
            var dep = new StaticBody3D { CollisionLayer = 1 << 0 };
            dep.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
            World.AddChild(dep);
            dep.GlobalPosition = new Vector3(-2f, 0.5f, 0f);
            dep.AddToGroup("deployables");
            yield return Ticks(2);

            var placer = new BarricadePlacer { Mount = BarricadeMount.Wall };
            World.AddChild(placer);
            placer.SetDef(DeployableDef.MetalBarricade);

            // aim at the structure wall: the hook AGREES -> valid (both gates pass); REFUSES -> invalid (gate has teeth).
            cam.Position = new Vector3(4f, 2f, 0f);
            cam.LookAt(new Vector3(5.5f, 2f, 0f), Vector3.Up);
            placer.CanAttach = (p, n, c) => true;
            T.Check("hook agrees on a structure wall -> valid (both gates pass)", placer.Aim(cam));
            placer.CanAttach = (p, n, c) => false;
            T.Check("hook refuses -> invalid (the structure gate has teeth)", !placer.Aim(cam));

            // the placer must hand the hook the ACTUAL hit collider (not a stand-in), or the structure gate can't tell
            // which piece it's judging -- the review caught this exact hole in the structure seam test.
            Node seen = null;
            placer.CanAttach = (p, n, c) => { seen = c; return true; };
            placer.Aim(cam);
            T.Check("placer hands the hook the real hit collider", seen == wall);

            // NO-STACK survives a wired hook: aim down at a deployable with a hook that AGREES -> still invalid.
            // (the old either/or bug let this through: the hook replaced DefaultAttachable, dropping the no-stack rule.)
            placer.Mount = BarricadeMount.Floor;
            placer.CanAttach = (p, n, c) => true;
            cam.Position = new Vector3(-2f, 3f, 0f);
            cam.LookAt(new Vector3(-2f, 0.5f, 0f), Vector3.Back);
            T.Check("no-stacking survives a wired hook: can't plant on another deployable", !placer.Aim(cam));

            // ABSTAIN off-structure: a hook returning true leaves open-ground placement to DefaultAttachable.
            placer.CanAttach = (p, n, c) => true;
            cam.Position = new Vector3(-8f, 3f, 3f);
            cam.LookAt(new Vector3(-8f, 0f, 3f), Vector3.Back);   // open ground, clear of the props
            T.Check("abstaining hook leaves open-ground placement valid", placer.Aim(cam));
        }
    }

    // The clearance sphere must skip the structure lattice (the mount surface): a barricade low on a wall, its sphere
    // dipping toward the adjacent floor slab, is still placeable. Was a ~0.73m dead zone at the base of every wall,
    // because the sphere excluded only the exact body the ray hit and counted the floor slab as an obstacle.
    public class BarricadeClearanceLattice : GameTest
    {
        public override string Name => "barricade.clearance_lattice";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            var wall = new StaticBody3D { CollisionLayer = 1 << 0 };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.4f, 4f, 4f) } });
            World.AddChild(wall);
            wall.GlobalPosition = new Vector3(6f, 2f, 0f);
            wall.AddToGroup("structures");
            var floor = new StaticBody3D { CollisionLayer = 1 << 0 };   // a floor slab meeting the wall at its base
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2f, 0.4f, 4f) } });
            World.AddChild(floor);
            floor.GlobalPosition = new Vector3(5f, 0f, 0f);
            floor.AddToGroup("structures");
            yield return Ticks(2);

            var placer = new BarricadePlacer { Mount = BarricadeMount.Wall };
            World.AddChild(placer);
            placer.SetDef(DeployableDef.MetalBarricade);
            cam.Position = new Vector3(4f, 0.6f, 0f);
            cam.LookAt(new Vector3(5.8f, 0.6f, 0f), Vector3.Up);   // aim LOW on the wall, so the clearance sphere reaches the floor slab
            T.Check("barricade low on a wall is VALID (the floor-slab lattice doesn't block clearance)", placer.Aim(cam));
        }
    }

    // A barricade must not go blue on a VEHICLE. Nothing parents it to the car (retail's dropPlantedBarricade
    // does), so it would sit in mid-air the moment the car moved -- the player loses the item and the game
    // never told them it would.
    public class BarricadeVehicleSurface : GameTest
    {
        public override string Name => "barricade.vehicle_surface";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.Generator);      // a Floor-mount deployable, the common case
            placer.Mount = BarricadeMount.Floor;

            // a flat surface at the same height, twice: one plain, one a "vehicle"
            var slab = new StaticBody3D { CollisionLayer = 1 << 0 };
            slab.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4f, 1f, 4f) } });
            World.AddChild(slab);
            slab.GlobalPosition = new Vector3(0f, 1f, 0f);

            var car = new StaticBody3D { CollisionLayer = 1 << 0 };
            car.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4f, 1f, 4f) } });
            World.AddChild(car);
            car.GlobalPosition = new Vector3(20f, 1f, 0f);
            car.AddToGroup("vehicles");
            yield return Ticks(3);

            // CONTROL: the identical plain slab must be placeable, or "refused" proves nothing
            cam.Position = new Vector3(0f, 4f, 0f);
            cam.LookAt(new Vector3(0f, 1.5f, 0f), Vector3.Forward);
            T.Check("control: an ordinary slab top is a VALID spot", placer.Aim(cam));

            cam.Position = new Vector3(20f, 4f, 0f);
            cam.LookAt(new Vector3(20f, 1.5f, 0f), Vector3.Forward);
            T.Check("but the identical surface on a VEHICLE is refused", !placer.Aim(cam));
        }
    }
}
