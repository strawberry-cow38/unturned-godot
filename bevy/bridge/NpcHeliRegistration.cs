using System.Text.Json;
using Godot;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Read-only view of the original exported Spec, used by extracted setup expressions.</summary>
public sealed class NpcHeliSpec(JsonElement data)
{
    public float Health=>Float(data,"Health");public float MainRotorHp=>Float(data,"MainRotorHp");public float TailRotorHp=>Float(data,"TailRotorHp");
    public float RotorRadius=>Float(data,"RotorRadius");public float TailRotorRadius=>Float(data,"TailRotorRadius");
    public Vector3 MainHubBox=>V("MainHubBox");public Vector3 TailHubBox=>V("TailHubBox");
    Vector3 V(string key){var v=Vec(data,key);return new(v.x,v.y,v.z);}
}
public static class NpcHeliRegistration
{
    static float[] V(Vector3 v)=>[v.X,v.Y,v.Z];
    public static JsonElement For(MountedWeaponCatalog.Fleet fleet,UnityEngine.Vector3 at,float yaw)
    {
        var row=fleet.Source;var s=row.GetProperty("spec");var spec=new NpcHeliSpec(s);
        if(!Bool(s,"Heli"))throw new ArgumentException("NPC airframe requires original helicopter Spec");
        // Source helicopter fleet has no trunk/hood storage. Refuse an unknown
        // future configuration until its original compartments are adapted.
        if(row.GetProperty("access").EnumerateArray().Any(a=>Text(a,"Kind") is "Hood" or "Trunk"))
            throw new NotSupportedException("NPC helicopter storage requires original compartment mapping");
        var center=Vec(s,"BoxCenter");var size=Vec(s,"BoxSize");bool rear=Bool(s,"RearEngine");
        return JsonSerializer.SerializeToElement(new{key="npc:"+Guid.NewGuid().ToString("N"),typeId=fleet.TypeId,variant=0,isHeli=true,isPlane=false,
            position=Array(at),yaw,health=spec.Health*NpcHeliRules.VehicleHealthScale,healthMax=spec.Health*NpcHeliRules.VehicleHealthScale,
            fuel=Float(s,"Fuel"),battery=NpcHeliRules.BatteryMax,speedMax=Float(s,"SpeedMax"),climbMax=Float(s,"HeliClimbMax"),fallMax=Float(s,"HeliFallMax"),
            seatOffsets=row.GetProperty("seats"),hasDoors=true,
            hitRegions=new[]{new{bodyPart="mainRotor",center=Array(Vec(s,"RotorHub")),size=V(NpcHeliRules.MainHubBox(spec)),health=NpcHeliRules.MainRotorHealth(spec)},
                new{bodyPart="tailRotor",center=Array(Vec(s,"TailRotorHub")),size=V(NpcHeliRules.TailHubBox(spec)),health=NpcHeliRules.TailRotorHealth(spec)}},
            engineRegion=new{rear,threshold=center.z+(rear?1:-1)*.15f*size.z,health=NpcHeliRules.EngineHealth(spec)}});
    }
}
