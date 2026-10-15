using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed partial class WeaponServices
{
    void InstallEquipmentAuthority()
    {
        _server.Inventories.ItemIdentityProvider = WeaponState.ItemIdentity;
        _server.Transactions.AuthoritativeEquipment = true;
        _server.SelectHeldRequested = (owner, command) => State(owner).SelectFromWire(command);
        _server.WeaponIntentRequested = (owner, command) => State(owner).QueueFromWire(command);
        _server.AttachmentOptionsFor = (owner, serial) => State(owner).OptionsForWire(serial);
    }

    void ForwardEquipmentFact(string kind, object data)
    {
        EquipmentFactKind? tag = kind switch {
            "attachmentRejected" => EquipmentFactKind.Rejected, "attachmentChanged" => EquipmentFactKind.Changed,
            "reload" => EquipmentFactKind.Reload, "rechamber" => EquipmentFactKind.Rechamber,
            "reloadComplete" => EquipmentFactKind.ReloadComplete, "reloadCancelled" => EquipmentFactKind.ReloadCancelled,
            "equipRefused" => EquipmentFactKind.EquipRefused, "dryFire" => EquipmentFactKind.DryFire,
            "barrelBroken" => EquipmentFactKind.BarrelBroken, _ => null,
        };
        if (tag == null) return;
        var json = JsonSerializer.SerializeToElement(data);
        ushort owner = checked((ushort)BridgeHost.Int(json, "playerId"));
        var fact = new EquipmentFactEvent {
            Kind = tag.Value, Action = EquipmentJson.Action(BridgeHost.Text(json,"action")),
            Slot = Enum.TryParse<EquipmentSlot>(BridgeHost.Text(json,"slot"),out var slot)?slot:EquipmentSlot.Sight,
            ItemId = checked((ushort)BridgeHost.Int(json,"itemId")), Ammo = checked((ushort)BridgeHost.Int(json,"ammo")),
            BarrelId = checked((ushort)BridgeHost.Int(json,"barrelId")),
            SelectionVersion = json.TryGetProperty("selectionVersion",out var version)?version.GetUInt64():0,
            Tick = (uint)_server.Session.CurrentTick, Seconds = BridgeHost.Float(json,"seconds"),
            Delay = BridgeHost.Float(json,"delay"), Speed = BridgeHost.Float(json,"speed"),
            ShellByShell = BridgeHost.Bool(json,"shellByShell"), MagazineAction = BridgeHost.Bool(json,"magazineAction"),
            Unloading = BridgeHost.Bool(json,"unloading"), Reason = BridgeHost.Text(json,"text",BridgeHost.Text(json,"reason")),
        };
        _server.SendEventTo(owner,NetMessagePak.Pack(ReplicationIds.EventEquipmentFact,fact.Write));
    }
}

public sealed partial class WeaponState
{
    EquipmentAddress AddressOf(Item item)
    {
        if(item!=null&&Inventory!=null)
            for(byte p=0;p<PlayerInventory.OWNPAGES;p++)foreach(var jar in Inventory.items[p].items)
                if(ReferenceEquals(item,jar.item))return new(p,jar.x,jar.y,item.id);
        return default;
    }
    public OwnerEquipmentState EquipmentReplica() => new() {
        Held = AddressOf(_ownedHeld), Token = ItemIdentity(_ownedHeld), SelectionVersion = (ulong)_selectionVersion,
        Reloading = _reloading||_unloading||MagazineBusy, Unloading = _unloading, Rechambering = Tick<_cycleReadyTick,
        Broken = _ownedHeld?.quality==0, CanFire = Gun!=null&&_combat?.Alive==true&&_heldItem?.quality>0&&_heldItem.gunFiremode!=0,
        Ammo = checked((ushort)(Gun==null?0:Ammo)), Capacity = checked((ushort)(Gun==null?0:MagCapacity)),
        Chambered = Gun!=null&&_chambered, Firemode = checked((sbyte)(Gun==null?-1:_heldItem.gunFiremode)),
        MagazineId = _loadedMagId, ShellItemId = checked((ushort)(UsesShells?ShellAsset?.id??0:0)), Silenced = Gun!=null&&Suppressed,
    };
    public void SelectFromWire(SelectHeldCommand command)
    {
        if(command.Token==0&&command.Address.Id==0&&!command.Equip)
        {
            _selected=null;_selectedId=0;_selectionPending=false;_pendingEquip=null;_emptySelectionPending=true;
            _selectionChange=0;Bind();return;
        }
        var address=command.Address;var item=At(address.Page,address.X,address.Y);
        var asset=item==null?null:Assets.find(item.id);
        string refusal=null;
        if(item==null||item.id!=address.Id||ItemIdentity(item)!=command.Token)refusal="That inventory item changed";
        else if(!HandRules.CanEquipItemAsset(asset))refusal="That item cannot be held";
        else if(!asset.slot.CanEquipInPage(address.Page))
        {
            int slot=asset.slot.PreferredSlot();
            if(!command.Equip||slot<0)refusal="Move that weapon into a weapon slot first";
            else
            {
                // Same source transaction as CommandEquipItem, on this sender's
                // authoritative inventory. Retain the exact Item reference.
                bool moved=Inventory.TryDrag(address.Page,address.X,address.Y,(byte)slot,0,0,0);
                if(moved)_server.Transactions.Diag.GridMovesApplied++;else _server.Transactions.Diag.GridMovesRejected++;
                if(!moved)refusal="That equipment move was refused";
            }
        }
        if(refusal!=null){_emit("equipRefused",new{playerId=_owner,text=refusal});return;}
        var accepted=AddressOf(item);
        if(accepted.Id==0||Select(accepted.Page,accepted.X,accepted.Y)==0)
        {_emit("equipRefused",new{playerId=_owner,text="That item is no longer available"});return;}
        _selectionChange=item.id;
    }
    public void QueueFromWire(WeaponIntentCommand command)
    {
        // Core has bounded all tags. The same existing source adapter rechecks
        // selected Item identity, version, ownership, busy state and capacity.
        if(command.GunToken>long.MaxValue||command.SelectionVersion>long.MaxValue||command.IncomingToken>long.MaxValue)
        {RefuseAttachment(EquipmentJson.ActionName(command.Action),command.Slot.ToString(),"The selected weapon changed");return;}
        var request=JsonSerializer.SerializeToElement(new {
            slot=command.Slot.ToString(),gunToken=(long)command.GunToken,selectionVersion=(long)command.SelectionVersion,
            page=command.Incoming.Page,x=command.Incoming.X,y=command.Incoming.Y,instanceToken=(long)command.IncomingToken,
            itemId=command.ShellId,mode=command.Firemode,
        });
        QueueAttachment(EquipmentJson.ActionName(command.Action),request);
    }
    public AttachmentOptionsEvent OptionsForWire(uint requestId)
    {
        // Read the existing source menu result; fitting and choice generation
        // have exactly one implementation, shared with integrated play.
        var options=JsonSerializer.SerializeToElement(AttachmentOptions());
        return new() {
            RequestId=requestId,Supported=true,HasGun=Gun!=null,GunToken=ItemIdentity(_heldItem),SelectionVersion=(ulong)_selectionVersion,
            Gun=Gun==null?default:AddressOf(_heldItem),GunName=Gun==null?"":Assets.find(_heldItem.id)?.itemName??"",
            Busy=AttachmentBusy,CanRack=options.GetProperty("canRack").GetBoolean(),
            Slots=options.GetProperty("slots").EnumerateArray().Select(s=>new EquipmentSlotOptions {
                Slot=Enum.Parse<EquipmentSlot>(s.GetProperty("name").GetString()),
                Kind=s.GetProperty("kind").GetString() switch {"shell"=>EquipmentSlotKind.Shell,"magazine"=>EquipmentSlotKind.Magazine,_=>EquipmentSlotKind.Attachment},
                Color=s.GetProperty("color").EnumerateArray().Select(c=>c.GetSingle()).ToArray(),
                Visible=s.GetProperty("visible").GetBoolean(),Available=s.GetProperty("available").GetBoolean(),
                Detachable=s.GetProperty("detachable").GetBoolean(),InstalledId=s.GetProperty("installedId").GetInt32(),
                Choices=s.GetProperty("choices").EnumerateArray().Select(c=>new EquipmentChoice(
                    new(c.GetProperty("page").GetByte(),c.GetProperty("x").GetByte(),c.GetProperty("y").GetByte(),c.GetProperty("id").GetUInt16()),
                    c.GetProperty("instanceToken").GetUInt64(),c.GetProperty("amount").GetUInt16(),c.GetProperty("quality").GetByte())).ToArray(),
                Shells=s.GetProperty("shells").ValueKind==JsonValueKind.Array?s.GetProperty("shells").EnumerateArray().Select(c=>
                    new EquipmentShell(c.GetProperty("id").GetUInt16(),c.GetProperty("count").GetUInt32(),c.GetProperty("selected").GetBoolean())).ToArray():Array.Empty<EquipmentShell>(),
            }).ToArray(),
        };
    }
}

static class EquipmentJson
{
    public static string FactName(EquipmentFactKind kind)=>kind switch {
        EquipmentFactKind.Rejected=>"attachmentRejected",EquipmentFactKind.Changed=>"attachmentChanged",EquipmentFactKind.Reload=>"reload",
        EquipmentFactKind.Rechamber=>"rechamber",EquipmentFactKind.ReloadComplete=>"reloadComplete",EquipmentFactKind.ReloadCancelled=>"reloadCancelled",
        EquipmentFactKind.EquipRefused=>"equipRefused",EquipmentFactKind.DryFire=>"dryFire",EquipmentFactKind.BarrelBroken=>"barrelBroken",_=>throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    public static object Fact(ushort owner,EquipmentFactEvent value)=>new {
        playerId=owner,action=ActionName(value.Action),slot=value.Slot.ToString(),itemId=value.ItemId,ammo=value.Ammo,barrelId=value.BarrelId,
        selectionVersion=value.SelectionVersion,acceptedTick=value.Tick,seconds=value.Seconds,delay=value.Delay,speed=value.Speed,
        shellByShell=value.ShellByShell,magazineAction=value.MagazineAction,unloading=value.Unloading,reason=value.Reason,text=value.Reason,
    };
    public static EquipmentAction Action(string name) => name switch {
        "removeAttachment"=>EquipmentAction.Remove,"chooseShellType"=>EquipmentAction.ChooseShell,"unloadShells"=>EquipmentAction.Unload,
        "rackGun"=>EquipmentAction.Rack,"setFiremode"=>EquipmentAction.Firemode,_=>EquipmentAction.Install,
    };
    public static string ActionName(EquipmentAction value)=>value switch {
        EquipmentAction.Remove=>"removeAttachment",EquipmentAction.ChooseShell=>"chooseShellType",EquipmentAction.Unload=>"unloadShells",
        EquipmentAction.Rack=>"rackGun",EquipmentAction.Firemode=>"setFiremode",_=>"installAttachment",
    };
    public static object Options(AttachmentOptionsEvent value)=>new {
        pending=false,supported=value.Supported,selectionVersion=value.SelectionVersion,gunToken=value.GunToken,
        gun=value.HasGun?Address(value.Gun):null,gunName=value.GunName,busy=value.Busy,canRack=value.CanRack,
        slots=(value.Slots??Array.Empty<EquipmentSlotOptions>()).Select(s=>new {
            name=s.Slot.ToString(),color=s.Color,visible=s.Visible,available=s.Available,installedId=s.InstalledId,detachable=s.Detachable,
            kind=s.Kind.ToString().ToLowerInvariant(),choices=s.Choices.Select(c=>new {page=c.Address.Page,x=c.Address.X,y=c.Address.Y,id=c.Address.Id,
                amount=c.Amount,quality=c.Quality,instanceToken=c.Token}).ToArray(),
            shells=s.Kind==EquipmentSlotKind.Shell?s.Shells.Select(c=>new{id=c.Id,count=c.Count,selected=c.Selected}).ToArray():null,
        }).ToArray(),
    };
    public static object Address(EquipmentAddress value)=>new {id=value.Id,page=value.Page,x=value.X,y=value.Y,
        kind=HandRules.KindOf(Assets.find(value.Id)).ToString().ToLowerInvariant()};
}
