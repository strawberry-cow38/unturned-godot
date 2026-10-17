using System.Text.Json;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Net;
using UVector=UnityEngine.Vector3;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Current source chunk/flow AI. Native HOT bodies are the only physics
/// solve; WARM/COLD records remain source data and never acquire native puppets.</summary>
public sealed partial class ZombieServices
{
    readonly NetWorldServer _server;
    readonly WorldGeometry _geometry;
    readonly Action<string,object> _emit;
    readonly Dictionary<uint,ZombieBodyState> _bodies=new();
    string _mapRoot;
    float _loadX,_loadZ;
    public Func<float> RainIntensity;
    /// <summary>The source builds the horde as <c>ZombieChunkField{Player=player}</c> for the SP/listen player
    /// only (WorldBuilder.cs:2370/2869): it streams and sights on that one player, and the dedicated server
    /// builds none. This is that player's id on the host (0 = none yet). Row 15A source parity.</summary>
    public Func<ushort> Owner;
    ushort OwnerId=>Owner?.Invoke()??0;
    /// <summary>Whether a player takes part in this horde: the listen owner, or 0 for an anonymous source
    /// (NPC rounds, world noise). A remote player's shots, swings and gunfire never reach it.</summary>
    public bool Participant(ushort player)=>player==0||player==OwnerId;
    /// <summary>The source's <c>else if (Player != null) _anchors.Add(Player.GlobalPosition);</c>.</summary>
    bool OwnerAnchor(out Vector3 position)
    {
        position=default;
        if(OwnerId==0||!_server.Players.TryGetByOwner(OwnerId,out var owner))return false;
        position=G(owner.Pos);return true;
    }
    long Tick=>_server.Session.CurrentTick;
    static UVector U(Vector3 v)=>new(v.X,v.Y,v.Z);
    static Vector3 G(UVector v)=>new(v.x,v.y,v.z);
    public Func<float,float,float> SourceGround;
    public Func<float,float,byte> SourceLayer;
    float SampleGround(float x,float z)=>(SourceGround ?? _geometry.GroundHeight)(x-_loadX,z-_loadZ);
    byte SampleLayer(float x,float z)=>(SourceLayer ?? _geometry.DominantLayer)(x-_loadX,z-_loadZ);
    public ZombieServices(NetWorldServer server,WorldGeometry geometry,Action<string,object> emit)
    {
        _server=server;_geometry=geometry;_emit=emit;ZombieTables.Reset();
        var previousRay=server.Combat.WorldRayDetailed;
        server.Combat.WorldRayDetailed=(UVector from,UVector to,out CombatWorldHit hit)=>Ray(previousRay,from,to,out hit);
        var previousHit=server.Combat.DamageBody;
        server.Combat.DamageBody=h=>{
            if(h.Hit.ZombieNetId!=0)Damage(h.Hit.ZombieNetId,h.Damage,h.Hit.Point,h.Direction,h.Hit.Point-h.Direction*2,h.Attacker,h.Melee?"melee":"bullet");
            else previousHit?.Invoke(h);
        };
        var previousBlast=server.Combat.DamageHostBlast;
        server.Combat.DamageHostBlast=b=>{previousBlast?.Invoke(b);Blast(b);};
        var previousMelee=server.Combat.DamageAnimalMelee;
        // Source ApplyMeleeHit breaks its zombie foreach, then enters a separate
        // animal foreach (PlayerController.cs 5507-5530). Both category cones run.
        server.Combat.DamageAnimalMelee=(owner,origin,direction,reach,damage,tick)=>
            Melee(owner,origin,direction,reach,damage)|(previousMelee?.Invoke(owner,origin,direction,reach,damage,tick)??false);
    }

    public object Register(JsonElement r,double originX,double originZ)
    {
        if(!_geometry.HasTerrain)throw new InvalidOperationException("Register terrain geometry before zombies");
        string path=Path.GetFullPath(Text(r,"mapRoot"));
        if(_mapRoot!=null)
        {
            if(_mapRoot!=path)throw new InvalidOperationException("A different zombie field requires init");
            return Summary();
        }
        _mapRoot=path;
        if(r.TryGetProperty("points",out var points))
        {
            ZombieTables.Load(path);LoadGenerated(points.EnumerateArray().Select(p=>G(Vec(p))).ToArray());
        }
        else
        {
            _loadX=(float)originX;_loadZ=(float)originZ;
            try{LoadFromPei(path);}finally{_loadX=_loadZ=0;}
            TranslateWorld(new((float)-originX,0,(float)-originZ));
        }
        return Summary();
    }
    public object Summary()=>new{chunks=_chunks.Count,points=_chunks.Values.Sum(c=>c.SpawnPts.Count),
        population=_chunks.Values.Sum(c=>c.Population),bodies=_bodies.Count,tierChunks=TierChunks,tierZombies=TierZombies,
        alert=HasAlert,blockedCells=_field.BlockedCells,fieldCells=_field.CellCount};
    public void Noise(UVector position,float radius)
    {
        if(!float.IsFinite(radius)||radius<0)throw new ArgumentException("Noise radius must be finite and nonnegative");
        radius=UnturnedSim.NoiseMasking.Carry(radius,RainIntensity?.Invoke()??0);
        if(radius>0)HearNoise(G(position),radius);
    }
    ZombieBodyState BuildBody(byte table,uint outfit,Vector3 position)
    {
        var b=new ZombieBodyState{NetId=_server.Ids.Mint().Value,Table=table,Outfit=outfit,GlobalPosition=position,
            Health=ZombieRules.Health,Wardrobe=ZombieRules.Roll(table,outfit)};
        _bodies.Add(b.NetId,b);
        _server.Zombies.ServerSpawn(new(b.NetId),0,U(position),Tick+1);
        return b;
    }
    void Retire(ZombieBodyState b)
    {b.Freed=true;_bodies.Remove(b.NetId);_server.Zombies.ServerRemove(new(b.NetId),Tick);}
    void Freeze(Chunk c)
    {
        // Source drops the Live reference here. Explicitly release its native
        // bodies too, including a teleport directly from HOT to FROZEN.
        foreach(var z in c.Live)if(z.Body!=null&&!z.Body.Dead)Retire(z.Body);
        c.Live=null;
    }
    public void Step()
    {
        if(!_geometry.HasTerrain||_mapRoot==null)return;
        SourceStep(SDG.Unturned.SimClock.FixedDelta);
        foreach(var b in _bodies.Values.ToArray())
        {
            if(b.Dead&&b.RetireTick<=Tick){Retire(b);continue;}
            if(Tick%4==0)_server.Zombies.ServerPublish(new(b.NetId),U(b.GlobalPosition),b.Yaw,
                b.Dead?(byte)ZombieNetAnim.Dead:b.Velocity.LengthSquared()>.04f?(byte)ZombieNetAnim.Walk:(byte)ZombieNetAnim.Idle,Tick);
        }
    }
    public object Publish(JsonElement r)
    {
        var updates=new List<(ZombieBodyState Body,UVector Position,UVector Velocity,float Yaw,JsonElement? Motion)>();
        foreach(var v in r.GetProperty("bodies").EnumerateArray())
        {
            uint id=v.GetProperty("netId").GetUInt32();
            if(!_bodies.TryGetValue(id,out var body)||body.Dead)continue;
            var pos=Vec(v,"position");var vel=Vec(v,"velocity");float yaw=Float(v,"yaw",body.Yaw);
            if(!float.IsFinite(yaw))throw new ArgumentException("Zombie yaw must be finite");
            JsonElement? savedMotion=null;
            if(v.TryGetProperty("motion",out var motion)&&motion.ValueKind!=JsonValueKind.Null)
            {
                _=Vec(motion,"windowPosition");
                foreach(string key in new[]{"windowTime","escapeTime"})
                    if(!float.IsFinite(Float(motion,key)))throw new ArgumentException($"Zombie {key} must be finite");
                savedMotion=motion.Clone();
            }
            updates.Add((body,pos,vel,yaw,savedMotion));
        }
        foreach(var u in updates)
        {u.Body.GlobalPosition=G(u.Position);u.Body.Velocity=G(u.Velocity);u.Body.Yaw=u.Yaw;if(u.Motion.HasValue)u.Body.Motion=u.Motion;}
        return new{published=updates.Count};
    }
    public object[] Snapshots(NetWorldClient client)=>Snapshots(client.Zombies);
    public object[] Snapshots(ZombieReplication zombies)=>zombies.All.Select(e=>{
        _bodies.TryGetValue(e.NetIdValue,out var b);var outfit=b?.Wardrobe;
        return(object)new{netId=e.NetIdValue,position=Array(e.Pos),yaw=e.YawDegrees,animState=e.AnimState,speciality=e.Speciality,
            hasBody=b!=null,hotBody=b!=null&&!b.Dead,hostPosition=b==null?null:Array(U(b.GlobalPosition)),hostYaw=b?.Yaw,
            velocity=b==null?null:Array(U(b.Velocity)),desiredVelocity=b==null?null:new[]{b.DesiredVel.X,b.DesiredVel.Y},
            health=b?.Health??0,dead=b?.Dead??e.IsDead,table=b?.Table,outfit=b?.Outfit,variant=outfit?.Variant,
            shirtId=outfit?.ShirtId,pantsId=outfit?.PantsId,hatId=outfit?.HatId,gearId=outfit?.GearId,motion=b?.Motion,
            corpseRemainingTicks=b?.Dead==true?Math.Max(0,b.RetireTick-Tick):0,
            deathImpulse=b?.Dead==true?Array(U(b.DeathImpulse)):null};
    }).ToArray();
    public void TranslateWorld(UVector delta)
    {
        var d=G(delta);int cx=(int)(delta.x/ChunkSize),cz=(int)(delta.z/ChunkSize);
        var chunks=_chunks.Values.ToArray();_chunks.Clear();
        foreach(var c in chunks)
        {
            c.Cx+=cx;c.Cz+=cz;c.Center+=d;
            for(int i=0;i<c.SpawnPts.Count;i++)c.SpawnPts[i]+=d;
            if(c.Live!=null)foreach(var z in c.Live){z.Home+=d;z.Pos+=d;}
            _chunks.Add((c.Cx,c.Cz),c);
        }
        foreach(var b in _bodies.Values){b.GlobalPosition+=d;if(b.Motion.HasValue)b.Motion=CoordinateJson.Window(b.Motion.Value,delta);}
        for(int i=0;i<_anchors.Count;i++)_anchors[i]+=d;
        _fieldAnchor+=d;_alertPos+=d;
        if(DebugAnchor.HasValue)DebugAnchor+=d;
        int fx=(int)(delta.x/ZombieFlowField.Cell),fz=(int)(delta.z/ZombieFlowField.Cell);
        var cached=_walkCache.ToArray();_walkCache.Clear();foreach(var p in cached)_walkCache[(p.Key.Item1+fx,p.Key.Item2+fz)]=p.Value;
        if(_fieldCell.Item1!=int.MinValue)_fieldCell=(_fieldCell.Item1+fx,_fieldCell.Item2+fz);
        _field.TranslateWorld(d);
    }
}

public sealed class ZombieBodyState
{
    public uint NetId,Outfit;
    public byte Table;
    public Vector3 GlobalPosition,Velocity,DeathImpulse;
    public Vector2 DesiredVel;
    public float Yaw,Health;
    public bool Dead,Freed;
    public long RetireTick;
    public ZombieRules.Wardrobe Wardrobe;
    public JsonElement? Motion;
}
