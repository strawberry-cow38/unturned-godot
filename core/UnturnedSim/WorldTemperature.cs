using System;

namespace SDG.Unturned
{
    /// <summary>THE WORLD'S AMBIENT TEMPERATURE, in degrees Celsius (strawberry 2026-09-10: "wire a universal
    /// temperature on the map; changes with weather, time of day, season").
    ///
    /// Engine-free on purpose: it takes a day-of-year, a 0..1 time of day and a weather offset, and returns a
    /// number. Everything that makes those three hard to obtain -- the day counter, the scheduler, the node --
    /// lives elsewhere, so the curve itself is unit-testable at any date and hour without a running game.
    ///
    /// This is the port's own model, not a ripped one. Retail Unturned has no ambient temperature; the closest
    /// thing is per-biome clothing advice on the loading screen. So every constant below is a stated choice
    /// rather than an extracted value, and they are all in one place to be tuned.</summary>
    public static class WorldTemperature
    {
        /// <summary>Annual mean. The year swings +-SeasonAmplitude around this and the day +-DiurnalAmplitude
        /// around that, so the extremes are about -8 C on a winter night and +32 C on a summer afternoon before
        /// weather is applied.</summary>
        public const float BaseMeanC = 12f;
        public const float SeasonAmplitude = 12f;
        public const float DiurnalAmplitude = 8f;

        public const int DaysPerYear = 365;

        /// <summary>Coldest day of the year (northern-hemisphere mid-January) and the hour the ground is
        /// coldest. The daily minimum is at dawn rather than midnight because the ground keeps radiating heat
        /// all night -- that is why the curve is anchored here and not at 00:00.</summary>
        public const int ColdestDayOfYear = 15;
        public const float ColdestTimeOfDay01 = 0.208f;   // ~05:00

        /// <summary>Day-of-year 0..364 from the world's start date plus its elapsed-day counter
        /// (DayNightCycle.Day, which is monotonic and survives a save). Negative days are clamped rather than
        /// wrapped backwards: the counter never decrements, so a negative here means a caller bug, and silently
        /// wrapping it would put the world in a plausible-looking wrong season.</summary>
        public static int DayOfYear(int startDayOfYear, int elapsedDays)
        {
            if (elapsedDays < 0) elapsedDays = 0;
            int d = (startDayOfYear + elapsedDays) % DaysPerYear;
            return d < 0 ? d + DaysPerYear : d;
        }

        /// <summary>-1 at the coldest day, +1 at the warmest, cosine between. Six months of winter easing into
        /// six months of summer is the whole model; there is no separate spring/autumn state to get out of
        /// sync with anything.</summary>
        public static float SeasonPhase(int dayOfYear)
            => -MathF.Cos(2f * MathF.PI * (dayOfYear - ColdestDayOfYear) / DaysPerYear);

        /// <summary>-1 at the coldest hour, +1 twelve hours later. A cosine puts the maximum at ~17:00 rather
        /// than the ~15:00 a real diurnal curve peaks at -- the real one is asymmetric (fast morning warm-up,
        /// slow evening cool-down) and this is not. Deliberate: the asymmetry costs a piecewise curve and buys
        /// two hours of accuracy nobody can feel through a survival stat.</summary>
        public static float DiurnalPhase(float timeOfDay01)
            => -MathF.Cos(2f * MathF.PI * (timeOfDay01 - ColdestTimeOfDay01));

        /// <summary>The ambient temperature a player standing outside is exposed to.
        /// `weatherOffsetC` is what the active weather does to it -- see WeatherOffsetC.</summary>
        public static float AmbientC(int dayOfYear, float timeOfDay01, float weatherOffsetC = 0f)
            => BaseMeanC
             + SeasonAmplitude * SeasonPhase(dayOfYear)
             + DiurnalAmplitude * DiurnalPhase(timeOfDay01)
             + weatherOffsetC;

        /// <summary>What an active weather archetype does to the ambient, scaled by its blend alpha so it eases
        /// in and out with the storm exactly as the wind and fog already do.
        ///
        /// Keyed by NAME rather than by a field on WeatherType, because that struct is a 1:1 port of the ripped
        /// WeatherAsset and its own comment says "values come from the ripped assets, nothing invented". A
        /// temperature offset IS invented -- there is no such key in the assets -- so it does not belong in
        /// there pretending to be extracted. Unknown names read 0, which is the correct behaviour for a weather
        /// this port has not formed an opinion about.</summary>
        public static float WeatherOffsetC(string weatherName, float blendAlpha)
        {
            if (string.IsNullOrEmpty(weatherName)) return 0f;
            if (blendAlpha < 0f) blendAlpha = 0f; else if (blendAlpha > 1f) blendAlpha = 1f;
            float raw = weatherName.ToLowerInvariant() switch
            {
                "rain"  => -5f,    // wet and windy: the single biggest routine swing
                "storm" => -8f,
                "snow"  => -10f,
                "blizzard" => -14f,
                "fog"   => -2f,    // still air under cloud; damp rather than cold
                _       => 0f,
            };
            return raw * blendAlpha;
        }
    }
}
