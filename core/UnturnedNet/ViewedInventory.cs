using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>Exact live instance in the sender's bag or current container.
    /// CrateId zero admits only owned pages; AREA is never an inventory authority.</summary>
    public readonly record struct ScopedInventoryAddress(uint CrateId, byte Page, byte X, byte Y, ushort Id, ulong Token)
    {
        public bool IsValid => Id != 0 && Token != 0 && (CrateId == 0
            ? Page < PlayerInventory.OWNPAGES : PlayerInventory.IsContainerView(Page));
        public void Write(NetPakWriter w)
        { w.WriteUInt32(CrateId); w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(Id); w.WriteUInt64(Token); }
        public static bool TryRead(NetPakReader r, out ScopedInventoryAddress value)
        {
            value = default;
            if (!r.ReadUInt32(out uint crate) || !r.ReadUInt8(out byte page) || !r.ReadUInt8(out byte x)
                || !r.ReadUInt8(out byte y) || !r.ReadUInt16(out ushort id) || !r.ReadUInt64(out ulong token)) return false;
            var next = new ScopedInventoryAddress(crate, page, x, y, id, token);
            if (!next.IsValid) return false;
            value = next; return true;
        }
        internal static ScopedInventoryAddress Owned(EquipmentAddress a, ulong token) => new(0, a.Page, a.X, a.Y, a.Id, token);
    }

    public struct EmptyViewedFuelCommand
    {
        public ScopedInventoryAddress Address;
        public void Write(NetPakWriter w) => Address.Write(w);
        public static bool TryRead(NetPakReader r, out EmptyViewedFuelCommand value)
        {
            value = default;
            if (!ScopedInventoryAddress.TryRead(r, out var address) || address.CrateId == 0) return false;
            value = new() { Address = address }; return true;
        }
    }

    /// <summary>One original MagRules step, with at least one authorized viewed
    /// instance. Unload has no source cell/token, only the expected loose-round ID.</summary>
    public struct ViewedMagazineTransferCommand
    {
        public uint RequestId;
        public ScopedInventoryAddress Magazine, Round;
        public bool Unloading;
        public ushort UnloadRoundId;
        public bool IsValid => RequestId != 0 && Magazine.IsValid && (Unloading
            ? Magazine.CrateId != 0 && Round == default && UnloadRoundId != 0
            : UnloadRoundId == 0 && Round.IsValid && (Magazine.CrateId != 0 || Round.CrateId != 0));
        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(RequestId); Magazine.Write(w); w.WriteBit(Unloading);
            if (Unloading) w.WriteUInt16(UnloadRoundId); else Round.Write(w);
        }
        public static bool TryRead(NetPakReader r, out ViewedMagazineTransferCommand value)
        {
            value = default; var next = new ViewedMagazineTransferCommand();
            if (!r.ReadUInt32(out next.RequestId) || !ScopedInventoryAddress.TryRead(r, out next.Magazine)
                || !r.ReadBit(out next.Unloading)) return false;
            if (next.Unloading) { if (!r.ReadUInt16(out next.UnloadRoundId)) return false; }
            else if (!ScopedInventoryAddress.TryRead(r, out next.Round)) return false;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }

    public sealed partial class NetWorldClient
    {
        public bool SendEmptyViewedFuel(uint crateId, byte page, byte x, byte y, ushort id, ulong token)
        {
            var address = new ScopedInventoryAddress(crateId, page, x, y, id, token);
            return address.IsValid && crateId != 0 && SendCommand(ReplicationIds.CommandEmptyViewedFuel,
                new EmptyViewedFuelCommand { Address = address }.Write);
        }
        public bool SendViewedMagazineTransfer(uint requestId, uint magCrateId, byte magPage, byte magX, byte magY, ushort magId, ulong magToken,
            uint roundCrateId, byte roundPage, byte roundX, byte roundY, ushort roundId, ulong roundToken, bool unloading)
        {
            // Canonical unused unload fields prevent a caller from smuggling a
            // second scope which the wire intentionally does not serialize.
            if (unloading && (roundCrateId != 0 || roundPage != 0 || roundX != 0 || roundY != 0 || roundToken != 0)) return false;
            var cmd = new ViewedMagazineTransferCommand { RequestId = requestId,
                Magazine = new(magCrateId, magPage, magX, magY, magId, magToken), Unloading = unloading,
                Round = unloading ? default : new(roundCrateId, roundPage, roundX, roundY, roundId, roundToken),
                UnloadRoundId = unloading ? roundId : (ushort)0 };
            return cmd.IsValid && SendCommand(ReplicationIds.CommandViewedMagazineTransfer, cmd.Write);
        }
    }
}
