using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // EXACTLY ONE TOOL IS LIVE. This invariant has now been broken twice in this file, both times the same
    // way: the rule was written out by hand in more than one place and the copies drifted.
    //
    //   1st: five hand-written "clear the others", each clearing a DIFFERENT subset, so an opening preset
    //        could stay armed alongside the room tool. Fixed by centralising in the panel's SetTool.
    //   2nd: the KEYBOARD never went through it -- pressing 1-6 set _armed directly, leaving the room tool
    //        armed too, with the room button still lit. The fix had landed on the panel path only.
    //
    // So this asserts the invariant against EVERY tool rather than the one that broke, because the failure
    // mode is a NEW entry point forgetting the rule, and clicking around the editor cannot find that.
    public class EditorBuildingsHasExactlyOneToolLive : GameTest
    {
        public override string Name => "buildtool.one_tool_at_a_time";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            // Every mode flag the editor branches on, read back after each selection. If someone adds a tool
            // and a flag but forgets to clear it in SelectTool, the count below goes to 2.
            int LiveCount() =>
                (eb.WallDrawMode ? 1 : 0) + (eb.RoomDrawMode ? 1 : 0) + (eb.SlabDrawMode ? 1 : 0) +
                (eb.DeleteDrawMode ? 1 : 0) + (eb.FoundationDrawMode ? 1 : 0) + (eb.StairsDrawMode ? 1 : 0) +
                (eb.ArmedArchetype >= 0 ? 1 : 0);

            var tools = new[]
            {
                EditorBuildings.BuildTool.Wall, EditorBuildings.BuildTool.Room,
                EditorBuildings.BuildTool.Floor, EditorBuildings.BuildTool.Roof,
                EditorBuildings.BuildTool.Foundation, EditorBuildings.BuildTool.Delete,
                EditorBuildings.BuildTool.Stairs,
            };

            foreach (var t in tools)
            {
                eb.SelectTool(t);
                T.Check($"{t}: exactly one mode live ({LiveCount()})", LiveCount() == 1);
                T.Check($"{t}: Tool reports itself", eb.Tool == t);
            }

            // The historical failure, explicitly: arm a tool, then arm an opening preset. The preset must
            // REPLACE it, not join it. BREAK IT: set _armed directly instead of going through SelectTool.
            eb.SelectTool(EditorBuildings.BuildTool.Room);
            eb.SelectTool(EditorBuildings.BuildTool.Opening, 2);
            T.Check($"an opening preset replaces the room tool ({LiveCount()} live)", LiveCount() == 1);
            T.Check("...and it is the preset that is armed", eb.ArmedArchetype == 2 && !eb.RoomDrawMode);

            // And the reverse: a tool must disarm the preset. This is the direction the ORIGINAL bug went.
            eb.SelectTool(EditorBuildings.BuildTool.Wall);
            T.Check($"a tool disarms the preset ({eb.ArmedArchetype})", eb.ArmedArchetype < 0 && LiveCount() == 1);

            // None clears everything -- pressing the live tool's key again drops to select.
            eb.SelectTool(EditorBuildings.BuildTool.None);
            T.Check($"None leaves nothing live ({LiveCount()})", LiveCount() == 0);

            // Floor and Roof share SlabDrawMode, so the KIND has to switch or picking roof after floor
            // silently keeps drawing floors -- one flag serving two tools is the same drift risk.
            eb.SelectTool(EditorBuildings.BuildTool.Floor);
            T.Check("floor sets the slab kind", eb.SlabDrawKind == SurfaceKind.Floor);
            eb.SelectTool(EditorBuildings.BuildTool.Roof);
            T.Check("roof switches the slab kind", eb.SlabDrawKind == SurfaceKind.Roof);

            // AND THE KEYBOARD PATH ITSELF, not just the method it should call. This is the half that was
            // missing last time: SelectTool was correct and the caller bypassed it, so a test of SelectTool
            // alone would have stayed green through the whole bug.
            eb.SelectTool(EditorBuildings.BuildTool.Room);
            bool took = eb.HandleToolKey(Key.Key3);
            T.Check("the preset key is handled", took);
            T.Check($"...and it went THROUGH SelectTool ({LiveCount()} live, armed {eb.ArmedArchetype})",
                    LiveCount() == 1 && eb.ArmedArchetype == 2 && !eb.RoomDrawMode);

            T.Check("B arms the wall tool", eb.HandleToolKey(Key.B) && eb.Tool == EditorBuildings.BuildTool.Wall);
            T.Check("T arms stairs", eb.HandleToolKey(Key.T) && eb.Tool == EditorBuildings.BuildTool.Stairs);
            T.Check("X arms delete", eb.HandleToolKey(Key.X) && eb.Tool == EditorBuildings.BuildTool.Delete);
            // Pressing the live tool's key again returns to select rather than re-arming it.
            T.Check("X again returns to select", eb.HandleToolKey(Key.X) && eb.Tool == EditorBuildings.BuildTool.None);
            // A key that is not a tool key must NOT be swallowed, or it stops reaching Q/E and Ctrl+Z.
            T.Check("a non-tool key is left alone", !eb.HandleToolKey(Key.Q) && !eb.HandleToolKey(Key.Z));

            eb.QueueFree();
        }
    }
}
