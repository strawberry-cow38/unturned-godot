using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

/// <summary>PlayerController.CraftingStationTags: source eye-radius to each
/// placed station origin and one world-layer ray excluding its own body.
/// Source grants tags independently of a station's power or burning state.</summary>
public static class CraftingStations
{
    public static bool EyeAllowed(NetWorldServer server, WorldGeometry geometry, ushort owner, Vector3 eye)
    {
        if (!server.Players.TryGetByOwner(owner,out var player)) return false;
        float foot=server.Combat.DefaultGun.MaxAimOriginOffset;
        if ((eye-player.Pos).sqrMagnitude<=foot*foot) return true;
        // Source third-person station queries use _cam.GlobalPosition too.
        // Bind the larger envelope to an ACTUAL accepted occupied vehicle,
        // using host scene bounds and source maximum chase length/zoom/height.
        // A client cannot assert either occupancy or the registered hull size.
        if (!server.VehicleHost.IsSeated(owner)) return false;
        foreach (var vehicle in server.Vehicles.All)
        {
            if (vehicle.DriverPlayerId!=owner && !(vehicle.Passengers?.Contains(owner)??false)) continue;
            if (!geometry.TryDynamicBounds("vehicle:"+vehicle.NetIdValue,out var min,out var max)) return false;
            float envelope=34f*1.8f+(max-min).magnitude*.22f;
            return (eye-vehicle.Pos).sqrMagnitude<=envelope*envelope;
        }
        return false;
    }
    public static ICollection<string> At(NetWorldServer server, WorldGeometry geometry, Vector3 eye)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var station in server.Deployables.All)
        {
            var def = DeployableDef.ById(station.DefId);
            if (def?.CraftingTags == null || def.CraftingTags.Length == 0) continue;
            if ((eye-station.Pos).sqrMagnitude > def.CraftingRange*def.CraftingRange) continue;
            if (geometry.StationBlocked(station.NetIdValue,eye,station.Pos)) continue;
            foreach (var tag in def.CraftingTags) tags.Add(tag);
        }
        return tags;
    }
}
