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
}
