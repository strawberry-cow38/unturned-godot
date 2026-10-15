using System;
using System.Linq;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public sealed class MountedWeaponTests
    {
        static NetPakReader Reader(byte[] bytes){var r=new NetPakReader();r.SetBufferSegment(bytes,bytes.Length);return r;}
        static byte[] Pack(Action<NetPakWriter> write)=>NetMessagePak.Pack(0,write)[1..];
        static MountedInputCommand Input=>new(){VehicleNetId=42,Slot=1,Sequence=65535,AimMode=MountedAimMode.Point,Yaw=180,Pitch=-70,Target=new(600,125,-42),Trigger=true,HeadOut=true};
        [Test]
        public void CommandsAndFactsRejectEveryTruncatedTailAndInvalidTags()
        {
            var input=Input;var bytes=Pack(input.Write);
            Assert.That(MountedInputCommand.TryRead(Reader(bytes),out var decoded),Is.True);Assert.That(decoded.Target,Is.EqualTo(input.Target));Assert.That(decoded.HeadOut,Is.True);
            for(int n=0;n<bytes.Length;n++)Assert.That(MountedInputCommand.TryRead(Reader(bytes[..n]),out _),Is.False,$"command prefix{n}");
            var bad=bytes.ToArray();bad[7]=3;Assert.That(MountedInputCommand.TryRead(Reader(bad),out _),Is.False);
            input=Input;input.Target.x=float.NaN;Assert.Throws<ArgumentException>(()=>Pack(input.Write));
            var shot=new MountedShotEvent{VehicleNetId=42,Mount=1,Tick=65537,Sequence=9,RemainingAmmo=199,Npc=true,
                Shot=new PlayerFiredEvent{PlayerId=0,Gun="dragonfang",Origin=new(1,2,3),Dir=new(0,0,-1)}};
            bytes=Pack(shot.Write);Assert.That(MountedShotEvent.TryRead(Reader(bytes),out var fact),Is.True);Assert.That(fact.Tick,Is.EqualTo(shot.Tick));
            for(int n=0;n<bytes.Length;n++)Assert.That(MountedShotEvent.TryRead(Reader(bytes[..n]),out _),Is.False,$"event prefix{n}");
            shot.Shot.PlayerId=1;Assert.Throws<ArgumentException>(()=>Pack(shot.Write));
        }
        [Test]
        public void RegistryParsesAtomicallyAndOnlyChangedMountsConsumeDeltaBudget()
        {
            var source=new VehicleMountReplication();var replica=new VehicleMountReplication();
            for(uint i=1;i<=100;i++)source.ServerSet(new(i,new[]{new VehicleMountState(0,0,200,0,false)},false),1);
            var full=Pack(w=>source.WriteFull(w,new(1,1,Vector3.zero)));replica.ReadSnapshot(Reader(full),true);
            Assert.That(replica.StateHash(),Is.EqualTo(source.StateHash()));ulong before=replica.StateHash();
            source.ServerSet(new(42,new[]{new VehicleMountState(35,-12,199,.12f,true)},true),2);source.ServerRemove(8,2);
            var delta=Pack(w=>source.WriteDelta(w,new(2,1,Vector3.zero),1));
            Assert.That(delta.Length,Is.LessThan(40),"One firing mount must not resend100 parked belts");
            for(int n=0;n<delta.Length;n++){replica.ReadSnapshot(Reader(delta[..n]),false);Assert.That(replica.StateHash(),Is.EqualTo(before),$"atomic delta prefix{n}");}
            replica.ReadSnapshot(Reader(delta),false);Assert.That(replica.StateHash(),Is.EqualTo(source.StateHash()));Assert.That(replica.TryGet(8,out _),Is.False);
            full=Pack(w=>source.WriteFull(w,new(2,1,Vector3.zero)));before=replica.StateHash();
            for(int n=0;n<full.Length;n++){replica.ReadSnapshot(Reader(full[..n]),true);Assert.That(replica.StateHash(),Is.EqualTo(before),$"atomic full prefix{n}");}
        }
        [Test]
        public void ChurnBeyondLiveCapacityUsesBoundedAtomicReplacementInsteadOfAnUnreadableRemovalList()
        {
            var source=new VehicleMountReplication();var replica=new VehicleMountReplication();
            for(uint i=1;i<=256;i++)source.ServerSet(new(i,new[]{new VehicleMountState(0,0,200,0,false)},false),1);
            replica.ReadSnapshot(Reader(Pack(w=>source.WriteFull(w,new(1,1,Vector3.zero)))),true);
            ulong before=replica.StateHash();
            for(uint i=1;i<=256;i++)source.ServerRemove(i,2);
            for(uint i=257;i<=512;i++){source.ServerSet(new(i,new[]{new VehicleMountState(0,0,200,0,false)},false),3);source.ServerRemove(i,3);}
            var delta=Pack(w=>source.WriteDelta(w,new(3,1,Vector3.zero),1));
            Assert.That(delta.Length,Is.LessThan(8),"Empty full replacement bounds512 historical removals");
            for(int n=0;n<delta.Length;n++){replica.ReadSnapshot(Reader(delta[..n]),false);Assert.That(replica.StateHash(),Is.EqualTo(before));}
            replica.ReadSnapshot(Reader(delta),false);Assert.That(replica.All,Is.Empty);Assert.That(replica.StateHash(),Is.EqualTo(source.StateHash()));
            source.ServerSet(new(513,new[]{new VehicleMountState(20,5,197,.1f,true)},false),4);
            replica.ReadSnapshot(Reader(Pack(w=>source.WriteDelta(w,new(4,1,Vector3.zero),3))),false);
            Assert.That(replica.StateHash(),Is.EqualTo(source.StateHash()),"New baseline resumes ordinary row deltas");
        }
        [Test]
        public void MaximumMountBlockRecoversThroughTheExistingReliableSnapshotBudgetPath()
        {
            TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7645).Connected("mounted fleet view");
            var mounts=Enumerable.Range(0,16).Select(i=>new VehicleMountState(i,-i,200,.1f,true)).ToArray();
            for(uint id=1;id<=256;id++)h.Server.VehicleMounts.ServerSet(new(id,mounts,false),h.Server.Session.CurrentTick+1);
            h.Step(160);
            Assert.That(h.Server.Composer.Diag.OversizedBlocksSkipped,Is.GreaterThan(0),"The test must exceed one unreliable datagram");
            Assert.That(h.Clients[0].VehicleMounts.StateHash(),Is.EqualTo(h.Server.VehicleMounts.StateHash()),"Existing reliable recovery must carry the bounded complete block");
        }
        [Test]
        public void AbsentHostAdapterCannotAcceptMountedClientCommands()
        {
            TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7641).Connected("operator");
            var client=h.Clients[0];int called=0;
            client.SendMountedInput(Input);h.Step(10);Assert.That(h.Server.Combat.LiveBullets,Is.Zero);
            h.Server.MountedInputRequested=(_,_)=>called++;
            h.Server.MountedInputAllowed=(owner,command)=>owner==client.PlayerId&&command.VehicleNetId==42;
            client.SendMountedInput(Input);h.Step(10);Assert.That(called,Is.EqualTo(1));
            var bad=Input;bad.VehicleNetId=99;client.SendMountedInput(bad);h.Step(10);Assert.That(called,Is.EqualTo(1));
        }
        [Test]
        public void TrustedNpcLaunchDamagesPlayersWithPvpOffButHasNoOwnerHitmarkerOrHeldAmmoMutation()
        {
            TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7642).Connected("victim");ushort id=h.Clients[0].PlayerId;
            h.Server.Players.ServerTeleport(id,new(0,0,-2),h.Server.Session.CurrentTick);
            h.Server.Combat.PvPEnabled=false;int markers=0;h.Clients[0].HitConfirmed+=_=>markers++;
            h.Server.CombatState.TryGet(id,out var life);int ammo=life.Ammo;float health=life.HealthExact;
            var profile=new ServerGunProfile{PlayerDamage=10,MuzzleVelocity=200,GravityMultiplier=0,BallisticSteps=2,FalloffStart=1,FalloffEnd=3,FalloffMin=.5f};
            Assert.That(h.Server.Combat.ServerLaunchMounted(0,1,new(0,1.6f,0),new(0,0,-1),profile,npc:true),Is.True);
            profile.PlayerDamage=1000; // accepted round keeps immutable mount gun data
            h.Step(10);Assert.That(life.HealthExact,Is.EqualTo(health-7.5f).Within(.01));Assert.That(life.Ammo,Is.EqualTo(ammo));Assert.That(markers,Is.Zero);
            Assert.That(h.Server.Combat.ServerLaunchMounted(id,2,new(0,1,0),new(0,0,-1),profile,npc:true),Is.False,"NPC identity cannot name a player");
            Assert.That(h.Server.Combat.ServerLaunchMounted(0,2,new(0,1,0),new(0,0,-1),profile),Is.False,"Player launches need actual owner");
        }
        [Test]
        public void TrustedNpcWarheadSharesAuthorityDamageWhileOrdinaryPvpOffBlastRemainsHarmless()
        {
            TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7644).Connected("victim");ushort id=h.Clients[0].PlayerId;
            h.Server.Players.TryGetByOwner(id,out var player);h.Server.CombatState.TryGet(id,out var life);h.Server.Combat.PvPEnabled=false;
            float before=life.HealthExact;var p=player.Pos;
            h.Server.Combat.ServerDetonateWeaponAt(p,4,12,20,25,0,h.Server.Session.CurrentTick);
            Assert.That(life.HealthExact,Is.EqualTo(before));
            h.Server.Combat.ServerDetonateWeaponAt(p,4,12,20,25,0,h.Server.Session.CurrentTick,npc:true);
            Assert.That(life.HealthExact,Is.EqualTo(before-12).Within(.01));
        }
        [Test]
        public void Version63IsExplicitlyRejectedBeforeMissingMountStateCanBeMistakenForAnEmptyGun()
        {
            Assert.That(NetProtocol.Version,Is.EqualTo(64));Assert.That(ReplicationIds.CommandMountedInput,Is.EqualTo(74));Assert.That(ReplicationIds.EventMountedShot,Is.EqualTo(55));
            var h=new NetSimHarness(7643);var old=h.AddClient("old",version:63);old.Connect();
            Assert.That(h.StepUntil(()=>old.State==NetSessionState.Disconnected,100),Is.True);Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));Assert.That(old.RejectServerVersion,Is.EqualTo(64));
        }
    }
}
