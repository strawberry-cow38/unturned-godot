using UnturnedGodot.Net;

namespace UnturnedBevy;

// World-list rows shared by the owner snapshot (client replicas) and the
// dedicated host snapshot (server registries). Both sides use the same core
// replication types, so native host systems read one row shape whichever
// authority produced it. Moved verbatim from State().
public sealed partial class BridgeHost
{
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
        fuel = _vehicles?.FuelFor(e.NetIdValue, e.Fuel) ?? e.Fuel, health = e.Health, battery = _vehicleLights?.BatteryFor(e.NetIdValue, e.Battery) ?? e.Battery, flags = e.Flags, highBeam = e.HighBeam, lightbarPattern = e.LightbarPattern, lampMask = e.LampMask, brokenLampMask = e.BrokenLampMask, glassMask = e.GlassMask, brokenGlassMask = e.BrokenGlassMask, tireMask = e.TireMask, hasBiFoldDoor = e.HasBiFoldDoor, doorOpen = e.DoorOpen, doorFold = e.DoorFold, engineHealth = _vehicles?.EngineHealth(e.NetIdValue),
        mainRotorHealth = _vehicles?.MainRotorHealth(e.NetIdValue), tailRotorHealth = _vehicles?.TailRotorHealth(e.NetIdValue), engineDrowned = _vehicles?.EngineDrowned(e.NetIdValue), onFire = _bodies?.OnFire(true, e.NetIdValue),
        burnSeconds = _bodies?.BurnSeconds(true, e.NetIdValue), wreckSalvageable = _bodies?.Salvageable(true, e.NetIdValue),
        driverPlayerId = e.DriverPlayerId, passengers = e.Passengers, seatCount = VehicleSeatCount(e),
        coupledTrailerNetId = e.CoupledTrailerNetId, towedNetId = e.TowedNetId, towRestLen = _vehicleTows?.RestLen(e.NetIdValue, e.TowRestLen) ?? e.TowRestLen, paintRgb = e.PaintRgb, poppedTireMask = e.PoppedTireMask,
        recovCounter = _vehicleRecov.GetValueOrDefault(e.NetIdValue) }).ToArray();
}
