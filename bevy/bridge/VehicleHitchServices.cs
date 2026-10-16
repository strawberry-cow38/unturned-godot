using System.Text.Json;
using UnityEngine;
using UnturnedGodot.Net;
using static UnturnedBevy.BridgeHost;
using Q=System.Numerics.Quaternion;
using V=System.Numerics.Vector3;
namespace UnturnedBevy;

/// <summary>Original Vehicle.CoupleTo/Uncouple/UpdateCoupled authority. Rapier
/// runs the actual two bodies and point joint; no second physical trailer lives
/// here. Only an accepted cab relationship retracts the native landing gear.</summary>
public sealed class VehicleHitchServices
{
    public const float CoupleReach=1.6f, ApproachReach=6f, HitchReach=3.5f, RollDisconnectDeg=50f;
    public sealed record Saved(int CabOrdinal,byte CabType,int TrailerOrdinal,byte TrailerType,float RipTimer);
    readonly NetWorldServer _server;
    readonly MountedWeaponCatalog _catalog;
    readonly WorldGeometry _geometry;
    readonly Dictionary<uint,float> _rip=new();
    public VehicleHitchServices(NetWorldServer server,Catalog catalog,WorldGeometry geometry)
    {
        _server=server;_catalog=catalog.Mounted;_geometry=geometry;
        server.VehicleHitchAllowed=Allowed;server.VehicleHitchRequested=Toggle;
        server.VehicleHost.CoupledPoseAllowed=PairAllowed;
    }
    JsonElement? Spec(byte type)=>_catalog.For(type)?.Source.GetProperty("spec");
    Vector3 Anchor(byte type,string name)=>Spec(type) is {} spec?Vec(spec,name):Vector3.zero;
    static V N(Vector3 p)=>new(p.x,p.y,p.z);
    static Vector3 U(V p)=>new(p.X,p.Y,p.Z);
    static Q Rotation(float yaw,float pitch,float roll)=>Q.CreateFromYawPitchRoll(yaw*Mathf.Deg2Rad,pitch*Mathf.Deg2Rad,roll*Mathf.Deg2Rad);
    static Q Rotation(VehicleReplication.VehicleEntity e)=>Rotation(e.YawDegrees,e.PitchDegrees,e.RollDegrees);
    static Vector3 Point(Vector3 local,Vector3 pos,Q rotation)=>pos+U(V.Transform(N(local),rotation));
    Vector3 Pin(VehicleReplication.VehicleEntity e,bool cab)=>Point(Anchor(e.TypeId,cab?"FifthWheel":"Kingpin"),e.Pos,Rotation(e));
    bool PairAllowed(VehicleReplication.VehicleEntity cab,VehicleReplication.VehicleEntity trailer,VehicleStateCommand command)
    {
        if(Anchor(cab.TypeId,"FifthWheel")==Vector3.zero||Anchor(trailer.TypeId,"Kingpin")==Vector3.zero||command.Trailer is not {} p)return false;
        var a=Point(Anchor(cab.TypeId,"FifthWheel"),command.Pos,Rotation(command.YawDegrees,command.PitchDegrees,command.RollDegrees));
        var b=Point(Anchor(trailer.TypeId,"Kingpin"),p.Pos,Rotation(p.YawDegrees,p.PitchDegrees,p.RollDegrees));
        return (a-b).sqrMagnitude<=CoupleReach*CoupleReach;
    }
    bool Allowed(ushort owner,VehicleHitchCommand command)
    {
        if(_server.VehicleHost.IsSeated(owner)||!_server.Players.TryGetByOwner(owner,out var player)
            ||!_server.Vehicles.TryGet(command.TrailerNetId,out var trailer)||trailer.Exploded
            ||Anchor(trailer.TypeId,"Kingpin")==Vector3.zero||(player.Pos-Pin(trailer,false)).sqrMagnitude>HitchReach*HitchReach)return false;
        float aim=_server.Combat.DefaultGun.MaxAimOriginOffset;
        if((command.Origin-player.Pos).sqrMagnitude>aim*aim)return false;
        var end=command.Origin+command.Direction*2.6f;
        if(_geometry.RayDetailed(command.Origin,end,out var hit))
        {
            if(hit.VehicleNetId==0)return false; // a real world face owns focus first
            end=hit.Point;
        }
        var candidates=_geometry.VehiclesAt(end,.16f);
        uint best=0;float distance=float.MaxValue;
        foreach(var e in _server.Vehicles.All)
            if(candidates.Contains(e.NetIdValue)) {float d=(e.Pos-end).sqrMagnitude;if(d<distance){distance=d;best=e.NetIdValue;}}
        if(best!=0)return best==command.TrailerNetId;
        // Original LookRayHitsHull fallback only after no collider won; closest
        // root among direct authored boxes, never an enclosing mesh AABB.
        distance=8.6f*8.6f;
        foreach(var e in _server.Vehicles.All)
        {
            float d=(e.Pos-command.Origin).sqrMagnitude;if(d>=distance)continue;
            var source=_catalog.For(e.TypeId)?.Source;if(source is not {} row)continue;
            var inverse=Q.Inverse(Rotation(e));
            var from=U(V.Transform(N(command.Origin-e.Pos),inverse));var to=U(V.Transform(N(end-e.Pos),inverse));
            if(VehicleDoorServices.HitsHull(row,from,to)){distance=d;best=e.NetIdValue;}
        }
        return best==command.TrailerNetId;
    }
    void Toggle(ushort owner,VehicleHitchCommand command)
    {
        if(_server.Vehicles.TryGetCoupledCab(command.TrailerNetId,out var linked)){Detach(linked.NetIdValue);return;}
        if(!_server.Vehicles.TryGet(command.TrailerNetId,out var trailer))return;
        foreach(var cab in _server.Vehicles.All)
        {
            if(cab.Exploded||cab.CoupledTrailerNetId!=0||Anchor(cab.TypeId,"FifthWheel")==Vector3.zero
                ||(Pin(cab,true)-Pin(trailer,false)).sqrMagnitude>CoupleReach*CoupleReach)continue;
            long tick=_server.Session.CurrentTick+1;
            if(!_server.Vehicles.ServerSetCoupledTrailer(cab.NetIdValue,trailer.NetIdValue,tick))continue;
            _server.Vehicles.ServerAdoptDriverState(new(trailer.NetIdValue),trailer.Pos+Pin(cab,true)-Pin(trailer,false),
                new(trailer.PitchDegrees,trailer.YawDegrees,trailer.RollDegrees),Vector3.zero,Vector3.zero,trailer.SteerDegrees,trailer.Flags,tick);
            _rip[cab.NetIdValue]=0;return;
        }
    }
    void Detach(uint id){_server.Vehicles.ServerSetCoupledTrailer(id,0,_server.Session.CurrentTick+1);_rip.Remove(id);}
    static float Tilt(VehicleReplication.VehicleEntity e)=>MathF.Acos(Math.Clamp(V.Transform(V.UnitY,Rotation(e)).Y,-1,1))*Mathf.Rad2Deg;
    public void Step()
    {
        foreach(var id in _rip.Keys.ToArray())if(!_server.Vehicles.TryGet(id,out var row)||row.CoupledTrailerNetId==0)_rip.Remove(id);
        foreach(var cab in _server.Vehicles.All)
        {
            if(cab.CoupledTrailerNetId==0)continue;
            if(!_server.Vehicles.TryGet(cab.CoupledTrailerNetId,out var trailer)||cab.Exploded||trailer.Exploded
                ||Tilt(cab)>RollDisconnectDeg||Tilt(trailer)>RollDisconnectDeg){Detach(cab.NetIdValue);continue;}
            float mismatch=Math.Abs(cab.LinVel.magnitude-trailer.LinVel.magnitude);
            float timer=cab.LinVel.magnitude>3&&mismatch>7?_rip.GetValueOrDefault(cab.NetIdValue)+(float)SDG.Unturned.SimClock.FixedDelta:0;
            if(timer>.15f){Detach(cab.NetIdValue);continue;}_rip[cab.NetIdValue]=timer;
        }
    }
    public Saved[] Capture()
    {
        var fleet=_server.Vehicles.All.ToArray();var result=new List<Saved>();
        for(int c=0;c<fleet.Length;c++)if(fleet[c].CoupledTrailerNetId!=0)
        {int t=System.Array.FindIndex(fleet,v=>v.NetIdValue==fleet[c].CoupledTrailerNetId);if(t>=0)result.Add(new(c,fleet[c].TypeId,t,fleet[t].TypeId,_rip.GetValueOrDefault(fleet[c].NetIdValue)));}
        return result.ToArray();
    }
    public void Restore(Saved[] saved)
    {
        var fleet=_server.Vehicles.All.ToArray();
        foreach(var row in saved??[])
        {
            if(row==null||row.CabOrdinal<0||row.CabOrdinal>=fleet.Length||row.TrailerOrdinal<0||row.TrailerOrdinal>=fleet.Length
                ||!float.IsFinite(row.RipTimer)||row.RipTimer<0||row.RipTimer>.15f)continue;
            var cab=fleet[row.CabOrdinal];var trailer=fleet[row.TrailerOrdinal];
            if(cab.TypeId!=row.CabType||trailer.TypeId!=row.TrailerType||Anchor(cab.TypeId,"FifthWheel")==Vector3.zero||Anchor(trailer.TypeId,"Kingpin")==Vector3.zero)continue;
            if(_server.Vehicles.ServerSetCoupledTrailer(cab.NetIdValue,trailer.NetIdValue,_server.Session.CurrentTick+1))_rip[cab.NetIdValue]=row.RipTimer;
        }
    }
    public static VehicleHitchCommand ReadCommand(JsonElement args)
    {
        var c=new VehicleHitchCommand{TrailerNetId=checked((uint)Int(args,"trailerNetId")),Origin=Vec(args,"origin"),Direction=Vec(args,"direction")};
        if(!c.IsValid)throw new ArgumentException("Invalid hitch intent");return c;
    }
}
