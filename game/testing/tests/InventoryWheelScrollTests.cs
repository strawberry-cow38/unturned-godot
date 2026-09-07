using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>The wheel scrolls the storage column anywhere over it, including over buttons.
    ///
    /// master 2026-09-07: "allow scrolling the inventory whenever mouse over that entire section, not just
    /// over a grid section". The rect was never the problem -- it already spans the whole right-hand panel to
    /// the screen edge. The problem was a guard I added earlier the same day to stop a CLICK being stolen from
    /// a Button underneath:
    ///
    ///     if (e is InputEventMouseButton pb &amp;&amp; PressLandsOnButton(pb.GlobalPosition)) return;
    ///
    /// A wheel notch arrives as an InputEventMouseButton too -- the scroll handler on the very next line
    /// proves it, since it reads ButtonIndex == WheelUp off exactly that type. So the guard swallowed every
    /// scroll that happened over a Button, and the grids are the part of the panel with no buttons in it.
    /// That is precisely "it only scrolls over a grid".
    ///
    /// The test aims a wheel at a REAL Button inside the box rather than at empty space, because empty space
    /// scrolled fine the whole time and would have passed against the bug.</summary>
    public sealed class InventoryWheelScrollTests : GameTest
    {
        public override string Name => "inv.wheel_scrolls_over_buttons";
        public override double TimeoutSimSeconds => 20;

        static BaseButton FindButton(Node n)
        {
            if (n is BaseButton b && b.Visible && !b.Disabled) return b;
            foreach (var c in n.GetChildren())
                if (c is Node cn) { var r = FindButton(cn); if (r != null) return r; }
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            // Force a short visible box so the column overflows and the scrollbar arms -- the same harness knob
            // the render tests use. Without this maxScroll is 0, _vscroll is hidden, and the scroll branch is
            // gated off entirely: the test would pass without ever exercising anything.
            System.Environment.SetEnvironmentVariable("UG_INVSCROLLTEST", "220");

            var inv = new PlayerInventory();
            inv.items[PlayerInventory.STORAGE].resize(6, 4);
            var ui = new InventoryUI { Inv = inv };
            World.AddChild(ui);
            ui.Open();
            yield return Ticks(3);

            var box = ui.DebugStorageCol;
            T.Check("the storage column exists", box != null);
            if (box == null) yield break;

            var btn = FindButton(box);
            // NOT a silent skip. If the panel has no button in it, this test cannot see the bug it exists for,
            // and a quiet pass would be worse than a failure.
            T.Check("found a real Button inside the storage box to aim at", btn != null);
            if (btn == null) yield break;

            var at = btn.GetGlobalRect().GetCenter();
            T.Check($"...and it sits inside the scroll region (btn {at})",
                    new Rect2(box.GlobalPosition, box.Size).HasPoint(at));

            float before = ui.DebugScrollY;
            ui._Input(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true, GlobalPosition = at });
            yield return Ticks(2);
            float after = ui.DebugScrollY;
            T.Check($"wheeling over a BUTTON still scrolls the column ({before:0.0} -> {after:0.0})", after > before);

            ui._Input(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, GlobalPosition = at });
            yield return Ticks(2);
            T.Check($"...and back up ({after:0.0} -> {ui.DebugScrollY:0.0})", ui.DebugScrollY < after);

            System.Environment.SetEnvironmentVariable("UG_INVSCROLLTEST", null);
            ui.QueueFree();
        }
    }
}
