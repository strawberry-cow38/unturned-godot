using NUnit.Framework;
using System.Text.Json;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

public sealed partial class NpcHeliServicesTests
{
    static JsonElement VehicleRow(JsonElement state,uint id)=>state.GetProperty("vehicles").EnumerateArray().Single(v=>v.GetProperty("netId").GetUInt32()==id);
    [Test]
    public void LateConsoleSpawnResolvesSourceSeatsOnBothEndsAndRemoteOccupantBoardsTheLastSeat()
    {
        Pump(30);Spawn();uint id=Id;
        Assert.That(Server.Vehicles.TryGet(id,out var authority),Is.True);Assert.That(authority.SeatCount,Is.EqualTo(6));
        Assert.That(VehicleRow(State(_host),id).GetProperty("seatCount").GetInt32(),Is.EqualTo(6));
        var remote=VehicleRow(State(_peer),id);Assert.That(remote.GetProperty("seatCount").GetInt32(),Is.EqualTo(6));
        Assert.That(remote.GetProperty("passengers").GetArrayLength(),Is.Zero,"An unoccupied source passenger array is sparse, not a seat-count field");
        Action(_peer,"console",new{text=$"teleport {authority.Pos.x} {authority.Pos.y} {authority.Pos.z}"});Pump(20);
        Action(_peer,"enterVehicle",new{netId=id,seat=5});Pump(20);
        Assert.That(State(_peer).GetProperty("player").GetProperty("vehicleNetId").GetUInt32(),Is.EqualTo(id));
        Assert.That(State(_peer).GetProperty("player").GetProperty("vehicleSeat").GetInt32(),Is.EqualTo(5));
        remote=VehicleRow(State(_peer),id);Assert.That(remote.GetProperty("seatCount").GetInt32(),Is.EqualTo(6));
        Assert.That(remote.GetProperty("passengers")[4].GetInt32(),Is.EqualTo(State(_peer).GetProperty("playerId").GetInt32()));
    }
    [Test]
    public void UnknownRemoteTypeReportsNoInventedCountWhileHostUsesAcceptedRegistration()
    {
        uint id=Call(_host,"worldVehicles",new{vehicles=new[]{new{typeId=250,position=new[]{0,0,0},seatCount=3,health=100}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();Pump(20);
        Assert.That(VehicleRow(State(_host),id).GetProperty("seatCount").GetInt32(),Is.EqualTo(3));
        Assert.That(VehicleRow(State(_peer),id).GetProperty("seatCount").ValueKind,Is.EqualTo(JsonValueKind.Null));
    }
}
