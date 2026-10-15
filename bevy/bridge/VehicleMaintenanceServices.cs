using System.Text.Json;
using UnityEngine;
using UnturnedGodot.Net;
using static UnturnedBevy.BridgeHost;
using static UnturnedBevy.VehicleMaintenanceRules;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;

namespace UnturnedBevy;

/// <summary>Source indexed pane/tire repairs and manual horn. Native publishes
/// original mesh transforms; this authority owns damage and accepted repair facts.</summary>
public sealed class VehicleMaintenanceServices
{
    public static readonly string[] Labels = [..GlassPaneLabels, "canopy"];
    public sealed record Pane(byte Slot, float[] Center, float[] Size, float[] Rotation, float[] Scale);
    public sealed record Saved(int Ordinal, byte TypeId, uint Mask, uint Broken, byte Tires, float HornRemaining, Pane[] Panes, bool Registered = true);
    sealed class Entry
    {
        public byte TypeId, Tires;
        public uint Mask, Broken;
        public bool Registered;
        public float HornRemaining;
        public Pane[] Panes = [];
    }
    readonly NetWorldServer _server;
    readonly MountedWeaponCatalog _catalog;
    readonly VehicleLightingServices _lighting;
    readonly Dictionary<uint,Entry> _entries = new();
    public Action<Vector3,float> Noise;
    public VehicleMaintenanceServices(NetWorldServer server, Catalog catalog, VehicleLightingServices lighting, WorldGeometry geometry)
    {
        _server=server;_catalog=catalog.Mounted;_lighting=lighting;
        server.VehicleMaintenanceAllowed=Allowed;server.VehicleMaintenanceRequested=Apply;
        geometry.VehiclePartSolid=Solid;
    }
    Entry Ensure(uint id)
    {
        if(!_server.Vehicles.TryGet(id,out var row))return null;
        if(_entries.TryGetValue(id,out var e)&&e.TypeId==row.TypeId)return e;
        _entries[id]=e=new(){TypeId=row.TypeId};return e;
    }
    static V N(Vector3 v)=>new(v.x,v.y,v.z);
    static V N(float[] v)=>new(v[0],v[1],v[2]);
    static Vector3 U(V v)=>new(v.X,v.Y,v.Z);
    static Q Hull(VehicleReplication.VehicleEntity e)=>Q.CreateFromYawPitchRoll(e.YawDegrees*Mathf.Deg2Rad,e.PitchDegrees*Mathf.Deg2Rad,e.RollDegrees*Mathf.Deg2Rad);
    static Q Rotation(Pane p)=>new(p.Rotation[0],p.Rotation[1],p.Rotation[2],p.Rotation[3]);
    static bool Valid(Pane p)=>p!=null&&p.Slot<Labels.Length&&p.Center?.Length==3&&p.Size?.Length==3&&p.Scale?.Length==3&&p.Rotation?.Length==4
        &&p.Center.All(float.IsFinite)&&p.Size.All(x=>float.IsFinite(x)&&x>=0&&x<=200)
        &&p.Scale.All(x=>float.IsFinite(x)&&Math.Abs(x)>=.001f&&Math.Abs(x)<=100)
        &&p.Rotation.All(float.IsFinite)&&Math.Abs(Rotation(p).LengthSquared()-1)<.002f&&N(p.Center).LengthSquared()<=10000;
    public object Register(JsonElement request)
    {
        uint id=checked((uint)Int(request,"netId"));var e=Ensure(id)??throw new ArgumentException("Unknown vehicle");
        var list=new List<Pane>();uint mask=0;
        foreach(var row in request.GetProperty("panes").EnumerateArray())
        {
            int slot=System.Array.IndexOf(Labels,Text(row,"label"));
            if(slot<0||(mask&(1u<<slot))!=0||list.Count>=18)throw new ArgumentException("Invalid/duplicate source pane label");
            var rotation=row.TryGetProperty("rotation",out var r)?r.EnumerateArray().Select(x=>x.GetSingle()).ToArray():[0,0,0,1];
            var scale=row.TryGetProperty("scale",out var z)?z.EnumerateArray().Select(x=>x.GetSingle()).ToArray():[1,1,1];
            var p=new Pane((byte)slot,Array(Vec(row,"center")),Array(Vec(row,"size")),rotation,scale);
            if(!Valid(p))throw new ArgumentException("Invalid pane transform");mask|=1u<<slot;list.Add(p);
        }
        byte tires=checked((byte)Int(request,"tireMask"));
        if(e.Registered&&(mask!=e.Mask||tires!=e.Tires))throw new ArgumentException("Original pane/tire set changed after registration");
        e.Registered=true;e.Mask=mask;e.Tires=tires;e.Panes=list.ToArray();Publish(id,e);
        return new{glassMask=mask,brokenGlassMask=e.Broken,tireMask=tires};
    }
    public bool Solid(uint id,string part)
    {
        if(part?.StartsWith("glass:")!=true)return true;
        return int.TryParse(part.AsSpan(6),out int slot)&&slot is >=0 and <18&&_entries.TryGetValue(id,out var e)
            &&(e.Mask&(1u<<slot))!=0&&(e.Broken&(1u<<slot))==0;
    }
    public int ResolveGlass(uint id,Vector3 world)
    {
        if(!_entries.TryGetValue(id,out var e)||!_server.Vehicles.TryGet(id,out var body))return -1;
        var hull=V.Transform(N(world-body.Pos),Q.Inverse(Hull(body)));int best=-1;float distance=GlassTolerance;
        foreach(var p in e.Panes)
        {
            if((e.Broken&(1u<<p.Slot))!=0)continue;
            var local=V.Transform(hull-N(p.Center),Q.Inverse(Rotation(p)))/N(p.Scale);
            var half=N(p.Size)*.5f+new V(GlassTolerance);
            if(Math.Abs(local.X)>half.X||Math.Abs(local.Y)>half.Y||Math.Abs(local.Z)>half.Z)continue;
            float d=local.Length();if(d<distance||best<0){best=p.Slot;distance=d;}
        }
        return best;
    }
    (Vector3 center,Vector3 normal,Vector3 half) Frame(uint id,Pane p)
    {
        _server.Vehicles.TryGet(id,out var body);var rotation=Hull(body);var outward=V.Transform(N(p.Center),rotation);
        var s=N(p.Size);V axis=s.X<=s.Y&&s.X<=s.Z?V.UnitX:s.Y<=s.Z?V.UnitY:V.UnitZ;
        var normal=V.Normalize(V.Transform(V.Transform(axis*N(p.Scale),Rotation(p)),rotation));
        if(V.Dot(normal,outward)<0)normal=-normal;
        return(body.Pos+U(outward),U(normal),U(V.Max(s*.5f,new V(.2f))));
    }
    public bool BreakGlass(uint id,int slot)
    {
        if(!_entries.TryGetValue(id,out var e)||slot is <0 or >=18||(e.Mask&(1u<<slot))==0||(e.Broken&(1u<<slot))!=0)return false;
        var pane=e.Panes.First(p=>p.Slot==slot);var frame=Frame(id,pane);
        e.Broken|=1u<<slot;Publish(id,e);
        Fact(id,VehicleMaintenanceKind.GlassBroken,(byte)slot,frame.center,frame.normal,frame.half);return true;
    }
    bool Allowed(ushort owner,VehicleMaintenanceCommand command)
    {
        if(!command.IsValid||!_server.Vehicles.TryGet(command.NetId,out var body)||body.Exploded)return false;
        var e=Ensure(command.NetId);
        if(command.Action==VehicleMaintenanceAction.Horn)
            return _server.VehicleHost.TryGetDriven(owner,out uint id)&&id==command.NetId&&e.HornRemaining<=0&&_lighting.CanHonk(id);
        if(!_server.Players.TryGetByOwner(owner,out var player)||(player.Pos-body.Pos).sqrMagnitude>ServerVehicles.PaintReach*ServerVehicles.PaintReach)return false;
        return command.Action==VehicleMaintenanceAction.RepairGlass?(e.Broken&(1u<<command.Index))!=0:
            (e.Tires&(1<<command.Index))!=0&&(body.PoppedTireMask&(1<<command.Index))!=0;
    }
    void Apply(ushort owner,VehicleMaintenanceCommand command)
    {
        var e=Ensure(command.NetId);if(e==null)return;
        _server.Vehicles.TryGet(command.NetId,out var body);
        switch(command.Action)
        {
            case VehicleMaintenanceAction.RepairGlass:
                e.Broken&=~(1u<<command.Index);Publish(command.NetId,e);
                Fact(command.NetId,VehicleMaintenanceKind.GlassRepaired,command.Index,Frame(command.NetId,e.Panes.First(p=>p.Slot==command.Index)).center);break;
            case VehicleMaintenanceAction.RepairTire:
                _server.Vehicles.ServerSetPoppedTires(new(command.NetId),(byte)(body.PoppedTireMask&~(1<<command.Index)),_server.Session.CurrentTick+1);
                Fact(command.NetId,VehicleMaintenanceKind.TireRepaired,command.Index,body.Pos);break;
            case VehicleMaintenanceAction.Horn:
                var spec=_catalog.For(body.TypeId)?.Source.GetProperty("spec");float cooldown=spec is JsonElement s?Float(s,"HornCooldown"):0;
                e.HornRemaining=cooldown>0?cooldown:HornCooldown;
                Fact(command.NetId,VehicleMaintenanceKind.ManualHorn,255,body.Pos);Noise?.Invoke(body.Pos,NoiseRules.Horn);break;
        }
    }
    void Publish(uint id,Entry e)=>_server.Vehicles.ServerPublishMaintenance(new(id),e.Mask,e.Broken,e.Tires,_server.Session.CurrentTick+1);
    void Fact(uint id,VehicleMaintenanceKind kind,byte index,Vector3 position,Vector3 normal=default,Vector3 half=default)
    {
        var fact=new VehicleMaintenanceEvent{NetId=id,Tick=unchecked((uint)_server.Session.CurrentTick),Kind=kind,Index=index,Position=position,Normal=normal,HalfSize=half};
        _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventVehicleMaintenance,fact.Write));
    }
    public void Step()
    {
        foreach(var (id,e) in _entries.ToArray())
        {
            if(!_server.Vehicles.TryGet(id,out var body)){_entries.Remove(id);continue;}
            // Original aircraft branch returns before road horn cooldown.
            var spec=_catalog.For(body.TypeId)?.Source.GetProperty("spec");
            if(spec is not JsonElement s||(!Bool(s,"Heli")&&!Bool(s,"Plane")))e.HornRemaining=Math.Max(0,e.HornRemaining-(float)SDG.Unturned.SimClock.FixedDelta);
        }
    }
    public Saved[] Capture()=>_server.Vehicles.All.Select((v,i)=>_entries.TryGetValue(v.NetIdValue,out var e)?new Saved(i,v.TypeId,e.Mask,e.Broken,e.Tires,e.HornRemaining,e.Panes,e.Registered):null).Where(s=>s!=null).ToArray();
    public void Restore(Saved[] saved)
    {
        var fleet=_server.Vehicles.All.ToArray();
        foreach(var s in saved??[])
        {
            if(s.Ordinal<0||s.Ordinal>=fleet.Length||fleet[s.Ordinal].TypeId!=s.TypeId)continue;
            if(s.Mask>VehicleReplication.GlassBits||(s.Broken&~s.Mask)!=0||!float.IsFinite(s.HornRemaining)||s.HornRemaining<0||s.HornRemaining>60
                ||s.Panes==null||s.Panes.Length>18||s.Panes.Any(p=>!Valid(p))||s.Panes.Select(p=>p.Slot).Distinct().Count()!=s.Panes.Length
                ||s.Panes.Aggregate(0u,(mask,p)=>mask|(1u<<p.Slot))!=s.Mask)throw new InvalidDataException("Invalid saved vehicle maintenance state");
            uint id=fleet[s.Ordinal].NetIdValue;var e=Ensure(id);e.Mask=s.Mask;e.Broken=s.Broken;e.Tires=s.Tires;e.Panes=s.Panes;e.HornRemaining=s.HornRemaining;e.Registered=s.Registered;Publish(id,e);
        }
    }
    public static object Metadata()=>new{glassLabels=Labels,glassNames=Labels.Select(GlassPaneDisplay).ToArray(),glassTolerance=GlassTolerance};
    public static VehicleMaintenanceCommand ReadCommand(JsonElement args)
    {
        if(!Enum.TryParse<VehicleMaintenanceAction>(Text(args,"action"),true,out var action)||!Enum.IsDefined(action))throw new ArgumentException("Unknown vehicle maintenance action");
        var cmd=new VehicleMaintenanceCommand{NetId=checked((uint)Int(args,"netId")),Action=action,Index=checked((byte)Int(args,"index"))};
        if(!cmd.IsValid)throw new ArgumentException("Invalid vehicle maintenance intent");return cmd;
    }
}
