using UnturnedGodot.Net;
using NVector=System.Numerics.Vector3;
using Vector3=UnityEngine.Vector3;

namespace UnturnedBevy;

public sealed partial class MountedWeaponServices
{
    /// <summary>Opaque, synchronous source TryTurretFire result. The original
    /// brain samples its spread only after this readiness check; completing it
    /// launches the captured muzzle/profile, never a client-supplied gun.</summary>
    public sealed class NpcShot
    {
        internal MountedWeaponServices Issuer;internal uint Id;internal int Index;internal long Tick;internal VehicleMountState State;internal bool Used;
        public Vector3 Origin {get;internal set;}
        public Vector3 Direction {get;internal set;}
        public string Gun {get;internal set;}
    }
    int NpcIndex(uint id,int seat)
    {
        SyncFleet();if(!_states.TryGetValue(id,out var state)||!_server.Vehicles.TryGet(id,out var v)||v.Exploded)return -1;
        // A human owns the accepted seat. AI cannot consume that person's finite
        // belt or overwrite their aim while they occupy it.
        if(_server.Vehicles.SeatOccupant(new(id),seat)!=0)return -1;
        for(int i=0;i<state.Spec.Mounts.Length;i++)if(state.Spec.Mounts[i].Seat==seat)return i;
        return -1;
    }
    public void EquipNpcCrew(uint id)
    {
        SyncFleet();if(!_states.TryGetValue(id,out var state))return;
        for(int i=0;i<state.Mounts.Length;i++)state.Mounts[i]=state.Mounts[i] with{CrewAlive=true};
        Publish(id,state);
    }
    public bool NpcCrewAlive(uint id,int seat)
    {
        if(!_states.TryGetValue(id,out var state))return false;
        for(int i=0;i<state.Spec.Mounts.Length;i++)if(state.Spec.Mounts[i].Seat==seat)return state.Mounts[i].CrewAlive;
        return false;
    }
    public bool AimNpc(uint id,int seat,float yaw,float pitch)
    {
        int i=NpcIndex(id,seat);if(i<0||!float.IsFinite(yaw)||!float.IsFinite(pitch))return false;
        ApplyAim(id,i,new(){VehicleNetId=id,AimMode=MountedAimMode.Look,Yaw=yaw,Pitch=pitch});return true;
    }
    public (Vector3 Muzzle,Vector3 Direction)? NpcBarrel(uint id,int seat)
    {
        if(!_states.TryGetValue(id,out var state)||!_server.Vehicles.TryGet(id,out var v)||v.Exploded)return null;
        int i=Array.FindIndex(state.Spec.Mounts,m=>m.Seat==seat);if(i<0)return null;
        var frame=Frames(id).Pitch[i];return(U(frame.Point(MountedWeaponCatalog.V(state.Spec.Mounts[i].Muzzle))),U(NVector.Transform(-NVector.UnitZ,frame.Rotation)));
    }
    public NpcShot ReadyNpc(uint id,int seat)
    {
        int i=NpcIndex(id,seat);if(i<0)return null;var state=_states[id];var value=state.Mounts[i];
        if(!value.CrewAlive||value.CooldownSeconds>0||value.Ammo==0)return null;
        var barrel=NpcBarrel(id,seat)!.Value;
        return new(){Issuer=this,Id=id,Index=i,Tick=_server.Session.CurrentTick,State=value,Origin=barrel.Muzzle,Direction=barrel.Direction,Gun=state.Spec.Mounts[i].GunId};
    }
    public bool CompleteNpc(NpcShot shot,Vector3 direction)
    {
        if(shot==null||shot.Used||!ReferenceEquals(shot.Issuer,this))return false;shot.Used=true;
        if(shot.Tick!=_server.Session.CurrentTick||!_states.TryGetValue(shot.Id,out var state)
            ||shot.Index>=state.Mounts.Length||state.Mounts[shot.Index]!=shot.State||NpcIndex(shot.Id,state.Spec.Mounts[shot.Index].Seat)!=shot.Index
            ||(!float.IsFinite(direction.x)||!float.IsFinite(direction.y)||!float.IsFinite(direction.z))||direction.sqrMagnitude<.000001f)return false;
        return Launch(shot.Id,shot.Index,0,true,true,shot.Origin,direction.normalized);
    }
}
