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
            // Commands 86 and 87 share one monotonic ledger: exact replay
            // of either wire request resends its reply and never re-applies.
            public uint LastRequestId;
            public byte[] LastRequestBytes;
            public NearbyInventoryEvent Reply, Current;
            public readonly List<(uint Id, ulong Token)> Order = new();
            public readonly Dictionary<uint, int> Quota = new();
            public readonly Dictionary<uint, byte[]> Expected = new();
            public byte[] Picture;
            public bool Found;
        }
        readonly Dictionary<ushort, NearbySession> _nearby = new();
        void ForgetNearby(ushort owner) => _nearby.Remove(owner);
        void RegisterNearbyCommands()
        {
            Commands.Register<NearbyInventoryCommand>(ReplicationIds.CommandNearbyInventory, NearbyInventoryCommand.TryRead, HandleNearby);
            Commands.Register<NearbyOperationCommand>(ReplicationIds.CommandNearbyOperation, NearbyOperationCommand.TryRead, HandleNearbyOperation);
            Commands.Register<NearbyMagazineTransferCommand>(ReplicationIds.CommandNearbyMagazineTransfer,
                NearbyMagazineTransferCommand.TryRead, HandleNearbyMagazine);
        }
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
                // A non-stacking row was admitted whole; its amount is state
                // (magazine rounds), not a stack quota, and a ground load grows it.
                int units = NearbyUnits(item.ServerItem);
                bool whole = !Currency.IsCurrency(item.ServerItem.id) && (item.ServerItem.GetAsset()?.stackSize ?? 1) <= 1;
                quota = whole ? units : Math.Min(quota, units); session.Quota[id] = quota;
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
        bool NearbyReplay(ushort owner, NearbySession session, uint request, byte[] bytes)
        {
            if (session.LastRequestId == 0 || unchecked((int)(request - session.LastRequestId)) > 0) return false;
            if (bytes.AsSpan().SequenceEqual(session.LastRequestBytes)) SendNearby(owner, session.Reply);
            else PublishNearby(owner, session, request, NearbyResult.Stale, true);
            return true;
        }
        static void Remember(NearbySession session, uint request, byte[] bytes)
        { session.LastRequestId = request; session.LastRequestBytes = bytes; session.Reply = session.Current; }
        static void ForgetScan(NearbySession session) { session.Current.ScanId = 0; session.Order.Clear(); session.Quota.Clear(); }
        void HandleNearby(ushort owner, NearbyInventoryCommand command)
        {
            if (!_nearby.TryGetValue(owner, out var session)) _nearby[owner] = session = new();
            var bytes = NetMessagePak.Pack(ReplicationIds.CommandNearbyInventory, command.Write);
            if (NearbyReplay(owner, session, command.RequestId, bytes)) return;
            NearbyResult result;
            if (command.Action == NearbyAction.Close) { ForgetScan(session); result = NearbyResult.Closed; }
            else if (!CombatState.IsAlive(owner)) { ForgetScan(session); result = NearbyResult.Dead; }
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
            Remember(session, command.RequestId, bytes);
        }
        /// <summary>The admission every Nearby action shares: the current scan and
        /// revision, the exact row root and first contributor, then every
        /// contributor's live ID/token/full bytes and current range/LOS.</summary>
        NearbyResult AdmitNearbyRow(NearbySession session, uint scan, uint revision, byte x, byte y, uint worldId, ulong token,
            Vector3 eye, out NearbyPacking.Row row)
        {
            row = null;
            if (session.Current.ScanId == 0 || scan != session.Current.ScanId || revision != session.Current.Revision) return NearbyResult.Stale;
            var found = session.Current.Rows.FirstOrDefault(r => r.Jar.x == x && r.Jar.y == y);
            if (found == null || found.Sources[0].WorldId != worldId || found.Sources[0].Token != token) return NearbyResult.Stale;
            foreach (var part in found.Sources)
                if (!WorldItems.TryGet(part.WorldId, out var e) || e.ServerItem == null || e.ServerItem.id != part.ItemId
                    || Inventories.ItemIdentityProvider(e.ServerItem) != part.Token || !NearbyReach(e, eye)
                    || !session.Expected.TryGetValue(part.WorldId, out var expected)
                    || !NearbyItemWire.Bytes(e.ServerItem).AsSpan().SequenceEqual(expected)) return NearbyResult.Denied;
            row = found; return NearbyResult.Ready;
        }
        NearbyResult TransferNearby(ushort owner, NearbyInventoryCommand command, NearbySession session)
        {
            var admitted = AdmitNearbyRow(session, command.ScanId, command.Revision, command.X, command.Y,
                command.WorldId, command.Token, command.Eye, out var row);
            if (admitted != NearbyResult.Ready) return admitted;
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
            DebitNearby(session, row, before, taken);
            Inventories.ServerMarkDirty(owner);
            if (command.CrateId != 0) Inventories.ServerRepaintCrateViewers(command.CrateId);
            return NearbyResult.Transferred;
        }
        void DebitNearby(NearbySession session, NearbyPacking.Row row, int before, int taken)
        {
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
        }
        void HandleNearbyOperation(ushort owner, NearbyOperationCommand command)
        {
            if (!_nearby.TryGetValue(owner, out var session)) _nearby[owner] = session = new();
            var bytes = NetMessagePak.Pack(ReplicationIds.CommandNearbyOperation, command.Write);
            if (NearbyReplay(owner, session, command.RequestId, bytes)) return;
            NearbyResult result;
            if (!CombatState.IsAlive(owner)) { ForgetScan(session); result = NearbyResult.Dead; }
            else if (!NearbyEye(owner, command.Eye)) result = NearbyResult.InvalidEye;
            else if (NearbyVisible == null || Inventories.ItemIdentityProvider == null || !Inventories.TryGet(owner, out _)) result = NearbyResult.Unavailable;
            else result = OperateNearby(owner, command, session);
            PublishNearby(owner, session, command.RequestId, result, true);
            Remember(session, command.RequestId, bytes);
        }
        NearbyResult OperateNearby(ushort owner, NearbyOperationCommand command, NearbySession session)
        {
            var admitted = AdmitNearbyRow(session, command.ScanId, command.Revision, command.X, command.Y,
                command.WorldId, command.Token, command.Eye, out var row);
            if (admitted != NearbyResult.Ready) return admitted;
            if (!Players.TryGetByOwner(owner, out var player) || !Inventories.TryGet(owner, out var entry)) return NearbyResult.Denied;
            if (command.Operation == NearbyOperation.EmptyFuel)
            {
                // Source EmptyFuelSelected writes fuelLevel=0 on the selected jar,
                // which for a ground row is the live world item itself.
                if (row.Sources.Length != 1 || !WorldItems.TryGet(row.Sources[0].WorldId, out var can)
                    || can.ServerItem.GetAsset()?.IsFuelContainer != true) return NearbyResult.Denied;
                can.ServerItem.fuelLevel = 0;
                return NearbyResult.Transferred;
            }
            var grid = command.CrateId == 0 ? entry.Inventory.items[command.Page]
                : Inventories.ServerNearbyDestination(owner, command.CrateId, command.Page, player.Pos);
            if (grid == null) return NearbyResult.Denied;
            var source = row.Jar.item; int before = source.amount, taken;
            var probe = new ItemJar(source);
            if (command.Operation == NearbyOperation.Place)
            {
                // PlayerInventory.TryDrag's move-onto-empty rule for a different
                // page: holster rule, rotation-aware empty footprint, hand slots at
                // their single cell with rot 0. A ground row has nowhere to receive
                // a swapped occupant, so an occupied target is refused.
                bool slot = command.Page < PlayerInventory.SLOTS;
                byte x = slot ? (byte)0 : command.ToX, y = slot ? (byte)0 : command.ToY, rot = slot ? (byte)0 : command.ToRot;
                if (source.GetAsset()?.slot.CanOccupyPage(command.Page) != true
                    || !grid.checkSpaceEmpty(x, y, probe.size_x, probe.size_y, rot)) return NearbyResult.NoRoom;
                grid.addItem(x, y, rot, source.Clone());
                taken = before;
            }
            else
            {
                // ServerTransactions.OnSplitItem's named-cell branch against the
                // projected row: never the whole stack; merge onto the same ID at
                // that cell up to its stack size, otherwise an empty footprint.
                if (command.Amount >= before) return NearbyResult.Denied;
                byte at = grid.getIndex(command.ToX, command.ToY);
                if (at != byte.MaxValue)
                {
                    var into = grid.getItem(at);
                    if (into?.item == null || into.item.id != source.id) return NearbyResult.NoRoom;
                    int room = Math.Max(1, Assets.find(into.item.id)?.stackSize ?? 1) - into.item.amount;
                    if (room <= 0) return NearbyResult.NoRoom;
                    taken = Math.Min(command.Amount, room);
                    into.item.amount = (ushort)(into.item.amount + taken);
                    grid.raiseStateUpdated();
                }
                else
                {
                    if (!grid.checkSpaceEmpty(command.ToX, command.ToY, probe.size_x, probe.size_y, command.ToRot)) return NearbyResult.NoRoom;
                    var made = source.Clone(); made.amount = command.Amount;
                    grid.addItem(command.ToX, command.ToY, command.ToRot, made);
                    taken = command.Amount;
                }
            }
            DebitNearby(session, row, before, taken);
            Inventories.ServerMarkDirty(owner);
            if (command.CrateId != 0) Inventories.ServerRepaintCrateViewers(command.CrateId);
            return NearbyResult.Transferred;
        }
        void HandleNearbyMagazine(ushort owner, NearbyMagazineTransferCommand command)
        {
            if (!_nearby.TryGetValue(owner, out var session)) _nearby[owner] = session = new();
            Transactions.ProcessMagazineRequest(owner, command.RequestId, command.MagazineToken, command,
                () => ApplyNearbyMagazine(owner, command, session));
        }
        /// <summary>Source StepMagOp on the ground page: the round or magazine is
        /// the live world item behind an admitted row, never the display clone.
        /// Rules and add-before-decrement order match ApplyMagazineTransfer.</summary>
        (MagazineTransferResult, ushort) ApplyNearbyMagazine(ushort owner, NearbyMagazineTransferCommand command, NearbySession session)
        {
            if (!Players.TryGetByOwner(owner, out var player) || !Inventories.TryGet(owner, out var entry)) return (MagazineTransferResult.MissingInventory, 0);
            if (!CombatState.IsAlive(owner)) return (MagazineTransferResult.Dead, 0);
            bool usable = NearbyVisible != null && Inventories.ItemIdentityProvider != null && NearbyEye(owner, command.Eye);
            NearbyPacking.Row Ground(NearbyRowRef r) => usable && AdmitNearbyRow(session, command.ScanId, command.Revision, r.X, r.Y,
                r.WorldId, r.Token, command.Eye, out var row) == NearbyResult.Ready ? row : null;
            var inv = entry.Inventory;
            Item magazine; uint magazineWorld = 0;
            if (command.MagazineGround)
            {
                var row = Ground(command.MagazineRow);
                if (row == null || row.Sources.Length != 1 || !WorldItems.TryGet(row.Sources[0].WorldId, out var e))
                    return (MagazineTransferResult.StaleMagazine, 0);
                magazine = e.ServerItem; magazineWorld = e.NetIdValue;
            }
            else if ((magazine = Inventories.ServerResolveScoped(owner, command.Magazine, player.Pos)) == null)
                return (MagazineTransferResult.StaleMagazine, 0);
            var magAsset = Assets.find(magazine.id);
            if (magAsset == null || !magAsset.IsMagazine) return (MagazineTransferResult.NotMagazine, 0);
            if (command.Unloading)
            {
                var roundAsset = Assets.find(command.UnloadRoundId);
                string held = MagRules.EffectiveRound(magazine, magAsset);
                if (magazine.amount <= 0 || roundAsset == null || roundAsset.IsMagazine || held == null || held != roundAsset.magRound)
                    return (MagazineTransferResult.RuleRefused, 0);
                if (!inv.tryAddItem(new Item(command.UnloadRoundId, 1))) return (MagazineTransferResult.NoRoom, 0);
                MagRules.ApplyUnload(magazine, magAsset);
            }
            else
            {
                NearbyPacking.Row roundRow = null; Items roundPage = null; byte roundIndex = byte.MaxValue; Item round;
                if (command.RoundGround)
                {
                    if ((roundRow = Ground(command.RoundRow)) == null) return (MagazineTransferResult.StaleRound, 0);
                    round = roundRow.Jar.item;
                }
                else
                {
                    var live = Inventories.ServerResolveScoped(owner, command.Round, player.Pos);
                    roundPage = live == null ? null : inv.items[command.Round.Page];
                    roundIndex = roundPage?.getIndex(command.Round.X, command.Round.Y) ?? byte.MaxValue;
                    if (roundIndex == byte.MaxValue || !ReferenceEquals(roundPage.getItem(roundIndex).item, live)) return (MagazineTransferResult.StaleRound, 0);
                    round = live;
                }
                if (round.amount <= 0) return (MagazineTransferResult.StaleRound, 0);
                if (!MagRules.ApplyLoad(magazine, magAsset, Assets.find(round.id))) return (MagazineTransferResult.RuleRefused, 0);
                // Spend only after the load is committed: one round of the exact
                // ground contributor in source order, or of the exact grid stack.
                if (roundRow != null) DebitNearby(session, roundRow, round.amount, 1);
                else if (--round.amount <= 0) roundPage.removeItem(roundIndex);
                if (command.Round.CrateId != 0) Inventories.ServerRepaintCrateViewers(command.Round.CrateId);
            }
            if (magazineWorld != 0) WorldItems.ServerItemChanged(magazineWorld, Session.CurrentTick);
            if (command.Magazine.CrateId != 0) Inventories.ServerRepaintCrateViewers(command.Magazine.CrateId);
            Inventories.ServerMarkDirty(owner);
            // The owner's ground picture changes in this tick, before Event60.
            PublishNearby(owner, session, session.LastRequestId, NearbyResult.Ready, false);
            return (MagazineTransferResult.Applied, magazine.amount);
        }
        void RefreshNearby()
        {
            foreach (var pair in _nearby)
            {
                var session = pair.Value; if (session.Current.ScanId == 0) continue;
                if (!CombatState.IsAlive(pair.Key)) ForgetScan(session);
                PublishNearby(pair.Key, session, session.LastRequestId, NearbyResult.Ready, false);
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
