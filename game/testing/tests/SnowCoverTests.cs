using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Snow lying on the ground and melting off it again (strawberry 2026-10-10: "after snowfall,
    /// the grass should fade into snow material. after it warms up, show melt back into grass").
    ///
    /// ⭐⭐ THE DESIGN CLAIM UNDER TEST IS THAT MELT IS POSSIBLE AT ALL. Coverage is a shader global the
    /// terrain blends with; the splatmap is never written. Had this repainted layer 2 to layer 6 the first
    /// half would look identical and the second half could never work, because the authored grass would be
    /// gone -- so "it melts back" is the assertion that distinguishes the two designs.</summary>
    public sealed class SnowCover : GameTest
    {
        public override string Name => "weather.snow_cover";

        static float Global() => (float)RenderingServer.GlobalShaderParameterGet("snow_cover");

        public override IEnumerable<Step> Run()
        {
            RainSystem3D.EnsureGlobals();
            // Probe the global itself before relying on it: a name that was never registered reads back as a
            // nil Variant, which casts to 0.0 and looks exactly like "the feature did not run".
            RenderingServer.GlobalShaderParameterSet("snow_cover", 0.5f);
            var probe = RenderingServer.GlobalShaderParameterGet("snow_cover");
            var rainProbe = RenderingServer.GlobalShaderParameterGet("rain_wetness");
            bool globalsLive = rainProbe.VariantType != Variant.Type.Nil;
            T.Check($"[info] shader globals available here: {globalsLive} "
                  + $"(rain_wetness {rainProbe.VariantType}, snow_cover {probe.VariantType})", true);
            RenderingServer.GlobalShaderParameterSet("snow_cover", 0f);
            int startWas = SDG.Unturned.WorldTemperature.StartDayOfYear;

            // Deep winter: at the coldest day of the year the mean is exactly BaseMeanC - SeasonAmplitude = 0 C.
            SDG.Unturned.WorldTemperature.StartDayOfYear = SDG.Unturned.WorldTemperature.ColdestDayOfYear;
            var dn = new DayNightCycle { DayLength = 100f, Time = 0.5f, Speed = 1f, VisualsEnabled = false };
            World.AddChild(dn);
            var wm = WeatherManager.Attach(World, null, dn, seed: 4242);
            wm.SeasonalOutlook = true;
            yield return Ticks(2);

            T.Check($"the cold day is at or below freezing ({wm.DayMeanC(0):0.0} C)",
                    wm.DayMeanC(0) <= SDG.Unturned.WeatherOutlook.FreezingC);
            T.Check($"a snow type exists ({WeatherManager.SnowTypeIndex})", WeatherManager.SnowTypeIndex >= 0);
            T.Check($"nothing is lying to begin with ({wm.SnowCover:0.000})", wm.SnowCover <= 0.001f);

            // ---- IT FALLS AND IT LIES ----------------------------------------------------------------------
            // Force the snow on rather than waiting for the schedule: the forecast machinery is tested next
            // door, and what is under test HERE is what lying snow does.
            T.Check("the snow weather can be forced on", wm.Sim.ForecastImmediately(WeatherManager.SnowTypeIndex));
            for (int i = 0; i < 400 && wm.SnowCover < 0.9f; i++) { wm.HubProcess(0.5); }
            yield return Ticks(1);
            T.Check($"snowing lays snow ({wm.SnowCover:0.00})", wm.SnowCover > 0.5f);
            // ⚠ ONLY WHERE THE RENDERING SERVER IS REAL. Headless Godot's dummy server registers no global
            // shader parameters at all -- rain_wetness reads Nil here too -- so asserting the round-trip would
            // be asserting the harness, not the feature. Checked when it CAN be, skipped loudly when it cannot.
            if (globalsLive)
                T.Check($"...and it reaches the shader ({Global():0.00})",
                        Mathf.Abs(Global() - wm.SnowCover) < 0.01f);
            else
                T.Check("[skipped] shader round-trip needs a real rendering server (headless has none)", true);
            T.Check("...and the 3D rain is in snow mode", wm.DebugRain3D == null || wm.DebugRain3D.Snowing);

            // ---- COLD AND CLEAR: IT STAYS ------------------------------------------------------------------
            // ⚠⚠ THE CONTROL THAT MAKES THE MELT TEST MEAN SOMETHING. If snow melted merely because the
            // snowfall stopped, the warm-melt below would pass without temperature being involved at all --
            // which is precisely the thing master asked for ("after it WARMS UP").
            wm.Sim.Clear();
            float heldAt = wm.SnowCover;
            for (int i = 0; i < 200; i++) wm.HubProcess(0.5);
            yield return Ticks(1);
            T.Check($"CONTROL: it stops snowing but stays freezing, so the snow STAYS "
                  + $"({heldAt:0.00} -> {wm.SnowCover:0.00})", wm.SnowCover > heldAt - 0.02f);

            // ---- WARM UP: IT MELTS -------------------------------------------------------------------------
            // Move the world to midsummer; the same code now runs downwards.
            SDG.Unturned.WorldTemperature.StartDayOfYear =
                (SDG.Unturned.WorldTemperature.ColdestDayOfYear + SDG.Unturned.WorldTemperature.DaysPerYear / 2)
                % SDG.Unturned.WorldTemperature.DaysPerYear;
            T.Check($"the warm day is well above freezing ({wm.DayMeanC(0):0.0} C)", wm.DayMeanC(0) > 10f);
            float meltFrom = wm.SnowCover;
            for (int i = 0; i < 2000 && wm.SnowCover > 0.001f; i++) wm.HubProcess(0.5);
            yield return Ticks(1);
            T.Check($"warming melts it back off ({meltFrom:0.00} -> {wm.SnowCover:0.00})", wm.SnowCover < 0.02f);
            if (globalsLive) T.Check($"...and the shader global came down with it ({Global():0.00})", Global() < 0.02f);

            // ---- RAIN IS NOT SNOW --------------------------------------------------------------------------
            // ⚠ CONTROL: a warm wet day must not lay anything. Without this, "snowing lays snow" is satisfied
            // by any precipitation at all accumulating.
            wm.Sim.ForecastImmediately(0);   // Default Rain
            for (int i = 0; i < 200; i++) wm.HubProcess(0.5);
            yield return Ticks(1);
            T.Check($"CONTROL: rain lays no snow ({wm.SnowCover:0.000})", wm.SnowCover <= 0.001f);

            // ---- HOW OFTEN IT CAN SNOW AT ALL --------------------------------------------------------------
            // Not a pass/fail of the feature so much as a measured FACT worth having in front of us: the
            // temperature model puts the coldest day at exactly 0 C, so only a narrow midwinter window can
            // ever be cold enough. If this number looks wrong, the dial to move is WorldTemperature, not here.
            int cold = 0;
            for (int d = 0; d < SDG.Unturned.WorldTemperature.DaysPerYear; d++)
                if (wm.DayMeanC(d) <= SDG.Unturned.WeatherOutlook.FreezingC) cold++;
            T.Check($"snow is a deep-winter event: {cold} of {SDG.Unturned.WorldTemperature.DaysPerYear} days "
                  + $"can be cold enough ({cold * 100 / SDG.Unturned.WorldTemperature.DaysPerYear}%)",
                    cold > 10 && cold < 120);

            SDG.Unturned.WorldTemperature.StartDayOfYear = startWas;
            RenderingServer.GlobalShaderParameterSet("snow_cover", 0f);
            wm.QueueFree(); dn.QueueFree();
        }
    }
}
