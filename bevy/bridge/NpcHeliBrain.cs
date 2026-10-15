using Godot;
using NVector=System.Numerics.Vector3;
using Quaternion=System.Numerics.Quaternion;

namespace UnturnedBevy;

/// <summary>Adapters for the generated original NpcHeli controller. This class
/// produces the same four stick axes; it never integrates or moves a body.</summary>
public sealed partial class NpcHeliBrain
{
    public NpcHeliAirframe Heli;
    public NpcHeliTerrain Terr;
    public Vector3 Target;
    public string TargetName="";
    public double NowMsec;
    public ulong PhysicsTick;
    public Func<IEnumerable<NpcHeliPlayer>> Players=()=>[];
    public Func<uint,Vector3,Vector3,ushort,bool> ClearWorld=(_,_,_,_)=>false;

    public sealed record Saved(int Phase,int Mode,float[] Target,string TargetName,float[] LastSeen,
        double SeenDamageAgo,double LockRemaining,float[] TurYaw,float[] TurPitch,int BurstLeft,float BurstWait,bool OrbitStrafing);
    static float[] Array(Vector3 v)=>[v.X,v.Y,v.Z];
    static Vector3 Vector(float[] v)=>new(v[0],v[1],v[2]);
    public Saved Capture()=>new((int)Phase,(int)Mode,Array(Target),TargetName,Array(LastSeen),NowMsec-_seenDamageAtMsec,
        _lockUntilMsec-NowMsec,_turYaw?.ToArray(),_turPitch?.ToArray(),_burstLeft,_burstWait,OrbitStrafing);
    public void Restore(Saved value)
    {
        Phase=(FlightPhase)value.Phase;Mode=(Stance)value.Mode;Target=Vector(value.Target);TargetName=value.TargetName;
        LastSeen=Vector(value.LastSeen);_seenDamageAtMsec=NowMsec-value.SeenDamageAgo;_lockUntilMsec=NowMsec+value.LockRemaining;
        _turYaw=value.TurYaw?.ToArray();_turPitch=value.TurPitch?.ToArray();_burstLeft=value.BurstLeft;_burstWait=value.BurstWait;
        OrbitStrafing=value.OrbitStrafing;
    }
    public void TranslateWorld(Vector3 delta){Target+=delta;LastSeen+=delta;}
}

public sealed record NpcHeliPlayer(ushort Owner,Vector3 GlobalPosition,float Health);
public sealed class NpcHeliTerrain
{
    public Func<float,float,float> SampleHeight;
    public (float minX,float maxX,float minZ,float maxZ) Bounds;
    public (float minX,float maxX,float minZ,float maxZ) WorldBoundsXZ()=>Bounds;
}
public sealed record NpcHeliMount(int Seat,float YawMin,float YawMax,float PitchMin,float PitchMax,Vector3 GunnerAt);
public sealed class NpcHeliAirframe
{
    public uint NetId;
    public bool Exists=true,Exploded;
    public Vector3 GlobalPosition,LinearVelocity,AngularVelocity;
    public Quaternion Rotation=Quaternion.Identity;
    public float SpeedMaxMps,DebugThrust;
    public Vector3 LastAttackedFrom;
    public double LastAttackedAtMsec=-1e9;
    public NpcHeliMount[] Turrets=[];
    public Func<int,bool> TurretCrewAlive=_=>false;
    public Action<int,float,float> AimTurret=(_,_,_)=>{};
    public Func<int,Vector3?> TurretMuzzle=_=>null;
    public Func<int,Vector3> TurretBarrelDir=_=>Vector3.Forward;
    public delegate bool ReadyShot(int seat,out Vector3 origin,out Vector3 direction,out string gun);
    public ReadyShot TryTurretFire=(int seat,out Vector3 origin,out Vector3 direction,out string gun)=>{origin=Vector3.Zero;direction=Vector3.Forward;gun=null;return false;};
    public readonly record struct Controls(float Collective,float Yaw,float Pitch,float Roll);
    public Controls Input;
    public Vector3 Right=>From(NVector.Transform(NVector.UnitX,Rotation));
    public Vector3 Back=>From(NVector.Transform(NVector.UnitZ,Rotation));
    public Vector3 ToLocal(Vector3 point)=>From(NVector.Transform(To(point-GlobalPosition),Quaternion.Inverse(Rotation)));
    public void DriveHeli(float collective,float yaw,float pitch,float roll,double delta)=>Input=new(collective,yaw,pitch,roll);
    static NVector To(Vector3 v)=>new(v.X,v.Y,v.Z);
    static Vector3 From(NVector v)=>new(v.X,v.Y,v.Z);
}
