using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // "doors shouldnt bounce closed. they should bounce open. and be sorta solid closing" (strawberry).
    //
    // That is a property of the sampled retail curves, and it held for every door except Cooler_0, whose open and
    // close curves were swapped: it sprang 10 degrees PAST SHUT on closing and barely moved past its stop on
    // opening -- precisely inverted. It survived a fix pass aimed at the whole set, and it survived my own first
    // measurement of it, because I compared PEAK values and a close-side overshoot is NEGATIVE (the door swings
    // past zero). Peak reported +0.0% for a curve dipping to -7.4%.
    //
    // So this asserts the invariant over EVERY door in the catalog rather than the one prop that was broken:
    // opening overshoot must exceed closing overshoot. That is scale-free -- props legitimately differ in how
    // springy they are -- and it is exactly what a swap breaks, on any prop, including ones added later.
    public sealed class DoorCurveBounceTests : GameTest
    {
        public override string Name => "props.doors_bounce_open_not_closed";

        // frac = angle / finalAngle. Opening runs 0 -> 1 and overshoots ABOVE 1; closing runs 1 -> 0 and
        // overshoots BELOW 0. Measuring one and not the other is how the cooler hid.
        static (float over, int n) Overshoot(string path, bool opening)
        {
            if (!System.IO.File.Exists(path)) return (float.NaN, 0);
            float worst = 0f; int n = 0;
            foreach (var line in System.IO.File.ReadLines(path))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float frac)) continue;
                n++;
                worst = opening ? Mathf.Max(worst, frac - 1f) : Mathf.Max(worst, -frac);
            }
            return (worst, n);
        }

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            string cat = dir + "doors.txt";
            T.Check("the door catalog exists", System.IO.File.Exists(cat));
            if (!System.IO.File.Exists(cat)) yield break;

            int checkedProps = 0, withCurves = 0;
            var offenders = new List<string>();
            foreach (var line in System.IO.File.ReadLines(cat))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                checkedProps++;
                // the curve key is the leaf mesh file minus "_door.obj" -- the same derivation WorldBuilder uses,
                // which is why the cooler's Cooler_0_Hinge_0 curves are found under a name unlike its prop name
                string mesh = p[1];
                string key = mesh.EndsWith("_door.obj") ? mesh[..^"_door.obj".Length] : p[0];
                var (openOver, nOpen) = Overshoot(dir + "door_curves/" + key + "_open.txt", true);
                var (closeOver, nClose) = Overshoot(dir + "door_curves/" + key + "_close.txt", false);
                if (nOpen == 0 || nClose == 0) continue;   // no sampled clip: falls back to procedural easing
                withCurves++;
                if (!(openOver > closeOver))
                    offenders.Add($"{key} (open +{openOver * 100f:0.0}% vs close +{closeOver * 100f:0.0}%)");
            }

            T.Check($"the catalog has doors with sampled curves ({withCurves} of {checkedProps})", withCurves > 0);
            T.Check($"every door bounces OPEN more than it bounces CLOSED"
                    + (offenders.Count > 0 ? " -- offenders: " + string.Join(", ", offenders) : ""),
                    offenders.Count == 0);
            yield break;
        }
    }
}
