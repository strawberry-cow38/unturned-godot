using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;
namespace UnturnedBevy;

/// <summary>Host choke point for v79 commands 99/100: the vehicle branches of
/// PlayerController.TryDepositFuel (LMB pour) and TryExtractFuel (RMB siphon).
/// The source mutated its local Vehicle and held can; here the exact held
/// instance (the v78 DepositFuel address+token shape) and the car are named,
/// and the host moves min(can, free tank) or min(free can, tank). The host's
/// vehicle fuel overlay (VehicleServices.FuelFor) is the replicated tank.</summary>
public sealed class VehicleFuelServices
{
    /// <summary>The other vehicle hand tools' reach (ServerVehicles.PaintReach): the
    /// source aims with the 2.6 m look ray; the server refuses forged far ids.</summary>
    public static float Reach => ServerVehicles.PaintReach;
    readonly NetWorldServer _server;
    readonly MountedWeaponCatalog _catalog;
    readonly VehicleServices _vehicles;
    public VehicleFuelServices(NetWorldServer server, Catalog catalog, VehicleServices vehicles)
    {
        _server = server; _catalog = catalog.Mounted; _vehicles = vehicles;
        server.Commands.Register<VehicleFuelCanCommand>(ReplicationIds.CommandPourVehicleFuel, VehicleFuelCanCommand.TryRead,
            (sender, cmd) => Transfer(sender, cmd, pour: true), Alive);
        server.Commands.Register<VehicleFuelCanCommand>(ReplicationIds.CommandSiphonVehicleFuel, VehicleFuelCanCommand.TryRead,
            (sender, cmd) => Transfer(sender, cmd, pour: false), Alive);
    }
    bool Alive(ushort sender, VehicleFuelCanCommand cmd)
        => _server.Inventories.TryGet(sender, out _) && _server.CombatState.TryGet(sender, out var life) && life.Alive;
    /// <summary>Vehicle.FuelMax = Spec.Fuel (mL, the same unit as a can's fuelLevel).</summary>
    public float FuelMax(byte type)
        => _catalog.For(type)?.Source.GetProperty("spec") is { } spec && spec.TryGetProperty("Fuel", out var fuel) ? fuel.GetSingle() : 0f;
    void Transfer(ushort sender, VehicleFuelCanCommand cmd, bool pour)
    {
        if (!_server.Vehicles.TryGet(cmd.VehicleNetId, out var car)) return;
        if (!_server.Players.TryGetByOwner(sender, out var p) || (car.Pos - p.Pos).sqrMagnitude > Reach * Reach) return;
        float max = FuelMax(car.TypeId);
        if (max <= 0f) return; // `_focusVehicle.FuelMax > 0f`
        if (!_server.Inventories.TryGet(sender, out var entry)) return;
        var page = cmd.Address.Page < PlayerInventory.OWNPAGES ? entry.Inventory.items[cmd.Address.Page] : null;
        byte index = page?.getIndex(cmd.Address.X, cmd.Address.Y) ?? byte.MaxValue;
        var jar = index == byte.MaxValue ? null : page.getItem(index);
        if (jar?.item?.id != cmd.Address.Id || jar.x != cmd.Address.X || jar.y != cmd.Address.Y
            || jar.GetAsset() is not { IsFuelContainer: true } asset || _server.Inventories.ItemIdentityProvider == null
            || _server.Inventories.ItemIdentityProvider(jar.item) != cmd.Token) return;
        float can = Mathf.Max(0f, jar.item.fuelLevel), tank = Mathf.Max(0f, _vehicles.FuelFor(car.NetIdValue, car.Fuel));
        float moved;
        if (pour)
        {
            if (can <= 0.01f) return;                   // "[fuel] can is empty"
            float space = max - tank;
            if (space <= 0.01f) return;                 // "[fuel] that tank is full"
            moved = Mathf.Min(can, space);
            tank += moved; can -= moved;
        }
        else
        {
            float space = asset.fuelCapacity - can;
            if (space <= 0.01f) return;                 // "[fuel] can is full"
            moved = Mathf.Min(space, tank);
            if (moved <= 0.01f) return;                 // "[fuel] that vehicle is empty"
            tank -= moved; can += moved;
        }
        _vehicles.SetFuel(car.NetIdValue, tank);
        jar.item.fuelLevel = can;
        _server.Inventories.ServerMarkDirty(sender);
    }
}
