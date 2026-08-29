using Godot;
using System.Collections.Generic;
using UnturnedGodot.Testing;

namespace UnturnedGodot.Testing.Tests
{
    /// <summary>ResourceCaches.ClearAll actually empties the caches.
    ///
    /// The bug this guards: leaving the map editor is ReloadCurrentScene, which frees the node tree but NOT
    /// static fields, so the next map was served textures and meshes built during the editor session
    /// (strawberry_cow: "every texture looks corrupted -- purple, black, white"; relaunching fixed it, which is
    /// what identified process-lifetime state as the culprit).
    ///
    /// WHAT THIS CAN AND CANNOT PROVE. It proves the caches are populated before and empty after, which is the
    /// mechanical claim. It does NOT prove the corruption is gone -- that is a GPU-visible symptom and this
    /// suite is headless, so the instrument cannot see it. Anyone reading a green run here should read it as
    /// "the reset does what it says", not "the purple textures are fixed".</summary>
    public class ResourceCacheClearAll : GameTest
    {
        public override string Name => "caches.clear_all";
        public override int Tier => 0;

        public override IEnumerable<Step> Run()
        {
            // Populate something real. EditorIcons draws to an Image with no file dependency, so it works in a
            // bare rig; ObjMesh needs a real .obj on disk, so it is used only if one is actually there.
            for (int i = 0; i < 3; i++) EditorIcons.Get((EditorIcons.Glyph)i, 32);

            int before = ResourceCaches.TotalCached;
            // THE LOAD-BEARING ASSERTION. Without it the whole test passes against an empty cache and an empty
            // ClearAll body -- "0 == 0" is the shape of a check that cannot fail.
            T.Check($"the caches are actually populated before the clear (count {before})", before > 0);

            ResourceCaches.ClearAll();

            int after = ResourceCaches.TotalCached;
            T.Check($"ClearAll empties every keyed cache (count {after})", after == 0);

            // And it must stay usable afterwards: a cache that clears but cannot refill is a worse bug than the
            // one being fixed. This is the `_tried`-flag trap in assertable form.
            var again = EditorIcons.Get((EditorIcons.Glyph)0, 32);
            T.Check("a cache still serves after being cleared (it refills, not just empties)",
                    again != null && ResourceCaches.TotalCached > 0);

            ResourceCaches.ClearAll();

            // A CACHE ADDED LATER IS THE ONE THAT GETS MISSED. GlassShards holds two retail meshes in statics
            // and was NOT registered when it was written -- same day, and nothing in the build or the suite
            // objects to a new static cache going unlisted. TotalCached counts only some of what ClearAll
            // clears, so an unregistered cache is invisible here twice over: not cleared, and not counted.
            //
            // Driving it explicitly is the only way this file can speak for it.
            var shards = GlassShards.Build(new Vector2(1.2f, 1.5f), Colors.White, 1234);
            T.Check("shards built (needs the retail Glass_0/Glass_1 on disk)", shards != null);
            if (shards != null)
            {
                shards.QueueFree();
                int cached = ResourceCaches.TotalCached;
                T.Check($"building shards populated a cache ({cached})", cached > 0);
                ResourceCaches.ClearAll();
                T.Check($"...and ClearAll drops it ({ResourceCaches.TotalCached})",
                        ResourceCaches.TotalCached == 0);
            }

            ResourceCaches.ClearAll();   // leave the shared boot as we found it
            yield break;
        }
    }
}
