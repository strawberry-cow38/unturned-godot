using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    /// <summary>Read only the accepted client graph. Solver output is derived
    /// by the existing core algorithm; this never registers or mutates authority.</summary>
    object ReadPowerGraph()
    {
        var graph = _client.Deployables;
        graph.Solve();
        return new {
            graphVersion = graph.GraphVersion,
            devices = graph.All.Select(e => new {
                netId = e.NetIdValue, defId = e.DefId,
                toggledOn = e.ToggledOn, onFire = e.OnFire,
                ports = e.Solved.Select((p, i) => new {
                    index = i, live = p.Live, powered = p.Powered, draw = p.Draw,
                }).ToArray(),
            }).ToArray(),
            wires = graph.AllWires.Select(w => new {
                netId = w.NetIdValue, sourceId = w.SrcId, sourcePort = w.SrcPort,
                consumerId = w.DstId, consumerPort = w.DstPort,
            }).ToArray(),
        };
    }
}
