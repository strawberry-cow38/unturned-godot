using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using SDG.Unturned;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    public sealed class MossbergPartsTests : GameTest
    {
        public override string Name => "gun.mossberg_parts";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var vm = new Viewmodel { GunName = "bluntforce" }; World.AddChild(vm);
            yield return Ticks(4);
            var nodes = vm.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
            var pump = nodes.FirstOrDefault(n => n.Name == "MossbergPump");
            T.Check("pump is a separate original 20-triangle mesh", pump?.Mesh?.GetFaces().Length == 20*3);
            T.Check("stationary mesh excludes pump", nodes.Any(n => n.Mesh?.GetFaces().Length == 104*3));
            T.Check("original ring plus new front post", nodes.Any(n => n.Name == "IronSights" && n.Mesh?.GetFaces().Length == 116*3));
            T.Check("reload uses one whole insert cycle", vm.ReloadIsSingleShell && Math.Abs(vm.ReloadLength-1.1f)<.001f);
            vm.CaptureAnimationPose("reload", .8f);
            T.Check("single shell is visible for the insertion", vm.MossbergShellVisibleForTest);
            vm.CaptureAnimationPose("reload", 1.1f);
            T.Check("inserted shell disappears before the empty hand returns", !vm.MossbergShellVisibleForTest);
            vm.SetReloading(false);
            vm.CaptureAnimationPose("hammer", .2f);
            T.Check("pump travels back with the hammer clip", vm.MossbergPumpOffsetForTest < -.16f && vm.MossbergPumpOffsetForTest > -.20f);
            vm.CaptureAnimationPose("hammer", .47f);
            T.Check("pump returns to its exact parked position", Math.Abs(vm.MossbergPumpOffsetForTest)<1e-6f);
            var parts = AttachmentFit.PartsFor("bluntforce",114,0,0);
            T.Check("world and third-person retain the separate pump", parts.Any(p => p.Slot == "Pump" && p.Mesh.GetFaces().Length == 20*3));
            T.Check("internal shell tube has no pretend external magazine", Viewmodel.MagazineVisualFor("bluntforce").Mesh == null && !AttachmentFit.PartsFor("bluntforce",114,113,0).Any(p=>p.Slot=="Magazine"));
            T.Check("unrelated guns gain no pump", !AttachmentFit.PartsFor("mac10",9147,9146,0).Any(p=>p.Slot=="Pump"));
            yield return Ticks(1);
        }
    }
    public sealed class MossbergReloadTests : GameTest
    {
        public override string Name => "gun.mossberg_reload";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var server = new NetWorldServer(new MemServerTransport(new MemNetwork(seed:9)), contentHash:NetContent.Hash);
            AuthoredGunProfiles.Install(server);
            server.Players.ServerSpawn(new NetId(1),1,UnityEngine.Vector3.zero,0);
            server.Players.ServerQueueInput(1,new MoveInput { Seq=1,HeldItemId=112 });
            var inv = server.Inventories.ServerAdd(1,0);
            T.Check("held id without ownership cannot select Mossberg timing", server.Combat.GunFor(1)==server.Combat.DefaultGun);
            inv.Inventory.tryAddItemAuto(new Item(112),out _);
            var profile=server.Combat.GunFor(1);
            T.Check("server insertion clock matches one-shell clip", profile.ReloadOneShell && profile.ReloadTicks==55 && profile.MagCapacity==8);
            T.Check("this timing change preserves host damage tuning", profile.PlayerDamage==server.Combat.DefaultGun.PlayerDamage && profile.Pellets==server.Combat.DefaultGun.Pellets);
            var state=server.CombatState.ServerAdd(1,UnityEngine.Vector3.zero,0,0);
            server.Combat.OnReload(1,new ReloadCommand{Seq=1},0);
            server.Combat.Step(54);
            T.Check("no shell before the insertion finishes", state.Ammo==0);
            server.Combat.Step(55);
            T.Check("one insert adds exactly one, not the full tube",state.Ammo==1);
            server.Combat.OnReload(1,new ReloadCommand{Seq=2},56);
            server.Combat.OnFire(1,new FireCommand{Seq=1,Origin=UnityEngine.Vector3.zero,Dir=UnityEngine.Vector3.forward},57);
            T.Check("fire interrupts insertion using the loaded shell",state.Ammo==0 && state.ReloadDoneTick<0);
            server.Combat.Step(111);
            T.Check("interrupted insertion gives no late shell",state.Ammo==0);
            server.Combat.OnReload(1,new ReloadCommand{Seq=3},200);
            server.Combat.OnReload(1,new ReloadCommand{Seq=4},255);
            T.Check("next request on completion tick does not swallow the prior shell",state.Ammo==1 && state.ReloadDoneTick==310);
            server.Combat.Step(310);
            T.Check("second complete insertion adds a second shell",state.Ammo==2);
            state.Ammo=30; server.Combat.OnReload(1,new ReloadCommand{Seq=5},350);
            T.Check("switching from the legacy default cannot leave a thirty-shell tube", state.Ammo==8 && state.ReloadDoneTick<0);
            var skills = server.Skills.ServerAdd(1,0).Skills;
            skills.AwardExperience(10000);
            while (skills.TryUpgrade(0,(int)EPlayerOffense.DEXTERITY)) { }
            T.Check("server insertion respects authoritative dexterity", server.Combat.GunFor(1).ReloadTicks == (int)Math.Ceiling(55 / skills.DexterityReloadSpeed()));
            server.Players.ServerQueueInput(1,new MoveInput{Seq=2,HeldItemId=1369});
            var control=server.CombatState.ServerAdd(2,UnityEngine.Vector3.zero,0,0);
            server.Combat.OnReload(2,new ReloadCommand{Seq=1},400);
            server.Combat.Step(400+server.Combat.DefaultGun.ReloadTicks);
            T.Check("legacy whole-mag reload is unchanged",control.Ammo==server.Combat.DefaultGun.MagCapacity);
            yield return Ticks(1);
        }
    }
}
