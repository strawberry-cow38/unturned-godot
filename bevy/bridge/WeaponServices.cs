using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Original gun inventory rules hosted beside ServerCombat. Every choice
/// uses the server's owned item, never the client's count or a cloned grid.</summary>
public sealed partial class WeaponServices
{
    readonly NetWorldServer _server;
    readonly Catalog _catalog;
    readonly Action<string, object> _emit;
    readonly Dictionary<ushort, WeaponState> _states = new();
    readonly Dictionary<ushort, Queue<ShotFacts>> _shots = new();
    readonly Dictionary<ushort, Queue<MeleeFacts>> _melees = new();
    public sealed record MeleeFacts(ushort ItemId, bool Repeated, bool Strong, long Tick);
    public Action<ushort,float> Noise;
    public sealed record ShotFlight(ShotFacts Facts,GunDef Gun,int Pellets,int Steps,float Gravity);
    readonly Dictionary<ushort,ShotFlight> _acceptedFlights = new();
    public Action<ushort,FireCommand,ShotFlight> CosmeticLaunch;
    public sealed record ShotFacts(string Gun, ushort ItemId, byte Quality, float HandlingPenalty, float RecoilMultiplier, long SelectionVersion, long Tick,
        int BarrelId,bool Silenced,bool BarrelSilenced,string ShootClip,float ShotVolume,float MuzzleVelocity,bool BarrelBroken);
    public WeaponServices(NetWorldServer server, Catalog catalog, Action<string, object> emit)
    {
        _server = server; _catalog = catalog; _emit = (kind, data) => { emit(kind, data); ForwardEquipmentFact(kind, data); };
        InstallEquipmentAuthority();
        server.Combat.ResolveHeldGunProfile = owner => State(owner).Profile;
        server.Combat.HeldCondition = owner => State(owner).Condition;
        server.Combat.CanFire = owner => State(owner).CanFire();
        server.Combat.ShotAccepted = (owner, tick) => {
            var state=State(owner); var facts=state.CaptureShot(tick);
            _acceptedFlights[owner]=state.CaptureFlight(facts);
            if(!state.Suppressed)Noise?.Invoke(owner,NoiseRules.Gunshot);
            if(facts!=null)
            {
                if(!_shots.TryGetValue(owner,out var queue))_shots[owner]=queue=new();
                queue.Enqueue(facts);
            }
            state.Shot(tick);
        };
        server.Combat.ShotCommandAccepted = (owner,command,tick) => {
            if(_acceptedFlights.Remove(owner,out var flight)&&flight?.Facts!=null)CosmeticLaunch?.Invoke(owner,command,flight);
        };
        server.Combat.TryBeginReload = (owner, tick) => State(owner).BeginReload(tick);
        server.Combat.CompleteReload = (owner, tick) => State(owner).CompleteReload(tick);
        server.Combat.ResolveHeldMeleeProfile = (owner, strong) => State(owner).MeleeProfile(strong);
        server.Combat.CanMelee = (owner, strong) => State(owner).CanMelee(strong);
        server.Combat.MeleeAccepted = (owner, strong, tick) => {
            var state=State(owner);
            if(!_melees.TryGetValue(owner,out var queue))_melees[owner]=queue=new();
            queue.Enqueue(state.CaptureMelee(strong,tick));
            if(state.MeleeNoise>0)Noise?.Invoke(owner,state.MeleeNoise);
            state.MeleeUsed(strong,tick);
        };
    }
    public ShotFacts TakeShot(ushort owner,string gun)
    {
        if(!_shots.TryGetValue(owner,out var queue)||queue.Count==0)return null;
        var shot=queue.Dequeue();
        string wireGun=shot.Gun.Length>PlayerFiredEvent.MaxGunNameLength?shot.Gun[..PlayerFiredEvent.MaxGunNameLength]:shot.Gun;
        return wireGun==gun?shot:null;
    }
    public MeleeFacts TakeMelee(ushort owner,bool strong)
    {
        if(!_melees.TryGetValue(owner,out var queue)||queue.Count==0)return null;
        var facts=queue.Dequeue();return facts.Strong==strong?facts:null;
    }
    public sealed record Saved(string Name, Dictionary<int,ushort> Shells);
    public Saved[] Capture() => _states.Select(p=>new Saved(_server.Transactions.NameOf(p.Key),p.Value.CaptureShells())).ToArray();
    public void Restore(Saved[] saved)
    {
        if(saved==null)return;
        foreach(var player in _server.CombatState.All)
        {
            var data=saved.FirstOrDefault(s=>s.Name==_server.Transactions.NameOf(player.OwnerPlayerId));
            if(data!=null)State(player.OwnerPlayerId).RestoreShells(data.Shells);
        }
    }
    public object AttachmentOptions(ushort owner) => State(owner).AttachmentOptions();
    public object AttachmentAction(ushort owner,string action,System.Text.Json.JsonElement args) => State(owner).QueueAttachment(action,args);
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
    public void Step() { foreach (var player in _server.CombatState.All) { var state = State(player.OwnerPlayerId); state.Step(); _server.Equipment.ServerSet(player.OwnerPlayerId, state.EquipmentReplica(), _server.Session.CurrentTick); } }
    public object Snapshot(ushort owner) => State(owner).Snapshot();
    public WarheadServices.Payload CaptureWarhead(ushort owner) => State(owner).CaptureWarhead();
    public float RepairRate(ushort owner) => State(owner).RepairRate;
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
    Item _selectionObserved;
    long _selectionVersion;
    ushort _selectedId;
    bool _selectionPending;
    bool _emptySelectionPending;
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
    public float RepairRate => Melee?.Repair == true ? Melee.VehicleDamage * 3f : 0;
    public byte? Condition => _ownedHeld!=null&&Durability.HasCondition(Assets.find(_ownedHeld.id))?_ownedHeld.quality:null;

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
        bool explicitEmpty = _emptySelectionPending;
        if (_emptySelectionPending && heldId == 0) _emptySelectionPending = false;
        for (byte page = 0; page < PlayerInventory.OWNPAGES && _ownedHeld == null && !explicitEmpty; page++)
            foreach (var jar in Inventory.items[page].items)
            {
                bool chosen = _selected != null ? ReferenceEquals(jar.item, _selected) : jar.item.id == heldId;
                var asset = Assets.find(jar.item.id);
                if (chosen && asset != null && HandRules.CanEquipItemAsset(asset) && asset.slot.CanEquipInPage(page))
                { _ownedHeld = jar.item; break; }
            }
        if (!ReferenceEquals(_ownedHeld, _selectionObserved)) { _selectionObserved = _ownedHeld; _selectionVersion++; }
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
        ResetAttachmentAnimation();
        _reloading = _hammerPending = _hammerActive = false; _cycleReadyTick = 0; _shotCount = 0; _combat.ReloadDoneTick = -1;
        if (Gun == null) { Ammo = 0; Profile = null; return; }
        AttachmentNoise.SeedDefaults(gun,Assets.find(gun.id)?.itemName);
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
        _pendingEquip = null; _selected = item; _selectedId = item.id; _selectionPending = true; _emptySelectionPending = false;
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
        // Original Fire sends the effective loaded round to the living-target
        // and tree channels. Condition remains the combat launcher's responsibility.
        float damage = ShotDamage();
        Profile.PlayerDamage = Profile.ZombieDamage = damage;
        Profile.AnimalDamage = damage;
        Profile.HumanoidDamage = damage;
        Profile.ResourceDamage = damage;
        Profile.MuzzleVelocity = Gun.MuzzleVelocity*(Suppressed?AttachmentNoise.SilencedVelocityMultiplier:1);
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
        return client.SendWeaponIntent(new WeaponIntentCommand { Action = EquipmentAction.Firemode,
            GunToken = ItemIdentity(_heldItem), SelectionVersion = (ulong)_selectionVersion, Firemode = checked((byte)mode) });
    }
    public bool CanFire()
    {
        if (_server.VehicleHost.IsDriver(_owner)) return false;
        if (_unloading || MagazineBusy || Gun == null || _heldItem?.gunFiremode == 0 || _heldItem?.quality == 0 || _server.Session.CurrentTick < _cycleReadyTick) return false;
        if (_reloading && Gun.ShellReload && Ammo > 0)
        { _reloading = false; _combat.ReloadDoneTick = -1; _emit("reloadCancelled", new { playerId = _owner }); }
        UpdateProfile(); return true;
    }
    public WeaponServices.ShotFacts CaptureShot(long tick)
    {
        if(Gun==null||_heldItem==null)return null;
        byte quality=_heldItem.quality;
        float handling=Durability.HandlingPenalty(quality);
        float skill=_server.Skills.TryGet(_owner,out var skills)?skills.Skills.SharpshooterRecoilMultiplier():1;
        int barrelId=AttachmentNoise.InstalledId(_heldItem,"Barrel");
        var barrel=AttachmentNoise.BarrelFor(barrelId);
        string own=AttachmentNoise.Snd(Profile.AssetName+"_shoot.ogg","eaglefire_shoot.ogg");
        string clip=string.IsNullOrEmpty(barrel.ShootClip)?own:AttachmentNoise.Snd(barrel.ShootClip,own);
        return new(Profile.AssetName,_heldItem.id,quality,handling,handling*skill,_selectionVersion,tick,
            barrelId,Suppressed,barrel.Silenced,clip,barrel.Volume,Profile.MuzzleVelocity,AttachmentNoise.BreaksOnShot(barrelId));
    }
    public WeaponServices.ShotFlight CaptureFlight(WeaponServices.ShotFacts facts) => Gun==null?null:new(facts,Gun,Profile.Pellets,Profile.BallisticSteps,-9.81f*Profile.GravityMultiplier);
    public WarheadServices.Payload CaptureWarhead() => Gun==null||_heldItem==null?null:new(_heldItem.id,Profile.AssetName,
        Gun.BlastRadius,Gun.BlastPlayerDamage,Gun.BlastZombieDamage,Gun.BlastVehicleDamage,WarheadRules.StickAmmoFor(Gun));
    static int BarrelMask=>1<<System.Array.IndexOf(AttachmentNoise.Slots,"Barrel");
    public bool Suppressed => (Gun?.IntegrallySuppressed??false)
        || (_heldItem?.gunBarrelId>0?AttachmentNoise.IsSilencer(_heldItem.gunBarrelId):_heldItem?.gunAttach>=0&&(_heldItem.gunAttach&BarrelMask)!=0);
    public WeaponServices.MeleeFacts CaptureMelee(bool strong,long tick) => new(_ownedHeld?.id??0,Melee?.Repeated??false,strong,tick);
    public float MeleeNoise => Melee?.Alert??0;
    public void Shot(long tick)
    {
        if (Gun == null) return;
        _chambered = HasChamber && Ammo > 0;
        if (Gun.RechamberAfterShotCount > 0 && ++_shotCount >= Gun.RechamberAfterShotCount)
        {
            _shotCount = 0;
            double hammer = _catalog.Clip(Profile.AssetName, "_Hammer"); if (hammer <= 0) hammer = .5;
            _cycleReadyTick = tick + Ticks(Gun.RechamberAfterShotDelay) + Ticks(hammer);
            _emit("rechamber", new { playerId = _owner, delay = Gun.RechamberAfterShotDelay, seconds = hammer });
        }
        int barrel=AttachmentNoise.InstalledId(_heldItem,"Barrel");
        if(AttachmentNoise.BreaksOnShot(barrel))
        {
            AttachmentNoise.SetInstalledId(_heldItem,"Barrel",-1);
            if(_heldItem.gunAttach>=0)_heldItem.gunAttach&=~BarrelMask;
            _emit("barrelBroken",new{playerId=_owner,itemId=_heldItem.id,barrelId=barrel,text="The bottle silencer burst"});
        }
        // ServerCombat spawns this shot immediately after ShotAccepted returns.
        // Keep its already-resolved profile until the next CanFire refresh: the
        // barrel that just broke still silenced THIS projectile's velocity.
        SaveGunState();
        Durability.UseWeapon(_heldItem, Assets.find(_heldItem.id), _random.NextDouble);
    }
    public bool BeginReload(long tick)
    {
        if (Gun == null || _reloading || _unloading || MagazineBusy || tick < _cycleReadyTick || Ammo >= ChamberedCap) return false;
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
        if (_server.VehicleHost.IsSeated(_owner)) return false;
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
        StepAttachmentActions();
        if (_pendingEquip != null)
        {
            var accepted = Inventory.items[_pendingSlot].items.FirstOrDefault(j => ReferenceEquals(j.item, _pendingEquip));
            if (accepted != null) _selectionChange = Select(_pendingSlot, accepted.x, accepted.y);
            else if (_server.Session.CurrentTick >= _pendingUntil || !Inventory.items.Take(PlayerInventory.OWNPAGES).Any(p => p.items.Any(j => ReferenceEquals(j.item, _pendingEquip))))
            { _pendingEquip = null; _emit("equipRefused", new { playerId = _owner }); }
        }
        if (_selected != null && !_selectionPending && _ownedHeld == null) _selectionChange = 0;
        if (!_combat.Alive) { ResetAttachmentAnimation(); _reloading = false; _combat.ReloadDoneTick = -1; }
        if (Gun == null && _heldItem == null) Ammo = 0;
    }
    public object Snapshot() => new { held = HeldAddress(), reloading = _reloading || _unloading || MagazineBusy, unloading = _unloading, rechambering = _server.Session.CurrentTick < _cycleReadyTick,
        selectionVersion = _selectionVersion, broken = _ownedHeld?.quality == 0,
        canFire = Gun != null && _combat?.Alive == true && _heldItem?.quality > 0 && _heldItem.gunFiremode != 0,
        ammo = Gun == null ? 0 : Ammo, capacity = Gun == null ? 0 : MagCapacity, chambered = Gun != null && _chambered,
        firemode = Gun == null ? -1 : _heldItem.gunFiremode, magazineId = _loadedMagId, shellItemId = UsesShells ? ShellAsset?.id ?? 0 : 0,
        attachments=Gun==null?null:new{ sightId=_heldItem.gunSightId,barrelId=_heldItem.gunBarrelId,gripId=_heldItem.gunGripId,
            tacticalId=_heldItem.gunTacticalId,magazineId=_heldItem.gunMagId,mask=_heldItem.gunAttach,seeded=_heldItem.gunAttachSeeded},
        silenced=Gun!=null&&Suppressed };
    static int Ticks(double seconds) => Math.Max(1, (int)Math.Ceiling(seconds / SimClock.FixedDelta));
}
