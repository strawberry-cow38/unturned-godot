using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // CRT POWER-OFF COLLAPSE + the fade's dark end (master: "with the crt, when turning it off, do the beam collapse
    // on the center, the classic crt turn off. instead of fading from 0,0,0 fade from the color of the screen on the
    // crt model itself").
    //
    // The classic effect is a SEQUENCE, and every wrong version of it is still an animation that plays and still looks
    // deliberate, which is why the ordering is asserted rather than eyeballed:
    //   - both axes shrinking together = a rectangle zooming to a point. Reads as a UI transition, not a television.
    //   - horizontal before vertical = a vertical bar. Wrong tube entirely.
    //   - level falling monotonically = a plain fade-out with an extra step. The FLASH is the effect: the raster keeps
    //     the same energy while painting a fraction of the area, so it gets brighter on the way to the line.
    // A screenshot of the midpoint would show a bright horizontal line in all four cases.
    //
    // Television_0/1 ship with Unturned and this box has no install, so the state machine that DRIVES this cannot run
    // here -- a bare TVDevice has no screen mesh. Hence the curves and the who-collapses policy are pure statics; the
    // alternative is a feature whose entire behaviour is unreachable by anything but a human with the game open.
    public sealed class TVCollapseTests : GameTest
    {
        public override string Name => "tv.crt_collapse";

        public override IEnumerable<Step> Run()
        {
            float dur = TVDevice.CollapseDur;

            var start = TVDevice.Collapse(0f);
            T.Check($"starts as the full picture ({start.Vert:0.00} x {start.Horiz:0.00} @ {start.Level:0.00})",
                Mathf.IsEqualApprox(start.Vert, 1f) && Mathf.IsEqualApprox(start.Horiz, 1f) && start.Level >= 1f);

            // ---- THE ORDERING. Sample the moment the vertical squeeze is essentially done and check the picture is
            // still FULL WIDTH. This is the check that separates "classic CRT" from "shrinking rectangle".
            float vertDone = dur * 0.42f;
            var line = TVDevice.Collapse(vertDone);
            T.Check($"vertical goes first -- at {vertDone:0.000}s the picture is a thin line ({line.Vert:0.000} tall)", line.Vert < 0.2f);
            T.Check($"...and still FULL WIDTH ({line.Horiz:0.00})", Mathf.IsEqualApprox(line.Horiz, 1f));

            // ...then the line pulls in horizontally while staying a line.
            var dot = TVDevice.Collapse(dur * 0.85f);
            T.Check($"then the width goes ({dot.Horiz:0.00} wide, {dot.Vert:0.000} tall)", dot.Horiz < line.Horiz && dot.Vert <= line.Vert + 1e-4f);

            // ---- THE FLASH. Level must RISE before it falls. If this ever reads monotonic the effect has quietly
            // become a fade with extra steps, which still animates and still looks intentional.
            float peak = 0f, peakAt = 0f;
            for (int i = 0; i <= 200; i++)
            {
                float t = dur * i / 200f;
                float lv = TVDevice.Collapse(t).Level;
                if (lv > peak) { peak = lv; peakAt = t; }
            }
            T.Check($"the beam FLASHES on the way to the line (peak {peak:0.00} at {peakAt:0.000}s, started at {start.Level:0.00})",
                peak > start.Level * 1.5f);
            T.Check($"...and the peak is at the LINE, not at the start ({peakAt:0.000}s of {dur:0.000}s)",
                peakAt > dur * 0.2f && peakAt < dur * 0.7f);

            // ---- MONOTONIC SHRINK. Neither axis may grow back at any point: a curve that overshoots and recovers
            // reads as a bounce, and with 200 samples an eyeball at 60fps would never see the frame it happened on.
            bool shrinks = true;
            float pv = 2f, ph = 2f;
            for (int i = 0; i <= 200; i++)
            {
                var s = TVDevice.Collapse(dur * i / 200f);
                if (s.Vert > pv + 1e-4f || s.Horiz > ph + 1e-4f) shrinks = false;
                pv = s.Vert; ph = s.Horiz;
                if (s.Level < -1e-4f) shrinks = false;
            }
            T.Check("both axes only ever shrink, and the level never goes negative", shrinks);

            // ---- IT ENDS. A curve that leaves a sliver behind hangs a one-pixel bright line on the front of every
            // switched-off television in the map, forever, and nothing else would ever report it.
            var over = TVDevice.Collapse(dur + 0.001f);
            T.Check($"finishes at exactly nothing ({over.Vert:0.000} x {over.Horiz:0.000} @ {over.Level:0.000})",
                over.Vert == 0f && over.Horiz == 0f && over.Level == 0f);
            var wayOver = TVDevice.Collapse(dur * 10f);
            T.Check("...and stays there", wayOver.Level == 0f && wayOver.Horiz == 0f);

            // ---- WHO GETS IT. An LCD does not have a raster to lose, and a set whose glass is already gone does not
            // get to play a power-off animation on its way out.
            T.Check("a CRT television collapses", TVDevice.ShouldCollapse(TVDevice.DeviceKind.CrtTv, broken: false, screenShot: false));
            // The computer CRT gets it too (master: "dupe the CRT thing onto the computer crt"). Asserted separately
            // from the television because the two are different enum members and it would be entirely possible to add
            // the monitor and leave this policy behind -- with no symptom except a monitor that snaps off.
            T.Check("...and so does the computer CRT", TVDevice.ShouldCollapse(TVDevice.DeviceKind.CrtMonitor, broken: false, screenShot: false));
            T.Check("a flatscreen TV does NOT -- it is an LCD", !TVDevice.ShouldCollapse(TVDevice.DeviceKind.FlatTv, broken: false, screenShot: false));
            T.Check("...nor does the flatscreen monitor", !TVDevice.ShouldCollapse(TVDevice.DeviceKind.FlatMonitor, broken: false, screenShot: false));
            T.Check("a smashed prop does NOT", !TVDevice.ShouldCollapse(TVDevice.DeviceKind.CrtTv, broken: true, screenShot: false));
            T.Check("a shot-out screen does NOT", !TVDevice.ShouldCollapse(TVDevice.DeviceKind.CrtTv, broken: false, screenShot: true));

            // ---- THE WARMUP IS A CROSSFADE, NOT A DIMMER (master: "should fade from the tv model screen color into
            // the image"). This was got wrong once in exactly the way that looks plausible: raise the brightness floor
            // to the glass level and lerp up from there. Albedo MULTIPLIES the texture, so that produces a dim SMPTE
            // pattern, fully drawn, present from the first frame -- never a flat colour. Master's read was "the power
            // on fade in got nuked", and he was right; there was no fade left, just a picture that started dim.
            //
            // So the thing to pin is that the warmup rides ALPHA and the brightness stays put. A regression back to
            // the brightness ramp shows up here as a level that moves and an alpha that does not.
            var cold = TVDevice.ScreenColor(1f, 0f);
            var mid = TVDevice.ScreenColor(1f, 0.5f);
            var hot = TVDevice.ScreenColor(1f, 1f);
            T.Check($"a cold tube is fully DISSOLVED, showing the model's own screen face ({cold.A:0.00} alpha)", cold.A == 0f);
            T.Check($"...at full brightness, not a dim picture ({cold.R:0.00})", cold.R == 1f);
            T.Check($"a warm tube is the full picture ({hot.A:0.00} alpha)", hot.A == 1f);
            T.Check($"...and it crossfades in between ({mid.A:0.00})", mid.A > 0f && mid.A < 1f);
            // Brightness must be independent of the fade -- that separation IS the fix. If these two ever move
            // together again the crossfade has quietly become a dimmer wearing an alpha channel.
            T.Check("brightness and fade are independent axes",
                TVDevice.ScreenColor(0.3f, 1f).R < TVDevice.ScreenColor(0.9f, 1f).R
                && Mathf.IsEqualApprox(TVDevice.ScreenColor(0.3f, 1f).A, TVDevice.ScreenColor(0.9f, 1f).A));
            T.Check("...and alpha is clamped, so a stray warm value cannot punch past opaque",
                TVDevice.ScreenColor(1f, 4f).A == 1f && TVDevice.ScreenColor(1f, -2f).A == 0f);

            yield break;
        }
    }
}
