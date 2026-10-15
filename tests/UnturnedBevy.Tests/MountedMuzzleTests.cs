using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;
using NVector=System.Numerics.Vector3;
using NQuaternion=System.Numerics.Quaternion;

namespace UnturnedBevyTests;

public sealed partial class MountedFlightTests
{
    static JsonElement BarrelFixture()
    {
        using var stream=typeof(MountedFlightTests).Assembly.GetManifestResourceStream("UnturnedBevy.Tests.fixtures.tank-moving-barrel.json");
        Assert.That(stream,Is.Not.Null);
        return JsonDocument.Parse(stream!).RootElement.Clone();
    }
    static float[] Row(Vector3 p)=>new[]{p.x,p.y,p.z};
    static float[][] FaceAt(JsonElement fixture,Vector3 origin,Vector3 direction)
    {
        // Rigidly align a captured production triangle to this accepted shot.
        // The recorded 5mm body displacement is explicit fixture input, not an
        // assertion that the second capture reproduced the original frame222.
        var shot=fixture.GetProperty("shot");var from=Vec(shot.GetProperty("direction")).normalized;var old=Vec(shot.GetProperty("muzzle"));
        var axis=Vector3.Cross(from,direction);float dot=Math.Clamp(Vector3.Dot(from,direction),-1,1);
        var rotation=axis.sqrMagnitude<1e-12f?NQuaternion.Identity:NQuaternion.CreateFromAxisAngle(new NVector(axis.x,axis.y,axis.z)/axis.magnitude,MathF.Acos(dot));
        return fixture.GetProperty("vertices").EnumerateArray().Select(v=>{
            var delta=Vec(v)-old;var transformed=NVector.Transform(new(delta.x,delta.y,delta.z),rotation);
            return Row(origin+new Vector3(transformed.X,transformed.Y,transformed.Z));
        }).ToArray();
    }
    static float[][] Plane(Vector3 center,Vector3 direction,float size=3)
    {
        var right=Vector3.Cross(direction,new(0,1,0)).normalized*size;var up=Vector3.Cross(right.normalized,direction).normalized*size;
        return new[]{Row(center-right-up),Row(center+right-up),Row(center+up*2)};
    }
    void Mesh(string key,float[][] vertices,uint vehicle=0)=>Call(_host,"worldDynamicGeometry",new{key,meshes=new[]{new{vertices,vehicleNetId=vehicle}}});

    [TestCase(false),TestCase(true)]
    public void CapturedTiltedTankBarrelCannotDetonateOwnAcceptedRoundButOtherVehiclesStillBlock(bool installFilter)
    {
        var fixture=BarrelFixture();var vehicle=fixture.GetProperty("vehicle");var pos=Vec(vehicle.GetProperty("position"));
        uint id=Call(_host,"worldVehicles",new{vehicles=new[]{new{typeId=27,key="captured-tank",position=Row(pos),
            eulerDegrees=vehicle.GetProperty("eulerDegrees").EnumerateArray().Select(v=>v.GetSingle()).ToArray(),seatCount=4,
            health=16000,fuel=2000,speedMax=40,climbMax=20,fallMax=100}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        Action(_host,"console",new{text=$"teleport {pos.x} {pos.y} {pos.z}"});Pump();Enter(id);
        Assert.That(Server.Vehicles.TryGet(id,out var tank)&&tank.DriverPlayerId==1,Is.True);
        var requested=Vec(fixture.GetProperty("shot").GetProperty("muzzle"))+Vec(fixture.GetProperty("shot").GetProperty("direction"))*600;
        for(int n=0;n<12;n++){Input(id,target:Row(requested),trigger:false);Pump(1);}
        if(!installFilter)Server.Combat.MountedRayDetailed=null; // exact old adapter negative control
        Input(id,target:Row(requested));
        for(int n=0;n<8&&Server.Combat.LiveBullets==0;n++)Pump(1);
        Assert.That(Server.Combat.LiveBullets,Is.EqualTo(1));
        var local=(NetWorldClient)typeof(BridgeHost).GetField("_client",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_host);
        for(int n=0;n<8&&Events(_host,"mountShot").Length==0;n++){local.PollNetwork();Thread.Sleep(1);}
        Input(id,target:Row(requested),trigger:false);
        var shot=Events(_host,"mountShot").Single().GetProperty("data");var origin=Vec(shot.GetProperty("muzzle"));var direction=Vec(shot.GetProperty("direction")).normalized;
        Mesh("vehicle:"+id,FaceAt(fixture,origin,direction),id);
        var ray=Call(_host,"worldRay",new{from=Row(origin),to=Row(origin+direction*5)});
        Assert.That(ray.GetProperty("hit").GetBoolean(),Is.True,"Captured actual cap overlaps the next segment after only5mm body motion");
        Assert.That(ray.GetProperty("vehicleNetId").GetUInt32(),Is.EqualTo(id));
        Assert.That((Vec(ray.GetProperty("point"))-origin).magnitude,Is.LessThan(.02));
        uint other=Call(_host,"worldVehicles",new{replace=false,vehicles=new[]{new{typeId=27,key="target",position=Row(origin+direction*80),seatCount=4,health=16000,fuel=2000}}})
            .GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        Mesh("vehicle:"+other,Plane(origin+direction*80,direction),other);
        Server.CombatState.TryGet(1,out var life);float health=life.HealthExact;
        Pump(1);
        if(!installFilter)
        {
            Assert.That(Events(_host,"bulletExplosion").Length,Is.EqualTo(1));
            Assert.That((Vec(Events(_host,"bulletExplosion")[0].GetProperty("data").GetProperty("position"))-origin).magnitude,Is.LessThan(.03));
            Assert.That(life.HealthExact,Is.LessThan(health),"Old world query detonates the source warhead beside its actual occupied tank");
            Assert.That(Flights(_host),Is.Empty);return;
        }
        Assert.That(Events(_host,"bulletExplosion"),Is.Empty);Assert.That(Flights(_host).Length,Is.EqualTo(1),"Cosmetic query must also exclude the firing vehicle");
        Assert.That(Server.Combat.LiveBullets,Is.EqualTo(1));Assert.That(life.HealthExact,Is.EqualTo(health));
        Action(_host,"exitVehicle",new{});Pump(2);
        Assert.That(Server.Vehicles.TryGet(id,out tank)&&tank.DriverPlayerId==0,Is.True);
        // Re-entering the vacated firing hull later in flight must still skip
        // that hull; recomputing exclusion from the shooter's seat would fail.
        Mesh("vehicle:"+id,Plane(origin+direction*30,direction),id);
        Pump(35);
        Assert.That(life.HealthExact,Is.EqualTo(health));Assert.That(tank.Health,Is.EqualTo(16000));
        Assert.That(Server.Vehicles.TryGet(other,out var target)&&target.Health<16000,Is.True,"Another vehicle remains damageable");
        Assert.That(Events(_host,"bulletExplosion").Length,Is.EqualTo(1));Assert.That(Events(_remote,"bulletExplosion").Length,Is.EqualTo(1));
        var impact=Vec(Events(_remote,"bulletExplosion")[0].GetProperty("data").GetProperty("position"));
        Assert.That((impact-(origin+direction*80)).magnitude,Is.LessThan(.02));
        Assert.That(Flights(_host),Is.Empty);Assert.That(Flights(_remote),Is.Empty);Assert.That(Server.Combat.LiveBullets,Is.Zero);
    }

    [Test]
    public void MountedIdentityLookupPreservesBedDamageBehindSkippedBarrelAndWorldStillBlocks()
    {
        uint id=Vehicle();var from=new Vector3(0,1,0);var to=new Vector3(0,1,-5);
        Mesh("vehicle:"+id,Plane(new(0,1,-.005f),new(0,0,-1)),id);
        Call(_host,"worldServices",new{beds=new[]{new{netId=42,position=new[]{0,1,-2},geometryKey="bed-behind-barrel"}}});
        Mesh("bed-behind-barrel",Plane(new(0,1,-2),new(0,0,-1)));
        Assert.That(Server.Combat.DamageBarricadeAlong(from,to,200,0),Is.False,"Ordinary query correctly stops at the hull");
        Assert.That(Server.Combat.MountedRayDetailed(id,from,to,out var hit),Is.True);Assert.That(hit.VehicleNetId,Is.Zero);Assert.That(hit.Point.z,Is.EqualTo(-2).Within(.001));
        Mesh("solid",Plane(new(0,1,-1),new(0,0,-1)));
        Assert.That(Server.Combat.DamageMountedBarricadeAlong(id,from,to,200,0),Is.False,"A different nearer world body still protects the bed");
        Call(_host,"worldDynamicGeometry",new{key="solid",meshes=Array.Empty<object>()});
        Assert.That(Server.Combat.DamageMountedBarricadeAlong(id,from,to,200,0),Is.True);
        Assert.That(Server.Interactables.BedCount,Is.Zero);
    }
}
