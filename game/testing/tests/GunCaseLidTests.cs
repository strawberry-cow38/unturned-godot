using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The gun case opens and shuts (strawberry 2026-09-07: "split the gun case (one of the crate
    /// models) [the open one] into its two halves and make it open/close like a fridge with smart storage
    /// container").
    ///
    /// Crate_1 and Crate_4 are the same gun case in two finishes and were ALREADY smart containers -- the
    /// material-variants block added them on 2026-09-04. What they never had was a door, so the lid was welded
    /// into the body and the case stood open forever, whatever the inventory did.
    ///
    /// The split is checked from BOTH sides, because each half alone has a passing failure mode. A body that
    /// still contains the lid gives a case with two lids the moment the leaf spawns; a leaf that is missing
    /// gives a case with none, and a shelf with no doors reports exactly the same `HasDoors == false` as a
    /// prop that was never meant to have one. So: the body must have LOST the lid's volume, the leaf must have
    /// it, and the shelf must actually swing.
    ///
    /// THE LOD IS CHECKED TOO, and it is the check most likely to be the one that ever fires. A leaf split out
    /// of the body but left welded into <c>_lod1.obj</c> draws TWICE at range -- the door swinging open through
    /// a lid that is still shut -- and nothing near the prop looks wrong, so it is invisible in exactly the
    /// place anyone would test it. Crate_1 has a real lods.txt line; Crate_4 ships no LOD at all, and that
    /// asymmetry is asserted rather than assumed so a later re-rip that adds one is caught.
    ///
    /// The fold is the strongest check here and it costs nothing: the closed lid must land at z 0.200..0.400
    /// on a tray of 0.000..0.200, and Crate_2 -- the CLOSED gun case, a separate prop neither the splitter nor
    /// the hinge derivation ever looked at -- is a box of exactly z 0.000..0.400. The two halves have to fold
    /// into a mesh that was shipped years ago by someone else.</summary>
    public sealed class GunCaseLidTests : GameTest
    {
        public override string Name => "props.gun_case_lid";
        public override double TimeoutSimSeconds => 60;

        const double Dt = 0.02;

        static Aabb? MeshBox(string file)
        {
            var m = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/" + file));
            return m == null ? (Aabb?)null : m.GetAabb();
        }

        public override IEnumerable<Step> Run()
        {
            // ---- (1) TWO HALVES ON DISK, and the body no longer carries the lid.
            foreach (var name in new[] { "Crate_1", "Crate_4" })
            {
                var body = MeshBox(name + ".obj");
                var leaf = MeshBox(name + "_door.obj");
                T.Check($"{name}.obj loads", body.HasValue);
                T.Check($"{name}_door.obj loads (the lid became its own mesh)", leaf.HasValue);
                if (!body.HasValue || !leaf.HasValue) continue;

                var b = body.Value; var l = leaf.Value;
                // The tray alone. The lid stood to z 0.917 while open, so a body that still reaches anywhere
                // near that has not been split -- this is the check that a re-run of the splitter must not undo.
                T.Check($"{name} body is the TRAY only (z max {b.Position.Z + b.Size.Z:0.000} <= 0.21)",
                        b.Position.Z + b.Size.Z <= 0.21f);
                T.Check($"{name} body keeps its full footprint (y span {b.Size.Y:0.000} ~ 0.75)",
                        Mathf.Abs(b.Size.Y - 0.75f) < 0.02f);

                // ---- THE FOLD. Authored CLOSED, because that is the pose ObjectDoor treats as swing 0.
                T.Check($"{name} lid is authored CLOSED, sitting on the tray (z {l.Position.Z:0.000}..{l.Position.Z + l.Size.Z:0.000} ~ 0.200..0.400)",
                        Mathf.Abs(l.Position.Z - 0.2f) < 0.01f && Mathf.Abs(l.Position.Z + l.Size.Z - 0.4f) < 0.01f);
                T.Check($"{name} lid covers the tray's depth (y span {l.Size.Y:0.000} ~ 0.75)",
                        Mathf.Abs(l.Size.Y - 0.75f) < 0.02f);
            }

            // ---- (2) IT FOLDS INTO THE PROP THAT SHIPPED CLOSED. Crate_2 is the closed gun case and nothing in
            // the split or the hinge solve was fitted to it, so agreeing with it is real corroboration.
            var closed = MeshBox("Crate_2.obj");
            var tray = MeshBox("Crate_1.obj");
            var lid = MeshBox("Crate_1_door.obj");
            if (closed.HasValue && tray.HasValue && lid.HasValue)
            {
                float foldedTop = Mathf.Max(tray.Value.Position.Z + tray.Value.Size.Z, lid.Value.Position.Z + lid.Value.Size.Z);
                float shippedTop = closed.Value.Position.Z + closed.Value.Size.Z;
                T.Check($"shut, the two halves stand as tall as the closed variant ({foldedTop:0.000} vs Crate_2 {shippedTop:0.000})",
                        Mathf.Abs(foldedTop - shippedTop) < 0.01f);
            }
            else T.Fail("Crate_2 (the closed gun case) did not load for the fold cross-check");

            // ---- (3) THE LOD LOST THE LID TOO. The failure this catches is only visible at range.
            var lod = MeshBox("Crate_1_lod1.obj");
            T.Check("Crate_1_lod1.obj loads", lod.HasValue);
            if (lod.HasValue)
                T.Check($"...and it is the tray only, so the lid cannot draw twice at range (z max {lod.Value.Position.Z + lod.Value.Size.Z:0.000} <= 0.21)",
                        lod.Value.Position.Z + lod.Value.Size.Z <= 0.21f);
            T.Check("Crate_4 ships no lod1 (so it needs no LOD split)",
                    !System.IO.File.Exists(ProjectSettings.GlobalizePath("res://content/objects/Crate_4_lod1.obj")));

            // ---- (4) THE CONTAINER ACTUALLY SWINGS IT, on the same path a fridge uses.
            Rigs.Ground(World);
            var shelf = new StoreShelf { MeshName = "Crate_1", MinItems = 0, MaxItems = 0, ShowItems = false, TableIndex = 8 };
            World.AddChild(shelf);
            shelf.GlobalPosition = new Vector3(2f, 0f, 0f);
            yield return Ticks(2);

            T.Check("the gun case container spawned a door leaf", shelf.HasDoors);
            if (!shelf.HasDoors) yield break;
            T.Check($"it starts SHUT ({shelf.DebugDoorSwing():0.000})", Mathf.IsZeroApprox(shelf.DebugDoorSwing()));

            // Opening the INVENTORY is what opens the lid -- not an F-toggle on the leaf, same as the fridge.
            shelf.SetDoorsOpen(true);
            float peak = 0f;
            for (int i = 0; i < 200; i++) { shelf.TickDoorsForTest(Dt); peak = Mathf.Max(peak, shelf.DebugDoorSwing()); }
            T.Check($"opening the container swings the lid fully up ({peak:0.000})", peak > 0.99f);

            // ---- THE DIRECTION, which every check above is blind to. The closed pose is identical whether the
            // hinge angle is +98.419 or -98.419; only one of them lifts the lid off the tray, the other sweeps
            // it down through the body. So read the OPEN pose off the live node and require it to be the pose
            // the prop was MODELLED in -- lid standing up and leaning back, y[-0.660,-0.352] z[0.145,0.917].
            // Split out, folded shut, swung open, back where the artist put it.
            var open = shelf.DebugDoorLeafAabb();
            float oy0 = open.Position.Y, oy1 = open.Position.Y + open.Size.Y;
            float oz0 = open.Position.Z, oz1 = open.Position.Z + open.Size.Z;
            T.Check($"open, the lid stands back up where it was modelled (y {oy0:0.000}..{oy1:0.000} ~ -0.660..-0.352)",
                    Mathf.Abs(oy0 + 0.660f) < 0.02f && Mathf.Abs(oy1 + 0.352f) < 0.02f);
            T.Check($"...and to the modelled height (z {oz0:0.000}..{oz1:0.000} ~ 0.145..0.917)",
                    Mathf.Abs(oz0 - 0.145f) < 0.02f && Mathf.Abs(oz1 - 0.917f) < 0.02f);
            T.Check($"...which is UP off the tray, not down through it (top {oz1:0.000} > tray top 0.200)", oz1 > 0.5f);

            shelf.SetDoorsOpen(false);
            for (int i = 0; i < 200; i++) shelf.TickDoorsForTest(Dt);
            T.Check($"closing it brings the lid back down ({shelf.DebugDoorSwing():0.000})",
                    Mathf.IsZeroApprox(shelf.DebugDoorSwing()));
        }
    }
}
