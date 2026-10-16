using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class NearbyInventoryTests
{
    static NetPakReader Reader(byte[] data) { var r = new NetPakReader(); r.SetBufferSegment(data, data.Length); return r; }
    static byte[] Bytes(Action<NetPakWriter> write) => NetMessagePak.Pack(0, write)[1..];
    [SetUp] public void AssetsForTest()
    {
        TransactionalFixtures.RegisterAssets();
        foreach (ushort id in Currency.Denominations) Assets.add(new ItemAsset { id=id, size_x=1, size_y=1,
            itemName="money", stackSize=Currency.MaxPerStack });
        Assets.find(TransactionalFixtures.Round556Id).stackSize = 30;
    }
    static NearbyPacking.Source Source(uint id, Item item) => new(id, id + 100UL, item);
    [Test]
    public void SourcePackingRetainsOrderAggregateContributorsOverflowAndNeverEditsWorldItems()
    {
        var gun = new Item(TransactionalFixtures.RifleId) { gunSightId=22, gunAmmo=7, quality=32 };
        var a = new Item(TransactionalFixtures.Round556Id, 23);
        var b = new Item(a.id, 18);
        var cash = new Item(1055, 7, 63);
        var view = NearbyPacking.Pack(new[] { Source(90,gun), Source(4,a), Source(8,b), Source(3,cash) });
        Assert.That(view.HasCandidates, Is.True); Assert.That(view.Rows, Has.Length.EqualTo(5));
        Assert.That(view.Rows[0].Sources[0].WorldId, Is.EqualTo(90), "scene order, never net-ID sort");
        Assert.That(view.Rows[0].Jar.item.gunSightId, Is.EqualTo(22));
        Assert.That(view.Rows[1].Jar.item.amount, Is.EqualTo(30));
        Assert.That(view.Rows[1].Sources.Select(p=>p.Amount), Is.EqualTo(new ushort[]{23,7}));
        Assert.That(view.Rows[2].Jar.item.amount, Is.EqualTo(11));
        Assert.That(view.Rows.Skip(3).Select(r=>r.Jar.item.amount), Is.EqualTo(new ushort[]{500,200}));
        Assert.That(view.Rows.Skip(3).All(r=>r.Jar.item.id==Currency.StackId && r.Sources[0].ItemId==1055), Is.True);
        Assert.That((a.amount,b.amount,cash.id,cash.amount,gun.quality), Is.EqualTo(((ushort)23,(ushort)18,(ushort)1055,(ushort)7,(byte)32)));
        var exact = new Items(PlayerInventory.AREA); exact.loadSize(6,6);
        foreach(var item in new[]{gun,a,b,cash})exact.tryAddItem(item.Clone());
        Assert.That(view.Rows.Select(r=>(r.Jar.x,r.Jar.y,r.Jar.rot,r.Jar.item.id,r.Jar.item.amount)),
            Is.EqualTo(exact.items.Select(j=>(j.x,j.y,j.rot,j.item.id,j.item.amount))));
        Assert.That(NearbyPacking.Pack(Enumerable.Range(1,40).Select(i=>Source((uint)i,new Item(TransactionalFixtures.BeansId)))).Rows.Length, Is.EqualTo(36));
    }
    static NearbyInventoryEvent Fact(Item item) => new() { RequestId=1, ScanId=1, Revision=1, Tick=uint.MaxValue,
        HasCandidates=true, Rows=NearbyPacking.Pack(new[]{Source(8,item)}).Rows };
    [Test]
    public void FullItemOwnerWireIsAtomicBoundedAndPreservesEveryItemField()
    {
        var item = new Item(TransactionalFixtures.RifleId,1,43) { gunAmmo=40000, gunFiremode=300,
            gunChambered=true, gunChamberedType="HP", gunMagId=6, gunAttach=7, gunSightId=22, gunBarrelId=4,
            gunGripId=5, gunTacticalId=6, gunAttachSeeded=true, fuelLevel=-1, fluidType=2, fluidAmount=12.3456f,
            fluidQuality=3, autoDrink=false, preserved=true, magLoadedRound="arbitrary-original-cartridge", cooked=123,cookStyle=2,frozen=74 };
        var fact = Fact(item); var data=Bytes(fact.Write);
        Assert.That(NearbyInventoryEvent.TryRead(Reader(data),out var decoded), Is.True);
        foreach(var field in typeof(Item).GetFields()) Assert.That(field.GetValue(decoded.Rows[0].Jar.item),
            Is.EqualTo(field.GetValue(item)),field.Name);
        for(int n=0;n<data.Length;n++) { Assert.That(NearbyInventoryEvent.TryRead(Reader(data[..n]),out decoded),Is.False);
            Assert.That(decoded,Is.EqualTo(default(NearbyInventoryEvent))); }
        fact.Rows=new[]{fact.Rows[0],fact.Rows[0]}; Assert.That(fact.IsValid,Is.False,"overlapping grid roots");
        fact=Fact(item); fact.Rows[0].Jar.rot=4; Assert.That(fact.IsValid,Is.False);
        fact=Fact(item); fact.Rows[0].Jar.x=6; Assert.That(fact.IsValid,Is.False);
        fact=Fact(item); fact.Rows[0].Sources[0]=fact.Rows[0].Sources[0] with {Token=0}; Assert.That(fact.IsValid,Is.False);
        fact=Fact(item); fact.Rows[0].Jar.item.fluidAmount=float.NaN; Assert.That(fact.IsValid,Is.False);
        fact=Fact(item); fact.Rows[0].Jar.item.gunChamberedType=new string('x',128); Assert.That(fact.IsValid,Is.False);
        fact=Fact(item); fact.Rows=new NearbyPacking.Row[37]; Assert.That(fact.IsValid,Is.False);
    }
    [Test]
    public void RequestWireRejectsEveryTruncationUnknownTagsAndAmbiguousDestinations()
    {
        var c = new NearbyInventoryCommand { RequestId=3,ScanId=1,Revision=2,WorldId=8,Token=9,
            Action=NearbyAction.Transfer,Eye=new(1,2,3),Destination=255 };
        var bytes=Bytes(c.Write); Assert.That(NearbyInventoryCommand.TryRead(Reader(bytes),out var got),Is.True); Assert.That(got,Is.EqualTo(c));
        for(int n=0;n<bytes.Length;n++) { Assert.That(NearbyInventoryCommand.TryRead(Reader(bytes[..n]),out got),Is.False);
            Assert.That(got,Is.EqualTo(default(NearbyInventoryCommand))); }
        c.CrateId=1; Assert.That(c.IsValid,Is.False); c.Destination=PlayerInventory.STORAGE; Assert.That(c.IsValid,Is.True);
        c.Destination=PlayerInventory.AREA; Assert.That(c.IsValid,Is.False);
        c.Destination=2; Assert.That(c.IsValid,Is.False); c.Destination=7; c.Token=0; Assert.That(c.IsValid,Is.False);
        c.Token=9;c.Action=(NearbyAction)99;Assert.That(c.IsValid,Is.False);
        c.Action=NearbyAction.Open;Assert.That(c.IsValid,Is.False,"open has canonical empty transfer identity");
    }
    static NearbyInventoryCommand Transfer(NetWorldClient owner, uint request, Vector3 eye, uint crate=0, byte page=255)
    { var v=owner.Nearby;var row=v.Rows[0];var first=row.Sources[0];return new(){RequestId=request,Action=NearbyAction.Transfer,
        ScanId=v.ScanId,Revision=v.Revision,WorldId=first.WorldId,Token=first.Token,X=row.Jar.x,Y=row.Jar.y,
        Eye=eye,CrateId=crate,Destination=page}; }
    [Test]
    public void RealWireOwnerProjectionTransferReplayPartialMergeAndViewerRefreshHaveNoServerArea()
    {
        var h=new TransactionalHarness(48301).Connected("owner","other");var a=h.Clients[0];var b=h.Clients[1];
        var ids=new Dictionary<Item,ulong>();ulong next=0;
        h.Server.Inventories.ItemIdentityProvider=i=>{if(!ids.TryGetValue(i,out var id))ids[i]=id=++next;return id;};
        h.Server.NearbyVisible=(_,_)=>true;
        h.Server.Players.TryGetByOwner(a.PlayerId,out var player);var eye=player.Pos+new Vector3(0,1,0);
        var raw=new Item(1055,7,63); var world=h.Server.WorldItems.ServerSpawn(new NetId(900),raw,eye,0);
        var bag=h.Server.Transactions.InventoryForTest(a.PlayerId);bag.items[2].resize(1,1);
        bag.items[2].addItem(0,0,0,new Item(Currency.StackId,450));
        a.SendNearbyInventory(new(){RequestId=1,Action=NearbyAction.Open,Eye=eye});
        Assert.That(h.StepUntil(()=>a.Nearby.RequestId==1),Is.True);
        Assert.That(a.Nearby.Rows.Select(r=>r.Jar.item.amount),Is.EqualTo(new ushort[]{500,200}));
        Assert.That(b.Nearby.Revision,Is.Zero);Assert.That(raw.id,Is.EqualTo(1055));
        Assert.That(bag.items[PlayerInventory.AREA].getItemCount(),Is.Zero);
        var command=Transfer(a,2,eye);a.SendNearbyInventory(command);
        Assert.That(h.StepUntil(()=>a.Nearby.RequestId==2),Is.True);
        Assert.That(a.Nearby.Result,Is.EqualTo(NearbyResult.Transferred));
        Assert.That(bag.getItemCount(Currency.StackId),Is.EqualTo(500));Assert.That((raw.id,raw.amount),Is.EqualTo(((ushort)1056,(ushort)650)));
        a.SendNearbyInventory(command);h.Step(12);Assert.That(raw.amount,Is.EqualTo(650),"same request cannot spend again");
        Assert.That(h.Server.WorldItems.TryGet(world.NetIdValue,out _),Is.True);
        var crate=h.Server.Inventories.ServerRegisterCrate(new NetId(901),2,2,player.Pos);
        a.SendOpenStorage(901);b.SendOpenStorage(901);h.Step(12);
        a.SendNearbyInventory(Transfer(a,3,eye,901,7));Assert.That(h.StepUntil(()=>a.Nearby.RequestId==3),Is.True);
        Assert.That(a.Nearby.Result,Is.EqualTo(NearbyResult.Transferred));
        h.Step(12);Assert.That(crate.Storage.items.Sum(j=>j.item.amount),Is.EqualTo(500));Assert.That(raw.amount,Is.EqualTo(150));
        Assert.That(b.Inventories.TryGet(b.PlayerId,out var replica),Is.True);
        Assert.That(replica.Inventory.items[7].items.Sum(j=>j.item.amount),Is.EqualTo(500));
    }
    [Test]
    public void AdmissionRechecksExactRangeVisibilityIdentityAliveViewAndOriginalInsertionOrder()
    {
        var h=new TransactionalHarness(48302).Connected("owner");var owner=h.Clients[0];
        h.Server.Inventories.ItemIdentityProvider=i=>(ulong)i.quality+1; bool clear=true;
        h.Server.NearbyVisible=(_,_)=>clear;h.Server.Players.TryGetByOwner(owner.PlayerId,out var player);var eye=player.Pos;
        h.Server.WorldItems.ServerSpawn(new NetId(900),new Item(TransactionalFixtures.BeansId,1,20),eye,0);
        h.Server.WorldItems.ServerSpawn(new NetId(2),new Item(TransactionalFixtures.BeansId,1,21),eye,0);
        h.Server.WorldItems.ServerSpawn(new NetId(3),new Item(TransactionalFixtures.BeansId,1,22),eye+new Vector3(4,0,0),0);
        owner.SendNearbyInventory(new(){RequestId=1,Action=NearbyAction.Open,Eye=eye});Assert.That(h.StepUntil(()=>owner.Nearby.RequestId==1),Is.True);
        Assert.That(owner.Nearby.Rows.Select(r=>r.Sources[0].WorldId),Is.EqualTo(new uint[]{900,2}));
        clear=false;owner.SendNearbyInventory(Transfer(owner,2,eye));Assert.That(h.StepUntil(()=>owner.Nearby.RequestId==2),Is.True);
        Assert.That(owner.Nearby.Result,Is.EqualTo(NearbyResult.Denied));clear=true;
        var old=Transfer(owner,3,eye);old.Token++;owner.SendNearbyInventory(old);Assert.That(h.StepUntil(()=>owner.Nearby.RequestId==3),Is.True);
        Assert.That(owner.Nearby.Result,Is.EqualTo(NearbyResult.Stale));
        old=Transfer(owner,4,eye);old.Eye=eye+new Vector3(100,0,0);owner.SendNearbyInventory(old);Assert.That(h.StepUntil(()=>owner.Nearby.RequestId==4),Is.True);
        Assert.That(owner.Nearby.Result,Is.EqualTo(NearbyResult.InvalidEye));
        old=Transfer(owner,5,eye);owner.SendNearbyInventory(new(){RequestId=5,Action=NearbyAction.Close});Assert.That(h.StepUntil(()=>owner.Nearby.RequestId==5),Is.True);
        old.RequestId=6;owner.SendNearbyInventory(old);Assert.That(h.StepUntil(()=>owner.Nearby.RequestId==6),Is.True);Assert.That(owner.Nearby.Result,Is.EqualTo(NearbyResult.Stale));
        h.Server.CombatState.TryGet(owner.PlayerId,out var life);life.Alive=false;
        owner.SendNearbyInventory(new(){RequestId=7,Action=NearbyAction.Open,Eye=eye});Assert.That(h.StepUntil(()=>owner.Nearby.RequestId==7),Is.True);
        Assert.That(owner.Nearby.Result,Is.EqualTo(NearbyResult.Dead));
    }
    [Test]
    public void LegacyGridCommandsCannotCreateSyntheticGroundInventory()
    {
        var h=new TransactionalHarness(48303).Connected("owner");var a=h.Clients[0];
        var bag=h.Server.Transactions.InventoryForTest(a.PlayerId);
        bag.items[2].resize(6,6);bag.items[2].addItem(0,0,0,new Item(TransactionalFixtures.Round556Id,20));
        // Even an old engine adapter leaving a 6x6 AREA cannot make it an
        // authoritative destination. Only world-ID Nearby transfers own it.
        bag.items[PlayerInventory.AREA].resize(6,6);
        Assets.add(new ItemAsset{id=9302,type=EItemType.SHIRT,size_x=1,size_y=1});
        bag.wearShirt(new Item(9302));
        a.SendSplitItem(2,0,0,3,PlayerInventory.AREA,0,0,0);
        a.SendQuickTransfer(2,0,0,PlayerInventory.AREA);
        a.SendUnwearClothing((byte)EItemType.SHIRT,PlayerInventory.AREA,0,0);
        a.SendMoveItem(2,0,0,PlayerInventory.AREA,0,0,0);
        h.Step(20);
        Assert.That(bag.items[PlayerInventory.AREA].getItemCount(),Is.Zero);
        Assert.That(bag.getItemCount(TransactionalFixtures.Round556Id),Is.EqualTo(20));
        Assert.That(bag.wornShirt?.id,Is.EqualTo(9302));
    }
    [Test]
    public void AggregateMoneyBeyondUshortPreservesAllWorldValueAndOnlyAdmittedQuota()
    {
        var h=new TransactionalHarness(48304).Connected("owner");var a=h.Clients[0];
        var ids=new Dictionary<Item,ulong>();ulong next=0;
        h.Server.Inventories.ItemIdentityProvider=i=>{if(!ids.TryGetValue(i,out var id))ids[i]=id=++next;return id;};
        h.Server.NearbyVisible=(_,_)=>true;h.Server.Players.TryGetByOwner(a.PlayerId,out var player);var eye=player.Pos;
        var raw=new Item(1055,1000);h.Server.WorldItems.ServerSpawn(new NetId(999),raw,eye,0);
        var bag=h.Server.Transactions.InventoryForTest(a.PlayerId);bag.items[2].resize(1,1);
        bag.items[2].addItem(0,0,0,new Item(Currency.StackId,453));
        a.SendNearbyInventory(new(){RequestId=1,Action=NearbyAction.Open,Eye=eye});Assert.That(h.StepUntil(()=>a.Nearby.RequestId==1),Is.True);
        Assert.That(a.Nearby.Rows.Sum(r=>r.Jar.item.amount),Is.EqualTo(18000));
        a.SendNearbyInventory(Transfer(a,2,eye));Assert.That(h.StepUntil(()=>a.Nearby.RequestId==2),Is.True);
        Assert.That(a.Nearby.Result,Is.EqualTo(NearbyResult.Transferred));
        long WorldValue()=>h.Server.WorldItems.All.Sum(e=>(long)Currency.ValueOf(e.ServerItem.id)*Math.Max(1,(int)e.ServerItem.amount));
        Assert.That(WorldValue()+bag.getItemCount(Currency.StackId),Is.EqualTo(100453));
        Assert.That(a.Nearby.Rows.Sum(r=>r.Jar.item.amount),Is.EqualTo(17953),"fresh change is not silently rediscovered in the old scan");
        Assert.That(h.Server.WorldItems.All.Count(),Is.EqualTo(2));
    }

    static NearbyOperationCommand Op(NetWorldClient owner, uint request, Vector3 eye, NearbyOperation op, int row=0,
        byte page=0, byte x=0, byte y=0, byte rot=0, ushort amount=0, uint crate=0)
    { var v=owner.Nearby;var r=v.Rows[row];var first=r.Sources[0];return new(){RequestId=request,Operation=op,ScanId=v.ScanId,
        Revision=v.Revision,WorldId=first.WorldId,Token=first.Token,X=r.Jar.x,Y=r.Jar.y,Eye=eye,CrateId=crate,Page=page,
        ToX=x,ToY=y,ToRot=rot,Amount=amount}; }
    [Test]
    public void OperationWireIsAtomicAndRejectsEveryAmbiguousShape()
    {
        var c=new NearbyOperationCommand{RequestId=4,Operation=NearbyOperation.Split,Eye=new(1,2,3),ScanId=1,Revision=2,WorldId=8,
            Token=9,X=5,Y=5,Amount=3,Page=3,ToX=2,ToY=1,ToRot=1};
        var bytes=Bytes(c.Write);Assert.That(NearbyOperationCommand.TryRead(Reader(bytes),out var got),Is.True);Assert.That(got,Is.EqualTo(c));
        for(int n=0;n<bytes.Length;n++){Assert.That(NearbyOperationCommand.TryRead(Reader(bytes[..n]),out got),Is.False);
            Assert.That(got,Is.EqualTo(default(NearbyOperationCommand)));}
        var bad=c;bad.Amount=0;Assert.That(bad.IsValid,Is.False,"split always names a positive amount");
        bad=c;bad.Operation=NearbyOperation.Place;Assert.That(bad.IsValid,Is.False,"place moves the whole row, never a count");
        bad.Amount=0;Assert.That(bad.IsValid,Is.True);
        bad=c;bad.Page=PlayerInventory.AREA;Assert.That(bad.IsValid,Is.False,"never a ground destination");
        bad=c;bad.Page=PlayerInventory.STORAGE;Assert.That(bad.IsValid,Is.False,"owned scope cannot name a container page");
        bad.CrateId=7;Assert.That(bad.IsValid,Is.True);bad.Page=2;Assert.That(bad.IsValid,Is.False,"container scope cannot name an owned page");
        bad=c;bad.ToRot=4;Assert.That(bad.IsValid,Is.False);
        bad=c;bad.X=6;Assert.That(bad.IsValid,Is.False);
        bad=c;bad.Token=0;Assert.That(bad.IsValid,Is.False);
        bad=c;bad.Eye=new(float.NaN,0,0);Assert.That(bad.IsValid,Is.False);
        bad=c;bad.Operation=NearbyOperation.EmptyFuel;Assert.That(bad.IsValid,Is.False,"empty carries no destination or amount");
        bad.Amount=0;bad.Page=0;bad.ToX=0;bad.ToY=0;bad.ToRot=0;Assert.That(bad.IsValid,Is.True);
        bad.CrateId=1;Assert.That(bad.IsValid,Is.False);
        bad=c;bad.Operation=(NearbyOperation)3;Assert.That(bad.IsValid,Is.False);
    }
    (TransactionalHarness h,NetWorldClient a,PlayerInventory bag,Vector3 eye) Owner(int seed,params string[] names)
    {
        var h=new TransactionalHarness(seed).Connected(names.Length==0?new[]{"owner"}:names);var a=h.Clients[0];
        var ids=new Dictionary<Item,ulong>();ulong next=0;
        h.Server.Inventories.ItemIdentityProvider=i=>{if(!ids.TryGetValue(i,out var id))ids[i]=id=++next;return id;};
        h.Server.NearbyVisible=(_,_)=>true;h.Server.Players.TryGetByOwner(a.PlayerId,out var player);
        return (h,a,h.Server.Transactions.InventoryForTest(a.PlayerId),player.Pos+new Vector3(0,1,0));
    }
    static void Open(TransactionalHarness h,NetWorldClient a,uint request,Vector3 eye)
    { a.SendNearbyInventory(new(){RequestId=request,Action=NearbyAction.Open,Eye=eye});Assert.That(h.StepUntil(()=>a.Nearby.RequestId==request),Is.True); }
    // A refused older request's reply can be followed in the same tick by the
    // ordinary refresh under the newest ID, so observe the correlated event.
    static NearbyResult Send(TransactionalHarness h,NetWorldClient a,NearbyOperationCommand c)
    {
        NearbyResult? seen=null;void Saw(NearbyInventoryEvent e){if(e.RequestId==c.RequestId)seen??=e.Result;}
        a.NearbyChanged+=Saw;
        try{Assert.That(a.SendNearbyOperation(c),Is.True);Assert.That(h.StepUntil(()=>seen.HasValue),Is.True);h.Step(2);return seen.Value;}
        finally{a.NearbyChanged-=Saw;}
    }
    [Test]
    public void PlaceUsesTheDraggedCellRotationAndHolsterRuleAndNeverSwapsWithTheGround()
    {
        var (h,a,bag,eye)=Owner(48311);
        var rifle=new Item(TransactionalFixtures.RifleId,1,61){gunAmmo=9,gunSightId=22};
        var world=h.Server.WorldItems.ServerSpawn(new NetId(900),rifle,eye,0);
        bag.items[PlayerInventory.BACKPACK].resize(4,4);bag.items[PlayerInventory.BACKPACK].addItem(0,0,0,new Item(TransactionalFixtures.ScrapId));
        Open(h,a,1,eye);
        Assert.That(Send(h,a,Op(a,2,eye,NearbyOperation.Place,page:PlayerInventory.BACKPACK,x:0,y:0,rot:1)),Is.EqualTo(NearbyResult.NoRoom),
            "an occupied cell is refused; a ground row has nowhere to take a swapped occupant");
        Assert.That(Send(h,a,Op(a,3,eye,NearbyOperation.Place,page:1)),Is.EqualTo(NearbyResult.NoRoom),
            "primary-only rifle cannot be holstered in SECONDARY");
        Assert.That(h.Server.WorldItems.TryGet(world.NetIdValue,out _),Is.True);
        var place=Op(a,4,eye,NearbyOperation.Place,page:PlayerInventory.BACKPACK,x:2,y:0,rot:1);
        Assert.That(Send(h,a,place),Is.EqualTo(NearbyResult.Transferred));
        var jar=bag.items[PlayerInventory.BACKPACK].items.Single(j=>j.item.id==TransactionalFixtures.RifleId);
        Assert.That((jar.x,jar.y,jar.rot),Is.EqualTo(((byte)2,(byte)0,(byte)1)),"rotated 4x2 occupies 2x4 at the dragged cell");
        foreach(var f in typeof(Item).GetFields())Assert.That(f.GetValue(jar.item),Is.EqualTo(f.GetValue(rifle)),f.Name);
        Assert.That(h.Server.WorldItems.TryGet(world.NetIdValue,out _),Is.False);
        Assert.That(a.Nearby.Rows,Is.Empty);
        a.SendNearbyOperation(place);h.Step(12);
        Assert.That(bag.getItemCount(TransactionalFixtures.RifleId),Is.EqualTo(1),"exact replay resends, never re-applies");
        h.Server.WorldItems.ServerSpawn(new NetId(901),new Item(TransactionalFixtures.RifleId),eye,0);Open(h,a,5,eye);
        Assert.That(Send(h,a,Op(a,6,eye,NearbyOperation.Place,page:0,x:3,y:2,rot:3)),Is.EqualTo(NearbyResult.Transferred));
        Assert.That(bag.items[0].items.Select(j=>(j.x,j.y,j.rot)),Is.EqualTo(new[]{((byte)0,(byte)0,(byte)0)}),"hand slot is its one cell at rot 0");
        Assert.That(bag.items[PlayerInventory.AREA].getItemCount(),Is.Zero);
    }
    [Test]
    public void SplitTakesOnlyTheNamedCountFromContributorsInSourceOrderAndMergesAtTheCell()
    {
        var (h,a,bag,eye)=Owner(48312);
        var first=new Item(TransactionalFixtures.Round556Id,23);var second=new Item(TransactionalFixtures.Round556Id,18);
        h.Server.WorldItems.ServerSpawn(new NetId(900),first,eye,0);h.Server.WorldItems.ServerSpawn(new NetId(901),second,eye,0);
        var pocket=bag.items[2];pocket.resize(3,1);pocket.addItem(1,0,0,new Item(TransactionalFixtures.Round556Id,25));
        pocket.addItem(2,0,0,new Item(TransactionalFixtures.BeansId));
        Open(h,a,1,eye);Assert.That(a.Nearby.Rows[0].Sources.Select(s=>s.Amount),Is.EqualTo(new ushort[]{23,7}));
        Assert.That(Send(h,a,Op(a,2,eye,NearbyOperation.Split,page:2,amount:30)),Is.EqualTo(NearbyResult.Denied),"whole stack is a move");
        Assert.That(Send(h,a,Op(a,3,eye,NearbyOperation.Split,page:2,x:2,amount:4)),Is.EqualTo(NearbyResult.NoRoom),"different item at the cell");
        Assert.That(Send(h,a,Op(a,4,eye,NearbyOperation.Split,page:2,x:0,amount:10)),Is.EqualTo(NearbyResult.Transferred));
        Assert.That(pocket.getItem(0,0).item.amount,Is.EqualTo(10));
        Assert.That((first.amount,second.amount),Is.EqualTo(((ushort)13,(ushort)18)),"first contributor pays first");
        Assert.That(a.Nearby.Rows.Select(r=>r.Jar.item.amount),Is.EqualTo(new ushort[]{30,1}),"remaining admitted quota repacks 13+18");
        Assert.That(Send(h,a,Op(a,5,eye,NearbyOperation.Split,page:2,x:1,amount:12)),Is.EqualTo(NearbyResult.Transferred));
        Assert.That(pocket.getItem(1,0).item.amount,Is.EqualTo(30),"merge stops at the stack size");
        Assert.That((first.amount,second.amount),Is.EqualTo(((ushort)8,(ushort)18)),"only the accepted five rounds were debited");
        Assert.That(bag.getItemCount(TransactionalFixtures.Round556Id)+first.amount+second.amount,Is.EqualTo(25+41),"rounds are conserved");
        Assert.That(Send(h,a,Op(a,6,eye,NearbyOperation.Split,page:2,x:1,amount:1)),Is.EqualTo(NearbyResult.NoRoom),"a full stack has no room");
    }
    [Test]
    public void SplitIntoTheViewedContainerRechecksAccessAndRepaintsTheOtherViewer()
    {
        var (h,a,bag,eye)=Owner(48313,"owner","other");var b=h.Clients[1];
        var raw=new Item(TransactionalFixtures.Round556Id,20);h.Server.WorldItems.ServerSpawn(new NetId(900),raw,eye,0);
        h.Server.Players.TryGetByOwner(a.PlayerId,out var player);
        var crate=h.Server.Inventories.ServerRegisterCrate(new NetId(950),2,2,player.Pos);
        a.SendOpenStorage(950);b.SendOpenStorage(950);h.Step(12);Open(h,a,1,eye);
        Assert.That(Send(h,a,Op(a,2,eye,NearbyOperation.Split,page:7,x:1,y:1,amount:6,crate:951)),Is.EqualTo(NearbyResult.Denied));
        crate.Access=(_,_)=>false;
        Assert.That(Send(h,a,Op(a,3,eye,NearbyOperation.Split,page:7,x:1,y:1,amount:6,crate:950)),Is.EqualTo(NearbyResult.Denied));
        crate.Access=null;a.SendOpenStorage(950);b.SendOpenStorage(950);h.Step(12);   // revoked access also closed both views
        Assert.That(Send(h,a,Op(a,4,eye,NearbyOperation.Split,page:7,x:1,y:1,amount:6,crate:950)),Is.EqualTo(NearbyResult.Transferred));
        Assert.That(crate.Storage.getItem(1,1).item.amount,Is.EqualTo(6));Assert.That(raw.amount,Is.EqualTo(14));
        h.Step(12);Assert.That(b.Inventories.TryGet(b.PlayerId,out var replica),Is.True);
        Assert.That(replica.Inventory.items[7].getItem(1,1)?.item.amount,Is.EqualTo(6),"the other viewer sees the accepted cell");
        Assert.That(bag.items.Take(PlayerInventory.OWNPAGES).Sum(p=>p.items.Count),Is.Zero,"nothing landed in the owner's bag");
    }
    [Test]
    public void EmptyFuelZeroesOnlyTheExactLiveWorldCanAndRepaintsEveryNearbyViewer()
    {
        var (h,a,_,eye)=Owner(48314,"owner","other");var b=h.Clients[1];
        var can=new Item(TransactionalFixtures.GasCanId){fuelLevel=73};var other=new Item(TransactionalFixtures.GasCanId){fuelLevel=40};
        h.Server.WorldItems.ServerSpawn(new NetId(900),can,eye,0);h.Server.WorldItems.ServerSpawn(new NetId(901),other,eye,0);
        h.Server.WorldItems.ServerSpawn(new NetId(902),new Item(TransactionalFixtures.BeansId),eye,0);
        Open(h,a,1,eye);Open(h,b,1,eye);uint before=b.Nearby.Revision;
        Assert.That(Send(h,a,Op(a,2,eye,NearbyOperation.EmptyFuel,row:2)),Is.EqualTo(NearbyResult.Denied),"only a fuel container");
        Assert.That(Send(h,a,Op(a,3,eye,NearbyOperation.EmptyFuel,row:0)),Is.EqualTo(NearbyResult.Transferred));
        Assert.That((can.fuelLevel,other.fuelLevel),Is.EqualTo((0f,40f)));
        Assert.That(a.Nearby.Rows[0].Jar.item.fuelLevel,Is.Zero);
        Assert.That(h.StepUntil(()=>b.Nearby.Revision!=before),Is.True);Assert.That(b.Nearby.Rows[0].Jar.item.fuelLevel,Is.Zero);
        Assert.That(h.Server.WorldItems.All.Count(),Is.EqualTo(3),"empty never takes the can");
    }
    [Test]
    public void OperationsShareTheTransferLedgerAndEveryLiveRecheck()
    {
        var (h,a,bag,eye)=Owner(48315);bool clear=true;h.Server.NearbyVisible=(_,_)=>clear;
        h.Server.WorldItems.ServerSpawn(new NetId(900),new Item(TransactionalFixtures.Round556Id,12),eye,0);
        bag.items[2].resize(2,1);Open(h,a,5,eye);
        Assert.That(Send(h,a,Op(a,4,eye,NearbyOperation.Split,page:2,amount:2)),Is.EqualTo(NearbyResult.Stale),"86 and 87 share one monotonic ledger");
        clear=false;Assert.That(Send(h,a,Op(a,6,eye,NearbyOperation.Split,page:2,amount:2)),Is.EqualTo(NearbyResult.Denied));clear=true;
        var far=Op(a,7,eye,NearbyOperation.Split,page:2,amount:2);far.Eye=eye+new Vector3(100,0,0);
        Assert.That(Send(h,a,far),Is.EqualTo(NearbyResult.InvalidEye));
        var foreign=Op(a,8,eye,NearbyOperation.Split,page:2,amount:2);foreign.Token++;Assert.That(Send(h,a,foreign),Is.EqualTo(NearbyResult.Stale));
        var stale=Op(a,9,eye,NearbyOperation.Split,page:2,amount:2);stale.Revision--;Assert.That(Send(h,a,stale),Is.EqualTo(NearbyResult.Stale));
        h.Server.WorldItems.TryGet(900,out var e);e.ServerItem.quality--;
        Assert.That(Send(h,a,Op(a,10,eye,NearbyOperation.Split,page:2,amount:2)),Is.EqualTo(NearbyResult.Denied),"changed full item bytes");
        Open(h,a,11,eye);h.Server.CombatState.TryGet(a.PlayerId,out var life);life.Alive=false;
        Assert.That(Send(h,a,Op(a,12,eye,NearbyOperation.Split,page:2,amount:2)),Is.EqualTo(NearbyResult.Dead));
        Assert.That(bag.getItemCount(TransactionalFixtures.Round556Id),Is.Zero);Assert.That(e.ServerItem.amount,Is.EqualTo(12));
    }
    static NearbyRowRef RowRef(NetWorldClient owner,int row){var r=owner.Nearby.Rows[row];return new(r.Sources[0].WorldId,r.Sources[0].Token,r.Jar.x,r.Jar.y);}
    static NearbyMagazineTransferCommand Mag(NetWorldClient owner,uint request,Vector3 eye)=>new(){RequestId=request,
        ScanId=owner.Nearby.ScanId,Revision=owner.Nearby.Revision,Eye=eye};
    [Test]
    public void GroundMagazineWireIsAtomicAndNeedsExactlyTheSourceShapes()
    {
        var row=new NearbyRowRef(8,9,1,2);var scoped=new ScopedInventoryAddress(0,2,3,0,TransactionalFixtures.Round556Id,44);
        var shapes=new[]{
            new NearbyMagazineTransferCommand{RequestId=1,ScanId=2,Revision=3,Eye=new(1,2,3),Unloading=true,MagazineGround=true,MagazineRow=row,UnloadRoundId=920},
            new NearbyMagazineTransferCommand{RequestId=1,ScanId=2,Revision=3,MagazineGround=true,MagazineRow=row,Round=scoped},
            new NearbyMagazineTransferCommand{RequestId=1,ScanId=2,Revision=3,Magazine=scoped with{Id=910},RoundGround=true,RoundRow=row},
            new NearbyMagazineTransferCommand{RequestId=1,ScanId=2,Revision=3,MagazineGround=true,MagazineRow=row,RoundGround=true,RoundRow=row with{X=3}}};
        foreach(var c in shapes)
        {
            Assert.That(c.IsValid,Is.True);var bytes=Bytes(c.Write);
            Assert.That(NearbyMagazineTransferCommand.TryRead(Reader(bytes),out var got),Is.True);Assert.That(got,Is.EqualTo(c));
            for(int n=0;n<bytes.Length;n++){Assert.That(NearbyMagazineTransferCommand.TryRead(Reader(bytes[..n]),out got),Is.False);
                Assert.That(got,Is.EqualTo(default(NearbyMagazineTransferCommand)));}
        }
        var bad=shapes[1];bad.MagazineGround=false;bad.MagazineRow=default;bad.Magazine=scoped;
        Assert.That(bad.IsValid,Is.False,"an all-scoped step belongs to81/85");
        bad=shapes[0];bad.MagazineGround=false;bad.MagazineRow=default;bad.Magazine=scoped;Assert.That(bad.IsValid,Is.False,"ground unload needs a ground magazine");
        bad=shapes[0];bad.Round=scoped;Assert.That(bad.IsValid,Is.False,"unload carries no round scope");
        bad=shapes[0];bad.UnloadRoundId=0;Assert.That(bad.IsValid,Is.False);
        bad=shapes[1];bad.UnloadRoundId=920;Assert.That(bad.IsValid,Is.False);
        bad=shapes[1];bad.MagazineRow=row with{X=6};Assert.That(bad.IsValid,Is.False);
        bad=shapes[1];bad.MagazineRow=row with{Token=0};Assert.That(bad.IsValid,Is.False);
        bad=shapes[1];bad.Revision=0;Assert.That(bad.IsValid,Is.False);
        bad=shapes[1];bad.Magazine=scoped;Assert.That(bad.IsValid,Is.False,"one scope per part");
        bad=shapes[2];bad.Round=scoped with{Page=PlayerInventory.AREA};bad.RoundGround=false;bad.RoundRow=default;
        Assert.That(bad.IsValid,Is.False,"AREA is never a scoped address");
    }
    [Test]
    public void GroundMagazineUnloadAndLoadMutateOnlyTheExactLiveItemsAndShareTheMagazineLedger()
    {
        var (h,a,bag,eye)=Owner(48316);var facts=new List<MagazineTransferEvent>();a.MagazineTransferred+=facts.Add;
        var ground=new Item(TransactionalFixtures.StanagId,3);h.Server.WorldItems.ServerSpawn(new NetId(900),ground,eye,0);
        bag.items[2].resize(1,1);Open(h,a,1,eye);
        MagazineTransferEvent Step(NearbyMagazineTransferCommand c)
        {int n=facts.Count;Assert.That(a.SendNearbyMagazineTransfer(c),Is.True);Assert.That(h.StepUntil(()=>facts.Count==n+1),Is.True);h.Step(2);return facts[^1];}
        var unload=Mag(a,2,eye);unload.Unloading=true;unload.MagazineGround=true;unload.MagazineRow=RowRef(a,0);
        unload.UnloadRoundId=TransactionalFixtures.Round556Id;
        var fact=Step(unload);Assert.That((fact.Result,fact.Amount,fact.MagazineToken),Is.EqualTo((MagazineTransferResult.Applied,(ushort)2,unload.MagazineRow.Token)));
        Assert.That(ground.amount,Is.EqualTo(2));Assert.That(bag.getItemCount(TransactionalFixtures.Round556Id),Is.EqualTo(1));
        Assert.That(a.Nearby.Rows[0].Jar.item.amount,Is.EqualTo(2),"owner picture follows the world magazine");
        Assert.That(Step(unload),Is.EqualTo(fact),"exact replay resends the stored result");Assert.That(ground.amount,Is.EqualTo(2));
        var stale=unload;stale.RequestId=3;Assert.That(Step(stale).Result,Is.EqualTo(MagazineTransferResult.StaleMagazine),"old revision");
        bag.items[2].items.Single().item.amount=30;   // the one pocket cell is a full stack
        var full=unload;full.RequestId=4;full.Revision=a.Nearby.Revision;
        Assert.That(Step(full).Result,Is.EqualTo(MagazineTransferResult.NoRoom),"full bag: add before decrement");
        Assert.That(ground.amount,Is.EqualTo(2));
        // Load the same ground magazine back from the owned stack: the whole
        // non-stacking row grows, so a later Take carries every round.
        bag.items[2].resize(2,1);var stack=bag.items[2].items.Single().item;stack.amount=2;
        var token=h.Server.Inventories.ItemIdentityProvider(stack);
        var load=Mag(a,5,eye);load.MagazineGround=true;load.MagazineRow=RowRef(a,0);
        load.Round=new(0,2,0,0,stack.id,token);
        fact=Step(load);Assert.That((fact.Result,fact.Amount),Is.EqualTo((MagazineTransferResult.Applied,(ushort)3)));
        Assert.That((ground.amount,stack.amount),Is.EqualTo(((ushort)3,(ushort)1)));Assert.That(a.Nearby.Rows[0].Jar.item.amount,Is.EqualTo(3));
        var older=load;older.RequestId=1;Assert.That(Step(older).Result,Is.EqualTo(MagazineTransferResult.StaleRequest));
        a.SendMagazineTransfer(5,2,0,0,stack.id,token,0,0,0,TransactionalFixtures.Round556Id,0,true);h.Step(12);
        Assert.That(facts[^1].Result,Is.EqualTo(MagazineTransferResult.StaleRequest),"81 and 88 share one monotonic ledger");
        bag.items[2].resize(2,2);
        a.SendNearbyInventory(Transfer(a,6,eye));Assert.That(h.StepUntil(()=>a.Nearby.RequestId==6),Is.True);
        Assert.That(a.Nearby.Result,Is.EqualTo(NearbyResult.Transferred));
        Assert.That(bag.items.Take(PlayerInventory.OWNPAGES).SelectMany(p=>p.items).Single(j=>j.item.id==TransactionalFixtures.StanagId).item.amount,
            Is.EqualTo(3));Assert.That(h.Server.WorldItems.TryGet(900,out _),Is.False,"no leftover magazine body on the ground");
    }
    [Test]
    public void OwnedMagazineLoadsFromTheFirstGroundContributorAndRechecksLos()
    {
        var (h,a,bag,eye)=Owner(48317);var facts=new List<MagazineTransferEvent>();a.MagazineTransferred+=facts.Add;
        bool clear=true;h.Server.NearbyVisible=(_,_)=>clear;
        var first=new Item(TransactionalFixtures.Round556Id,2);var second=new Item(TransactionalFixtures.Round556Id,5);
        h.Server.WorldItems.ServerSpawn(new NetId(900),first,eye,0);h.Server.WorldItems.ServerSpawn(new NetId(901),second,eye,0);
        bag.items[2].resize(2,2);var mag=new Item(TransactionalFixtures.StanagId,0);bag.items[2].addItem(0,0,0,mag);
        var token=h.Server.Inventories.ItemIdentityProvider(mag);Open(h,a,1,eye);
        Assert.That(a.Nearby.Rows.Single().Sources.Select(s=>s.Amount),Is.EqualTo(new ushort[]{2,5}));
        NearbyMagazineTransferCommand Load(uint id){var c=Mag(a,id,eye);c.Magazine=new(0,2,0,0,mag.id,token);c.RoundGround=true;c.RoundRow=RowRef(a,0);return c;}
        MagazineTransferResult Step(NearbyMagazineTransferCommand c)
        {int n=facts.Count;Assert.That(a.SendNearbyMagazineTransfer(c),Is.True);Assert.That(h.StepUntil(()=>facts.Count==n+1),Is.True);h.Step(2);return facts[^1].Result;}
        clear=false;Assert.That(Step(Load(2)),Is.EqualTo(MagazineTransferResult.StaleRound),"transfer-time LOS");Assert.That(first.amount,Is.EqualTo(2));
        clear=true;
        for(uint i=3;i<6;i++)Assert.That(Step(Load(i)),Is.EqualTo(MagazineTransferResult.Applied));
        Assert.That((mag.amount,second.amount),Is.EqualTo(((ushort)3,(ushort)4)),"first contributor's two rounds, then the second's");
        Assert.That(h.Server.WorldItems.TryGet(900,out _),Is.False,"an emptied ground stack leaves the world");
        Assert.That(facts.All(f=>f.MagazineToken==token),Is.True);
        var wrong=new Item(TransactionalFixtures.Round762Id,4);h.Server.WorldItems.ServerSpawn(new NetId(902),wrong,eye,0);Open(h,a,6,eye);
        var c=Load(7);c.RoundRow=RowRef(a,a.Nearby.Rows.ToList().FindIndex(r=>r.Jar.item.id==wrong.id));
        Assert.That(Step(c),Is.EqualTo(MagazineTransferResult.RuleRefused),"original MagRules caliber");Assert.That(wrong.amount,Is.EqualTo(4));
    }
    [Test]
    public void MaximumProjectionFitsReliableBudgetAndMalformedCountsAreAtomic()
    {
        var parts=Enumerable.Range(1,NearbyPacking.MaxContributions).Select(i=>new NearbyPacking.Contribution((uint)i,(ulong)i,1056,1)).ToArray();
        var fact=new NearbyInventoryEvent{RequestId=1,ScanId=1,Revision=1,HasCandidates=true,
            Rows=new[]{new NearbyPacking.Row(new ItemJar(new Item(1056,(ushort)parts.Length)),parts)}};
        var bytes=Bytes(fact.Write);Assert.That(bytes.Length,Is.LessThan(NetProtocol.MaxReliableMessageBytes));
        Assert.That(NearbyInventoryEvent.TryRead(Reader(bytes),out var decoded),Is.True);Assert.That(decoded.Rows[0].Sources,Has.Length.EqualTo(4096));
        parts[1]=parts[1] with {Token=parts[0].Token};Assert.That(fact.IsValid,Is.False,"duplicate owner token across distinct world IDs");
        parts[1]=parts[0];Assert.That(fact.IsValid,Is.False,"duplicate source in one row");
        var excessive=Bytes(w=>{w.WriteUInt32(1);w.WriteUInt32(1);w.WriteUInt32(1);w.WriteUInt32(1);w.WriteUInt8(0);w.WriteBit(true);w.WriteUInt8(37);});
        Assert.That(NearbyInventoryEvent.TryRead(Reader(excessive),out decoded),Is.False);Assert.That(decoded,Is.EqualTo(default(NearbyInventoryEvent)));
        Assert.Throws<InvalidOperationException>(()=>NearbyPacking.Pack(Enumerable.Range(1,4097).Select(i=>Source((uint)i,new Item(1056,1)))));
    }
    [Test]
    public void OtherOwnerPickupRefreshesPictureAndCloseReplayNeverResurrectsIt()
    {
        var h=new TransactionalHarness(48305).Connected("a","b");var a=h.Clients[0];var b=h.Clients[1];
        h.Server.Inventories.ItemIdentityProvider=_=>9;h.Server.NearbyVisible=(_,_)=>true;
        h.Server.Players.TryGetByOwner(a.PlayerId,out var player);var eye=player.Pos;
        h.Server.WorldItems.ServerSpawn(new NetId(999),new Item(TransactionalFixtures.BeansId),eye,0);
        var open=new NearbyInventoryCommand{RequestId=1,Action=NearbyAction.Open,Eye=eye};
        a.SendNearbyInventory(open);b.SendNearbyInventory(open);
        Assert.That(h.StepUntil(()=>a.Nearby.RequestId==1 && b.Nearby.RequestId==1),Is.True);
        var stale=Transfer(b,2,eye);a.SendNearbyInventory(Transfer(a,2,eye));
        Assert.That(h.StepUntil(()=>a.Nearby.Result==NearbyResult.Transferred && b.Nearby.Rows.Length==0),Is.True);
        b.SendNearbyInventory(stale);Assert.That(h.StepUntil(()=>b.Nearby.RequestId==2),Is.True);Assert.That(b.Nearby.Result,Is.EqualTo(NearbyResult.Stale));
        b.SendNearbyInventory(new(){RequestId=3,Action=NearbyAction.Close});Assert.That(h.StepUntil(()=>b.Nearby.RequestId==3),Is.True);
        b.SendNearbyInventory(open);h.Step(12);Assert.That(b.Nearby.ScanId,Is.Zero);Assert.That(b.Nearby.Rows,Is.Empty);
        var oldOwner=b.PlayerId;b.Disconnect();Assert.That(b.Nearby.Revision,Is.Zero);h.Step(20);
        var ledger=(System.Collections.IDictionary)typeof(NetWorldServer).GetField("_nearby",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(h.Server);
        Assert.That(ledger.Contains(oldOwner),Is.False,"disconnect retires the owner scan and replay ledger");
        var fresh=h.AddClient("fresh");Assert.That(h.StepUntil(()=>fresh.State==NetSessionState.Connected),Is.True);
        fresh.SendNearbyInventory(open);Assert.That(h.StepUntil(()=>fresh.Nearby.RequestId==1),Is.True,"new session accepts a fresh monotonic ledger");
    }

}
