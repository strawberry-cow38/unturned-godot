using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // craft.queue: the crafting queue's ESCROW. Queuing a job consumes its ingredients into limbo; the timer
    // produces one output per craft-second (a xN job pops one at a time); cancelling hands the remaining
    // ingredients back. Drives CraftingMenu's queue headless via the Debug* hooks (no scene tree needed).
    public class CraftQueueTest : GameTest
    {
        public override string Name => "craft.queue";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var scrap = Assets.find(67); var torch = Assets.find(76);
            T.Check("scrap + blowtorch assets resolve", scrap != null && torch != null);

            // a synthetic 2-scrap -> 1-blowtorch craft (Outputs populated so OutAsset resolves the produced item)
            var bp = new BlueprintDef { Operation = "Craft", Name = "queue-synthetic" };
            bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = scrap.guid, Amount = 2, Consume = true });
            bp.Outputs.Add(new BlueprintDef.Ingredient { Guid = torch.guid, Amount = 1, Consume = true });

            var pinv = new PlayerInventory();
            pinv.tryAddItem(new Item(67, 4));   // 4 scrap = two crafts' worth
            var menu = new CraftingMenu { Inv = pinv };

            menu.DebugEnqueue(bp, 2);   // consume 2x2 = 4 scrap into limbo
            T.Check("enqueue moved 4 scrap into limbo (0 left)", pinv.getItemCount(67) == 0);
            T.Check("one job queued", menu.DebugQueueCount == 1);
            T.Check("nothing produced yet", pinv.getItemCount(76) == 0);

            menu.DebugTick(1.1f);
            T.Check("after 1s: 1 blowtorch produced", pinv.getItemCount(76) == 1);
            T.Check("after 1s: job still running (1 unit left)", menu.DebugQueueCount == 1);

            menu.DebugTick(1.1f);
            T.Check("after 2s: 2nd blowtorch produced", pinv.getItemCount(76) == 2);
            T.Check("after 2s: queue drained", menu.DebugQueueCount == 0);
            menu.Free();

            // cancel returns the escrow for the REMAINING units
            var pinv2 = new PlayerInventory();
            pinv2.tryAddItem(new Item(67, 6));
            var menu2 = new CraftingMenu { Inv = pinv2 };
            menu2.DebugEnqueue(bp, 3);   // 6 scrap -> limbo
            T.Check("enqueue3 took 6 scrap", pinv2.getItemCount(67) == 0);
            menu2.DebugCancelActive();
            T.Check("cancel returned all 6 scrap", pinv2.getItemCount(67) == 6);
            T.Check("cancel emptied the queue", menu2.DebugQueueCount == 0);
            menu2.Free();

            // RMB "move to start": the promoted job becomes the active (rightmost) one, crafted next
            var bpB = new BlueprintDef { Operation = "Craft", Name = "queue-synthetic-B" };
            bpB.Inputs.Add(new BlueprintDef.Ingredient { Guid = scrap.guid, Amount = 2, Consume = true });
            bpB.Outputs.Add(new BlueprintDef.Ingredient { Guid = torch.guid, Amount = 1, Consume = true });

            var pinv3 = new PlayerInventory();
            pinv3.tryAddItem(new Item(67, 8));
            var menu3 = new CraftingMenu { Inv = pinv3 };
            menu3.DebugEnqueue(bp, 1);     // A queued first -> rightmost/active
            menu3.DebugEnqueue(bpB, 1);    // B queued second -> index 0 (leftmost/newest)
            T.Check("first-queued job is the active one", ReferenceEquals(menu3.DebugActiveBp, bp));
            menu3.DebugMoveToStart(0);     // promote B (index 0) to the front
            T.Check("promoted job is now active", ReferenceEquals(menu3.DebugActiveBp, bpB));
            T.Check("still two jobs queued", menu3.DebugQueueCount == 2);
            menu3.Free();

            // QueueCraft (the inventory quick-craft entry point) escrows + queues like the CRAFT button
            var pinv4 = new PlayerInventory(); pinv4.tryAddItem(new Item(67, 4));
            var menu4 = new CraftingMenu { Inv = pinv4 };
            menu4.QueueCraft(bp, 2);
            T.Check("QueueCraft consumed ingredients into limbo", pinv4.getItemCount(67) == 0);
            T.Check("QueueCraft queued one job", menu4.DebugQueueCount == 1);
            menu4.Free();

            yield break;
        }
    }
}
