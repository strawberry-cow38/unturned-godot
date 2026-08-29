using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // The material paint tool. strawberry_cow: "give a preview of all the building material combos, have
    // them work as a paint tool, showing a preview of the material on the wall ur hovering."
    //
    // The swatch list is L0 (WallCombosTests) and is not repeated here. What needs an engine is the thing
    // that makes this feature different from every other tool: THE PREVIEW IS A REAL EDIT. There is no ghost
    // material -- hovering writes MaterialId onto the live wall and rebuilds it, because seeing it lit and in
    // place is the whole request. Everything below is about that edit not escaping.
    static class Paint
    {
        public static EditorBuildings Rig(GameTest t, out Editor ed)
        {
            ed = new Editor(); t.World.AddChild(ed);
            var eb = new EditorBuildings(); t.World.AddChild(eb);
            eb.Setup(ed, null, null);
            return eb;
        }

        /// <summary>A combo index that is not what the wall wears AND is a visibly different colour.
        ///
        /// The second half is not belt-and-braces. The swatch list deliberately contains pairs that render
        /// identically -- a palette's role entry and its wall texel are the same colour by construction
        /// (see WallCombosTests) -- so "a different (material, texel)" picks one of those on a fresh wall
        /// and every visual assertion downstream compares a colour against itself and passes vacuously.
        /// Found exactly that way: "hovering repaints it" passed while "the colour changed" did not.</summary>
        public static int OtherThan(WallSurface w)
        {
            var c = WallMaterials.Combos;
            for (int i = 0; i < c.Count; i++)
            {
                if (c[i].Material == w.MaterialId && c[i].Texel == w.Texel) continue;
                if (Rgb(c[i].Rgb) != w.Tint) return i;
            }
            return -1;
        }

        static Color Rgb(int rgb)
            => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
    }

    public class PaintPreviewShowsAndRestores : GameTest
    {
        public override string Name => "buildtool.paint_preview_shows_and_restores";

        public override IEnumerable<Step> Run()
        {
            var eb = Paint.Rig(this, out var ed);
            yield return Step.Ticks(1);
            T.Check($"there are swatches to paint with ({WallMaterials.Combos.Count})",
                    WallMaterials.Combos.Count > 1);
            if (WallMaterials.Combos.Count < 2) { eb.QueueFree(); ed.QueueFree(); yield break; }

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            yield return Step.Ticks(1);
            int wasMat = w.MaterialId, wasTexel = w.Texel;
            var wasTint = w.Tint;

            eb.ActiveCombo = Paint.OtherThan(w);
            eb.PaintHover(w);
            yield return Step.Ticks(1);

            // BREAK IT: make PaintHover set nothing -> the "preview" is invisible, which is the feature.
            T.Check($"hovering repaints it ({w.MaterialId}/{w.Texel} from {wasMat}/{wasTexel})",
                    w.MaterialId != wasMat || w.Texel != wasTexel);
            T.Check("and the colour actually changed", w.Tint != wasTint);
            T.Check("the tool knows what it is previewing", eb.PaintPreviewTarget == w);

            // BREAK IT: restore only MaterialId and not Texel -> a pinned texel survives the un-hover and
            // the wall keeps a colour you only passed the mouse over.
            eb.PaintHover(null);
            yield return Step.Ticks(1);
            T.Check($"moving off restores the palette ({w.MaterialId} of {wasMat})", w.MaterialId == wasMat);
            T.Check($"...and the texel ({w.Texel} of {wasTexel})", w.Texel == wasTexel);
            T.Check("...and therefore the colour", w.Tint == wasTint);
            T.Check("nothing is previewed any more", eb.PaintPreviewTarget == null);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class PaintPreviewNeverReachesDisk : GameTest
    {
        public override string Name => "buildtool.paint_preview_never_reaches_disk";

        static string Path => ProjectSettings.GlobalizePath("res://content/buildings/") + "editor_none_Walls.dat";

        public override IEnumerable<Step> Run()
        {
            // THE ONE THAT MATTERS. A hovered wall really is wearing the swatch, so a save taken while the
            // mouse rests on it would write a colour the user never chose -- and they would never know,
            // because moving the mouse puts the old colour back on screen while the file keeps the new one.
            if (System.IO.File.Exists(Path)) System.IO.File.Delete(Path);
            var eb = Paint.Rig(this, out var ed);
            yield return Step.Ticks(1);
            if (WallMaterials.Combos.Count < 2) { eb.QueueFree(); ed.QueueFree(); yield break; }

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            eb.SetMaterial(w, 7);
            yield return Step.Ticks(1);
            int chosen = w.MaterialId;

            eb.ActiveCombo = Paint.OtherThan(w);
            eb.PaintHover(w);
            yield return Step.Ticks(1);
            T.Check($"the preview is up and differs from the saved choice ({w.MaterialId} vs {chosen})",
                    w.MaterialId != chosen || w.Texel != -1);

            // BREAK IT: drop the _paintHover substitution from PlanOf -> this writes the hovered colour.
            int n = eb.Save();
            T.Check($"saved {n} wall(s)", n == 1);
            T.Check("the file exists", System.IO.File.Exists(Path));
            if (!System.IO.File.Exists(Path)) { eb.QueueFree(); ed.QueueFree(); yield break; }

            var back = WallSave.Read(System.IO.File.ReadAllLines(Path));
            T.Check($"one wall came back ({back.Count})", back.Count == 1);
            if (back.Count != 1) { eb.QueueFree(); ed.QueueFree(); yield break; }
            T.Check($"and it kept the CHOSEN palette, not the hovered one ({back[0].Material} of {chosen})",
                    back[0].Material == chosen);
            T.Check($"and no pinned texel leaked in ({back[0].Texel})", back[0].Texel == -1);

            // Snapshot is the same choke point, and it is what undo captures through.
            var snap = eb.Snapshot();
            T.Check($"Snapshot agrees ({snap[0].Material} of {chosen})", snap[0].Material == chosen);

            // And the preview is still on screen -- hiding it from the file must not cancel it.
            T.Check("the preview survived the save", eb.PaintPreviewTarget == w && w.MaterialId != chosen);

            // CLEAN UP THE FILE. Setup() loads this path, and every in-engine test shares one godot boot --
            // so a layout left on disk here is silently loaded by every test that runs after, and they all
            // start with one extra wall. That is not a hypothetical: it broke four storey tests the first
            // time this ran, and the failures pointed at the dupe code rather than at this line.
            if (System.IO.File.Exists(Path)) System.IO.File.Delete(Path);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class PaintCommitUndoesToTheTrueOriginal : GameTest
    {
        public override string Name => "buildtool.paint_commit_undoes_to_the_true_original";

        public override IEnumerable<Step> Run()
        {
            var eb = Paint.Rig(this, out var ed);
            yield return Step.Ticks(1);
            if (WallMaterials.Combos.Count < 2) { eb.QueueFree(); ed.QueueFree(); yield break; }

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            eb.SetMaterial(w, 11);
            yield return Step.Ticks(1);
            int original = w.MaterialId;

            eb.ActiveCombo = Paint.OtherThan(w);
            eb.PaintHover(w);
            T.Check("committing reports it painted", eb.PaintCommit());
            yield return Step.Ticks(1);
            int painted = w.MaterialId;
            T.Check($"the wall is painted ({painted} from {original})",
                    painted != original || w.Texel != -1);

            // BREAK IT: capture the undo state WITHOUT restoring the preview first -> the undo step
            // remembers the preview, so Ctrl+Z restores the colour it already has and visibly does nothing.
            // The wall is then stuck on the painted colour with no way back.
            eb.PaintHover(null);
            T.Check("undo reports it did something", ed.Undo());
            yield return Step.Ticks(1);
            T.Check($"and it went back to the ORIGINAL, not the preview ({w.MaterialId} of {original})",
                    w.MaterialId == original);
            T.Check($"with no pinned texel left behind ({w.Texel})", w.Texel == -1);

            // Painting what is already there must not push an undo step -- an undo that does nothing eats
            // the user's real one.
            eb.PaintHover(w);
            eb.PaintCommit();
            eb.PaintHover(null);
            yield return Step.Ticks(1);
            int after = w.MaterialId;
            T.Check("repainting the same swatch reports no change", !PaintAgain(eb, w));
            T.Check($"and left the wall alone ({w.MaterialId} of {after})", w.MaterialId == after);

            eb.QueueFree(); ed.QueueFree();
        }

        static bool PaintAgain(EditorBuildings eb, WallSurface w)
        {
            eb.PaintHover(w);
            bool r = eb.PaintCommit();
            eb.PaintHover(null);
            return r;
        }
    }

    public class PaintPreviewDiesWithTheHandles : GameTest
    {
        public override string Name => "buildtool.paint_preview_dies_with_the_handles";

        public override IEnumerable<Step> Run()
        {
            // strawberry_cow: "kill all handles, selection boxes etc when going into play mode". A preview
            // is transient in exactly the same sense, and it is worse than a stray outline: left up, the
            // next save writes it. ClearTransientVisuals is the one teardown authority, so it has to know.
            var eb = Paint.Rig(this, out var ed);
            yield return Step.Ticks(1);
            if (WallMaterials.Combos.Count < 2) { eb.QueueFree(); ed.QueueFree(); yield break; }

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            yield return Step.Ticks(1);
            int was = w.MaterialId;

            eb.ActiveCombo = Paint.OtherThan(w);
            eb.PaintHover(w);
            T.Check("preview is up", w.MaterialId != was || w.Texel != -1);

            // BREAK IT: leave EndPaintPreview out of ClearTransientVisuals -> entering play mode freezes the
            // hovered colour onto the wall for good.
            eb.ClearTransientVisuals();
            yield return Step.Ticks(1);
            T.Check($"entering play mode put the colour back ({w.MaterialId} of {was})", w.MaterialId == was);
            T.Check("and dropped the preview", eb.PaintPreviewTarget == null);

            // Space wipes the tool, which routes through the same teardown.
            eb.PaintHover(w);
            eb.HandleToolKey(Key.Space);
            yield return Step.Ticks(1);
            T.Check($"space wipes it too ({w.MaterialId} of {was})", w.MaterialId == was);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class PaintFollowsTheSelectedSide : GameTest
    {
        public override string Name => "buildtool.paint_follows_the_selected_side";

        public override IEnumerable<Step> Run()
        {
            // Walls are painted per side, so the preview has to be on the side the click will land on.
            //
            // The first version of this test asserted that un-hovering restores the front -- which it
            // could not fail, because EndPaintPreview puts BOTH sides back unconditionally. A mutation
            // aimed at the side logic survived against a green test. The real claim is the one below:
            // flipping the selected side while the cursor sits still MOVES the preview, and the commit
            // lands where the preview was.
            var eb = Paint.Rig(this, out var ed);
            yield return Step.Ticks(1);
            if (WallMaterials.Combos.Count < 2) { eb.QueueFree(); ed.QueueFree(); yield break; }

            var w = eb.AddWall(new Vector3(0f, eb.ActiveFloorY, 0f), 0f, 12f);
            yield return Step.Ticks(1);
            int frontWas = w.MaterialId, backWas = w.MaterialIdBack;

            eb.SelectSide(w, back: false);
            eb.ActiveCombo = Paint.OtherThan(w);
            eb.PaintHover(w);
            yield return Step.Ticks(1);
            T.Check($"the FRONT is previewed ({w.MaterialId} from {frontWas})",
                    w.MaterialId != frontWas || w.Texel != -1);
            T.Check($"and the back is untouched ({w.MaterialIdBack} of {backWas})",
                    w.MaterialIdBack == backWas);

            // BREAK IT: leave the preview where it was on a side flip -> the swatch stays on the front
            // while the click paints the back, and you only find out after painting.
            eb.SelectSide(w, back: true);
            yield return Step.Ticks(1);
            T.Check($"flipping sides moved the preview to the back ({w.MaterialIdBack} from {backWas})",
                    w.MaterialIdBack != backWas || w.TexelBack != -1);
            T.Check($"...and gave the front back ({w.MaterialId} of {frontWas})", w.MaterialId == frontWas);

            // BREAK IT: commit against SelectedBack read fresh rather than the side that was previewed --
            // same value here, but the commit must agree with what was on screen, so this pins it.
            T.Check("committing reports it painted", eb.PaintCommit());
            eb.PaintHover(null);
            yield return Step.Ticks(1);
            T.Check($"the BACK took the paint ({w.MaterialIdBack} from {backWas})",
                    w.MaterialIdBack != backWas || w.TexelBack != -1);
            T.Check($"and the front is still original ({w.MaterialId} of {frontWas})",
                    w.MaterialId == frontWas);

            // And undo puts the back, not the front, back.
            T.Check("undo reports it did something", ed.Undo());
            yield return Step.Ticks(1);
            T.Check($"the back returned to its original ({w.MaterialIdBack} of {backWas})",
                    w.MaterialIdBack == backWas);
            T.Check($"and the front never moved ({w.MaterialId} of {frontWas})", w.MaterialId == frontWas);

            eb.QueueFree(); ed.QueueFree();
        }
    }
}
