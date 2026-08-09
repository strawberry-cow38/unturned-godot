using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the building palette table.
    //
    // This exists because the table is MEASUREMENT, and its first version was measured wrong in a way nothing
    // could see: the sampler read the 4x2 palette without the V-flip the engine applies to those same
    // textures, landed one row low, and every one of the 52 buildings came back a shade of brown. It parsed,
    // it loaded, it rendered. The tests below pin colours a human can check against the actual game -- a fire
    // station is red with white trim -- because that is the only kind of assertion a silent row shift fails.
    [TestFixture]
    public class WallPalettesTests
    {
        static string TsvPath()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string p = Path.Combine(d.FullName, "game", "content", "wall_palettes.tsv");
                if (File.Exists(p)) return p;
                d = d.Parent;
            }
            throw new FileNotFoundException("game/content/wall_palettes.tsv not found above " + AppContext.BaseDirectory);
        }

        static System.Collections.Generic.List<WallPalette> Load() =>
            WallPalettes.Parse(File.ReadAllLines(TsvPath()));

        static WallPalette By(string name) =>
            Load().FirstOrDefault(p => p.Name == name) is { Name: not null } m ? m
            : throw new AssertionException($"no palette named {name}");

        // BREAK IT: drop the header/comment skip, or the hex parse -> rows vanish or arrive as zeroes.
        [Test]
        public void EveryRowParsesWithEightTexelsAndInRangeRoles()
        {
            var all = Load();
            Assert.That(all.Count, Is.GreaterThanOrEqualTo(50), "the sampled buildings should all survive the parse");
            foreach (var p in all)
            {
                Assert.That(p.Name, Is.Not.Null.And.Not.Empty);
                Assert.That(p.Rgb.Length, Is.EqualTo(8), p.Name);
                Assert.That(p.WallTexel, Is.InRange(0, 7), p.Name);
                Assert.That(p.RevealTexel, Is.InRange(0, 7), p.Name);
                Assert.That(p.Thickness, Is.InRange(0.2f, 1.5f), $"{p.Name}: a wall thickness outside this is a mis-measure");
                Assert.That(p.Rgb.Distinct().Count(), Is.GreaterThan(1), $"{p.Name}: eight identical texels means the sampler read nothing");
            }
        }

        // The row-shift regression, pinned against buildings anyone can look at in game.
        // BREAK IT: shift every wall/reveal index by 4 (what reading the palette un-flipped did) -> the fire
        // station stops being red, the post office loses its orange trim.
        [Test]
        public void MeasuredRolesMatchWhatTheBuildingsActuallyLookLike()
        {
            AssertHue(By("Fire_0").Wall, 160, 42, 42, "a fire station is red");
            AssertHue(By("Fire_0").Reveal, 219, 219, 219, "with white trim");
            AssertHue(By("Post_0").Reveal, 208, 105, 30, "the post office is trimmed in postal orange");
            AssertHue(By("Barn_0").Wall, 140, 55, 55, "a barn is barn red");
            AssertHue(By("House_00").Wall, 203, 199, 166, "House_00 is the cream one");
        }

        static void AssertHue(int rgb, int r, int g, int b, string why)
        {
            var (pr, pg, pb) = WallPalettes.Split(rgb);
            Assert.That((pr, pg, pb), Is.EqualTo((r, g, b)), why);
        }

        // BREAK IT: default RevealTexel to 0 instead of 2, or skip the tail columns -> a short row silently
        // takes the wall colour for its trim and the frame disappears into the wall.
        [Test]
        public void ShortRowsFallBackWithoutThrowing()
        {
            var p = WallPalettes.Parse(new[] { "x\t010203\t040506\t070809\t0a0b0c\t0d0e0f\t101112\t131415\t161718" });
            Assert.That(p.Count, Is.EqualTo(1));
            Assert.That(p[0].RevealTexel, Is.EqualTo(2), "the commonest measured role, not texel 0");
            Assert.That(p[0].Thickness, Is.EqualTo(WallOpenings.DefaultThickness).Within(1e-4f));

            Assert.That(WallPalettes.Parse(new[] { "junk", "a\tb", "" }).Count, Is.EqualTo(0), "garbage is skipped, not thrown");
            Assert.That(WallPalettes.Parse(null).Count, Is.EqualTo(0));
        }

        // Most buildings agree; the exceptions are the reason the roles are stored per material at all.
        // BREAK IT: hardcode wall=0/reveal=2 for everything -> the four dissenters stop dissenting.
        [Test]
        public void TheRolesAreNotUniformAcrossBuildings()
        {
            var all = Load();
            Assert.That(all.Count(p => p.WallTexel != 0), Is.GreaterThanOrEqualTo(4),
                "several buildings do not put their wall colour in texel 0");
            Assert.That(all.Count(p => p.WallTexel == 0), Is.GreaterThan(all.Count / 2),
                "but most do -- if that stops being true the sampler has drifted");
        }
    }
}
