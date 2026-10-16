using System;
using System.Collections.Generic;
using System.Linq;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public sealed partial class NetWorldServer
    {
        /// <summary>Host-owned model-box/pose/source-world ray adapter. A missing
        /// adapter refuses the feature, rather than claiming through-wall access.</summary>
        public Func<WorldItemReplication.WorldItemEntity, Vector3, bool> NearbyVisible;
        sealed class NearbySession
        {
            public NearbyInventoryCommand LastRequest;
            public NearbyInventoryEvent Reply, Current;
            public readonly List<(uint Id, ulong Token)> Order = new();
            public readonly Dictionary<uint, int> Quota = new();
            public readonly Dictionary<uint, byte[]> Expected = new();
            public byte[] Picture;
            public bool Found;
        }
        readonly Dictionary<ushort, NearbySession> _nearby = new();
        void ForgetNearby(ushort owner) => _nearby.Remove(owner);
        void RegisterNearbyCommands() => Commands.Register<NearbyInventoryCommand>(ReplicationIds.CommandNearbyInventory,
            NearbyInventoryCommand.TryRead, HandleNearby);
        bool NearbyEye(ushort owner, Vector3 eye) => Players.TryGetByOwner(owner, out var p)
            && (CraftingEyeAllowed?.Invoke(owner, eye)
                ?? (eye - p.Pos).sqrMagnitude <= Combat.DefaultGun.MaxAimOriginOffset * Combat.DefaultGun.MaxAimOriginOffset);
        bool NearbyReach(WorldItemReplication.WorldItemEntity item, Vector3 eye) => (item.Pos - eye).sqrMagnitude < 16f
            && NearbyVisible?.Invoke(item, eye) == true;
        void SendNearby(ushort owner, NearbyInventoryEvent value) => SendEventTo(owner,
            NetMessagePak.Pack(ReplicationIds.EventNearbyInventory, value.Write));
        static int NearbyUnits(Item item) => Currency.IsCurrency(item.id)
            ? Currency.ValueOf(item.id) * Math.Max(1, (int)item.amount) : item.amount;
        NearbyPacking.View NearbyPicture(NearbySession session)
        {
            var sources = new List<NearbyPacking.Source>(); session.Expected.Clear();
            foreach (var (id, token) in session.Order)
            {
                if (!WorldItems.TryGet(id, out var item) || item.ServerItem == null
                    || Inventories.ItemIdentityProvider(item.ServerItem) != token || !session.Quota.TryGetValue(id, out int quota)) continue;
                quota = Math.Min(quota, NearbyUnits(item.ServerItem)); session.Quota[id] = quota;
                var display = item.ServerItem.Clone();
                if (Currency.IsCurrency(display.id)) display.id = Currency.StackId;
                display.amount = checked((ushort)quota);
                session.Expected[id] = NearbyItemWire.Bytes(item.ServerItem);
                sources.Add(new(id, token, item.ServerItem, display));
            }
            var packed = NearbyPacking.Pack(sources);
            return new(session.Found, packed.Rows);
        }
        void PublishNearby(ushort owner, NearbySession session, uint request, NearbyResult result, bool force)
        {
            var picture = session.Current.ScanId == 0 ? new NearbyPacking.View(false, Array.Empty<NearbyPacking.Row>()) : NearbyPicture(session);
            var next = new NearbyInventoryEvent { RequestId = request, ScanId = session.Current.ScanId,
                Revision = 1, Tick = 0, Result = NearbyResult.Ready, HasCandidates = picture.HasCandidates, Rows = picture.Rows };
            // Compare accepted item bytes, not only id/amount: attachments,
            // cartridge locks, fuel, spoilage and water all repaint correctly.
            var bytes = NetMessagePak.Pack(ReplicationIds.EventNearbyInventory, next.Write);
            if (!force && session.Picture != null && bytes.AsSpan().SequenceEqual(session.Picture)) return;
            next.Revision = unchecked(session.Current.Revision + 1); if (next.Revision == 0) next.Revision = 1;
            next.Tick = unchecked((uint)Session.CurrentTick); next.Result = result;
            session.Picture = bytes; session.Current = next; SendNearby(owner, next);
        }
        void HandleNearby(ushort owner, NearbyInventoryCommand command)
        {
            if (!_nearby.TryGetValue(owner, out var session)) _nearby[owner] = session = new();
            if (session.LastRequest.RequestId != 0 && unchecked((int)(command.RequestId - session.LastRequest.RequestId)) <= 0)
            {
                if (command.Equals(session.LastRequest)) SendNearby(owner, session.Reply);
                else PublishNearby(owner, session, command.RequestId, NearbyResult.Stale, true);
                return;
            }
            NearbyResult result;
            if (command.Action == NearbyAction.Close)
            { session.Current.ScanId = 0; session.Order.Clear(); session.Quota.Clear(); result = NearbyResult.Closed; }
            else if (!CombatState.IsAlive(owner))
            { session.Current.ScanId = 0; session.Order.Clear(); session.Quota.Clear(); result = NearbyResult.Dead; }
            else if (!NearbyEye(owner, command.Eye)) result = NearbyResult.InvalidEye;
            else if (NearbyVisible == null || Inventories.ItemIdentityProvider == null || !Inventories.TryGet(owner, out _)) result = NearbyResult.Unavailable;
            else if (command.Action == NearbyAction.Open)
            {
                session.Order.Clear(); session.Quota.Clear(); session.Current.ScanId = command.RequestId;
                try
                {
                    var packed = NearbyPacking.Pack(WorldItems.All.OrderBy(e => e.SpawnOrder)
                        .Where(e => e.ServerItem != null && NearbyReach(e, command.Eye))
                        .Select(e => new NearbyPacking.Source(e.NetIdValue, Inventories.ItemIdentityProvider(e.ServerItem), e.ServerItem)));
                    session.Found = packed.HasCandidates;
                    foreach (var row in packed.Rows) foreach (var part in row.Sources)
                    {
                        if (!session.Quota.ContainsKey(part.WorldId)) session.Order.Add((part.WorldId, part.Token));
                        session.Quota[part.WorldId] = session.Quota.GetValueOrDefault(part.WorldId) + part.Amount;
                    }
                    result = NearbyResult.Ready;
                }
                catch (InvalidOperationException) { session.Current.ScanId = 0; result = NearbyResult.Unavailable; }
            }
            else result = TransferNearby(owner, command, session);
            PublishNearby(owner, session, command.RequestId, result, true);
            session.LastRequest = command; session.Reply = session.Current;
        }
        NearbyResult TransferNearby(ushort owner, NearbyInventoryCommand command, NearbySession session)
        {
            if (session.Current.ScanId == 0 || command.ScanId != session.Current.ScanId || command.Revision != session.Current.Revision) return NearbyResult.Stale;
            var row = session.Current.Rows.FirstOrDefault(r => r.Jar.x == command.X && r.Jar.y == command.Y);
            if (row == null || row.Sources[0].WorldId != command.WorldId || row.Sources[0].Token != command.Token) return NearbyResult.Stale;
            foreach (var part in row.Sources)
                if (!WorldItems.TryGet(part.WorldId, out var e) || e.ServerItem == null || e.ServerItem.id != part.ItemId
                    || Inventories.ItemIdentityProvider(e.ServerItem) != part.Token || !NearbyReach(e, command.Eye)
                    || !session.Expected.TryGetValue(part.WorldId, out var expected)
                    || !NearbyItemWire.Bytes(e.ServerItem).AsSpan().SequenceEqual(expected)) return NearbyResult.Denied;
            if (!Players.TryGetByOwner(owner, out var player) || !Inventories.TryGet(owner, out var entry)) return NearbyResult.Denied;
            var destination = command.CrateId == 0 ? null : Inventories.ServerNearbyDestination(owner, command.CrateId, command.Destination, player.Pos);
            if (command.CrateId != 0 && destination == null) return NearbyResult.Denied;
            var moving = row.Jar.item.Clone(); int before = moving.amount;
            bool placed = destination == null ? entry.Inventory.tryAddItem(moving) : destination.tryAddItem(moving);
            int taken = placed ? before : before - moving.amount;
            if (!placed && taken == 0) return NearbyResult.NoRoom;
            // The grid's source operation has decided exactly how much fitted.
            // Debit only those contributions; partial/full-bag overflow remains
            // live in the world, never as a synthetic server AREA jar.
            foreach (var part in row.Sources)
            {
                int debit = Math.Min(taken, part.Amount);
                if (debit == 0 && before != 0) continue;
                WorldItems.TryGet(part.WorldId, out var e);
                int remaining = NearbyUnits(e.ServerItem) - debit;
                session.Quota[part.WorldId] -= debit;
                if (session.Quota[part.WorldId] == 0) session.Quota.Remove(part.WorldId);
                if (remaining == 0) { Transactions.RemoveWorldItem(part.WorldId); session.Quota.Remove(part.WorldId); }
                else
                {
                    if (Currency.IsCurrency(e.ServerItem.id))
                    {
                        if (remaining <= ushort.MaxValue) { e.ServerItem.id = Currency.StackId; e.ServerItem.amount = (ushort)remaining; }
                        else
                        {
                            int denomination = Currency.ValueOf(e.ServerItem.id);
                            e.ServerItem.amount = checked((ushort)(remaining / denomination));
                            int change = remaining % denomination;
                            if (change > 0) { var extra = e.ServerItem.Clone(); extra.id = Currency.StackId; extra.amount = (ushort)change;
                                Transactions.SpawnWorldItem(extra, e.Pos, Vector3.zero); }
                        }
                    }
                    else e.ServerItem.amount = (ushort)remaining;
                    WorldItems.ServerItemChanged(e.NetIdValue, Session.CurrentTick);
                }
                taken -= debit; if (taken == 0) break;
            }
            Inventories.ServerMarkDirty(owner);
            if (command.CrateId != 0) Inventories.ServerRepaintCrateViewers(command.CrateId);
            return NearbyResult.Transferred;
        }
        void RefreshNearby()
        {
            foreach (var pair in _nearby)
            {
                var session = pair.Value; if (session.Current.ScanId == 0) continue;
                if (!CombatState.IsAlive(pair.Key)) { session.Current.ScanId = 0; session.Order.Clear(); session.Quota.Clear(); }
                PublishNearby(pair.Key, session, session.LastRequest.RequestId, NearbyResult.Ready, false);
            }
        }
    }
    public sealed partial class InventoryReplication
    {
        internal Items ServerNearbyDestination(ushort owner, uint crateId, byte page, Vector3 position)
        {
            if (!IsLiveNearbyDestination(owner, crateId, page, position, out var entry)) return null;
            // Existing view mutation callbacks synchronously push the backing
            // grid and refresh every other viewer through the same source path.
            return entry.Inventory.items[page];
        }
        bool IsLiveNearbyDestination(ushort owner, uint crateId, byte page, Vector3 position, out PlayerEntry entry)
        {
            entry = null;
            return PlayerInventory.IsContainerView(page) && _byOwner.TryGetValue(owner, out entry)
                && entry.OpenCrateId == crateId && _crates.TryGetValue(crateId, out var crate)
                && crate.Viewers.Contains(owner) && crate.CanAccess(owner, position)
                && TryBacking(crate, page, out _, out byte width, out byte height)
                && entry.Inventory.items[page].width == width && entry.Inventory.items[page].height == height;
        }
    }
}
