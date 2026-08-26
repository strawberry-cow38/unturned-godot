using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Undo for the two tools that never had it: terrain sculpting and rivers.
    ///
    /// Both were flagged in EditorRiver's own header as a known gap ("NO UNDO, deliberately... a heightmap
    /// snapshot per carve is a different piece of work"). Rivers turned out to be cheap once they became
    /// displacement rather than baked geometry — undo only has to restore the anchors. Terrain is the real one:
    /// its brushes apply every FRAME while held, so the journal opens on press and closes on release, and one
    /// Ctrl+Z has to rewind the whole drag rather than one frame of it.
    ///
    /// The load-bearing checks here are the ones that fail when undo is a no-op. "Height changed back" passes
    /// trivially against a brush that never fired, so every restore is paired with a proof the edit LANDED
    /// first, and the empty-stroke case asserts that nothing is pushed — an empty step on the stack is the bug
    /// where the NEXT Ctrl+Z silently consumes itself and appears ignored.</summary>
    public sealed class EditorUndoTests : GameTest
    {
        public override string Name => "editor.undo_terrain_and_river";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var terr = Terrain.CreateFlat(1, 1, withCollider: true);
            World.AddChild(terr);
            yield return Ticks(2);

            // ---- 1. SCULPT: a whole stroke is one undo step, and it restores EXACTLY.
            float before = terr.SampleHeight(200f, -120f);
            terr.BeginSculptStroke();
            terr.EditHeight(200f, -120f, 90f, 40f);     // three frames of a held drag, same stroke
            terr.EditHeight(210f, -130f, 90f, 25f);
            terr.EditHeight(190f, -110f, 90f, 15f);
            float after = terr.SampleHeight(200f, -120f);
            T.Check($"the sculpt actually landed ({before:0.00} -> {after:0.00} m)", Mathf.Abs(after - before) > 1f);

            var restore = terr.EndSculptStroke();
            T.Check("a stroke that touched cells returns an undo action", restore != null);
            restore?.Invoke();
            float undone = terr.SampleHeight(200f, -120f);
            // Exact, not approximate: the journal stores the original float, so anything other than an exact
            // match means it restored a recomputed value rather than the one it saved.
            T.Check($"undo restores the ORIGINAL height exactly ({undone:0.0000} vs {before:0.0000})",
                Mathf.Abs(undone - before) < 1e-4f);

            // Three frames, ONE step. If the journal reopened per call this would need three undos.
            T.Check("...and the whole drag was a single step, not one per frame",
                Mathf.Abs(terr.SampleHeight(210f, -130f) - before) < 1e-3f);
            yield return Ticks(1);

            // ---- 2. THE EMPTY STROKE. A click that touches nothing must push NOTHING.
            terr.BeginSculptStroke();
            var empty = terr.EndSculptStroke();
            T.Check("a stroke that touched no cells returns null, so no empty step reaches the stack", empty == null);

            // ---- 3. HOLES are journalled too — they are a separate array from the heights.
            terr.BeginSculptStroke();
            terr.EditHoles(400f, -400f, 40f, true);
            int dug = terr.HoleCount;
            T.Check($"the hole brush landed ({dug} holes)", dug > 0);
            var holeUndo = terr.EndSculptStroke();
            T.Check("the hole stroke produced an undo action", holeUndo != null);
            holeUndo?.Invoke();
            T.Check($"undo removes the holes as well as the heights ({terr.HoleCount} holes, was {dug})", terr.HoleCount == 0);
            yield return Ticks(1);

            // ---- 4. RIVERS: snapshot/restore, which is what made this cheap.
            var anchors = new List<Vector3> { new Vector3(300f, 0f, -300f), new Vector3(420f, 0f, -360f), new Vector3(540f, 0f, -300f) };
            var snap = terr.SnapshotRivers();
            int before0 = terr.RiverCount;
            terr.CarveRiverPath(anchors, 8f, 4f);
            T.Check($"the river carved ({before0} -> {terr.RiverCount} rivers)", terr.RiverCount > before0);
            terr.RestoreRivers(snap);
            T.Check($"undo removes the river ({terr.RiverCount} rivers, expected {before0})", terr.RiverCount == before0);

            // A snapshot must be a COPY. If it aliased the live list, carving would have mutated the snapshot
            // too and the restore above would be a no-op that still passed the count check by accident.
            var snap2 = terr.SnapshotRivers();
            terr.CarveRiverPath(anchors, 8f, 4f);
            T.Check($"the snapshot is a copy, not a reference into the live list ({snap2.Count} held vs {terr.RiverCount} live)",
                snap2.Count == before0 && terr.RiverCount > before0);
            terr.RestoreRivers(snap2);
            yield return Ticks(1);

            // ---- 5. ANCHOR MOVE round-trips.
            terr.CarveRiverPath(anchors, 8f, 4f);
            var pre = terr.SnapshotRivers();
            Vector3 origin = terr.Rivers[0].Anchors[1];
            terr.MoveRiverAnchor(0, 1, origin + new Vector3(60f, 0f, 40f));
            T.Check($"the anchor moved ({origin.X:0} -> {terr.Rivers[0].Anchors[1].X:0} x)",
                terr.Rivers[0].Anchors[1].DistanceTo(origin) > 1f);
            terr.RestoreRivers(pre);
            T.Check($"undo puts the anchor back ({terr.Rivers[0].Anchors[1].DistanceTo(origin):0.000} m off)",
                terr.Rivers[0].Anchors[1].DistanceTo(origin) < 1e-3f);

            terr.QueueFree();
            yield break;
        }
    }
}
