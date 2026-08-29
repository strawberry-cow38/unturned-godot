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

        /// <summary>An empty stage. Setup() loads whatever layout is on disk, and every in-engine test
        /// shares one godot boot -- so an absolute surface count here is really a claim about what earlier
        /// tests left behind. BuildToolTests solves this by counting deltas; these tests need absolute
        /// positions to say anything at all, so they clear instead.</summary>
        public static void Empty(EditorBuildings eb) => eb.RestoreAll(new List<WallPlan>());

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
            Storey.Empty(eb);
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
            int second = eb.PasteFloor();
            T.Check($"the clipboard survives the first paste and pastes again ({second})", second == 4);
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
            Storey.Empty(eb);
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

    public class CopyPasteFloorSkipsFoundationsAndRoofs : GameTest
    {
        public override string Name => "buildtool.copy_paste_floor_skips_foundations_and_roofs";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            Storey.Empty(eb);
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
                if (s.Kind == SurfaceKind.Foundation) foundInBand++;
                else if (s.Kind != SurfaceKind.Roof) inBand++;      // the roof is added below
            }
            T.Check($"a foundation is inside the storey band, so the Kind filter is what excludes it "
                    + $"({foundInBand} foundations, {inBand} others)", foundInBand > 0);
            if (foundInBand == 0) { eb.QueueFree(); ed.QueueFree(); yield break; }

            // A ROOF on the same storey, excluded for the same reason. strawberry_cow: "no foundy or roof".
            var roof = eb.AddWall(new Vector3(20f, y0, 0f), 0f, 6f);
            roof.Kind = SurfaceKind.Roof;
            roof.Rebuild();
            yield return Step.Ticks(1);

            int copied = eb.CopyFloor();

            // BREAK IT: drop either Kind from the filter -> copied grows by that kind's count, and storey
            // three gets a buried skirt or a roof sandwiched inside the floor above it.
            T.Check($"the clipboard took the others and skipped foundation + roof ({copied} of {inBand})",
                    copied == inBand);

            eb.ChangeFloor(+2);
            float y2 = eb.ActiveFloorY;
            eb.PasteFloor();
            yield return Step.Ticks(1);

            int upFound = 0, upRoof = 0;
            float b2 = EditorBuildings.StoreyHeight * 0.5f;
            foreach (var s in eb.Walls)
            {
                if (!GodotObject.IsInstanceValid(s) || Mathf.Abs(s.Position.Y - y2) > b2) continue;
                if (s.Kind == SurfaceKind.Foundation) upFound++;
                if (s.Kind == SurfaceKind.Roof) upRoof++;
            }
            T.Check($"and nothing pasted a foundation upstairs ({upFound})", upFound == 0);
            T.Check($"nor a roof ({upRoof})", upRoof == 0);
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
            Storey.Empty(eb);
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
            Storey.Empty(eb);
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

    // CTRL+D DUPLICATES. strawberry_cow: "ctrl d should dupe floors instead imo" -- this replaced a
    // ctrl+C/ctrl+V pair, and the replacement is strictly safer because D is unbound where V is the
    // Foundation tool. The V check stays anyway: it is now a regression guard on the binding that pair
    // used to threaten.
    public class DuplicateFloorKeyDupesAndMovesUp : GameTest
    {
        public override string Name => "buildtool.duplicate_floor_key";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            Storey.Empty(eb);
            yield return Step.Ticks(1);

            eb.HandleToolKey(Key.V);
            T.Check($"plain V still arms the Foundation tool ({eb.Tool})",
                    eb.Tool == EditorBuildings.BuildTool.Foundation);
            eb.HandleToolKey(Key.Space);

            float y0 = eb.ActiveFloorY;
            int floor0 = eb.ActiveFloor;
            Storey.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);

            T.Check("ctrl+D is handled", eb.HandleToolKey(Key.D, ctrl: true));
            yield return Step.Ticks(1);
            float y1 = eb.ActiveFloorY;

            // BREAK IT: dupe without the ChangeFloor -> the copy lands on the storey you are already on,
            // inside the walls that are already there, and the whole thing reads as "nothing happened".
            T.Check($"it moved you up a storey ({eb.ActiveFloor} from {floor0})", eb.ActiveFloor == floor0 + 1);
            T.Check($"the new storey has the copy ({Storey.CountAt(eb, y1)})", Storey.CountAt(eb, y1) == 4);
            T.Check($"the original is untouched ({Storey.CountAt(eb, y0)})", Storey.CountAt(eb, y0) == 4);

            // Again, from where it left you: this is how you actually build a tower.
            eb.HandleToolKey(Key.D, ctrl: true);
            yield return Step.Ticks(1);
            T.Check($"a second dupe stacks a third storey ({eb.ActiveFloor})", eb.ActiveFloor == floor0 + 2);
            T.Check($"...with its own copy ({Storey.CountAt(eb, eb.ActiveFloorY)})",
                    Storey.CountAt(eb, eb.ActiveFloorY) == 4);
            T.Check($"and storey one did not gain a duplicate ({Storey.CountAt(eb, y1)})",
                    Storey.CountAt(eb, y1) == 4);

            // BREAK IT: drop the ctrl guard -> plain D dupes on every press, which is the sort of thing you
            // discover after building four accidental storeys.
            int n = eb.Walls.Count;
            T.Check("plain D is unhandled", !eb.HandleToolKey(Key.D));
            T.Check($"and duplicated nothing ({eb.Walls.Count - n})", eb.Walls.Count == n);

            // An empty storey has nothing to dupe, and must not move you or push an undo step.
            eb.ChangeFloor(+3);
            int at = eb.ActiveFloor;
            int empty = eb.DuplicateFloor();          // ONE call: interpolating it as well ran it twice
            T.Check($"duplicating an empty storey does nothing ({empty})", empty == 0);
            T.Check($"...and leaves you where you were ({eb.ActiveFloor} of {at})", eb.ActiveFloor == at);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    // "make sure all settings are preserved for each tweakable when duped" -- strawberry_cow.
    //
    // COMPARES THE LIVE SURFACES, NOT TWO SNAPSHOTS. The first version of this compared Snapshot() before
    // against Snapshot() after, which is a test that cannot see the bug it was written for: both sides come
    // out of PlanOf, so a field PlanOf FORGETS TO READ is absent from both and compares equal. Mutation
    // proved it -- deleting GableRise and then the back material from PlanOf's initialiser both left this
    // green. Reading the surfaces themselves means a dropped field shows up as the copy sitting at its
    // default while the original still carries the value.
    //
    // The field LIST still comes from WallPlan by reflection, so a tweakable added later is compared later.
    // A name with no mapping below is a hard failure rather than a skip: silently ignoring an unknown field
    // is exactly how the first version passed.
    public class DuplicatedSurfacesKeepEverySetting : GameTest
    {
        public override string Name => "buildtool.duplicated_surfaces_keep_every_setting";

        /// <summary>The live value of a WallPlan field, read off the surface. Returns false for a name this
        /// test has never heard of, which fails the run.</summary>
        static bool Live(WallSurface w, string field, out object v)
        {
            switch (field)
            {
                case "X": v = w.Position.X; return true;
                case "Z": v = w.Position.Z; return true;
                case "Yaw": v = w.RotationDegrees.Y; return true;
                case "Pitch": v = w.RotationDegrees.X; return true;
                case "Kind": v = w.Kind; return true;
                case "GableRise": v = w.GableRise; return true;
                case "Length": v = w.Length; return true;
                case "Height": v = w.Height; return true;
                case "Thickness": v = w.Thickness; return true;
                case "Material": v = w.MaterialId; return true;
                case "Texel": v = w.Texel; return true;
                case "InsetL0": v = w.InsetL0; return true;
                case "InsetL1": v = w.InsetL1; return true;
                case "InsetR0": v = w.InsetR0; return true;
                case "InsetR1": v = w.InsetR1; return true;
                case "MaterialBack": v = w.MaterialIdBack; return true;
                case "TexelBack": v = w.TexelBack; return true;
                default: v = null; return false;
            }
        }

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            Storey.Empty(eb);
            yield return Step.Ticks(1);

            // Every tweakable OFF its default, so a dropped one is visible. A field left at its default
            // compares equal whether it was copied or freshly constructed -- the degenerate fixture that
            // would make this whole test vacuous.
            var w = eb.AddWall(new Vector3(3f, eb.ActiveFloorY, -6f), 37f, 9f);
            eb.AddOpening(w, 2f, 0f, 0);
            eb.AddOpening(w, 6f, 2f, 1);
            w.MaterialId = 13;      w.Texel = 4;
            w.MaterialIdBack = 27;  w.TexelBack = 6;
            w.Height = 5.5f;        w.Thickness = 0.42f;
            w.GableRise = 1.75f;
            w.InsetL0 = 0.3f; w.InsetL1 = 0.9f; w.InsetR0 = 0.15f; w.InsetR1 = 1.2f;
            w.RotationDegrees = new Vector3(-12f, 37f, 0f);
            w.Rebuild();
            yield return Step.Ticks(1);
            float srcY = w.Position.Y;

            int made = eb.DuplicateFloor();
            yield return Step.Ticks(1);
            T.Check($"duped one ({made})", made == 1);

            WallSurface copy = null;
            foreach (var s in eb.Walls) if (GodotObject.IsInstanceValid(s) && s != w) copy = s;
            T.Check("the copy exists", copy != null);
            if (copy == null) { eb.QueueFree(); ed.QueueFree(); yield break; }

            int compared = 0, wrong = 0, unmapped = 0;
            string firstBad = null, firstUnmapped = null;
            foreach (var f in typeof(WallPlan).GetFields())
            {
                if (f.Name == "Openings") continue;                 // compared on its own below
                if (f.Name == "Y") continue;                        // must differ: it went up a storey
                if (!Live(w, f.Name, out var a) || !Live(copy, f.Name, out var b))
                { unmapped++; firstUnmapped ??= f.Name; continue; }
                compared++;
                // A TIGHT epsilon. Yaw survives a round trip through the node quaternion and comes back
                // 37.000 as 36.999992; a genuinely dropped field arrives at its DEFAULT, orders of
                // magnitude away.
                if (a is float fa && b is float fb) { if (Mathf.Abs(fa - fb) < 1e-3f) continue; }
                else if (Equals(a, b)) continue;
                wrong++;
                firstBad ??= $"{f.Name}: {a} -> {b}";
            }

            // BREAK IT: drop any field from PlanOf's initialiser -> the copy gets that field's default and
            // this names it. No edit here is needed to cover a field added later.
            T.Check($"every WallPlan field survived the dupe ({compared} compared, {wrong} wrong"
                    + (firstBad == null ? ")" : $", first: {firstBad})"), wrong == 0);
            T.Check($"every WallPlan field is mapped to a live one ({unmapped} unmapped"
                    + (firstUnmapped == null ? ")" : $", first: {firstUnmapped})"), unmapped == 0);
            T.Check($"and there were real fields to compare ({compared})", compared >= 15);

            T.Check($"Y moved up exactly one storey ({copy.Position.Y - srcY:0.###})",
                    Mathf.Abs((copy.Position.Y - srcY) - EditorBuildings.StoreyHeight) < 1e-3f);
            T.Check($"both openings came ({copy.Openings.Count} of {w.Openings.Count})",
                    copy.Openings.Count == w.Openings.Count && copy.Openings.Count == 2);

            eb.QueueFree(); ed.QueueFree();
        }
    }
}
