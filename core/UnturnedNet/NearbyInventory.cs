using System;
using System.Collections.Generic;
using System.Text;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public enum NearbyAction : byte { Open, Close, Transfer }
    public enum NearbyResult : byte { Ready, Closed, Transferred, NoRoom, Stale, Dead, InvalidEye, Unavailable, Denied }
    public struct NearbyInventoryCommand
    {
        public uint RequestId, ScanId, Revision, WorldId, CrateId;
        public ulong Token;
        public byte X, Y, Destination;
        public NearbyAction Action;
        public Vector3 Eye;
        public bool IsValid => RequestId != 0 && Action <= NearbyAction.Transfer && MountedWire.Finite(Eye)
            && (Action == NearbyAction.Transfer
                ? ScanId != 0 && Revision != 0 && WorldId != 0 && Token != 0 && X < 6 && Y < 6
                    && (CrateId == 0 ? Destination == 255 : PlayerInventory.IsContainerView(Destination))
                : ScanId == 0 && Revision == 0 && WorldId == 0 && Token == 0 && CrateId == 0 && X == 0 && Y == 0 && Destination == 0)
            && (Action != NearbyAction.Close || Eye == Vector3.zero);
        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(RequestId); w.WriteUInt8((byte)Action);
            MountedWire.WriteVector(w, Eye);
            w.WriteUInt32(ScanId); w.WriteUInt32(Revision); w.WriteUInt32(WorldId); w.WriteUInt64(Token);
            w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt32(CrateId); w.WriteUInt8(Destination);
        }
        public static bool TryRead(NetPakReader r, out NearbyInventoryCommand value)
        {
            value = default; var next = new NearbyInventoryCommand();
            if (!r.ReadUInt32(out next.RequestId) || !r.ReadUInt8(out byte action) || !MountedWire.ReadVector(r, out next.Eye)
                || !r.ReadUInt32(out next.ScanId) || !r.ReadUInt32(out next.Revision) || !r.ReadUInt32(out next.WorldId)
                || !r.ReadUInt64(out next.Token) || !r.ReadUInt8(out next.X) || !r.ReadUInt8(out next.Y)
                || !r.ReadUInt32(out next.CrateId) || !r.ReadUInt8(out next.Destination)) return false;
            next.Action = (NearbyAction)action; if (!next.IsValid) return false;
            value = next; return true;
        }
    }

    public enum NearbyOperation : byte { Place, Split, EmptyFuel }
    /// <summary>Protocol 77 command 87: one exact accepted Nearby row, addressed
    /// exactly like Transfer, placed whole at a named cell, split N to a named
    /// cell (source DoSplitTo/OnSplitItem), or emptied (EmptyFuelSelected).
    /// CrateId zero names an owned page; nonzero only the currently viewed one.</summary>
    public struct NearbyOperationCommand
    {
        public uint RequestId, ScanId, Revision, WorldId, CrateId;
        public ulong Token;
        public byte X, Y, Page, ToX, ToY, ToRot;
        public ushort Amount;
        public NearbyOperation Operation;
        public Vector3 Eye;
        public bool IsValid => RequestId != 0 && Operation <= NearbyOperation.EmptyFuel && MountedWire.Finite(Eye)
            && ScanId != 0 && Revision != 0 && WorldId != 0 && Token != 0 && X < NearbyPacking.Side && Y < NearbyPacking.Side
            && (Operation == NearbyOperation.EmptyFuel
                ? Amount == 0 && CrateId == 0 && Page == 0 && ToX == 0 && ToY == 0 && ToRot == 0
                : ToRot < 4 && (Operation == NearbyOperation.Split) == (Amount != 0)
                    && (CrateId == 0 ? Page < PlayerInventory.OWNPAGES : PlayerInventory.IsContainerView(Page)));
        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(RequestId); w.WriteUInt8((byte)Operation); MountedWire.WriteVector(w, Eye);
            w.WriteUInt32(ScanId); w.WriteUInt32(Revision); w.WriteUInt32(WorldId); w.WriteUInt64(Token);
            w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(Amount); w.WriteUInt32(CrateId);
            w.WriteUInt8(Page); w.WriteUInt8(ToX); w.WriteUInt8(ToY); w.WriteUInt8(ToRot);
        }
        public static bool TryRead(NetPakReader r, out NearbyOperationCommand value)
        {
            value = default; var next = new NearbyOperationCommand();
            if (!r.ReadUInt32(out next.RequestId) || !r.ReadUInt8(out byte operation) || !MountedWire.ReadVector(r, out next.Eye)
                || !r.ReadUInt32(out next.ScanId) || !r.ReadUInt32(out next.Revision) || !r.ReadUInt32(out next.WorldId)
                || !r.ReadUInt64(out next.Token) || !r.ReadUInt8(out next.X) || !r.ReadUInt8(out next.Y)
                || !r.ReadUInt16(out next.Amount) || !r.ReadUInt32(out next.CrateId) || !r.ReadUInt8(out next.Page)
                || !r.ReadUInt8(out next.ToX) || !r.ReadUInt8(out next.ToY) || !r.ReadUInt8(out next.ToRot)) return false;
            next.Operation = (NearbyOperation)operation; if (!next.IsValid) return false;
            value = next; return true;
        }
    }

    /// <summary>Full Item projection, reusing the existing jar codec plus its
    /// explicitly missing/raw fields. System7 bytes remain unchanged. Nullable
    /// strings are bounded before allocation; fresh fuel/fluid sentinels survive.</summary>
    internal static class NearbyItemWire
    {
        static bool Text(string s) => s == null || Encoding.UTF8.GetByteCount(s) <= 127;
        internal static bool Valid(Item item) => item != null && item.id != 0
            && float.IsFinite(item.fuelLevel) && float.IsFinite(item.fluidAmount)
            && Text(item.gunChamberedType) && Text(item.magLoadedRound);
        static void WriteText(NetPakWriter w, string s) { w.WriteBit(s != null); if (s != null) w.WriteString(s, 7); }
        static bool ReadText(NetPakReader r, out string s)
        { s = null; return r.ReadBit(out bool has) && (!has || r.ReadString(out s, 7)); }
        internal static void Write(NetPakWriter w, ItemJar jar)
        {
            InventoryReplication.WriteJar(w, jar);
            var item = jar.item;
            w.WriteInt32(item.gunAmmo); w.WriteInt32(item.gunFiremode);
            w.WriteFloat(item.fuelLevel); w.WriteFloat(item.fluidAmount); w.WriteBit(item.preserved);
            WriteText(w, item.gunChamberedType); WriteText(w, item.magLoadedRound);
        }
        internal static bool Read(NetPakReader r, out ItemJar jar)
        {
            jar = null;
            if (!InventoryReplication.ReadJar(r, out byte x, out byte y, out byte rot, out var item)
                || !r.ReadInt32(out item.gunAmmo) || !r.ReadInt32(out item.gunFiremode)
                || !r.ReadFloat(out item.fuelLevel) || !r.ReadFloat(out item.fluidAmount) || !r.ReadBit(out item.preserved)
                || !ReadText(r, out item.gunChamberedType) || !ReadText(r, out item.magLoadedRound) || !Valid(item)) return false;
            jar = new(x, y, rot, item); return true;
        }
        internal static byte[] Bytes(Item item) => NetMessagePak.Pack(0, w => Write(w, new ItemJar(item)));
    }

    public struct NearbyInventoryEvent
    {
        public uint RequestId, ScanId, Revision, Tick;
        public NearbyResult Result;
        public bool HasCandidates;
        public NearbyPacking.Row[] Rows;
        public bool IsValid
        {
            get
            {
                if (RequestId == 0 || Revision == 0 || Result > NearbyResult.Denied) return false;
                var rows = Rows ?? Array.Empty<NearbyPacking.Row>();
                if (rows.Length > NearbyPacking.MaxRows || ((ScanId == 0 || !HasCandidates) && rows.Length != 0)) return false;
                var page = new Items(PlayerInventory.AREA); page.loadSize(6, 6);
                var identities = new Dictionary<uint, (ulong Token, ushort Id)>();
                var tokens = new Dictionary<ulong, uint>(); int total = 0;
                foreach (var row in rows)
                {
                    var jar = row?.Jar;
                    if (jar == null || !NearbyItemWire.Valid(jar.item) || jar.GetAsset() == null || jar.size_x == 0 || jar.size_y == 0 || jar.rot > 3
                        || !page.checkSpaceEmpty(jar.x, jar.y, jar.size_x, jar.size_y, jar.rot)) return false;
                    page.addItem(jar.x, jar.y, jar.rot, jar.item);
                    var parts = row.Sources;
                    if (parts == null || parts.Length == 0 || (total += parts.Length) > NearbyPacking.MaxContributions) return false;
                    var members = new HashSet<uint>(); int amount = 0;
                    foreach (var part in parts)
                    {
                        if (part.WorldId == 0 || part.Token == 0 || part.ItemId == 0 || !members.Add(part.WorldId)) return false;
                        if (identities.TryGetValue(part.WorldId, out var old) && old != (part.Token, part.ItemId)) return false;
                        if (tokens.TryGetValue(part.Token, out var oldId) && oldId != part.WorldId) return false;
                        identities[part.WorldId] = (part.Token, part.ItemId); tokens[part.Token] = part.WorldId;
                        if (part.ItemId != jar.item.id && !(Currency.IsCurrency(part.ItemId) && jar.item.id == Currency.StackId)) return false;
                        amount += part.Amount;
                    }
                    if (amount != jar.item.amount || (amount == 0 && parts.Length != 1)) return false;
                }
                return true;
            }
        }
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid Nearby projection");
            w.WriteUInt32(RequestId); w.WriteUInt32(ScanId); w.WriteUInt32(Revision); w.WriteUInt32(Tick);
            w.WriteUInt8((byte)Result); w.WriteBit(HasCandidates);
            var rows = Rows ?? Array.Empty<NearbyPacking.Row>(); w.WriteUInt8((byte)rows.Length);
            foreach (var row in rows)
            {
                NearbyItemWire.Write(w, row.Jar); w.WriteUInt16((ushort)row.Sources.Length);
                foreach (var p in row.Sources) { w.WriteUInt32(p.WorldId); w.WriteUInt64(p.Token); w.WriteUInt16(p.ItemId); w.WriteUInt16(p.Amount); }
            }
        }
        public static bool TryRead(NetPakReader r, out NearbyInventoryEvent value)
        {
            value = default; var next = new NearbyInventoryEvent();
            if (!r.ReadUInt32(out next.RequestId) || !r.ReadUInt32(out next.ScanId) || !r.ReadUInt32(out next.Revision)
                || !r.ReadUInt32(out next.Tick) || !r.ReadUInt8(out byte result) || !r.ReadBit(out next.HasCandidates)
                || !r.ReadUInt8(out byte count) || count > NearbyPacking.MaxRows) return false;
            next.Result = (NearbyResult)result; next.Rows = new NearbyPacking.Row[count]; int total = 0;
            for (int i = 0; i < count; i++)
            {
                if (!NearbyItemWire.Read(r, out var jar) || !r.ReadUInt16(out ushort n) || n == 0
                    || (total += n) > NearbyPacking.MaxContributions) return false;
                var parts = new NearbyPacking.Contribution[n];
                for (int j = 0; j < n; j++)
                {
                    if (!r.ReadUInt32(out uint id) || !r.ReadUInt64(out ulong token)
                        || !r.ReadUInt16(out ushort item) || !r.ReadUInt16(out ushort amount)) return false;
                    parts[j] = new(id, token, item, amount);
                }
                next.Rows[i] = new(jar, parts);
            }
            if (!next.IsValid) return false; value = next; return true;
        }
    }

    public sealed partial class NetWorldClient
    {
        public NearbyInventoryEvent Nearby { get; private set; }
        public event Action<NearbyInventoryEvent> NearbyChanged;
        void RegisterNearbyEvents() => Events.Register<NearbyInventoryEvent>(ReplicationIds.EventNearbyInventory,
            NearbyInventoryEvent.TryRead, value => {
                if (Nearby.Revision != 0 && unchecked((int)(value.Revision - Nearby.Revision)) <= 0) return;
                Nearby = value; NearbyChanged?.Invoke(value);
            });
        void ResetNearby() => Nearby = default;
        public bool SendNearbyInventory(NearbyInventoryCommand command) => command.IsValid
            && SendCommand(ReplicationIds.CommandNearbyInventory, command.Write);
        public bool SendNearbyOperation(NearbyOperationCommand command) => command.IsValid
            && SendCommand(ReplicationIds.CommandNearbyOperation, command.Write);
    }
}
