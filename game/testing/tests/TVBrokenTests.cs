using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // SMASHED TVs GO DARK (master: "when tvs get destroyed make sure to kill the screen").
    //
    // The screen sub-mesh and the spill OmniLight are TVDevice's OWN children -- they are not in the mesh array
    // handed to DestructibleField -- so breaking the prop hid the cabinet and left a lit screen glowing in the air
    // over its own rubble. Same trap the street lamp hit ("hiding the meshes left a lit cone hanging over the
    // rubble"), which is why WorldBuilder's onAliveChanged callback already existed to hang this on.
    //
    // The assertion that actually matters is the SECOND one: broken has to be STATE, not a one-shot switch-off.
    // Refresh() re-derives the effective lit state on every PowerNet sweep, and the day/night cycle calls it, so a
    // TV that was merely turned off would relight itself at the next grid change while still being rubble. A
    // one-shot implementation passes "it went dark" and fails only later, in the dark, at dusk.
    //
    // Television_0/1 ship with Unturned and this box has no install, so TVDevice.Make cannot run here -- a bare
    // TVDevice has no screen or light. That is fine for this suite: what is under test is the lit-state machine,
    // and DebugLit is the value the visuals are driven FROM.
    public sealed class TVBrokenTests : GameTest
    {
        public override string Name => "tv.broken_kills_screen";

        public override IEnumerable<Step> Run()
        {
            bool gridWas = PowerNet.GlobalPower;
            PowerNet.SetGlobalPower(true);

            var tv = new TVDevice { PropName = "Television_1" };
            World.AddChild(tv);
            yield return Ticks(2);

            tv.DebugForceOn();
            yield return Ticks(2);
            T.Check($"a powered TV is lit to start ({tv.DebugLit})", tv.DebugLit);

            tv.SetBroken(true);
            yield return Ticks(2);
            T.Check($"smashing it kills the screen ({tv.DebugLit}, broken={tv.DebugBroken})", !tv.DebugLit && tv.DebugBroken);

            // THE ONE THAT CATCHES A ONE-SHOT FIX. The grid sweep calls Refresh on every TV; a broken set must not
            // come back with it. Toggle the grid off and on to drive a real sweep rather than calling Refresh alone.
            PowerNet.SetGlobalPower(false);
            tv.Refresh();
            PowerNet.SetGlobalPower(true);
            tv.Refresh();
            yield return Ticks(2);
            T.Check($"a grid sweep does NOT resurrect it ({tv.DebugLit})", !tv.DebugLit);

            // ...and the player cannot switch a smashed set back on either.
            tv.Toggle();
            yield return Ticks(2);
            T.Check($"pressing F on rubble does nothing ({tv.DebugLit})", !tv.DebugLit);

            // Rubble reset restores the prop. It comes back OFF -- a rebuilt set is not mid-programme.
            tv.SetBroken(false);
            yield return Ticks(2);
            T.Check($"a rubble reset leaves it intact but off ({tv.DebugLit}, broken={tv.DebugBroken})",
                !tv.DebugLit && !tv.DebugBroken);

            // ...and it works again afterwards, which is what makes the previous check a reset rather than a corpse.
            tv.DebugForceOn();
            yield return Ticks(2);
            T.Check($"and it turns on again after the reset ({tv.DebugLit})", tv.DebugLit);

            PowerNet.SetGlobalPower(gridWas);
        }
    }
}
