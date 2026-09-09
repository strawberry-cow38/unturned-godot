using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // LEAVES SHAKE WHEN YOU CHOP (strawberry 2026-09-09: "change the tree leaves to react when you hit it with an
    // axe. have them shake with each hit").
    //
    // This is tested rather than photographed because --treetest CANNOT reach it: its standing tree is a plain
    // LoadTreeVisual node and its felled one is a bare TreeTrunk with Field = null, so neither is a ResourceField
    // instance and `Field?.HitShake` is a no-op in both. Rather than ship a second visual path on the strength of
    // reading it -- which is how the leaf reaction and the trunk reverb both got shipped broken today -- the
    // plumbing gets a check.
    //
    // What is actually at risk here is not the maths, it is the WIRING: custom data not enabled on the MultiMesh,
    // the wrong slot recorded, the write never reaching the buffer. So every read goes back through the MultiMesh
    // itself, and the key assertion is the one that would catch the tempting shortcut -- a shared uniform, which
    // would have shaken every tree of the species at once.
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
                if (field.CanopyShakeForTest(i) < 0f) continue;
                if (a < 0) a = i; else b = i;
            }
            T.Check($"found two canopy instances to work with (a={a}, b={b})", a >= 0 && b >= 0);
            if (a < 0 || b < 0) yield break;

            T.Check($"they start unshaken (a={field.CanopyShakeForTest(a):0.###}, b={field.CanopyShakeForTest(b):0.###})",
                field.CanopyShakeForTest(a) < 0.001f && field.CanopyShakeForTest(b) < 0.001f);

            field.HitShake(a);
            float hitA = field.CanopyShakeForTest(a), hitB = field.CanopyShakeForTest(b);
            GD.Print($"[hitshake] after one hit: struck={hitA:0.###} neighbour={hitB:0.###}");

            // The write reached the buffer at all.
            T.Check($"a hit arms the struck tree's own channel ({hitA:0.###})", hitA > 0.9f);
            // ⭐ THE ONE THAT MATTERS. A uniform on the shared canopy material would have set this too, and every
            // other tree of the species with it. If this ever goes non-zero, the effect has gone global.
            T.Check($"...and NOT its neighbour's -- the shake is per tree ({hitB:0.###})", hitB < 0.001f);

            // It decays, and it decays to actual zero rather than lingering at a floor.
            yield return Ticks(20);
            float mid = field.CanopyShakeForTest(a);
            T.Check($"it decays after the hit ({mid:0.###} < {hitA:0.###})", mid < hitA);
            yield return Ticks(90);
            T.Check($"and reaches zero rather than sticking ({field.CanopyShakeForTest(a):0.###})",
                field.CanopyShakeForTest(a) < 0.001f);

            // Each hit re-arms it: the ask was "shake with each hit", not "shake once per tree".
            field.HitShake(a);
            T.Check($"a later hit re-arms it ({field.CanopyShakeForTest(a):0.###})", field.CanopyShakeForTest(a) > 0.9f);
            yield break;
        }
    }
}
