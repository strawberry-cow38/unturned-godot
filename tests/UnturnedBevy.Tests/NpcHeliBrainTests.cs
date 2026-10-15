using Godot;
using NUnit.Framework;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture]
public sealed class NpcHeliBrainTests
{
    static NpcHeliBrain Brain()=>new(){Heli=new(){GlobalPosition=new(0,50,500),SpeedMaxMps=40,DebugThrust=9.81f},
        Terr=new(){SampleHeight=(_,_)=>0,Bounds=(-1000,1000,-1000,1000)},Target=Vector3.Zero};
    static void Tick(NpcHeliBrain b,int n=1){for(int i=0;i<n;i++){b.NowMsec+=20;b.PhysicsTick++;b.Step(.02);}}
    [Test]
    public void OriginalThreeStateCollectiveAndPhaseHysteresisProduceInputsWithoutMovingBody()
    {
        var b=Brain();var start=b.Heli.GlobalPosition;Tick(b);
        Assert.That(b.Phase,Is.EqualTo(NpcHeliBrain.FlightPhase.Transit));Assert.That(b.LastWantNoseUpDeg,Is.EqualTo(-38));
        Assert.That(b.LastColl,Is.Zero);Assert.That(b.LastYaw,Is.EqualTo(0).Within(.000001));Assert.That(b.LastPitch,Is.EqualTo(-1));Assert.That(b.LastRoll,Is.EqualTo(0).Within(.000001));Assert.That(b.Heli.GlobalPosition,Is.EqualTo(start));
        b.Terr.SampleHeight=(_,_)=>10;Tick(b);Assert.That(b.DebugWantY,Is.EqualTo(60));Assert.That(b.LastColl,Is.EqualTo(1));
        b.Heli.LinearVelocity=new(0,4,0);Tick(b);Assert.That(b.LastColl,Is.Zero,"Original0.6m/s climb deadband accepts a0.5 error");
        b.Heli.LinearVelocity=new(0,4.2f,0);Tick(b);Assert.That(b.LastColl,Is.EqualTo(-1));
        b.Heli.GlobalPosition=new(0,50,130);b.Heli.LinearVelocity=new(0,0,-30);Tick(b);
        Assert.That(b.Phase,Is.EqualTo(NpcHeliBrain.FlightPhase.Arrive));Assert.That(b.LastWantNoseUpDeg,Is.EqualTo(22));
        Tick(b);Assert.That(b.Phase,Is.EqualTo(NpcHeliBrain.FlightPhase.Arrive));
        b.Heli.LinearVelocity=new(0,0,-15);Tick(b);Assert.That(b.Phase,Is.EqualTo(NpcHeliBrain.FlightPhase.Orbit));
        Assert.That(b.LastWantNoseUpDeg,Is.EqualTo(-7.5f));
        b.Heli.GlobalPosition=new(0,50,224);Tick(b);Assert.That(b.Phase,Is.EqualTo(NpcHeliBrain.FlightPhase.Orbit));
        b.Heli.GlobalPosition=new(0,50,225);Tick(b);Assert.That(b.Phase,Is.EqualTo(NpcHeliBrain.FlightPhase.Transit));
    }
    [Test]
    public void ActualDamageOriginStartsTheLockButPassingPlayersDoNotAndCanopyStopsNewSight()
    {
        var b=Brain();b.Heli.GlobalPosition=new(0,50,100);
        b.Heli.Turrets=[new(0,-65,65,-80,25,Vector3.Zero)];b.Heli.TurretCrewAlive=_=>true;
        b.Heli.TurretMuzzle=_=>b.Heli.GlobalPosition;b.Heli.TurretBarrelDir=_=>new Vector3(0,-48.8f,-100).Normalized();
        b.Players=()=>[new(7,Vector3.Zero,100)];b.ClearWorld=(_,_,_,_)=>true;int attempts=0,shots=0;
        b.Heli.TryTurretFire=(int seat,out Vector3 origin,out Vector3 direction,out string gun)=>{origin=b.Heli.GlobalPosition;direction=b.Heli.TurretBarrelDir(seat);gun="gsh23";attempts++;return false;};
        b.NpcShot=(_,_,_)=>shots++;
        Tick(b,10);Assert.That(b.Mode,Is.EqualTo(NpcHeliBrain.Stance.Patrol));Assert.That(attempts,Is.Zero);
        b.Heli.LastAttackedAtMsec=b.NowMsec;b.Heli.LastAttackedFrom=new(30,0,20);b.CanopyBlocks=(_,_)=>true;Tick(b);
        Assert.That(b.Mode,Is.EqualTo(NpcHeliBrain.Stance.Engaged));Assert.That(b.LastSeen,Is.EqualTo(new Vector3(30,0,20)));
        Assert.That(b.DebugLockLeftSec,Is.EqualTo(300));Assert.That(attempts,Is.Zero);
        b.CanopyBlocks=(_,_)=>false;Tick(b);Assert.That(b.LastSeen,Is.EqualTo(new Vector3(0,1.2f,0)));
        Assert.That(attempts,Is.EqualTo(1));Assert.That(shots,Is.Zero);int burst=b.DebugBurstLeft;
        Tick(b);Assert.That(b.DebugBurstLeft,Is.EqualTo(burst),"Rejected cadence must not spend a source burst round");
        b.ClearWorld=(_,_,_,_)=>false;b.NowMsec+=300001;Tick(b);
        Assert.That(b.Mode,Is.EqualTo(NpcHeliBrain.Stance.Patrol));Assert.That(shots,Is.Zero);
    }
    [Test]
    public void LastCrewDeathEntersFleeAndSavedStateRetainsClockAndOriginRelativeTargets()
    {
        var b=Brain();b.Heli.GlobalPosition=new(900,50,0);b.Heli.Turrets=[new(1,30,150,-75,30,new(-1,0,0))];
        b.Heli.LastAttackedAtMsec=1;b.Heli.LastAttackedFrom=new(0,0,0);b.Heli.TurretCrewAlive=_=>false;Tick(b);
        Assert.That(b.Mode,Is.EqualTo(NpcHeliBrain.Stance.Flee));Assert.That(b.Heli.Input.Yaw,Is.Positive,"Nearest east edge requires the measured right-turn pedal sign");
        var saved=b.Capture();var restored=Brain();restored.NowMsec=100000;restored.Restore(saved);
        Assert.That(restored.Mode,Is.EqualTo(b.Mode));Assert.That(restored.DebugLockLeftSec,Is.EqualTo(b.DebugLockLeftSec));
        restored.TranslateWorld(new(-256,0,512));Assert.That(restored.Target,Is.EqualTo(new Vector3(-256,0,512)));
        Assert.That(restored.LastSeen,Is.EqualTo(new Vector3(-256,0,512)));
    }
}
