using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Which clip a resource makes when it comes down (master 2026-09-10: "wire all the audio properly,
    /// how it is done in the source game").
    ///
    /// The first pass at this matched on SPECIES -- birch to birch_*, maple to maple_*, pine to pine_* -- on the
    /// reasoning that species is the axis that means something and the variant index is noise. Both halves of that
    /// were wrong, and the retail data says so plainly: `ResourceAsset.explosion` resolves to an EffectAsset whose
    /// AudioSource holds the clip, and across all 69 retail resources 23 of the 28 distinct effects carry the SAME
    /// `Timber` wav. There is no per-species crash. Worse, the species-named files that matching reached for
    /// (birch_2_wood, maple_4_wood, pine_2_wood) are OBJECT rubble effects carrying the generic Wood crate-break,
    /// so the feature shipped playing a smashing crate for every maple and pine it ever felled.
    ///
    /// That is what these checks are pointed at. A tree must land on the LONG clip: the timber crash runs 8.3s and
    /// the crate-break 1.4s, so a duration floor rejects exactly the value the bug produced rather than merely
    /// agreeing with the fix. And every tree must resolve to the same stream, which species matching cannot do by
    /// construction.</summary>
    public sealed class ResourceBreakAudioTests : GameTest
    {
        public override string Name => "audio.resource_break";
        public override double TimeoutSimSeconds => 20;

        static double Len(string file)
        {
            string p = ProjectSettings.GlobalizePath("res://content/audio/explosions/" + file);
            if (!System.IO.File.Exists(p)) return -1;
            var w = AudioStreamWav.LoadFromFile(p);
            return w != null ? w.GetLength() : -1;
        }

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(2);

            // ---- THE TWO CLIPS ARE DISTINGUISHABLE. If the crate-break were as long as the timber crash the
            // duration floor below would prove nothing, so establish the gap before leaning on it.
            double timber = Len("birch_0_timber.wav"), wood = Len("birch_2_wood.wav"), metal = Len("metal_2_metal.wav");
            T.Check($"timber clip on disk and long ({timber:0.00}s)", timber > 5.0);
            T.Check($"generic wood crate-break on disk and short ({wood:0.00}s)", wood > 0.1 && wood < 2.5);
            T.Check($"metal clip on disk ({metal:0.00}s)", metal > 0.5);

            // ---- EVERY TREE GETS THE TIMBER CRASH. The floor is above the crate-break's length, so the clip the
            // old species matching handed a maple or a pine fails this outright.
            foreach (var tree in new[] { "Birch_0", "Birch_1", "Maple_0", "Maple_1", "Pine_0", "Pine_1", "Pine_2", "Pine_3" })
            {
                var s = GameAudio.ResourceBreak(tree);
                double d = s != null ? s.GetLength() : -1;
                T.Check($"{tree} falls with the timber crash ({d:0.00}s)", s != null && d > 5.0);
            }

            // ---- ...AND IT IS ONE CLIP, NOT ONE PER SPECIES. Reference equality: species matching returns a
            // different stream per prefix and cannot pass this even when each prefix happens to be right.
            var birch = GameAudio.ResourceBreak("Birch_0");
            T.Check("maple gets the very same stream as birch", ReferenceEquals(GameAudio.ResourceBreak("Maple_1"), birch));
            T.Check("pine gets the very same stream as birch", ReferenceEquals(GameAudio.ResourceBreak("Pine_0"), birch));

            // ---- ORE AND CLAY SHARE THE METAL CLIP, WHICH IS NOT THE TREE ONE.
            var ore = GameAudio.ResourceBreak("Metal_0");
            T.Check($"a metal node breaks with the metal clip ({(ore != null ? ore.GetLength() : -1):0.00}s)",
                    ore != null && ore.GetLength() > 0.5 && ore.GetLength() < 5.0);
            T.Check("it is not the tree's clip", !ReferenceEquals(ore, birch));
            T.Check("Metal_2 matches Metal_0", ReferenceEquals(GameAudio.ResourceBreak("Metal_2"), ore));
            T.Check("clay nodes share it", ReferenceEquals(GameAudio.ResourceBreak("Clay_3"), ore));

            // ---- WHAT HAS NO BREAK PATH STAYS SILENT. Bushes and mushrooms do resolve to a Foliage clip in
            // retail, but nothing in the port destroys them -- they carry no harvest body at all. Returning a
            // clip here would be a wire to a caller that cannot fire, and returning a TREE clip (which the old
            // fallback chain did, ending at Pick("explosions","birch")) would be audibly wrong the day one is
            // hooked up. Retail agrees on Bush_0/Bush_1 specifically: their .dat has no Explosion field.
            foreach (var quiet in new[] { "Bush_0", "Bush_Amber", "Mushroom_Red_0", "Snow_Pile_00", "Cane_00" })
                T.Check($"{quiet} is silent, not borrowing a tree's crash", GameAudio.ResourceBreak(quiet) == null);
            T.Check("a null name does not throw", GameAudio.ResourceBreak(null) == null);
        }
    }
}
