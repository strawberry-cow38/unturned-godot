using System;
using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    // Protocol71 command81 is deliberately distinct from legacy command39.
    // One round per request, exact owned instances, no client ammunition count.
    public struct MagazineTransferCommand
    {
        public uint RequestId;
        public EquipmentAddress Magazine, Round;
        public ulong MagazineToken, RoundToken;
        public bool Unloading;
        public bool IsValid => RequestId != 0 && Magazine.Page < PlayerInventory.OWNPAGES
            && Magazine.Id != 0 && MagazineToken != 0 && Round.Id != 0
            && (Unloading ? Round.Page == 0 && Round.X == 0 && Round.Y == 0 && RoundToken == 0
                : Round.Page < PlayerInventory.OWNPAGES && RoundToken != 0);
        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(RequestId); Magazine.Write(w); w.WriteUInt64(MagazineToken);
            w.WriteBit(Unloading); Round.Write(w); w.WriteUInt64(RoundToken);
        }
        public static bool TryRead(NetPakReader r, out MagazineTransferCommand value)
        {
            value = default; var next = new MagazineTransferCommand();
            if (!r.ReadUInt32(out next.RequestId) || !EquipmentAddress.TryRead(r, out next.Magazine)
                || !r.ReadUInt64(out next.MagazineToken) || !r.ReadBit(out next.Unloading)
                || !EquipmentAddress.TryRead(r, out next.Round) || !r.ReadUInt64(out next.RoundToken)
                || !next.IsValid) return false;
            value = next; return true;
        }
        internal MagLoadCommand Legacy => new() { MagPage = Magazine.Page, MagX = Magazine.X, MagY = Magazine.Y,
            MagId = Magazine.Id, RoundPage = Round.Page, RoundX = Round.X, RoundY = Round.Y,
            RoundId = Round.Id, Unloading = Unloading };
    }

    public enum MagazineTransferResult : byte
    {
        Applied, MissingInventory, Dead, StaleMagazine, StaleRound, NotMagazine, RuleRefused, NoRoom, StaleRequest
    }
    /// <summary>Owner-only completion of a magazine UI request. Amount is an
    /// accepted fact, not permission to edit the local inventory replica.</summary>
    public struct MagazineTransferEvent
    {
        public uint RequestId, Tick;
        public ulong MagazineToken;
        public MagazineTransferResult Result;
        public ushort Amount;
        public bool Applied => Result == MagazineTransferResult.Applied;
        public bool IsValid => RequestId != 0 && MagazineToken != 0 && Result <= MagazineTransferResult.StaleRequest
            && (Applied || Amount == 0);
        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(RequestId); w.WriteUInt32(Tick); w.WriteUInt64(MagazineToken);
            w.WriteUInt8((byte)Result); w.WriteUInt16(Amount);
        }
        public static bool TryRead(NetPakReader r, out MagazineTransferEvent value)
        {
            value = default; var next = new MagazineTransferEvent();
            if (!r.ReadUInt32(out next.RequestId) || !r.ReadUInt32(out next.Tick)
                || !r.ReadUInt64(out next.MagazineToken) || !r.ReadUInt8(out byte result)
                || !r.ReadUInt16(out next.Amount)) return false;
            next.Result = (MagazineTransferResult)result;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }
    public sealed partial class NetWorldClient
    {
        public event Action<MagazineTransferEvent> MagazineTransferred;
        void RegisterMagazineTransferEvents() => Events.Register<MagazineTransferEvent>(ReplicationIds.EventMagazineTransfer,
            MagazineTransferEvent.TryRead, value => MagazineTransferred?.Invoke(value));
        public bool SendMagazineTransfer(uint requestId, byte magPage, byte magX, byte magY, ushort magId, ulong magToken,
            byte roundPage, byte roundX, byte roundY, ushort roundId, ulong roundToken, bool unloading)
        {
            var cmd = new MagazineTransferCommand { RequestId = requestId, Magazine = new(magPage, magX, magY, magId),
                MagazineToken = magToken, Round = new(roundPage, roundX, roundY, roundId), RoundToken = roundToken, Unloading = unloading };
            return cmd.IsValid && SendCommand(ReplicationIds.CommandMagazineTransfer, cmd.Write);
        }
    }
}
