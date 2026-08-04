using Godot;

namespace UnturnedGodot
{
    // Global client measurement-unit setting + formatting. Retail only has a binary OptionsSettings.metric
    // (m vs yd, MeasurementTool.cs); this is the port's richer version -- Metric / Imperial / BOTH (default), per
    // master. **BOTH is not "show two units at once"** -- it's a MIXED convention where each quantity uses its
    // natural unit (master's spec): weapon RANGES=m, FLUIDS=L, SPEED=mph, TRAVEL distance=miles, TEMPERATURE=C.
    // Metric=all metric, Imperial=all imperial. Conversion factors are retail's (MtoYd 1.09361, KPHToMPH
    // /1.609344, KtoM km->mi 0.621371; volume L->US gal 0.264172; temp C->F *9/5+32).
    public enum MeasurementSystem { Metric, Imperial, Both }

    public static class Units
    {
        public static MeasurementSystem System = MeasurementSystem.Both;   // client setting; default Both (persistence/menu = follow-up)

        // bothMetric = which unit the mixed BOTH mode uses for THIS quantity (per master's per-category spec).
        static string Pick(string metric, string imperial, bool bothMetric) => System switch
        {
            MeasurementSystem.Metric => metric,
            MeasurementSystem.Imperial => imperial,
            _ => bothMetric ? metric : imperial,
        };

        // Weapon range / lengths: metres <-> yards. BOTH -> metres.
        public static string Length(float metres) =>
            Pick($"{Mathf.RoundToInt(metres)} m", $"{Mathf.RoundToInt(metres * 1.09361f)} yd", bothMetric: true);

        // Speed given in km/h: km/h <-> mph. BOTH -> mph.
        public static string SpeedKph(float kph) =>
            Pick($"{Mathf.RoundToInt(kph)} km/h", $"{Mathf.RoundToInt(kph / 1.609344f)} mph", bothMetric: false);

        // Speed given in m/s (internal physics unit) -> km/h / mph.
        public static string SpeedMs(float ms) => SpeedKph(ms * 3.6f);

        // Long travel distance in km: km <-> miles. BOTH -> miles.
        public static string Distance(float km) =>
            Pick($"{km:0.0} km", $"{km * 0.621371f:0.0} mi", bothMetric: false);

        // Fluid volume in litres: L <-> US gallons. BOTH -> litres.
        public static string Volume(float litres) =>
            Pick($"{litres:0.#} L", $"{litres * 0.264172f:0.#} gal", bothMetric: true);

        // Temperature in Celsius: C <-> F. BOTH -> Celsius.
        public static string Temperature(float celsius) =>
            Pick($"{Mathf.RoundToInt(celsius)} °C", $"{Mathf.RoundToInt(celsius * 9f / 5f + 32f)} °F", bothMetric: true);

        // For the scope range ladder: just the number+unit for a metre value, no "both" fallback spacing issues.
        public static string RangeLabel(int metres) => Length(metres);

        public static bool TrySet(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "metric": System = MeasurementSystem.Metric; return true;
                case "imperial": System = MeasurementSystem.Imperial; return true;
                case "both": System = MeasurementSystem.Both; return true;
                default: return false;
            }
        }
    }
}
