using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;
using NVector=System.Numerics.Vector3;
using Quaternion=System.Numerics.Quaternion;

namespace UnturnedBevyTests;

public sealed partial class NpcHeliServicesTests
{
    uint CrewAircraft(float[] rotation=null)
    {
        uint id=Call(_host,"worldVehicles",new{vehicles=new[]{new{typeId=21,position=new[]{20,5,-40},eulerDegrees=rotation??new[]{20f,35f,10f},seatCount=4,health=12000,speedMax=30,climbMax=20,fallMax=40}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        Call(_host,"worldNpcHelis",new{vehicles=new[]{new{netId=id,target=new[]{500,50,0},targetName="crew fixture"}}});Pump();return id;
    }
    (Vector3 Point,Vector3 From) CrewAim(uint id,int index,float height)
    {
        Server.Vehicles.TryGet(id,out var v);var def=Field<Catalog>("_catalog").Mounted.For(v.TypeId).Mounts[index];
        var q=Quaternion.CreateFromYawPitchRoll(v.YawDegrees*MathF.PI/180,v.PitchDegrees*MathF.PI/180,v.RollDegrees*MathF.PI/180);
        NVector local=new(def.GunnerAt[0],def.GunnerAt[1]+height,def.GunnerAt[2]);
        Vector3 World(NVector p){var w=NVector.Transform(p,q);return new(w.X+v.Pos.x,w.Y+v.Pos.y,w.Z+v.Pos.z);}
        return(World(local),World(local+NVector.UnitZ*4));
    }
    void CrewRound(uint id,int index,float height,ushort seq,float damage=40)
    {
        var ray=CrewAim(id,index,height);var client=(NetWorldClient)typeof(BridgeHost).GetField("_client",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_peer);
        var profile=new ServerGunProfile{AssetName="eaglefire",PlayerDamage=damage,AnimalDamage=7,VehicleDamage=1,MuzzleVelocity=100,GravityMultiplier=0,BallisticSteps=10};
        Assert.That(Server.Combat.ServerLaunchMounted(client.PlayerId,seq,ray.From,(ray.Point-ray.From).normalized,profile),Is.True);Pump(12);
    }
    JsonElement Crew(int index)=>Aircraft.GetProperty("crews").EnumerateArray().Single(c=>c.GetProperty("index").GetInt32()==index);
    [Test]
    public void ActualUdpCrewHeightDamageUsesHullRotatedBoxAndRetiresOnlyAcceptedCrew()
    {
        uint id=CrewAircraft();Server.Vehicles.TryGet(id,out var hull);float hullHp=hull.Health;
        var target=CrewAim(id,0,1.6f);var service=Field<NpcHeliServices>("_npcHelis");CombatWorldHit contact=default;
        Assert.That(service.RayCrew(0,target.From,target.Point,false,ref contact),Is.True);Assert.That(contact.VehicleNetId,Is.EqualTo(id));Assert.That(contact.CrewSlot,Is.EqualTo(1));
        Assert.That(service.RayCrew(id,target.From,target.Point,false,ref contact),Is.False,"A mounted round excludes its own aircraft's crew as well as its hull");
        CrewRound(id,0,1.6f,1);Assert.That(Crew(0).GetProperty("health").GetSingle(),Is.EqualTo(20).Within(.001));
        Assert.That(hull.Health,Is.EqualTo(hullHp));Assert.That(Aircraft.GetProperty("mode").GetString(),Is.EqualTo("patrol"),"A source dummy hit is not a vehicle NoteAttackedFrom call");
        var hits=State(_peer).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="hit").ToArray();
        Assert.That(hits.Length,Is.EqualTo(1));var hit=hits[0].GetProperty("data");
        Assert.That(hit.GetProperty("targetKind").GetByte(),Is.EqualTo((byte)HitTargetKind.Humanoid));Assert.That(hit.GetProperty("targetId").GetUInt32(),Is.EqualTo(id));
        Assert.That(hit.GetProperty("headshot").GetBoolean(),Is.True);Assert.That(hit.GetProperty("damage").GetSingle(),Is.EqualTo(80).Within(.1));
        CrewRound(id,0,.4f,2);Assert.That(Crew(0).GetProperty("alive").GetBoolean(),Is.False);
        var replica=State(_peer).GetProperty("vehicleMounts").EnumerateArray().Single(v=>v.GetProperty("vehicleNetId").GetUInt32()==id);
        Assert.That(replica.GetProperty("mounts")[0].GetProperty("crewAlive").GetBoolean(),Is.False);
        Assert.That(replica.GetProperty("mounts")[1].GetProperty("crewAlive").GetBoolean(),Is.True);
        contact=default;Assert.That(service.RayCrew(0,target.From,target.Point,false,ref contact),Is.False,"Dead crew no longer consumes bullets");
        CrewRound(id,1,1.6f,3);CrewRound(id,1,1.6f,4);
        Assert.That(Aircraft.GetProperty("mode").GetString(),Is.EqualTo("patrol"),"Source checks dead crew only after a hull hit has engaged the controller");
        Server.Combat.DamageBody(new(new(hull.Pos,vehicleNetId:id),1,1,false,Vector3.forward,Server.Session.CurrentTick,new Vector3(100,0,100)));Pump(1);
        Assert.That(Aircraft.GetProperty("mode").GetString(),Is.EqualTo("flee"));
        Pump(160);Assert.That(Crew(0).GetProperty("alive").GetBoolean(),Is.False,"NeverRespawn remains in force after the dummy's ordinary3second interval");
    }
    [Test]
    public void NearerWorldStopsCrewDamageAndSavedPartialHealthSurvivesWreckAndLoad()
    {
        uint id=CrewAircraft([0,0,0]);var aim=CrewAim(id,0,1);float z=(aim.From.z+aim.Point.z)*.5f;
        Call(_host,"worldGeometry",new{meshes=new[]{new{vertices=new[]{new[]{aim.Point.x-2,aim.Point.y-2,z},new[]{aim.Point.x+2,aim.Point.y-2,z},new[]{aim.Point.x,aim.Point.y+2,z}}}}});
        CrewRound(id,0,1,1);Assert.That(Crew(0).GetProperty("health").GetSingle(),Is.EqualTo(100));
        Call(_host,"worldGeometry",new{meshes=Array.Empty<object>()});CrewRound(id,0,1,2);Assert.That(Crew(0).GetProperty("health").GetSingle(),Is.EqualTo(60));
        string path=Path.Combine(TestContext.CurrentContext.WorkDirectory,"crew-save-"+Guid.NewGuid()+".json");
        try
        {
            Call(_host,"save",new{path});Call(_host,"load",new{path});id=Id;Assert.That(Crew(0).GetProperty("health").GetSingle(),Is.EqualTo(60));
            Server.Vehicles.TryGet(id,out var v);Server.Vehicles.ServerPublish(new(id),v.Pos,new(0,v.YawDegrees,0),v.LinVel,v.AngVel,0,v.Fuel,0,v.Battery,VehicleReplication.FlagExploded,Server.Session.CurrentTick);
            aim=CrewAim(id,0,1);CombatWorldHit hit=default;
            Assert.That(Field<NpcHeliServices>("_npcHelis").RayCrew(0,aim.From,aim.Point,false,ref hit),Is.True,"Source Explode chars live crew; it does not kill or remove their TargetDummy");
        }
        finally{File.Delete(path);}
    }
    [Test]
    public void ExplicitControllerReplacementRemovesAcceptedCrewAndTheirBoxesButKeepsAirframe()
    {
        uint id=CrewAircraft();var aim=CrewAim(id,0,1);CombatWorldHit hit=default;var service=Field<NpcHeliServices>("_npcHelis");
        Assert.That(service.RayCrew(0,aim.From,aim.Point,false,ref hit),Is.True);
        Call(_host,"worldNpcHelis",new{replace=true});Pump(12);
        Assert.That(State(_host).GetProperty("npcHelis").GetArrayLength(),Is.Zero);Assert.That(Server.Vehicles.TryGet(id,out _),Is.True);
        hit=default;Assert.That(service.RayCrew(0,aim.From,aim.Point,false,ref hit),Is.False);
        var replica=State(_peer).GetProperty("vehicleMounts").EnumerateArray().Single(v=>v.GetProperty("vehicleNetId").GetUInt32()==id);
        Assert.That(replica.GetProperty("mounts").EnumerateArray().All(m=>!m.GetProperty("crewAlive").GetBoolean()),Is.True,
            "Remote presentation cannot retain an accepted live crew after its authority box was retired");
    }
    [Test]
    public void HandheldProfileUsesOriginalEffectiveShellDamageForHumanoids()
    {
        var client=Field<NetWorldClient>("_client");Server.Inventories.TryGet(client.PlayerId,out var bag);
        var page=bag.Inventory.items[0];while(page.getItemCount()>0)page.removeItem(0);
        page.addItem(0,0,0,new SDG.Unturned.Item(112){gunAmmo=6,gunMagId=113});
        var state=new WeaponState(Server,Field<Catalog>("_catalog"),client.PlayerId,(_,_)=>{});state.Bind();
        Assert.That(state.Select(0,0,0),Is.EqualTo(112));float baseDamage=Field<Catalog>("_catalog").GunDefs[112].Damage;
        Assert.That(state.Profile.HumanoidDamage,Is.EqualTo(baseDamage));
        state.RestoreShells(new(){{8,5000}});Assert.That(state.Profile.HumanoidDamage,Is.EqualTo(SDG.Unturned.Assets.find(5000).damageOverride));
        Assert.That(state.Profile.HumanoidDamage,Is.EqualTo(40));Assert.That(state.Profile.Pellets,Is.EqualTo(1));
        state.RestoreShells(new(){{8,5002}});Assert.That(state.Profile.HumanoidDamage,Is.EqualTo(20));
    }
    [Test]
    public void CrewMetadataUsesActualBindPoseAndOriginalTargetDummyGeometry()
    {
        var data=Call(_host,"catalog").GetProperty("npcCrew");
        Assert.That(data.GetProperty("rig").GetString(),Is.EqualTo("rig.json"));Assert.That(data.GetProperty("face").GetInt32(),Is.EqualTo(19));
        Assert.That(data.GetProperty("clip").ValueKind,Is.EqualTo(JsonValueKind.Null));
        Assert.That(data.GetProperty("bodySize").Deserialize<float[]>(),Is.EqualTo(new[]{.84f,1.8f,.5f}));
        Assert.That(data.GetProperty("gunRotationDegrees").Deserialize<float[]>(),Is.EqualTo(new[]{0f,0f,90f}));
        Assert.That(data.GetProperty("tint").Deserialize<float[]>(),Is.EqualTo(new[]{.82f,.66f,.52f}));
    }
}
