using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // A2 (SP/MP-unify): the server-authoritative fuel-station tanks behind the IFuelStation seam. Owns the
    // absolute per-station FluidTank (8000 L, full at world-build) + the pumpNetId -> stationId map, all
    // server-side -- the absolute litres NEVER cross the wire. The ExtractFuel choke (ServerTransactions.
    // OnExtractFuel) reads/drains through this seam; the only value that replicates is the 0..100 PERCENT
    // written onto each pump's entity.Fuel scalar (the pump def carries FuelCapacity=0, so entity.Fuel is
    // unused as litres and free to hold the percent). The consuming loopback + the dedicated server build one
    // of these from the server-placed gas-pump fixtures.
    public sealed class GasStationServer : IFuelStation
    {
        readonly Dictionary<uint, int> _pumpStation = new();          // pump NetId -> station id
        readonly Dictionary<int, FluidTank> _tanks = new();           // station id -> the shared absolute tank
        readonly Dictionary<int, List<uint>> _stationPumps = new();   // station id -> its pumps (placement order, stable fan-out)

        // Register a server-placed gas-pump fixture: map it to its station (a fresh full 8000 L tank on first
        // sight), then SEED the pump's replicated percent so joiners see a FULL pump before any extract (the
        // tank starts full but ServerPlace sets entity.Fuel=def.FuelCapacity=0). Deterministic: the stationId
        // is the SAME value WorldBuilder derived via StationFuel.StationIdFor(gpos).
        public void RegisterPump(DeployableReplication.DeployableEntity pump, int stationId, DeployableReplication deployables, long tick)
        {
            _pumpStation[pump.NetIdValue] = stationId;
            if (!_tanks.ContainsKey(stationId)) _tanks[stationId] = new FluidTank(FluidType.Fuel, StationFuel.StationCapacity);   // -1 amount -> starts FULL
            if (!_stationPumps.TryGetValue(stationId, out var list)) { list = new List<uint>(); _stationPumps[stationId] = list; }
            list.Add(pump.NetIdValue);
            // seed the replicated fill percent (full tank -> 100%); Health/OnFire pass through (a pump has neither)
            deployables.ServerSetScalars(pump.NetIdValue, pump.Health, Percent(stationId), pump.OnFire, tick);
        }

        // The current 0..100 fill percent of a station (Remaining / Capacity). Used to seed pumps on placement;
        // OnExtractFuel recomputes it identically after a drain and fans it out.
        public float Percent(int stationId)
            => _tanks.TryGetValue(stationId, out var t) && t.Capacity > 0f ? Mathf.Clamp(t.Amount / t.Capacity * 100f, 0f, 100f) : 0f;

        // ---- IFuelStation (the extract choke reads through these) ----
        public bool TryGetStation(uint pumpNetId, out int stationId) => _pumpStation.TryGetValue(pumpNetId, out stationId);
        public float Remaining(int stationId) => _tanks.TryGetValue(stationId, out var t) ? t.Amount : 0f;
        public float Capacity(int stationId) => _tanks.TryGetValue(stationId, out var t) ? t.Capacity : 0f;
        public float Drain(int stationId, float requested) => _tanks.TryGetValue(stationId, out var t) ? t.Drain(requested) : 0f;
        public IReadOnlyList<uint> Pumps(int stationId) => _stationPumps.TryGetValue(stationId, out var l) ? l : System.Array.Empty<uint>();
    }
}
