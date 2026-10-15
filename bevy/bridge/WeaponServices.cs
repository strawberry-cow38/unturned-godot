using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Original gun inventory rules hosted beside ServerCombat. Every choice
/// uses the server's owned item, never the client's count or a cloned grid.</summary>
public sealed class WeaponServices
{
    readonly NetWorldServer _server;
    readonly Catalog _catalog;
    readonly Action<string, object> _emit;
    readonly Dictionary<ushort, WeaponState> _states = new();
    public WeaponServices(NetWorldServer server, Catalog catalog, Action<string, object> emit)
    {
        _server = server; _catalog = catalog; _emit = emit;
        server.Combat.ResolveHeldGunProfile = owner => State(owner).Profile;
        server.Combat.CanFire = owner => State(owner).CanFire();
        server.Combat.ShotAccepted = (owner, tick) => State(owner).Shot(tick);
        server.Combat.TryBeginReload = (owner, tick) => State(owner).BeginReload(tick);
        server.Combat.CompleteReload = (owner, tick) => State(owner).CompleteReload(tick);
        server.Combat.ResolveHeldMeleeProfile = (owner, strong) => State(owner).MeleeProfile(strong);
        server.Combat.CanMelee = (owner, strong) => State(owner).CanMelee(strong);
        server.Combat.MeleeAccepted = (owner, strong, tick) => State(owner).MeleeUsed(strong, tick);
    }
    WeaponState State(ushort owner)
    {
        if (!_states.TryGetValue(owner, out var s)) _states[owner] = s = new(_server, _catalog, owner, _emit);
        s.Bind(); return s;
    }
    public (bool queued, ushort heldItemId, bool pending) EquipAndHold(ushort owner, NetWorldClient client, int page, int x, int y)
        => State(owner).EquipAndHold(client, page, x, y);
    public ushort? TakeSelectionChange(ushort owner) => State(owner).TakeSelectionChange();
    public ushort Select(ushort owner, int page, int x, int y) => State(owner).Select(page, x, y);
    public bool SetFiremode(ushort owner, NetWorldClient client, int mode) => State(owner).SetFiremode(client, mode);
    public void Step() { foreach (var player in _server.CombatState.All) State(player.OwnerPlayerId).Step(); }
    public object Snapshot(ushort owner) => State(owner).Snapshot();
}

public sealed partial class WeaponState
{
    readonly NetWorldServer _server;
    readonly Catalog _catalog;
    readonly ushort _owner;
    readonly Action<string, object> _emit;
    readonly Random _random = new();
    PlayerCombatReplication.CombatEntity _combat;
    Item _heldItem;
    Item _ownedHeld, _selected;
    ushort _selectedId;
    bool _selectionPending;
    Item _pendingEquip;
    byte _pendingSlot;
    long _pendingUntil;
    ushort? _selectionChange;
    int _savedAmmo;
    GunDef Gun;
    PlayerInventory Inventory;
    int _loadedMagId;
    bool _chambered;
    string _chamberedAmmoType;
    bool _reloading, _hammerPending, _hammerActive;
    long _cycleReadyTick;
    int _shotCount;
    float _reloadSpeed = 1;
    // The linked source's direct branch runs here because this IS the server.
    bool InventoryIsServerOwned => false;
    readonly Action<byte, byte, byte, ushort, ushort> NetReloadSwap = null;
    int Ammo { get => _combat?.Ammo ?? 0; set { if (_combat != null) _combat.Ammo = value; } }
    public ServerGunProfile Profile { get; private set; }

    public WeaponState(NetWorldServer server, Catalog catalog, ushort owner, Action<string, object> emit)
    { _server = server; _catalog = catalog; _owner = owner; _emit = emit; }
    public void Bind()
    {
        if (!_server.CombatState.TryGet(_owner, out _combat) || !_server.Inventories.TryGet(_owner, out var inv)) return;
        Inventory = inv.Inventory;
        ushort heldId = _server.Players.TryGetHeldInput(_owner, out var input) ? input.HeldItemId : (ushort)0;
        if (_selected != null && heldId == _selectedId) _selectionPending = false;
        if (_selected != null && heldId != _selectedId && !_selectionPending) { _selected = null; _selectedId = 0; }
        _ownedHeld = null;
        for (byte page = 0; page < PlayerInventory.OWNPAGES && _ownedHeld == null; page++)
            foreach (var jar in Inventory.items[page].items)
            {
                bool chosen = _selected != null ? ReferenceEquals(jar.item, _selected) : jar.item.id == heldId;
                var asset = Assets.find(jar.item.id);
                if (chosen && asset != null && HandRules.CanEquipItemAsset(asset) && asset.slot.CanEquipInPage(page))
                { _ownedHeld = jar.item; break; }
            }
        Item gun = _ownedHeld != null && _catalog.GunDefs.ContainsKey(_ownedHeld.id) ? _ownedHeld : null;
        if (ReferenceEquals(gun, _heldItem))
        {
            // Existing unload/reload/attachment commands can edit this very item
            // between combat ticks. Distinguish that write from ServerCombat's
            // in-progress shot decrement, which has not called ShotAccepted yet.
            if (gun != null && gun.gunAmmo != _savedAmmo)
            {
                Ammo = Math.Max(0, gun.gunAmmo); _savedAmmo = gun.gunAmmo;
                _loadedMagId = gun.gunMagId; _chambered = gun.gunChambered; _chamberedAmmoType = gun.gunChamberedType;
                UpdateProfile();
            }
            return;
        }
        SaveGunState();
        _heldItem = gun; Gun = gun == null ? null : _catalog.GunDefs[gun.id];
        _reloading = _hammerPending = _hammerActive = false; _cycleReadyTick = 0; _shotCount = 0; _combat.ReloadDoneTick = -1;
        if (Gun == null) { Ammo = 0; Profile = null; return; }
        var modes = AvailableModes();
        if (!modes.Contains(gun.gunFiremode)) gun.gunFiremode = modes.Contains(1) ? 1 : modes.Contains(2) ? 2 : modes[0];
        Ammo = gun.gunAmmo < 0 ? Gun.AmmoMax : gun.gunAmmo;
        _loadedMagId = gun.gunMagId < 0 ? Gun.MagazineId : gun.gunMagId;
        _chambered = gun.gunAmmo < 0 ? HasChamber : gun.gunChambered;
        _chamberedAmmoType = gun.gunAmmo < 0 ? Assets.find((ushort)Gun.MagazineId)?.ammoType : gun.gunChamberedType;
        // Each owner gets a profile; loaded drums/pellets must not mutate another
        // player's profile merely because they happen to hold the same asset.
        var source = _catalog.Guns[gun.id];
        Profile = new ServerGunProfile();
        foreach (var field in typeof(ServerGunProfile).GetFields()) field.SetValue(Profile, field.GetValue(source));
        SaveGunState(); UpdateProfile();
    }
    Item At(int page, int x, int y)
    {
        if (Inventory == null || page < 0 || page >= PlayerInventory.OWNPAGES || x < 0 || x > 255 || y < 0 || y > 255) return null;
        var pg = Inventory.items[page]; byte at = pg.getIndex((byte)x, (byte)y);
        return at == byte.MaxValue ? null : pg.getItem(at)?.item;
    }
    public (bool queued, ushort heldItemId, bool pending) EquipAndHold(NetWorldClient client, int page, int x, int y)
    {
        var item = At(page, x, y); var asset = item == null ? null : Assets.find(item.id);
        if (_combat?.Alive != true || !HandRules.CanEquipItemAsset(asset)) return (false, 0, false);
        if (asset.slot.CanEquipInPage((byte)page))
        {
            ushort selected = Select(page, x, y); return (selected != 0, selected, false);
        }
        int slot = asset.slot.PreferredSlot();
        if (slot < 0 || !client.SendEquipItem((byte)page, (byte)x, (byte)y, (byte)slot)) return (false, 0, false);
        _pendingEquip = item; _pendingSlot = (byte)slot;
        _pendingUntil = _server.Session.CurrentTick + NetProtocol.TicksPerSecond;
        return (true, 0, true);
    }
    public ushort? TakeSelectionChange() { var id = _selectionChange; _selectionChange = null; return id; }
    public ushort Select(int page, int x, int y)
    {
        if (_combat?.Alive != true || Inventory == null || page < 0 || page >= PlayerInventory.OWNPAGES || x < 0 || x > 255 || y < 0 || y > 255) return 0;
        var item = At(page, x, y);
        var asset = item == null ? null : Assets.find(item.id);
        if (!HandRules.CanEquipItemAsset(asset) || !asset.slot.CanEquipInPage((byte)page)) return 0;
        _pendingEquip = null; _selected = item; _selectedId = item.id; _selectionPending = true;
        Bind(); return item.id;
    }
    object HeldAddress()
    {
        if (_ownedHeld == null || Inventory == null) return null;
        for (int page = 0; page < PlayerInventory.OWNPAGES; page++)
            foreach (var jar in Inventory.items[page].items) if (ReferenceEquals(jar.item, _ownedHeld))
                return new { id = jar.item.id, page, x = jar.x, y = jar.y, kind = HandRules.KindOf(Assets.find(jar.item.id)).ToString().ToLowerInvariant() };
        return null;
    }
    void UpdateProfile()
    {
        if (Profile == null) return;
        Profile.MagCapacity = ChamberedCap; Profile.Pellets = PelletsPerShot;
    }
    void SaveGunState()
    {
        if (_heldItem == null || Gun == null || _combat == null) return;
        _savedAmmo = Ammo; _heldItem.gunAmmo = Ammo; _heldItem.gunMagId = _loadedMagId;
        _heldItem.gunChambered = _chambered; _heldItem.gunChamberedType = _chamberedAmmoType;
        _server.Inventories.ServerMarkDirty(_owner);
    }
    int[] AvailableModes()
    {
        var modes = new List<int>();
        if (Gun?.HasSafety == true) modes.Add(0);
        if (Gun?.HasSemi == true) modes.Add(1);
        if (Gun?.HasAuto == true) modes.Add(2);
        if (Gun?.BurstCount > 0) modes.Add(3);
        if (modes.Count == 0) modes.Add(1);
        return modes.ToArray();
    }
    public bool SetFiremode(NetWorldClient client, int mode)
    {
        if (_heldItem == null || Gun == null || !AvailableModes().Contains(mode) || _combat?.Alive != true) return false;
        for (byte page = 0; page < PlayerInventory.OWNPAGES; page++)
            foreach (var jar in Inventory.items[page].items) if (ReferenceEquals(jar.item, _heldItem))
            {
                var i = _heldItem;
                // Existing request/validation/owner-echo path. Only the selector is
                // changed; every other field comes from current authoritative state.
                return client.SendGunState(page, jar.x, jar.y, i.id, checked((short)Ammo), _chambered,
                    checked((sbyte)mode), _loadedMagId, i.gunAttach, i.gunSightId, i.gunBarrelId, i.gunGripId, i.gunTacticalId, i.gunAttachSeeded);
            }
        return false;
    }
    public bool CanFire()
    {
        if (Gun == null || _heldItem?.gunFiremode == 0 || _heldItem?.quality == 0 || _server.Session.CurrentTick < _cycleReadyTick) return false;
        if (_reloading && Gun.ShellReload && Ammo > 0)
        { _reloading = false; _combat.ReloadDoneTick = -1; _emit("reloadCancelled", new { playerId = _owner }); }
        UpdateProfile(); return true;
    }
    public void Shot(long tick)
    {
        if (Gun == null) return;
        _chambered = HasChamber && Ammo > 0;
        Durability.UseWeapon(_heldItem, Assets.find(_heldItem.id), _random.NextDouble);
        if (Gun.RechamberAfterShotCount > 0 && ++_shotCount >= Gun.RechamberAfterShotCount)
        {
            _shotCount = 0;
            double hammer = _catalog.Clip(Profile.AssetName, "_Hammer"); if (hammer <= 0) hammer = .5;
            _cycleReadyTick = tick + Ticks(Gun.RechamberAfterShotDelay) + Ticks(hammer);
            _emit("rechamber", new { playerId = _owner, delay = Gun.RechamberAfterShotDelay, seconds = hammer });
        }
        SaveGunState();
    }
    public bool BeginReload(long tick)
    {
        if (Gun == null || _reloading || tick < _cycleReadyTick || Ammo >= ChamberedCap) return false;
        if (UsesMagItem && (FindBestMag()?.item.amount ?? 0) <= 0) return Dry();
        if (UsesShells && CountShells() <= 0) return Dry();
        if (!UsesMagItem && !UsesShells && Gun.Caliber > 0) return Dry();
        _reloadSpeed = _server.Skills.TryGet(_owner, out var skills) ? skills.Skills.DexterityReloadSpeed() : 1;
        _reloading = true; _hammerActive = false;
        _hammerPending = Ammo <= 0 && HasChamber && _catalog.Clip(Profile.AssetName, "_Hammer") > 0 && Profile.AssetName != "sks";
        double full = _catalog.ReloadSeconds(_heldItem.id) / _reloadSpeed;
        _combat.ReloadDoneTick = tick + Ticks(Gun.ShellReload ? full / Math.Max(1, Gun.AmmoMax) : full);
        _emit("reload", new { playerId = _owner, itemId = _heldItem.id, seconds = full, speed = _reloadSpeed, shellByShell = Gun.ShellReload });
        return true;
    }
    bool Dry() { _emit("dryFire", new { playerId = _owner }); return false; }
    public int? CompleteReload(long tick)
    {
        if (!_reloading || Gun == null) { _combat.ReloadDoneTick = -1; return null; }
        if (_hammerActive) { _hammerActive = false; _reloading = false; }
        else if (Gun.ShellReload)
        {
            if (!UsesShells || ConsumeShells(1) > 0) Ammo = Math.Min(Ammo + 1, Gun.AmmoMax);
            if (Ammo >= Gun.AmmoMax || (UsesShells && CountShells() <= 0)) _reloading = false;
            // Original subsequent shell interval deliberately does not repeat the
            // Dexterity division; retain the existing PlayerController rule.
            else _combat.ReloadDoneTick = tick + Ticks(_catalog.ReloadSeconds(_heldItem.id) / Math.Max(1, Gun.AmmoMax));
        }
        else
        {
            if (UsesShells) Ammo += ConsumeShells(Gun.AmmoMax - Ammo);
            else if (UsesMagItem)
            {
                // A bag can change during the animation. Revalidate before the
                // original swap helper's direct path to avoid its demo fallback.
                if ((FindBestMag()?.item.amount ?? 0) > 0) DoMagSwap();
            }
            else Ammo = HasChamber && Ammo > 0 ? Gun.AmmoMax + 1 : Gun.AmmoMax;
            if (_hammerPending && Ammo > 0)
            {
                _hammerPending = false; _hammerActive = true;
                double seconds = _catalog.Clip(Profile.AssetName, "_Hammer") / _reloadSpeed;
                _combat.ReloadDoneTick = tick + Ticks(seconds);
                _emit("rechamber", new { playerId = _owner, delay = 0, seconds });
            }
            else _reloading = false;
        }
        _chambered = HasChamber && Ammo > 0;
        if (!_reloading) { _combat.ReloadDoneTick = -1; _emit("reloadComplete", new { playerId = _owner, ammo = Ammo }); }
        SaveGunState(); UpdateProfile(); return Ammo;
    }
    MeleeDef Melee => _ownedHeld == null ? MeleeDef.Fists : _catalog.MeleeDefs.GetValueOrDefault(_ownedHeld.id);
    public bool CanMelee(bool strong)
    {
        var melee = Melee;
        if (melee == null || _ownedHeld?.quality == 0 || (melee.Repeated && (strong || melee.Repair))) return false;
        if (strong && melee.Stamina > 0 && _server.Vitals.TryGet(_owner, out var vitals) && vitals.Sim.Stamina < melee.Stamina / 100) return false;
        return true;
    }
    public ServerMeleeProfile MeleeProfile(bool strong)
    {
        var melee = Melee ?? MeleeDef.Fists;
        double seconds = _catalog.Clip(melee.Name, strong ? "_Strong" : "_Weak", melee.Name == "fists" ? (strong ? "Punch_Right" : "Punch_Left") : (strong ? "Melee_Strong" : "Melee_Weak"));
        if (seconds <= .05) seconds = strong ? .75 : .45;
        float skill = _server.Skills.TryGet(_owner, out var skills) ? skills.Skills.OverkillMeleeMultiplier() : 1;
        float condition = _ownedHeld == null ? 1 : Durability.DamageMultiplier(_ownedHeld.quality);
        return new ServerMeleeProfile { AssetName = melee.Name, Range = melee.Range,
            ZombieDamage = melee.ZombieDamage * skill * condition, PlayerDamage = melee.PlayerDamage * skill * condition,
            ObjectDamage = melee.ResourceDamage * 4 * condition, VehicleDamage = melee.VehicleDamage * condition,
            ResourceDamage = melee.ResourceDamage, StrongMult = melee.Strength,
            CooldownTicks = Ticks(melee.Repeated ? HandRules.RepeatedHitInterval : seconds),
            HitDelayTicks = melee.Repeated ? 0 : Ticks(seconds * Math.Clamp(strong ? melee.Strong : melee.Weak, .05f, .95f)) };
    }
    public void MeleeUsed(bool strong, long tick)
    {
        var melee = Melee;
        if (strong && melee != null && melee.Stamina > 0 && _server.Vitals.TryGet(_owner, out var vitals))
        { vitals.Sim.Stamina = Math.Max(0, vitals.Sim.Stamina - melee.Stamina / 100); vitals.Sim.StaminaRegenDelay = 1; }
        if (_ownedHeld != null) { Durability.UseWeapon(_ownedHeld, Assets.find(_ownedHeld.id), _random.NextDouble); _server.Inventories.ServerMarkDirty(_owner); }
    }
    public void Step()
    {
        if (_combat == null) return;
        if (_pendingEquip != null)
        {
            var accepted = Inventory.items[_pendingSlot].items.FirstOrDefault(j => ReferenceEquals(j.item, _pendingEquip));
            if (accepted != null) _selectionChange = Select(_pendingSlot, accepted.x, accepted.y);
            else if (_server.Session.CurrentTick >= _pendingUntil || !Inventory.items.Take(PlayerInventory.OWNPAGES).Any(p => p.items.Any(j => ReferenceEquals(j.item, _pendingEquip))))
            { _pendingEquip = null; _emit("equipRefused", new { playerId = _owner }); }
        }
        if (_selected != null && !_selectionPending && _ownedHeld == null) _selectionChange = 0;
        if (!_combat.Alive) { _reloading = false; _combat.ReloadDoneTick = -1; }
        if (Gun == null && _heldItem == null) Ammo = 0;
    }
    public object Snapshot() => new { held = HeldAddress(), reloading = _reloading, rechambering = _server.Session.CurrentTick < _cycleReadyTick,
        ammo = Gun == null ? 0 : Ammo, capacity = Gun == null ? 0 : MagCapacity, chambered = Gun != null && _chambered,
        firemode = Gun == null ? -1 : _heldItem.gunFiremode, magazineId = _loadedMagId, shellItemId = UsesShells ? ShellAsset?.id ?? 0 : 0 };
    static int Ticks(double seconds) => Math.Max(1, (int)Math.Ceiling(seconds / SimClock.FixedDelta));
}
