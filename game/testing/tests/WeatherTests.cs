using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // L1 for weather: the scheduler itself is covered engine-free (WeatherSimTests), so these guard the wiring
    // only the engine can answer -- that the sim actually reaches the rain overlay and the day/night overcast
    // flag, and that lightning is gated to the weather that carries it.

    // Forcing rain drives the overlay; clearing it turns the overlay back off.
    public class WeatherDrivesOverlay : GameTest
    {
        public override string Name => "weather.drives_overlay";
        public override IEnumerable<Step> Run()
        {
            var dn = new DayNightCycle { DayLength = 120f, VisualsEnabled = false };
            World.AddChild(dn);
            var overlay = new RainOverlay { Cycle = dn, Raining = false };
            World.AddChild(overlay);
            var wm = WeatherManager.Attach(World, overlay, dn, seed: 12345);
            yield return Ticks(2);

            T.Check("starts dry (no coin-flip rain at world build any more)", !overlay.Raining);

            wm.Sim.ForecastImmediately(0);   // Default Rain
            yield return Ticks(2);
            // 20 s fade-in: a couple of ticks in, it should be raining but nowhere near full
            T.Check($"overlay switched on as the weather starts (intensity {overlay.Intensity:0.000})", overlay.Raining);
            T.Check("still fading in, not slammed to full", overlay.Intensity < 0.9f);

            // run out the fade-in. PEI's active window (0.05-0.15 cycles) is SHORTER than the asset's 20 s
            // fade-in on this port's 120 s day, so the ramps get proportionally split and the peak lands at the
            // middle of the shower -- wait for the peak rather than assuming a fixed tick count.
            yield return Until(() => wm.Sim.BlendAlpha > 0.9f, 25);
            // Default Rain's severity is its asset Fog_Density 0.7, so a fully-committed LIGHT shower tops out
            // around 0.7 -- not 1.0. Heavy Rain is the one that reaches full (asserted below).
            T.Check($"light rain settles near its 0.7 severity (intensity {overlay.Intensity:0.00})",
                    overlay.Intensity > 0.6f && overlay.Intensity < 0.8f);
            T.Check("day/night flipped to overcast", dn.Overcast);

            wm.Sim.Clear();
            yield return Ticks(2);
            T.Check("clearing the weather turns the overlay off", !overlay.Raining && overlay.Intensity <= 0.001f);
            T.Check("overcast released", !dn.Overcast);
        }
    }

    // Lightning belongs to Heavy Rain only (Default Rain's .asset has no Has_Lightning), and a strike must
    // actually reach the screen.
    public class WeatherLightningGating : GameTest
    {
        public override string Name => "weather.lightning_gating";
        public override IEnumerable<Step> Run()
        {
            var dn = new DayNightCycle { DayLength = 120f, VisualsEnabled = false };
            World.AddChild(dn);
            var overlay = new RainOverlay { Cycle = dn, Raining = false };
            World.AddChild(overlay);
            var wm = WeatherManager.Attach(World, overlay, dn, seed: 999);
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

            // the gap the render caught: heavy rain must actually LOOK heavier than light rain, not just carry
            // different scalars. Severity comes from the assets' Fog_Density (0.7 vs 1.0).
            float heavyIntensity = overlay.Intensity;
            T.Check($"heavy rain renders denser than light rain ({heavyIntensity:0.00} vs light's ~0.70)",
                    heavyIntensity > 0.9f);
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
            var overlay = new RainOverlay { Cycle = dn, Raining = false };
            World.AddChild(overlay);
            var wm = WeatherManager.Attach(World, overlay, dn, seed: 77);
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
