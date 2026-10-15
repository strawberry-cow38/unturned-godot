using System;
using System.Linq;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class VehicleLightingProtocolTests
{
    static NetPakReader Reader(byte[] bytes) { var r=new NetPakReader();r.SetBufferSegment(bytes,bytes.Length);return r; }
    static byte[] Pack(Action<NetPakWriter> write)=>NetMessagePak.Pack(1,write)[1..];
    [Test]
    public void FullBatteryHighBeamPatternsAndEachLampSurviveHashIdenticalSnapshot()
    {
        var source=new VehicleReplication();var copy=new VehicleReplication();var id=new NetId(7);
        source.ServerSpawn(id,11,0,Vector3.zero,0);
        source.ServerPublishLighting(id,VehicleReplication.FlagHeadlights,10000,true,2,127,85,1);
        var context=new ReplicationContext(1,1,Vector3.zero);byte[] bytes=Pack(w=>source.WriteFull(w,context));
        copy.ReadSnapshot(Reader(bytes),true);Assert.That(copy.TryGet(id,out var row),Is.True);
        Assert.That(row.Battery,Is.EqualTo(10000),"The old signed14-bit wire clipped a full source battery to8191");
        Assert.That(row.HighBeam,Is.True);Assert.That(row.LightbarPattern,Is.EqualTo(2));Assert.That(row.LampMask,Is.EqualTo(127));Assert.That(row.BrokenLampMask,Is.EqualTo(85));
        Assert.That(copy.StateHash(),Is.EqualTo(source.StateHash()));
        for(int length=0;length<bytes.Length;length++)
        {copy.ReadSnapshot(Reader(bytes[..length]),true);Assert.That(copy.StateHash(),Is.EqualTo(source.StateHash()),"Truncated row must not clear the accepted view");}
        foreach(var invalid in new Action[]{()=>source.ServerPublishLighting(id,0,10000,true,0,0,0,2),()=>source.ServerPublishLighting(id,2,10000,true,3,0,0,2),()=>source.ServerPublishLighting(id,2,10000,true,0,1,2,2)})Assert.Throws<ArgumentException>(()=>invalid());
    }
    [Test]
    public void CommandsAndFactsRejectUnknownTagsValuesAndEveryTruncatedTailAtomically()
    {
        var cmd=new VehicleLightingCommand{NetId=9,Action=VehicleLightingAction.SetPattern,Value=2};var bytes=Pack(cmd.Write);
        Assert.That(VehicleLightingCommand.TryRead(Reader(bytes),out var read),Is.True);Assert.That(read.Value,Is.EqualTo(2));
        for(int n=0;n<bytes.Length;n++){Assert.That(VehicleLightingCommand.TryRead(Reader(bytes[..n]),out read),Is.False);Assert.That(read.NetId,Is.Zero);}
        foreach(var pair in new[]{(0,1),(1,3),(2,3),(3,7),(4,0),(255,0)})
        {var bad=Pack(w=>{w.WriteUInt32(9);w.WriteUInt8((byte)pair.Item1);w.WriteUInt8((byte)pair.Item2);});Assert.That(VehicleLightingCommand.TryRead(Reader(bad),out read),Is.False);}
        var fact=new VehicleLightingEvent{NetId=9,Kind=VehicleLightingKind.LampBroken,Lamp=6,Tick=uint.MaxValue,Position=new(1,2,3)};bytes=Pack(fact.Write);
        Assert.That(VehicleLightingEvent.TryRead(Reader(bytes),out var result),Is.True);Assert.That(result.Tick,Is.EqualTo(uint.MaxValue));
        for(int n=0;n<bytes.Length;n++){Assert.That(VehicleLightingEvent.TryRead(Reader(bytes[..n]),out result),Is.False);Assert.That(result.NetId,Is.Zero);}
        foreach(byte kind in new byte[]{3,255}){var bad=(byte[])bytes.Clone();bad[8]=kind;Assert.That(VehicleLightingEvent.TryRead(Reader(bad),out result),Is.False);}
        fact.Position=new(float.NaN,0,0);Assert.Throws<ArgumentException>(()=>Pack(fact.Write));
    }
    [Test]
    public void OptionalFlagAuthorityLeavesOldHarnessUntouchedAndVersion67IsRefused()
    {
        var h=new TransactionalHarness(8175).Connected("driver");var client=h.Clients[0];
        var id=h.Server.Ids.Mint();h.Server.Vehicles.ServerSpawn(id,0,0,Vector3.zero,0,100);h.Server.Vehicles.ServerPublishVitals(id,100,100,10000,false,0);
        client.SendEnterVehicle(id.Value);h.Step(10);
        client.SendVehicleState(id.Value,Vector3.zero,Vector3.zero,Vector3.zero,Vector3.zero,0,0,0,false,10,0);h.Step(10);
        h.Server.Vehicles.TryGet(id,out var row);Assert.That(row.Flags,Is.EqualTo(10),"No-service original flag ownership remains unchanged");
        h.Server.VehicleHost.NormalizeDriverFlags=(_,flags)=>(byte)(flags&1);
        client.SendVehicleState(id.Value,Vector3.zero,Vector3.zero,Vector3.zero,Vector3.zero,0,0,0,false,255,0);h.Step(10);Assert.That(row.Flags,Is.EqualTo(1));
        var net=new NetSimHarness(8176);var old=net.AddClient("old lights",version:67);old.Connect();
        Assert.That(net.StepUntil(()=>old.State==NetSessionState.Disconnected,100),Is.True);Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));
    }
}
