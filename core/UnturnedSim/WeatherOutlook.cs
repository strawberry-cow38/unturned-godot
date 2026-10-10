using System;

namespace SDG.Unturned
{
    /// <summary>What kind of day it is, weather-wise — decided ONCE per day from the world seed and the
    /// season, so it can be asked about BEFORE it happens.
    ///
    /// Master 2026-10-10: "wire weather to actually exist as more than just a command. consider time of
    /// year, etc. the radio/stereo prop should get a billboard text that says the weather for today and
    /// tomorrow."
    ///
    /// ⭐⭐ A FORECAST HAS TO BE TRUE, so this does not PREDICT the sim — it DECIDES for it. WeatherSim picks
    /// its next weather off a sequential RNG with continuous timers, which means "what happens tomorrow" is
    /// not answerable without simulating a day forward, and any answer that drifted would make the radio a
    /// liar. Instead the day's TYPE is a pure function of (seed, day): the radio and the sim read the same
    /// function, so they cannot disagree. The sim still owns WHEN within the day it arrives and for how long.
    ///
    /// ⭐ SEASON COMES FROM THE MODEL THAT ALREADY EXISTS. WorldTemperature.SeasonPhase is a cosine from -1
    /// at the coldest day to +1 at the warmest, already driving ambient temperature; weather simply consults
    /// it rather than inventing a second, drifting notion of winter.</summary>
    public static class WeatherOutlook
    {
        /// <summary>Chance of a wet day at the two ends of the year. Winter is wetter — the only claim being
        /// made here, and it is a dial rather than a derivation.</summary>
        public const float SummerWetChance = 0.28f, WinterWetChance = 0.68f;

        /// <summary>How hard the season tilts WHICH weather a wet day gets. 0 = season changes only how OFTEN
        /// it rains, not how hard. 0.6 = a winter storm is noticeably likelier than a winter drizzle.</summary>
        public const float SeverityTilt = 0.6f;

        public struct Day
        {
            public bool Wet;
            public int ScheduleIndex;     // index into the schedule array; -1 when clear
            public int TypeIndex;         // index into the types array; -1 when clear
            public string Name;           // the weather's own name, or "Clear"
            public float Severity;        // 0..1 across the types on offer, 0 when clear
            public bool Snow;             // it falls as snow rather than rain
        }

        /// <summary>⚠ FNV-1a over the two values, NOT `new Random(seed + day)`. Seeds that differ by one give
        /// System.Random very similar early sequences, so consecutive days would come out correlated and a
        /// week would read as one long drizzle.</summary>
        static uint Hash(int seed, int day, uint salt)
        {
            uint h = 2166136261u;
            unchecked
            {
                foreach (uint part in new[] { (uint)seed, (uint)day, salt })
                    for (int b = 0; b < 4; b++) { h ^= (part >> (b * 8)) & 0xFF; h *= 16777619u; }
            }
            return h;
        }

        static float Unit(int seed, int day, uint salt) => (Hash(seed, day, salt) & 0xFFFFFF) / (float)0x1000000;

        /// <summary>How severe each type is relative to the others on offer, 0..1, from the authored
        /// WindMain + FogDensity. Derived rather than listed so adding a weather type needs no edit here.</summary>
        static float SeverityOf(WeatherType t, float windMax, float fogMax)
        {
            float w = windMax > 0f ? t.WindMain / windMax : 0f;
            float f = fogMax > 0f ? t.FogDensity / fogMax : 0f;
            return Math.Clamp((w + f) * 0.5f, 0f, 1f);
        }

        /// <summary>Below this day-mean temperature a wet day falls as SNOW rather than rain.</summary>
        public const float FreezingC = 0.5f;

        /// <summary>The outlook for `day`. `seasonPhase` is WorldTemperature.SeasonPhase(dayOfYear).
        ///
        /// ⭐ `dayMeanC` + `snowScheduleIndex` make a cold wet day fall as snow. The TEMPERATURE decides the
        /// FORM, which is the one thing the season model was already able to answer and nothing asked it:
        /// WorldTemperature has driven ambient warmth since it was written, and precipitation never consulted
        /// it. Pass snowScheduleIndex < 0 (the default) for a world with no snow type and nothing changes.</summary>
        public static Day ForDay(int seed, int day, WeatherType[] types, WeatherSchedule[] schedule,
                                 float seasonPhase, float dayMeanC = 99f, int snowScheduleIndex = -1)
        {
            var clear = new Day { Wet = false, ScheduleIndex = -1, TypeIndex = -1, Name = "Clear", Severity = 0f };
            if (types == null || schedule == null || types.Length == 0 || schedule.Length == 0) return clear;

            // winter (phase -1) -> WinterWetChance, summer (+1) -> SummerWetChance
            float wetChance = SummerWetChance + (WinterWetChance - SummerWetChance) * (1f - seasonPhase) * 0.5f;
            if (Unit(seed, day, 0x9E3779B9u) >= wetChance) return clear;

            // ⚠ FORM BEFORE FLAVOUR. A freezing wet day is snow, full stop -- it does NOT roll among the rain
            // variants first and then get converted, because that would make the weighting below decide
            // something it never sees the result of, and a "Tempest Rain" at -8 C would still be a lie.
            if (dayMeanC <= FreezingC && snowScheduleIndex >= 0 && snowScheduleIndex < schedule.Length)
            {
                int sti = schedule[snowScheduleIndex].TypeIndex;
                return new Day
                {
                    Wet = true,
                    ScheduleIndex = snowScheduleIndex,
                    TypeIndex = sti,
                    Name = sti >= 0 && sti < types.Length ? types[sti].Name : "Snowfall",
                    Severity = 0.6f,
                    Snow = true,
                };
            }

            float windMax = 0f, fogMax = 0f;
            foreach (var t in types) { windMax = MathF.Max(windMax, t.WindMain); fogMax = MathF.Max(fogMax, t.FogDensity); }

            // Base weight keeps each entry's AUTHORED rarity (a type the schedule calls for every 9-18 cycles
            // stays rarer than one it calls every 2.3-5.6), then the season tilts toward or away from severity.
            float total = 0f;
            var weights = new float[schedule.Length];
            for (int i = 0; i < schedule.Length; i++)
            {
                var s = schedule[i];
                float meanFreq = MathF.Max(0.01f, (s.MinFrequency + s.MaxFrequency) * 0.5f);
                float sev = s.TypeIndex >= 0 && s.TypeIndex < types.Length
                          ? SeverityOf(types[s.TypeIndex], windMax, fogMax) : 0.5f;
                float tilt = 1f + SeverityTilt * sev * -seasonPhase;    // winter: boost severe, summer: damp
                weights[i] = MathF.Max(0.0001f, (1f / meanFreq) * MathF.Max(0.05f, tilt));
                total += weights[i];
            }

            float r = Unit(seed, day, 0x85EBCA6Bu) * total;
            int pick = schedule.Length - 1;
            for (int i = 0; i < schedule.Length; i++) { r -= weights[i]; if (r <= 0f) { pick = i; break; } }

            int ti = schedule[pick].TypeIndex;
            return new Day
            {
                Wet = true,
                ScheduleIndex = pick,
                TypeIndex = ti,
                Name = ti >= 0 && ti < types.Length ? types[ti].Name : "Rain",
                Severity = ti >= 0 && ti < types.Length ? SeverityOf(types[ti], windMax, fogMax) : 0.5f,
            };
        }
    }
}
