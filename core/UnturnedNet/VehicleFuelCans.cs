using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>v82: the VEHICLE halves of PlayerController.TryDepositFuel (LMB: pour the held fuel container
    /// into the focused car) and TryExtractFuel (RMB: siphon the focused car into it). The unified source
    /// mutated its local Vehicle and held item directly, so neither ever reached a server -- a joined client
    /// focuses a puppet and got nothing. As intents they name the exact held instance (address + identity
    /// token, the v78 DepositFuel shape) and the car; how much moves is min(can, free tank) / min(free can,
    /// tank), decided by the host.</summary>
    public struct VehicleFuelCanCommand
    {
        public uint VehicleNetId;
        public EquipmentAddress Address;
        public ulong Token;
        public void Write(NetPakWriter w) { w.WriteUInt32(VehicleNetId); Address.Write(w); w.WriteUInt64(Token); }
        public static bool TryRead(NetPakReader r, out VehicleFuelCanCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id) || id == 0 || !EquipmentAddress.TryRead(r, out var address) || address.Id == 0
                || !r.ReadUInt64(out ulong token) || token == 0) return false;
            cmd = new() { VehicleNetId = id, Address = address, Token = token }; return true;
        }
    }
    public sealed partial class NetWorldClient
    {
        public bool SendPourVehicleFuel(uint vehicleNetId, byte page, byte x, byte y, ushort id, ulong token) =>
            SendCommand(ReplicationIds.CommandPourVehicleFuel, new VehicleFuelCanCommand { VehicleNetId = vehicleNetId, Address = new(page, x, y, id), Token = token }.Write);
        public bool SendSiphonVehicleFuel(uint vehicleNetId, byte page, byte x, byte y, ushort id, ulong token) =>
            SendCommand(ReplicationIds.CommandSiphonVehicleFuel, new VehicleFuelCanCommand { VehicleNetId = vehicleNetId, Address = new(page, x, y, id), Token = token }.Write);
    }
}
