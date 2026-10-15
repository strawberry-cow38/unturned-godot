using System.Text.Json;
using SDG.Unturned;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

public sealed partial class WorldServices
{
    sealed class DoorBody(uint id, string key, Vector3 hinge)
    {
        public readonly uint Id = id;
        public readonly string Key = key;
        public Vector3 Hinge = hinge;
        public float Health = 250f; // Door.cs Health/HealthMax.
        public bool Open;
    }
    readonly Dictionary<string, DoorBody> _doorBodies = new();
    public Action<Vector3, float> DoorNoise { get; set; }

    void RegisterDoorBody(JsonElement spec)
    {
        uint id = checked((uint)Int(spec, "netId"));
        string key = Text(spec, "geometryKey");
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (_doorBodies.TryGetValue(key, out var existing))
            {
                if (existing.Id != id) throw new ArgumentException("Door geometry key cannot change fixture identity");
                return; // A geometry retry must not heal, revive, unlock or close a door.
            }
            _doorBodies.Add(key, new DoorBody(id, key, Vec(spec, "position") + Vec(spec, "hingeOffset", new(-.5f, 0, 0))));
        }
        _server.Interactables.RegisterDoor(id, Vec(spec, "position"), checked((ulong)Int(spec, "owner")), Bool(spec, "locked"));
    }

    bool DamageDoor(string key, float amount, WorldGeometry geometry)
    {
        if (!_doorBodies.TryGetValue(key, out var door) || door.Health <= 0) return false;
        if (!(amount > 0) || !float.IsFinite(amount)) return true;
        door.Health = Math.Max(0, door.Health - amount);
        if (door.Health > 0) return true;
        _server.Interactables.RemoveDoor(door.Id);
        geometry.RemoveDynamic(door.Key);
        return true;
    }

    void StepDoors()
    {
        // InteractableNetSync mirrors accepted state onto Door.ApplyReplicatedToggle,
        // which emits at its fixed hinge. Remote renderers never publish this noise.
        foreach (var door in _doorBodies.Values)
        {
            if (door.Health <= 0 || !_server.Interactables.TryGetDoor(door.Id, out var state)) continue;
            if (door.Open == state.State.IsOpen) continue;
            door.Open = state.State.IsOpen;
            DoorNoise?.Invoke(door.Hinge, DoorLogic.ToggleLoudness);
        }
    }
    void TranslateDoors(Vector3 delta)
    {
        foreach (var door in _doorBodies.Values) door.Hinge += delta;
    }
}
