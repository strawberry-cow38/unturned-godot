using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the material-combo browser. The list itself is trivial; the two dedup rules are not, and they
    // pull against each other -- one says "collapse identical colours", the other says "never collapse the
    // role entry, which is identical by construction".
    [TestFixture]
    public class WallCombosTests
    {
        static WallPalette Pal(string name, int[] rgb, int wall = 0, int reveal = 2, int roof = -1)
            => new WallPalette { Name = name, Rgb = rgb, WallTexel = wall, RevealTexel = reveal,
                                 RoofTexel = roof, Thickness = 0.3f };

        static int[] Eight(params int[] v)
        {
            var a = new int[8];
            for (int i = 0; i < 8; i++) a[i] = i < v.Length ? v[i] : v[v.Length - 1];
            return a;
        }

        [Test]
        public void EightDistinctTexelsGiveEightSwatchesPlusTheRoleEntry()
        {
            var combos = WallCombos.All(new List<WallPalette>
                { Pal("A", Eight(1, 2, 3, 4, 5, 6, 7, 8)) });

            Assert.That(combos.Count, Is.EqualTo(9), "one role entry + eight distinct texels");
            Assert.That(combos[0].IsRole, Is.True, "the role entry comes first");
            Assert.That(combos.Skip(1).Select(c => c.Texel), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
        }

        [Test]
        public void RepeatedTexelsCollapse()
        {
            // A real 4x2 building texture repeats. Eight swatches where five do the same thing is not a
            // browser, it is padding.
            var combos = WallCombos.All(new List<WallPalette>
                { Pal("A", Eight(0x111111, 0x111111, 0x222222, 0x111111, 0x222222, 0x111111, 0x111111, 0x111111)) });

            var texels = combos.Where(c => !c.IsRole).Select(c => c.Texel).ToArray();
            Assert.That(texels, Is.EqualTo(new[] { 0, 2 }), "first occurrence of each colour wins");
            Assert.That(combos.Count, Is.EqualTo(3), "role entry + two distinct colours");
        }

        [Test]
        public void TheRoleEntrySurvivesEvenThoughItsColourIsADuplicate()
        {
            // THE RULE THAT FIGHTS THE OTHER ONE. The role entry's colour IS the wall texel's colour, always
            // -- so any dedup that keys on colour alone deletes it. Losing it means the browser has no way
            // to say "follow the palette's roles", and painting a roof with the wall swatch pins the wall
            // colour on a roof, which is the bug this codebase already fixed once.
            var combos = WallCombos.All(new List<WallPalette>
                { Pal("A", Eight(0xAABBCC, 0xAABBCC, 0xAABBCC, 0xAABBCC), wall: 0, reveal: 0) });

            Assert.That(combos.Count, Is.EqualTo(2), "the role entry plus the one distinct colour");
            Assert.That(combos[0].IsRole, Is.True);
            Assert.That(combos[1].Texel, Is.EqualTo(0));
            Assert.That(combos[0].Rgb, Is.EqualTo(combos[1].Rgb),
                        "and they really are the same colour -- that is the whole point");
        }

        [Test]
        public void DedupIsWithinAPaletteNotAcrossThem()
        {
            // The same grey in two palettes is two different buildings' grey. They carry different
            // thicknesses and different reveals, so collapsing them would make picking a swatch silently
            // change what the wall is made of.
            var combos = WallCombos.All(new List<WallPalette>
            {
                Pal("A", Eight(0x808080)),
                Pal("B", Eight(0x808080)),
            });

            Assert.That(combos.Count, Is.EqualTo(4), "two palettes x (role + one colour)");
            Assert.That(combos.Select(c => c.Material).Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void LabelsNameTheRoleRatherThanTheIndex()
        {
            // "Police_1 2" tells you nothing. Roles are measured per palette, so the index is not portable
            // between them and naming it is the only thing that carries meaning.
            var combos = WallCombos.All(new List<WallPalette>
                { Pal("Police_1", Eight(1, 2, 3, 4, 5, 6, 7, 8), wall: 1, reveal: 3, roof: 5) });

            string For(int t) => combos.First(c => c.Texel == t).Label;
            Assert.That(For(1), Is.EqualTo("Police_1 wall"));
            Assert.That(For(3), Is.EqualTo("Police_1 reveal"));
            Assert.That(For(5), Is.EqualTo("Police_1 roof"));
            Assert.That(For(0), Is.EqualTo("Police_1 #0"), "an unroled texel keeps its number");
            Assert.That(combos[0].Label, Is.EqualTo("Police_1"), "the role entry is just the palette name");
        }

        [Test]
        public void IndexOfMatchesThePairNotTheColour()
        {
            // The role entry and its wall texel are always the same RGB. Matching on colour would highlight
            // whichever came first, so the browser's selection would jump the moment you painted anything.
            var combos = WallCombos.All(new List<WallPalette>
                { Pal("A", Eight(0xAABBCC, 0x112233)) });

            int role = WallCombos.IndexOf(combos, 0, -1);
            int pinned = WallCombos.IndexOf(combos, 0, 0);
            Assert.That(role, Is.Not.EqualTo(pinned), "same colour, different entries");
            Assert.That(combos[role].IsRole, Is.True);
            Assert.That(combos[pinned].Texel, Is.EqualTo(0));
            Assert.That(WallCombos.IndexOf(combos, 0, 7), Is.EqualTo(-1), "a collapsed texel is not in the list");
            Assert.That(WallCombos.IndexOf(combos, 9, -1), Is.EqualTo(-1), "nor is a palette that does not exist");
        }

        [Test]
        public void AMalformedPaletteIsSkippedRatherThanCrashingTheBrowser()
        {
            var combos = WallCombos.All(new List<WallPalette>
            {
                Pal("short", new[] { 1, 2, 3 }),
                Pal("null", null),
                Pal("good", Eight(0x010203)),
            });

            Assert.That(combos.Select(c => c.Material).Distinct().ToArray(), Is.EqualTo(new[] { 2 }),
                        "only the well-formed palette contributes, and it keeps its own index");
            Assert.That(WallCombos.All(null), Is.Empty);
        }
    }
}
