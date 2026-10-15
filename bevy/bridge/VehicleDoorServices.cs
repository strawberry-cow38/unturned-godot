using System.Text.Json;
using UnityEngine;
using UnturnedGodot.Net;
using static UnturnedBevy.BridgeHost;
using Q=System.Numerics.Quaternion;
using V=System.Numerics.Vector3;

namespace UnturnedBevy;

/// <summary>One source door state per authoritative vehicle. Clients send a
/// driver key or focused look segment; snapshots alone supply phase and target.</summary>
public sealed class VehicleDoorServices
{
    public sealed record Saved(int Ordinal,byte TypeId,bool Open,float Fold,float Hold);
    sealed record Entry(byte TypeId,Vector3 Center,VehicleDoorState State);
    readonly NetWorldServer _server;
    readonly MountedWeaponCatalog _catalog;
    readonly WorldGeometry _geometry;
    readonly Dictionary<uint,Entry> _doors=new();
    public VehicleDoorServices(NetWorldServer server,Catalog catalog,WorldGeometry geometry)
    {
        _server=server;_catalog=catalog.Mounted;_geometry=geometry;
        server.VehicleDoorAllowed=Allowed;server.VehicleDoorRequested=Toggle;
        server.VehicleHost.SeatChanged=Cycle;
    }
    static bool Same(JsonElement s,string a,string b)=>Vec(s,a)==Vec(s,b);
    static bool Present(JsonElement s)=>!Bool(s,"Heli")&&!Bool(s,"Plane")
        &&Same(s,"LandingLegZoneMin","LandingLegZoneMax")&&Same(s,"HeadlightZoneMin","HeadlightZoneMax")
        &&!Same(s,"DoorZoneMin","DoorZoneMax");
    Entry Ensure(uint id)
    {
        if(!_server.Vehicles.TryGet(id,out var body))return null;
        if(_doors.TryGetValue(id,out var found)&&found.TypeId==body.TypeId)return found;
        _doors.Remove(id);
        if(_catalog.For(body.TypeId)?.Source.GetProperty("spec") is not JsonElement spec||!Present(spec))return null;
        var center=(Vec(spec,"DoorZoneMin")+Vec(spec,"DoorZoneMax"))*.5f;
        var e=new Entry(body.TypeId,center,new VehicleDoorState(Float(spec,"DoorFoldDeg",90),()=>Fact(id)));
        _doors[id]=e;Publish(id,e);return e;
    }
    void Publish(uint id,Entry e)=>_server.Vehicles.ServerPublishDoor(new(id),true,e.State.Open,e.State.Fold,_server.Session.CurrentTick+1);
    static V N(Vector3 p)=>new(p.x,p.y,p.z);
    static Vector3 U(V p)=>new(p.X,p.Y,p.Z);
    static Q Rotation(VehicleReplication.VehicleEntity e)=>Q.CreateFromYawPitchRoll(e.YawDegrees*Mathf.Deg2Rad,e.PitchDegrees*Mathf.Deg2Rad,e.RollDegrees*Mathf.Deg2Rad);
    void Fact(uint id)
    {
        if(!_doors.TryGetValue(id,out var e)||!_server.Vehicles.TryGet(id,out var body))return;
        var fact=new VehicleDoorEvent{NetId=id,Tick=unchecked((uint)_server.Session.CurrentTick),Open=e.State.Open,Position=body.Pos+U(V.Transform(N(e.Center),Rotation(body)))};
        _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventVehicleDoor,fact.Write));
    }
    void Toggle(ushort owner,VehicleDoorCommand command)
    {
        var e=Ensure(command.NetId);if(e==null)return;e.State.ToggleDoor();Publish(command.NetId,e);
    }
    void Cycle(uint id)
    {
        var e=Ensure(id);if(e==null)return;e.State.CycleDoor();Publish(id,e);
    }
    bool Allowed(ushort owner,VehicleDoorCommand command)
    {
        var e=Ensure(command.NetId);
        if(e==null||e.State.DoorMoving||!_server.Vehicles.TryGet(command.NetId,out var body))return false;
        if(command.Driver)return _server.VehicleHost.TryGetDriven(owner,out var driven)&&driven==command.NetId;
        if(_server.VehicleHost.IsSeated(owner)||!_server.Players.TryGetByOwner(owner,out var player)
            ||(command.Origin-player.Pos).sqrMagnitude>_server.Combat.DefaultGun.MaxAimOriginOffset*_server.Combat.DefaultGun.MaxAimOriginOffset)return false;
        // Same source look range as PlayerController scan; an intervening world
        // face blocks focus. A hit on this vehicle clips the endpoint as in source.
        var to=command.Origin+command.Direction*2.6f;
        bool hitVehicle=_geometry.RayDetailed(command.Origin,to,out var hit);
        if(hitVehicle)
        {
            if(hit.VehicleNetId!=command.NetId)return false;
            to=hit.Point;
        }
        var inv=Q.Inverse(Rotation(body));
        var from=U(V.Transform(N(command.Origin-body.Pos),inv));var end=U(V.Transform(N(to-body.Pos),inv));
        var source=_catalog.For(body.TypeId).Source;
        if(!hitVehicle&&!HitsHull(source,from,end))return false;
        return Resolve(source.GetProperty("access"),from,end)=="BiFold";
    }
    static bool Segment(Vector3 center,Vector3 size,Vector3 from,Vector3 to)
    {
        var d=from-center;var h=size*.5f;
        return (Math.Abs(d.x)<=h.x&&Math.Abs(d.y)<=h.y&&Math.Abs(d.z)<=h.z)
            ||WorldGeometry.SegmentBox(center,size,from,to,out _,out _);
    }
    // Original LookRayHitsHull fallback tests direct box children, never the
    // enclosing bounds of a concave hull. Source access still picks the zone.
    static bool HitsHull(JsonElement source,Vector3 from,Vector3 to)
    {
        var spec=source.GetProperty("spec");
        bool Boxes(string key,out JsonElement rows)=>spec.TryGetProperty(key,out rows)&&rows.ValueKind==JsonValueKind.Array;
        if(!Boxes("HullBoxes",out var boxes)&&!Boxes("HullBands",out _)&&Segment(Vec(spec,"BoxCenter"),Vec(spec,"BoxSize"),from,to))return true;
        if(boxes.ValueKind==JsonValueKind.Array)foreach(var box in boxes.EnumerateArray())
        {
            var center=ReadVector(box[1]);var rotation=Q.CreateFromAxisAngle(V.UnitY,-box[2].GetSingle()*Mathf.Deg2Rad);
            if(Segment(Vector3.zero,ReadVector(box[0]),U(V.Transform(N(from-center),rotation)),U(V.Transform(N(to-center),rotation))))return true;
        }
        if(source.TryGetProperty("roof",out var roof)&&roof.ValueKind==JsonValueKind.Array&&Segment(ReadVector(roof[1]),ReadVector(roof[0]),from,to))return true;
        if(Boxes("ExtraBoxes",out var extra))foreach(var box in extra.EnumerateArray())if(Segment(ReadVector(box[1]),ReadVector(box[0]),from,to))return true;
        return Vec(spec,"LandingGearSize")!=Vector3.zero&&Segment(Vec(spec,"LandingGearCenter"),Vec(spec,"LandingGearSize"),from,to);
    }
    static Vector3 ReadVector(JsonElement a)=>new(a[0].GetSingle(),a[1].GetSingle(),a[2].GetSingle());
    // Vehicle.ResolveAccess: segment-intersecting zones win in eye-distance
    // order, otherwise nearest endpoint box within AccessNearReach (1.5m).
    static string Resolve(JsonElement zones,Vector3 from,Vector3 to)
    {
        string hit=null,near=null;float bestHit=float.MaxValue,bestNear=float.MaxValue;
        foreach(var zone in zones.EnumerateArray())
        {
            var c=Vec(zone,"Center");var size=Vec(zone,"Size");var half=size*.5f;var delta=from-c;
            bool inside=Math.Abs(delta.x)<=half.x&&Math.Abs(delta.y)<=half.y&&Math.Abs(delta.z)<=half.z;
            if(inside||WorldGeometry.SegmentBox(c,size,from,to,out _,out _))
            {float d=(from-c).sqrMagnitude;if(d<bestHit){bestHit=d;hit=Text(zone,"Kind");}continue;}
            var p=to-c;float x=Math.Max(Math.Abs(p.x)-half.x,0),y=Math.Max(Math.Abs(p.y)-half.y,0),z=Math.Max(Math.Abs(p.z)-half.z,0);
            float nd=x*x+y*y+z*z;if(nd<bestNear){bestNear=nd;near=Text(zone,"Kind");}
        }
        return hit??(bestNear<=1.5f*1.5f?near:null);
    }
    public void Step()
    {
        foreach(var id in _doors.Keys.Where(id=>!_server.Vehicles.TryGet(id,out _)).ToArray())_doors.Remove(id);
        foreach(var body in _server.Vehicles.All)
        {var e=Ensure(body.NetIdValue);if(e==null)continue;e.State.UpdateDoor((float)SDG.Unturned.SimClock.FixedDelta);Publish(body.NetIdValue,e);}
    }
    public Saved[] Capture()=>_server.Vehicles.All.Select((body,i)=>_doors.TryGetValue(body.NetIdValue,out var e)?new Saved(i,body.TypeId,e.State.Open,e.State.Fold,Math.Max(0,e.State.Hold)):null).Where(e=>e!=null).ToArray();
    public void Restore(Saved[] saved)
    {
        var fleet=_server.Vehicles.All.ToArray();
        foreach(var row in saved??[])
        {
            if(row==null||row.Ordinal<0||row.Ordinal>=fleet.Length||fleet[row.Ordinal].TypeId!=row.TypeId)continue;
            var body=fleet[row.Ordinal];var e=Ensure(body.NetIdValue);if(e==null)continue;
            e.State.Restore(row.Open,row.Fold,row.Hold);Publish(body.NetIdValue,e);
        }
    }
    public static VehicleDoorCommand ReadCommand(JsonElement args)
    {
        var c=new VehicleDoorCommand{NetId=checked((uint)Int(args,"netId")),Driver=Bool(args,"driver")};
        if(!c.Driver){c.Origin=Vec(args,"origin");c.Direction=Vec(args,"direction");}
        if(!c.IsValid)throw new ArgumentException("Invalid folding door intent");return c;
    }
}
