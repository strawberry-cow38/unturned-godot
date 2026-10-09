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
    /// ⚠ INFORMATION (the map) IS NOT IN THE LOOP: a bare L1 rig has no MapUI.Current to open, so it is
    /// genuinely absent rather than broken. Inventory, Craft and Skills are the three that exist here, and the
    /// bug lived in the branch they share, so they cover it.
    ///
    /// ⚠ HEADLESS CANNOT CAPTURE THE MOUSE, so `UiInputBlocked` (MouseMode != Captured) is permanently true
    /// there and the first control below cannot hold -- docs/CLIENT_SCRIPTING_HARNESS_PLAN.md already says so
    /// and I walked into it anyway. The control is therefore SKIPPED when the capture does not stick, rather
    /// than failing: test.sh runs L1 headless, and a test that reds the nightly to make a point is worse than
    /// one that says what it could not check. Everything else runs in both, because the interesting assertion
    /// -- dashboard open, therefore NOT blocked -- needs UiInputBlocked to be true, which headless gives free.
    ///
    /// ⚠ If you run this WINDOWED on the box and it aborts somewhere arbitrary with a FATAL "Index 0 out of
    /// bounds (size() = 0)", that is the renderer, not this test: `driver/threads/thread_model=2` loses a race
    /// in windowed runs (5 crashes of 5; 0 of 5 under `--render-thread safe`, which leaves project.godot
    /// alone). Headless is unaffected, which is why the nightly never saw it.</summary>
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
            bool captures = Input.MouseMode == Input.MouseModeEnum.Captured;   // false headless -- see the note above
            if (captures) T.Check("control: with nothing open the player can move", !player.DebugMoveInputBlocked);
            else GD.Print("[ui-walk] headless: the mouse will not capture, so the 'nothing open -> can move' control is SKIPPED");

            foreach (var (tab, label) in new[]
                     {
                         (MenuNavbar.Tab.Inventory, "inventory"),
                         (MenuNavbar.Tab.Craft, "crafting"),
                         (MenuNavbar.Tab.Skills, "skills"),
                     })
            {
                player.ShowMenu(tab);
                yield return Ticks(3);
                T.Check($"{label}: the screen is up and it freed the mouse",
                        player.DebugAnyDashboardScreenOpen && Input.MouseMode != Input.MouseModeEnum.Captured);
                // (headless reads "freed" trivially, since it never captured -- the check above is only
                //  load-bearing windowed. The next one is the actual subject and is real in both.)
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
                if (captures)
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
            // ⭐ THIS ONE SURVIVES HEADLESS, and it is the control that matters: it is what fails on a build
            // where the gate was deleted rather than narrowed. The skipped control is the weaker direction.

            Input.MouseMode = Input.MouseModeEnum.Captured;
            yield return Ticks(2);
        }
    }
}
