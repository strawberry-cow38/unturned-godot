using System.Collections.Generic;
using Godot;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // THE WATER BOTTLE SILENCER (strawberry 2026-10-07: "add a water bottle silencer. crafted w a water bottle + tape.
    // attaches to any weapon w the bottle model on the tip of the muzzle. breaks after the first shot.").
    //
    // Driven through the singleplayer loopback -- the server owns the bag, so the fit has to be spent there and the burst
    // has to reach the server's copy, or the next owner echo puts the bottle straight back on the gun.
    //
    // ⚠ The FIRST shot is the one under test and the SECOND is the control. "No zombie alert" on its own is satisfied by
    // a gun that never makes noise at all; it is the second, loud shot that makes the first one's silence mean something.
    public sealed class BottleSilencerTests : GameTest
    {
        public override string Name => "attach.bottle_silencer";
        public override double TimeoutSimSeconds => 40;

        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        static int CountOf(PlayerInventory inv, ushort id)
        {
            int n = 0;
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
                for (byte i = 0; i < (inv.items[b]?.getItemCount() ?? 0); i++)
                    if (inv.items[b].getItem(i)?.item?.id == id) n++;
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.Load();
            const ushort Bottle = AttachmentFit.WaterBottleSilencerId;
            var asset = Assets.find(Bottle);
            T.Check($"the item exists as a BARREL attachment ({asset?.itemName}, {asset?.type})", asset != null && asset.type == EItemType.BARREL);
            if (asset == null) yield break;

            // ---- 0. THE RECIPE: bottled water + tape -> the silencer, both spent.
            BlueprintDef recipe = null;
            foreach (var bp in BlueprintRegistry.All)
                foreach (var o in bp.Outputs) if (Crafting.Resolve(o.Guid) == Bottle) recipe = bp;
            var ins = new List<string>();
            if (recipe != null) foreach (var i in recipe.Inputs) ins.Add($"{Crafting.Resolve(i.Guid)}x{i.Amount}{(i.Consume ? "" : " (tool)")}");
            T.Check($"crafted from a water bottle + tape ({string.Join(", ", ins)})",
                recipe != null && recipe.Inputs.Count == 2 && ins.Contains("14x1") && ins.Contains("69x1"));

            // ---- 1. ANY WEAPON, ONE SIZE: it fits every gun, keeps the SAME size on all of them, and only grows on a barrel
            // too big for it (strawberry: "maintains its size no matter what gun its on, only scaling up when it needs to fit
            // a bigger barrel"). The 3P/puppet/drop part is checked here; the 1P one is checked against it below.
            int guns = 0, fits = 0, sameSize = 0, oneScale = 0, grown = 0, partOk = 0;
            var bad = new List<string>();
            var baseSize = AttachmentFit.BottleMesh(1f)?.GetAabb().Size ?? Vector3.Zero;
            foreach (var g in Assets.all())
            {
                if (string.IsNullOrEmpty(g.gunName)) continue;
                guns++;
                if (AttachmentFit.Fits(asset, "Barrel", g.gunCaliber, g.gunCaliberName, g)) fits++;
                var fit = AttachmentFit.BottleFit(g.gunName);
                foreach (var part in AttachmentFit.PartsFor(g.gunName, 0, 0, Bottle))
                {
                    if (part.Slot != "Barrel") continue;
                    var size = part.Mesh.GetAabb().Size;
                    if (part.Pos == fit.Seat && part.Tex != null && (size - baseSize * fit.Scale).Length() < 1e-3f) partOk++;
                    else bad.Add($"{g.gunName} x{fit.Scale:0.00}");
                    if (fit.Scale == 1f) { oneScale++; if ((size - baseSize).Length() < 1e-4f) sameSize++; }
                    else grown++;
                }
            }
            T.Check($"fits every gun ({fits}/{guns})", guns > 0 && fits == guns);
            T.Check($"the mounted part is the fitted bottle on every gun, in its own colours ({partOk}/{guns}{(bad.Count > 0 ? ": " + string.Join(", ", bad) : "")})", partOk == guns);
            T.Check($"ONE size: every gun whose barrel it fits gets exactly the same bottle ({sameSize}/{oneScale} identical, {grown} grown)",
                oneScale >= 40 && sameSize == oneScale);
            // ...and the ones that grow are the ones with a barrel too big for it -- and grown just enough to cover it
            var rocket = AttachmentFit.BottleFit("launcher_rocket"); var sawed = AttachmentFit.BottleFit("sawed_off");
            T.Check($"a rocket tube and a side-by-side double get a bigger bottle ({rocket.Scale:0.00}x, {sawed.Scale:0.00}x)",
                rocket.Scale > 1.5f && sawed.Scale > 1.5f);
            // a hand-authored muzzle hook can sit ahead of the barrel (for the flash); the bottle goes on the BARREL
            var ef = AttachmentFit.BottleFit("eaglefire"); var efHook = Viewmodel.VisualForTest("eaglefire").MuzzleHook;
            T.Check($"on the AR-15 it sits on the barrel tip, not floating at the flash hook ({ef.Seat.Y:0.000} vs hook {efHook.Y:0.000})",
                ef.Seat.Y < efHook.Y - 0.03f && ef.Seat.Y > 0.7f);
            // CONTROL: a real suppressor still mounts at the shared hook, so the muzzle placement is the bottle's, not every barrel's.
            bool supAtHook = false;
            foreach (var part in AttachmentFit.PartsFor("cobra", 0, 0, 7)) if (part.Slot == "Barrel") supAtHook = part.Pos == AttachmentFit.DefaultBarrelHook;
            T.Check("the 5.56 silencer's mount is unchanged", supAtHook);

            // ---- 2. FIT IT, on a real gun, through the ring's own click.
            Rigs.Ground(World);
            var driver = new SimDriver(); World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            var loop = new MpLoopback { Player = player, Driver = driver, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Wait(() => loop.Client.State == NetSessionState.Connected && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;
            loop.Server.Inventories.TryGet(pid, out var sinv);
            var served = new Item(4);   // Eaglefire
            sinv.Inventory.items[0].addItem(0, 0, 0, served);
            sinv.Inventory.items[2].clear();
            sinv.Inventory.items[2].addItem(0, 0, 0, new Item(Bottle));
            sinv.Inventory.items[2].raiseStateUpdated();
            yield return Wait(() => player.Inventory.items[0].getItemCount() == 1 && CountOf(player.Inventory, Bottle) == 1, 5);
            player.EquipHotbar(1);
            yield return Ticks(100);
            T.Check($"gun in hand ({player.HeldGunName})", player.HasGunOut);
            if (!player.HasGunOut) yield break;

            var vm = player.VM;
            var menu = new AttachmentMenu { Player = player, VM = vm };
            World.AddChild(menu);
            yield return Ticks(1);
            var offered = AttachmentFit.InBagInstances(player.Inventory, "Barrel", player.Gun?.Caliber ?? 0, player.Gun?.CaliberName, Assets.find(4));
            Item clicked = null; foreach (var o in offered) if (o.Asset.id == Bottle) clicked = o.Item;
            T.Check("the T menu offers it for the barrel", clicked != null);
            if (clicked == null) yield break;
            T.Check("clicking it fits it", menu.ClickFit("Barrel", Bottle, clicked));
            yield return Wait(() => CountOf(sinv.Inventory, Bottle) == 0 && AttachmentFit.InstalledId(served, "Barrel") == Bottle, 5);
            T.Check($"the SERVER spent it from the bag and has it on the gun ({CountOf(sinv.Inventory, Bottle)} left, barrel {AttachmentFit.InstalledId(served, "Barrel")})",
                CountOf(sinv.Inventory, Bottle) == 0 && AttachmentFit.InstalledId(served, "Barrel") == Bottle);
            var heldFit = AttachmentFit.BottleFit(vm.GunName);
            T.Check($"1P: the bottle is drawn, seated where the 3P one is ({vm.DebugBarrelPosition} vs {heldFit.Seat})",
                vm.DebugBarrelIsBottle && vm.DebugBarrelPosition == heldFit.Seat && heldFit.Seat != AttachmentFit.DefaultBarrelHook);
            T.Check("...and the gun is SILENCED from the moment it is fitted -- no re-equip (the sound/flash used to apply only on equip)",
                vm.BarrelSilenced && player.Suppressed);
            yield return Wait(() => loop.Server.CombatState.TryGet(pid, out var c) && c.HeldBarrel == Bottle, 3);
            T.Check("other players are told it is on the gun (the puppet draws it)",
                loop.Server.CombatState.TryGet(pid, out var ce1) && ce1.HeldBarrel == Bottle);

            // ---- 3. THE FIRST SHOT: silent, and it bursts.
            int loud = 0;
            System.Action<Vector3, float> ear = (pos, l) => { if (l >= SoundBus.Gunshot) loud++; };
            SoundBus.OnNoise += ear;
            player.Ammo = 30;
            int burst0 = player.DebugBarrelsBurst;
            bool fired = player.Fire();
            T.Check($"shot 1 fired, and made NO gunshot noise ({loud} alerts)", fired && loud == 0);
            T.Check($"...and the bottle burst on it ({player.DebugBarrelsBurst - burst0})", player.DebugBarrelsBurst == burst0 + 1);
            T.Check("...gone from the gun, the 1P model with it", AttachmentFit.InstalledId(player.HeldItemForTest, "Barrel") <= 0 && !vm.DebugBarrelNodeVisible);
            yield return Wait(() => !vm.BarrelSilenced, 2);
            T.Check("once that shot's flash is over the gun is loud again", !vm.BarrelSilenced && !player.Suppressed);
            yield return Wait(() => AttachmentFit.InstalledId(served, "Barrel") <= 0, 5);
            T.Check($"the SERVER's gun lost it too ({AttachmentFit.InstalledId(served, "Barrel")}) -- or the next echo would put it back",
                AttachmentFit.InstalledId(served, "Barrel") <= 0);
            yield return Wait(() => loop.Server.CombatState.TryGet(pid, out var c) && c.HeldBarrel == 0, 3);
            T.Check("...and other players see it gone", loop.Server.CombatState.TryGet(pid, out var ce2) && ce2.HeldBarrel == 0);
            yield return Ticks(30);
            T.Check("an owner echo later, it has NOT come back", AttachmentFit.InstalledId(player.HeldItemForTest, "Barrel") <= 0 && !vm.DebugBarrelNodeVisible);

            // ---- 4. CONTROL: the second shot is a normal, loud one.
            yield return Ticks(20);
            bool fired2 = player.Fire();
            T.Check($"shot 2 is LOUD ({loud} alert)", fired2 && loud == 1);
            SoundBus.OnNoise -= ear;
        }
    }
}
