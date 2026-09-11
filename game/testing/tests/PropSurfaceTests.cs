using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>What placed props are made of (master 2026-09-10: "wire all the audio properly. how it is
    /// done in the source game").
    ///
    /// WorldBuilder decided every prop's surface with one ternary -- `fmesh != null ? Wood : Concrete`,
    /// "trees have a foliage mesh so they are wood, everything else in the world is concrete". The bundles
    /// carry the real answer on each collider, and it is not close: of the 1028 object prefabs that name a
    /// physic material, 506 are METAL and only 134 are concrete. Fences, containers and car wrecks all rang
    /// like pavement, and the impact debris and decal came off the same wrong answer.
    ///
    /// So the checks are against the SHIPPED table -- what the retail materials say specific props are --
    /// rather than against the mapping function agreeing with itself, and the headline case is asserted as a
    /// NOT-concrete so the value the bug produced fails.</summary>
    public sealed class PropSurfaceTests : GameTest
    {
        public override string Name => "prop.surfaces";
        public override double TimeoutSimSeconds => 20;

        static Dictionary<string, string> LoadTable()
        {
            var d = new Dictionary<string, string>();
            string p = ProjectSettings.GlobalizePath("res://content/objects/surfaces.tsv");
            if (!System.IO.File.Exists(p)) return d;
            foreach (var ln in System.IO.File.ReadAllLines(p))
            {
                var c = ln.Split('\t');
                if (c.Length >= 2 && c[0].Length > 0) d[c[0].Trim().ToLowerInvariant()] = c[1].Trim();
            }
            return d;
        }

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);

            var tbl = LoadTable();
            T.Check($"surfaces.tsv ships the prop table ({tbl.Count} rows)", tbl.Count > 900);
            T.Check("...and it loads", PropSurfaces.Count > 900 || PropSurfaces.For("ac_0") != null);

            // ---- THE MATERIALS THE BUNDLE ACTUALLY NAMES all resolve to a real surface.
            var seen = new HashSet<string>();
            foreach (var kv in tbl) seen.Add(kv.Value);
            T.Check($"every distinct material maps ({seen.Count} of them)", seen.Count >= 12);
            foreach (var m in seen)
            {
                var s = PropSurfaces.SurfForMaterial(m);
                T.Check($"{m} -> {s}", System.Enum.IsDefined(typeof(PlayerController.Surf), s));
            }

            // ---- THE SPLIT THAT IS NOT A SOUND. _Static/_Dynamic is whether the collider moves; both halves
            // of a material are the same stuff and must land on the same surface, or a pushed crate would
            // sound different from a nailed-down one.
            foreach (var pair in new[] { ("Concrete_Static", "Concrete_Dynamic"), ("Metal_Static", "Metal_Dynamic"),
                                         ("Wood_Static", "Wood_Dynamic"), ("Gravel_Static", "Gravel_Dynamic"),
                                         ("Tile_Static", "Tile_Dynamic"), ("Cloth_Static", "Cloth_Dynamic") })
                T.Check($"{pair.Item1} and {pair.Item2} are the same surface",
                        PropSurfaces.SurfForMaterial(pair.Item1) == PropSurfaces.SurfForMaterial(pair.Item2));
            T.Check("Metal_Slip is still metal", PropSurfaces.SurfForMaterial("Metal_Slip") == PlayerController.Surf.Metal);
            T.Check("Wood_Silent is still wood", PropSurfaces.SurfForMaterial("Wood_Silent") == PlayerController.Surf.Wood);

            // ---- THE HEADLINE: HALF THE MAP IS METAL AND USED TO BE CONCRETE.
            int metal = 0, concrete = 0;
            foreach (var kv in tbl)
            {
                var s = PropSurfaces.SurfForMaterial(kv.Value);
                if (s == PlayerController.Surf.Metal) metal++;
                if (s == PlayerController.Surf.Concrete) concrete++;
            }
            T.Check($"more props are metal than concrete ({metal} vs {concrete})", metal > concrete);
            T.Check($"...and metal is a big share of the world ({metal} of {tbl.Count})", metal > tbl.Count / 3);

            // ---- SPECIFIC PROPS, against the shipped rows. Each is asserted NOT-concrete where the old
            // ternary would have said concrete, so the value the bug produced fails rather than the fix
            // merely agreeing with the table.
            foreach (var name in new[] { "ac_0", "agriculture_0" })
                if (tbl.ContainsKey(name))
                {
                    var got = PropSurfaces.For(name);
                    T.Check($"{name} ({tbl[name]}) is not concrete", got.HasValue && got.Value != PlayerController.Surf.Concrete);
                }

            // ---- A PIECE OF A PROP IS MADE OF WHAT THE PROP IS MADE OF. Our rip splits some props into
            // their own meshes that never had a prefab, so the lookup walks up the name.
            var parent = PropSurfaces.For("ac_0");
            T.Check("a rip-split sub-part inherits its parent", PropSurfaces.For("ac_0_someribbedpart") == parent);

            // ---- AND AN UNKNOWN PROP SAYS SO rather than guessing, so WorldBuilder's fallback still runs.
            T.Check("an unknown prop returns null", PropSurfaces.For("definitely_not_a_real_prop_xyz") == null);
            T.Check("a null name does not throw", PropSurfaces.For(null) == null);

            // ---- TREES AND BUSHES ARE DELIBERATELY ABSENT. They are resources in a different bundle and are
            // exactly the case the old ternary was written for, so they must fall through to it.
            foreach (var res in new[] { "birch_0", "bush_amber", "mushroom_red_0" })
                T.Check($"{res} has no prop row (it is a resource)", PropSurfaces.For(res) == null);
        }
    }
}
