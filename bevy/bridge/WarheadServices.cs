using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Original PlayerController.StepBullets impact blast and StickProjectile,
/// fed only by authoritative accepted-round contacts. Cosmetic bullets never enter here.</summary>
public sealed class WarheadServices
{
    public sealed record Payload(ushort ItemId,string Gun,float Radius,float PlayerDamage,float ZombieDamage,float VehicleDamage,ushort StickItemId);
    public sealed record Pose(float[] Direction,float[] Normal,float[] Euler);
    public sealed record Saved(int WorldItemIndex,ushort ItemId,Pose Pose);
    readonly NetWorldServer _server;
    readonly Action<string,object> _emit;
    readonly Dictionary<uint,Pose> _stuck=new();
    public WarheadServices(NetWorldServer server,WeaponServices weapons,Action<string,object> emit)
    {
        _server=server;_emit=emit;
        server.Combat.CaptureBulletContext=owner=>weapons.CaptureWarhead(owner);
        server.Combat.BulletImpact=Impact;
    }
    void Impact(CombatBulletImpact hit)
    {
        if(hit.Context is not Payload payload)return;
        if(payload.Radius>0)
        {
            _server.Combat.ServerDetonateWeaponAt(hit.Point,payload.Radius,payload.PlayerDamage,
                payload.ZombieDamage,payload.VehicleDamage,hit.Shooter,hit.Tick);
            _emit("bulletExplosion",new{playerId=hit.Shooter,sequence=hit.Sequence,pellet=hit.Pellet,
                itemId=payload.ItemId,gun=payload.Gun,position=Array(hit.Point),radius=payload.Radius});
        }
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
