using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

// World-list rows shared by the owner snapshot (client replicas) and the
// dedicated host snapshot (server registries). Both sides use the same core
// replication types, so native host systems read one row shape whichever
// authority produced it. Moved verbatim from State().
public sealed partial class BridgeHost
{
    /// <summary>v80 System9 tail on a remote: the host's u8 part fraction against the same spec maximum the
    /// native registration gives the host (vehicle.rs `registration`: engine Health*10*0.4, rotors
    /// MainRotorHp/TailRotorHp or Health*0.45/0.28 on helicopters only). Null on a host, whose exact
    /// VehicleServices values are used instead.</summary>
    float? RemotePart(VehicleReplication.VehicleEntity e, int part)
    {
        if (_vehicles != null || _catalog?.Mounted.For(e.TypeId)?.Source is not { } row || !row.TryGetProperty("spec", out var spec)) return null;
        float health = Float(spec, "Health");
        bool heli = spec.TryGetProperty("Heli", out var h) && h.ValueKind == JsonValueKind.True;
        float maximum = part switch {
            0 => health * 10f * .4f,
            1 => heli ? (Float(spec, "MainRotorHp") is var m && m > 0 ? m : health * .45f) : 0f,
            _ => heli ? (Float(spec, "TailRotorHp") is var t && t > 0 ? t : health * .28f) : 0f,
        };
        return VehicleReplication.PartHealth(part switch { 0 => e.EnginePart, 1 => e.MainRotorPart, _ => e.TailRotorPart }, maximum);
    }
    /// <summary>BodyCombatServices.BurnSeconds from the replicated burn tick (-1 = not burning, as on the host).</summary>
    float? RemoteBurnSeconds(VehicleReplication.VehicleEntity e) => _bodies != null || _client == null ? null
        : e.BurnTick is int burn ? (_client.Applier.LastAppliedServerTick - burn) * (float)SimClock.FixedDelta : -1f;

    object[] WorldItemRows(WorldItemReplication items) => items.All.Select(e => new { netId = e.NetIdValue, itemId = e.ItemId, amount = e.Amount,
        quality = e.Quality, position = Array(e.Pos), settled = e.Settled,
        stuckDirection = _warheads?.PoseOf(e.NetIdValue)?.Direction, stuckNormal = _warheads?.PoseOf(e.NetIdValue)?.Normal,
        stuckEuler = _warheads?.PoseOf(e.NetIdValue)?.Euler }).ToArray();
    object[] VehicleMountRows(VehicleMountReplication mounts) => mounts.All.Select(v => new { vehicleNetId = v.VehicleNetId, headOut = v.HeadOut, gunnerRise = v.GunnerRise,
        mounts = v.Mounts.Select((m,index) => new { index, yaw = m.Yaw, pitch = m.Pitch, ammo = m.Ammo, cooldownSeconds = m.CooldownSeconds, crewAlive = m.CrewAlive }).ToArray() }).ToArray();
    object[] DeployableRows(DeployableReplication deployables) => deployables.All.Select(e => new { netId = e.NetIdValue, defId = e.DefId, position = Array(e.Pos), yaw = e.YawDegrees, mountUp = e.MountUp, health = e.Health, fuel = e.Fuel, energy = e.Energy, wind = e.Wind, onFire = e.OnFire, burnSeconds = _bodies?.BurnSeconds(false, e.NetIdValue), wreckSalvageable = _bodies?.Salvageable(false, e.NetIdValue), toggledOn = e.ToggledOn }).ToArray();
    object[] VehicleRows(VehicleReplication vehicles) => vehicles.All.Select(e => new { netId = e.NetIdValue, typeId = e.TypeId, variant = e.Variant,
        position = Array(e.Pos), yaw = e.YawDegrees, eulerDegrees = Array(new(e.PitchDegrees, e.YawDegrees, e.RollDegrees)),
        linVel = Array(e.LinVel), angVel = Array(e.AngVel), steerDegrees = e.SteerSigned,
        fuel = _vehicles?.FuelFor(e.NetIdValue, e.Fuel) ?? e.Fuel, health = e.Health, battery = _vehicleLights?.BatteryFor(e.NetIdValue, e.Battery) ?? e.Battery, flags = e.Flags, highBeam = e.HighBeam, lightbarPattern = e.LightbarPattern, lampMask = e.LampMask, brokenLampMask = e.BrokenLampMask, glassMask = e.GlassMask, brokenGlassMask = e.BrokenGlassMask, tireMask = e.TireMask, hasBiFoldDoor = e.HasBiFoldDoor, doorOpen = e.DoorOpen, doorFold = e.DoorFold, engineHealth = _vehicles?.EngineHealth(e.NetIdValue) ?? RemotePart(e, 0),
        mainRotorHealth = _vehicles?.MainRotorHealth(e.NetIdValue) ?? RemotePart(e, 1), tailRotorHealth = _vehicles?.TailRotorHealth(e.NetIdValue) ?? RemotePart(e, 2),
        engineDrowned = _vehicles?.EngineDrowned(e.NetIdValue) ?? e.EngineDrowned,
        onFire = _bodies?.OnFire(true, e.NetIdValue) ?? (e.FuseLit || e.BurnTick.HasValue),
        burnSeconds = _bodies?.BurnSeconds(true, e.NetIdValue) ?? RemoteBurnSeconds(e), wreckSalvageable = _bodies?.Salvageable(true, e.NetIdValue) ?? RemoteBurnSeconds(e) >= BodyRules.CoolSeconds,
        driverPlayerId = e.DriverPlayerId, passengers = e.Passengers, seatCount = VehicleSeatCount(e),
        coupledTrailerNetId = e.CoupledTrailerNetId, towedNetId = e.TowedNetId, towRestLen = _vehicleTows?.RestLen(e.NetIdValue, e.TowRestLen) ?? e.TowRestLen, paintRgb = e.PaintRgb, poppedTireMask = e.PoppedTireMask,
        recovCounter = _vehicleRecov.GetValueOrDefault(e.NetIdValue) }).ToArray();
}
