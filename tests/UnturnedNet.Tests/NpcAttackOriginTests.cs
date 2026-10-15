using NUnit.Framework;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class NpcAttackOriginTests
{
    [Test]
    public void BodyDamageCarriesOriginalLaunchOriginAfterFlightAndOwnerMotion()
    {
        TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7984).Connected("shooter");
        var origin=new Vector3(0,2,0);CombatBodyHit? received=null;
        h.Server.Combat.BulletRayDetailed=(Vector3 a,Vector3 b,out CombatWorldHit hit)=>{hit=new(new(0,2,-6),vehicleNetId:42);return a.z>=-6&&b.z<=-6;};
        h.Server.Combat.DamageBody=hit=>received=hit;
        var gun=new ServerGunProfile{PlayerDamage=10,VehicleDamage=25,MuzzleVelocity=100,GravityMultiplier=0,BallisticSteps=12};
        Assert.That(h.Server.Combat.ServerLaunchMounted(h.Clients[0].PlayerId,1,origin,new(0,0,-1),gun),Is.True);
        h.Step();Assert.That(received,Is.Null);h.Server.Players.ServerTeleport(h.Clients[0].PlayerId,new(99,0,99),h.Server.Session.CurrentTick);h.Step(4);
        Assert.That(received,Is.Not.Null);Assert.That(received.Value.ShotOrigin,Is.EqualTo(origin));
        Assert.That(received.Value.Hit.Point.z,Is.EqualTo(-6));Assert.That(received.Value.Damage,Is.EqualTo(25));
        var legacy=new CombatBodyHit(new(origin),1,1,true,Vector3.forward,0);Assert.That(legacy.ShotOrigin,Is.Null);
    }
}
