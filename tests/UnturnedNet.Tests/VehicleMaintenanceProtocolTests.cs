using System;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class VehicleMaintenanceProtocolTests
{
    static NetPakReader Reader(byte[] bytes) { var r=new NetPakReader();r.SetBufferSegment(bytes,bytes.Length);return r; }
    static byte[] Pack(Action<NetPakWriter> write)=>NetMessagePak.Pack(1,write)[1..];
    [Test]
    public void ShipFuelHealthAndSparseMaintenanceStateSurviveAtomicFullAndDelta()
    {
        var source=new VehicleReplication();var copy=new VehicleReplication();var id=new NetId(7);
        source.ServerSpawn(id,11,0,Vector3.zero,0,40000);
        source.ServerPublishVitals(id,300000,40000,10000,false,1);
        source.ServerPublishMaintenance(id,(1u<<17)|5,1u<<17,165,1);
        var context=new ReplicationContext(1,1,Vector3.zero);byte[] bytes=Pack(w=>source.WriteFull(w,context));
        copy.ReadSnapshot(Reader(bytes),true);Assert.That(copy.TryGet(id,out var row),Is.True);
        Assert.That(row.Fuel,Is.EqualTo(300000),"Original fleet fuel must not clip at old2047.5mL");
        Assert.That(row.Health,Is.EqualTo(40000),"Original ship hull must not clip at old32767.5HP");
        Assert.That(row.GlassMask,Is.EqualTo((1u<<17)|5));Assert.That(row.BrokenGlassMask,Is.EqualTo(1u<<17));Assert.That(row.TireMask,Is.EqualTo(165));
        Assert.That(copy.StateHash(),Is.EqualTo(source.StateHash()));
        for(int length=0;length<bytes.Length;length++)
        {copy.ReadSnapshot(Reader(bytes[..length]),true);Assert.That(copy.StateHash(),Is.EqualTo(source.StateHash()),"Truncated row must preserve entire accepted view");}
        source.ServerPublishMaintenance(id,(1u<<17)|5,4,165,2);
        bytes=Pack(w=>source.WriteDelta(w,new ReplicationContext(1,2,Vector3.zero),1));
        copy.ReadSnapshot(Reader(bytes),false);Assert.That(copy.StateHash(),Is.EqualTo(source.StateHash()));
        Assert.Throws<ArgumentException>(()=>source.ServerPublishMaintenance(id,1,2,0,3));
        Assert.Throws<ArgumentException>(()=>source.ServerPublishMaintenance(id,1u<<18,0,0,3));
    }
    [Test]
    public void AllIntentAndFactTagsRejectMalformedAndTruncatedPayloadsBeforeDispatch()
    {
        foreach(var cmd in new[]{new VehicleMaintenanceCommand{NetId=9,Action=VehicleMaintenanceAction.RepairGlass,Index=17},new VehicleMaintenanceCommand{NetId=9,Action=VehicleMaintenanceAction.RepairTire,Index=7},new VehicleMaintenanceCommand{NetId=9,Action=VehicleMaintenanceAction.Horn}})
        {
            var bytes=Pack(cmd.Write);Assert.That(VehicleMaintenanceCommand.TryRead(Reader(bytes),out var read),Is.True);Assert.That(read.Action,Is.EqualTo(cmd.Action));
            for(int n=0;n<bytes.Length;n++){Assert.That(VehicleMaintenanceCommand.TryRead(Reader(bytes[..n]),out read),Is.False);Assert.That(read.NetId,Is.Zero);}
        }
        foreach(var pair in new[]{(0,18),(1,8),(2,1),(3,0),(255,0)})
        {var bytes=Pack(w=>{w.WriteUInt32(9);w.WriteUInt8((byte)pair.Item1);w.WriteUInt8((byte)pair.Item2);});Assert.That(VehicleMaintenanceCommand.TryRead(Reader(bytes),out _),Is.False);}
        foreach(var kind in Enum.GetValues<VehicleMaintenanceKind>())
        {
            var fact=new VehicleMaintenanceEvent{NetId=9,Kind=kind,Index=(byte)(kind==VehicleMaintenanceKind.ManualHorn?255:1),Tick=uint.MaxValue,Position=new(1,2,3),Normal=Vector3.up,HalfSize=new(.2f,2,3)};
            var bytes=Pack(fact.Write);Assert.That(VehicleMaintenanceEvent.TryRead(Reader(bytes),out var read),Is.True);Assert.That(read.Tick,Is.EqualTo(uint.MaxValue));
            for(int n=0;n<bytes.Length;n++){Assert.That(VehicleMaintenanceEvent.TryRead(Reader(bytes[..n]),out read),Is.False);Assert.That(read.NetId,Is.Zero);}
            bytes[8]=255;Assert.That(VehicleMaintenanceEvent.TryRead(Reader(bytes),out _),Is.False);
            fact.Position=new(float.NaN,0,0);Assert.Throws<ArgumentException>(()=>Pack(fact.Write));
        }
        var invalid=new VehicleMaintenanceEvent{NetId=1,Normal=Vector3.zero,HalfSize=Vector3.one};Assert.Throws<ArgumentException>(()=>Pack(invalid.Write));
        invalid.Normal=Vector3.up;invalid.HalfSize=new(.19f,1,1);Assert.Throws<ArgumentException>(()=>Pack(invalid.Write));
    }
    [Test]
    public void MaintenanceRequiresInstalledAuthorityAndOldProtocol68IsExplicitlyRefused()
    {
        var h=new TransactionalHarness(8195).Connected("driver");var client=h.Clients[0];int called=0;
        var cmd=new VehicleMaintenanceCommand{NetId=9,Action=VehicleMaintenanceAction.Horn};
        client.SendVehicleMaintenance(cmd);h.Step(10);Assert.That(called,Is.Zero);
        h.Server.VehicleMaintenanceRequested=(_,_)=>called++;h.Server.VehicleMaintenanceAllowed=(_,_)=>false;
        client.SendVehicleMaintenance(cmd);h.Step(10);Assert.That(called,Is.Zero);
        h.Server.VehicleMaintenanceAllowed=(_,_)=>true;client.SendVehicleMaintenance(cmd);h.Step(10);Assert.That(called,Is.EqualTo(1));
        var net=new NetSimHarness(8196);var old=net.AddClient("old maintenance",version:68);old.Connect();
        Assert.That(net.StepUntil(()=>old.State==NetSessionState.Disconnected,100),Is.True);Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));
    }
}
