using System.Text.Json;
using SDG.Unturned;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    static object[] GestureMetadata() => Enum.GetValues<EPlayerGesture>().Select(id =>
    {
        var definition = GestureRules.Of(id);
        return (object)new { id = (byte)id, name = id.ToString().ToLowerInvariant(),
            clip = definition.Clip, loops = definition.Loops, stops = (byte)definition.Stops,
            requestable = definition.PlayerRequestable, broadcast = definition.Broadcast,
            requiredStance = definition.RequiredStance?.ToString().ToLowerInvariant() };
    }).ToArray();

    const string HandsBound = "Your hands are bound.";
    /// <summary>PlayerController.IsArrested: the accepted gesture on our own combat entity.</summary>
    bool OwnerArrested() => _client != null && _client.CombatState.TryGet(_client.PlayerId, out var self)
        && self.Gesture == (byte)EPlayerGesture.ARREST_START;

    // PlayerController.RequestGesture performs this same local advisory check
    // before sending. Never pose optimistically: the publisher still decides on
    // its next tick using authoritative hands/stance and emits the accepted fact.
    object RequestGesture(JsonElement args)
    {
        int value = Int(args, "gesture", -1);
        if (value < 0 || value > byte.MaxValue) throw new ArgumentException("gesture must be a byte");
        var want = (EPlayerGesture)(byte)value;
        _client.CombatState.TryGet(_client.PlayerId, out var player);
        var stance = _movement.Stance;
        var vehicle = _client.Vehicles.All.FirstOrDefault(v => v.DriverPlayerId == _client.PlayerId
            || (v.Passengers?.Contains(_client.PlayerId) ?? false));
        if (vehicle != null) stance = vehicle.DriverPlayerId == _client.PlayerId ? EPlayerStance.DRIVING : EPlayerStance.SITTING;
        else if (_client.InteractableState.ReplicaSeats.Any(s => s.Value == _client.PlayerId)) stance = EPlayerStance.SITTING;
        string reason = GestureRules.RefusalFor(want, (EPlayerGesture)(player?.Gesture ?? 0), stance, _held != 0);
        if (reason != null)
        {
            Emit("gestureRejected", new { gesture = (byte)value, text = reason });
            return new { queued = false, reason };
        }
        return new { queued = _client.SendRequestGesture((byte)value), reason = (string)null };
    }
}
