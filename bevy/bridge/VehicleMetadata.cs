using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    int? VehicleSeatCount(VehicleReplication.VehicleEntity replica)
    {
        // SeatCount is deliberately host-only in the original protocol. Reading
        // it from a decoded replica always produces the unset zero. Hosts report
        // their accepted validator count; peers resolve the same source TypeId
        // table, as the original VehicleReplication contract requires.
        if (_server != null && _server.Vehicles.TryGet(replica.NetIdValue, out _))
            return _server.Vehicles.SeatsOn(new(replica.NetIdValue));
        return _catalog.Mounted.For(replica.TypeId)?.Source.GetProperty("seats").GetArrayLength();
    }
}
