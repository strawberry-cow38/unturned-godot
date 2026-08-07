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
            T.Check("floor: mount basis == StandBasis (parity with the ground path)",
                ColsMatch(BarricadePlacer.MountBasis(BarricadeMount.Floor, placer.Normal, placer.Yaw), DeployableDef.StandBasis(placer.Yaw)));
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

        static bool ColsMatch(Basis a, Basis b) =>
            (a.Column0 - b.Column0).Length() < 1e-3f && (a.Column1 - b.Column1).Length() < 1e-3f && (a.Column2 - b.Column2).Length() < 1e-3f;
    }

    // AlignUpTo (the Sticky flush rotation) + YawFacing (the Wall facing yaw) are the two orientation primitives.
    public class BarricadeOrientationPrimitives : GameTest
    {
        public override string Name => "barricade.orientation_primitives";
        public override IEnumerable<Step> Run()
        {
            foreach (var n in new[] { Vector3.Up, Vector3.Down, Vector3.Right, Vector3.Forward, new Vector3(1f, 1f, 0f).Normalized() })
                T.Check($"AlignUpTo maps up onto {n}", BarricadePlacer.AlignUpTo(n).Column1.Dot(n) > 0.999f);
            T.Check("AlignUpTo floor case is exactly identity", BarricadePlacer.AlignUpTo(Vector3.Up) == Basis.Identity);
            // a StandBasis yawed by YawFacing(n) must FACE out along n (its facing == n) for any horizontal n.
            foreach (var n in new[] { Vector3.Right, Vector3.Left, Vector3.Forward, Vector3.Back, new Vector3(1f, 0f, 1f).Normalized() })
                T.Check($"YawFacing makes the front face {n}",
                    BarricadeAxes.Facing(DeployableDef.StandBasis(BarricadePlacer.YawFacing(n))).Dot(n) > 0.999f);
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

    // RepairTool packages the blowtorch: heal a hurt barricade (reporting the REAL HP restored, capped), and the piece
    // the deployable path lacked — salvage a LIVE barricade to reclaim its own item (vs a burning wreck, too hot).
    public class BarricadeRepairSalvage : GameTest
    {
        public override string Name => "barricade.repair_salvage";
        public override IEnumerable<Step> Run()
        {
            var n = new Vector3(-1f, 0f, 0f);
            var d = Barricade.PlaceOnSurface(World, DeployableDef.Generator, new Vector3(0f, 2f, 0f), n, BarricadePlacer.YawFacing(n), BarricadeMount.Wall);
            yield return Ticks(1);

            // REPAIR: hurt it (no fire at 60%), then the blowtorch heals toward max and REPORTS the real amount.
            d.TakeDamage(d.HealthMax * 0.4f);
            T.Check($"hurt but not on fire / a wreck (hp {d.Health / d.HealthMax:0.00})", d.Hurt && !d.IsWreck);
            float restored = RepairTool.Repair(d, 0.1f);
            T.Check($"repair reports real HP restored (got {restored:0.00}, expect ~{RepairTool.RepairRate * 0.1f:0.00})",
                restored > 0f && restored <= RepairTool.RepairRate * 0.1f + 0.01f);
            for (int i = 0; i < 200; i++) RepairTool.Repair(d, 0.1f);   // plenty -> clamps at max
            T.Check($"repair caps at max (got {d.Health:0.0}/{d.HealthMax:0.0})", Mathf.Abs(d.Health - d.HealthMax) < 0.01f);
            T.Check("a full-HP barricade returns 0 restored (not repairable)", RepairTool.Repair(d, 0.1f) <= 0f);
            // near max, the reported amount is the REAL deficit, never the full rate (don't over-bill).
            d.Health = d.HealthMax - 1f;
            float capped = RepairTool.Repair(d, 1f);   // 30 HP of rate available, but only 1 HP of deficit
            T.Check($"repair caps the reported amount to the real deficit (got {capped:0.00})", capped <= 1.01f && Mathf.Abs(d.Health - d.HealthMax) < 0.01f);

            // LIVE SALVAGE: hold the tool; before SalvageTime -> InProgress; crossing it reclaims the item + removes.
            float held = 0f;
            var s1 = RepairTool.Tick(d, ref held, RepairTool.SalvageTime * 0.5f, out _);
            T.Check("mid-hold salvage is IN PROGRESS", s1 == RepairTool.SalvageState.InProgress);
            var s2 = RepairTool.Tick(d, ref held, RepairTool.SalvageTime, out var refund);
            T.Check("completing the hold salvages", s2 == RepairTool.SalvageState.Done);
            T.Check($"live salvage reclaims the barricade item (got id {refund.ItemId} x{refund.Count})",
                refund.ItemId == DeployableDef.Generator.Id && refund.Count == 1);
            yield return Ticks(2);
            T.Check("salvaged barricade is removed from the world", !GodotObject.IsInstanceValid(d));

            // WRECK gating: a burning husk is too hot to salvage and resets the hold (src "Too hot to salvage").
            var w = Barricade.PlaceOnSurface(World, DeployableDef.Generator, new Vector3(6f, 2f, 0f), n, BarricadePlacer.YawFacing(n), BarricadeMount.Wall);
            yield return Ticks(1);
            w.DebugStage("wreck");
            T.Check("wreck is a burning husk", w.IsWreck && w.WreckOnFire);
            float held2 = 0f;
            var sw = RepairTool.Tick(w, ref held2, 1f, out _);
            T.Check("a burning wreck is TOO HOT to salvage (hold reset)", sw == RepairTool.SalvageState.TooHot && held2 == 0f);
        }
    }
}
