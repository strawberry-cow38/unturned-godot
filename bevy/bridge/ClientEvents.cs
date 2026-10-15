using SDG.NetTransport;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    void CreateClient(IClientTransport transport)
    {
        _npcView = new();
        _storageView = new();
        _craftQueue = default;
        _craftQueueTick = 0;
        _client = new NetWorldClient(transport, _name, contentHash: NetContent.Hash);
        DeployableNetSchema.RegisterAll(_client.Deployables.Schema);
        WorldServices.RegisterCrops(_client.Crops.Schema);
        _client.PlayerRecov += e => { _position = e.Pos; _velocity = e.Vel; _recov = e.RecovCounter; _movement.Velocity = WorldToLocal(_velocity, _yaw); _correctionVersion++; };
        _client.ConsoleResult += e => Emit("console", e);
        _client.HitConfirmed += e => Emit("hit", e);
        _client.CraftQueue_ += e => { _craftQueue = e; _craftQueueTick = _network.CurrentTick; Emit("craftQueue", e); };
        _client.PlayerFired += e => {
            var facts=_weapons?.TakeShot(e.PlayerId,e.Gun);
            Emit("fire",new{playerId=e.PlayerId,origin=e.Origin,dir=e.Dir,gun=e.Gun,
                itemId=facts?.ItemId,quality=facts?.Quality,handlingPenalty=facts?.HandlingPenalty,
                recoilMultiplier=facts?.RecoilMultiplier,selectionVersion=facts?.SelectionVersion,acceptedTick=facts?.Tick,
                barrelId=facts?.BarrelId,silenced=facts?.Silenced,barrelSilenced=facts?.BarrelSilenced,shootClip=facts?.ShootClip,shotVolume=facts?.ShotVolume,
                muzzleVelocity=facts?.MuzzleVelocity,barrelBroken=facts?.BarrelBroken});
        };
        _client.MountedShot += ReceiveMountedShot;
        _client.MountedFlight += ReceiveMountedFlight;
        _client.PlayerMeleed += e => {
            var facts=_weapons?.TakeMelee(e.PlayerId,e.Strong);
            Emit("melee",new{playerId=e.PlayerId,strong=e.Strong,itemId=facts?.ItemId,repeated=facts?.Repeated,acceptedTick=facts?.Tick});
        };
        _client.PlayerGestured += e => Emit("gesture", e);
        _client.AvatarArrived += hash => Emit("profileAvatar", new { avatarHash = hash });
        _client.EquipmentFactReceived += e => {
            if (_server == null) _events.Add(new(++_eventSequence, EquipmentJson.FactName(e.Kind), EquipmentJson.Fact(_client.PlayerId,e), e.Tick));
        };
        _client.StorageClosed += e => { _storageView.Close(); Emit("storageClosed", e); };
        _client.VehicleDoor += e => Emit("vehicleDoor", new { netId=e.NetId, open=e.Open, position=Array(e.Position), acceptedTick=e.Tick });
        _client.VehicleMaintenance += e => Emit(e.Kind switch { VehicleMaintenanceKind.GlassBroken => "vehicleGlassBroken", VehicleMaintenanceKind.GlassRepaired => "vehicleGlassRepaired", VehicleMaintenanceKind.TireRepaired => "vehicleTireRepaired", _ => "vehicleHorn" }, new { netId=e.NetId, index=e.Index, label=e.Kind is VehicleMaintenanceKind.GlassBroken or VehicleMaintenanceKind.GlassRepaired?VehicleMaintenanceServices.Labels[e.Index]:null, position=Array(e.Position), normal=e.Kind==VehicleMaintenanceKind.GlassBroken?Array(e.Normal):null, halfSize=e.Kind==VehicleMaintenanceKind.GlassBroken?Array(e.HalfSize):null, acceptedTick=e.Tick });
        _client.VehicleLighting += e => Emit(e.Kind switch { VehicleLightingKind.AlarmHorn => "vehicleAlarmHorn", VehicleLightingKind.LampBroken => "vehicleLampBroken", _ => "vehicleLampRepaired" }, new { netId=e.NetId, lamp=e.Lamp, label=e.Lamp<VehicleLightRules.LampLabels.Length?VehicleLightRules.LampLabels[e.Lamp]:null, position=Array(e.Position), acceptedTick=e.Tick });
        _client.VehicleEntered += e => Emit("vehicleEntered", e);
        _client.VehicleExited += e => {
            if (e.PlayerId == _client.PlayerId) { _position = e.Pos; _velocity = Vector3.zero; _movement = new(); _correctionVersion++; }
            Emit("vehicleExited", e);
        };
        _client.VehicleExitRefused += e => Emit("vehicleExitRefused", e);
        _client.VehicleRecov += e => { _vehicleRecov[e.NetId] = e.RecovCounter; Emit("vehicleRecov", e); };
        _client.ChatMessage += e => Emit("chat", e);
        _client.ImpactFx += e => {
            var facts=_warheads?.TakeImpact();
            Emit("impact",new{pos=e.Pos,surface=e.Surface,
                normal=facts?.Normal is Vector3 normal?Array(normal):null,
                water=facts?.Water,thin=facts?.Thin,playerId=facts?.Shooter,
                sequence=facts?.Sequence,pellet=facts?.Pellet,acceptedTick=facts?.Tick});
        };
        _client.GrenadeExploded += e => Emit("grenadeExploded", e);
        _client.PlayerDied += e => { if (e.Victim == _client.PlayerId) _client.ClearCombatRing(); Emit("death", e); };
        _client.PlayerRespawned += e => {
            if (e.PlayerId == _client.PlayerId) { _client.ClearCombatRing(); _movement = new(); _stance = new(); _velocity = Vector3.zero; _held = 0; _correctionVersion++; }
            Emit("respawn", e);
        };
        _client.CropPlanted += e => Emit("cropPlanted", e);
        _client.CropHarvested += e => Emit("cropHarvested", e);
        _client.ResourceContact += ReceiveResourceContact;
        _client.ResourceHarvested += e => Emit("resourceHarvested", e);
        _client.ResourceRespawned += e => Emit("resourceRespawned", e);
        _client.ObjectDestroyed += e => Emit("objectDestroyed", e);
        _client.ObjectRestored += e => Emit("objectRestored", e);
        _client.NpcState += e => { _npcView.Adopt(e); Emit("npcState", e); };
        _client.DoorStateChanged += e => Emit("doorState", e);
        _client.ObjectDoorState += e => Emit("objectDoorState", e);
        _client.BedClaimed += e => Emit("bedClaimed", e);
        _client.SeatOccupied += e => Emit("seatOccupied", e);
        _client.StorageOpened += e => { _storageView.Open(e); Emit("storageOpened", e); };
        _client.CookerState += e => Emit("cookerState", e);
        _client.ItemPickupDenied += e => Emit("pickupDenied", e);
        _client.PlayerHurt += e => Emit("hurt", e);
        _client.Connect();
    }
}
