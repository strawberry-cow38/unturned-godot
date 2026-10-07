using System.Collections.Generic;
using Godot;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // FluidRules (core) works in BYTES because core cannot see the game's FluidType / WaterQuality enums. The bytes are those
    // enums' values -- append-only and persisted on every Item -- so a renumbering on either side would silently change
    // what the SERVER thinks is drinkable while the client's labels still read right. Pinned here, against the enums.
    public sealed class FluidRulesEnumTests : GameTest
    {
        public override string Name => "fluid.rules_match_enums";
        public override int Tier => 0;

        public override IEnumerable<Step> Run()
        {
            T.Check("None/Water", FluidRules.None == (byte)FluidType.None && FluidRules.Water == (byte)FluidType.Water);
            T.Check("the beverage ids", FluidRules.Soda == (byte)FluidType.Soda && FluidRules.Cola == (byte)FluidType.Cola
                && FluidRules.OrangeJuice == (byte)FluidType.OrangeJuice && FluidRules.Milk == (byte)FluidType.Milk
                && FluidRules.CoconutWater == (byte)FluidType.CoconutWater && FluidRules.EnergyDrink == (byte)FluidType.EnergyDrink
                && FluidRules.AppleJuice == (byte)FluidType.AppleJuice && FluidRules.GrapeJuice == (byte)FluidType.GrapeJuice);
            T.Check("Clean", FluidRules.Clean == (byte)WaterQuality.Clean);
            // the rule itself, written out against the ENUMS: only bad water is undrinkable; autodrink takes clean water + beverages
            var bev = new HashSet<FluidType> { FluidType.Soda, FluidType.Cola, FluidType.OrangeJuice, FluidType.Milk,
                                               FluidType.CoconutWater, FluidType.EnergyDrink, FluidType.AppleJuice, FluidType.GrapeJuice };
            int bad = 0, pairs = 0;
            foreach (FluidType t in System.Enum.GetValues(typeof(FluidType)))
                foreach (WaterQuality q in System.Enum.GetValues(typeof(WaterQuality)))
                {
                    pairs++;
                    bool drink = !(t == FluidType.Water && q != WaterQuality.Clean);
                    bool safe = (t == FluidType.Water && q == WaterQuality.Clean) || bev.Contains(t);
                    if (FluidRules.Drinkable((byte)t, (byte)q) != drink || FluidRules.Safe((byte)t, (byte)q) != safe) bad++;
                }
            T.Check($"Drinkable/Safe agree with the rule over every type x quality ({bad} wrong of {pairs})", pairs >= 60 && bad == 0);
            yield break;
        }
    }

    // DRINKING THAT STICKS, AND FILLING AT A SINK OR A BATHTUB (strawberry 2026-10-07: "change fluid containers + drinking
    // to be less annoying. remove the 'not thirsty' gate. make sure we are actually granting thirst on drink. allow
    // filling containers w clean water from sinks and bathtubs").
    //
    // Driven through the singleplayer loopback, because that is where it was broken: the server owns the bag AND the
    // vitals, so a drink the client makes on its own copy is undone twice -- Water by the next vitals adopt, the bottle by
    // the next inventory echo. Every check below reads the SERVER's copy, then the client's after an echo has landed.
    public sealed class FluidDrinkTapTests : GameTest
    {
        public override string Name => "fluid.drink_and_tap_fill";
        public override double TimeoutSimSeconds => 60;

        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");

        // A map prop, placed the way WorldBuilder places one: its own mesh, the upright basis interior props stand on
        // (euler X 270 -- the meshes are authored Z-up), a trimesh body on the small-prop layer, and the SAME
        // WaterTap.Make call WorldBuilder makes with the mesh's own bounds.
        WaterTap PlaceTapProp(string name, Vector3 at)
        {
            var mesh = ObjMesh.Load(Dir + name + ".obj");
            var xf = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(270f)), at);
            var body = new StaticBody3D { Transform = xf, CollisionLayer = (1u << 6) | (1u << 8) };
            body.AddChild(new CollisionShape3D { Shape = ObjMesh.TrimeshShape(mesh) });
            World.AddChild(body);
            World.AddChild(new MeshInstance3D { Mesh = mesh, Transform = xf });
            var tap = WaterTap.Make(name, xf, mesh.GetAabb());
            World.AddChild(tap);
            return tap;
        }

        static Item ServerBottle(PlayerInventory inv, ushort id)
        {
            for (byte p = 0; p < PlayerInventory.OWNPAGES; p++)
                for (byte i = 0; i < (inv.items[p]?.getItemCount() ?? 0); i++)
                    if (inv.items[p].getItem(i)?.item?.id == id) return inv.items[p].getItem(i).item;
            return null;
        }

        static bool InGrid(PlayerInventory inv, Item it)
        {
            for (byte p = 0; p < inv.items.Length; p++)
                for (byte i = 0; i < (inv.items[p]?.getItemCount() ?? 0); i++)
                    if (ReferenceEquals(inv.items[p].getItem(i)?.item, it)) return true;
            return false;
        }

        // Headless refuses to capture the mouse, and the look scan only runs while it is captured -- so without the seam
        // nothing is ever aimed at. Global, so it is cleared however the test ends.
        public override IEnumerable<Step> Run()
        {
            PlayerController.DebugForceLookScan = true;
            try { foreach (var s in Body()) yield return s; }
            finally { PlayerController.DebugForceLookScan = false; FluidNet.SetGlobalWater(true); }
        }

        IEnumerable<Step> Body()
        {
            ItemCatalog.RegisterAll();
            const ushort Bottle = 14;   // Bottled Water
            var wa = Assets.find(Bottle);
            float cap = wa?.fluidCapacity ?? 0f;
            T.Check($"bottled water is a fluid container ({cap} mL)", wa != null && cap > 0f);
            if (wa == null) yield break;
            FluidNet.SetGlobalWater(true);

            Rigs.Ground(World);
            // the sink's FRONT is mesh +Y, which the upright basis turns to world -Z: stand on that side
            var sink = PlaceTapProp("Counter_1", new Vector3(0f, 0f, 0f));
            var tub = PlaceTapProp("Tub_0", new Vector3(6f, 0f, 0f));
            var driver = new SimDriver(); World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, -1.4f));
            yield return Ticks(2);
            var loop = new MpLoopback { Player = player, Driver = driver, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Wait(() => loop.Client.State == NetSessionState.Connected && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;
            loop.Server.Inventories.TryGet(pid, out var sinv);
            loop.Server.Vitals.TryGet(pid, out var vit);
            var diag = loop.Server.Transactions.Diag;
            T.Check("server owns the bag and the vitals", sinv != null && vit != null && player.InventoryIsServerOwned);
            if (sinv == null || vit == null) yield break;

            // a gun in slot 0, held FIRST: the inventory's Hold used to leave the PREVIOUS held item's cell recorded, so
            // a bottle held after a gun was addressed to the gun's cell
            sinv.Inventory.items[0].addItem(0, 0, 0, new Item(4));
            sinv.Inventory.items[2].clear();
            // autodrink OFF until section 7: it is ON by default, and the server would sip this bottle the moment water
            // drops under half -- before the hand ever got to it
            var loot = Assets.makeLoot(Bottle); loot.autoDrink = false;
            sinv.Inventory.items[2].addItem(0, 0, 0, loot);
            sinv.Inventory.items[2].raiseStateUpdated();
            yield return Wait(() => player.Inventory.items[0].getItemCount() == 1 && player.Inventory.items[2].getItemCount() == 1, 5);
            player.EquipHotbar(1);
            yield return Ticks(30);

            // ---- 1. HOLD IT the way the inventory's Hold button does, then DRINK while thirsty.
            var cj = player.Inventory.items[2].getItem(0);
            player.EquipHeldFluidContainer(cj.GetAsset(), cj.item);
            yield return Ticks(5);
            T.Check("the bottle is in hand", player.DebugHeldFluidItem != null && player.DebugHeldFluidItem.id == Bottle);
            vit.Sim.Water = 0.30f;
            yield return Wait(() => Mathf.Abs(player.Water - 0.30f) < 0.01f, 3);
            T.Check($"thirsty: server water 0.30, client adopted it ({player.Water:0.00})", Mathf.Abs(player.Water - 0.30f) < 0.01f);
            player.DebugLookAt(player.GlobalPosition + new Vector3(0f, 1.5f, -10f));   // away from the sink: LMB drinks
            yield return Ticks(5);
            long drinks0 = diag.DrinksApplied;
            player.DebugDrinkContainer();   // LMB
            yield return Wait(() => diag.DrinksApplied > drinks0, 3);
            var sb = ServerBottle(sinv.Inventory, Bottle);
            float expect = Mathf.Min(1f, 0.30f + cap * FluidRules.HydrationPerML);
            T.Check($"the SERVER drank it: its bottle is empty ({sb?.fluidAmount} mL) and its water rose to {vit.Sim.Water:0.00} (want {expect:0.00})",
                diag.DrinksApplied == drinks0 + 1 && sb != null && sb.fluidAmount <= 0.01f && Mathf.Abs(vit.Sim.Water - expect) < 0.01f);
            yield return Wait(() => Mathf.Abs(player.Water - expect) < 0.02f, 3);
            T.Check($"...and the CLIENT's thirst bar shows it ({player.Water:0.00})", Mathf.Abs(player.Water - expect) < 0.02f);
            yield return Ticks(40);
            var held = player.DebugHeldFluidItem;
            T.Check($"an echo later the bottle in hand is the LIVE one in the bag, still empty ({held?.fluidAmount} mL) -- not refilled, not a dead copy",
                held != null && InGrid(player.Inventory, held) && held.fluidAmount <= 0.01f);

            // ---- 2. FILL AT THE SINK: aim into it, RMB.
            var sinkTop = sink.GlobalTransform * sink.LocalBounds.GetCenter();
            player.DebugLookAt(sinkTop);
            yield return Wait(() => player.DebugAimedTap == sink, 2);
            T.Check($"aiming into the sink finds it ({player.DebugAimedTap?.DisplayName ?? "nothing"})", player.DebugAimedTap == sink);
            long fills0 = diag.TapFillsApplied;
            player.DebugSecondaryUse();   // RMB
            yield return Wait(() => diag.TapFillsApplied > fills0, 3);
            sb = ServerBottle(sinv.Inventory, Bottle);
            T.Check($"the SERVER filled it: {sb?.fluidAmount}/{cap} mL, type {sb?.fluidType}, quality {sb?.fluidQuality}",
                diag.TapFillsApplied == fills0 + 1 && sb != null && Mathf.Abs(sb.fluidAmount - cap) < 0.5f
                && sb.fluidType == FluidRules.Water && sb.fluidQuality == FluidRules.Clean);
            yield return Ticks(40);
            held = player.DebugHeldFluidItem;
            T.Check($"...and the bottle in hand reads full after the echo ({held?.fluidAmount} mL)", held != null && Mathf.Abs(held.fluidAmount - cap) < 0.5f);

            // ---- 3. NO "NOT THIRSTY" GATE: at full water, LMB still drinks.
            vit.Sim.Water = 1f;
            yield return Wait(() => player.Water > 0.99f, 3);
            player.DebugLookAt(player.GlobalPosition + new Vector3(0f, 1.5f, -10f));
            yield return Ticks(5);
            drinks0 = diag.DrinksApplied;
            int sent0 = player.DebugDrinksSent;
            player.DebugDrinkContainer();
            yield return Wait(() => diag.DrinksApplied > drinks0, 3);
            sb = ServerBottle(sinv.Inventory, Bottle);
            T.Check($"at full water the drink still goes through (sent {player.DebugDrinksSent - sent0}, applied {diag.DrinksApplied - drinks0}, bottle {sb?.fluidAmount} mL)",
                player.DebugDrinksSent == sent0 + 1 && diag.DrinksApplied == drinks0 + 1 && sb != null && sb.fluidAmount <= 0.01f);
            T.Check($"...and water stays capped at 1 ({vit.Sim.Water:0.00})", vit.Sim.Water <= 1f && vit.Sim.Water > 0.99f);

            // ---- 4. TAINTED WATER: refused to drink, and the BATHTUB pours it out and refills clean.
            sb.fluidType = FluidRules.Water; sb.fluidAmount = 200f; sb.fluidQuality = 1;   // tainted
            loop.Server.Inventories.ServerMarkDirty(pid);
            yield return Wait(() => player.DebugHeldFluidItem != null && player.DebugHeldFluidItem.fluidQuality == 1, 3);
            sent0 = player.DebugDrinksSent;
            player.DebugDrinkContainer();
            yield return Ticks(10);
            T.Check($"tainted water is still refused, and nothing is sent ({player.DebugDrinksSent - sent0} sent)", player.DebugDrinksSent == sent0);
            player.GlobalPosition = new Vector3(6f, 1f, -2.4f);   // in front of the tub
            yield return Ticks(5);
            var tubIn = tub.GlobalTransform * tub.LocalBounds.GetCenter();
            player.DebugLookAt(tubIn);
            yield return Wait(() => player.DebugAimedTap == tub, 2);
            T.Check($"aiming into the bathtub finds it ({player.DebugAimedTap?.DisplayName ?? "nothing"})", player.DebugAimedTap == tub);
            fills0 = diag.TapFillsApplied;
            player.DebugSecondaryUse();
            yield return Wait(() => diag.TapFillsApplied > fills0, 3);
            sb = ServerBottle(sinv.Inventory, Bottle);
            T.Check($"the tub tipped out the tainted water and filled it clean ({sb?.fluidAmount} mL, quality {sb?.fluidQuality})",
                diag.TapFillsApplied == fills0 + 1 && sb != null && Mathf.Abs(sb.fluidAmount - cap) < 0.5f && sb.fluidQuality == FluidRules.Clean);

            // ---- 5. THE SERVER'S REACH CHECK HAS TEETH: empty it, walk away, and send the fill anyway (a forged client).
            player.DebugLookAt(player.GlobalPosition + new Vector3(0f, 1.5f, -10f));
            yield return Ticks(5);
            player.DebugDrinkContainer();
            yield return Wait(() => ServerBottle(sinv.Inventory, Bottle)?.fluidAmount <= 0.01f, 3);
            player.GlobalPosition = new Vector3(30f, 1f, 30f);
            yield return Ticks(20);
            long rej0 = diag.TapFillsRejected;
            fills0 = diag.TapFillsApplied;
            player.NetFillAtTap(2, 0, 0, Bottle);
            yield return Wait(() => diag.TapFillsRejected > rej0, 3);
            sb = ServerBottle(sinv.Inventory, Bottle);
            T.Check($"30 m from any tap the server REFUSES the fill ({diag.TapFillsRejected - rej0} refused, {diag.TapFillsApplied - fills0} applied, bottle {sb?.fluidAmount} mL)",
                diag.TapFillsRejected == rej0 + 1 && diag.TapFillsApplied == fills0 && sb != null && sb.fluidAmount <= 0.01f);

            // ---- 6. WATER OFF: the sink is dry, nothing is sent.
            player.GlobalPosition = new Vector3(0f, 1f, -1.4f);
            yield return Ticks(5);
            FluidNet.SetGlobalWater(false);
            player.DebugLookAt(sinkTop);
            yield return Wait(() => player.DebugAimedTap == sink, 2);
            int fsent0 = player.DebugTapFillsSent;
            player.DebugSecondaryUse();
            yield return Ticks(10);
            T.Check($"with the water off the sink fills nothing ({player.DebugTapFillsSent - fsent0} sent)", player.DebugTapFillsSent == fsent0 && WaterTap.RunningNear(player.GlobalPosition) == false);
            FluidNet.SetGlobalWater(true);
            T.Check("CONTROL: with it back on, the server sees a running tap from here", WaterTap.RunningNear(player.GlobalPosition));

            // ---- 7. AUTODRINK, on the server: under half water, a full bottle in the bag is sipped; above half, it is not.
            sb.fluidType = FluidRules.Water; sb.fluidAmount = cap; sb.fluidQuality = FluidRules.Clean; sb.autoDrink = true;
            loop.Server.Inventories.ServerMarkDirty(pid);
            player.EquipUnarmed();
            vit.Sim.Water = 0.9f;
            long sips0 = loop.Server.AutoDrink.Sips;
            yield return Ticks(100);
            T.Check($"CONTROL: at 90% water autodrink leaves it alone ({loop.Server.AutoDrink.Sips - sips0} sips)", loop.Server.AutoDrink.Sips == sips0);
            vit.Sim.Water = 0.30f;
            long tick0 = loop.Server.Session.CurrentTick;
            yield return Wait(() => loop.Server.AutoDrink.Sips >= sips0 + 2, 4);
            Log.Print($"[fluidtest] autodrink window: ticks {loop.Server.Session.CurrentTick - tick0}, sips {loop.Server.AutoDrink.Sips - sips0}, steps {loop.Server.AutoDrink.Steps}"
                + $" cooldown {loop.Server.AutoDrink.OnCooldown} notThirsty {loop.Server.AutoDrink.NotThirsty} dead {loop.Server.AutoDrink.Dead} noBottle {loop.Server.AutoDrink.NoBottle} refused {loop.Server.AutoDrink.Refused} water {vit.Sim.Water:0.000}");
            sb = ServerBottle(sinv.Inventory, Bottle);
            var active = FluidRules.ActiveAutoDrink(sinv.Inventory);
            var ad = loop.Server.AutoDrink;
            T.Check($"at 30% the server sips from it ({loop.Server.AutoDrink.Sips - sips0} sips, bottle {sb?.fluidAmount} mL, water {vit.Sim.Water:0.00}"
                    + $" | alive {loop.Server.CombatState.IsAlive(pid)}, live vitals {(loop.Server.Vitals.TryGet(pid, out var lv) && ReferenceEquals(lv, vit))},"
                    + $" active bottle {(active == null ? "none" : ReferenceEquals(active, sb) ? "this one" : "another")}, autoDrink {sb?.autoDrink}, in bag {(sb != null && InGrid(sinv.Inventory, sb))}"
                    + $" | steps {ad.Steps} cooldown {ad.OnCooldown} notThirsty {ad.NotThirsty} dead {ad.Dead} noBottle {ad.NoBottle} refused {ad.Refused})",
                loop.Server.AutoDrink.Sips >= sips0 + 2 && sb != null && sb.fluidAmount < cap - 1f && vit.Sim.Water > 0.30f);
            yield return Wait(() => player.Water > 0.31f, 3);
            T.Check($"...and the client's bar rises with it ({player.Water:0.00})", player.Water > 0.31f);
        }
    }
}
