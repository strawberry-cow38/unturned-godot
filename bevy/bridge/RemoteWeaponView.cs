using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Client-local hand selection over the real owned inventory replica.
/// This class never changes an Item, inventory, combat state or weapon timer.</summary>
sealed class RemoteWeaponView
{
    readonly NetWorldClient _client;
    readonly Catalog _catalog;
    readonly Action<string, object> _emit;
    record Address(byte Page, byte X, byte Y, ushort Id);
    Address _selected, _pending;
    byte _pendingSlot;
    string _pendingSignature;
    PlayerInventory _pendingInventory;
    long _pendingUntil, _selectionVersion;
    ushort? _selectionChange;
    PlayerInventory Inventory => _client.Inventories.TryGet(_client.PlayerId, out var bag) ? bag.Inventory : null;
    bool Alive => _client.CombatState.TryGet(_client.PlayerId, out var player) && player.Alive;
    Item Selected => _selected == null ? null : At(_selected.Page, _selected.X, _selected.Y) is Item i && i.id == _selected.Id ? i : null;
    GunDef Gun => Selected is Item i ? _catalog.GunDefs.GetValueOrDefault(i.id) : null;
    public int Ammo => Gun == null ? 0 : Selected.gunAmmo < 0 ? Gun.AmmoMax : Selected.gunAmmo;

    public RemoteWeaponView(NetWorldClient client, Catalog catalog, Action<string, object> emit)
    { _client = client; _catalog = catalog; _emit = emit; }

    Item At(int page, int x, int y)
    {
        if (Inventory == null || page < 0 || page >= PlayerInventory.OWNPAGES || x < 0 || x > 255 || y < 0 || y > 255) return null;
        var p = Inventory.items[page]; byte index = p.getIndex((byte)x, (byte)y);
        return index == byte.MaxValue ? null : p.getItem(index)?.item;
    }

    public ushort Select(int page, int x, int y)
    {
        var item = At(page, x, y); var asset = item == null ? null : Assets.find(item.id);
        if (!Alive || !HandRules.CanEquipItemAsset(asset) || !asset.slot.CanEquipInPage((byte)page)) return 0;
        var next = new Address((byte)page, (byte)x, (byte)y, item.id);
        if (next != _selected) _selectionVersion++;
        _selected = next; _pending = null; return item.id;
    }

    static string Signature(Item i) => i == null ? null : string.Join(':', i.id, i.amount, i.quality,
        i.gunAmmo, i.gunFiremode, i.gunMagId, i.gunChambered, i.gunAttach,
        i.gunSightId, i.gunBarrelId, i.gunGripId, i.gunTacticalId, i.gunAttachSeeded);

    public (bool queued, ushort heldItemId, bool pending) EquipAndHold(int page, int x, int y)
    {
        var item = At(page, x, y); var asset = item == null ? null : Assets.find(item.id);
        if (!Alive || !HandRules.CanEquipItemAsset(asset)) return (false, 0, false);
        if (asset.slot.CanEquipInPage((byte)page)) return (true, Select(page, x, y), false);
        int slot = asset.slot.PreferredSlot();
        if (slot < 0 || !_client.SendEquipItem((byte)page, (byte)x, (byte)y, (byte)slot)) return (false, 0, false);
        _pending = new((byte)page, (byte)x, (byte)y, item.id); _pendingSlot = (byte)slot;
        _pendingSignature = Signature(item); _pendingInventory = Inventory;
        _pendingUntil = _client.Session.CurrentTick + NetProtocol.TicksPerSecond * 3;
        return (true, 0, true);
    }

    public void Step(ushort held)
    {
        if (_pending != null)
        {
            // InventoryReplication rebuilds Item objects, so reference identity
            // cannot cross this wire. Require a new replica and both sides of
            // the requested move. Indistinguishable twins cannot be proven and
            // deliberately time out instead of selecting an unrelated weapon.
            var slot = Inventory?.items[_pendingSlot].items.FirstOrDefault();
            if (!ReferenceEquals(_pendingInventory, Inventory) && slot != null
                && Signature(slot.item) == _pendingSignature
                && Signature(At(_pending.Page, _pending.X, _pending.Y)) != _pendingSignature)
                _selectionChange = Select(_pendingSlot, slot.x, slot.y);
            else if (_client.Session.CurrentTick >= _pendingUntil)
            { _pending = null; _emit("equipRefused", new { text = "Equipment move was not confirmed by the server" }); }
        }
        if (_selected != null && (_selected.Id != held && _selectionChange == null || Selected == null || !Alive))
        { _selected = null; _selectionVersion++; _selectionChange = 0; }
    }

    public ushort? TakeSelectionChange() { var next = _selectionChange; _selectionChange = null; return next; }

    public bool SetFiremode(int mode)
    {
        var gun = Gun; var item = Selected;
        bool valid = gun != null && (mode == 0 && gun.HasSafety || mode == 1 && gun.HasSemi
            || mode == 2 && gun.HasAuto || mode == 3 && gun.BurstCount > 0);
        if (!Alive || !valid) return false;
        return _client.SendGunState(_selected.Page, _selected.X, _selected.Y, item.id,
            checked((short)item.gunAmmo), item.gunChambered, checked((sbyte)mode), item.gunMagId,
            item.gunAttach, item.gunSightId, item.gunBarrelId, item.gunGripId, item.gunTacticalId, item.gunAttachSeeded);
    }

    public object Snapshot()
    {
        var item = Selected; var gun = Gun;
        int magazine = item == null ? 0 : item.gunMagId < 0 ? gun?.MagazineId ?? 0 : item.gunMagId;
        var mag = magazine is > 0 and <= ushort.MaxValue ? Assets.find((ushort)magazine) : null;
        return new {
            held = item == null ? null : new { id = item.id, page = _selected.Page, x = _selected.X, y = _selected.Y,
                kind = HandRules.KindOf(Assets.find(item.id)).ToString().ToLowerInvariant() },
            selectionVersion = _selectionVersion, broken = item?.quality == 0,
            canFire = Alive && gun != null && item.quality > 0 && item.gunFiremode != 0,
            ammo = Ammo, capacity = mag?.magCapacity ?? gun?.AmmoMax ?? 0,
            chambered = item?.gunChambered ?? false, firemode = item?.gunFiremode ?? -1, magazineId = magazine,
            attachments = gun == null ? null : new { sightId = item.gunSightId, barrelId = item.gunBarrelId,
                gripId = item.gunGripId, tacticalId = item.gunTacticalId, magazineId = item.gunMagId,
                mask = item.gunAttach, seeded = item.gunAttachSeeded },
            // Reload/rechamber clocks are server-only in the original wire.
            // Omit them rather than presenting a fabricated accepted timer.
        };
    }
}
