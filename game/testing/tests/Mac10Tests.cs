using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SDG.Unturned;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    public sealed class Mac10ContentTests : GameTest
    {
        public override string Name => "gun.mac10_content";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var gun = Assets.find(9145); var mag = Assets.find(9146); var irons = Assets.find(9147);
            T.Check("new gun, not Uzi renamed", gun?.gunName == "mac10" && Assets.find(1369)?.gunName == "bulldog");
            T.Check("spawn/display name", gun?.itemName == "MAC-10");
            T.Check("real proprietary 30-round .45 magazine", mag?.magCapacity == 30 && mag.magCaliber == 205 && mag.magRound == ".45 ACP");
            T.Check("MAC magazine fits MAC", AttachmentFit.Fits(mag, "Magazine", gun.gunCaliber));
            T.Check("Uzi magazine is refused", !AttachmentFit.Fits(Assets.find(1371), "Magazine", gun.gunCaliber));
            T.Check("MAC magazine does not become Uzi magazine", !AttachmentFit.Fits(mag, "Magazine", Assets.find(1369).gunCaliber));
            T.Check("default irons have their own item", AttachmentFit.DefaultIronsId(gun.itemName) == 9147 && irons?.type == EItemType.SIGHT);
            T.Check("separate attachment models", AttachmentFit.MeshFor(9146) == "mag_mac10.txt" && AttachmentFit.MeshFor(9147) == "mac10_sight.txt");
            var info = Viewmodel.VisualForTest("mac10"); var mount = Viewmodel.MagazineVisualFor("mac10");
            T.Check("authored ADS anchor, not donor gun hook", info.AimHook.DistanceTo(new Vector3(0,-.392f,-.188f)) < 1e-6f);
            T.Check("mag mount is authored insertion point", mount.Hook.DistanceTo(new Vector3(0,-.027797834f,.17472924f)) < 1e-6f);
            T.Check("nongun held models cannot inherit a rifle magazine", Viewmodel.MagazineVisualFor("carjack.txt").Mesh == null);
            T.Check("iron palette supplied", info.SightAlbedo == "mac10_sight_albedo.png" && AttachmentFit.TexFor(9147) != null);
            foreach (int id in new[] { 9145,9146,9147 })
            {
                T.Check($"world model {id}", System.IO.File.Exists(ProjectSettings.GlobalizePath($"res://content/items/{id}.txt")));
                T.Check($"inventory icon {id}", System.IO.File.Exists(ProjectSettings.GlobalizePath($"res://content/items/icons/{id}.png")));
            }
            var parts = AttachmentFit.PartsFor("mac10", 9147,9146,0,0);
            T.Check("world/3P have separate irons and magazine", parts.Count == 2);
            T.Check("world/3P magazine shares 1P hook", parts.Any(p => p.Item1 == "Magazine" && p.Item3.DistanceTo(mount.Hook) < 1e-6f && p.Item5 != null));
            T.Check("explicitly ejected mag is absent from world/3P", !AttachmentFit.PartsFor("mac10",9147,0,0,0).Any(p => p.Item1 == "Magazine"));
            yield return Ticks(1);
        }
    }

    public sealed class Mac10ViewmodelTests : GameTest
    {
        public override string Name => "gun.mac10_viewmodel";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var vm = new Viewmodel { GunName = "mac10" }; World.AddChild(vm);
            yield return Ticks(4);
            var all = vm.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
            var iron = all.FirstOrDefault(m => m.Name == "IronSights"); var mag = all.FirstOrDefault(m => m.Name == "Magazine");
            T.Check("irons and magazine are separate nodes", iron != null && mag != null && iron != mag);
            T.Check("iron geometry is separate 132 triangles", iron?.Mesh?.GetFaces().Length == 132*3);
            T.Check("magazine geometry is separate 12 triangles", mag?.Mesh?.GetFaces().Length == 12*3);
            T.Check("irons carry their palette", iron?.MaterialOverride is StandardMaterial3D im && im.AlbedoTexture != null && im.AlbedoColor == Colors.White);
            vm.SetSlotAttached("Magazine", false);
            T.Check("magazine can disappear without changing gun body", mag != null && !mag.Visible && iron != null && iron.Visible);
            vm.SetSlotAttached("Magazine", true);
            vm.SetSlotMesh("Sight", "mac10_sight.txt");
            T.Check("irons refit keeps their texture", iron?.MaterialOverride is StandardMaterial3D restored && restored.AlbedoTexture != null);
            T.Check("Uzi reload reused explicitly", Math.Abs(vm.ReloadLength-1.8f)<.001f);
            T.Check("normal equip gate still completes", !vm.IsEquipComplete);
            yield return Ticks(45);
            T.Check("equip finishes", vm.IsEquipComplete);
        }
    }

    public sealed class Mac10ServerProfileTests : GameTest
    {
        public override string Name => "gun.mac10_server_profile";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var profile = AuthoredGunProfiles.Profile;
            var def = GunDef.FromDatText(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/mac10.dat")));
            T.Check("server uses same MAC data", profile.AssetName == "mac10" && profile.PlayerDamage == def.Damage && profile.FirerateTicks == def.Firerate && profile.CyclicRateRPM == 950 && profile.CyclicRateRPM == def.CyclicRateRPM && profile.MagCapacity == 30);
            T.Check("server reload follows the selected clip", profile.ReloadTicks == 90);
            var net = new NetWorldServer(new MemServerTransport(new MemNetwork(seed:7)), contentHash:NetContent.Hash);
            AuthoredGunProfiles.Install(net);
            net.Players.ServerSpawn(new NetId(1),1,UnityEngine.Vector3.zero,0);
            net.Players.ServerQueueInput(1,new MoveInput { Seq=1,HeldItemId=9145 });
            var entry = net.Inventories.ServerAdd(1,0);
            T.Check("claimed MAC without owning it keeps default profile", net.Combat.GunFor(1) == net.Combat.DefaultGun);
            entry.Inventory.tryAddItemAuto(new Item(9145),out _);
            T.Check("owned held MAC resolves on actual server", net.Combat.GunFor(1).AssetName == "mac10");
            net.Players.ServerQueueInput(1,new MoveInput { Seq=2,HeldItemId=1369 });
            T.Check("switch away restores former behavior", net.Combat.GunFor(1) == net.Combat.DefaultGun);
            var custom = new ServerGunProfile { AssetName="test_override" };net.Combat.SetGunProfile(1,custom);
            T.Check("explicit host/test overrides still win", net.Combat.GunFor(1) == custom);
            yield return Ticks(1);
        }
    }
    public sealed class Mac10PickupTests : GameTest
    {
        public override string Name => "gun.mac10_pickup";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var item = new Item(9145); item.gunSightId = 9147; item.gunMagId = 9146;
            var pickup = UnturnedGodot.WorldItem.Spawn(World, item, Vector3.Zero);
            yield return Ticks(2);
            var meshes = pickup.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
            T.Check("pickup is real gun body, not rarity marker", meshes.Any(m => m.Mesh?.GetFaces().Length == 264*3));
            T.Check("pickup mounts separate irons", meshes.Any(m => m.Name == "Attach_Sight" && m.Mesh?.GetFaces().Length == 132*3));
            T.Check("pickup mounts separate magazine", meshes.Any(m => m.Name == "Attach_Magazine" && m.Mesh?.GetFaces().Length == 12*3));
        }
    }

    public sealed class Mac10DedicatedWiringTests : GameTest
    {
        public override string Name => "gun.mac10_dedicated_wiring";
        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot:"res://__no_such_map__", mapPlace:"placements.txt", syncLoad:true, activeHoliday:"NONE");
            var world = task.Result;
            var ded = new DedicatedServer { Driver=world.Sim, TransportOverride=new MemServerTransport(new MemNetwork(923)) };
            World.AddChild(ded);
            yield return Ticks(2);
            T.Check("real dedicated boot installs content resolver", ded.Server?.Combat.ResolveHeldGunProfile != null);
            ded.Server.Players.ServerSpawn(new NetId(900), 99, UnityEngine.Vector3.zero, 0);
            var inv = ded.Server.Inventories.ServerAdd(99, 0);
            inv.Inventory.tryAddItemAuto(new Item(9145), out _);
            ded.Server.Players.ServerQueueInput(99,new MoveInput {Seq=1,HeldItemId=9145});
            T.Check("actual dedicated host uses owned MAC profile", ded.Server.Combat.GunFor(99).AssetName == "mac10");
        }
    }

    public sealed class Mac10CadenceTests : GameTest
    {
        public override string Name => "gun.mac10_cadence";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var player = new PlayerController { CaptureMouse=false };World.AddChild(player);
            yield return Ticks(2);
            player.Inventory.items[1].tryAddItem(new Item(9145));
            player.EquipHotbar(2);
            yield return Ticks(70);
            T.Check("accepted MAC equipped",player.HasGunOut && player.Gun?.CyclicRateRPM==950);
            player.Ammo=30;
            var fired=new List<int>();
            for(int tick=0;tick<=60;tick++)
            {
                if(player.Fire()) fired.Add(tick);
                if(tick<60) yield return Ticks(1);
            }
            T.Check($"real shell fires 20 shots ({fired.Count})",fired.Count==20);
            T.Check("19 real shell intervals total 60 ticks",fired.Count==20 && fired[^1]-fired[0]==60);
            T.Check("real shell alternates only 3/4-tick gaps",fired.Zip(fired.Skip(1),(a,b)=>b-a).All(g=>g==3||g==4));
            T.Check("shots genuinely spend ammo",player.Ammo==10);
            yield return Ticks(30);
            bool resume=player.Fire();
            T.Check("pause resumes without stored burst",resume && !player.Fire());
        }
    }

}
