using UnityEngine;
namespace UnturnedBevy;

/// <summary>Scene-free holders for extracted Vehicle.Toggle/Cycle/Set/UpdateDoor.
/// Neither pivot is physics. The only body remains native/owner-authoritative.</summary>
public sealed partial class VehicleDoorState
{
    sealed class Pivot { public Vector3 RotationDegrees; }
    sealed class Audio(Action play) { public void Play()=>play(); }
    readonly Pivot _doorPivotA=new(),_doorPivotB=new();
    readonly Audio _doorAudio;
    readonly float _doorFoldDeg;
    float _doorT,_doorHold;
    bool _doorOpen;
    static bool IsInstanceValid(object value)=>value!=null;
    public VehicleDoorState(float degrees,Action edge) { _doorFoldDeg=degrees;_doorAudio=new(edge); }
    public bool DoorMoving=>_doorT!=(_doorOpen?1f:0f);
    public bool Open=>_doorOpen;
    public float Fold=>_doorT;
    public float Hold=>_doorHold;
    public void Restore(bool open,float fold,float hold)
    {
        if(!float.IsFinite(fold)||fold<0||fold>1||!float.IsFinite(hold)||hold<0||hold>DoorHoldSeconds)
            throw new InvalidDataException("Invalid saved folding door state");
        _doorOpen=open;_doorT=fold;_doorHold=hold;
    }
}
