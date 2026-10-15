using SDG.Unturned;
using System.Text.Json;
using UnturnedGodot;
using UnturnedGodot.Net;
using NVector = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = UnityEngine.Vector3;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Vehicle's original aim/cycle/belt state, on the authoritative host.
/// Clients send look/point and held trigger intent; accepted fleet and seat data
/// determine the gun, muzzle, limits, ammunition and operator on every tick.</summary>
public sealed class MountedWeaponServices
{
    const float Dt=(float)SimClock.FixedDelta, Rad=MathF.PI/180f;
    // Transport guard, not a source fire-rate constant. Refresh each fixed tick;
    // loss of a client must not turn its last held input into an unattended gun.
    const int InputLifetimeTicks=5;
    sealed class State
    {
        public MountedWeaponCatalog.Fleet Spec;
        public VehicleMountState[] Mounts;
        public bool HeadOut;
    }
    sealed record Input(MountedInputCommand Command,long Until,int Seat);
    public sealed record Saved(int Ordinal,byte TypeId,VehicleMountState[] Mounts,bool HeadOut);
    readonly NetWorldServer _server;
    readonly MountedWeaponCatalog _catalog;
    readonly CosmeticBulletServices _cosmetic;
    readonly Dictionary<uint,State> _states=new();
    readonly Dictionary<ushort,Input> _inputs=new();
    readonly Dictionary<ushort,ushort> _sequences=new();
    ushort _shotSequence;
    public MountedWeaponServices(NetWorldServer server,Catalog catalog,CosmeticBulletServices cosmetic)
    {
        _server=server;_catalog=catalog.Mounted;_cosmetic=cosmetic;
        server.MountedInputAllowed=Allowed;
        server.MountedInputRequested=Accept;
        server.Session.PeerDisconnected+=(peer,reason)=>{_inputs.Remove(peer.PlayerId);_sequences.Remove(peer.PlayerId);};
    }
    public void SyncFleet()
    {
        foreach(var id in _states.Keys.ToArray())if(!_server.Vehicles.TryGet(id,out _))
        {_states.Remove(id);_server.VehicleMounts.ServerRemove(id,_server.Session.CurrentTick+1);}
        foreach(var v in _server.Vehicles.All)
        {
            if(_states.TryGetValue(v.NetIdValue,out var old)&&old.Spec.TypeId==v.TypeId)continue;
            var spec=_catalog.For(v.TypeId);if(spec==null)continue;
            _states[v.NetIdValue]=new(){Spec=spec,Mounts=spec.Mounts.Select(m=>new VehicleMountState(0,0,(ushort)m.Belt,0,MountedWeaponCatalog.V(m.GunnerAt)==NVector.Zero)).ToArray()};
            Publish(v.NetIdValue,_states[v.NetIdValue]);
        }
    }
    int SeatOf(uint id,ushort owner)
    {
        for(int i=0;i<_server.Vehicles.SeatsOn(new(id));i++)if(_server.Vehicles.SeatOccupant(new(id),i)==owner)return i;
        return -1;
    }
    public int IndexFor(uint id,int seat,int slot)
    {
        if(!_states.TryGetValue(id,out var state))return -1;
        int n=0;var mounts=state.Spec.Mounts;
        for(int i=0;i<mounts.Length;i++)if(mounts[i].Seat==seat&&n++==slot)return i;
        if(n>0)return -1;
        if(seat==0)for(int i=0;i<mounts.Length;i++)if(mounts[i].DriverFallback&&_server.Vehicles.SeatOccupant(new(id),mounts[i].Seat)==0&&n++==slot)return i;
        return -1;
    }
    bool Allowed(ushort owner,MountedInputCommand command)
    {
        SyncFleet();
        if(!command.IsValid||!_server.CombatState.IsAlive(owner)||!_server.Vehicles.TryGet(command.VehicleNetId,out var vehicle)||vehicle.Exploded)return false;
        int seat=SeatOf(command.VehicleNetId,owner),index=IndexFor(command.VehicleNetId,seat,command.Slot);
        if(seat<0||index<0)return false;
        var mount=_states[command.VehicleNetId].Spec.Mounts[index];
        // A point-laid source mount cannot be snapped past its slew limiter by
        // submitting the look-only path used by the Hind and door guns.
        if(mount.CrosshairAim&&command.Trigger&&command.AimMode!=MountedAimMode.Point)return false;
        return command.AimMode==MountedAimMode.None||command.AimMode==(mount.CrosshairAim?MountedAimMode.Point:MountedAimMode.Look);
    }
    void Accept(ushort owner,MountedInputCommand command)
    {
        if(_sequences.TryGetValue(owner,out ushort last)&&!NetSeq.IsNewer(command.Sequence,last))return;
        _sequences[owner]=command.Sequence;
        _inputs[owner]=new(command,_server.Session.CurrentTick+InputLifetimeTicks,SeatOf(command.VehicleNetId,owner));
    }
    public void Step()
    {
        SyncFleet();long tick=_server.Session.CurrentTick;
        foreach(ushort owner in _sequences.Keys.Where(owner=>!_server.Players.TryGetByOwner(owner,out _)).ToArray()){_sequences.Remove(owner);_inputs.Remove(owner);}
        foreach(var state in _states.Values)for(int i=0;i<state.Mounts.Length;i++)
            if(state.Mounts[i].CooldownSeconds>0)state.Mounts[i]=state.Mounts[i] with{CooldownSeconds=Math.Max(0,state.Mounts[i].CooldownSeconds-Dt)};
        foreach(var pair in _inputs.ToArray())
        {
            var input=pair.Value;var command=input.Command;ushort owner=pair.Key;
            if(input.Until<tick||SeatOf(command.VehicleNetId,owner)!=input.Seat||!Allowed(owner,command))
            {_inputs.Remove(owner);continue;}
            var state=_states[command.VehicleNetId];int index=IndexFor(command.VehicleNetId,input.Seat,command.Slot);
            if(state.Spec.Tank&&input.Seat==1)state.HeadOut=command.HeadOut;
            ApplyAim(command.VehicleNetId,index,command);
            if(command.Trigger)Fire(command.VehicleNetId,index,owner,false,false);
        }
        foreach(var pair in _states)
        {
            if(pair.Value.Spec.Tank&&_server.Vehicles.SeatOccupant(new(pair.Key),1)==0)pair.Value.HeadOut=false;
            Publish(pair.Key,pair.Value);
        }
    }
    // Restore/host intent may run after this tick's snapshot was acknowledged.
    // Stamp the next publication so even a fully idle restored belt is echoed.
    void Publish(uint id,State state)=>_server.VehicleMounts.ServerSet(new(id,state.Mounts,state.HeadOut),_server.Session.CurrentTick+1);
    readonly record struct Frame(NVector Position,Quaternion Rotation)
    {
        public NVector Point(NVector p)=>Position+NVector.Transform(p,Rotation);
        public NVector Local(NVector p)=>NVector.Transform(p-Position,Quaternion.Inverse(Rotation));
        public Frame Child(NVector p,Quaternion q)=>new(Point(p),Quaternion.Normalize(Rotation*q));
    }
    (Frame[] Yaw,Frame[] Pitch,Frame Hull) Frames(uint id)
    {
        var state=_states[id];_server.Vehicles.TryGet(id,out var vehicle);
        var hull=new Frame(N(vehicle.Pos),Quaternion.CreateFromYawPitchRoll(vehicle.YawDegrees*Rad,vehicle.PitchDegrees*Rad,vehicle.RollDegrees*Rad));
        var yaw=new Frame[state.Mounts.Length];var pitch=new Frame[yaw.Length];
        for(int i=0;i<yaw.Length;i++)
        {
            var spec=state.Spec.Mounts[i];var parent=spec.ParentMount>=0?yaw[spec.ParentMount]:hull;
            yaw[i]=parent.Child(MountedWeaponCatalog.V(spec.Pivot),Quaternion.CreateFromAxisAngle(NVector.UnitY,state.Mounts[i].Yaw*Rad));
            pitch[i]=yaw[i].Child(MountedWeaponCatalog.V(spec.PitchOffset),Quaternion.CreateFromAxisAngle(NVector.UnitX,state.Mounts[i].Pitch*Rad));
        }
        return(yaw,pitch,hull);
    }
    void ApplyAim(uint id,int i,MountedInputCommand command)
    {
        if(command.AimMode==MountedAimMode.None)return;
        var state=_states[id];var t=state.Spec.Mounts[i];float yaw=command.Yaw,pitch=command.Pitch,delta=0;
        if(command.AimMode==MountedAimMode.Point)
        {
            var frames=Frames(id);var parent=t.ParentMount>=0?frames.Yaw[t.ParentMount]:frames.Hull;
            var dl=parent.Local(N(command.Target))-MountedWeaponCatalog.V(t.Pivot);
            yaw=MathF.Atan2(-dl.X,-dl.Z)/Rad;
            var dp=NVector.Transform(dl,Quaternion.CreateFromAxisAngle(NVector.UnitY,-yaw*Rad))-MountedWeaponCatalog.V(t.PitchOffset);
            pitch=MathF.Atan2(dp.Y,MathF.Sqrt(dp.X*dp.X+dp.Z*dp.Z))/Rad;delta=Dt;
        }
        float y=t.YawFree?Wrap(yaw):Math.Clamp(yaw,t.YawMin,t.YawMax);
        float pitchMin=t.PitchMin;
        if(i==0&&state.Spec.Tank&&Math.Abs(Wrap(y))>=135)pitchMin*=_catalog.RearDepressionScale;
        float p=Math.Clamp(pitch,pitchMin,t.PitchMax);var current=state.Mounts[i];
        if(delta>0)
        {
            if(t.YawRate>0)y=t.YawFree?Wrap(current.Yaw+Math.Clamp(Wrap(y-current.Yaw),-t.YawRate*delta,t.YawRate*delta)):Move(current.Yaw,y,t.YawRate*delta);
            if(t.PitchRate>0)p=Move(current.Pitch,p,t.PitchRate*delta);
        }
        state.Mounts[i]=current with{Yaw=y,Pitch=p};
    }
    bool Fire(uint id,int index,ushort owner,bool npc,bool infinite)
    {
        var state=_states[id];var value=state.Mounts[index];var def=state.Spec.Mounts[index];
        if(value.CooldownSeconds>0||value.Ammo==0)return false;
        var frame=Frames(id).Pitch[index];var muzzle=U(frame.Point(MountedWeaponCatalog.V(def.Muzzle)));
        var direction=U(NVector.Transform(-NVector.UnitZ,frame.Rotation));var gun=def.Gun;
        float damage=gun?.PlayerDamage??(npc?30:40),vehicle=gun?.VehicleDamage??(npc?40:10),obj=gun?.ObjectDamage??(npc?20:25);
        int steps=gun==null?(npc?125:75):!npc&&gun.BallisticSteps>0?gun.BallisticSteps:Math.Max(1,(int)(gun.Range/2));
        var profile=new ServerGunProfile{AssetName=def.GunId,PlayerDamage=damage,ZombieDamage=damage,AnimalDamage=damage,
            ResourceDamage=damage,VehicleDamage=vehicle,ObjectDamage=obj,MuzzleVelocity=gun?.MuzzleVelocity??120,
            BallisticSteps=steps,GravityMultiplier=0,Pellets=1,FalloffStart=gun?.FalloffStart??0,FalloffEnd=gun?.FalloffEnd??0,FalloffMin=gun?.FalloffMin??1};
        var context=new WarheadServices.Payload(0,def.GunId,gun?.BlastRadius??0,gun?.BlastPlayerDamage??0,
            gun?.BlastZombieDamage??0,gun?.BlastVehicleDamage??0,gun==null?(ushort)0:WarheadRules.StickAmmoFor(gun),npc);
        ushort sequence=++_shotSequence;
        if(!_server.Combat.ServerLaunchMounted(owner,sequence,muzzle,direction,profile,context,npc))return false;
        state.Mounts[index]=value with{Ammo=(ushort)(infinite?value.Ammo:value.Ammo-1),CooldownSeconds=def.Cycle};
        _cosmetic.LaunchMounted(owner,sequence,muzzle,direction,gun,def.GunId,profile,context.Radius);
        var shot=new MountedShotEvent{VehicleNetId=id,Mount=(byte)index,RemainingAmmo=state.Mounts[index].Ammo,
            Tick=(uint)_server.Session.CurrentTick,Sequence=sequence,Npc=npc,
            Shot=new PlayerFiredEvent{PlayerId=owner,Origin=muzzle,Dir=direction,Gun=def.GunId}};
        _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventMountedShot,shot.Write));
        return true;
    }
    /// <summary>Explicit trusted-host AI adapter. Remote sessions cannot dispatch
    /// this operation. Infinite ammunition is a property of this AI shot, never of
    /// a vehicle a human may subsequently board.</summary>
    public object HostInput(JsonElement request)
    {
        SyncFleet();uint id=checked((uint)Int(request,"netId"));int index=Int(request,"index",-1);
        if(!_states.TryGetValue(id,out var state)||index<0||index>=state.Mounts.Length||!_server.Vehicles.TryGet(id,out var v)||v.Exploded)return new{accepted=false};
        var mount=state.Spec.Mounts[index];
        if(request.TryGetProperty("crewAlive",out var crew))state.Mounts[index]=state.Mounts[index] with{CrewAlive=crew.GetBoolean()};
        bool free=_server.Vehicles.SeatOccupant(new(id),mount.Seat)==0;
        if(free&&state.Mounts[index].CrewAlive)
        {
            var command=ReadInput(request,true);
            if(!command.IsValid)throw new ArgumentException("Invalid trusted mount aim");
            ApplyAim(id,index,command);
            if(command.Trigger)Fire(id,index,0,true,Bool(request,"infinite",true));
        }
        Publish(id,state);return new{accepted=free&&state.Mounts[index].CrewAlive};
    }
    public Saved[] Capture()
    {
        SyncFleet();return _server.Vehicles.All.Select((v,i)=>(v,i)).Where(p=>_states.ContainsKey(p.v.NetIdValue))
            .Select(p=>new Saved(p.i,p.v.TypeId,_states[p.v.NetIdValue].Mounts.ToArray(),_states[p.v.NetIdValue].HeadOut)).ToArray();
    }
    public void Restore(Saved[] saved)
    {
        SyncFleet();var fleet=_server.Vehicles.All.ToArray();
        foreach(var row in saved??[])
        {
            if(row.Ordinal<0||row.Ordinal>=fleet.Length||fleet[row.Ordinal].TypeId!=row.TypeId||!_states.TryGetValue(fleet[row.Ordinal].NetIdValue,out var state)
                ||row.Mounts==null||row.Mounts.Length!=state.Mounts.Length||row.Mounts.Any(m=>!m.IsValid))continue;
            for(int i=0;i<row.Mounts.Length;i++)state.Mounts[i]=row.Mounts[i] with{Ammo=(ushort)Math.Min(row.Mounts[i].Ammo,state.Spec.Mounts[i].Belt)};
            state.HeadOut=row.HeadOut;Publish(fleet[row.Ordinal].NetIdValue,state);
        }
    }
    public void TranslateWorld(Vector3 delta)
    {
        foreach(var key in _inputs.Keys.ToArray()){var input=_inputs[key];var command=input.Command;command.Target+=delta;_inputs[key]=input with{Command=command};}
    }
    public static MountedInputCommand ReadInput(JsonElement args,bool trusted=false)
    {
        if(!trusted)foreach(string field in new[]{"ammo","infinite","npc","gun","muzzle","direction","damage","crewAlive"})
            if(args.TryGetProperty(field,out _))throw new ArgumentException($"{field} is authority-owned mounted state");
        var mode=Text(args,"aimMode","none");if(mode is not ("none" or "look" or "point"))throw new ArgumentException("Unknown mount aimMode");
        return new(){VehicleNetId=checked((uint)Int(args,"netId")),Slot=checked((byte)Int(args,"slot")),
            AimMode=mode=="look"?MountedAimMode.Look:mode=="point"?MountedAimMode.Point:MountedAimMode.None,
            Yaw=Float(args,"yaw"),Pitch=Float(args,"pitch"),Target=Vec(args,"target"),Trigger=Bool(args,"trigger"),HeadOut=Bool(args,"headOut")};
    }
    static float Wrap(float v)=>((v+180)%360+360)%360-180;
    static float Move(float from,float to,float amount)=>from+Math.Clamp(to-from,-amount,amount);
    static NVector N(Vector3 v)=>new(v.x,v.y,v.z);
    static Vector3 U(NVector v)=>new(v.X,v.Y,v.Z);
}
