using UnturnedGodot;
using UnturnedGodot.Net;
namespace UnturnedBevy;

/// <summary>Host seams of the source vehicle hand tools (ledger row 9).
/// Core's ServerVehicles validates commands 50/55/56; the game layer
/// (MpLoopback/DedicatedServer, VehicleNetSync) supplied the content table and
/// the physics shove. The hybrid host supplies the same two seams here.</summary>
public sealed class VehicleHandToolServices
{
    readonly Random _random = new();
    public VehicleHandToolServices(NetWorldServer server, Action<string, object> emit)
    {
        // MpLoopback.cs/DedicatedServer.cs: the spraypaint table is content, so
        // the host hands it down. Without it SpendPaintCan refuses every can.
        server.Transactions.PaintColorFor = Rgb;
        // VehicleNetSync.ApplyCarjackForce -> Vehicle.Carjack. The server rolls
        // the source vectors (System.Random, R(a,b) = a + NextDouble*(b-a), in
        // this order); the native physics owner applies them after its own
        // empty/wreck/JackableNow gate, exactly where Vehicle.Carjack gates.
        server.VehicleHost.ApplyCarjackForce = (id, sender) =>
        {
            float R(float a, float b) => a + (float)_random.NextDouble() * (b - a);
            bool flight = false; // source: "no FLIGHT skill system here yet"
            var force = new[] { R(-32f, 32f), R(480f, 544f) * (flight ? 4f : 1f), R(-32f, 32f) };
            var torque = new[] { R(-64f, 64f), R(-64f, 64f), R(-64f, 64f) };
            emit("carjack", new { netId = id, playerId = sender, force, torque });
        };
    }

    /// <summary>VehiclePaints.For as MpLoopback packs it: 0xRRGGBB from Color.R8/G8/B8.</summary>
    public static uint? Rgb(ushort itemId)
    {
        if (VehiclePaints.For(itemId) is not Godot.Color c) return null;
        static uint Byte(float v) => (uint)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return (Byte(c.R) << 16) | (Byte(c.G) << 8) | Byte(c.B);
    }
}
