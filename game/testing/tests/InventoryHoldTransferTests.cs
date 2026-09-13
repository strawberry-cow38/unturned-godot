using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Hold-to-transfer (strawberry 2026-09-13): "holding h and hovering over an item in your inventory
    /// with a container open will start a short delay for each item ... when complete it transfers the item into
    /// the container. and the same applies the other way too ... both are cancelled when closing the ui."
    ///
    /// Four claims, each asserted separately because each can break while the others work: it takes TIME, it goes
    /// BOTH ways, only ONE charge runs at a time, and closing the UI kills it.
    ///
    /// ⚠ WHAT THIS CANNOT SEE. The harness injects the two things headless does not have -- whether the key is
    /// down, and where the pointer is. So it does NOT prove H is bound to QuickTransfer, nor that a real cursor
    /// lands where DebugCellPoint says. Everything downstream of those two reads is the production path: the
    /// injected point still goes through PointToCell, and the move still goes through QuickAction.</summary>
    public class InventoryHoldTransferTests : GameTest
    {
        public override string Name => "inv.hold_transfer";
        public override double TimeoutSimSeconds => 30;

        // Hold over a cell for `seconds` of SIM time, re-aiming at the same cell each frame the way a still
        // cursor does. Returns false if the grid has no layout -- unrunnable, not a pass.
        static bool Aim(InventoryUI ui, byte page, byte x, byte y)
        {
            if (!ui.DebugCellPoint(page, x, y, out Vector2 pt)) return false;
            ui.DebugQtMouse = pt; ui.DebugQtHeld = true;
            return true;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var inv = new PlayerInventory();
            inv.items[PlayerInventory.STORAGE].resize(6, 4);   // a crate is open
            var ui = new InventoryUI { Inv = inv };
            World.AddChild(ui);
            ui.Open();
            yield return Ticks(4);

            T.Check("seeded an item into Hands", inv.items[2].tryAddItem(new Item(15)));   // Medkit 2x2
            yield return Ticks(2);
            var jar = inv.items[2].getItem(0);
            if (!Aim(ui, 2, jar.x, jar.y))
            {
                T.Check("UNRUNNABLE: the Hands grid has no layout headless", false);
                yield break;
            }

            // ---- 1. IT TAKES TIME. A keypress that moved instantly would pass every other check here.
            yield return Ticks(4);   // ~0.08 s, well short of the 0.35 s charge
            T.Check($"still charging, not yet moved ({ui.DebugQtProgress:0.##})",
                    inv.items[2].getItemCount() == 1 && ui.DebugQtProgress > 0f && ui.DebugQtProgress < 1f);

            // ---- 2. AND THEN IT MOVES, into the crate.
            int guard = 0;
            while (inv.items[2].getItemCount() == 1 && guard++ < 120) { Aim(ui, 2, jar.x, jar.y); yield return Ticks(1); }
            T.Check($"it left your bag ({guard} ticks)", inv.items[2].getItemCount() == 0);
            T.Check($"...and arrived in the crate ({inv.items[PlayerInventory.STORAGE].getItemCount()})",
                    inv.items[PlayerInventory.STORAGE].getItemCount() == 1);

            // ---- 3. THE OTHER WAY. Same gesture on the crate item must bring it back.
            var cj = inv.items[PlayerInventory.STORAGE].getItem(0);
            if (!Aim(ui, PlayerInventory.STORAGE, cj.x, cj.y))
            {
                T.Check("UNRUNNABLE: the crate grid has no layout headless", false);
                yield break;
            }
            guard = 0;
            while (inv.items[PlayerInventory.STORAGE].getItemCount() == 1 && guard++ < 120)
            { Aim(ui, PlayerInventory.STORAGE, cj.x, cj.y); yield return Ticks(1); }
            T.Check($"it came back out of the crate ({guard} ticks)", inv.items[PlayerInventory.STORAGE].getItemCount() == 0);

            // ---- 4. HOVERING COLLECTS, IT DOES NOT ABANDON. strawberry: "when hovering, add the item to the
            // transfer delay queue, dont have to hold H on each item." The first cut restarted the charge on
            // every sweep, so touching a second item threw the first away -- this asserts the opposite, and
            // would fail against that build.
            inv.items[2].tryAddItem(new Item(15));
            inv.items[2].tryAddItem(new Item(15));
            yield return Ticks(2);
            int bagBefore = inv.items[2].getItemCount();
            T.Check($"two items to sweep over ({bagBefore})", bagBefore >= 2);
            var a = inv.items[2].getItem(0);
            var b = inv.items[2].getItem(1);

            Aim(ui, 2, a.x, a.y);
            yield return Ticks(2);
            Aim(ui, 2, b.x, b.y);          // sweep onto the second WITHOUT waiting for the first
            yield return Ticks(1);
            T.Check($"both are queued after one sweep ({ui.DebugQtQueued})", ui.DebugQtQueued == 2);

            // ...and only the head is counting down. The queue is not two parallel timers.
            float headAt = ui.DebugQtProgress;
            yield return Ticks(2);
            T.Check($"one delay running, and it is advancing ({headAt:0.##} -> {ui.DebugQtProgress:0.##})",
                    ui.DebugQtProgress > headAt);

            // Both drain, in their own time, with the cursor now parked OFF the grid entirely -- proving the
            // queue is what carries them and not the hover.
            ui.DebugQtMouse = new Vector2(-500f, -500f);
            int guard2 = 0;
            while (ui.DebugQtQueued > 0 && guard2++ < 200) yield return Ticks(1);
            T.Check($"the queue drained with the cursor away ({guard2} ticks)", ui.DebugQtQueued == 0);
            T.Check($"both items left the bag ({bagBefore} -> {inv.items[2].getItemCount()})",
                    inv.items[2].getItemCount() == bagBefore - 2);
            // Two delays, not one: draining must not collapse into a single tick's catch-up.
            // ⚠ HONEST LIMIT: I could not construct a mutation that fails ONLY this bound. Both attempts --
            // draining the queue in one loop pass, and carrying the charge to the next head -- are neutralised
            // by the head-change reset in TickQuickTransfer, which zeroes _qtT whenever the front of the queue
            // changes. So the per-item delay is genuinely covered (removing the charge guard fails the check
            // above), and this line is a REDUNDANT floor rather than an independently-proven one. Kept because
            // it would catch a future rewrite that drops that reset, but not counted as teeth.
            int minTicks = (int)(2 * InventoryUI.QuickTransferSeconds / 0.02f) - 8;
            T.Check($"it really took two delays ({guard2} ticks, floor {minTicks})", guard2 >= minTicks);

            // ---- 5. CLOSING THE UI CANCELS. A charge that outlived the crate would fire into a container
            // the player has already walked away from.
            inv.items[2].tryAddItem(new Item(15));
            yield return Ticks(2);
            var c = inv.items[2].getItem((byte)(inv.items[2].getItemCount() - 1));
            Aim(ui, 2, c.x, c.y);
            yield return Ticks(12);
            T.Check($"charged again before closing ({ui.DebugQtProgress:0.##})", ui.DebugQtProgress > 0.4f);
            int beforeClose = inv.items[2].getItemCount();
            ui.Close();
            yield return Ticks(2);
            T.Check($"the charge AND the queue died with the ui ({ui.DebugQtProgress:0.##}, {ui.DebugQtQueued} queued)",
                    ui.DebugQtProgress == 0f && ui.DebugQtQueued == 0);
            T.Check($"and nothing transferred after the close ({beforeClose} -> {inv.items[2].getItemCount()})",
                    inv.items[2].getItemCount() == beforeClose);

            // ---- 6. RELEASING H DOES NOT CANCEL WHAT IS ALREADY QUEUED. strawberry: "continue delay progress
            // even without H held, as long as it was held while the item was added to the queue." H is the
            // ENQUEUE key; letting go stops you adding more, it does not throw away the run you committed to.
            // Step 4 above parks the cursor off the grid but keeps the key DOWN, so it never covered this.
            ui.Open();
            yield return Ticks(4);
            while (inv.items[2].getItemCount() < 2) inv.items[2].tryAddItem(new Item(15));
            yield return Ticks(2);
            int bag6 = inv.items[2].getItemCount();
            var d6 = inv.items[2].getItem(0);
            var e6 = inv.items[2].getItem(1);
            Aim(ui, 2, d6.x, d6.y);
            yield return Ticks(2);
            Aim(ui, 2, e6.x, e6.y);
            yield return Ticks(1);
            T.Check($"queued two while holding ({ui.DebugQtQueued})", ui.DebugQtQueued == 2);

            ui.DebugQtHeld = false;                       // LET GO
            ui.DebugQtMouse = new Vector2(-500f, -500f);
            yield return Ticks(2);
            T.Check($"the queue survived the release ({ui.DebugQtQueued} still queued)", ui.DebugQtQueued == 2);
            int guard6 = 0;
            while (ui.DebugQtQueued > 0 && guard6++ < 200) yield return Ticks(1);
            T.Check($"and both still transferred with the key up ({bag6} -> {inv.items[2].getItemCount()})",
                    inv.items[2].getItemCount() == bag6 - 2);

            // The other half of the same rule, and the one that stops this becoming "hover transfers things":
            // with the key UP, hovering must add NOTHING. Without this the change above reads as a pass while
            // the gesture has quietly become keyless.
            while (inv.items[2].getItemCount() < 1) inv.items[2].tryAddItem(new Item(15));
            yield return Ticks(2);
            var f6 = inv.items[2].getItem(0);
            ui.DebugQtHeld = false;
            if (ui.DebugCellPoint(2, f6.x, f6.y, out Vector2 pt6)) ui.DebugQtMouse = pt6;
            yield return Ticks(6);
            T.Check($"hovering with the key UP enqueues nothing ({ui.DebugQtQueued})", ui.DebugQtQueued == 0);
        }
    }
}
