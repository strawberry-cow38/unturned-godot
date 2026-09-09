using Godot;
using SDG.Unturned;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // `give <item id|name> [quantity]` (strawberry 2026-09-09: "change the spawn command to take a quantity
    // arg after the item name/id").
    //
    // Drives the REAL console dispatch and counts UNITS IN THE BAG afterwards, rather than testing the parse
    // in isolation. The parse is the easy half; the half that can silently be wrong is the packing -- amount
    // is a byte, so a stackSize the asset reports above 255 wraps, and a magazine's `amount` means loaded
    // rounds rather than "how many magazines", so writing a quantity into it hands you one over-full mag
    // instead of N mags.
    //
    // The NAME cases are here because `arg` is the whole rest of the line: splitting a quantity off the end
    // must not break `give 12 Gauge Buckshot`, and `give 113` must still read as an id and not as a quantity
    // with an empty name.
    public sealed class ConsoleGiveQuantity : GameTest
    {
        public override string Name => "console.give_quantity";
        public override double TimeoutSimSeconds => 60;

        static int Units(PlayerInventory inv, ushort id)
        {
            int n = 0;
            for (int page = 0; page < inv.items.Length; page++)
            {
                var items = inv.items[page];
                if (items == null) continue;
                for (int i = 0; i < items.getItemCount(); i++)
                {
                    var jar = items.getItem((byte)i);
                    if (jar?.item != null && jar.item.id == id) n += jar.item.amount;
                }
            }
            return n;
        }

        static int Stacks(PlayerInventory inv, ushort id)
        {
            int n = 0;
            for (int page = 0; page < inv.items.Length; page++)
            {
                var items = inv.items[page];
                if (items == null) continue;
                for (int i = 0; i < items.getItemCount(); i++)
                {
                    var jar = items.getItem((byte)i);
                    if (jar?.item != null && jar.item.id == id) n++;
                }
            }
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var player = Rigs.Player(World, new Vector3(0, 2, 0));
            var console = new DevConsole { Player = player };
            World.AddChild(console);
            yield return Ticks(2);

            var inv = player.Inventory;
            T.Check("the rig has an inventory", inv != null);

            // 5.56 FMJ: stackSize 128, so 200 units must arrive as 128 + 72 across two stacks.
            const ushort FMJ = 5004;
            int cap = Assets.find(FMJ)?.stackSize ?? 0;
            T.Check($"5.56 stackSize is 128 (got {cap})", cap == 128);
            console.RunForTest("give 5004 200");
            yield return Ticks(4);
            T.Check($"`give 5004 200` put 200 units in the bag (got {Units(inv, FMJ)})", Units(inv, FMJ) == 200);
            T.Check($"...packed into 2 stacks, not 200 (got {Stacks(inv, FMJ)})", Stacks(inv, FMJ) == 2);

            // A quantity ABOVE the byte ceiling. stackSize is an int on the asset, amount is a byte, so this is
            // where an unclamped cast wraps to zero and hands out empty stacks that still look like items.
            console.RunForTest("give 5004 300");
            yield return Ticks(4);
            T.Check($"`give 5004 300` added exactly 300 more (total {Units(inv, FMJ)})", Units(inv, FMJ) == 500);

            // BY NAME, with spaces, and a quantity on the end -- the case the trailing-integer split could break.
            const ushort BUCK = 113;
            console.RunForTest("give 12 Gauge Buckshot 40");
            yield return Ticks(4);
            int buckCap = Assets.find(BUCK)?.stackSize ?? 0;
            T.Check($"`give 12 Gauge Buckshot 40` gave 40 (got {Units(inv, BUCK)})", Units(inv, BUCK) == 40);
            T.Check($"...in ceil(40/{buckCap}) stacks (got {Stacks(inv, BUCK)})",
                    Stacks(inv, BUCK) == (40 + buckCap - 1) / buckCap);

            // BY NAME WITHOUT a quantity must still work, and must add exactly one UNIT -- the old behaviour.
            // Asserting the STACK COUNT here is wrong and this test caught me doing it: 40 buckshot packs as
            // 32 + 8, so the single extra shell merges into the part-filled stack and the count does not move.
            // Units is the property that matters; stack count is an artefact of what was already in the bag.
            int before = Units(inv, BUCK);
            console.RunForTest("give 12 Gauge Buckshot");
            yield return Ticks(4);
            T.Check($"a bare name still gives exactly one unit ({before} -> {Units(inv, BUCK)})",
                    Units(inv, BUCK) == before + 1);

            // A BARE ID must read as the id, not as a quantity with an empty name.
            const ushort BLK = 5005;
            console.RunForTest("give 5005");
            yield return Ticks(4);
            T.Check($"`give 5005` resolved the id and gave one (got {Units(inv, BLK)})", Units(inv, BLK) >= 1);

            // CONTROL: a quantity attached to something that resolves to NOTHING must give nothing at all --
            // otherwise every check above would pass on a console that hands out items for any input.
            int totalBefore = 0;
            foreach (var a in Assets.all()) totalBefore += Units(inv, (ushort)a.id);
            console.RunForTest("give definitelynotanitem 25");
            yield return Ticks(4);
            int totalAfter = 0;
            foreach (var a in Assets.all()) totalAfter += Units(inv, (ushort)a.id);
            T.Check($"a nonsense name with a quantity gives nothing ({totalBefore} -> {totalAfter})", totalAfter == totalBefore);

            yield break;
        }
    }
}
