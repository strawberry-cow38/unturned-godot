using SDG.Unturned;
using System.Runtime.CompilerServices;
using System.Text.Json;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Listen-host intent adapter for the original attachment menu and
/// specific-magazine actions. No client grid or client ammo count is accepted.</summary>
public sealed partial class WeaponState
{
    sealed record ItemToken(long Value);
    static long _nextToken;
    static readonly ConditionalWeakTable<Item, ItemToken> Tokens = new();
    static long Token(Item item) => item == null ? 0 : Tokens.GetValue(item, _ => new(Interlocked.Increment(ref _nextToken))).Value;
    public static ulong ItemIdentity(Item item) => (ulong)Token(item);
    sealed record AttachmentIntent(string Action, string Slot, Item Gun, long Selection, Item Incoming, ushort ShellId, int Firemode);
    readonly Queue<AttachmentIntent> _attachmentIntents = new();
    long Tick => _server.Session.CurrentTick;
    bool _unloading, _magSwapAutoRack;
    long _magSwapUntil, _unloadAt;
    bool MagazineBusy => Tick < _magSwapUntil;
    float ReloadSpeed => _server.Skills.TryGet(_owner,out var skills) ? skills.Skills.DexterityReloadSpeed() : 1;
    double HammerSeconds => _catalog.Clip(Profile.AssetName,"_Hammer") is > 0 and var seconds ? seconds : .4;
    bool AttachmentBusy => _reloading || _unloading || MagazineBusy || Tick < _cycleReadyTick;

    bool SlotHasModel(string slot) => Gun != null && slot switch {
        "Sight" => GunVisualData.HasSight(Profile.AssetName),
        "Magazine" => GunVisualData.HasMagazine(Profile.AssetName),
        // Viewmodel always builds these two nodes, even when their mesh is empty.
        "Barrel" or "Tactical" => true,
        // Source _attachMesh has no Grip node. Do not invent a usable slot.
        _ => false
    };
    bool SlotAvailable(string slot) => slot=="Sight" && GunVisualData.IntegralSight(Profile?.AssetName+"_gun.txt")
        ?false : slot=="Magazine"&&UsesShells || SlotHasModel(slot);
    float[] SlotColor(string slot)
    {
        int id=AttachmentNoise.InstalledId(_heldItem,slot);
        var color=AttachmentTransaction.SlotColor(SlotHasModel(slot),id>0?Assets.find((ushort)id):null);
        return [color.R,color.G,color.B,color.A];
    }
    bool CanDetach(string slot)
    {
        if (!SlotAvailable(slot)) return false;
        if (slot == "Magazine") return UsesShells ? Ammo > 0 : UsesMagItem && _loadedMagId > 0;
        return AttachmentNoise.InstalledId(_heldItem,slot)>0 && (slot!="Sight" || !AttachmentNoise.IsDefaultIrons(_heldItem));
    }
    object Choice(Item item, int page, int index)
    {
        var jar=Inventory.items[page].getItem((byte)index);
        return new {page,x=jar.x,y=jar.y,id=item.id,amount=item.amount,quality=item.quality,instanceToken=Token(item)};
    }
    public object AttachmentOptions()
    {
        bool ready=Gun!=null && _combat?.Alive==true;
        return new { selectionVersion=_selectionVersion,gun=HeldAddress(),gunName=_heldItem==null?null:Assets.find(_heldItem.id)?.itemName,gunToken=Token(_heldItem),busy=AttachmentBusy,
            canRack=ready && UsesMagItem && HasChamber && Ammo>0,
            slots=AttachmentNoise.Slots.Select(slot=>new {
                name=slot,color=SlotColor(slot),visible=!(slot=="Sight"&&GunVisualData.IntegralSight(Profile?.AssetName+"_gun.txt")),available=ready&&SlotAvailable(slot),installedId=ready?AttachmentNoise.InstalledId(_heldItem,slot):-1,
                detachable=ready&&CanDetach(slot),kind=slot=="Magazine"?(UsesShells?"shell":"magazine"):"attachment",
                choices=ready&&SlotAvailable(slot)&&!(slot=="Magazine"&&UsesShells)
                    ? AttachmentNoise.InBagInstances(Inventory,slot,Gun.Caliber,Gun.CaliberName,HeldGunAsset)
                        .Select(c=>Choice(c.Item,c.Page,c.Index)).ToArray() : Array.Empty<object>(),
                shells=ready&&slot=="Magazine"&&UsesShells ? ShellTypeChoices().Select(c=>new{id=c.asset.id,count=c.count,selected=c.selected}).ToArray():null
            }).ToArray() };
    }
    static long Long(JsonElement args,string key) => args.TryGetProperty(key,out var value)&&value.TryGetInt64(out long n)?n:-1;
    public object QueueAttachment(string action,JsonElement args)
    {
        string slot=BridgeHost.Text(args,"slot");
        long version=Long(args,"selectionVersion");
        // gunToken closes the save/load epoch hole as well as same-ID replacement.
        if(_heldItem==null||Gun==null||_combat?.Alive!=true||version!=_selectionVersion||Long(args,"gunToken")!=Token(_heldItem))
            return RefuseAttachment(action,slot,"The selected weapon changed");
        if(_attachmentIntents.Count>=16)return RefuseAttachment(action,slot,"Too many pending attachment requests");
        Item incoming=null;
        if(action=="installAttachment")
        {
            incoming=At(BridgeHost.Int(args,"page",-1),BridgeHost.Int(args,"x",-1),BridgeHost.Int(args,"y",-1));
            if(incoming==null||Long(args,"instanceToken")!=Token(incoming))return RefuseAttachment(action,slot,"That inventory item changed");
        }
        ushort shell=(ushort)Math.Clamp(BridgeHost.Int(args,"itemId"),0,ushort.MaxValue);
        var intent=new AttachmentIntent(action,slot,_heldItem,version,incoming,shell,BridgeHost.Int(args,"mode",-1));
        string refusal=ValidateAttachment(intent);
        if(refusal!=null)return RefuseAttachment(action,slot,refusal);
        // The original selector command mutates during command dispatch, before
        // combat. Deferring it to the post-combat attachment step would allow a
        // later shot in the same tick to spend ammo after safety was accepted.
        if(action=="setFiremode")ApplyAttachmentAndPublish(intent);
        else _attachmentIntents.Enqueue(intent);
        return new{queued=true,reason=(string)null};
    }
    object RefuseAttachment(string action,string slot,string reason)
    {
        _emit("attachmentRejected",new{playerId=_owner,action,slot,reason,text=reason});
        return new{queued=false,reason,text=reason};
    }
    string ValidateAttachment(AttachmentIntent intent)
    {
        if(_combat?.Alive!=true||!ReferenceEquals(intent.Gun,_heldItem)||intent.Selection!=_selectionVersion)
            return "The selected weapon changed";
        if(intent.Action=="setFiremode")return AvailableModes().Contains(intent.Firemode)?null:"This weapon does not support that fire mode";
        if(AttachmentBusy)return "The weapon is busy";
        if(intent.Action=="rackGun")return UsesMagItem&&HasChamber&&Ammo>0?null:"No chambered round";
        if(intent.Action is "chooseShellType" or "unloadShells")
        {
            if(!UsesShells)return "This weapon does not feed loose rounds";
            if(intent.Action=="unloadShells")return Ammo>0?null:"No rounds loaded";
            var shell=Assets.find(intent.ShellId);
            return shell?.isAmmo==true&&shell.magCaliber==Gun.Caliber&&CountOfShell(intent.ShellId)>0?null:"No compatible rounds carried";
        }
        if(!SlotAvailable(intent.Slot))return "This weapon has no detachable slot there";
        if(intent.Action=="removeAttachment")return CanDetach(intent.Slot)?null:"No removable attachment";
        if(intent.Slot=="Magazine"&&UsesShells)return "Choose a loose round type instead";
        if(intent.Incoming==null||!Inventory.items.Take(PlayerInventory.OWNPAGES).Any(p=>p.items.Any(j=>ReferenceEquals(j.item,intent.Incoming))))
            return "That inventory item changed";
        return AttachmentNoise.Fits(Assets.find(intent.Incoming.id),intent.Slot,Gun.Caliber,Gun.CaliberName,HeldGunAsset)
            ?null:"That attachment does not fit this weapon";
    }
    // Preflight uses the very same Items placement algorithm with cloned items.
    // The real mutation runs synchronously afterward, preserving every real Item
    // reference and avoiding remove/undo callbacks on a refused transaction.
    PlayerInventory CloneOwned(out Dictionary<Item,Item> clones)
    {
        var copy=new PlayerInventory();clones=new();
        for(byte page=0;page<PlayerInventory.OWNPAGES;page++)
        {
            var src=Inventory.items[page];var dst=copy.items[page];dst.loadSize(src.width,src.height);
            foreach(var jar in src.items){var item=jar.item.Clone();clones[jar.item]=item;dst.addItem(jar.x,jar.y,jar.rot,item);}
        }
        return copy;
    }
    string ApplyAttachment(AttachmentIntent intent)
    {
        string refusal=ValidateAttachment(intent);if(refusal!=null)return refusal;
        if(intent.Action=="setFiremode"){_heldItem.gunFiremode=intent.Firemode;return null;}
        if(intent.Action=="rackGun"){RackGun();return null;}
        if(intent.Action=="chooseShellType"){ChooseShellType(intent.ShellId);return null;}
        if(intent.Action=="unloadShells"||(intent.Action=="removeAttachment"&&intent.Slot=="Magazine"&&UsesShells))
        {BeginUnload();return null;}
        if(intent.Slot=="Magazine")
        {
            if(intent.Action=="removeAttachment"){RemoveMagazine();return null;}
            var probe=CloneOwned(out var cloned);
            AttachmentTransaction.TakeFromBagInstanceIn(probe,cloned[intent.Incoming]);
            bool chambered=HasChamber&&Ammo>0;
            if(_loadedMagId>0&&!probe.tryAddItem(new Item((ushort)_loadedMagId,(byte)Math.Max(0,Ammo-(chambered?1:0)))))
                return "No room to swap that — free a slot first";
            LoadMagInstance(intent.Incoming);return null;
        }
        if(intent.Action=="removeAttachment")
        {
            int previous=AttachmentNoise.InstalledId(_heldItem,intent.Slot);
            var probe=CloneOwned(out _);
            if(!probe.tryAddItem(new Item((ushort)previous)))return "No room to remove that — free a slot first";
            if(!Inventory.tryAddItem(new Item((ushort)previous)))throw new InvalidOperationException("Attachment preflight disagreed with inventory");
            AttachmentNoise.SetInstalledId(_heldItem,intent.Slot,intent.Slot=="Sight"?AttachmentNoise.DefaultIronsIdOf(_heldItem):-1);
        }
        else
        {
            var probe=CloneOwned(out var clones);
            // Wire empty slot0 is not an actual attachment. Normalize only that
            // sentinel before invoking the original source transaction.
            var held=clones[_heldItem];
            if(AttachmentNoise.InstalledId(held,intent.Slot)==0)AttachmentNoise.SetInstalledId(held,intent.Slot,-1);
            if(!AttachmentTransaction.FitAttachmentTo(held,probe,intent.Slot,intent.Incoming.id,clones[intent.Incoming],out refusal))
                return refusal??"That inventory item changed";
            if(AttachmentNoise.InstalledId(_heldItem,intent.Slot)==0)AttachmentNoise.SetInstalledId(_heldItem,intent.Slot,-1);
            if(!AttachmentTransaction.FitAttachmentTo(_heldItem,Inventory,intent.Slot,intent.Incoming.id,intent.Incoming,out _))
                throw new InvalidOperationException("Attachment preflight disagreed with inventory");
        }
        return null;
    }
    void ApplyAttachmentAndPublish(AttachmentIntent intent)
    {
        string refusal=ApplyAttachment(intent);
        if(refusal!=null){_emit("attachmentRejected",new{playerId=_owner,action=intent.Action,slot=intent.Slot,reason=refusal,text=refusal});return;}
        RefreshAttachmentMask();SaveGunState();UpdateProfile();
        _emit("attachmentChanged",new{playerId=_owner,action=intent.Action,itemId=_heldItem.id,slot=intent.Slot,selectionVersion=_selectionVersion});
    }
    void StepAttachmentActions()
    {
        while(_attachmentIntents.TryDequeue(out var intent))ApplyAttachmentAndPublish(intent);
        if(_magSwapUntil>0&&Tick>=_magSwapUntil)
        {
            _magSwapUntil=0;_emit("reloadComplete",new{playerId=_owner,ammo=Ammo});
            if(_magSwapAutoRack){_magSwapAutoRack=false;BeginRackAnimation(ReloadSpeed);_cycleReadyTick=Tick+Ticks(HammerSeconds/ReloadSpeed);}
        }
        if(!_unloading)return;
        if(!UsesShells||_combat?.Alive!=true){_unloading=false;return;}
        if(Tick<_unloadAt)return;
        var shell=ShellAsset;
        if(Gun.ShellReload)
        {
            if(Ammo>0&&shell!=null){if(TryReturnShells(shell,1))Ammo--;else _unloading=false;}
            if(Ammo<=0)_unloading=false;
            else _unloadAt=Tick+Ticks(_catalog.ReloadSeconds(_heldItem.id)/Math.Max(1,Gun.AmmoMax));
        }
        else{if(shell!=null&&Ammo>0&&TryReturnShells(shell,Ammo))Ammo=0;_unloading=false;}
        SaveGunState();UpdateProfile();
        if(!_unloading)_emit("reloadComplete",new{playerId=_owner,ammo=Ammo});
    }
    void RefreshAttachmentMask()
    {
        int mask=0;
        for(int i=0;i<AttachmentNoise.Slots.Length;i++)if(AttachmentNoise.InstalledId(_heldItem,AttachmentNoise.Slots[i])>0)mask|=1<<i;
        _heldItem.gunAttach=mask;
    }
    void DropAmmo(Item item)
    {
        if(!_server.Players.TryGetByOwner(_owner,out var player))throw new InvalidOperationException("Player position unavailable");
        _server.WorldItems.ServerSpawn(_server.Ids.Mint(),item,player.Pos+new UnityEngine.Vector3(0,1,0),Tick);
    }
    void BeginMagazineAnimation(float speed) => _emit("reload",new{playerId=_owner,itemId=_heldItem.id,seconds=_catalog.ReloadSeconds(_heldItem.id)/speed,speed,shellByShell=false,magazineAction=true});
    void BeginRackAnimation(float speed) => _emit("rechamber",new{playerId=_owner,itemId=_heldItem.id,delay=0,seconds=HammerSeconds/speed});
    bool TryReturnShells(ItemAsset shell,int count)
    {
        var probe=CloneOwned(out _);if(!probe.tryAddItem(new Item(shell.id,(byte)count)))return false;
        return Inventory.tryAddItem(new Item(shell.id,(byte)count));
    }
    void ReturnShells(ItemAsset shell,int count)
    {
        var item=new Item(shell.id,(byte)count);if(!Inventory.tryAddItem(item))DropAmmo(item);
    }
    void ChooseShellType(ushort id)
    {
        var loaded=ShellAsset;
        if(loaded?.id==id&&Ammo>=Gun.AmmoMax)return;
        if(loaded!=null&&loaded.id!=id&&Ammo>0){ReturnShells(loaded,Ammo);Ammo=0;}
        SelectedShellId=id;
        BeginReload(Tick);
    }
    void BeginUnload()
    {
        _unloading=true;float speed=ReloadSpeed;double full=_catalog.ReloadSeconds(_heldItem.id)/speed;
        _unloadAt=Tick+Ticks(Gun.ShellReload?full/Math.Max(1,Ammo):full);
        _emit("reload",new{playerId=_owner,itemId=_heldItem.id,seconds=full,speed,shellByShell=Gun.ShellReload,unloading=true});
    }
    public Dictionary<int,ushort> CaptureShells() => new(_selectedShellByCaliber);
    public void RestoreShells(Dictionary<int,ushort> shells)
    {
        _selectedShellByCaliber.Clear();
        if(shells!=null)foreach(var choice in shells)
        {
            var asset=Assets.find(choice.Value);
            if(asset?.isAmmo==true&&asset.magCaliber==choice.Key)_selectedShellByCaliber[choice.Key]=choice.Value;
        }
        UpdateProfile();
    }
    void ResetAttachmentAnimation(){_unloading=_magSwapAutoRack=false;_magSwapUntil=0;}
}
