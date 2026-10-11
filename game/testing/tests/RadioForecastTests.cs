using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The radio's weather board (strawberry 2026-10-10: "the radio/stereo prop should get a
    /// billboard text that says the weather for today and tomorrow").
    ///
    /// ⭐⭐ THE CLAIM THAT MATTERS IS THAT IT IS NOT LYING. A forecast is only worth shipping if what it
    /// announces is what the world then does, so the board's text is held against the SAME outlook the
    /// weather sim is driven from -- and the sim's chosen weather is checked against the day it names.
    /// Asserting only "the label has words on it" would pass against a board showing yesterday's guess.</summary>
    public sealed class RadioForecast : GameTest
    {
        public override string Name => "radio.forecast_board";

        static RadioDevice Build()
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.3f, 0.25f) } };
            return RadioDevice.Make(mi, "Radio_0");
        }

        public override IEnumerable<Step> Run()
        {
            bool gridWas = PowerNet.GlobalPower;
            // ⚠⚠ TOGGLE, DON'T SET. SetGlobalPower early-outs when the flag already reads the wanted value
            // (`if (_globalPower != on)`), so a plain SetGlobalPower(true) is a NO-OP after another test left
            // the flag true with the mains stale -- and the board then never lights. This test passed alone and
            // failed in a batch until the precondition was forced and then ASSERTED rather than assumed.
            PowerNet.SetGlobalPower(false);
            PowerNet.SetGlobalPower(true);
            T.Check($"the mains are actually live to start with ({PowerNet.MainsLive})", PowerNet.MainsLive);

            var dn = new DayNightCycle { DayLength = 120f, Time = 0.5f, Speed = 0f, VisualsEnabled = false };
            World.AddChild(dn);
            var wm = WeatherManager.Attach(World, null, dn, seed: 31337);
            yield return Ticks(2);

            // ---- THE OPT-IN IS REAL ------------------------------------------------------------------------
            T.Check("seasonal outlook starts OFF", !wm.SeasonalOutlook);
            wm.SeasonalOutlook = true;
            T.Check("...and turns on", wm.SeasonalOutlook);

            var r = Build();
            World.AddChild(r);
            yield return Ticks(3);

            // ---- A DEAD RADIO SAYS NOTHING -----------------------------------------------------------------
            T.Check("an unswitched radio shows no board", !r.BoardVisibleForTest);

            r.Toggle();
            yield return Ticks(3);
            T.Check($"switching it on shows the board [{r.BoardStateForTest}]", r.BoardVisibleForTest);
            T.Check($"...with text ('{r.BoardTextForTest.Replace("\n", " / ")}')", r.BoardTextForTest.Length > 0);

            // ⭐ IT SAYS WHAT THE WEATHER SYSTEM SAYS -- same string, not a parallel re-derivation.
            T.Check("the board matches the manager's forecast", r.BoardTextForTest == wm.ForecastLine);
            T.Check("it names TODAY and TOMORROW",
                    r.BoardTextForTest.Contains("TODAY") && r.BoardTextForTest.Contains("TOMORROW"));

            // ---- AND THE FORECAST IS TRUE ------------------------------------------------------------------
            // ⭐⭐ The sim must actually be driven by the day the board announced. If today is wet, the type the
            // sim schedules has to be the type named -- that equality IS the feature.
            // ⚠⚠ THE CLOCK HAS TO RUN. HubProcess scales its delta by Cycle.Speed, so with Speed = 0 the sim
            // never steps -- the first version of this check sat at ActiveTypeIndex = -1 forever and "today is
            // Clear and the sim scheduled nothing" passed against a sim that had not been asked anything. A
            // deliberately WRONG chooser passed it too, which is how it was caught.
            dn.Speed = 1f;

            // ⚠ AND IT MUST TEST BOTH KINDS OF DAY. Whichever day 0 happens to be only exercises one branch,
            // so a wet day and a clear day are hunted out explicitly.
            int wetDay = -1, clearDay = -1;
            for (int d = 0; d < 400 && (wetDay < 0 || clearDay < 0); d++)
            {
                var o = wm.OutlookFor(d);
                if (o.Wet && wetDay < 0) wetDay = d;
                if (!o.Wet && clearDay < 0) clearDay = d;
            }
            T.Check($"this seed has both a wet day ({wetDay}) and a clear one ({clearDay})",
                    wetDay >= 0 && clearDay >= 0);

            // A WET day: the sim must schedule exactly the type the board named.
            dn.Day = wetDay;
            wm.SeasonalOutlook = true;              // re-arm, which force-refreshes the outlook
            wm.Sim.Clear();
            for (int i = 0; i < 200 && wm.Sim.ActiveTypeIndex < 0; i++) { wm.HubProcess(1.0); yield return Ticks(1); }
            T.Check($"a wet day ('{wm.Today.Name}') schedules the type it named "
                  + $"(got {wm.Sim.ActiveTypeIndex}, wanted {wm.Today.TypeIndex})",
                    wm.Sim.ActiveTypeIndex == wm.Today.TypeIndex && wm.Today.TypeIndex >= 0);

            // A CLEAR day: the sim must schedule NOTHING, however long it is stepped.
            dn.Day = clearDay;
            wm.SeasonalOutlook = true;
            wm.Sim.Clear();
            for (int i = 0; i < 200; i++) wm.HubProcess(1.0);
            yield return Ticks(1);
            T.Check($"a clear day schedules nothing ({wm.Sim.ActiveTypeIndex}, stage {wm.Sim.Stage})",
                    wm.Sim.ActiveTypeIndex < 0);
            dn.Day = 0; wm.SeasonalOutlook = true; dn.Speed = 0f;

            // ---- THE DAY TURNING MOVES THE BOARD ON --------------------------------------------------------
            // ⚠ The board polls rather than subscribing, because the day turning raises no signal. A one-shot
            // read at Build would be frozen on day 0 forever, and nothing above would catch that.
            string before = r.BoardTextForTest;
            var wasTomorrow = wm.Tomorrow;
            dn.Day += 1;
            for (int i = 0; i < 4; i++) { wm.HubProcess(0.1); yield return Ticks(1); }
            T.Check($"a new day re-reads the outlook (today is now '{wm.Today.Name}')",
                    wm.Today.TypeIndex == wasTomorrow.TypeIndex && wm.Today.Wet == wasTomorrow.Wet);
            T.Check("...and the board followed it",
                    r.BoardTextForTest.Contains(wm.Today.Wet ? wm.Today.Name : "Clear"));
            if (before == r.BoardTextForTest && wasTomorrow.Name == wm.Tomorrow.Name)
                T.Check("(board text unchanged because both days read the same -- not a failure)", true);

            // ---- SWITCHING OFF HIDES IT AGAIN --------------------------------------------------------------
            r.Toggle();
            yield return Ticks(3);
            T.Check("switching off hides the board", !r.BoardVisibleForTest);

            // ⚠ CONTROL: cutting the FEED must hide it too, not just the switch. A board that survived a
            // blackout would be a lit screen in a dead house.
            r.Toggle();
            yield return Ticks(3);
            T.Check("on again", r.BoardVisibleForTest);
            PowerNet.SetGlobalPower(false);
            yield return Ticks(4);
            T.Check("CONTROL: a blackout hides the board even with the switch on", !r.BoardVisibleForTest);

            PowerNet.SetGlobalPower(gridWas);
            r.QueueFree(); wm.QueueFree(); dn.QueueFree();
        }
    }
}
