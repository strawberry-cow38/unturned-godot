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
