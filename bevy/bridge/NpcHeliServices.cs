using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using Quaternion=System.Numerics.Quaternion;
using GVector=Godot.Vector3;
using Vector3=UnityEngine.Vector3;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Original NpcHeli controller over the accepted host airframe. Its only
/// motion result is a four-axis input; Bevy keeps the one physical vehicle body.</summary>
public sealed class NpcHeliServices
{
    readonly NetWorldServer _server;readonly Catalog _catalog;readonly MountedWeaponServices _mounts;
    readonly WorldGeometry _geometry;readonly SourceCanopies _canopies;readonly CombatMountedWorldRay _ray;
    readonly Func<JsonElement,uint> _register;readonly Dictionary<uint,NpcHeliBrain> _brains=new();
    readonly List<(string Name,GVector Pos)> _nodes=new();
    readonly List<NpcHeliTerrainGrid> _grids=new();
    NpcHeliTerrain _terrain;string _nodeFile="nodes.tsv";
    long Tick=>_server.Session.CurrentTick;
    public sealed record Saved(float[] Bounds,string NodeFile,float[] NodeOffset,Entry[] Vehicles,NpcHeliTerrainGrid.Saved[] Terrain=null);
    public sealed record Entry(int Ordinal,byte TypeId,NpcHeliBrain.Saved Brain,float[] LastAttack,double AttackAge);
    Vector3 _nodeOffset;
    public NpcHeliServices(NetWorldServer server,Catalog catalog,MountedWeaponServices mounts,WorldGeometry geometry,
        SourceCanopies canopies,CombatMountedWorldRay ray,Func<JsonElement,uint> register,Vector3 initialOffset=default)
    {
        _server=server;_catalog=catalog;_mounts=mounts;_geometry=geometry;_canopies=canopies;_ray=ray;_register=register;_nodeOffset=initialOffset;
        var previousHit=server.Combat.DamageBody;
        server.Combat.DamageBody=hit=>{
            if(!hit.Melee&&hit.ShotOrigin is Vector3 origin&&_brains.TryGetValue(hit.Hit.VehicleNetId,out var brain))
            {brain.Heli.LastAttackedFrom=G(origin);brain.Heli.LastAttackedAtMsec=Tick*20.0;}
            previousHit?.Invoke(hit);
        };
        var priorConsole=server.Transactions.HostConsole;
        server.Transactions.HostConsole=(owner,verb,arg)=>verb=="spawnheli"?SpawnConsole(owner,arg):priorConsole?.Invoke(owner,verb,arg);
    }
    static GVector G(Vector3 v)=>new(v.x,v.y,v.z);
    static Vector3 U(GVector v)=>new(v.X,v.Y,v.Z);
    public object Configure(JsonElement request)
    {
        if(request.TryGetProperty("terrainPatches",out var terrain))
        {
            var grids=terrain.EnumerateArray().Select(row=>new NpcHeliTerrainGrid(row)).ToArray();
            if(Bool(request,"terrainPatchesReplace"))_grids.Clear();_grids.AddRange(grids);
        }
        if(request.TryGetProperty("mapBounds",out var bounds)&&bounds.ValueKind!=JsonValueKind.Null)
        {
            if(bounds.GetArrayLength()!=4)throw new ArgumentException("NPC map bounds require minX,maxX,minZ,maxZ");
            float[] b=bounds.EnumerateArray().Select(v=>v.GetSingle()).ToArray();
            if(b.Any(v=>!float.IsFinite(v))||b[0]>=b[1]||b[2]>=b[3])throw new ArgumentException("Invalid NPC map bounds");
            _terrain=new(){Bounds=(b[0],b[1],b[2],b[3]),SampleHeight=SampleHeight};
        }
        _nodeFile=Text(request,"nodeFile",_nodeFile);
        if(Path.GetFileName(_nodeFile)!=_nodeFile)throw new ArgumentException("NPC nodeFile must be a content filename");
        ReloadNodes();foreach(var brain in _brains.Values)brain.Terr=_terrain;
        if(Bool(request,"replace"))_brains.Clear();
        if(request.TryGetProperty("vehicles",out var vehicles))foreach(var row in vehicles.EnumerateArray())
            Attach(checked((uint)Int(row,"netId")),G(Vec(row,"target")),Text(row,"targetName"),true);
        return new{registered=_brains.Count,locations=_nodes.Count,hasTerrain=_terrain!=null,terrainPatches=_grids.Count};
    }
    float SampleHeight(float x,float z)
    {
        if(_grids.Count==0)throw new InvalidOperationException("NPC helicopter requires complete source terrain grids");
        NpcHeliTerrainGrid best=null;float distance=float.PositiveInfinity;
        foreach(var grid in _grids){float d=grid.DistanceSquared(x,z);if(d>=distance)continue;best=grid;distance=d;if(d==0)break;}
        return best.SampleHeight(x,z);
    }
    void ReloadNodes()
    { _nodes.Clear();foreach(var (name,p) in SourceMapNodes.Load(Path.Combine(_catalog.Root,_nodeFile)))_nodes.Add((name,p+G(_nodeOffset))); }
    void Attach(uint id,GVector target,string name,bool equipCrew)
    {
        if(!_server.Vehicles.TryGet(id,out var vehicle)||_catalog.Mounted.For(vehicle.TypeId) is not{} fleet
            ||!Bool(fleet.Source.GetProperty("spec"),"Heli"))throw new ArgumentException("NPC requires a registered source helicopter");
        if(_brains.ContainsKey(id))throw new ArgumentException("Helicopter already has an original NPC controller");
        var source=fleet.Source.GetProperty("spec");
        var body=new NpcHeliAirframe{NetId=id,SpeedMaxMps=Float(source,"SpeedMax"),DebugThrust=Float(source,"HeliThrust"),
            Turrets=fleet.Mounts.Select(m=>new NpcHeliMount(m.Seat,m.YawMin,m.YawMax,m.PitchMin,m.PitchMax,new(m.GunnerAt[0],m.GunnerAt[1],m.GunnerAt[2]))).ToArray()};
        if(equipCrew)_mounts.EquipNpcCrew(id);
        body.TurretCrewAlive=seat=>_mounts.NpcCrewAlive(id,seat);
        body.AimTurret=(seat,yaw,pitch)=>_mounts.AimNpc(id,seat,yaw,pitch);
        body.TurretMuzzle=seat=>_mounts.NpcBarrel(id,seat) is{} frame?G(frame.Muzzle):null;
        body.TurretBarrelDir=seat=>_mounts.NpcBarrel(id,seat) is{} frame?G(frame.Direction):GVector.Forward;
        MountedWeaponServices.NpcShot pending=null;
        body.TryTurretFire=(int seat,out GVector origin,out GVector direction,out string gun)=>{
            pending=_mounts.ReadyNpc(id,seat);origin=pending==null?GVector.Zero:G(pending.Origin);
            direction=pending==null?GVector.Forward:G(pending.Direction);gun=pending?.Gun;return pending!=null;
        };
        var brain=new NpcHeliBrain{Heli=body,Terr=_terrain,Target=target,TargetName=name,NowMsec=Tick*20.0,PhysicsTick=(ulong)Tick,
            Players=()=>_server.Players.All.Select(p=>new NpcHeliPlayer(p.OwnerPlayerId,G(p.Pos),_server.CombatState.TryGet(p.OwnerPlayerId,out var c)&&c.Alive?c.HealthExact:0)),
            ClearWorld=ClearShot,CanopyBlocks=_canopies.CanopyBlocksSegment,NpcShot=(_,direction,_)=>{_mounts.CompleteNpc(pending,U(direction));pending=null;}};
        _brains.Add(id,brain);Adopt(brain,vehicle);
    }
    bool ClearShot(uint id,GVector a,GVector b,ushort target)
    {
        var from=U(a);var to=U(b);float length=(to-from).magnitude;if(length<=0)return true;
        bool blocked=_ray(id,from,to,out var hit);float nearest=blocked?(hit.Point-from).magnitude:length;
        var direction=(to-from)/length;
        // Original physics query accepts the intended player's own collider and
        // refuses a different player standing in front. Reuse the source capsule
        // math, with the actual player body's extracted radius/stance height.
        foreach(var p in _server.Players.All)
        {
            if(!_server.CombatState.IsAlive(p.OwnerPlayerId))continue;
            var stance=p.Stance switch{2=>EPlayerStance.CROUCH,3=>EPlayerStance.PRONE,_=>EPlayerStance.STAND};
            float d=ZombieCombat.RayCapsule(from,direction,p.Pos,NpcHeliRules.PlayerRadius,PlayerMovementDef.HeightForStance(stance));
            if(d<0||d>nearest)continue;nearest=d;blocked=p.OwnerPlayerId!=target;
        }
        return !blocked;
    }
    static void Adopt(NpcHeliBrain brain,VehicleReplication.VehicleEntity vehicle)
    {
        var body=brain.Heli;body.GlobalPosition=G(vehicle.Pos);body.LinearVelocity=G(vehicle.LinVel);body.AngularVelocity=G(vehicle.AngVel);
        body.Rotation=Quaternion.CreateFromYawPitchRoll(vehicle.YawDegrees*MathF.PI/180,vehicle.PitchDegrees*MathF.PI/180,vehicle.RollDegrees*MathF.PI/180);
        body.Exploded=vehicle.Exploded;
    }
    public void Step()
    {
        foreach(var pair in _brains.ToArray())
        {
            if(!_server.Vehicles.TryGet(pair.Key,out var v)){pair.Value.Heli.Exists=false;_brains.Remove(pair.Key);continue;}
            var brain=pair.Value;Adopt(brain,v);brain.NowMsec=Tick*20.0;brain.PhysicsTick=(ulong)Tick;
            if(v.Exploded||v.DriverPlayerId!=0){brain.Heli.Input=default;continue;}
            brain.Step(SimClock.FixedDelta);
        }
    }
    public object[] Snapshots()=>_brains.Select(pair=>{
        var b=pair.Value;var c=b.Heli.Input;bool active=_server.Vehicles.TryGet(pair.Key,out var v)&&!v.Exploded&&v.DriverPlayerId==0;
        return (object)new{netId=pair.Key,active,rotorRunning=v?.EngineOn??false,controls=new{collective=c.Collective,yaw=c.Yaw,pitch=c.Pitch,roll=c.Roll},
            phase=b.Phase.ToString().ToLowerInvariant(),mode=b.Mode.ToString().ToLowerInvariant(),target=Array(U(b.Target)),targetName=b.TargetName,
            lastSeen=Array(U(b.LastSeen)),lockSeconds=b.DebugLockLeftSec,range=b.DebugRange,wantHeight=b.DebugWantY,
            wantClimb=b.DebugWantClimbMps,wantNoseUp=b.LastWantNoseUpDeg,orbitStrafing=b.OrbitStrafing,burstLeft=b.DebugBurstLeft};
    }).ToArray();
    (bool Success,string Text) SpawnConsole(ushort owner,string arg)
    {
        var list=_catalog.Mounted.All;
        var spec=list.FirstOrDefault(v=>v.Key.Equals(arg,StringComparison.OrdinalIgnoreCase))??list.FirstOrDefault(v=>v.Key.StartsWith(arg,StringComparison.OrdinalIgnoreCase));
        if(spec==null)return(false,$"no vehicle '{arg}'");
        if(!Bool(spec.Source.GetProperty("spec"),"Heli"))return(false,$"'{spec.Key}' is not a helicopter");
        if(!_server.Players.TryGetByOwner(owner,out var player))return(false,"no player");
        if(_terrain!=null&&_grids.Count==0)return(false,"NPC helicopter requires complete source terrain grids");
        var plan=NpcHeliBrain.Plan(_nodes,_terrain,G(player.Pos));
        if(plan==null)return(false,"no map nodes to fly to (nodes.tsv empty?)");
        var to=(new Godot.Vector2(plan.Target.X-plan.Position.X,plan.Target.Z-plan.Position.Z)).Normalized();
        float yaw=MathF.Atan2(-to.X,-to.Y)*180/MathF.PI;
        var registration=NpcHeliRegistration.For(spec,U(plan.Position),yaw);
        uint id=_register(registration);var v=_server.Vehicles.All.First(e=>e.NetIdValue==id);
        var velocity=new Vector3(to.X,0,to.Y)*Float(spec.Source.GetProperty("spec"),"SpeedMax")*.5f;
        _server.Vehicles.ServerPublish(new(id),v.Pos,new(0,yaw,0),velocity,Vector3.zero,0,v.Fuel,v.Health,v.Battery,VehicleReplication.FlagEngineOn,Tick);
        Attach(id,plan.Target,plan.Name,true);
        return(true,$"npc {spec.Key} inbound from the map edge -> {plan.Name}, holding {NpcHeliBrain.CanopyClearance:0} m over the terrain, will circle at {NpcHeliBrain.OrbitRadius:0} m");
    }
    public Saved Capture()
    {
        var fleet=_server.Vehicles.All.ToArray();var b=_terrain?.Bounds;
        return new(b.HasValue?[b.Value.minX,b.Value.maxX,b.Value.minZ,b.Value.maxZ]:null,_nodeFile,Array(_nodeOffset),
            fleet.Select((v,i)=>(v,i)).Where(p=>_brains.ContainsKey(p.v.NetIdValue)).Select(p=>{
                var brain=_brains[p.v.NetIdValue];return new Entry(p.i,p.v.TypeId,brain.Capture(),Array(U(brain.Heli.LastAttackedFrom)),brain.NowMsec-brain.Heli.LastAttackedAtMsec);
            }).ToArray(),_grids.Select(g=>g.Capture()).ToArray());
    }
    public void Restore(Saved saved)
    {
        if(saved==null)return;_nodeOffset=Vec(JsonSerializer.SerializeToElement(saved.NodeOffset));
        Configure(JsonSerializer.SerializeToElement(new{mapBounds=saved.Bounds,nodeFile=saved.NodeFile,replace=true}));
        _grids.Clear();_grids.AddRange((saved.Terrain??[]).Select(NpcHeliTerrainGrid.Restore));
        var fleet=_server.Vehicles.All.ToArray();
        foreach(var e in saved.Vehicles??[])
        {
            if(e.Ordinal<0||e.Ordinal>=fleet.Length||fleet[e.Ordinal].TypeId!=e.TypeId)continue;
            uint id=fleet[e.Ordinal].NetIdValue;Attach(id,G(Vec(JsonSerializer.SerializeToElement(e.Brain.Target))),e.Brain.TargetName,false);
            var brain=_brains[id];brain.Restore(e.Brain);brain.Heli.LastAttackedFrom=G(Vec(JsonSerializer.SerializeToElement(e.LastAttack)));
            brain.Heli.LastAttackedAtMsec=brain.NowMsec-e.AttackAge;
        }
    }
    public void TranslateWorld(Vector3 delta)
    {
        foreach(var grid in _grids)grid.Translate(delta);
        _nodeOffset+=delta;for(int i=0;i<_nodes.Count;i++)_nodes[i]=(_nodes[i].Name,_nodes[i].Pos+G(delta));
        if(_terrain!=null){var b=_terrain.Bounds;_terrain.Bounds=(b.minX+delta.x,b.maxX+delta.x,b.minZ+delta.z,b.maxZ+delta.z);}
        foreach(var brain in _brains.Values){brain.TranslateWorld(G(delta));brain.Heli.LastAttackedFrom+=G(delta);}
    }
}
