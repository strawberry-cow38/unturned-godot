using Godot;

namespace UnturnedGodot
{
    // Global client measurement-unit setting + formatting. Retail only has a binary OptionsSettings.metric
    // (m vs yd, see MeasurementTool.cs); this is the port's richer version -- Metric / Imperial / BOTH (default),
    // per master. "Both" shows e.g. "100 m / 109 yd". Conversion factors are retail's (MeasurementTool):
    // MtoYd 1.09361, KPHToMPH /1.609344, KtoM (km->mi) 0.621371; volume L->US gal 0.264172; temp C->F *9/5+32.
    public enum MeasurementSystem { Metric, Imperial, Both }

    public static class Units
    {
        public static MeasurementSystem System = MeasurementSystem.Both;   // client setting; default Both (persistence/menu = follow-up)

        static string Pair(string metric, string imperial) => System switch
        {
            MeasurementSystem.Metric => metric,
            MeasurementSystem.Imperial => imperial,
            _ => $"{metric} / {imperial}",
        };

        // Weapon range / lengths: metres <-> yards.
        public static string Length(float metres) =>
            Pair($"{Mathf.RoundToInt(metres)} m", $"{Mathf.RoundToInt(metres * 1.09361f)} yd");

        // Speed given in km/h: km/h <-> mph.
        public static string SpeedKph(float kph) =>
            Pair($"{Mathf.RoundToInt(kph)} km/h", $"{Mathf.RoundToInt(kph / 1.609344f)} mph");

        // Speed given in m/s (internal physics unit) -> km/h / mph.
        public static string SpeedMs(float ms) => SpeedKph(ms * 3.6f);

        // Long travel distance in km: km <-> miles.
        public static string Distance(float km) =>
            Pair($"{km:0.0} km", $"{km * 0.621371f:0.0} mi");

        // Fluid volume in litres: L <-> US gallons.
        public static string Volume(float litres) =>
            Pair($"{litres:0.#} L", $"{litres * 0.264172f:0.#} gal");

        // Temperature in Celsius: C <-> F.
        public static string Temperature(float celsius) =>
            Pair($"{Mathf.RoundToInt(celsius)} °C", $"{Mathf.RoundToInt(celsius * 9f / 5f + 32f)} °F");

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
