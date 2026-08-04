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
            T.Check("a CRT collapses", TVDevice.ShouldCollapse(isCrt: true, broken: false, screenShot: false));
            T.Check("a flatscreen does NOT -- it is an LCD", !TVDevice.ShouldCollapse(isCrt: false, broken: false, screenShot: false));
            T.Check("a smashed prop does NOT", !TVDevice.ShouldCollapse(isCrt: true, broken: true, screenShot: false));
            T.Check("a shot-out screen does NOT", !TVDevice.ShouldCollapse(isCrt: true, broken: false, screenShot: true));

            // ---- THE DARK END (master: "instead of fading from 0,0,0 fade from the color of the screen on the crt
            // model itself"). The screen sub-mesh is an OVERLAY on the cabinet's own screen face, so a warmup starting
            // at 0 draws a rectangle darker than the surrounding plastic for the first moment of every power-on. The
            // failure is a dip, not a missing feature, which is why it reads as "fine" in motion.
            const float glass = 53f / 255f;   // Television_1's screen texel, rgb 53,53,53
            float cold = TVDevice.WarmLevel(0f, glass, 1f);
            float hot = TVDevice.WarmLevel(1f, glass, 1f);
            T.Check($"a cold tube sits at the GLASS colour, not at black ({cold:0.000})", Mathf.IsEqualApprox(cold, glass));
            T.Check($"...which is a real brightness ({cold:0.000} > 0)", cold > 0.01f);
            T.Check($"a warm tube is the full picture ({hot:0.000})", Mathf.IsEqualApprox(hot, 1f));
            bool rises = true;
            float prev = -1f;
            for (int i = 0; i <= 32; i++) { float v = TVDevice.WarmLevel(i / 32f, glass, 1f); if (v < prev - 1e-5f) rises = false; prev = v; }
            T.Check("...and it only ever climbs in between", rises);
            // The flatscreen path: k is pinned at 1, so the LCD is untouched by any of this and cannot inherit a floor.
            T.Check("an LCD (warm always 1) is unaffected by the glass floor",
                Mathf.IsEqualApprox(TVDevice.WarmLevel(1f, 0.9f, 1f), 1f));

            yield break;
        }
    }
}
