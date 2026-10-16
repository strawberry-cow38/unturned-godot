using System;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
namespace UnturnedNet.Tests;
/// <summary>v82 vehicle pour/siphon intents: the exact held instance and the car,
/// atomic under truncation, zero identities refused, and the version bump that
/// keeps a v78 peer (which would drop ids 99/100) out.</summary>
[TestFixture]
public sealed class VehicleFuelCanWireTests
{
    static NetPakReader Reader(byte[] bytes){var r=new NetPakReader();r.SetBufferSegment(bytes,bytes.Length);return r;}
    static byte[] Pack(Action<NetPakWriter> write)=>NetMessagePak.Pack(1,write)[1..];
    [Test]
    public void TheHeldCanIntentRoundTripsAndRefusesTruncationAndZeroIdentities()
    {
        var c=new VehicleFuelCanCommand{VehicleNetId=42,Address=new(2,3,1,28),Token=0xFEEDFACECAFEUL};
        var bytes=Pack(c.Write);
        Assert.That(VehicleFuelCanCommand.TryRead(Reader(bytes),out var d),Is.True);
        Assert.That((d.VehicleNetId,d.Address.Page,d.Address.X,d.Address.Y,d.Address.Id,d.Token),Is.EqualTo((42u,(byte)2,(byte)3,(byte)1,(ushort)28,0xFEEDFACECAFEUL)));
        for(int n=0;n<bytes.Length;n++){Assert.That(VehicleFuelCanCommand.TryRead(Reader(bytes[..n]),out d),Is.False);Assert.That(d.VehicleNetId,Is.Zero);}
        foreach(var bad in new[]{c with{VehicleNetId=0},c with{Token=0},c with{Address=new(2,3,1,0)}})
            Assert.That(VehicleFuelCanCommand.TryRead(Reader(Pack(bad.Write)),out _),Is.False);
        Assert.That((ReplicationIds.CommandPourVehicleFuel,ReplicationIds.CommandSiphonVehicleFuel),Is.EqualTo(((byte)99,(byte)100)));
        Assert.That(NetProtocol.Version,Is.GreaterThanOrEqualTo(82));
    }
}
