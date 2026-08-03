using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The lore blackout (strawberry): global power dies for GOOD on a scheduled day. This asserts the day-based kill
    // deterministically; the warning-brownout timing + the flicker are visual/temporal, tested in-game via the
    // date / dateset / whenBlackout / triggerGlobalBrownout console commands.
    public sealed class GlobalBlackoutTests : GameTest
    {
        public override string Name => "props.global_blackout";

        public override IEnumerable<Step> Run()
        {
            var cyc = new DayNightCycle { DayLength = 100000f, Speed = 0f, VisualsEnabled = false };
            World.AddChild(cyc);
            PowerNet.SetGlobalPower(true);

            cyc.BlackoutDay = cyc.Day + 3;   // override the random roll to a known near-future day
            yield return Ticks(2);
            T.Check("grid still live before the blackout day", PowerNet.GlobalPower);

            cyc.Day = cyc.BlackoutDay;
            // Until, NOT Ticks: DriveBlackout runs in _Process (a RENDER frame) while Ticks advances PHYSICS ticks,
            // and the two are not locked together. With Ticks(2) this passed only when the suite happened to be busy
            // enough to interleave render frames -- so it went green in a full run and red on its own, which is the
            // worst way for a test to fail: it makes the suite unable to localise anything.
            yield return Until(() => !PowerNet.GlobalPower);
            T.Check("reaching the blackout day kills global power", !PowerNet.GlobalPower);

            cyc.Day = cyc.BlackoutDay + 5;
            yield return Ticks(2);
            T.Check("...and it stays dark past the blackout day", !PowerNet.GlobalPower);

            PowerNet.SetGlobalPower(true);   // restore for the rest of the suite
        }
    }
}
