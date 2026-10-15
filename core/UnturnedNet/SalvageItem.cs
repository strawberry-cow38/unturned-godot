using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>Salvage the exact owned copy selected in InventoryUI. Recipe and
    /// quantity are resolved by the host, never asserted by the client.</summary>
    public struct SalvageItemCommand
    {
        public EquipmentAddress Address;
        public ulong Token;
        public void Write(NetPakWriter w) { Address.Write(w); w.WriteUInt64(Token); }
        public static bool TryRead(NetPakReader r, out SalvageItemCommand cmd)
        {
            cmd = default;
            if (!EquipmentAddress.TryRead(r, out var address) || address.Id == 0
                || !r.ReadUInt64(out ulong token) || token == 0) return false;
            cmd = new() { Address = address, Token = token }; return true;
        }
    }
    public sealed partial class NetWorldClient
    {
        public bool SendSalvageItem(byte page, byte x, byte y, ushort id, ulong token) =>
            SendCommand(ReplicationIds.CommandSalvageItem, new SalvageItemCommand { Address = new(page, x, y, id), Token = token }.Write);
    }
}
