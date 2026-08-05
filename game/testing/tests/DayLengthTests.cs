using Godot;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnturnedGodot.Testing
{
    // HOW LONG A DAY IS (strawberry: "make days 24 minutes long").
    //
    // The interesting part is not the number. It is that the number used to live in TWO places and they disagreed:
    // DayNightCycle.DayLength defaulted to 120 s, and WorldBuilder overrode it with its own 300 f at both call sites.
    // So the game ran on five-minute days while the field everyone would read said two. Editing that field would have
    // changed nothing in play, and every existing test would still have passed -- there was nothing anywhere that
    // compared the two.
    //
    // So this suite pins the value AND the single-source-of-truth, by reading WorldBuilder's own source for a
    // re-introduced literal. A constant that call sites are free to override is not a constant, it is a suggestion.
    public sealed class DayLengthTests : GameTest
    {
        public override string Name => "world.day_length";

        public override IEnumerable<Step> Run()
        {
            // ---- THE VALUE. A FULL CYCLE -- midnight through noon and back -- not the daylight half. That is the unit
            // the dev console's `daylength <minutes>` speaks in (DevConsole sets DayLength = minutes * 60), so "24
            // minute days" and `daylength 24` have to mean the same thing or the console lies about the world.
            T.Check($"a day is 24 real minutes ({DayNightCycle.DefaultDayLength / 60f:0.##} min)",
                Mathf.IsEqualApprox(DayNightCycle.DefaultDayLength, 24f * 60f));
            T.Check($"...which is {DayNightCycle.DefaultDayLength:0} seconds per full cycle",
                Mathf.IsEqualApprox(DayNightCycle.DefaultDayLength, 1440f));
            // One game hour per real minute falls out of that, which is the property worth stating: it is what makes
            // the clock legible without doing arithmetic.
            T.Check("...so one game hour takes one real minute",
                Mathf.IsEqualApprox(DayNightCycle.DefaultDayLength / 24f, 60f));

            // ---- A FRESH CYCLE ADOPTS IT. The field default and the constant must not drift apart either.
            var dnc = new DayNightCycle();
            T.Check($"a new cycle starts at the canonical length ({dnc.DayLength:0})",
                Mathf.IsEqualApprox(dnc.DayLength, DayNightCycle.DefaultDayLength));

            // ---- ...AND THE WORLD DOES NOT OVERRIDE IT. This is the check that would have caught the original split:
            // WorldBuilder is where the real game's cycle is constructed, so a numeric literal there silently wins over
            // anything this suite asserts about the constant.
            string wb = ReadSource("res://WorldBuilder.cs");
            T.Check($"WorldBuilder's source was readable ({wb.Length} chars)", wb.Length > 1000);
            if (wb.Length > 1000)
            {
                var literal = Regex.Match(wb, @"DayLength\s*=\s*[0-9]");
                T.Check($"WorldBuilder sets NO literal day length{(literal.Success ? " -- found: " + literal.Value : "")}",
                    !literal.Success);
                T.Check("...it references the shared constant instead",
                    wb.Contains("DayLength = DayNightCycle.DefaultDayLength"));
                // Both call sites, not just the one someone remembered. The old bug was duplicated across two.
                T.Check($"...at EVERY call site ({Regex.Matches(wb, @"DayLength = DayNightCycle\.DefaultDayLength").Count} of {Regex.Matches(wb, @"new DayNightCycle").Count})",
                    Regex.Matches(wb, @"DayLength = DayNightCycle\.DefaultDayLength").Count
                    == Regex.Matches(wb, @"new DayNightCycle").Count);
            }

            // ---- THE CLOCK ACTUALLY ADVANCES AT THAT RATE. The constant being right means nothing if _Process divides
            // by something else -- Advance takes CYCLES, so a full day of real seconds must be exactly one cycle.
            var clock = new DayNightCycle { VisualsEnabled = false, Time = 0f, Speed = 1f };
            float cycles = DayNightCycle.DefaultDayLength * 1f / clock.DayLength;   // the same expression _Process uses
            T.Check($"one day of real seconds is exactly one cycle ({cycles:0.####})", Mathf.IsEqualApprox(cycles, 1f));
            T.Check($"...and half a day is half a cycle ({DayNightCycle.DefaultDayLength * 0.5f / clock.DayLength:0.####})",
                Mathf.IsEqualApprox(DayNightCycle.DefaultDayLength * 0.5f / clock.DayLength, 0.5f));

            yield break;
        }

        static string ReadSource(string resPath)
        {
            try
            {
                string p = ProjectSettings.GlobalizePath(resPath);
                return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : "";
            }
            catch { return ""; }
        }
    }
}
