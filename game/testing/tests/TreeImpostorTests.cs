using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE TREE -> BILLBOARD HANDOVER.
    //
    // Real trees stop 25% early and camera-facing quads carry the far field. The bug that shipped in the first
    // version is the one worth a permanent test: the real mesh ended and the billboard began at the SAME
    // distance. That reads as obviously correct and flickers on sight -- strawberry found it within minutes,
    // "trees flicker in and out ... happens on the tree -> imposter line". Standing on a shared edge, sub-metre
    // camera jitter toggles both nodes on one frame and leaves a frame with NEITHER drawn; and the two
    // MultiMeshes measure from different AABBs anyway (the quads are centred half a tree-height up), so they
    // cross it at slightly different moments regardless.
    //
    // The fix is an overlap band: billboards switch ON before real trees switch OFF. This asserts that band
    // EXISTS, which is a pure arithmetic property and therefore the one thing a headless suite can still prove.
    // It cannot check what the billboards look like -- a SubViewport renders nothing headless, so the bake
    // returns empty here and no impostor nodes exist at all. That half is --imptest and an eye.
    public sealed class TreeImpostorHandoverTests : GameTest
    {
        public override string Name => "tree.impostor_handover";

        public override IEnumerable<Step> Run()
        {
            var field = new ResourceField();
            World.AddChild(field);
            field.LoadResources("NONE");
            yield return Step.Ticks(2);

            var ranges = field.DebugImpostorRangesForTest();
            T.Check($"tree species queued billboards ({ranges.Count})", ranges.Count > 0);

            // THE TEETH. Set ImpostorOverlap back to 1.0 -- the shipped bug -- and every one of these fails.
            int gapless = 0, ordered = 0;
            foreach (var (name, realEnd, impBegin, impEnd) in ranges)
            {
                if (impBegin < realEnd) gapless++;             // billboards are already up before the mesh goes
                if (realEnd < impEnd) ordered++;               // and the far field really does reach further
            }
            T.Check($"every species overlaps rather than meeting ({gapless}/{ranges.Count})", gapless == ranges.Count);
            T.Check($"the far field extends past the real one ({ordered}/{ranges.Count})", ordered == ranges.Count);
            T.Check($"the overlap fraction is a real band, not a rounding error ({ResourceField.ImpostorOverlap:0.###})",
                    ResourceField.ImpostorOverlap > 0.5f && ResourceField.ImpostorOverlap < 1f);

            // The band has to be wide enough to swallow a frame of movement. A sprinting player covers metres
            // per second, so a centimetre of overlap is the same bug with extra steps.
            float narrowest = float.MaxValue;
            foreach (var (_, realEnd, impBegin, _) in ranges) narrowest = Mathf.Min(narrowest, realEnd - impBegin);
            T.Check($"the narrowest band is metres, not centimetres ({narrowest:0.#} m)", narrowest >= 5f);

            // And the 25% cut actually happened -- 447m region cap -> ~335m.
            float maxEnd = 0f;
            foreach (var (_, realEnd, _, _) in ranges) maxEnd = Mathf.Max(maxEnd, realEnd);
            T.Check($"real trees cull short of retail's {LodTable.RegionMaxDistance:0}m cap ({maxEnd:0}m)",
                    maxEnd < LodTable.RegionMaxDistance && maxEnd > LodTable.RegionMaxDistance * 0.5f);

            foreach (var r in ranges) _ = r;
            field.QueueFree();
        }
    }
}
