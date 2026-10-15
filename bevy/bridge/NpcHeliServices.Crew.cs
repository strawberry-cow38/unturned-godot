using System.Numerics;
using UnturnedGodot.Net;
using NVector=System.Numerics.Vector3;
using Vector3=UnityEngine.Vector3;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

public sealed partial class NpcHeliServices
{
    readonly Dictionary<(uint,int),NpcHeliCrewBody> _crew=new();
    public sealed record CrewSaved(int Ordinal,byte TypeId,int Index,float Health);
    void AttachCrew(uint id,bool fresh)
    {
        _server.Vehicles.TryGet(id,out var vehicle);var fleet=_catalog.Mounted.For(vehicle.TypeId);
        for(int i=0;i<fleet.Mounts.Length;i++)
        {
            var def=fleet.Mounts[i];var feet=MountedWeaponCatalog.V(def.GunnerAt);if(feet==NVector.Zero)continue;
            int index=i;var body=new NpcHeliCrewBody{Vehicle=id,Index=index,Seat=def.Seat,Gun=def.GunId,LocalFeet=new(feet.X,feet.Y,feet.Z)};
            if(!fresh&&!_mounts.NpcCrewAlive(id,def.Seat))body.Health=0;
            body.ToLocal=p=>{
                if(!_server.Vehicles.TryGet(id,out var v))return default;
                var local=NVector.Transform(new(p.X-v.Pos.x,p.Y-v.Pos.y,p.Z-v.Pos.z),Quaternion.Inverse(Rotation(v)))-feet;
                return new(local.X,local.Y,local.Z);
            };
            body.OnDown=()=>_mounts.SetNpcCrewAlive(id,index,false);_crew[(id,index)]=body;
        }
    }
    static Quaternion Rotation(VehicleReplication.VehicleEntity v)=>Quaternion.CreateFromYawPitchRoll(v.YawDegrees*MathF.PI/180,v.PitchDegrees*MathF.PI/180,v.RollDegrees*MathF.PI/180);
    static NVector N(Vector3 v)=>new(v.x,v.y,v.z);
    static Vector3 U(NVector v)=>new(v.X,v.Y,v.Z);
    public bool RayCrew(uint excludedVehicle,Vector3 from,Vector3 to,bool found,ref CombatWorldHit hit)
    {
        float nearest=found?(hit.Point-from).sqrMagnitude:float.PositiveInfinity;
        foreach(var body in _crew.Values)
        {
            if(body.Down||body.Vehicle==excludedVehicle||!_server.Vehicles.TryGet(body.Vehicle,out var v))continue;
            var rotation=Rotation(v);var inverse=Quaternion.Inverse(rotation);
            var a=U(NVector.Transform(N(from-v.Pos),inverse));var b=U(NVector.Transform(N(to-v.Pos),inverse));
            var center=U(body.LocalFeet+NpcCrewRules.BodyCenter);
            if(!WorldGeometry.SegmentBox(center,U(NpcCrewRules.BodySize),a,b,out var point,out var normal))continue;
            var world=U(NVector.Transform(N(point),rotation))+v.Pos;float d=(world-from).sqrMagnitude;if(d>=nearest)continue;
            nearest=d;found=true;hit=new(world,vehicleNetId:body.Vehicle,crewSlot:checked((byte)(body.Index+1)),normal:U(NVector.Transform(N(normal),rotation)));
        }
        return found;
    }
    CombatHumanoidResult? DamageCrew(CombatBodyHit hit)
    {
        if(hit.Melee||hit.Hit.CrewSlot==0||!float.IsFinite(hit.Damage)||hit.Damage<0
            ||!_crew.TryGetValue((hit.Hit.VehicleNetId,hit.Hit.CrewSlot-1),out var body)||body.Down)return null;
        float amount=body.TakeHit(hit.Damage,G(hit.Hit.Point));
        return new(amount,body.Down,body.LastZone==NpcHeliCrewBody.HitZone.Head);
    }
    object[] CrewSnapshots(uint id)=>_crew.Values.Where(c=>c.Vehicle==id).Select(c=>(object)new{index=c.Index,seat=c.Seat,gun=c.Gun,
        localPosition=Array(U(c.LocalFeet)),health=c.Health,alive=!c.Down}).ToArray();
    CrewSaved[] CaptureCrew()
    {
        var fleet=_server.Vehicles.All.ToArray();return fleet.SelectMany((v,ordinal)=>_crew.Values.Where(c=>c.Vehicle==v.NetIdValue)
            .Select(c=>new CrewSaved(ordinal,v.TypeId,c.Index,c.Health))).ToArray();
    }
    void RestoreCrew(CrewSaved[] saved)
    {
        var fleet=_server.Vehicles.All.ToArray();float maximum=new SDG.Unturned.PlayerVitalsSim().MaxHealth;
        foreach(var s in saved??[])
            if(s.Ordinal>=0&&s.Ordinal<fleet.Length&&fleet[s.Ordinal].TypeId==s.TypeId&&float.IsFinite(s.Health)&&s.Health>=0&&s.Health<=maximum
                &&_crew.TryGetValue((fleet[s.Ordinal].NetIdValue,s.Index),out var c))
            {c.Health=s.Health;_mounts.SetNpcCrewAlive(c.Vehicle,c.Index,!c.Down);}
    }
}
