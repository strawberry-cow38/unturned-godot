using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Walking with the dashboard open, and Tab being the way OUT of it.
    ///
    /// Master 2026-10-09: "allow wasd movement with the inventory/crafting/skills/info ui open. pressing tab on
    /// any of the crafting/skills/info ui should close them, not switch to the inventory tab."
    ///
    /// ⚠ NOT TESTABLE THROUGH ScriptedInput, which is how every other movement test here drives the player.
    /// ScriptedInput is read BEFORE the movement gate and replaces it, so a test that set it and watched the
    /// player walk would pass identically on a build where the gate was never touched -- it would be measuring
    /// physics. The subject is the gate's PREDICATE: which open UI roots the player and which does not.
    ///
    /// ⭐ SO THE LAST CHECK IS THE ONE THAT MATTERS. "Movement is allowed while a menu is open" is satisfied by
    /// deleting the gate outright, and every other check below would still pass. A freed mouse with NO dashboard
    /// screen open -- the pause menu, the console -- has to STILL root you.
    ///
    /// ⚠⚠ WHAT THIS DOES NOT COVER, measured rather than assumed. Only the INVENTORY can be opened in an L1
    /// rig: Craft and Skills die on a Godot FATAL ("Index 0 out of bounds (size() = 0)") the moment they open
    /// -- the same pre-existing crash that has left craft.layout dead, verified against a stashed tree at HEAD
    /// -- and Information is the map, which a bare rig has no MapUI.Current for.
    ///
    /// That makes this test VACUOUS FOR THE TAB HALF of the ask, and the only reason that is written down here
    /// rather than quietly true is that it was mutation-checked: reverting ToggleInventoryMenu to its old
    /// inventory-only form leaves this test GREEN. Of course it does -- the inventory is the one screen the old
    /// code already closed correctly; the bug was that any OTHER screen fell through to ShowMenu(Inventory).
    /// Reverting the MOVEMENT gate does turn it red, so that half is real coverage.
    ///
    /// So: the movement fix is tested. The Tab fix is NOT, and it needs the other three screens, which needs
    /// that FATAL fixed first.</summary>
    public sealed class DashboardMovementTests : GameTest
    {
        public override string Name => "ui.walk_with_dashboard_open";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.ResetForTests();
            Rigs.Ground(World);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(4);
            // ⚠ WORKAROUND for a PRE-EXISTING L1 rig crash, not part of the subject. Opening a dashboard screen
            // in a bare rig can die on a Godot FATAL ("Index 0 out of bounds (size() = 0)") -- the same crash
            // that has left craft.layout dead, verified against a stashed tree at HEAD. Doing the nearby scan
            // once up front, which InventoryUI.Open() does for itself anyway, gets the screens open.
            player.ScanNearbyItems();
            yield return Ticks(2);

            // ---- CONTROL A: nothing open, mouse captured -> not blocked. Without this, every "not blocked"
            // below could be reading a predicate that is simply always false.
            Input.MouseMode = Input.MouseModeEnum.Captured;
            yield return Ticks(2);
            T.Check("control: with nothing open the player can move", !player.DebugMoveInputBlocked);

            foreach (var (tab, label) in new[]
                     {
                         (MenuNavbar.Tab.Inventory, "inventory"),
                     })
            {
                player.ShowMenu(tab);
                yield return Ticks(3);
                T.Check($"{label}: the screen is up and it freed the mouse",
                        player.DebugAnyDashboardScreenOpen && Input.MouseMode != Input.MouseModeEnum.Captured);
                T.Check($"{label}: ...and the player can still WALK with it open", !player.DebugMoveInputBlocked);

                // THE BUG: ToggleInventoryMenu only ever asked `_invUI.IsOpen`, so with any OTHER screen up that
                // was false and it fell through to ShowMenu(Inventory) -- Tab navigated one tab sideways instead
                // of leaving, and it took a second press to get out.
                GD.Print($"[ui-walk] {label}: open={player.DebugAnyDashboardScreenOpen} mouse={Input.MouseMode} moveBlocked={player.DebugMoveInputBlocked}");
                player.DebugTabKey();
                yield return Ticks(3);
                T.Check($"{label}: Tab CLOSED the dashboard rather than switching tabs "
                      + $"(any open = {player.DebugAnyDashboardScreenOpen}, inventory = {player.DashboardOpen})",
                        !player.DebugAnyDashboardScreenOpen && !player.DashboardOpen);
                T.Check($"{label}: ...and the mouse went back to the world",
                        Input.MouseMode == Input.MouseModeEnum.Captured);
            }

            // ---- Tab FROM CLOSED must still OPEN the inventory: the fix must not make Tab close-only.
            player.DebugTabKey();
            yield return Ticks(3);
            T.Check("from closed, Tab still opens the INVENTORY", player.DashboardOpen);
            player.DebugTabKey();
            yield return Ticks(3);
            T.Check("...and Tab again closes it", !player.DebugAnyDashboardScreenOpen);

            // ---- ⭐⭐ CONTROL B: a freed mouse with NOTHING open still roots you. This is the check that fails
            // on a build where the gate was removed rather than narrowed.
            Input.MouseMode = Input.MouseModeEnum.Visible;
            yield return Ticks(2);
            T.Check("control: a freed mouse with NO dashboard screen open still blocks movement "
                  + $"(any open = {player.DebugAnyDashboardScreenOpen})",
                    !player.DebugAnyDashboardScreenOpen && player.DebugMoveInputBlocked);

            Input.MouseMode = Input.MouseModeEnum.Captured;
            yield return Ticks(2);
        }
    }
}
