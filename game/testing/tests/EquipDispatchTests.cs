using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Regression for the bug master reported on 2026-09-11 as "cant equip spraypaints" / "cant equip umbrella".
    //
    // Both features WORKED. PlayerController's dispatch knew about them and a hotbar number key equipped them
    // fine. What was broken was that InventoryUI kept a SECOND, private list of what counts as holdable
    // (HasHandAction) to decide whether to draw an Equip button at all, and four features had been added to the
    // dispatch without being added to that list -- the 32 spraypaints, the carjack, the 8 umbrellas and the
    // throwables. Right-clicking any of them in the bag offered Drop/Close and nothing else, which is
    // indistinguishable from the item being broken.
    //
    // THE OLD TEST DID NOT CATCH IT, and that is the interesting part. InventoryHandActions (InventoryTests.cs)
    // was written for the SAME bug class -- the Rope, master 2026-07-20: "the option to hold is NOT THERE" --
    // and asserts one hand-built ItemAsset per KIND it knows about. It can only ever catch a regression in a
    // branch that already exists; a NEW feature adds a branch nobody writes an assertion for, so the list it
    // guards drifted four more times underneath a green test.
    //
    // The fix was structural: PlayerController.KindOf is now the ONE ordered list, both the predicate and the
    // dispatch switch on it, and InventoryUI asks it. These checks pin that, derived from the real catalog
    // rather than from a list retyped here -- so a fifth feature is covered without anyone remembering to
    // come back and add a line.
    public class EquipDispatchCoverage : GameTest
    {
        public override string Name => "equip.dispatch_coverage";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            // ---- 1. the menu's predicate and the dispatch's classification agree on EVERY item in the catalog.
            // This is the check that rejects the shipped bug: before the fix, HasHandAction(1840) was false while
            // KindOf(1840) was Paint, for all 32 paints, 8 umbrellas, the carjack and every throwable.
            int items = 0, disagree = 0; string firstBad = null;
            var firstOfKind = new Dictionary<PlayerController.HandKind, ushort>();
            for (int i = 0; i <= ushort.MaxValue; i++)
            {
                var a = Assets.find((ushort)i);
                if (a == null) continue;
                items++;
                var kind = PlayerController.KindOf(a);
                if (kind != PlayerController.HandKind.None && !firstOfKind.ContainsKey(kind)) firstOfKind[kind] = (ushort)i;
                bool menu = InventoryUI.HasHandAction(a);
                if (menu != (kind != PlayerController.HandKind.None))
                {
                    disagree++;
                    firstBad ??= $"{a.itemName} ({i}): menu={menu} kind={kind}";
                }
            }
            T.Check($"the catalog actually loaded ({items} items)", items > 1000);
            T.Check($"menu predicate agrees with the dispatch on all {items} items" + (firstBad != null ? $" -- first offender {firstBad}" : ""), disagree == 0);

            // ---- 2. every HandKind is reachable from the REAL catalog. A kind no item resolves to means a
            // feature's id table does not match the shipped catalog -- exactly how the spraypaints silently did
            // nothing on the box when content/vehicle_paints.tsv had not been copied across.
            foreach (PlayerController.HandKind k in System.Enum.GetValues(typeof(PlayerController.HandKind)))
            {
                if (k == PlayerController.HandKind.None) continue;
                T.Check($"some real item resolves to HandKind.{k}" + (firstOfKind.TryGetValue(k, out var rid) ? $" (id {rid})" : ""),
                        firstOfKind.ContainsKey(k));
            }

            // ---- 3. the four families that were missing, named individually so a failure says WHICH broke.
            T.Check("Midnight Black spraypaint (1840) offers a hand action", InventoryUI.HasHandAction(Assets.find(1840)));
            T.Check("the Carjack (277) offers a hand action", InventoryUI.HasHandAction(Assets.find(277)));
            T.Check("Black Umbrella (1103) offers a hand action", InventoryUI.HasHandAction(Assets.find(1103)));
            T.Check("Red Smoke (266) offers a hand action", InventoryUI.HasHandAction(Assets.find(266)));

            // ---- 4. ORDER. A water bottle is both a fluid container and a consumable; it must be held as the
            // container or it gets chugged whole. This is why KindOf is an ordered chain and not a set of flags.
            T.Check("Bottled Water (14) classifies as Fluid, not Consumable",
                    PlayerController.KindOf(Assets.find(14)) == PlayerController.HandKind.Fluid);
            T.Check("a plain SUPPLY item with no feature claiming it is not holdable",
                    PlayerController.KindOf(new ItemAsset { id = 63333, type = EItemType.SUPPLY }) == PlayerController.HandKind.None);
            T.Check("null asset -> HandKind.None", PlayerController.KindOf(null) == PlayerController.HandKind.None);

            // ---- 5. ...and KindOf saying yes must mean the dispatch actually equips it. A new enum case with no
            // switch arm returns false here; nothing else in the suite would notice.
            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(2);

            foreach (var kv in firstOfKind)
            {
                var a = Assets.find(kv.Value);
                T.Check($"HandKind.{kv.Key} ({a?.itemName}, {kv.Value}) equips through the dispatch",
                        p.EquipItemAsset(a, new Item(kv.Value)));
                yield return Ticks(1);
            }

            p.EquipUnarmed();
            yield return Ticks(1);
            p.QueueFree();
            yield break;
        }
    }
}
