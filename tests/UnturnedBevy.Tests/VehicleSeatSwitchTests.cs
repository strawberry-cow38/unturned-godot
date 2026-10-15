using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.NetTransport.Udp;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class VehicleSeatSwitchTests
{
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true,Converters={new VectorJson(),new ByteArrayJson()}};
    static string Root(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement Call(BridgeHost h,string op,object args)=>JsonSerializer.SerializeToElement(h.Handle(op,JsonSerializer.SerializeToElement(args)),Options);
    static JsonElement State(BridgeHost h)=>JsonSerializer.SerializeToElement(h.State(),Options);
    static void Enter(BridgeHost h,uint id,int seat)=>Call(h,"action",new{name="enterVehicle",args=new{netId=id,seat}});
    static void Pump(BridgeHost h,NetWorldClient peer,int count=20){for(int i=0;i<count;i++){peer.Tick();Call(h,"tick",new{});Thread.Sleep(1);}}

    [Test]
    public void JsonHostAndUdpPeerSwitchThroughAcceptedSeatsAndCannotStealDriver()
    {
        using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)socket.LocalEndPoint).Port;socket.Close();
        using var host=new BridgeHost();Call(host,"init",new{contentRoot=Root(),spawn=new[]{0,0,0},listenPort=port});
        var reply=Call(host,"worldVehicles",new{vehicles=new[]{new{typeId=0,key="seat switch jeep",position=new[]{0,0,0},seatCount=4,health=600,fuel=1000,speedMax=30}}});
        uint id=reply.GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        var peer=new NetWorldClient(new UdpClientTransport("127.0.0.1",(ushort)port),"seat switch peer",contentHash:NetContent.Hash);
        peer.Connect();Pump(host,peer,40);Assert.That(peer.State,Is.EqualTo(NetSessionState.Connected));
        try {
            Enter(host,id,0);Pump(host,peer);
            Assert.That(State(host).GetProperty("player").GetProperty("vehicleSeat").GetInt32(),Is.Zero);
            Enter(host,id,1);Pump(host,peer);
            Assert.That(State(host).GetProperty("player").GetProperty("vehicleSeat").GetInt32(),Is.EqualTo(1));
            Assert.That(peer.Vehicles.SeatOccupant(new(id),0),Is.Zero);
            Assert.That(peer.Vehicles.SeatOccupant(new(id),1),Is.EqualTo(1));
            peer.SendEnterVehicle(id,0);Pump(host,peer);
            Assert.That(peer.Vehicles.SeatOccupant(new(id),0),Is.EqualTo(peer.PlayerId));
            var server=(NetWorldServer)typeof(BridgeHost).GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(host);
            long rejected=server.Commands.Diag.ValidationRejected;
            Enter(host,id,0);Pump(host,peer);
            Assert.That(server.Commands.Diag.ValidationRejected,Is.GreaterThan(rejected));
            Assert.That(peer.Vehicles.SeatOccupant(new(id),0),Is.EqualTo(peer.PlayerId));
            Assert.That(peer.Vehicles.SeatOccupant(new(id),1),Is.EqualTo(1));
            peer.SendEnterVehicle(id,2);Pump(host,peer);
            Assert.That(server.VehicleHost.IsDriver(peer.PlayerId),Is.False);
            Assert.That(peer.Vehicles.SeatOccupant(new(id),0),Is.Zero);
            Assert.That(peer.Vehicles.SeatOccupant(new(id),2),Is.EqualTo(peer.PlayerId));
            Enter(host,id,0);Pump(host,peer);
            Assert.That(server.VehicleHost.IsDriver(1),Is.True);
            Assert.That(peer.Vehicles.SeatOccupant(new(id),0),Is.EqualTo(1));
            Assert.That(peer.Vehicles.SeatOccupant(new(id),1),Is.Zero);
            Assert.That(peer.Vehicles.SeatOccupant(new(id),2),Is.EqualTo(peer.PlayerId));
        } finally {peer.Disconnect();Pump(host,peer,5);}
    }
}
