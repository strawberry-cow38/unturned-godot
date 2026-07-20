using System.Collections.Generic;

namespace UnturnedGodot.Net
{
    // A2 (SP/MP-unify): the server-side fuel-station seam the ExtractFuel choke reads through. The game's
    // GasStationServer implements it (it owns the authoritative absolute per-station FluidTanks + the
    // pumpNetId -> stationId map, all server-side); L0 tests install a tiny fake. The interface is deliberately
    // dumb DATA -- the drain math, the min(canSpace, remaining) clamp, the recomputed percent, and the atomic
    // fan-out across the station's pumps all live in ServerTransactions.OnExtractFuel (the ONE mutation point),
    // so a fake station can't hide a bug in that logic. Core cannot see the game FluidTank/StationFuel types,
    // so the tank stays behind this seam. The absolute 8000 L tank NEVER crosses the wire; only the 0..100
    // percent (entity.Fuel) replicates.
    public interface IFuelStation
    {
        // Is this netId a registered gas pump? out its station id (pumps sharing a station share one tank).
        bool TryGetStation(uint pumpNetId, out int stationId);

        // Litres left in the absolute station tank (server-authoritative; never replicated).
        float Remaining(int stationId);

        // The absolute station tank size, for the recomputed 0..100 percent (Remaining / Capacity * 100).
        float Capacity(int stationId);

        // Drain the absolute station tank by `requested`; returns how much actually came out (clamped to what's left).
        float Drain(int stationId, float requested);

        // Every pump netId sharing this station -- the choke writes the recomputed percent onto ALL of them in
        // ONE tick (atomic fan-out; a divergent per-pump fill would desync). Stable order (placement order).
        IReadOnlyList<uint> Pumps(int stationId);
    }
}
