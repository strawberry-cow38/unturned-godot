using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using System.Text.Json;
using Vector3 = Godot.Vector3;
using UVector = UnityEngine.Vector3;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>The source bullet picture and its single accepted scatter sample.
/// It can retire/pierce a streak, but never damages or emits an impact/hitmarker;
/// ServerCombat consumes the same directions and owns all gameplay contacts.</summary>
public sealed partial class CosmeticBulletServices
{
    public sealed record Context(float AimAlpha,UVector? MuzzleAnchor,UVector? Origin=null,UVector? Direction=null);
    sealed record Pending(Context Context,long Until);
    sealed class Bullet
    {
        public long Id,ShotId,LaunchTick;
        public ushort Owner,Sequence,ItemId;
        public int Pellet,StepsLeft,Pierced;
        public string Gun;
        public UVector Position,Origin,Velocity;
        public UVector? Anchor;
        public float Gravity,Width,BlastRadius;
        public float[] Color;
        public bool Silenced,Rocket,Mounted;
    }
    sealed record WaterBox(UVector Center,UVector Size);
    readonly NetWorldServer _server;
    readonly WorldGeometry _geometry;
    readonly Action<string,object> _emit;
    readonly Dictionary<(ushort,ushort),Pending> _contexts=new();
    readonly Dictionary<(ushort,ushort),UVector[]> _acceptedDirections=new();
    readonly Dictionary<ushort,(int Shot,long Tick)> _patterns=new();
    readonly List<Bullet> _bullets=new();
    readonly List<WaterBox> _water=new();
    readonly Godot.RandomNumberGenerator _rng=new();
    static long _nextBullet,_nextShot;
    long Tick=>_server.Session.CurrentTick;
    public CosmeticBulletServices(NetWorldServer server,WorldGeometry geometry,WeaponServices weapons,Action<string,object> emit)
    {
        _server=server;_geometry=geometry;_emit=emit;weapons.CosmeticLaunch=Launch;
        server.Combat.ResolveBulletDirections=(owner,command,tick)=>
            _acceptedDirections.Remove((owner,command.Seq),out var directions)?directions:null;
    }
    static Vector3 G(UVector p)=>new(p.x,p.y,p.z);
    static UVector U(Vector3 p)=>new(p.X,p.Y,p.Z);
    static (Vector3 Right,Vector3 Up) LookAxes(Vector3 aim)
    {
        // Godot Basis.LookingAt: Z=-forward, X=up.Cross(Z), Y=Z.Cross(X).
        var right=aim.Cross(Vector3.Up).Normalized();
        if(right.LengthSquared()==0)return (Vector3.Right,Vector3.Up);
        return (right,right.Cross(aim).Normalized());
    }
    public static Context ReadContext(JsonElement args)
    {
        if(args.ValueKind!=JsonValueKind.Object)return new(0,null);
        UVector? origin=args.TryGetProperty("origin",out _)?Vec(args,"origin"):null;
        UVector? direction=args.TryGetProperty("dir",out _)?Vec(args,"dir"):null;
        if(!args.TryGetProperty("visual",out var v)||v.ValueKind!=JsonValueKind.Object)return new(0,null,origin,direction);
        return new(Math.Clamp(Float(v,"aimAlpha"),0,1),
            v.TryGetProperty("muzzleAnchor",out _)?Vec(v,"muzzleAnchor"):null,origin,direction);
    }
    public void Queue(ushort owner,ushort sequence,Context context)
    {
        if(sequence!=0)_contexts[(owner,sequence)]=new(context,Tick+5*NetProtocol.TicksPerSecond);
    }
    public object RegisterGeometry(JsonElement request)
    {
        var next=new List<WaterBox>();
        if(request.TryGetProperty("waterBoxes",out var boxes))foreach(var box in boxes.EnumerateArray())
        {
            var size=Vec(box,"size");if(size.x<=0||size.y<=0||size.z<=0)throw new ArgumentException("Water box dimensions must be positive");
            next.Add(new(Vec(box,"center"),size));
        }
        _water.Clear();_water.AddRange(next);return new{waterBoxes=_water.Count};
    }
    void Launch(ushort owner,FireCommand command,WeaponServices.ShotFlight flight)
    {
        var context=_contexts.Remove((owner,command.Seq),out var queued)?queued.Context:new Context(0,null);
        var facts=flight.Facts;var gun=flight.Gun;
        var pattern=_patterns.GetValueOrDefault(owner);
        if((facts.Tick-pattern.Tick)*SimClock.FixedDelta>=CosmeticRules.PatternResetSeconds)pattern.Shot=0;
        if(gun.PatternClimb>0){pattern.Shot++;pattern.Tick=facts.Tick;_patterns[owner]=pattern;}
        // Burst count now affects damage directions. Only accepted server shots
        // advance it; the old optional visual.patternShot hint is ignored.
        int patternShot=pattern.Shot;
        long shotId=Interlocked.Increment(ref _nextShot);
        var aim=G(context.Direction??command.Dir).Normalized();var origin=context.Origin??command.Origin;int pellet=0;
        var color=GunDef.TracerColor(gun.CaliberName);float[] tint=[color.r,color.g,color.b];
        var launched=new List<object>();
        // Sample the original cone/per-axis algorithm ONCE. Core consumes the
        // same immutable directions immediately after this accepted callback;
        // it still chooses count, origin, speed, range, damage and collision.
        var directions=Directions(gun,aim,context.AimAlpha,facts.RecoilMultiplier,patternShot,Math.Max(1,flight.Pellets)).Select(U).ToArray();
        _acceptedDirections[(owner,command.Seq)]=directions;
        foreach(var direction in directions)
        {
            var bullet=new Bullet{Id=Interlocked.Increment(ref _nextBullet),ShotId=shotId,LaunchTick=facts.Tick,
                Owner=owner,Sequence=command.Seq,ItemId=facts.ItemId,Pellet=pellet++,Gun=facts.Gun,
                Position=origin,Origin=origin,Velocity=direction*facts.MuzzleVelocity,
                Anchor=context.MuzzleAnchor,StepsLeft=Math.Max(1,flight.Steps),Gravity=flight.Gravity,
                Width=CosmeticRules.TracerBaseW*GunDef.TracerScale(gun.CaliberName),Color=tint,
                Silenced=facts.Silenced,Rocket=gun.Action=="Rocket",BlastRadius=gun.BlastRadius};
            _bullets.Add(bullet);launched.Add(new{id=bullet.Id,pellet=bullet.Pellet,velocity=Array(bullet.Velocity)});
        }
        _emit("bulletLaunched",new{shotId,playerId=owner,sequence=command.Seq,itemId=facts.ItemId,gun=facts.Gun,
            origin=Array(origin),direction=Array(U(aim)),acceptedTick=facts.Tick,aimAlpha=context.AimAlpha,patternShot,
            pelletCount=pellet,muzzleVelocity=facts.MuzzleVelocity,gravity=flight.Gravity,steps=flight.Steps,
            silenced=facts.Silenced,tracerWidth=CosmeticRules.TracerBaseW*GunDef.TracerScale(gun.CaliberName),tracerColor=tint,
            rocket=gun.Action=="Rocket",muzzleAnchor=context.MuzzleAnchor.HasValue?Array(context.MuzzleAnchor.Value):null,bullets=launched});
    }
    public void LaunchMounted(ushort owner,ushort sequence,UVector origin,UVector direction,GunDef gun,string gunId,ServerGunProfile profile,float radius)
    {
        var color=GunDef.TracerColor(gun?.CaliberName);float[] tint=[color.r,color.g,color.b];
        var bullet=new Bullet{Id=Interlocked.Increment(ref _nextBullet),ShotId=Interlocked.Increment(ref _nextShot),LaunchTick=Tick,
            Owner=owner,Sequence=sequence,Mounted=true,Gun=gunId,Position=origin,Origin=origin,Velocity=direction*profile.MuzzleVelocity,
            StepsLeft=profile.BallisticSteps,Gravity=0,Width=CosmeticRules.TracerBaseW*GunDef.TracerScale(gun?.CaliberName),
            Color=tint,Rocket=gun?.Action=="Rocket",BlastRadius=radius};
        _bullets.Add(bullet);
    }
    public void Step()
    {
        foreach(var key in _contexts.Where(p=>p.Value.Until<Tick).Select(p=>p.Key).ToArray())_contexts.Remove(key);
        for(int i=_bullets.Count-1;i>=0;i--)
        {
            var b=_bullets[i];
            // Source calls StepBullets before Fire in its physics tick. Newly
            // launched rounds remain at the muzzle until the following tick.
            if(b.LaunchTick>=Tick)continue;
            var next=BallisticsMath.NextPos(b.Position,b.Velocity);
            if(Ray(b.Position,next,out var point,out bool thin,out bool water))
            {
                if(b.Pierced<CosmeticRules.MaxPierce&&b.BlastRadius<=0&&(thin||water))
                {
                    b.Pierced++;var direction=b.Velocity.normalized;
                    b.Velocity*=thin?CosmeticRules.ThinVelKeep:CosmeticRules.WaterVelKeep;
                    b.Position=point+direction*CosmeticRules.PierceExit;
                    if(--b.StepsLeft<=0)Retire(i,"expired");
                    else if(b.Mounted)PublishMounted(new(){Kind=MountedFlightKind.Continue,LaunchTick=(uint)b.LaunchTick,
                        Tick=(uint)Tick,Sequence=b.Sequence,Position=b.Position,Velocity=b.Velocity,
                        RemainingSteps=checked((ushort)b.StepsLeft),Pierced=checked((byte)b.Pierced)});
                    continue;
                }
                b.Position=point;Retire(i,"impact");continue;
            }
            b.Position=next;b.Velocity=BallisticsMath.StepVel(b.Velocity,b.Gravity);
            if(--b.StepsLeft<=0)Retire(i,"expired");
        }
    }
    bool Ray(UVector from,UVector to,out UVector point,out bool thin,out bool water)
    {
        bool found=_geometry.RayCosmetic(from,to,out point,out thin,out water);
        float nearest=found?(point-from).sqrMagnitude:float.PositiveInfinity;
        if(_server.Combat.WorldRayDetailed?.Invoke(from,to,out var host)==true && (host.Point-from).sqrMagnitude<nearest-1e-5f)
        {found=true;point=host.Point;thin=host.Thin;water=host.Water;nearest=(point-from).sqrMagnitude;}
        foreach(var box in _water)
        {
            if(!WaterRay(box,from,to,out var hit,out _)||(hit-from).sqrMagnitude>=nearest)continue;
            found=true;point=hit;thin=false;water=true;nearest=(hit-from).sqrMagnitude;
        }
        return found;
    }
    public bool RayWater(UVector from,UVector to,out UVector point,out UVector normal)
    {
        bool found=false;point=to;normal=default;float nearest=float.PositiveInfinity;
        foreach(var box in _water)
        {
            if(!WaterRay(box,from,to,out var hit,out var n)||(hit-from).sqrMagnitude>=nearest)continue;
            found=true;point=hit;normal=n;nearest=(hit-from).sqrMagnitude;
        }
        return found;
    }
    static bool WaterRay(WaterBox box,UVector from,UVector to,out UVector point,out UVector normal)
    {
        // Godot ray queries default hit_from_inside=false: after piercing the
        // top face a round starts inside the .2m splash slab and passes its back.
        normal=default;var half=box.Size*.5f;var min=box.Center-half;var max=box.Center+half;
        if(from.x>min.x&&from.x<max.x&&from.y>min.y&&from.y<max.y&&from.z>min.z&&from.z<max.z){point=to;return false;}
        float enter=0,exit=1;var dir=to-from;
        for(int axis=0;axis<3;axis++)
        {
            float p=axis==0?from.x:axis==1?from.y:from.z,d=axis==0?dir.x:axis==1?dir.y:dir.z;
            float lo=axis==0?min.x:axis==1?min.y:min.z,hi=axis==0?max.x:axis==1?max.y:max.z;
            if(Math.Abs(d)<1e-8f){if(p<lo||p>hi){point=to;return false;}continue;}
            float a=(lo-p)/d,b=(hi-p)/d;if(a>b)(a,b)=(b,a);
            if(a>=enter){enter=a;float sign=d>0?-1:1;normal=axis==0?new(sign,0,0):axis==1?new(0,sign,0):new(0,0,sign);}
            exit=Math.Min(exit,b);
            if(enter>exit){point=to;return false;}
        }
        // A tangent along a boundary (or a ray leaving it) has no entry face.
        // Like hit_from_inside=false, it must not manufacture a t=0 surface hit.
        if(normal.sqrMagnitude==0){point=to;return false;}
        point=from+dir*enter;return true;
    }
    void Retire(int index,string reason)
    {
        var b=_bullets[index];_bullets.RemoveAt(index);
        if(b.Mounted)PublishMounted(new(){Kind=MountedFlightKind.Retire,LaunchTick=(uint)b.LaunchTick,
            Tick=(uint)Tick,Sequence=b.Sequence,Position=b.Position,Velocity=b.Velocity,
            Reason=reason=="impact"?MountedFlightRetirement.Impact:MountedFlightRetirement.Expired});
        _emit("bulletRetired",new{id=b.Id,shotId=b.ShotId,playerId=b.Owner,reason,position=Array(b.Position),velocity=Array(b.Velocity),acceptedTick=Tick});
    }
    public void RetireMountedAt(uint launchTick,ushort sequence,UVector point)
    {
        for(int i=_bullets.Count-1;i>=0;i--)
        {
            var b=_bullets[i];
            if(!b.Mounted||(uint)b.LaunchTick!=launchTick||b.Sequence!=sequence)continue;
            b.Position=point;Retire(i,"impact");
        }
    }
    void PublishMounted(MountedFlightEvent fact)=>_server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventMountedFlight,fact.Write));
    public object[] Snapshots()=>_bullets.Select(b=>(object)new{id=b.Id,shotId=b.ShotId,playerId=b.Owner,sequence=b.Sequence,
        pellet=b.Pellet,itemId=b.ItemId,gun=b.Gun,origin=Array(b.Origin),position=Array(b.Position),velocity=Array(b.Velocity),
        gravity=b.Gravity,remainingSteps=b.StepsLeft,launchTick=b.LaunchTick,silenced=b.Silenced,tracerWidth=b.Width,
        tracerColor=b.Color,rocket=b.Rocket,pierced=b.Pierced,muzzleAnchor=b.Anchor.HasValue?Array(b.Anchor.Value):null}).ToArray();
    public void TranslateWorld(UVector delta)
    {
        foreach(var b in _bullets){b.Position+=delta;b.Origin+=delta;if(b.Anchor.HasValue)b.Anchor+=delta;}
        foreach(var key in _contexts.Keys.ToArray()){var p=_contexts[key];_contexts[key]=p with{Context=p.Context with{MuzzleAnchor=p.Context.MuzzleAnchor+delta,Origin=p.Context.Origin+delta}};}
        for(int i=0;i<_water.Count;i++)_water[i]=_water[i] with{Center=_water[i].Center+delta};
    }
    public sealed record WaterSaved(float[] Center,float[] Size);
    public WaterSaved[] CaptureWater() => _water.Select(b=>new WaterSaved(Array(b.Center),Array(b.Size))).ToArray();
    public void RestoreWater(WaterSaved[] saved)
    {
        _water.Clear();
        if(saved!=null)foreach(var b in saved)_water.Add(new(new(b.Center[0],b.Center[1],b.Center[2]),new(b.Size[0],b.Size[1],b.Size[2])));
    }
    public static object Metadata()
    {
        string texture=System.IO.File.Exists(Godot.ProjectSettings.GlobalizePath("res://content/tracer.png"))?"tracer.png":"bullet.png";
        var fix=CosmeticRules.RocketMeshFix;
        return new{tracerTexture=texture,maxLength=CosmeticRules.MaxLen,headFraction=CosmeticRules.tHead,segments=CosmeticRules.Segments,
            muzzleAnchorDepth=CosmeticRules.MuzzleAnchorDepth,rocketMesh="rocket_projectile.txt",rocketMeshRotation=new[]{fix.X,fix.Y,fix.Z},
            rocketColor=new[]{.324f,.397f,.331f},rocketRoughness=.75f,rocketSound="projectile_fire.ogg",rocketVolumeDb=2f,rocketUnitSize=12f,rocketMaxDistance=320f};
    }
}
