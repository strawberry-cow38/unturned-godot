using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>InventoryUI's Empty action. The sender names an owned instance,
    /// never a fuel amount or another player's inventory.</summary>
    public struct EmptyFuelCommand
    {
        public EquipmentAddress Address;
        public ulong Token;
        public void Write(NetPakWriter w) { Address.Write(w); w.WriteUInt64(Token); }
        public static bool TryRead(NetPakReader r, out EmptyFuelCommand cmd)
        {
            cmd = default;
            if (!EquipmentAddress.TryRead(r, out var address) || address.Id == 0
                || !r.ReadUInt64(out ulong token) || token == 0) return false;
            cmd = new() { Address = address, Token = token }; return true;
        }
    }
    public sealed partial class NetWorldClient
    {
        public bool SendEmptyFuel(byte page, byte x, byte y, ushort id, ulong token) =>
            SendCommand(ReplicationIds.CommandEmptyFuel, new EmptyFuelCommand { Address = new(page, x, y, id), Token = token }.Write);
    }
}
