using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // In-engine cover for stair PLACEMENT. StairsTests (L0) proves the arithmetic -- steps * rise lands on the
    // storey exactly -- but arithmetic cannot tell you whether the treads end up in the right place, because
    // that depends on a convention the generator has to get right: Rebuild() builds a surface with its
    // thickness CENTRED (+/- Thickness/2), and a flat tread is pitched -90, so the walking surface of a tread
    // sits half a tread ABOVE its origin. Get that wrong and every flight is sunk or floating by 0.1 -- which
    // renders perfectly and is invisible in a screenshot.
    public class StairsLandFlushOnTheFloorAbove : GameTest
    {
        public override string Name => "buildtool.stairs_land_flush";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            float baseY = 0f;
            int steps = eb.AddStairs(new Vector3(0f, baseY, 0f), 0f);
            yield return Step.Ticks(1);

            T.Check($"a flight is emitted ({steps} treads)", steps == WallOpenings.StairSteps(EditorBuildings.StoreyHeight));

            // Collect the treads by kind, so this reads what the generator actually produced rather than
            // re-deriving it. A flight that emitted nothing would otherwise pass every check below vacuously.
            var treads = new List<WallSurface>();
            foreach (var w in eb.Walls) if (w.Kind == SurfaceKind.Stairs) treads.Add(w);
            T.Check($"...and they are tagged Stairs ({treads.Count})", treads.Count == steps && steps > 0);

            if (treads.Count == 0) yield break;

            // The walking surface of a tread is its origin plus half its thickness -- that is the convention
            // under test, not an assumption being restated: if the generator forgot the half-thickness the
            // top tread lands 0.1 low and this fails.
            float half = WallOpenings.StairTreadThickness * 0.5f;
            float topWalk = float.MinValue, lowWalk = float.MaxValue;
            foreach (var t in treads)
            {
                float walk = t.Position.Y + half;
                topWalk = Mathf.Max(topWalk, walk);
                lowWalk = Mathf.Min(lowWalk, walk);
            }

            float storey = EditorBuildings.StoreyHeight;
            T.Check($"the top tread is flush with the floor above ({topWalk:0.000} vs {storey:0.000})",
                    Mathf.Abs(topWalk - storey) < 0.01f);
            // And the FIRST step is one rise up, not sitting on the floor -- a flight whose bottom tread is at
            // zero has one step too many and would reach the storey with a doubled first stair.
            float rise = WallOpenings.StairStepRise(storey);
            T.Check($"the bottom tread is one rise up ({lowWalk:0.000} vs {rise:0.000})",
                    Mathf.Abs(lowWalk - rise) < 0.01f);

            // Evenly spaced: no accumulating drift, which a per-step increment would introduce and a
            // multiply would not. Checked as a spread over all gaps rather than first-vs-last.
            var ys = new List<float>();
            foreach (var t in treads) ys.Add(t.Position.Y);
            ys.Sort();
            float minGap = float.MaxValue, maxGap = float.MinValue;
            for (int i = 1; i < ys.Count; i++)
            { float g = ys[i] - ys[i - 1]; minGap = Mathf.Min(minGap, g); maxGap = Mathf.Max(maxGap, g); }
            T.Check($"steps are evenly spaced (gap {minGap:0.000}..{maxGap:0.000})", maxGap - minGap < 0.005f);

            eb.QueueFree();
        }
    }

    // Snapping is by the STAIRCASE'S FOOTPRINT, not the lattice. A flight is anchored at its origin
    // corner and fills one lattice across by run/lattice along -- a 2x1 tile block at the kit's storey.
    // Snapping the origin to lattice joints steps by 3 along an axis the flight occupies 6 of, so
    // consecutive placements overlap by a whole tile and the flight never sits on the block it fills.
    // strawberry: "it should snap to the 2x1 full tiles of the staircase".
    public class StairsSnapToTheirOwnFootprint : GameTest
    {
        public override string Name => "buildtool.stairs_snap_to_footprint";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            float run = WallOpenings.StairDefaultRun(EditorBuildings.StoreyHeight);
            float width = WallOpenings.StairDefaultWidth;
            T.Check($"the kit's flight is 2 tiles deep ({run} / {WallOpenings.LatticeStep})",
                    Mathf.Abs(run - 2f * WallOpenings.LatticeStep) < 0.01f);

            // yaw 0: the run axis is world -Z. A hit 2 m back rounds to the NEAREST WHOLE FLIGHT (0),
            // where lattice snapping would have put it at -3 -- half a flight out, straddling two blocks.
            // BREAK IT: snap world X/Z by SnapGrid and this lands on -3.
            var a = EditorBuildings.SnapStairOrigin(new Vector3(0.4f, 7f, -2f), 0f, run, width);
            T.Check($"the run axis snaps by RUN, not lattice (z {a.Z:0.00})", Mathf.Abs(a.Z) < 0.01f);
            T.Check($"...and the width axis snaps by WIDTH (x {a.X:0.00})", Mathf.Abs(a.X) < 0.01f);
            T.Check($"...and the hit's height is untouched ({a.Y:0.0})", Mathf.Abs(a.Y - 7f) < 0.01f);

            // One flight further back lands exactly one RUN away -- adjacent, not overlapping.
            var b = EditorBuildings.SnapStairOrigin(new Vector3(0.4f, 7f, -run - 0.4f), 0f, run, width);
            T.Check($"consecutive positions are one full flight apart ({Mathf.Abs(b.Z - a.Z):0.00} vs {run:0.00})",
                    Mathf.Abs(Mathf.Abs(b.Z - a.Z) - run) < 0.01f);

            // YAW-AWARE. Facing east the run axis is world X, so THAT is the axis that must step by run.
            // BREAK IT: snap world Z by run regardless of yaw and this fails.
            var e = EditorBuildings.SnapStairOrigin(new Vector3(-2f, 7f, 0.4f), 90f, run, width);
            var e2 = EditorBuildings.SnapStairOrigin(new Vector3(-run - 0.4f, 7f, 0.4f), 90f, run, width);
            T.Check($"facing east, the RUN axis is world X ({Mathf.Abs(e2.X - e.X):0.00} vs {run:0.00})",
                    Mathf.Abs(Mathf.Abs(e2.X - e.X) - run) < 0.01f);

            // The GHOST and the CLICK must agree -- they call the same function now, and this is the
            // assertion that keeps it that way. BREAK IT: give the ghost back its own SnapGrid pair.
            var hit = new Vector3(4.2f, 2000f, -7.7f);
            int n = eb.PlaceStairsAt(hit, 0f);
            yield return Step.Ticks(1);
            T.Check($"a flight was placed ({n})", n > 0);
            var expect = EditorBuildings.SnapStairOrigin(hit, 0f, run, width);
            float best = float.MaxValue;
            foreach (var w in eb.Walls)
                if (GodotObject.IsInstanceValid(w) && w.Kind == SurfaceKind.Stairs)
                    best = Mathf.Min(best, Mathf.Abs(w.Position.X - expect.X) + Mathf.Abs(w.Position.Z - expect.Z));
            T.Check($"the placed flight sits at the snapped origin (off by {best:0.00})", best < 0.01f);

            eb.QueueFree();
        }
    }

    // The bug every other stair test passed straight through: the tool substituted FloorY (the storey offset,
    // 0 on the ground floor) for the raycast hit's WORLD Y. The editor stage sits at y = 2000, so every flight
    // was placed 2000 below the building -- present, undoable, invisible. strawberry: "the stairs tool doesnt
    // do anything."
    //
    // AddStairs was never wrong, which is exactly why testing AddStairs could not catch it. This drives
    // PlaceStairsAt, the decision the tool actually makes.
    public class StairsPlaceAtTheCursorNotTheStoreyOffset : GameTest
    {
        public override string Name => "buildtool.stairs_place_at_the_hit";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            // A hit far from the origin in BOTH the stage sense (y) and the plan sense (x/z), so a dropped
            // component shows up as a large miss rather than rounding.
            var hit = new Vector3(37f, 2000f, -14f);
            int n = eb.PlaceStairsAt(hit, 0f);
            yield return Step.Ticks(1);
            T.Check($"a flight is emitted ({n})", n > 0);

            float lowest = float.MaxValue, highest = float.MinValue;
            int treads = 0;
            foreach (var w in eb.Walls)
                if (w.Kind == SurfaceKind.Stairs)
                { treads++; lowest = Mathf.Min(lowest, w.Position.Y); highest = Mathf.Max(highest, w.Position.Y); }
            T.Check($"treads exist ({treads})", treads == n && n > 0);
            if (treads == 0) yield break;

            // BREAK IT: pass FloorY instead of groundHit.Y -> lowest lands near 0 and this fails by ~2000.
            T.Check($"the flight is built at the cursor's height, not the storey offset ({lowest:0.0})",
                    lowest > hit.Y - 1f && lowest < hit.Y + EditorBuildings.StoreyHeight + 1f);
            T.Check($"and it climbs one storey from there ({highest - lowest + WallOpenings.StairTreadThickness:0.00})",
                    Mathf.Abs(highest + WallOpenings.StairTreadThickness * 0.5f - (hit.Y + EditorBuildings.StoreyHeight)) < 0.01f);

            // The plan position is snapped, not discarded -- the other half of the same mistake.
            float x = float.MaxValue, z = float.MaxValue;
            foreach (var w in eb.Walls)
                if (w.Kind == SurfaceKind.Stairs) { x = Mathf.Min(x, Mathf.Abs(w.Position.X - hit.X)); z = Mathf.Min(z, Mathf.Abs(w.Position.Z - hit.Z)); }
            T.Check($"placed near the cursor in plan too (dx {x:0.0}, dz {z:0.0})",
                    x <= WallOpenings.LatticeStep && z <= WallOpenings.LatticeStep);

            eb.QueueFree();
        }
    }
}
