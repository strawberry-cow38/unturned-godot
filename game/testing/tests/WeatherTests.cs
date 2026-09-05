using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // L1 for weather: the scheduler itself is covered engine-free (WeatherSimTests), so these guard the wiring only
    // the engine can answer -- that the sim actually reaches the RAIN INTENSITY the visuals scale off, the day/night
    // overcast flag, and that lightning is gated to the weather that carries it.
    //
    // NOTE: the 2D RainOverlay these once pinned was RETIRED for the worldspace 3D rain (RainSystem3D + the
    // rain_wetness/rain_intensity globals). WeatherManager forces Overlay.Raining=false and drives the 3D rain off
    // `rint` (= BlendAlpha x Severity). ⚠ These assert `wm.Rain3DIntensity` -- the ACTUAL value RainSystem3D consumes
    // to fade the rain (WeatherManager writes `_rain3d.Intensity = rint * shelter`; shelter reads 1 with no camera in
    // a headless test, so this equals rint here). It is NOT a parallel getter: zero the driving `rint` and this reads
    // 0 and the tests go red -- which is the point (tinyclaw finding 3: the old `RainVisualIntensity` re-derived rint
    // on its own, so a dead output path still passed). A null overlay is passed, exactly as the real PEI attach does.

    // Forcing rain drives the real rain input; heavy is denser than light IN THE SAME RUN; clearing it zeroes it.
    public class WeatherDrivesRain : GameTest
    {
        public override string Name => "weather.drives_rain";
        // Both rain assets fade in over 20 s. The default 15 s watchdog only ever held because the test's short weather
        // windows (0.05-0.15 of a 120 s day = 6-18 s) made WeatherSim shrink the fades to fit; with storms 4x longer
        // (master 2026-09-04 "rain storms should last much longer") the window clears 40 s and the fades run at their
        // real 20 s, which is what the game shows -- so the test now waits for the real thing. Nightly 2026-09-05.
        public override double TimeoutSimSeconds => 90;
        public override IEnumerable<Step> Run()
        {
            var dn = new DayNightCycle { DayLength = 120f, VisualsEnabled = false };
            World.AddChild(dn);
            var wm = WeatherManager.Attach(World, null, dn, seed: 12345);   // null overlay -- the 3D rain replaced the 2D one
            yield return Ticks(2);

            T.Check($"starts dry (rint {wm.Rain3DIntensity:0.000})", wm.Rain3DIntensity <= 0.001f);

            wm.Sim.ForecastImmediately(0);   // Default Rain
            // Wait for the fade-in to visibly begin. A fixed couple of ticks doesn't clear a threshold on this port's
            // fast/short active window (rint reads ~0 for the first frames), so poll rather than assume a tick count.
            yield return Until(() => wm.Rain3DIntensity > 0.05f, 30);
            T.Check($"rain fades in as the weather starts (rint {wm.Rain3DIntensity:0.000})", wm.Rain3DIntensity > 0.05f);

            // run out the fade-in. PEI's active window (0.05-0.15 cycles) is SHORTER than the asset's 20 s fade-in on
            // this port's 120 s day, so the peak lands mid-shower -- wait for the peak rather than a fixed tick count.
            yield return Until(() => wm.Sim.BlendAlpha > 0.9f, 25);
            // Default Rain's severity is its asset Fog_Density 0.7, so a fully-committed LIGHT shower tops out ~0.7,
            // not 1.0 -- Heavy Rain is the one that reaches full.
            T.Check($"light rain settles near its 0.7 severity (rint {wm.Rain3DIntensity:0.00})",
                    wm.Rain3DIntensity > 0.6f && wm.Rain3DIntensity < 0.8f);
            T.Check("day/night flipped to overcast", dn.Overcast);

            // Escalate light -> HEAVY in the SAME run and prove heavy is genuinely denser. Nothing else compares the
            // two within one run; the old heavy check just asserted >0.9, which its own neighbours already implied.
            float lightRint = wm.Rain3DIntensity;
            wm.Sim.ForecastImmediately(1);   // Heavy Rain (severity 1.0)
            yield return Until(() => wm.Rain3DIntensity > lightRint + 0.15f, 30);
            T.Check($"heavy rain is denser than light in the same run ({wm.Rain3DIntensity:0.00} > {lightRint:0.00})",
                    wm.Rain3DIntensity > lightRint + 0.15f);

            wm.Sim.Clear();
            yield return Ticks(2);
            T.Check($"clearing the weather zeroes the rain (rint {wm.Rain3DIntensity:0.000})", wm.Rain3DIntensity <= 0.001f);
            T.Check("overcast released", !dn.Overcast);
        }
    }

    // Lightning belongs to Heavy Rain only (Default Rain's .asset has no Has_Lightning), and a strike must survive.
    public class WeatherLightningGating : GameTest
    {
        public override string Name => "weather.lightning_gating";
        public override double TimeoutSimSeconds => 60;   // the 20 s fade-in at full length (see WeatherDrivesRain)
        public override IEnumerable<Step> Run()
        {
            var dn = new DayNightCycle { DayLength = 120f, VisualsEnabled = false };
            World.AddChild(dn);
            var wm = WeatherManager.Attach(World, null, dn, seed: 999);
            yield return Ticks(2);

            T.Check("Default Rain carries no lightning (matches DefaultRain.asset)", !WeatherSim.PeiTypes()[0].HasLightning);
            T.Check("Heavy Rain does, at the ripped 15-60 s interval",
                    WeatherSim.PeiTypes()[1].HasLightning
                    && Mathf.Abs(WeatherSim.PeiTypes()[1].MinLightningInterval - 15f) < 0.01f
                    && Mathf.Abs(WeatherSim.PeiTypes()[1].MaxLightningInterval - 60f) < 0.01f);

            wm.Sim.ForecastImmediately(1);   // Heavy Rain
            yield return Until(() => wm.Sim.BlendAlpha > 0.9f, 25);
            T.Check($"heavy rain committed (blend {wm.Sim.BlendAlpha:0.00})", wm.Sim.BlendAlpha > 0.9f);
            T.Check("heavy rain pulls the fishing bite interval down (0.8 at full)",
                    WeatherManager.FishBiteInterval < 0.95f);

            // heavy rain reaches full intensity at the ACTUAL 3D-rain input (severity 1.0 vs light's 0.7).
            T.Check($"heavy rain reaches full intensity (rint {wm.Rain3DIntensity:0.00})", wm.Rain3DIntensity > 0.9f);
            T.Check("severity is the asset value, not an invented one", Mathf.Abs(wm.Severity - 1.0f) < 0.01f);

            wm.Strike();   // don't wait 15-60 s for a natural one
            yield return Ticks(1);
            T.Check("a strike is survivable and the manager stays alive", GodotObject.IsInstanceValid(wm));
        }
    }

    // The console surface people will actually use.
    public class WeatherConsoleCommands : GameTest
    {
        public override string Name => "weather.console";
        public override IEnumerable<Step> Run()
        {
            var dn = new DayNightCycle { DayLength = 120f, VisualsEnabled = false };
            World.AddChild(dn);
            var wm = WeatherManager.Attach(World, null, dn, seed: 77);
            yield return Ticks(2);

            T.Check("`weather heavy` is accepted", wm.ApplyCommand("heavy"));
            yield return Ticks(2);
            T.Check("heavy rain selected", wm.Sim.Active?.Name == "Heavy Rain");

            T.Check("`weather clear` is accepted", wm.ApplyCommand("clear"));
            yield return Ticks(2);
            T.Check("cleared", !wm.IsRaining);

            T.Check("`weather rain` is accepted", wm.ApplyCommand("rain"));
            yield return Ticks(2);
            T.Check("light rain selected", wm.Sim.Active?.Name == "Default Rain");

            T.Check("garbage is rejected rather than silently ignored", !wm.ApplyCommand("banana"));
        }
    }
}
