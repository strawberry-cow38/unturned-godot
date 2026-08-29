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
