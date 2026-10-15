using System;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;
namespace UnturnedNet.Tests;
[TestFixture]
public sealed class VehicleDoorProtocolTests
{
    static NetPakReader Reader(byte[] bytes){var r=new NetPakReader();r.SetBufferSegment(bytes,bytes.Length);return r;}
    static byte[] Pack(Action<NetPakWriter> write)=>NetMessagePak.Pack(1,write)[1..];
    [Test]
    public void DoorFullDeltaExactPhaseAndTruncatedRowsAreAtomic()
    {
        var source=new VehicleReplication();var copy=new VehicleReplication();var id=new NetId(7);
        source.ServerSpawn(id,2,0,Vector3.zero,0,6000);source.ServerPublishDoor(id,true,true,.675f,1);
        byte[] bytes=Pack(w=>source.WriteFull(w,new ReplicationContext(1,1,Vector3.zero)));
        copy.ReadSnapshot(Reader(bytes),true);Assert.That(copy.TryGet(id,out var row),Is.True);
        Assert.That(row.HasBiFoldDoor&&row.DoorOpen,Is.True);Assert.That(row.DoorFold,Is.EqualTo(.675f));Assert.That(copy.StateHash(),Is.EqualTo(source.StateHash()));
        for(int n=0;n<bytes.Length;n++){copy.ReadSnapshot(Reader(bytes[..n]),true);Assert.That(copy.StateHash(),Is.EqualTo(source.StateHash()));}
        source.ServerPublishDoor(id,true,false,.325f,2);copy.ReadSnapshot(Reader(Pack(w=>source.WriteDelta(w,new ReplicationContext(1,2,Vector3.zero),1))),false);
        Assert.That(copy.StateHash(),Is.EqualTo(source.StateHash()));
        Assert.Throws<ArgumentException>(()=>source.ServerPublishDoor(id,true,true,float.NaN,3));
        Assert.Throws<ArgumentException>(()=>source.ServerPublishDoor(id,false,true,0,3));
        Assert.That(ReplicationIds.CommandVehicleDoor,Is.EqualTo(78));Assert.That(ReplicationIds.EventVehicleDoor,Is.EqualTo(59));
    }
    [Test]
    public void DoorIntentAndFactRejectMalformedVectorsAndTruncation()
    {
        foreach(var c in new[]{new VehicleDoorCommand{NetId=7,Driver=true},new VehicleDoorCommand{NetId=7,Origin=new(1,2,3),Direction=Vector3.forward}})
        {var bytes=Pack(c.Write);Assert.That(VehicleDoorCommand.TryRead(Reader(bytes),out var actual),Is.True);Assert.That(actual.Driver,Is.EqualTo(c.Driver));for(int n=0;n<bytes.Length;n++){Assert.That(VehicleDoorCommand.TryRead(Reader(bytes[..n]),out actual),Is.False);Assert.That(actual.NetId,Is.Zero);}}
        foreach(var direction in new[]{Vector3.zero,new Vector3(float.NaN,0,0),Vector3.one})
        {var c=new VehicleDoorCommand{NetId=7,Direction=direction};Assert.Throws<ArgumentException>(()=>Pack(c.Write));}
        var fact=new VehicleDoorEvent{NetId=7,Tick=uint.MaxValue,Open=true,Position=new(3,2,1)};
        var b=Pack(fact.Write);Assert.That(VehicleDoorEvent.TryRead(Reader(b),out var decoded),Is.True);Assert.That(decoded.Tick,Is.EqualTo(uint.MaxValue));
        for(int n=0;n<b.Length;n++){Assert.That(VehicleDoorEvent.TryRead(Reader(b[..n]),out decoded),Is.False);Assert.That(decoded.NetId,Is.Zero);}
    }
    [Test]
    public void DoorDispatchNeedsInstalledAuthorityAndAcceptedOwner()
    {
        var h=new TransactionalHarness(9034).Connected("bus rider");var client=h.Clients[0];int accepted=0;
        var c=new VehicleDoorCommand{NetId=7,Driver=true};client.SendVehicleDoor(c);h.Step(10);Assert.That(accepted,Is.Zero);
        h.Server.VehicleDoorRequested=(owner,_)=>{Assert.That(owner,Is.EqualTo(client.PlayerId));accepted++;};
        h.Server.VehicleDoorAllowed=(_,_)=>false;client.SendVehicleDoor(c);h.Step(10);Assert.That(accepted,Is.Zero);
        h.Server.VehicleDoorAllowed=(_,_)=>true;client.SendVehicleDoor(c);h.Step(10);Assert.That(accepted,Is.EqualTo(1));
    }
}
