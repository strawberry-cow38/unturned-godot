using System.Text.Json;
using Godot;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using UVector = UnityEngine.Vector3;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Source AnimalField/AnimalAgent hosted on the real authoritative world.
/// The generated partial contains its original fauna parser and stream decisions.</summary>
public sealed partial class WildlifeServices
{
    readonly NetWorldServer _server;
    readonly WorldGeometry _geometry;
    readonly Action<string,object> _emit;
    readonly Dictionary<uint,AnimalBrain> _brains = new();
    readonly List<Vector3> _anchors = new();
    string _mapRoot;
    int _animalSpawnSeq;
    static long CorpseTicks => (long)Math.Ceiling(AnimalBrain.CorpseSeconds / SimClock.FixedDelta);
    public bool GeometryReady => _geometry.HasTerrain;
    long Tick => _server.Session.CurrentTick;
    public WildlifeServices(NetWorldServer server,WorldGeometry geometry,Action<string,object> emit)
    {
        _server=server; _geometry=geometry; _emit=emit;
        var previousHit=server.Combat.DamageBody;
        server.Combat.DamageBody=h=>{if(h.Hit.AnimalNetId!=0)Damage(h.Hit.AnimalNetId,h.Damage,h.Hit.Point,h.Direction,h.Attacker,h.Melee?"melee":"bullet");else previousHit?.Invoke(h);};
        var previousBlast=server.Combat.DamageHostBlast;
        server.Combat.DamageHostBlast=b=>{previousBlast?.Invoke(b);Blast(b);};
        server.Combat.DamageAnimalMelee=Melee;
        server.Combat.WorldRayDetailed=Ray;
    }
    static UVector U(Vector3 v) => new(v.X,v.Y,v.Z);
    static Vector3 G(UVector v) => new(v.x,v.y,v.z);
    byte SampleLayer(float x,float z) => _geometry.DominantLayer(x,z);
    List<Vector3> GatherAnchors()
    {
        _anchors.Clear(); foreach(var player in _server.Players.All) _anchors.Add(G(player.Pos)); return _anchors;
    }
    bool Occluded(Vector3 a,Vector3 b) => _geometry.RayDetailed(U(a),U(b),out _);
    float Ground(float x,float z)
    {
        float sampled=_geometry.GroundHeight(x,z);
        return _geometry.RayDetailed(new(x,sampled+4,z),new(x,sampled-4,z),out var hit)?hit.Point.y:sampled;
    }
    public object Register(JsonElement r,double originX,double originZ)
    {
        string mapRoot=Path.GetFullPath(Text(r,"mapRoot"));
        if(_mapRoot==mapRoot) return new {points=_pts.Count,live=_brains.Count};
        if(_mapRoot!=null) throw new InvalidOperationException("A different wildlife field requires init");
        _mapRoot=mapRoot;
        if(r.TryGetProperty("points",out var points))
            LoadGenerated(mapRoot,points.EnumerateArray().Select(p=>G(Vec(p))).ToArray());
        else
        {
            LoadFromPei(mapRoot);
            TranslateWorld(new((float)-originX,0,(float)-originZ));
        }
        return new {points=_pts.Count,live=_brains.Count,sourceExists=File.Exists(Path.Combine(mapRoot,"Spawns","Fauna.dat"))};
    }
    AnimalBrain BuildAnimal(ushort animalId,Vector3 atGround,uint seed)
    {
        if(!Kinds.TryGetValue(animalId,out var def)) return null;
        var a=new AnimalBrain{NetId=_server.Ids.Mint().Value,AnimalId=animalId,Species=AnimalCatalog.SpeciesForAnimalId(animalId),
            BodyH=def.bodyH,Health=def.health,Seed=seed,Home=new(atGround.X,0,atGround.Z),
            GlobalPosition=new(atGround.X,_geometry.GroundHeight(atGround.X,atGround.Z),atGround.Z),
            SampleGround=Ground,SampleLayer=SampleLayer,Occluded=Occluded,Anchors=()=>_server.Players.All.Select(p=>G(p.Pos))};
        a.Panic=()=>_emit("animalPanic",new{netId=a.NetId,species=a.Species,rig=AnimalCatalog.Get(a.Species).Rig,position=Array(U(a.GlobalPosition))});
        a.Died=impulse=>{
            a.DeathImpulse=impulse;
            a.RetireTick=Tick+CorpseTicks;
            _server.Animals.ServerPublish(new(a.NetId),U(a.GlobalPosition),a.Yaw,(byte)AnimalNetAnim.Dead,Tick);
            _emit("animalDied",new{netId=a.NetId,position=Array(U(a.GlobalPosition)),impulse=Array(U(impulse)),corpseTicks=CorpseTicks});
        };
        a.Begin(); _brains.Add(a.NetId,a);
        // Console registration occurs between ticks; the next delta must include
        // even an Idle animal whose transform has not changed since creation.
        _server.Animals.ServerSpawn(new(a.NetId),a.Species,U(a.GlobalPosition),Tick+1);
        return a;
    }
    void Remove(AnimalBrain a)
    { a.Freed=true; _brains.Remove(a.NetId); _server.Animals.ServerRemove(new(a.NetId),Tick); }
    public bool Ray(UVector from,UVector to,out CombatWorldHit hit)
    {
        bool found=_geometry.RayDetailed(from,to,out hit);
        var direction=to-from; float length=direction.magnitude;
        if(length<=0)return found;
        direction/=length; float nearest=found?(hit.Point-from).magnitude:length;
        foreach(var a in _brains.Values)
        {
            if(a.Dead)continue;
            float distance=ZombieCombat.RayCapsule(from,direction,U(a.GlobalPosition),a.Radius,Math.Max(.6f,a.BodyH));
            if(distance<0||distance>nearest)continue;
            nearest=distance;hit=new CombatWorldHit(from+direction*distance,animalNetId:a.NetId);found=true;
        }
        return found;
    }
    void Damage(uint id,float amount,UVector point,UVector direction,ushort attacker,string source)
    {
        if(amount<=0||!_brains.TryGetValue(id,out var a)||a.Dead)return;
        a.DamageHit(amount,G(point),G(direction));
        _emit("animalHit",new{netId=id,damage=amount,point=Array(point),direction=Array(direction),killed=a.Dead,health=a.Health,attacker,source,impactEmitted=source=="bullet",hitConfirmedEmitted=false});
    }
    bool Melee(ushort owner,UVector origin,UVector direction,float reach,float damage,long tick)
    {
        // Source ApplyMeleeHit handles focused bodies, trees and destructible
        // props before its animal cone. Ordinary world walls have no extra cone
        // occlusion check in that source path; retain that behavior here.
        if(_geometry.RayDetailed(origin,origin+direction*reach,out var blocker)
            &&(blocker.ResourceIndex>=0||blocker.DestructibleIndex>=0||blocker.VehicleNetId!=0||blocker.DeployableNetId!=0))return false;
        foreach(var a in _brains.Values)
        {
            if(a.Dead)continue;
            var point=U(a.GlobalPosition)+new UVector(0,.5f,0);var to=point-origin;
            if(to.magnitude>=reach||UVector.Dot(to.normalized,direction)<=.3f)continue;
            Damage(a.NetId,damage,point,direction,owner,"melee");return true;
        }
        return false;
    }
    void Blast(CombatBlast b)
    {
        foreach(var a in _brains.Values.ToArray())
        {
            if(a.Dead)continue;var p=U(a.GlobalPosition);float range=(p-b.Position).magnitude;
            if(range>b.Radius||_geometry.BlastBlocked(b.Position,p))continue;
            Damage(a.NetId,ExplosionMath.Linear(b.AnimalDamage,range,b.Radius),p,(p-b.Position).normalized,b.Attacker,"blast");
        }
    }
    public void Step()
    {
        // Chunked native worldGeometry replacement temporarily has no terrain
        // BVH. Keep the existing body pose until the loader finalizes it instead
        // of sampling the missing-height sentinel as a real sea-level floor.
        if(!GeometryReady)return;
        HubProcess(SimClock.FixedDelta);
        foreach(var a in _brains.Values.ToArray())
        {
            if(a.Dead && a.RetireTick<=Tick) {Remove(a);continue;}
            a.Step(SimClock.FixedDelta);
            if(Tick%PublishDivisorTicks==0) _server.Animals.ServerPublish(new(a.NetId),U(a.GlobalPosition),a.Yaw,a.Dead?(byte)AnimalNetAnim.Dead:a.NetAnim,Tick);
        }
    }
    public object[] Snapshots(NetWorldClient client) => client.Animals.All.Select(e=>{
        _brains.TryGetValue(e.NetIdValue,out var a); var kind=AnimalCatalog.Get(e.Species);
        return (object)new{netId=e.NetIdValue,species=e.Species,animalId=kind.AnimalId,position=Array(e.Pos),yaw=e.YawDegrees,
            hostPosition=a==null?null:Array(U(a.GlobalPosition)),hostYaw=a?.Yaw,
            animState=e.AnimState,clip=a?.Clip??"Idle",health=a?.Health??0,dead=a?.Dead??e.IsDead,
            bodyHeight=a?.BodyH??1,radius=a?.Radius??.38f,rig=$"{kind.Rig}_rig.json",texture=$"objects/{kind.Tex}",
            rigYawFix=AnimalBrain.RigYawFix,corpseRemainingTicks=a?.Dead==true?Math.Max(0,a.RetireTick-Tick):0,
            deathImpulse=a?.Dead==true?Array(U(a.DeathImpulse)):null};
    }).ToArray();
    public static object[] Catalog() => Kinds.Select(k=>new{animalId=k.Key,species=AnimalCatalog.SpeciesForAnimalId(k.Key),rig=$"{k.Value.rig}_rig.json",
        texture=$"objects/{k.Value.tex}",bodyHeight=k.Value.bodyH,health=k.Value.health,rigYawFix=AnimalBrain.RigYawFix}).Cast<object>().ToArray();
    public void TranslateWorld(UVector d)
    {
        for(int i=0;i<_pts.Count;i++) {var p=_pts[i];p.X+=d.x;p.Z+=d.z;_pts[i]=p;}
        foreach(var a in _brains.Values) a.TranslateWorld(G(d));
        for(int i=0;i<_anchors.Count;i++) _anchors[i]+=G(d);
    }
    public sealed record SavedPoint(byte Type,float X,float Z);
    public sealed record SavedAnimal(int Point,AnimalBrain.Saved Brain);
    public sealed record Saved(string MapRoot,SavedPoint[] Points,ushort[][] Tables,double StreamTimer,SavedAnimal[] Animals,int SpawnSequence=0);
    public Saved Capture() => _mapRoot==null&&_brains.Count==0?null:new(_mapRoot,_pts.Select(p=>new SavedPoint(p.Type,p.X,p.Z)).ToArray(),_tableIds,_acc,
        _brains.Values.Select(a=>new SavedAnimal(_live.FirstOrDefault(p=>ReferenceEquals(p.Value,a),new(-1,null)).Key,a.Capture(Tick))).ToArray(),_animalSpawnSeq);
    public void Restore(Saved saved)
    {
        if(saved==null)return; _mapRoot=saved.MapRoot; _tableIds=saved.Tables; _acc=saved.StreamTimer; _animalSpawnSeq=saved.SpawnSequence;
        _pts.AddRange(saved.Points.Select(p=>new Pt{Type=p.Type,X=p.X,Z=p.Z}));
        foreach(var s in saved.Animals)
        {
            var b=s.Brain; var a=BuildAnimal(b.AnimalId,new(b.Position[0],b.Position[1],b.Position[2]),b.Seed);
            if(a==null)continue; a.Restore(b,Tick);if(s.Point>=0)_live[s.Point]=a;
            _server.Animals.ServerPublish(new(a.NetId),U(a.GlobalPosition),a.Yaw,a.Dead?(byte)AnimalNetAnim.Dead:a.NetAnim,Tick+1);
        }
    }
    public bool Console(ushort owner,string command)
    {
        var tokens=command.Trim().Split(' ',2,StringSplitOptions.RemoveEmptyEntries);
        if(tokens.Length==0||tokens[0].ToLowerInvariant() is not ("spawnanimal" or "animal"))return false;
        void Echo(string text)=>_emit("console",new{text});
        if(!_server.Transactions.AllowCheats) Echo("console commands are disabled on this server");
        else if(!GeometryReady) Echo("no animal field in this world (load a map first)");
        else if(!_server.Players.TryGetByOwner(owner,out var player)) Echo("no player to spawn near");
        else SpawnConsole(tokens.Length>1?tokens[1]:"",G(player.Pos),Echo);
        return true;
    }
}
