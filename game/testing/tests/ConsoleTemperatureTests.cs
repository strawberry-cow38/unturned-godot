using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The temperature debug commands (strawberry 2026-09-11: "add debug commands for temperature. anything we
    // might need").
    //
    // Drives the REAL console dispatch, because that is the only part of these commands that can be wrong.
    // The curves underneath are all L0-covered; what is untested until here is the parse, the null guards and
    // whether the readout is reporting the same numbers the sim is actually running on.
    //
    // Several of these commands exist ONLY to print, so the assertion has to be on the text. That is what
    // DevConsole.LastEcho is for -- a version of this test that only checked side effects would pass on a
    // command that computed everything correctly and then printed the wrong field.
    public sealed class ConsoleTemperature : GameTest
    {
        public override string Name => "console.temperature";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var player = Rigs.Player(World, new Vector3(0, 2, 0));
            var console = new DevConsole { Player = player };
            World.AddChild(console);
            yield return Ticks(2);

            int startBefore = WorldTemperature.StartDayOfYear;   // a process-global; put it back at the end

            // ---- tempset ----
            console.RunForTest("tempset -20");
            yield return Ticks(1);
            T.Check($"`tempset -20` pinned the body at -20 (got {player.Temperature.BodyC:0.0})",
                    Mathf.Abs(player.Temperature.BodyC + 20f) < 0.5f);
            T.Check($"...and said which band that is (got '{console.LastEcho}')",
                    console.LastEcho.Contains("Freezing"));

            // ---- the CONTROL for temphold: prove it drifts without the hold ----
            // Without this leg the hold test below is vacuous -- a body that never moved would pass it. 3 s at
            // a 45 s time constant is ~6% of the gap, which is small but has to be measurable or the control
            // itself is blind.
            float beforeDrift = player.Temperature.BodyC;
            yield return Ticks(3 * Engine.PhysicsTicksPerSecond);
            float drifted = player.Temperature.BodyC;
            T.Check($"unheld, the body drifts back toward ambient ({beforeDrift:0.00} -> {drifted:0.00})",
                    drifted > beforeDrift + 0.2f);

            // ---- temphold ----
            console.RunForTest("tempset -20");
            console.RunForTest("temphold on");
            yield return Ticks(3 * Engine.PhysicsTicksPerSecond);
            T.Check($"`temphold on` holds it there (got {player.Temperature.BodyC:0.00}, expected -20)",
                    Mathf.Abs(player.Temperature.BodyC + 20f) < 0.01f);

            // Moving the pin while already holding must move the HELD value, not snap back to the old one.
            console.RunForTest("tempset 45");
            yield return Ticks(2 * Engine.PhysicsTicksPerSecond);
            T.Check($"`tempset` while held moves the pin (got {player.Temperature.BodyC:0.00}, expected 45)",
                    Mathf.Abs(player.Temperature.BodyC - 45f) < 0.01f);
            T.Check($"...and 45 C reads as Boiling (got {player.Temperature.CurrentBand})",
                    player.Temperature.CurrentBand == PlayerTemperatureSim.Band.Boiling);

            console.RunForTest("temphold off");
            yield return Ticks(3 * Engine.PhysicsTicksPerSecond);
            T.Check($"`temphold off` releases it (got {player.Temperature.BodyC:0.00}, expected below 45)",
                    player.Temperature.BodyC < 44.8f);

            // ---- wetness ----
            console.RunForTest("wetness soak");
            yield return Ticks(1);
            T.Check($"`wetness soak` soaks you (got {player.Temperature.Wetness:0.00})",
                    Mathf.Abs(player.Temperature.Wetness - 1f) < 0.01f);
            console.RunForTest("wetness 0.25");
            yield return Ticks(1);
            T.Check($"`wetness 0.25` takes a number (got {player.Temperature.Wetness:0.00})",
                    Mathf.Abs(player.Temperature.Wetness - 0.25f) < 0.01f);
            console.RunForTest("wetness dry");
            yield return Ticks(1);
            T.Check($"`wetness dry` dries you off (got {player.Temperature.Wetness:0.00})",
                    player.Temperature.Wetness <= 0.001f);
            console.RunForTest("wetness banana");
            T.Check($"a bad wetness prints usage rather than setting something (got '{console.LastEcho}')",
                    console.LastEcho.Contains("usage"));

            // ---- thermal ----
            console.RunForTest("thermal");
            T.Check($"`thermal` with nothing around says so (got '{console.LastEcho}')",
                    console.LastEcho.Contains("no thermal sources"));

            var fire = new Node3D { Name = "TestCampfire" };
            World.AddChild(fire);
            fire.GlobalPosition = new Vector3(2f, 2f, 0f);   // 2 m away, well inside a 6 m radius, clear line of sight
            var src = ThermalSource.AttachTo(fire, 25f, 6f);
            yield return Ticks(2);

            console.RunForTest("thermal");
            T.Check($"`thermal` names the source by its OWNER, not 'ThermalSource' (got '{console.LastEcho}')",
                    console.LastEcho.Contains("TestCampfire"));
            T.Check($"...and reports it warming you (got '{console.LastEcho}')",
                    console.LastEcho.Contains("+") && !console.LastEcho.Contains("out of range"));

            // The readout has to agree with the field that is actually driving the body. This is the check that
            // would catch a `thermal` that quietly re-implemented the range or line-of-sight rules.
            float net = ThermalField.NetC(player, player.GlobalPosition);
            T.Check($"a lit fire 2 m away genuinely warms the player (net {net:0.0} C)", net > 1f);

            src.Active = false;
            yield return Ticks(2);
            console.RunForTest("thermal");
            T.Check($"an unlit source reads OFF (got '{console.LastEcho}')", console.LastEcho.Contains("OFF"));
            T.Check("...and contributes nothing", Mathf.IsZeroApprox(ThermalField.NetC(player, player.GlobalPosition)));

            src.Active = true;
            src.Radius = 1f;   // now 2 m away is outside it
            yield return Ticks(2);
            console.RunForTest("thermal");
            T.Check($"a source out of range says so (got '{console.LastEcho}')", console.LastEcho.Contains("out of range"));

            // ---- worldtemp / startdate ----
            console.RunForTest("worldtemp 15 0500");
            T.Check($"`worldtemp` echoes the date as a month (got '{console.LastEcho}')",
                    console.LastEcho.Contains("15 January"));
            console.RunForTest("worldtemp july noon");
            T.Check($"`worldtemp` takes a month name and a clock word (got '{console.LastEcho}')",
                    console.LastEcho.Contains("July"));
            console.RunForTest("worldtemp notamonth");
            T.Check($"a bad date is refused, not guessed (got '{console.LastEcho}')",
                    console.LastEcho.Contains("bad date"));

            console.RunForTest("startdate july");
            T.Check($"`startdate july` sets the calendar start to day 182 (got {WorldTemperature.StartDayOfYear})",
                    WorldTemperature.StartDayOfYear == 182);
            console.RunForTest("startdate 15");
            T.Check($"`startdate 15` takes a raw day of year (got {WorldTemperature.StartDayOfYear})",
                    WorldTemperature.StartDayOfYear == 15);
            console.RunForTest("startdate 900");
            T.Check($"an out-of-year day is refused (still {WorldTemperature.StartDayOfYear})",
                    WorldTemperature.StartDayOfYear == 15 && console.LastEcho.Contains("bad date"));

            // ---- the bare readout ----
            console.RunForTest("temp");
            string t = console.LastEcho;
            T.Check($"`temp` reports the body and the band (got '{t}')", t.Contains("body") && t.Contains("bar"));
            T.Check("`temp` reports every input, not just the answer",
                    t.Contains("ambient") && t.Contains("weather") && t.Contains("sources")
                    && t.Contains("exertion") && t.Contains("wetness") && t.Contains("clothing"));

            // The readout's ambient must be the one the sim is running on, not a fresh sample. Same reasoning
            // as `thermal` above: a readout that measures for itself is reporting a different probe.
            var probe = player.DebugTemperatureProbe;
            T.Check($"`temp`'s ambient is the probe driving the body ({probe.ambientC:0.0} C)",
                    t.Contains($"{probe.ambientC:0.0} C"));

            WorldTemperature.StartDayOfYear = startBefore;
            T.Check("the calendar start is put back for the tests after this one",
                    WorldTemperature.StartDayOfYear == startBefore);
        }
    }
}
