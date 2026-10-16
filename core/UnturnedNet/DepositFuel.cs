using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>v78: PlayerController.TryDepositFuel as an intent -- LMB with a held fuel container pours
    /// min(can, free tank space) into the focused generator. The unified source mutated its replica node and
    /// held item locally, so the pour never reached the server (and was rewritten by the next replica tick);
    /// with the server now burning fuel, this is the accepted refuel. The exact held instance is named, so a
    /// stale request cannot drain a replacement can at the same address.</summary>
    public struct DepositFuelCommand
    {
        public uint NetId;
        public EquipmentAddress Address;
        public ulong Token;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); Address.Write(w); w.WriteUInt64(Token); }
        public static bool TryRead(NetPakReader r, out DepositFuelCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id) || id == 0 || !EquipmentAddress.TryRead(r, out var address) || address.Id == 0
                || !r.ReadUInt64(out ulong token) || token == 0) return false;
            cmd = new() { NetId = id, Address = address, Token = token }; return true;
        }
    }
    public sealed partial class NetWorldClient
    {
        public bool SendDepositFuel(uint netId, byte page, byte x, byte y, ushort id, ulong token) =>
            SendCommand(ReplicationIds.CommandDepositFuel, new DepositFuelCommand { NetId = netId, Address = new(page, x, y, id), Token = token }.Write);
    }
}
