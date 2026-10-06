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

namespace UnturnedGodot.Testing
{
    public sealed class MossbergWoodenTests : GameTest
    {
        public override string Name => "gun.mossberg_wooden";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var wood=Assets.find(9148);var standard=Assets.find(112);
            T.Check("wood version is separately spawnable by its requested name",wood?.itemName=="Mossberg 500 Wooden" && wood.gunName=="bluntforce_wood" && standard.itemName=="Mossberg 500");
            T.Check("same feed, capacity and slot",wood.gunCaliber==standard.gunCaliber && wood.gunAmmoMax==standard.gunAmmoMax && wood.slot==standard.slot);
            var a=GunDef.FromDatText(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/bluntforce.dat")));
            var b=GunDef.FromDatText(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/bluntforce_wood.dat")));
            T.Check("same gameplay tuning",a.Damage==b.Damage && a.AmmoMax==b.AmmoMax && a.Firerate==b.Firerate && a.Range==b.Range && a.Action==b.Action && a.Caliber==b.Caliber);
            T.Check("shares factory sights rather than inventing another sight item",AttachmentFit.DefaultIronsId(wood.itemName)==114);
            var visual=Viewmodel.VisualForTest("bluntforce_wood");var baseVisual=Viewmodel.VisualForTest("bluntforce");
            T.Check("mounts are identical",visual.AimHook==baseVisual.AimHook && visual.MuzzleHook==baseVisual.MuzzleHook && visual.SightPos==baseVisual.SightPos);
            var vm=new Viewmodel{GunName="bluntforce_wood"};World.AddChild(vm);yield return Ticks(4);
            T.Check("same single-shell reload",vm.ReloadIsSingleShell && Math.Abs(vm.ReloadLength-1.1f)<.001f && Math.Abs(vm.HammerLength-.4666667f)<.001f);
            var nodes=vm.FindChildren("*","MeshInstance3D",true,false).OfType<MeshInstance3D>().ToList();
            var pump=nodes.FirstOrDefault(n=>n.Name=="MossbergPump");
            T.Check("pump keeps original grey atlas, not brown wood atlas",pump?.MaterialOverride is StandardMaterial3D mat && mat.AlbedoTexture?.GetImage().GetWidth()==256);
            T.Check("wood body uses its own atlas",nodes.Any(n=>n.Mesh?.GetFaces().Length==104*3 && n.MaterialOverride is StandardMaterial3D mat && mat.AlbedoTexture?.GetImage().GetWidth()==512));
            vm.CaptureAnimationPose("hammer",.2f);T.Check("wood gun's grey pump still moves",vm.MossbergPumpOffsetForTest<-.16f);
            var parts=AttachmentFit.PartsFor("bluntforce_wood",114,113,0);
            T.Check("parked world parts are pump and irons, never an external shell mag",parts.Any(p=>p.Slot=="Pump") && parts.Any(p=>p.Slot=="Sight") && !parts.Any(p=>p.Slot=="Magazine"));
            var server=new NetWorldServer(new MemServerTransport(new MemNetwork(seed:10)),contentHash:NetContent.Hash);AuthoredGunProfiles.Install(server);
            server.Players.ServerSpawn(new NetId(1),1,UnityEngine.Vector3.zero,0);server.Players.ServerQueueInput(1,new MoveInput{Seq=1,HeldItemId=9148});
            var inv=server.Inventories.ServerAdd(1,0);inv.Inventory.tryAddItemAuto(new Item(9148),out _);
            var profile=server.Combat.GunFor(1);T.Check("wood gun uses same authoritative single-shell clock",profile.AssetName=="bluntforce_wood" && profile.ReloadOneShell && profile.ReloadTicks==55 && profile.MagCapacity==8);
            yield return Ticks(1);
        }
    }
}
