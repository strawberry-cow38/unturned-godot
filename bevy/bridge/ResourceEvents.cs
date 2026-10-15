using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Shared local/remote presentation payload; no damage or reward writes.</summary>
public static class ResourceEvents
{
    public static string Name(ResourceContactEvent value) => value.Kind == ResourceContactKind.Hit ? "resourceHit" : "resourceFelled";
    public static object Data(ResourceContactEvent value, string name = null, double ageSeconds = 0)
    {
        string kind = value.Material == ResourceContactMaterial.Tree ? "tree" : "ore";
        if (value.Kind == ResourceContactKind.Hit)
            return new { index = value.Index, name, kind, health = value.Health, damage = value.Damage,
                point = BridgeHost.Array(value.Position), acceptedTick = value.Tick, ageSeconds };
        return new { index = value.Index, name, kind, position = BridgeHost.Array(value.Position),
            direction = BridgeHost.Array(value.Direction.normalized), trunkLength = value.TrunkLength,
            debrisSeconds = value.DebrisSeconds, acceptedTick = value.Tick, ageSeconds };
    }
    // Snapshot ticks can lag a reliable event; unsigned wrapping must not turn
    // that case into a centuries-old tree. This is age known at receipt only.
    public static double Age(uint acceptedTick, uint observedTick) =>
        Math.Max(0, unchecked((int)(observedTick - acceptedTick))) * SimClock.FixedDelta;
}

public sealed partial class BridgeHost
{
    void ReceiveResourceContact(ResourceContactEvent value)
    {
        // The integrated host already emits the exact source fact directly.
        // Its local wire echo must not restart canopy shake/fall a second time.
        if (_server != null) return;
        _events.Add(new(++_eventSequence, ResourceEvents.Name(value), ResourceEvents.Data(value,
            ageSeconds: ResourceEvents.Age(value.Tick, unchecked((uint)_client.Applier.LastAppliedServerTick))), value.Tick));
    }
}
