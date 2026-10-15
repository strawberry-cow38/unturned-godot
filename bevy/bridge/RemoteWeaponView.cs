using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Read-only owner equipment view. Selection and attachment requests
/// identify real server Item tokens; no client inventory or weapon state writes.</summary>
sealed class RemoteWeaponView
{
    readonly NetWorldClient _client;
    readonly Catalog _catalog;
    readonly Action<string, object> _emit;
    ulong? _observedSelection;
    ushort? _selectionChange;
    uint _nextOptions, _pendingOptions;
    long _optionsAt;
    AttachmentOptionsEvent? _options;
    PlayerInventory _queriedInventory;
    OwnerEquipmentState _queriedEquipment;
    PlayerInventory Inventory => _client.Inventories.TryGet(_client.PlayerId, out var bag) ? bag.Inventory : null;
    OwnerEquipmentState Equipment => _client.Equipment.TryGet(_client.PlayerId,out var state)?state:null;
    bool Alive => _client.CombatState.TryGet(_client.PlayerId, out var player) && player.Alive;
    public int Ammo => Equipment?.Ammo??0;

    public RemoteWeaponView(NetWorldClient client, Catalog catalog, Action<string, object> emit)
    {
        _client=client;_catalog=catalog;_emit=emit;
        client.AttachmentOptionsReceived+=value=>{
            if(value.RequestId!=_pendingOptions)return;
            _options=value;_pendingOptions=0;
        };
    }
    Item At(int page,int x,int y)
    {
        if(Inventory==null||page<0||page>=PlayerInventory.OWNPAGES||x<0||x>255||y<0||y>255)return null;
        var p=Inventory.items[page];byte index=p.getIndex((byte)x,(byte)y);
        return index==byte.MaxValue?null:p.getItem(index)?.item;
    }
    Item Selected
    {
        get {
            var state=Equipment;if(state==null||state.Token==0)return null;
            var a=state.Held;var item=At(a.Page,a.X,a.Y);
            return item?.id==a.Id&&_client.Inventories.TryGetItemToken(_client.PlayerId,a.Page,a.X,a.Y,out var token)&&token==state.Token?item:null;
        }
    }
    public bool RequestSelect(int page,int x,int y,bool equip)
    {
        var item=At(page,x,y);var asset=item==null?null:Assets.find(item.id);
        if(!Alive||!HandRules.CanEquipItemAsset(asset)||Equipment==null
            ||!_client.Inventories.TryGetItemToken(_client.PlayerId,(byte)page,(byte)x,(byte)y,out var token))return false;
        return _client.SendSelectHeld((byte)page,(byte)x,(byte)y,item.id,token,equip);
    }
    public void Step(ushort held)
    {
        var state=Equipment;if(state==null)return;
        if(_observedSelection!=state.SelectionVersion)
        {
            _observedSelection=state.SelectionVersion;_selectionChange=state.Held.Id;
            if(_options?.SelectionVersion!=state.SelectionVersion)_options=null;
        }
    }
    public ushort? TakeSelectionChange(){var value=_selectionChange;_selectionChange=null;return value;}
    public bool SetFiremode(int mode)
    {
        var state=Equipment;var gun=state==null?null:_catalog.GunDefs.GetValueOrDefault(state.Held.Id);
        bool valid=gun!=null&&(mode==0&&gun.HasSafety||mode==1&&gun.HasSemi||mode==2&&gun.HasAuto||mode==3&&gun.BurstCount>0);
        return Alive&&valid&&_client.SendWeaponIntent(new(){Action=EquipmentAction.Firemode,GunToken=state.Token,
            SelectionVersion=state.SelectionVersion,Firemode=(byte)mode});
    }
    public bool RequestAttachment(string action,JsonElement args)
    {
        if(!Alive||Equipment==null)return false;
        try
        {
            if(!Enum.TryParse<EquipmentSlot>(BridgeHost.Text(args,"slot","Sight"),out var slot)||!Enum.IsDefined(slot))return false;
            var command=new WeaponIntentCommand {
                Action=EquipmentJson.Action(action),Slot=slot,GunToken=args.GetProperty("gunToken").GetUInt64(),
                SelectionVersion=args.GetProperty("selectionVersion").GetUInt64(),ShellId=checked((ushort)BridgeHost.Int(args,"itemId")),
            };
            if(command.Action==EquipmentAction.Install)
            {
                command.IncomingToken=args.GetProperty("instanceToken").GetUInt64();
                command.Incoming=new(checked((byte)BridgeHost.Int(args,"page")),checked((byte)BridgeHost.Int(args,"x")),checked((byte)BridgeHost.Int(args,"y")),0);
                if(command.Incoming.Page>=PlayerInventory.OWNPAGES)return false;
            }
            return _client.SendWeaponIntent(command);
        }
        catch(Exception error) when(error is InvalidOperationException or KeyNotFoundException or OverflowException or FormatException)
        { _emit("attachmentRejected",new{playerId=_client.PlayerId,text="That attachment request is no longer valid"});return false; }
    }
    public object AttachmentOptions()
    {
        var state=Equipment;long now=_client.Session.CurrentTick;
        bool stale=_options==null||_queriedEquipment!=state||!ReferenceEquals(_queriedInventory,Inventory);
        if(stale&&(_pendingOptions==0||now-_optionsAt>=NetProtocol.TicksPerSecond)&&now-_optionsAt>=5)
        {
            _pendingOptions=++_nextOptions;_optionsAt=now;_queriedInventory=Inventory;_queriedEquipment=state;
            if(!_client.SendAttachmentOptions(_pendingOptions))_pendingOptions=0;
        }
        return _options is AttachmentOptionsEvent options&&(!options.Supported||options.SelectionVersion==state?.SelectionVersion)
            ?EquipmentJson.Options(options):new{pending=true};
    }
    public object Snapshot()
    {
        var state=Equipment;if(state==null)return null;
        var item=Selected;var gun=item==null?null:_catalog.GunDefs.GetValueOrDefault(item.id);
        return new {
            held=state.Token==0?null:EquipmentJson.Address(state.Held),selectionVersion=state.SelectionVersion,
            state.Reloading,state.Unloading,state.Rechambering,state.Broken,state.CanFire,state.Ammo,state.Capacity,
            state.Chambered,state.Firemode,magazineId=state.MagazineId,shellItemId=state.ShellItemId,state.Silenced,
            attachments=gun==null?null:new {sightId=item.gunSightId,barrelId=item.gunBarrelId,gripId=item.gunGripId,
                tacticalId=item.gunTacticalId,magazineId=item.gunMagId,mask=item.gunAttach,seeded=item.gunAttachSeeded},
        };
    }
}
