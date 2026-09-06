using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // CHROMATIC ABERRATION (master 2026-09-06 "research implementing chromatic aberration to the edges of the
    // screen"). Retail ships it; GraphicsOptions had it on the held-back list for want of a Godot-side hook.
    //
    // The checks that matter here are STRUCTURAL rather than pictorial -- what the pass sits above and below,
    // and that "off" costs nothing -- because those are the two things a screenshot cannot tell you and the two
    // things that make it wrong in a way nobody notices for weeks.
    public sealed class ChromaticTests : GameTest
    {
        public override string Name => "gfx.chromatic";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            bool savedOn = GraphicsOptions.ChromaticAberration;
            float savedAmt = GraphicsOptions.ChromaticAmount;
            try
            {
                var ca = new ChromaticAberration();
                World.AddChild(ca);
                yield return Ticks(2);

                T.Check("the shader loads at all (content/chromatic.gdshader)", ca.DebugSamples > 0);

                // LAYER ORDER IS THE DESIGN. A hint_screen_texture pass distorts what is drawn BELOW its layer,
                // so the number decides what is treated as being behind a lens. Above nightvision (6) because
                // the goggles are part of the optical path; below the rain overlay (9), HUD (10) and menus (11)
                // because smearing interface text reads as a rendering fault, not as a lens.
                T.Check($"it sits above the world, viewmodel and nightvision (layer {ca.Layer} > 6)", ca.Layer > 6);
                T.Check($"...and below the rain overlay, HUD and menus (layer {ca.Layer} < 9)", ca.Layer < 9);

                // OFF MUST COST NOTHING. A zero intensity still samples the screen once per tap per pixel and
                // hands back what it started with -- the entire cost of the effect for none of the effect. Off
                // has to mean the rect is not drawn.
                GraphicsOptions.ChromaticAberration = false;
                GraphicsOptions.ApplyChromatic();
                yield return Ticks(1);
                T.Check("switched OFF the pass is not drawn at all, rather than drawn at zero strength", !ca.DebugVisible);

                GraphicsOptions.ChromaticAberration = true;
                GraphicsOptions.ChromaticAmount = 0.6f;
                GraphicsOptions.ApplyChromatic();
                yield return Ticks(1);
                T.Check($"switched ON it draws ({ca.DebugVisible}) at the configured strength ({ca.DebugIntensity:0.###})",
                    ca.DebugVisible && Mathf.IsEqualApprox(ca.DebugIntensity, 0.6f));

                // The settings row must reach the LIVE pass, not just the static. This is the wire that breaks
                // silently: the menu shows the new value, the screen keeps the old one.
                GraphicsOptions.ChromaticAmount = 0.2f;
                GraphicsOptions.ApplyChromatic();
                yield return Ticks(1);
                T.Check($"changing the strength row moves the live shader ({ca.DebugIntensity:0.###})",
                    Mathf.IsEqualApprox(ca.DebugIntensity, 0.2f));

                // ...and a strength of zero is off, not an invisible full-cost pass.
                GraphicsOptions.ChromaticAmount = 0f;
                GraphicsOptions.ApplyChromatic();
                yield return Ticks(1);
                T.Check("zero strength also stops drawing, so the row cannot leave a free-running no-op pass", !ca.DebugVisible);

                ca.QueueFree();
                yield return Ticks(2);
                T.Check("a torn-down pass clears Current, so the settings panel cannot poke a freed node",
                    ChromaticAberration.Current == null);
            }
            finally
            {
                GraphicsOptions.ChromaticAberration = savedOn;
                GraphicsOptions.ChromaticAmount = savedAmt;
                GraphicsOptions.ApplyChromatic();
            }
            yield break;
        }
    }
}
