using System.Text.Json;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

public sealed partial class WorldServices
{
    readonly Dictionary<uint, StationarySeatPose> _seatPoses = new();

    void BindStationarySeats() => _server.PlayerHost.StationarySeatOf = player =>
        !_server.VehicleHost.IsSeated(player)
        && _server.Interactables.TryGetSeatOf(player, out uint id)
        && _seatPoses.TryGetValue(id, out var pose) ? pose : null;

    void RegisterStationarySeat(JsonElement spec)
    {
        uint id = checked((uint)Int(spec, "netId"));
        if (id == 0 || _retiredBedSeats.Contains(id)) return;
        var anchor = Vec(spec, "position");
        var forward = Vec(spec, "face", new(0, 0, -1)); forward.y = 0;
        if (!float.IsFinite(anchor.x) || !float.IsFinite(anchor.y) || !float.IsFinite(anchor.z)
            || !float.IsFinite(forward.x) || !float.IsFinite(forward.z))
            throw new ArgumentException("Invalid stationary seat pose");
        if (forward.sqrMagnitude < .0001f) forward = new(0, 0, -1);
        // PlayerController.SeatStandPosition: bed feet stay at the anchor;
        // ordinary chairs put the animated Idle_Sit pelvis on the cushion.
        var foot = Bool(spec, "recline") ? anchor : anchor - Vector3.up * BodyRules.PropSeatHipSeated;
        float yaw = MathF.Atan2(-forward.x, -forward.z) * Mathf.Rad2Deg;
        _seatPoses[id] = new(id, foot, yaw);
        _server.Interactables.RegisterSeat(id, anchor);
    }

    void TranslateStationarySeats(Vector3 delta)
    {
        foreach (uint id in _seatPoses.Keys.ToArray())
        { var pose = _seatPoses[id]; _seatPoses[id] = pose with { Position = pose.Position + delta }; }
    }

    void ReleaseInvalidStationaryOccupants(long tick)
    {
        // Source EjectFromVehicleOnDeath calls StandUp. Run on authority so a
        // lost/dead client cannot retain a chair, and a vehicle's accepted seat
        // cannot fight an old furniture anchor on the following publication.
        foreach (var entry in _server.Interactables.SeatOccupants.ToArray())
        {
            ushort owner = entry.Value;
            if (owner == 0 || (_server.CombatState.IsAlive(owner) && !_server.VehicleHost.IsSeated(owner))) continue;
            if (!_server.Interactables.Stand(owner, out uint freed)) continue;
            _server.Players.ServerRefreshStance(owner, tick);
            _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventSeatOccupied,
                new SeatOccupiedEvent { NetId = freed, Occupant = 0 }.Write));
        }
    }
}
