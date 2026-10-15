using SDG.Unturned;
using UnturnedGodot.Net;
using UVector=UnityEngine.Vector3;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

public sealed partial class ZombieServices
{
    bool Ray(CombatWorldRayDetailed previous,UVector from,UVector to,out CombatWorldHit hit)
    {
        bool found=previous(from,to,out hit);
        var direction=to-from;float length=direction.magnitude;
        if(length<=0)return found;
        direction/=length;float nearest=found?(hit.Point-from).magnitude:length;
        foreach(var b in _bodies.Values)
        {
            if(b.Dead)continue;
            float distance=ZombieCombat.RayCapsule(from,direction,U(b.GlobalPosition),.4f,1.8f);
            if(distance<0||distance>nearest)continue;
            var point=from+direction*distance;
            nearest=distance;hit=new(point,zombieNetId:b.NetId,
                normal:ZombieCombat.CapsuleNormal(point,U(b.GlobalPosition),.4f,1.8f));found=true;
        }
        return found;
    }
    void Damage(uint id,float amount,UVector point,UVector direction,UVector from,ushort attacker,string source)
    {
        if(amount<=0||!_bodies.TryGetValue(id,out var b)||b.Dead)return;
        // ZombieBody.Damage/Die: the current source has one 100 HP pool, no
        // speciality multipliers, attack loop, loot, or experience award.
        b.Health-=amount;
        if(b.Health<=0)
        {
            b.Dead=true;b.DesiredVel=Godot.Vector2.Zero;
            b.DeathImpulse=(b.GlobalPosition-G(from)).Normalized()*6+Godot.Vector3.Up*2;
            b.RetireTick=Tick+(long)Math.Ceiling(ZombieRules.CorpseSeconds/SimClock.FixedDelta);
            _server.Zombies.ServerSetAnim(new(id),ZombieNetAnim.Dead,Tick);
            if(_server.CombatState.TryGet(attacker,out var owner))
            {owner.Kills++;_server.CombatState.MarkDirty(owner,Tick);}
            _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventZombieDied,new ZombieDiedEvent{NetId=id,Killer=attacker}.Write));
            _emit("zombieDied",new{netId=id,position=Array(U(b.GlobalPosition)),impulse=Array(U(b.DeathImpulse)),corpseTicks=b.RetireTick-Tick});
        }
        _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventZombieHit,new ZombieHitEvent{NetId=id,Damage=amount,Shooter=attacker}.Write));
        _emit("zombieHit",new{netId=id,damage=amount,point=Array(point),direction=Array(direction),killed=b.Dead,health=b.Health,attacker,source,impactEmitted=source=="bullet",hitConfirmedEmitted=false});
    }
    bool Melee(ushort owner,UVector origin,UVector direction,float reach,float damage)
    {
        if(_geometry.RayDetailed(origin,origin+direction*reach,out var blocker)
            &&(blocker.ResourceIndex>=0||blocker.DestructibleIndex>=0||blocker.VehicleNetId!=0||blocker.DeployableNetId!=0))return false;
        foreach(var b in _bodies.Values)
        {
            if(b.Dead)continue;var point=U(b.GlobalPosition)+new UVector(0,1,0);var to=point-origin;
            if(to.magnitude>=reach||UVector.Dot(to.normalized,direction)<=.3f)continue;
            Damage(b.NetId,damage,point,direction,origin,owner,"melee");return true;
        }
        return false;
    }
    void Blast(CombatBlast blast)
    {
        foreach(var b in _bodies.Values.ToArray())
        {
            if(b.Dead)continue;var position=U(b.GlobalPosition);float range=(position-blast.Position).magnitude;
            if(range>blast.Radius||_geometry.BlastBlocked(blast.Position,position))continue;
            Damage(b.NetId,ExplosionMath.Linear(blast.AnimalDamage,range,blast.Radius),position,
                (position-blast.Position).normalized,blast.Position,blast.Attacker,"blast");
        }
    }
}
