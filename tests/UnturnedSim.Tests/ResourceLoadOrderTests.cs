using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    /// <summary>
    /// resources.txt ROW ORDER IS THE WIRE FORMAT, and until now the only thing guarding it was somebody
    /// remembering that.
    ///
    /// ResourceField.LoadResources walks the manifest line by line building _instances, and
    /// InstanceCount = _instances.Count is what sizes the replicated alive-bitmap
    /// (WorldNetSync.cs:211 -> ResourceReplication.ServerInit). So a resource's position in this file IS
    /// its network id -- ReplicationIds.SystemResources is documented as "alive-bitmap keyed by load-order
    /// index".
    ///
    /// ⚠ AND IT IS NOT ONLY DISPLAY STATE. The same index rides ForageResourceCommand.Index
    /// (WorldReplication.cs:466), a CLIENT COMMAND. So two peers built from different row orders do not
    /// merely draw trees wrong: a player clicks bush A and the server harvests bush B. The client acts on
    /// the wrong entity.
    ///
    /// Nothing detects that at runtime. The handshake's content hash cannot: NetContent.Identity is a
    /// hardcoded string ("unturned-godot content v1") hashed into NetContent.Hash, so it is a
    /// somebody-remembered-to-bump-it check and is derived from none of the content it claims to identify
    /// -- the same hole as v50, and the same shape as the UGSR status block, which is an index into a
    /// table that is not itself versioned.
    ///
    /// This test does NOT fix that; the fix is a wire-design decision (derive the identity from the
    /// content) and belongs to whoever owns the protocol. What it does is convert a regen of this file
    /// from a SILENT DESYNC into a BUILD-TIME FAILURE, which is the achievable half -- the same trade as
    /// the UGSR status-block golden.
    ///
    /// ⚠ IF THIS FAILS, DO NOT JUST RE-BLESS IT. Reordering or inserting a row renumbers every resource
    /// after it. Either restore the previous order, or accept the renumber deliberately AND move both
    /// sides of the wire together, exactly as a protocol bump would.
    /// (Gap found by cow tools, 2026-09-16, who had been holding this invariant in their own notes.)
    /// </summary>
    [TestFixture]
    public class ResourceLoadOrderTests
    {
        static string ManifestPath(string dir)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string p = Path.Combine(d.FullName, "game", "content", dir, "resources.txt");
                if (File.Exists(p)) return p;
                d = d.Parent;
            }
            throw new FileNotFoundException($"game/content/{dir}/resources.txt not found above " + AppContext.BaseDirectory);
        }

        // The manifest's first token per line is the resource name; blank lines and comments are skipped the
        // way the loader skips them. Order is preserved deliberately -- it IS the thing under test.
        static List<string> Names(string dir) =>
            File.ReadAllLines(ManifestPath(dir))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("//"))
                .Select(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0])
                .ToList();

        // Goldened 2026-09-16. These are ORDERED lists, not sets: index i is resource id i on the wire.
        static readonly Dictionary<string, string[]> Expected = new()
        {
            ["resources"] = new[]
            {
                "Birch_1",
                "Maple_1",
                "Maple_0",
                "Birch_0",
                "Bush_1",
                "Bush_0",
                "Pine_0",
                "Pine_1",
                "Cane_00",
                "Metal_2",
                "Mushroom_Red_0",
                "Mushroom_Brown_0",
                "Snow_Pile_00",
                "Bush_Mauve",
                "Bush_Indigo",
                "Bush_Jade",
                "Bush_Amber",
                "Bush_Russet",
                "Bush_Teal",
                "Bush_Vermillion",
                "Bush_Hanu",
                "Clay_1",
                "Clay_0",
                "Clay_2",
                "Clay_4",
                "Clay_3",
            },
        };

        [Test]
        public void the_default_map_resource_order_has_not_moved()
        {
            var actual = Names("resources");
            var expected = Expected["resources"];
            // EVERY existing row is pinned, not a prefix. A first cut pinned only the first four and I
            // caught it before committing: a swap at rows 10/11 renumbers just as thoroughly as one at
            // 0/1 and would have sailed through. Appending at the END is still the one safe edit and
            // still passes, because the assert compares the first Expected.Length entries -- so growth
            // is cheap and nobody is tempted to re-bless the list wholesale.
            Assert.That(actual.Count, Is.GreaterThanOrEqualTo(expected.Length),
                "resources.txt lost rows. Removing a row renumbers every resource after it, and the "
                + "alive-bitmap plus ForageResourceCommand.Index both key off that number.");
            Assert.That(actual.Take(expected.Length).ToArray(), Is.EqualTo(expected),
                "resources.txt ROW ORDER CHANGED, and row order is the wire id for the resource system. "
                + "Every peer built from a different order disagrees about which tree is which -- and "
                + "because ForageResourceCommand carries the same index, a player clicking one bush "
                + "harvests another. Nothing at runtime detects this: NetContent.Hash is derived from a "
                + "hardcoded string, not from the content. Restore the order, or take the renumber "
                + "deliberately and move both sides of the wire together.");
        }

        [Test]
        public void every_map_manifest_parses_and_has_unique_names()
        {
            foreach (var dir in new[] { "resources", "resources_washington", "resources_yukon" })
            {
                List<string> names;
                try { names = Names(dir); }
                catch (FileNotFoundException) { continue; }   // a map that ships no manifest is not this test's business
                Assert.That(names, Is.Not.Empty, $"{dir}/resources.txt parsed to zero rows -- the loader would size the bitmap at 0");
                var dupes = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                Assert.That(dupes, Is.Empty,
                    $"{dir}/resources.txt repeats a resource name, so two wire ids point at the same asset "
                    + "and a harvest of one is ambiguous: " + string.Join(", ", dupes));
            }
        }
    }
}
