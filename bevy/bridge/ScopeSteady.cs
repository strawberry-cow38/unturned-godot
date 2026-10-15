using System.Text.Json;
using SDG.Unturned;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    // Same predictive shell as PlayerController: the shared class determines
    // presentation/accepted button, while server vitals own the replicated cost.
    readonly ScopeSteadySim _scopeSteady = new();
    bool _wantsSteady;

    void ReadSteady(JsonElement input)
    {
        if (input.TryGetProperty("steadyKey", out _))
            _wantsSteady = Bool(input, "steadyKey") && Bool(input, "aiming") && Float(input, "scopeZoom", 1) > 1;
    }

    void StepSteady()
    {
        if (!_client.Vitals.TryGet(_client.PlayerId, out var vitals)) return;
        if (_client.CombatState.TryGet(_client.PlayerId, out var life) && !life.Alive)
        {
            _scopeSteady.Reset(); _wantsSteady = false; return;
        }
        bool seated = _server.VehicleHost.IsSeated(_client.PlayerId) || _server.Interactables.IsSeated(_client.PlayerId);
        float oxygen = vitals.Sim.Oxygen;
        _scopeSteady.Step(_wantsSteady && !seated, ref oxygen, (float)SimClock.FixedDelta);
        // Prediction must keep the drain between snapshot arrivals, exactly as
        // PlayerController does. Discarding this loses the floor/lockout behind
        // the quantized oxygen replica and leaves an endlessly held scope steady.
        // Only the separate server vitals mutate authoritative oxygen.
        vitals.Sim.Oxygen = oxygen;
    }

    object SteadyState() => new { active = _scopeSteady.Steadying, blend = _scopeSteady.Blend,
        rateScale = _scopeSteady.SwayRateScale, lockout = _scopeSteady.Lockout };
}
