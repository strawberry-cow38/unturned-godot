using System.Text.Json;
using UnityEngine;
using UnturnedGodot.Net;
using static UnturnedBevy.BridgeHost;
using Q=System.Numerics.Quaternion;
using V=System.Numerics.Vector3;
namespace UnturnedBevy;

/// <summary>Original VehicleNetSync.OnAttachTow/OnDetachTow choke point plus
/// Vehicle.AttachTow/DetachTow/UpdateTow's relationship rules. Rapier applies
/// the pull-only spring to the real bodies; this owns who is roped, the rest
/// length and the snap. Source WorldSave never persisted ropes.</summary>
public sealed class VehicleTowServices
{
    public const float RopeReach=9f, TowRestMin=2f, TowAttachReach=4.5f, TowBreakLen=7.5f;
    readonly NetWorldServer _server;
    readonly MountedWeaponCatalog _catalog;
    readonly Func<ushort> _localPlayer;
    // tower -> (towed, rest length). Source Vehicle.Towing/_towRestLen.
    readonly Dictionary<uint,(uint Towed,float RestLen)> _ropes=new();
    public VehicleTowServices(NetWorldServer server,Catalog catalog,Func<ushort> localPlayer)
    {
        _server=server;_catalog=catalog.Mounted;_localPlayer=localPlayer;
        server.Commands.Register<AttachTowCommand>(ReplicationIds.CommandAttachTow,AttachTowCommand.TryRead,OnAttachTow,
            (sender,cmd)=>cmd.TowerNetId!=0&&cmd.TowedNetId!=0&&cmd.TowerNetId!=cmd.TowedNetId);
        server.Commands.Register<DetachTowCommand>(ReplicationIds.CommandDetachTow,DetachTowCommand.TryRead,OnDetachTow,
            (sender,cmd)=>cmd.NetId!=0);
    }
    JsonElement? Spec(byte type)=>_catalog.For(type)?.Source.GetProperty("spec");
    /// <summary>Vehicle.Build: bumper-height centre of the front (-Z) or rear
    /// (+Z) box face, nudged 0.15 m outside the hull.</summary>
    Vector3 TowLocal(byte type,bool rear)
    {
        if(Spec(type) is not {} spec)return Vector3.zero;
        var c=Vec(spec,"BoxCenter");var s=Vec(spec,"BoxSize");
        return new(c.x,c.y-s.y*.30f,rear?c.z+s.z*.5f+.15f:c.z-s.z*.5f-.15f);
    }
    static Q Rotation(VehicleReplication.VehicleEntity e)=>Q.CreateFromYawPitchRoll(e.YawDegrees*Mathf.Deg2Rad,e.PitchDegrees*Mathf.Deg2Rad,e.RollDegrees*Mathf.Deg2Rad);
    Vector3 TowWorld(VehicleReplication.VehicleEntity e,bool rear)
    {
        var l=TowLocal(e.TypeId,rear);var p=V.Transform(new V(l.x,l.y,l.z),Rotation(e));
        return e.Pos+new Vector3(p.X,p.Y,p.Z);
    }
    bool Roped(uint id)=>_ropes.ContainsKey(id)||_ropes.Values.Any(r=>r.Towed==id);
    // VehicleNetSync.IsRemoteDriven: a driver other than the listen host's own player.
    bool Remote(ushort driver)=>driver!=0&&driver!=_localPlayer();
    // Hybrid only: a coupled trailer is simulated by its cab driver's host
    // (source keeps every trailer on the host), so a remote cab makes its
    // trailer a remote-simulated rope end too.
    bool RemoteDriven(VehicleReplication.VehicleEntity e)=>Remote(e.DriverPlayerId)
        ||_server.Vehicles.TryGetCoupledCab(e.NetIdValue,out var cab)&&Remote(cab.DriverPlayerId);
    // Vehicle._PhysicsProcess returns into StepPlane/StepHeli before UpdateTow,
    // so an aircraft tower never pulls and never snaps its rope.
    bool Aircraft(byte type)=>Spec(type) is {} s&&(Bool(s,"Heli")||Bool(s,"Plane"));
    /// <summary>The host's exact _towRestLen. The replicated value is floored
    /// to 1/16 m and capped at 3.9375 (cosmetic, VehicleReplication), so the
    /// physics owner on the listen host reads this one.</summary>
    public float RestLen(uint tower,float replicated)=>_ropes.TryGetValue(tower,out var r)?r.RestLen:replicated;
    bool WithinReach(ushort sender,VehicleReplication.VehicleEntity e)
        =>_server.Players.TryGetByOwner(sender,out var p)&&(p.Pos-e.Pos).magnitude<=RopeReach;
    bool Coupled(uint a,uint b)=>_server.Vehicles.TryGet(a,out var x)&&_server.Vehicles.TryGet(b,out var y)
        &&(x.CoupledTrailerNetId==b||y.CoupledTrailerNetId==a);
    void OnAttachTow(ushort sender,AttachTowCommand cmd)
    {
        if(!_server.Vehicles.TryGet(cmd.TowerNetId,out var tower)||!_server.Vehicles.TryGet(cmd.TowedNetId,out var towed))return;
        if(RemoteDriven(tower)||RemoteDriven(towed))return;
        if(Roped(tower.NetIdValue)||Roped(towed.NetIdValue))return;
        if(!WithinReach(sender,tower)||!WithinReach(sender,towed))return;
        // Vehicle.AttachTow's own gate: wrecks, semi-hitched pairs, tow-point gap.
        if(tower.Exploded||towed.Exploded||Coupled(tower.NetIdValue,towed.NetIdValue))return;
        if(TowLocal(tower.TypeId,true)==Vector3.zero||TowLocal(towed.TypeId,false)==Vector3.zero)return;
        float gap=(TowWorld(tower,true)-TowWorld(towed,false)).magnitude;
        if(gap>TowAttachReach)return;
        _ropes[tower.NetIdValue]=(towed.NetIdValue,Mathf.Clamp(gap,TowRestMin,TowAttachReach));
        Publish();
    }
    void OnDetachTow(ushort sender,DetachTowCommand cmd)
    {
        if(!_server.Vehicles.TryGet(cmd.NetId,out var node)||!WithinReach(sender,node))return;
        Detach(cmd.NetId);
    }
    /// <summary>Vehicle.DetachTow: either end resolves to the tower.</summary>
    void Detach(uint id)
    {
        uint tower=_ropes.ContainsKey(id)?id:_ropes.FirstOrDefault(r=>r.Value.Towed==id).Key;
        if(tower==0||!_ropes.Remove(tower))return;
        Publish(tower);
    }
    void Publish(uint cleared=0)
    {
        long tick=_server.Session.CurrentTick+1;
        if(cleared!=0)_server.Vehicles.ServerPublishTow(new NetId(cleared),0,0,tick);
        foreach(var (tower,rope) in _ropes)_server.Vehicles.ServerPublishTow(new NetId(tower),rope.Towed,rope.RestLen,tick);
    }
    /// <summary>UpdateTow's snap and the wreck/despawn drops, judged from the
    /// accepted poses the physics owner publishes.</summary>
    public void Step()
    {
        foreach(var (towerId,rope) in _ropes.ToArray())
        {
            // VehicleNetSync enter guard: a REMOTE driver boarding either end
            // drops the rope (the host's spring would fight the adopted pose).
            if(!_server.Vehicles.TryGet(towerId,out var tower)||!_server.Vehicles.TryGet(rope.Towed,out var towed)
                ||tower.Exploded||towed.Exploded||RemoteDriven(tower)||RemoteDriven(towed)
                ||!Aircraft(tower.TypeId)&&(TowWorld(tower,true)-TowWorld(towed,false)).magnitude>TowBreakLen)
            {
                _ropes.Remove(towerId);
                if(_server.Vehicles.TryGet(towerId,out _))Publish(towerId);
            }
        }
    }
}
