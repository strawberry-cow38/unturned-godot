using System.Collections.Generic;
using NUnit.Framework;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class MountedMuzzleTests
{
    static ServerGunProfile Gun=>new(){PlayerDamage=10,VehicleDamage=25,MuzzleVelocity=100,GravityMultiplier=0,BallisticSteps=12};
    [Test]
    public void CapturedVehicleIsStableAcrossOwnerMotionAndDoesNotExcludeOtherPlayers()
    {
        TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7981).Connected("shooter","target");
        ushort owner=h.Clients[0].PlayerId,target=h.Clients[1].PlayerId;
        h.Server.Players.ServerTeleport(target,new(0,0,-6),h.Server.Session.CurrentTick);
        h.Server.CombatState.TryGet(target,out var life);float hp=life.HealthExact;
        h.Server.Combat.PvPEnabled=true;var ids=new List<uint>();
        h.Server.Combat.MountedRayDetailed=(uint id,Vector3 a,Vector3 b,out CombatWorldHit hit)=>{ids.Add(id);hit=default;return false;};
        Assert.That(h.Server.Combat.ServerLaunchMounted(owner,1,new(0,1,0),new(0,0,-1),Gun,firingVehicle:42),Is.True);
        h.Step();h.Server.Players.ServerTeleport(owner,new(100,0,100),h.Server.Session.CurrentTick);h.Step(10);
        Assert.That(ids.Count,Is.GreaterThan(1));Assert.That(ids,Is.All.EqualTo(42));
        Assert.That(life.HealthExact,Is.LessThan(hp),"Vehicle identity exclusion must not suppress the independent player capsule test");
        Assert.That(h.Server.Combat.LiveBullets,Is.Zero);
    }
    [TestCase(false),TestCase(true)]
    public void AbsentAdapterOrZeroCapturedIdentityPreservesLegacyWorldRay(bool zeroIdentity)
    {
        TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7982).Connected("shooter");int old=0,newRay=0,damage=0;
        h.Server.Combat.BulletRayDetailed=(Vector3 a,Vector3 b,out CombatWorldHit hit)=>{old++;hit=new(a+new Vector3(0,0,-.01f),vehicleNetId:42);return true;};
        if(zeroIdentity)h.Server.Combat.MountedRayDetailed=(uint id,Vector3 a,Vector3 b,out CombatWorldHit hit)=>{newRay++;hit=default;return false;};
        h.Server.Combat.DamageBody=_=>damage++;
        Assert.That(h.Server.Combat.ServerLaunchMounted(h.Clients[0].PlayerId,1,new(0,1,0),new(0,0,-1),Gun,firingVehicle:zeroIdentity?0u:42u),Is.True);
        h.Step(3);Assert.That(old,Is.EqualTo(1));Assert.That(newRay,Is.Zero);Assert.That(damage,Is.EqualTo(1));
    }
    [Test]
    public void MountedBarricadeIdentityLookupReceivesTheSameCapturedVehicle()
    {
        TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7983).Connected("shooter");int old=0,current=0;
        h.Server.Combat.MountedRayDetailed=(uint id,Vector3 a,Vector3 b,out CombatWorldHit hit)=>{hit=new(a+new Vector3(0,0,-1));return true;};
        h.Server.Combat.DamageBarricadeAlong=(_,_,_,_)=>{old++;return false;};
        h.Server.Combat.DamageMountedBarricadeAlong=(id,a,b,amount,tick)=>{Assert.That(id,Is.EqualTo(42));Assert.That(amount,Is.EqualTo(25));current++;return true;};
        h.Server.Combat.ServerLaunchMounted(h.Clients[0].PlayerId,1,new(0,1,0),new(0,0,-1),Gun,firingVehicle:42);
        h.Step(3);Assert.That(current,Is.EqualTo(1));Assert.That(old,Is.Zero);
    }
}
