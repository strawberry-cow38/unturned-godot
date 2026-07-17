using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // ---------------------------------------------------------------------------------------------------
    // Inventory (MP_PLAN §3.3): the server owns every PlayerInventory; all mutations are commands validated
    // against the SERVER grid -- the ported tryFindSpace/checkSpaceDrag/TryDrag logic IS the validator, so
    // an illegal move is rejected by the same cell math that makes it illegal in single-player. The owner
    // gets an owner-only Snap block: the FULL inventory, re-sent when dirty (keyed on the model layer's
    // existing onStateUpdated events) -- inventories are small and change on discrete player actions, so
    // whole-state-on-dirty is the honest delta.
    // ---------------------------------------------------------------------------------------------------

    public struct MoveItemCommand
    {
        public byte Page0, X0, Y0;
        public byte Page1, X1, Y1, Rot1;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt8(Page0); w.WriteUInt8(X0); w.WriteUInt8(Y0);
            w.WriteUInt8(Page1); w.WriteUInt8(X1); w.WriteUInt8(Y1); w.WriteUInt8(Rot1);
        }

        public static bool TryRead(NetPakReader r, out MoveItemCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p0) || !r.ReadUInt8(out byte x0) || !r.ReadUInt8(out byte y0)) return false;
            if (!r.ReadUInt8(out byte p1) || !r.ReadUInt8(out byte x1) || !r.ReadUInt8(out byte y1) || !r.ReadUInt8(out byte rot)) return false;
            cmd = new MoveItemCommand { Page0 = p0, X0 = x0, Y0 = y0, Page1 = p1, X1 = x1, Y1 = y1, Rot1 = rot };
            return true;
        }
    }

    public struct DropItemCommand
    {
        public byte Page, X, Y;
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); }
        public static bool TryRead(NetPakReader r, out DropItemCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)) return false;
            cmd = new DropItemCommand { Page = p, X = x, Y = y };
            return true;
        }
    }

    public struct PickupItemCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out PickupItemCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new PickupItemCommand { NetId = id };
            return true;
        }
    }

    public struct EquipItemCommand
    {
        public byte FromPage, X, Y, Slot;   // Slot: 0 primary / 1 secondary
        public void Write(NetPakWriter w) { w.WriteUInt8(FromPage); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt8(Slot); }
        public static bool TryRead(NetPakReader r, out EquipItemCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y) || !r.ReadUInt8(out byte s)) return false;
            cmd = new EquipItemCommand { FromPage = p, X = x, Y = y, Slot = s };
            return true;
        }
    }

    public struct CraftCommand
    {
        public ushort BlueprintIndex;   // index into the host-registered blueprint catalog (same list both sides)
        public void Write(NetPakWriter w) => w.WriteUInt16(BlueprintIndex);
        public static bool TryRead(NetPakReader r, out CraftCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort idx)) return false;
            cmd = new CraftCommand { BlueprintIndex = idx };
            return true;
        }
    }

    public struct ConsumeCommand
    {
        public byte Page, X, Y;
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); }
        public static bool TryRead(NetPakReader r, out ConsumeCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)) return false;
            cmd = new ConsumeCommand { Page = p, X = x, Y = y };
            return true;
        }
    }

    public struct OpenStorageCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out OpenStorageCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new OpenStorageCommand { NetId = id };
            return true;
        }
    }

    public struct CloseStorageCommand
    {
        public void Write(NetPakWriter w) { }
        public static bool TryRead(NetPakReader r, out CloseStorageCommand cmd) { cmd = default; return true; }
    }

    public struct StorageOpenedEvent
    {
        public uint NetId;
        public byte Width, Height;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteUInt8(Width); w.WriteUInt8(Height); }
        public static bool TryRead(NetPakReader r, out StorageOpenedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id) || !r.ReadUInt8(out byte width) || !r.ReadUInt8(out byte height)) return false;
            evt = new StorageOpenedEvent { NetId = id, Width = width, Height = height };
            return true;
        }
    }

    public struct StorageClosedEvent
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out StorageClosedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new StorageClosedEvent { NetId = id };
            return true;
        }
    }

    /// <summary>DevConsole mutations as a command (§2.3 "all state mutation goes through commands --
    /// including DevConsole cheats"): the raw line crosses the wire; the SERVER parses, whitelists, and
    /// applies against its own authoritative state. A client build can no longer grant itself anything.</summary>
    public struct ConsoleCommand
    {
        public string Text;
        public void Write(NetPakWriter w) => w.WriteString(Text ?? "");
        public static bool TryRead(NetPakReader r, out ConsoleCommand cmd)
        {
            cmd = default;
            if (!r.ReadString(out string text)) return false;
            cmd = new ConsoleCommand { Text = text };
            return true;
        }
    }

    public struct ConsoleResultEvent
    {
        public string Text;
        public void Write(NetPakWriter w) => w.WriteString(Text ?? "");
        public static bool TryRead(NetPakReader r, out ConsoleResultEvent evt)
        {
            evt = default;
            if (!r.ReadString(out string text)) return false;
            evt = new ConsoleResultEvent { Text = text };
            return true;
        }
    }

    /// <summary>
    /// Player inventories as an IReplicatedSystem (SystemId 7) -- the second owner-only block (§2.6): each
    /// client's snapshot carries at most ONE entry, its own full inventory (9 pages of jars + worn
    /// clothing). Other players' inventories never reach you (their look is the appearance snapshot's job,
    /// a deferred system). Server side holds a real PlayerInventory per player plus the storage-crate pages
    /// (an Items page addressed by the crate's NetId, §3.7) with one-opener-at-a-time arbitration; opening
    /// mirrors the SP mechanic exactly -- the crate grid is loaded into the opener's STORAGE page (7), so
    /// MoveItem needs no special crate addressing and the owner block carries the view for free.
    /// </summary>
    public sealed class InventoryReplication : IReplicatedSystem
    {
        public sealed class PlayerEntry
        {
            public ushort OwnerPlayerId;
            public PlayerInventory Inventory = new PlayerInventory();
            public uint OpenCrateId;      // 0 = none (server-side arbitration state)
            public long LastChangedTick;
            internal bool Dirty;          // set by the model's onStateUpdated; stamped to a tick by ServerCommitDirty
        }

        public sealed class CrateEntry
        {
            public uint NetIdValue;
            public Items Storage;
            public byte Width, Height;
            public Vector3 Pos;
            public ushort OpenBy;         // 0 = closed
        }

        /// <summary>Server-side crate interaction reach. SP opens at 2.5 m (OpenNearestCrate); the server
        /// allows slack for replication-grid rounding and eye-vs-feet geometry.</summary>
        public const float StorageReach = 4f;

        public byte SystemId => ReplicationIds.SystemInventory;

        readonly Dictionary<ushort, PlayerEntry> _byOwner = new Dictionary<ushort, PlayerEntry>();
        readonly Dictionary<uint, CrateEntry> _crates = new Dictionary<uint, CrateEntry>();

        /// <summary>Client side: fires after ReadSnapshot rebuilt my replica (UI refresh hook).</summary>
        public event Action<ushort> ReplicaUpdated;

        public int Count => _byOwner.Count;

        public bool TryGet(ushort ownerPlayerId, out PlayerEntry entry) => _byOwner.TryGetValue(ownerPlayerId, out entry);

        public bool TryGetCrate(uint netId, out CrateEntry crate) => _crates.TryGetValue(netId, out crate);

        // ---- server side ----

        public PlayerEntry ServerAdd(ushort ownerPlayerId, long tick)
        {
            var e = new PlayerEntry { OwnerPlayerId = ownerPlayerId, LastChangedTick = tick };
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                // any grid mutation marks the owner dirty -- the same onStateUpdated dirtiness SP's UI keys on
                e.Inventory.items[p].onStateUpdated += () => e.Dirty = true;
            }
            _byOwner[ownerPlayerId] = e;
            return e;
        }

        public void ServerRemove(ushort ownerPlayerId, long tick)
        {
            if (_byOwner.TryGetValue(ownerPlayerId, out var e) && e.OpenCrateId != 0)
                ServerCloseStorage(ownerPlayerId, tick);   // a vanishing opener must not wedge the crate shut
            _byOwner.Remove(ownerPlayerId);
        }

        /// <summary>Stamp this tick onto every entry the last dispatch round dirtied. Call once per server
        /// tick, after command dispatch, so the delta baseline math sees a real tick number.</summary>
        public void ServerCommitDirty(long tick)
        {
            foreach (var e in _byOwner.Values)
                if (e.Dirty) { e.Dirty = false; e.LastChangedTick = tick; }
        }

        public CrateEntry ServerRegisterCrate(NetId id, byte width, byte height, Vector3 pos)
        {
            var c = new CrateEntry { NetIdValue = id.Value, Width = width, Height = height, Pos = pos, Storage = new Items(PlayerInventory.STORAGE) };
            c.Storage.loadSize(width, height);
            _crates[id.Value] = c;
            return c;
        }

        /// <summary>Open arbitration (§3.7: one opener at a time, server-enforced). On success the crate
        /// grid is copied into the opener's STORAGE page -- the exact SP OpenNearestCrate mechanic.</summary>
        public bool ServerOpenStorage(ushort ownerPlayerId, uint crateId, Vector3 senderPos, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return false;
            if (!_crates.TryGetValue(crateId, out var crate)) return false;
            if (crate.OpenBy != 0 && crate.OpenBy != ownerPlayerId) return false;   // someone else has it open
            if ((crate.Pos - senderPos).magnitude > StorageReach) return false;
            if (e.OpenCrateId != 0 && e.OpenCrateId != crateId) ServerCloseStorage(ownerPlayerId, tick);   // implicit close-then-open

            crate.OpenBy = ownerPlayerId;
            e.OpenCrateId = crateId;
            CopyPage(crate.Storage, e.Inventory.items[PlayerInventory.STORAGE], crate.Width, crate.Height);
            e.Dirty = true;
            return true;
        }

        /// <summary>Close = save the STORAGE page back into the crate and clear the view (SP CloseCrate).</summary>
        public bool ServerCloseStorage(ushort ownerPlayerId, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e) || e.OpenCrateId == 0) return false;
            if (_crates.TryGetValue(e.OpenCrateId, out var crate))
            {
                CopyPage(e.Inventory.items[PlayerInventory.STORAGE], crate.Storage, crate.Width, crate.Height);
                crate.OpenBy = 0;
            }
            var s = e.Inventory.items[PlayerInventory.STORAGE];
            s.clear();
            s.loadSize(0, 0);
            e.OpenCrateId = 0;
            e.Dirty = true;
            return true;
        }

        // the SP page copy (PlayerController.CopyPage): clear + resize + re-seat every jar cell-for-cell
        static void CopyPage(Items from, Items to, byte w, byte h)
        {
            to.clear();
            to.loadSize(w, h);
            for (byte i = 0; i < from.getItemCount(); i++)
            {
                var j = from.getItem(i);
                to.addItem(j.x, j.y, j.rot, j.item);
            }
        }

        // ---- IReplicatedSystem (owner-only, full-on-dirty) ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => WriteOwnerBlock(w, ctx.ClientPlayerId, always: true);

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            bool dirty = _byOwner.TryGetValue(ctx.ClientPlayerId, out var e) && e.LastChangedTick > baselineTick;
            WriteOwnerBlock(w, ctx.ClientPlayerId, always: dirty);
        }

        void WriteOwnerBlock(NetPakWriter w, ushort clientPlayerId, bool always)
        {
            if (!always || !_byOwner.TryGetValue(clientPlayerId, out var e)) { w.WriteUInt8(0); return; }
            var inv = e.Inventory;
            w.WriteUInt8(1);
            w.WriteUInt16(e.OwnerPlayerId);
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                var page = inv.items[p];
                w.WriteUInt8(page.width);
                w.WriteUInt8(page.height);
                w.WriteUInt8(page.getItemCount());
                for (byte i = 0; i < page.getItemCount(); i++) WriteJar(w, page.getItem(i));
            }
            WriteWorn(w, inv.wornHat); WriteWorn(w, inv.wornGlasses); WriteWorn(w, inv.wornMask);
            WriteWorn(w, inv.wornShirt); WriteWorn(w, inv.wornVest); WriteWorn(w, inv.wornBackpack); WriteWorn(w, inv.wornPants);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt8(out byte count) || count == 0) return;
            if (!r.ReadUInt16(out ushort owner)) return;

            // rebuild the whole replica from the wire (full-on-dirty: the block IS the state)
            var inv = new PlayerInventory();
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                if (!r.ReadUInt8(out byte width) || !r.ReadUInt8(out byte height) || !r.ReadUInt8(out byte itemCount)) return;
                var page = inv.items[p];
                page.loadSize(width, height);
                for (byte i = 0; i < itemCount; i++)
                {
                    if (!ReadJar(r, out byte x, out byte y, out byte rot, out Item item)) return;
                    page.addItem(x, y, rot, item);
                }
            }
            if (!ReadWorn(r, out var hat) || !ReadWorn(r, out var glasses) || !ReadWorn(r, out var mask)) return;
            if (!ReadWorn(r, out var shirt) || !ReadWorn(r, out var vest) || !ReadWorn(r, out var backpack) || !ReadWorn(r, out var pants)) return;
            inv.wornHat = hat; inv.wornGlasses = glasses; inv.wornMask = mask;
            inv.wornShirt = shirt; inv.wornVest = vest; inv.wornBackpack = backpack; inv.wornPants = pants;

            if (!_byOwner.TryGetValue(owner, out var e))
            {
                e = new PlayerEntry { OwnerPlayerId = owner };
                _byOwner[owner] = e;
            }
            e.Inventory = inv;
            ReplicaUpdated?.Invoke(owner);
        }

        static void WriteJar(NetPakWriter w, ItemJar j)
        {
            w.WriteUInt8(j.x); w.WriteUInt8(j.y); w.WriteUInt8(j.rot);
            w.WriteUInt16(j.item?.id ?? 0);
            w.WriteUInt8(j.item?.amount ?? 0);
            w.WriteUInt8(j.item?.quality ?? 0);
            // gun state travels so a dropped-in-grid gun keeps its mag/firemode on the replica (Item fields)
            w.WriteInt16((short)(j.item?.gunAmmo ?? -1));
            w.WriteInt8((sbyte)(j.item?.gunFiremode ?? -1));
            w.WriteInt32(j.item?.gunMagId ?? -1);
            w.WriteInt32(j.item?.gunAttach ?? -1);
        }

        static bool ReadJar(NetPakReader r, out byte x, out byte y, out byte rot, out Item item)
        {
            item = null;
            x = y = rot = 0;
            if (!r.ReadUInt8(out x) || !r.ReadUInt8(out y) || !r.ReadUInt8(out rot)) return false;
            if (!r.ReadUInt16(out ushort id)) return false;
            if (!r.ReadUInt8(out byte amount)) return false;
            if (!r.ReadUInt8(out byte quality)) return false;
            if (!r.ReadInt16(out short gunAmmo)) return false;
            if (!r.ReadInt8(out sbyte gunFiremode)) return false;
            if (!r.ReadInt32(out int gunMagId)) return false;
            if (!r.ReadInt32(out int gunAttach)) return false;
            item = new Item(id, amount, quality) { gunAmmo = gunAmmo, gunFiremode = gunFiremode, gunMagId = gunMagId, gunAttach = gunAttach };
            return true;
        }

        static void WriteWorn(NetPakWriter w, Item item)
        {
            w.WriteBit(item != null);
            if (item == null) return;
            w.WriteUInt16(item.id);
            w.WriteUInt8(item.amount);
            w.WriteUInt8(item.quality);
        }

        static bool ReadWorn(NetPakReader r, out Item item)
        {
            item = null;
            if (!r.ReadBit(out bool has)) return false;
            if (!has) return true;
            if (!r.ReadUInt16(out ushort id)) return false;
            if (!r.ReadUInt8(out byte amount)) return false;
            if (!r.ReadUInt8(out byte quality)) return false;
            item = new Item(id, amount, quality);
            return true;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            var owners = new List<ushort>(_byOwner.Keys);
            owners.Sort();
            foreach (ushort id in owners) h = MixEntry(h, _byOwner[id]);
            return h;
        }

        /// <summary>Owner-only parity hash (same contract as SkillsReplication.StateHashFor).</summary>
        public ulong StateHashFor(ushort ownerPlayerId)
        {
            ulong h = NetHash.FnvOffset;
            if (_byOwner.TryGetValue(ownerPlayerId, out var e)) h = MixEntry(h, e);
            return h;
        }

        static ulong MixEntry(ulong h, PlayerEntry e)
        {
            h = NetHash.MixUInt32(h, e.OwnerPlayerId);
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                var page = e.Inventory.items[p];
                h = NetHash.MixByte(h, page.width);
                h = NetHash.MixByte(h, page.height);
                h = NetHash.MixByte(h, page.getItemCount());
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var j = page.getItem(i);
                    h = NetHash.MixByte(h, j.x); h = NetHash.MixByte(h, j.y); h = NetHash.MixByte(h, j.rot);
                    h = NetHash.MixUInt32(h, j.item?.id ?? 0u);
                    h = NetHash.MixByte(h, j.item?.amount ?? (byte)0);
                    h = NetHash.MixByte(h, j.item?.quality ?? (byte)0);
                    h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunAmmo ?? -1));
                    h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunFiremode ?? -1));
                    h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunMagId ?? -1));
                    h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunAttach ?? -1));
                }
            }
            foreach (var worn in new[] { e.Inventory.wornHat, e.Inventory.wornGlasses, e.Inventory.wornMask,
                                         e.Inventory.wornShirt, e.Inventory.wornVest, e.Inventory.wornBackpack, e.Inventory.wornPants })
            {
                h = NetHash.MixByte(h, worn != null ? (byte)1 : (byte)0);
                if (worn != null)
                {
                    h = NetHash.MixUInt32(h, worn.id);
                    h = NetHash.MixByte(h, worn.amount);
                    h = NetHash.MixByte(h, worn.quality);
                }
            }
            return h;
        }
    }
}
