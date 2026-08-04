using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // CRT VERTICAL HOLD SLIPPING (master: "add a small chance every x seconds to do a vertical de-sync scroll, for x
    // ticks, before correcting itself").
    //
    // Everything here fails QUIETLY, which is the whole reason it is pinned:
    //
    //  - a flat per-frame chance instead of a rate looks correct on the machine it was tuned on and fires twice as
    //    often at 120 fps. The only symptom is somebody saying the televisions seem worse on their PC.
    //  - an unwrapped offset works for hours and then loses float precision, so the roll goes choppy on a server that
    //    has been up for days and nothing points back here.
    //  - a slip that does not reset when the set goes dark comes back still rolling, which reads as the effect having
    //    LATCHED rather than fired -- and only after someone happens to power-cycle a TV mid-roll.
    //
    // None of those are visible in a screenshot and two of them are not visible in a short session either.
    public sealed class TVDesyncTests : GameTest
    {
        public override string Name => "tv.crt_desync";

        public override IEnumerable<Step> Run()
        {
            // ---- RATE, not a per-frame coin flip. Expected slips per second must be the same at any frame time.
            float at60 = TVDevice.DesyncChance(1f / 60f, 45f) * 60f;
            float at30 = TVDevice.DesyncChance(1f / 30f, 45f) * 30f;
            float at144 = TVDevice.DesyncChance(1f / 144f, 45f) * 144f;
            T.Check($"slip rate is framerate-independent (60fps {at60:0.0000}/s, 30fps {at30:0.0000}/s, 144fps {at144:0.0000}/s)",
                Mathf.IsEqualApprox(at60, at30, 1e-5f) && Mathf.IsEqualApprox(at60, at144, 1e-5f));
            T.Check($"...and works out to one slip per ~{1f / at60:0} seconds", Mathf.IsEqualApprox(1f / at60, 45f, 0.5f));
            T.Check("a zero/absurd mean gap does not divide by zero", TVDevice.DesyncChance(0.016f, 0f) == 0f);
            T.Check("a huge frame time cannot exceed certainty", TVDevice.DesyncChance(600f, 45f) <= 1f);

            // ---- WHO SLIPS. Vertical hold is a tube problem; an LCD has no deflection to lose. And a set already
            // mid-slip must not restart, or a long enough session eventually stacks rolls on top of each other.
            T.Check("a lit CRT can slip", TVDevice.DesyncCanFire(isCrt: true, lit: true, running: 0f));
            T.Check("a flatscreen never does", !TVDevice.DesyncCanFire(isCrt: false, lit: true, running: 0f));
            T.Check("a dark CRT never does", !TVDevice.DesyncCanFire(isCrt: true, lit: false, running: 0f));
            T.Check("...and one already rolling does not restart", !TVDevice.DesyncCanFire(isCrt: true, lit: true, running: 0.5f));

            // ---- THE ROLL. Offset must MOVE, stay inside [0,1) in both directions, and snap home when the clock runs
            // out. Run it for a long simulated slip so a slow leak out of range would show.
            float left = 3f, off = 0f;
            bool inRange = true, moved = false;
            for (int i = 0; i < 400 && left > 0f; i++)
            {
                (left, off) = TVDevice.DesyncStep(left, off, 1.7f, 1f / 60f);
                if (off < 0f || off >= 1f) inRange = false;
                if (off > 0.01f) moved = true;
            }
            T.Check("the picture actually rolls", moved);
            T.Check("...and the offset stays wrapped into [0,1) rolling one way", inRange);

            left = 3f; off = 0f; inRange = true;
            for (int i = 0; i < 400 && left > 0f; i++)
            {
                (left, off) = TVDevice.DesyncStep(left, off, -2.2f, 1f / 60f);
                if (off < 0f || off >= 1f) inRange = false;
            }
            // The one a naive `offset % 1f` gets wrong: C# `%` keeps the sign, so a downward roll goes NEGATIVE and
            // the UV samples off the far side. Mathf.PosMod is doing real work here, not decoration.
            T.Check("...and rolling the OTHER way too (negative speed does not go negative)", inRange);

            // ---- IT CATCHES. Master: "before correcting itself". A hold locking is a snap, so the last step has to
            // land on exactly 0 -- not near it. A residual offset leaves every CRT in the map permanently a few
            // percent off frame, which reads as the pattern being misaligned rather than as a bug.
            var caught = TVDevice.DesyncStep(0.004f, 0.63f, 1.7f, 1f / 60f);
            T.Check($"the hold catches and the picture SNAPS back to frame ({caught.Offset:0.000}, {caught.Left:0.000}s left)",
                caught.Offset == 0f && caught.Left == 0f);
            var overshoot = TVDevice.DesyncStep(0.5f, 0.4f, 1.7f, 9f);   // a huge stall frame must not leave it rolling
            T.Check($"...even across a stall frame that blows past the end ({overshoot.Offset:0.000})",
                overshoot.Offset == 0f && overshoot.Left == 0f);

            // ---- LIVE: a slip must not survive the set going dark. Bare TVDevice (no install, so no screen mesh),
            // which is fine -- the roll state is what drives the material, and it is the state that latches.
            bool gridWas = PowerNet.GlobalPower;
            PowerNet.SetGlobalPower(true);
            var tv = new TVDevice { PropName = "Television_1" };
            World.AddChild(tv);
            yield return Ticks(2);
            tv.DebugForceOn();
            yield return Ticks(2);

            tv.DebugForceDesync(5f, 1.7f);
            yield return Ticks(4);
            T.Check($"a forced slip is rolling ({tv.DebugDesyncOffset:0.000})", tv.DebugDesyncRolling);

            tv.Toggle();                     // switch it off mid-roll
            yield return Ticks(2);
            T.Check($"switching off drops the slip ({tv.DebugDesyncRolling}, offset {tv.DebugDesyncOffset:0.000})",
                !tv.DebugDesyncRolling && tv.DebugDesyncOffset == 0f);

            tv.DebugForceOn();
            yield return Ticks(4);
            T.Check($"...and it comes back on IN FRAME, not still rolling ({tv.DebugDesyncOffset:0.000})",
                tv.DebugDesyncOffset == 0f);

            PowerNet.SetGlobalPower(gridWas);
        }
    }
}
