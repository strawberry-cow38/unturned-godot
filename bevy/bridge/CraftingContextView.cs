using System.Text.Json;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    uint _craftContextNext, _craftContextAccepted;
    CraftingContextEvent _craftContext;
    long _craftContextReceived;

    void ResetCraftingContext()
    {
        _craftContextNext = _craftContextAccepted = 0;
        _craftContext = default; _craftContextReceived = 0;
    }
    void ReceiveCraftingContext(CraftingContextEvent value)
    {
        // This is the last accepted owner fact, just like inventory/queue state.
        // Camera easing, physics jitter and remote latency must not erase it
        // before the next reply arrives. Every actual craft rechecks access.
        if (_craftContextAccepted != 0 && unchecked((int)(value.RequestId-_craftContextAccepted)) <= 0) return;
        _craftContext = value; _craftContextAccepted = value.RequestId;
        _craftContextReceived = _network.CurrentTick;
        Emit("craftingContext", new { requestId = value.RequestId, acceptedTick = value.Tick,
            result = value.Result.ToString(), tags = value.Tags ?? System.Array.Empty<string>() });
    }
    ICollection<string> AcceptedCraftingStations => _craftContextAccepted != 0
        && _craftContext.HasContext && _network.CurrentTick-_craftContextReceived <= 25
        ? _craftContext.Tags : System.Array.Empty<string>();

    object RequestCraftingContext(JsonElement request, bool craft = false)
    {
        var eye = Vec(request.GetProperty("eye"));
        uint next = unchecked(++_craftContextNext); if (next == 0) next = ++_craftContextNext;
        int index = craft ? Int(request,"index",-1) : 0;
        bool queued = index is >= 0 and <= ushort.MaxValue
            && _client.SendCraftingContext(next,eye,craft,(ushort)index);
        return new { queued, requestId = next };
    }
    void ProbeCraftingContext(JsonElement request)
    {
        if (request.TryGetProperty("eye",out var eye) && eye.ValueKind != JsonValueKind.Null)
            RequestCraftingContext(request);
    }
}
