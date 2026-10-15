using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class NpcCrewProtocolTests
{
    static NetPakReader Reader(byte[] bytes){var r=new NetPakReader();r.SetBufferSegment(bytes,bytes.Length);return r;}
    static byte[] Pack(HitConfirmEvent v)=>NetMessagePak.Pack(ReplicationIds.EventHitConfirm,v.Write)[1..];
    [Test]
    public void HumanoidConfirmationIsDistinctAndRejectsUnknownKindEmptyIdentityAndEveryTruncatedTail()
    {
        var fact=new HitConfirmEvent{Seq=7,TargetKind=(byte)HitTargetKind.Humanoid,TargetId=123,Damage=80,Killed=false,Headshot=true};
        var bytes=Pack(fact);Assert.That(HitConfirmEvent.TryRead(Reader(bytes),out var read),Is.True);
        Assert.That(read.TargetKind,Is.EqualTo(3));Assert.That(read.TargetId,Is.EqualTo(123));Assert.That(read.Damage,Is.EqualTo(80));Assert.That(read.Headshot,Is.True);
        for(int n=0;n<bytes.Length;n++){Assert.That(HitConfirmEvent.TryRead(Reader(bytes[..n]),out read),Is.False);Assert.That(read.TargetId,Is.Zero,"A truncated tail never publishes partial identity");}
        foreach(byte kind in new byte[]{0,4,255}){fact.TargetKind=kind;Assert.That(HitConfirmEvent.TryRead(Reader(Pack(fact)),out read),Is.False);Assert.That(read.TargetId,Is.Zero);}
        fact.TargetKind=3;fact.TargetId=0;Assert.That(HitConfirmEvent.TryRead(Reader(Pack(fact)),out _),Is.False);
    }
    [TestCase(false),TestCase(true)]
    public void SourceCrewUsesEffectivePlayerRoundNotVehicleDamageAndNpcGetsNoOwnerConfirmation(bool npc)
    {
        TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7985).Connected("shooter");var owner=h.Clients[0].PlayerId;
        var confirmations=new List<HitConfirmEvent>();h.Clients[0].HitConfirmed+=confirmations.Add;CombatBodyHit? received=null;int hullHits=0;
        h.Server.Combat.BulletRayDetailed=(Vector3 a,Vector3 b,out CombatWorldHit hit)=>{hit=new(a+(b-a)*.5f,vehicleNetId:42,crewSlot:2);return true;};
        h.Server.Combat.DamageBody=_=>hullHits++;
        h.Server.Combat.DamageHumanoid=hit=>{received=hit;return new(hit.Damage*2,true,true);};
        var gun=new ServerGunProfile{PlayerDamage=40,AnimalDamage=7,VehicleDamage=2,MuzzleVelocity=100,GravityMultiplier=0,BallisticSteps=4};
        Assert.That(h.Server.Combat.ServerLaunchMounted(npc?(ushort)0:owner,10,new(10,3,10),Vector3.forward,gun,npc:npc),Is.True);h.Step(12);
        Assert.That(received,Is.Not.Null);Assert.That(received.Value.Damage,Is.EqualTo(40));Assert.That(received.Value.Hit.CrewSlot,Is.EqualTo(2));Assert.That(hullHits,Is.Zero);
        Assert.That(confirmations.Count,Is.EqualTo(npc?0:1));
        if(!npc){Assert.That(confirmations[0].TargetKind,Is.EqualTo((byte)HitTargetKind.Humanoid));Assert.That(confirmations[0].TargetId,Is.EqualTo(42));Assert.That(confirmations[0].Damage,Is.EqualTo(80));}
    }
    [TestCase(false),TestCase(true)]
    public void HandheldHumanoidRoundCapturesDefaultOrExplicitShellDamageBeforeProfileChanges(bool explicitShell)
    {
        TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7987).Connected("shooter");h.Step(12);
        var client=h.Clients[0];h.Server.Players.TryGetByOwner(client.PlayerId,out var player);var origin=player.Pos+Vector3.up*1.6f;
        var profile=new ServerGunProfile{PlayerDamage=explicitShell?7:80,HumanoidDamage=explicitShell?80:null,AnimalDamage=13,
            MuzzleVelocity=100,GravityMultiplier=0,BallisticSteps=12,FalloffStart=5,FalloffEnd=15,FalloffMin=.25f};
        h.Server.Combat.SetGunProfile(client.PlayerId,profile);h.Server.Combat.HeldCondition=_=>25;
        CombatBodyHit? accepted=null;h.Server.Combat.DamageHumanoid=hit=>{accepted=hit;return new(hit.Damage,false,false);};
        h.Server.Combat.BulletRayDetailed=(Vector3 from,Vector3 to,out CombatWorldHit hit)=>{
            hit=new(origin+Vector3.forward*10,vehicleNetId:42,crewSlot:1);return from.z<=origin.z+10&&to.z>=origin.z+10;
        };
        var command=new FireCommand{Seq=1,Origin=origin,Dir=Vector3.forward};
        h.Server.Combat.OnFire(client.PlayerId,in command,h.Server.Session.CurrentTick);Assert.That(h.Server.Combat.LiveBullets,Is.EqualTo(1));
        profile.PlayerDamage=999;profile.AnimalDamage=999;profile.HumanoidDamage=999;h.Step(12);
        Assert.That(accepted,Is.Not.Null);
        Assert.That(accepted.Value.Damage,Is.EqualTo(80*.625f*SDG.Unturned.Durability.DamageMultiplier(25)).Within(.001),
            "The in-flight humanoid round keeps its source damage and applies original condition and distance falloff");
    }
    [Test]
    public void TrustedLaunchRefusesInvalidOptionalHumanoidDamage()
    {
        var h=new TransactionalHarness(7988).Connected("shooter");var gun=new ServerGunProfile();
        foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,-1f})
        {gun.HumanoidDamage=invalid;Assert.That(h.Server.Combat.ServerLaunchMounted(h.Clients[0].PlayerId,1,new(10,3,10),Vector3.forward,gun),Is.False);}
        Assert.That(h.Server.Combat.LiveBullets,Is.Zero);
    }
    [Test]
    public void Version66IsExplicitlyRejectedForNewCrewSemantics()
    {
        Assert.That(NetProtocol.Version,Is.GreaterThanOrEqualTo(67));var h=new NetSimHarness(7986);var old=h.AddClient("old crew",version:66);old.Connect();
        Assert.That(h.StepUntil(()=>old.State==NetSessionState.Disconnected,100),Is.True);Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));
    }
}
