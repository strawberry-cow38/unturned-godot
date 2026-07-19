using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Shared underground fuel tanks per gas STATION (master, PZ-style): pumps with the same stationId draw from ONE big
    // tank, so a station's several pumps share its reserve. A pump's stationId is set in the map editor, or auto-derived
    // from position (pumps in the same ~30m cell = one station) for the baked PEI pumps. Cleared on each world (re)build.
    public static class StationFuel
    {
        public const float StationCapacity = 8000f;   // shared tank size per station (PZ pumps are 1k-14k units)
        static readonly Dictionary<int, FluidTank> _tanks = new();

        public static FluidTank Tank(int stationId)
        {
            if (!_tanks.TryGetValue(stationId, out var t)) { t = new FluidTank(FluidType.Fuel, StationCapacity); _tanks[stationId] = t; }
            return t;
        }

        // auto-group baked pumps: same ~30m cell -> same station (a station's pumps sit within a few metres of each other)
        public static int StationIdFor(Vector3 pos)
        {
            int cx = Mathf.FloorToInt(pos.X / 30f), cz = Mathf.FloorToInt(pos.Z / 30f);
            return cx * 100003 + cz;   // spatial hash -> a stable station id
        }

        public static void Reset() => _tanks.Clear();   // fresh tanks on each world (re)build
    }
}
