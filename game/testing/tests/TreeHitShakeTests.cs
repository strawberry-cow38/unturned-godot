using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // LEAVES SHAKE WHEN YOU CHOP (strawberry 2026-09-09: "change the tree leaves to react when you hit it with an
    // axe. have them shake with each hit").
    //
    // ⚠ THIS TEST USED TO READ THE MULTIMESH BACK, AND COULD NOT. A headless Godot boot keeps MultiMesh instance
    // data in a stub RenderingServer, so the payload is written into nothing: measured here, writing 0.77 to a
    // valid slot and reading it back IN THE SAME CALL returns 0, with UseCustomData true and the instance count
    // correct. The object looks configured; only the data is gone.
    //
    // That made every assertion below return the same answer whether the feature worked or was deleted -- and
    // worse, the two checks that PASSED ("they start unshaken") passed for exactly the reason the others failed,
    // which is what a dead instrument looks like from the inside. It had been red on correct code since it was
    // written.
    //
    // So the reads go through MANAGED state instead. That cannot prove a pixel moved, and it is honestly weaker
    // than what was intended here. What it still proves is the thing that actually breaks batched code: WHICH
    // instance got armed, that its neighbour did not, and that the decay drives it back to zero. The visual half
    // belongs on the L2 tier, which has a real renderer.
    public sealed class TreeHitShakeTests : GameTest
    {
        public override string Name => "world.tree_hit_shake";

        public override IEnumerable<Step> Run()
        {
            var field = new ResourceField();
            World.AddChild(field);
            field.LoadResources("NONE");
            yield return Ticks(2);

            // Find two tree instances that carry a leaf part, so the second can serve as the untouched control.
            int a = -1, b = -1;
            for (int i = 0; i < 4096 && b < 0; i++)
            {
                if (field.CanopyShakeStateForTest(i) < 0f) continue;
                if (a < 0) a = i; else b = i;
            }
            T.Check($"found two canopy instances to work with (a={a}, b={b})", a >= 0 && b >= 0);
            if (a < 0 || b < 0) yield break;

            // A TRIPWIRE, not a requirement. It asserts that the MultiMesh is still unreadable here, which is the
            // sole reason this test settles for managed state. If a future Godot or a non-headless harness makes
            // this pass the data back, THIS check fails -- and that failure means "go back to reading the
            // MultiMesh", not "something broke". Without it the weaker assertions below would quietly outlive
            // the limitation that justified them.
            field.HitShake(a);
            float viaBuffer = field.CanopyShakeForTest(a);
            T.Check($"the MultiMesh is still write-only here, so managed state is the honest read (buffer says {viaBuffer:0.###}) "
                  + "-- IF THIS FAILS, the buffer became readable: restore the MultiMesh assertions",
                    viaBuffer < 0.001f);

            float hitA = field.CanopyShakeStateForTest(a), hitB = field.CanopyShakeStateForTest(b);
            T.Check($"a hit arms the struck tree's own channel ({hitA:0.###})", hitA > 0.9f);
            // ⭐ THE ONE THAT MATTERS. A shared uniform on the canopy material would have set this too, and every
            // other tree of the species with it. If this ever goes non-zero, the effect has gone global.
            T.Check($"...and NOT its neighbour's -- the shake is per tree ({hitB:0.###})", hitB < 0.001f);

            // It decays, and it decays to actual zero rather than lingering at a floor.
            yield return Ticks(20);
            float mid = field.CanopyShakeStateForTest(a);
            T.Check($"it decays after the hit ({mid:0.###} < {hitA:0.###})", mid < hitA);
            yield return Ticks(90);
            T.Check($"and reaches zero rather than sticking ({field.CanopyShakeStateForTest(a):0.###})",
                field.CanopyShakeStateForTest(a) < 0.001f);

            // Each hit re-arms it: the ask was "shake with each hit", not "shake once per tree".
            field.HitShake(a);
            T.Check($"a later hit re-arms it ({field.CanopyShakeStateForTest(a):0.###})",
                field.CanopyShakeStateForTest(a) > 0.9f);
            yield break;
        }
    }
}
