using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Owner-side body temperature, where the source computes it: the client shell
/// (PlayerController.TemperatureTick) runs the engine-free PlayerTemperatureSim off its own world
/// probes and streams only the resulting band to the server on PlayerStateCommand.TempBand (v59),
/// which is what the server's vitals step reads. The native client supplies the two probes that
/// need its physics world -- ShelterProbe overhead cover and ThermalField's line-of-sight heat sum --
/// and this adapter supplies ambient/weather/exertion/clothing exactly as the source does.</summary>
public sealed partial class BridgeHost
{
    /// <summary>PlayerController.TemperatureProbeSeconds: probes at 4 Hz, the body every tick.</summary>
    const float TemperatureProbeSeconds = 0.25f;
    /// <summary>PlayerController.ThermometerId.</summary>
    const ushort ThermometerId = 9346;

    PlayerTemperatureSim _temperature = new();
    float _tempProbeTimer, _tempAmbientC = 20f, _tempSourceC;
    bool _tempRaining, _tempSheltered, _tempExerting;
    bool _probeSheltered; float _probeSourceC;

    void ResetTemperature()
    {
        _temperature = new(); _tempProbeTimer = 0; _tempAmbientC = 20f; _tempSourceC = 0;
        _tempRaining = _tempSheltered = _tempExerting = false; _probeSheltered = false; _probeSourceC = 0;
    }

    /// <summary>Latest native world probes; latched by the next 4 Hz probe like the source's cached
    /// _tempSheltered/_tempSourceC.</summary>
    void ReadTemperatureProbe(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("temperatureProbe", out var probe)) return;
        _probeSheltered = Bool(probe, "sheltered");
        _probeSourceC = ThermalFalloff.Clamp(Float(probe, "sourceC"));
    }

    bool OwnerSeated()
    {
        ushort me = _client.PlayerId;
        if (_client.Vehicles.All.Any(v => v.DriverPlayerId == me || (v.Passengers?.Contains(me) ?? false))) return true;
        foreach (var seat in _client.InteractableState.ReplicaSeats) if (seat.Value == me) return true;
        return false;
    }

    /// <summary>PlayerController.UpdateVitals → TemperatureTick, before the state command is sent.</summary>
    void TemperatureTick()
    {
        if (_client == null || _client.State != NetSessionState.Connected) return;
        ushort me = _client.PlayerId;
        // UpdateVitals returns before TemperatureTick while dead.
        if (_client.CombatState.TryGet(me, out var life) && !life.Alive) return;
        _client.Vitals.TryGet(me, out var vitals);
        // SEATED IS NOT SPRINTING; broken legs and a major dose cannot sprint either.
        bool exerting = _movingInput && _movement.Stance == EPlayerStance.SPRINT
            && !(vitals?.Broken ?? false) && !(vitals?.Sim.MajorlyIrradiated ?? false) && !OwnerSeated();
        _tempExerting = exerting;
        float dt = (float)SimClock.FixedDelta;
        _tempProbeTimer -= dt;
        if (_tempProbeTimer <= 0f)
        {
            _tempProbeTimer = TemperatureProbeSeconds;
            long tick = _server?.Session.CurrentTick ?? _client.Applier.LastAppliedServerTick;
            // No accepted day/weather (a remote client today: dayNumber/weather wire gaps) uses the
            // source's "no cycle" fallback: day 0, no weather.
            int day = _environment?.DayAt(_client.Clock, tick) ?? 0;
            float tod = _client.Clock.TimeOfDayAt(tick);
            var weather = _environment?.Sim;
            float blend = weather?.BlendAlpha ?? 0f;   // WeatherManager.RainIntensity IS the blend alpha
            _tempAmbientC = WorldTemperature.AmbientC(WorldTemperature.DayOfYear(WorldTemperature.StartDayOfYear, day), tod,
                WorldTemperature.WeatherOffsetC(weather?.Active?.Name, blend));
            _tempSourceC = _probeSourceC;
            _tempRaining = weather?.IsRaining ?? false;
            _tempSheltered = _tempRaining && _probeSheltered;
        }
        _client.Inventories.TryGet(me, out var owned);
        var inv = owned?.Inventory;
        _temperature.Step(_tempAmbientC, _tempSourceC, exerting, _tempRaining, _tempSheltered,
            inv?.ProofsWater ?? false, inv?.InsulationColdC ?? 0f, inv?.InsulationHeatC ?? 0f, dt);
    }

    byte TemperatureBand => (byte)_temperature.CurrentBand;

    object TemperatureState()
    {
        if (_client == null) return null;
        _client.Inventories.TryGet(_client.PlayerId, out var owned);
        return new {
            bodyC = _temperature.BodyC, comfort = _temperature.Comfort, band = _temperature.CurrentBand.ToString(),
            wetness = _temperature.Wetness, hasThermometer = (owned?.Inventory?.getItemCount(ThermometerId) ?? 0) > 0,
            ambientC = _tempAmbientC, sourceC = _tempSourceC, raining = _tempRaining, sheltered = _tempSheltered, exerting = _tempExerting };
    }

    /// <summary>HUD radiation status (PlayerController.InDeadzone = DeadzoneSeconds &gt; 0): the host's
    /// own DeadzoneSim clock for the owner, the same rule the source's DeadzoneField publishes.</summary>
    bool OwnerInDeadzone() => _server != null && _client != null && _server.Deadzones.SecondsInside(_client.PlayerId) > 0f;
}
