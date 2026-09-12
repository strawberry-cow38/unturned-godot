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

            // ---- FORAGEABLE PLANTS DO SOUND, and this half of the test went stale the day after it was
            // written. It used to assert Bush_Amber and Mushroom_Red_0 were silent, on the reasoning that
            // "nothing in the port destroys them -- they carry no harvest body at all". e8481f55 gave them
            // one an hour later, so the clip now has a caller and silence would be the bug.
            var foliage = GameAudio.ResourceBreak("Bush_Amber");
            T.Check("a forageable bush has a break clip", foliage != null);
            T.Check("it is not the tree's clip", !ReferenceEquals(foliage, birch));
            T.Check("it is not the ore clip", !ReferenceEquals(foliage, ore));
            foreach (var picked in new[] { "Bush_Indigo", "Bush_Jade", "Bush_Mauve", "Bush_Russet",
                                           "Bush_Teal", "Bush_Vermillion", "Bush_Hanu",
                                           "Mushroom_Brown_0", "Mushroom_Red_0" })
                T.Check($"{picked} shares the one Foliage clip",
                        ReferenceEquals(GameAudio.ResourceBreak(picked), foliage));

            // ---- WHAT HAS NO BREAK PATH STILL STAYS SILENT, and Bush_0/Bush_1 are the point of this block.
            // Retail gives them no Explosion field and no Forage key, so they are scenery that neither breaks
            // nor picks -- ResourceField's forage roster omits them deliberately and GameAudio's own doc says
            // they return null. The CODE said otherwise between e8481f55 and this fix: it branched on
            // `StartsWith("Bush")`, which catches both, so the two plain green bushes rustled. Asserted by
            // name rather than by prefix, because a prefix is exactly what got this wrong.
            foreach (var quiet in new[] { "Bush_0", "Bush_1", "Snow_Pile_00", "Cane_00" })
                T.Check($"{quiet} is silent, not borrowing a clip it has no caller for",
                        GameAudio.ResourceBreak(quiet) == null);
            T.Check("a null name does not throw", GameAudio.ResourceBreak(null) == null);
        }
    }
}
