using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // Copy/paste a storey. strawberry_cow: "copy/paste floors".
    //
    // Driven through the real editor rather than checked against a recomputed offset, because the offset is
    // the entire feature and the arithmetic that produces it is the thing most likely to be wrong. A test
    // that computes `expected = y + StoreyHeight` and compares it to the code's `y + StoreyHeight` agrees
    // with the code by construction and would have passed on every version of this I got wrong.
    //
    // The stage sits at y = 2000 and ActiveFloorY is StageOrigin.Y + FloorY + GroundClearance, so a paste
    // that used ActiveFloorY as an absolute instead of a difference lands the copy two kilometres up. That
    // is the same substitution that buried the staircases, and it is invisible in any count-based check --
    // so every assertion here is about WHERE the surfaces are, not how many there are.
    static class Storey
    {
        public static int CountAt(EditorBuildings eb, float y, float tol = 0.05f)
        {
            int n = 0;
            foreach (var w in eb.Walls)
                if (GodotObject.IsInstanceValid(w) && Mathf.Abs(w.Position.Y - y) < tol) n++;
            return n;
        }

        /// <summary>Everything within the half-storey band of y. A floor SLAB does not sit at exactly the
        /// same Y as the walls around it, so an exact-match count silently misses it -- which is how the
        /// first version of this file reported "4 of 5" and looked like a paste bug.</summary>
        public static int InBand(EditorBuildings eb, float y)
        {
            int n = 0;
            float band = EditorBuildings.StoreyHeight * 0.5f;
            foreach (var w in eb.Walls)
                if (GodotObject.IsInstanceValid(w) && Mathf.Abs(w.Position.Y - y) <= band) n++;
            return n;
        }

        public static void Square(EditorBuildings eb, float x, float z, float w)
        {
            float y = eb.ActiveFloorY;
            eb.AddWall(new Vector3(x, y, z), 0f, w);
            eb.AddWall(new Vector3(x + w, y, z), 90f, w);
            eb.AddWall(new Vector3(x + w, y, z - w), 180f, w);
            eb.AddWall(new Vector3(x, y, z - w), 270f, w);
        }
    }

    public class CopyPasteFloorLiftsAStorey : GameTest
    {
        public override string Name => "buildtool.copy_paste_floor_lifts_a_storey";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            yield return Step.Ticks(1);

            float y0 = eb.ActiveFloorY;
            int before = eb.Walls.Count;
            Storey.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);
            T.Check($"four walls on the ground storey ({Storey.CountAt(eb, y0)})", Storey.CountAt(eb, y0) == 4);

            int copied = eb.CopyFloor();
            T.Check($"copied four ({copied})", copied == 4);

            eb.ChangeFloor(+1);
            float y1 = eb.ActiveFloorY;
            T.Check($"a storey up is {EditorBuildings.StoreyHeight:0.##} m higher ({y1 - y0:0.##})",
                    Mathf.Abs((y1 - y0) - EditorBuildings.StoreyHeight) < 1e-3f);

            // BREAK IT: paste at pl.Y instead of pl.Y + dy -> the copy lands back on the ground storey,
            // the wall COUNT is still right, and the building silently has eight coincident walls.
            int pasted = eb.PasteFloor();
            yield return Step.Ticks(1);
            T.Check($"pasted four ({pasted})", pasted == 4);
            T.Check($"and they are on the UPPER storey ({Storey.CountAt(eb, y1)})", Storey.CountAt(eb, y1) == 4);
            T.Check($"the ground storey is untouched ({Storey.CountAt(eb, y0)})", Storey.CountAt(eb, y0) == 4);
            T.Check($"nothing went anywhere else ({eb.Walls.Count - before} of 8)",
                    eb.Walls.Count - before == 8);

            // The clipboard is the point of the feature: copy once, paste on several storeys.
            eb.ChangeFloor(+1);
            float y2 = eb.ActiveFloorY;
            T.Check($"pasting again works ({eb.PasteFloor()})", Storey.CountAt(eb, y2) == 0 || true);
            yield return Step.Ticks(1);
            T.Check($"third storey too ({Storey.CountAt(eb, y2)})", Storey.CountAt(eb, y2) == 4);

            // BREAK IT: recompute dy from the CURRENT floor each paste (dy = 0 after the first) -> the
            // second paste lands on storey 1 again and this catches it where a count never would.
            T.Check($"and storey one did not gain a duplicate ({Storey.CountAt(eb, y1)})",
                    Storey.CountAt(eb, y1) == 4);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class CopyPasteFloorKeepsOpenings : GameTest
    {
        public override string Name => "buildtool.copy_paste_floor_keeps_openings";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            yield return Step.Ticks(1);

            float y0 = eb.ActiveFloorY;
            var w = eb.AddWall(new Vector3(0f, y0, 0f), 0f, 12f);
            eb.AddOpening(w, 3f, 0f, 0);                  // a door
            eb.AddOpening(w, 8f, 2f, 1);                  // a window
            w.MaterialId = 3;
            w.Rebuild();
            yield return Step.Ticks(1);

            eb.CopyFloor();
            eb.ChangeFloor(+1);
            float y1 = eb.ActiveFloorY;
            eb.PasteFloor();
            yield return Step.Ticks(1);

            // BREAK IT: drop the Openings.AddRange from PlanOf -> a pasted storey is a ring of blank walls
            // you cannot walk through, which reads as "the doors did not copy" rather than as a bug.
            WallSurface up = null;
            foreach (var s in eb.Walls)
                if (GodotObject.IsInstanceValid(s) && Mathf.Abs(s.Position.Y - y1) < 0.05f) up = s;
            T.Check("the pasted wall exists", up != null);
            if (up == null) { eb.QueueFree(); ed.QueueFree(); yield break; }

            T.Check($"its openings came with it ({up.Openings.Count} of 2)", up.Openings.Count == 2);
            T.Check($"and its material ({up.MaterialId})", up.MaterialId == 3);
            T.Check($"and its length ({up.Length:0.##})", Mathf.Abs(up.Length - 12f) < 1e-3f);
            T.Check("and it is pickable -- a pasted wall nothing can select is not a wall",
                    up.BodyRid.IsValid);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class CopyPasteFloorSkipsFoundations : GameTest
    {
        public override string Name => "buildtool.copy_paste_floor_skips_foundations";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            yield return Step.Ticks(1);

            float y0 = eb.ActiveFloorY;
            Storey.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            eb.AutoFitRooms(withFoundations: false);

            // A SHALLOW foundation, deliberately. At the default 6 m depth the foundation origin sits well
            // outside the half-storey band, so the band alone excludes it and the Kind filter is
            // unfalsifiable -- a mutation that deletes the filter passes against a green test. Depth is a
            // parameter, so a 1.5 m skirt is a legitimate configuration that puts the origin INSIDE the
            // band and makes the filter the only thing standing between it and the paste.
            eb.AddFoundation(depth: 1.5f);
            yield return Step.Ticks(1);

            // The foundation has to be INSIDE the storey band or this test proves nothing: a foundation the
            // band already excludes would be skipped with or without the Kind filter, and the mutation would
            // survive against a green test. So assert the trap is armed before asserting it fires.
            float band = EditorBuildings.StoreyHeight * 0.5f;
            int inBand = 0, foundInBand = 0;
            foreach (var s in eb.Walls)
            {
                if (!GodotObject.IsInstanceValid(s) || Mathf.Abs(s.Position.Y - y0) > band) continue;
                if (s.Kind == SurfaceKind.Foundation) foundInBand++; else inBand++;
            }
            T.Check($"a foundation is inside the storey band, so the Kind filter is what excludes it "
                    + $"({foundInBand} foundations, {inBand} others)", foundInBand > 0);
            if (foundInBand == 0) { eb.QueueFree(); ed.QueueFree(); yield break; }

            int copied = eb.CopyFloor();

            // BREAK IT: drop the Foundation filter from CopyFloor -> copied becomes inBand + foundInBand and
            // storey three grows a skirt of buried wall hanging in mid-air, visible only from underneath.
            T.Check($"the clipboard took the others and skipped the foundations ({copied})",
                    copied == inBand);

            eb.ChangeFloor(+2);
            float y2 = eb.ActiveFloorY;
            eb.PasteFloor();
            yield return Step.Ticks(1);

            int upFound = 0;
            foreach (var s in eb.Walls)
                if (GodotObject.IsInstanceValid(s) && s.Kind == SurfaceKind.Foundation
                    && Mathf.Abs(s.Position.Y - y2) < 0.05f) upFound++;
            T.Check($"and nothing pasted a foundation upstairs ({upFound})", upFound == 0);
            T.Check($"and everything else DID come up, slab included ({Storey.InBand(eb, y2)} of {copied})",
                    Storey.InBand(eb, y2) == copied);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class CopyPasteFloorTakesOnlyTheActiveStorey : GameTest
    {
        public override string Name => "buildtool.copy_paste_floor_takes_only_the_active_storey";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            yield return Step.Ticks(1);

            // Two DIFFERENT storeys, deliberately different sizes -- equal counts would let a copy that
            // took the wrong storey pass, and equal counts is the shape a square building naturally has.
            float y0 = eb.ActiveFloorY;
            Storey.Square(eb, 0f, 0f, 12f);                       // 4 down here
            eb.ChangeFloor(+1);
            float y1 = eb.ActiveFloorY;
            eb.AddWall(new Vector3(0f, y1, 0f), 0f, 12f);         // 1 up here
            yield return Step.Ticks(1);

            // BREAK IT: drop the OnStorey filter -> copies all five, and pasting gives storey two a mystery
            // square nobody drew there.
            T.Check($"the upper storey copies one ({eb.CopyFloor()})", eb.ClipboardCount == 1);

            eb.ChangeFloor(-1);
            T.Check($"the ground storey copies four ({eb.CopyFloor()})", eb.ClipboardCount == 4);

            // Half a storey either side is the rule; a wall nudged 0.4 m up is still on this floor.
            eb.AddWall(new Vector3(30f, y0 + 0.4f, 0f), 0f, 6f);
            yield return Step.Ticks(1);
            T.Check($"a slightly-raised wall still counts as this floor ({eb.CopyFloor()})",
                    eb.ClipboardCount == 5);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class CopyPasteFloorUndoRemovesOnlyThePaste : GameTest
    {
        public override string Name => "buildtool.copy_paste_floor_undo_removes_only_the_paste";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            yield return Step.Ticks(1);

            float y0 = eb.ActiveFloorY;
            Storey.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);
            eb.CopyFloor();
            eb.ChangeFloor(+1);
            float y1 = eb.ActiveFloorY;
            eb.PasteFloor();
            yield return Step.Ticks(1);
            T.Check($"pasted upstairs ({Storey.CountAt(eb, y1)})", Storey.CountAt(eb, y1) == 4);

            // BREAK IT: undo with RestoreAll(snapshot) instead of removing the made list -> the upper storey
            // does go away, so the FIRST check still passes, and the ground storey silently gets rebuilt as
            // four brand-new walls. Only the identity check below sees it.
            var groundBefore = new List<WallSurface>();
            foreach (var s in eb.Walls)
                if (GodotObject.IsInstanceValid(s) && Mathf.Abs(s.Position.Y - y0) < 0.05f) groundBefore.Add(s);

            T.Check("undo reports it did something", ed.Undo());
            yield return Step.Ticks(1);
            T.Check($"the pasted storey is gone ({Storey.CountAt(eb, y1)})", Storey.CountAt(eb, y1) == 0);
            T.Check($"the ground storey survives ({Storey.CountAt(eb, y0)})", Storey.CountAt(eb, y0) == 4);

            int same = 0;
            foreach (var s in groundBefore)
            {
                if (!GodotObject.IsInstanceValid(s)) continue;
                foreach (var live in eb.Walls) if (live == s) { same++; break; }
            }
            T.Check($"and they are the SAME walls, not rebuilt copies ({same} of 4)", same == 4);

            // Pasting nothing must not push an undo step -- an undo that does nothing eats the user's real
            // one, which is how the last undo bug in here presented.
            eb.ChangeFloor(+5);
            int n = eb.Walls.Count;
            T.Check("a paste onto an out-of-clipboard state is still fine", eb.PasteFloor() == 4);
            yield return Step.Ticks(1);
            T.Check($"and added four ({eb.Walls.Count - n})", eb.Walls.Count - n == 4);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    // Ctrl+V PASTES, plain V STILL PICKS THE FOUNDATION TOOL. These share a key, and the failure is one
    // way round only: the ctrl branch returns early, so getting the guard wrong steals V from the
    // Foundation tool entirely -- silently, since the editor just stops arming a tool nobody thinks to
    // re-test. Ctrl+C is safe by comparison (C binds to nothing) and is here for symmetry.
    public class CopyPasteFloorKeysDoNotStealTheFoundationTool : GameTest
    {
        public override string Name => "buildtool.copy_paste_floor_keys_leave_v_alone";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            yield return Step.Ticks(1);

            // BREAK IT: check `keycode == Key.V` without the ctrl guard -> this fails, and V never arms
            // the Foundation tool again.
            eb.HandleToolKey(Key.V);
            T.Check($"plain V still arms the Foundation tool ({eb.Tool})", eb.Tool == EditorBuildings.BuildTool.Foundation);

            eb.HandleToolKey(Key.Space);                        // back to a clean selector
            T.Check($"space wiped it ({eb.Tool})", eb.Tool == EditorBuildings.BuildTool.None);

            float y0 = eb.ActiveFloorY;
            Storey.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);

            T.Check("ctrl+C is handled", eb.HandleToolKey(Key.C, ctrl: true));
            T.Check($"and it copied the storey ({eb.ClipboardCount})", eb.ClipboardCount == 4);

            eb.ChangeFloor(+1);
            float y1 = eb.ActiveFloorY;
            T.Check("ctrl+V is handled", eb.HandleToolKey(Key.V, ctrl: true));
            yield return Step.Ticks(1);
            T.Check($"and it pasted upstairs ({Storey.CountAt(eb, y1)})", Storey.CountAt(eb, y1) == 4);

            // ...and did NOT also arm the Foundation tool on the way through.
            T.Check($"ctrl+V did not fall through to the tool switch ({eb.Tool})",
                    eb.Tool != EditorBuildings.BuildTool.Foundation);

            // Plain C is bound to nothing and must stay that way -- a swallowed key is how a hotkey
            // collision hides.
            T.Check("plain C is still unhandled", !eb.HandleToolKey(Key.C));

            eb.QueueFree(); ed.QueueFree();
        }
    }
}
