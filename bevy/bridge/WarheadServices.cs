using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Original PlayerController.StepBullets impact blast and StickProjectile,
/// fed only by authoritative accepted-round contacts. Cosmetic bullets never enter here.</summary>
public sealed class WarheadServices
{
    public sealed record Payload(ushort ItemId,string Gun,float Radius,float PlayerDamage,float ZombieDamage,float VehicleDamage,ushort StickItemId,bool Npc=false,uint? MountedLaunchTick=null);
    public sealed record Pose(float[] Direction,float[] Normal,float[] Euler);
    public sealed record Saved(int WorldItemIndex,ushort ItemId,Pose Pose);
    readonly NetWorldServer _server;
    readonly CosmeticBulletServices _cosmetic;
    readonly Action<string,object> _emit;
    readonly Dictionary<uint,Pose> _stuck=new();
    readonly Queue<CombatBulletImpact> _impacts=new();
    public WarheadServices(NetWorldServer server,WeaponServices weapons,CosmeticBulletServices cosmetic,Action<string,object> emit)
    {
        _server=server;_emit=emit;_cosmetic=cosmetic;
        server.Combat.CaptureBulletContext=owner=>weapons.CaptureWarhead(owner);
        server.Combat.BulletImpact=Impact;
        server.Combat.ContinueBullet=Continue;
        var previousRay=server.Combat.WorldRayDetailed;
        server.Combat.BulletRayDetailed=(Vector3 from,Vector3 to,out CombatWorldHit hit)=>{
            bool found=previousRay(from,to,out hit);
            if(cosmetic.RayWater(from,to,out var point,out var normal)
                &&(!found||(point-from).sqrMagnitude<(hit.Point-from).sqrMagnitude))
            {hit=new(point,normal:normal,water:true);return true;}
            return found;
        };
    }
    void Impact(CombatBulletImpact hit)
    {
        // ServerCombat is the sole ImpactFx publisher. It queues one reliable event
        // immediately before this callback for every player/zombie/world contact.
        // Preserve that order instead of matching nearby quantized positions.
        _impacts.Enqueue(hit);
        if(hit.Context is not Payload payload)return;
        if(payload.Radius>0)
        {
            _server.Combat.ServerDetonateWeaponAt(hit.Point,payload.Radius,payload.PlayerDamage,
                payload.ZombieDamage,payload.VehicleDamage,hit.Shooter,hit.Tick,payload.Npc);
            _emit("bulletExplosion",new{playerId=hit.Shooter,sequence=hit.Sequence,pellet=hit.Pellet,
                itemId=payload.ItemId,gun=payload.Gun,position=Array(hit.Point),radius=payload.Radius});
            if(payload.MountedLaunchTick is uint launch)
            {
                var fact=new MountedFlightEvent{Kind=MountedFlightKind.Blast,LaunchTick=launch,Tick=(uint)hit.Tick,
                    Sequence=hit.Sequence,Radius=payload.Radius,
                    Shot=new(){PlayerId=hit.Shooter,Gun=payload.Gun,Origin=hit.Point,Dir=hit.Velocity.normalized}};
                _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventMountedFlight,fact.Write));
            }
        }
    }
    public CombatBulletImpact? TakeImpact() => _impacts.TryDequeue(out var hit)?hit:null;
    CombatBulletContinuation? Continue(CombatBulletImpact hit)
    {
        if(hit.Context is not Payload payload)return null;
        // Exact source order: direct impact/damage, warhead, piercing, final stick.
        // Thin metadata is opt-in; no production prop is guessed to be penetrable.
        if(payload.Radius<=0&&hit.Pierced<CosmeticRules.MaxPierce&&(hit.Thin||hit.Water))
        {
            float velocity=hit.Thin?CosmeticRules.ThinVelKeep:CosmeticRules.WaterVelKeep;
            float damage=hit.Thin?CosmeticRules.ThinDmgKeep:CosmeticRules.WaterDmgKeep;
            _emit("bulletPierced",new{playerId=hit.Shooter,sequence=hit.Sequence,pellet=hit.Pellet,
                position=Array(hit.Point),normal=hit.Normal.HasValue?Array(hit.Normal.Value):null,
                water=hit.Water,thin=hit.Thin,pierced=hit.Pierced+1,velocityKeep=velocity,damageKeep=damage});
            return new(velocity,damage,CosmeticRules.PierceExit);
        }
        if(payload.MountedLaunchTick is uint launch)_cosmetic.RetireMountedAt(launch,hit.Sequence,hit.Point);
        Stick(hit,payload);return null;
    }
    void Stick(CombatBulletImpact hit,Payload payload)
    {
        // The old approximate core actor ray has no surface normal. Do not invent an
        // attachment pose. Every Bevy triangle and current animal/zombie capsule supplies it.
        if(payload.StickItemId==0||hit.Normal is not Vector3 normal)return;
        var position=hit.Point+normal*.05f;
        var item=_server.WorldItems.ServerSpawn(_server.Ids.Mint(),new Item(payload.StickItemId,1,100),position,hit.Tick);
        _server.WorldItems.ServerSettle(item.NetIdValue,item.Pos,hit.Tick);
        var direction=hit.Velocity.sqrMagnitude>.001f?hit.Velocity.normalized:-normal;
        // Source LookAt targets POINT+direction, while the item is offset by normal*.05.
        // Nearly vertical shots retain the original randomized spawn rotation instead.
        float[] look=null,euler=null;
        if(MathF.Abs(Vector3.Dot(direction,Vector3.up))<.98f)look=Array((hit.Point+direction-position).normalized);
        else {var e=WarheadRules.DropEuler();euler=new[]{e.X,e.Y,e.Z};}
        var pose=new Pose(look,Array(normal),euler);_stuck[item.NetIdValue]=pose;
        _emit("projectileStuck",new{netId=item.NetIdValue,itemId=payload.StickItemId,position=Array(item.Pos),
            stuckDirection=pose.Direction,stuckNormal=pose.Normal,stuckEuler=pose.Euler});
    }
    public Pose PoseOf(uint id)=>_stuck.GetValueOrDefault(id);
    public int LivePoseCount=>_stuck.Count;
    public void Prune()
    {
        foreach(uint id in _stuck.Keys.ToArray())
            if(!_server.WorldItems.TryGet(id,out _))_stuck.Remove(id);
    }
    public Saved[] Capture()
    {
        return _server.WorldItems.All.Select((item,index)=>(item,index))
            .Where(p=>_stuck.ContainsKey(p.item.NetIdValue))
            .Select(p=>new Saved(p.index,p.item.ItemId,_stuck[p.item.NetIdValue])).ToArray();
    }
    public void Restore(Saved[] saved)
    {
        _stuck.Clear();if(saved==null)return;
        // WorldSave restores its world-item list in enumeration order, with fresh NetIds.
        // Store that ordinal explicitly; do not bind equal arrows by a rounded position.
        var items=_server.WorldItems.All.ToArray();
        foreach(var row in saved)
        {
            if(row.WorldItemIndex<0||row.WorldItemIndex>=items.Length)continue;
            var item=items[row.WorldItemIndex];if(item.ItemId!=row.ItemId)continue;
            _stuck[item.NetIdValue]=row.Pose;
            _server.WorldItems.ServerSettle(item.NetIdValue,item.Pos,_server.Session.CurrentTick);
        }
    }
}
