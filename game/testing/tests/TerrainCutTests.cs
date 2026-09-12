using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Authored terrain cuts (retail LandscapeHoleVolume) reach the hole mask.
    ///
    /// Retail punches landscape holes with placed VOLUMES, not a brush. The port had every other part of this
    /// already — the per-quad mask, SetHole/IsHole, a mesh and collider rebuild that honour it, an editor
    /// brush, a save sidecar — and nothing that read the MAP's volumes. The sidecar only loads beside a saved
    /// editor sculpt, which retail PEI has none of, so PEI's authored cut was solid ground on every load.
    ///
    /// Uses a FLAT terrain at a known height rather than loading PEI, so the assertions can be exact. The PEI
    /// end of it is proved by booting: "[terraincut] 1 volume(s) from terraincuts.tsv -> 6 quad(s) cut".</summary>
    public class TerrainCutsApply : GameTest
    {
        public override string Name => "terrain.authored_cuts_apply";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            var terr = Terrain.CreateFlat(1, 1, withCollider: true);
            World.AddChild(terr);
            yield return Ticks(2);

            float ground = terr.SampleHeight(0f, 0f);
            T.Check($"flat ground sampled at y={ground:0.##}", Mathf.Abs(ground) < 200f);
            T.Check("nothing is a hole to start with", !terr.IsHole(10, 10));

            // A volume straddling the ground at the origin. Y span deliberately brackets the measured ground
            // height rather than a guessed one -- the first version of this feature compared the volume's
            // world Y against the NORMALISED grid value (0..1) and cut nothing, and 0.53 looks enough like a
            // height that it read as "the volume is in the wrong place" rather than "wrong unit".
            int cut = terr.CutHoleBox(new Vector3(0f, ground, 0f), new Vector3(6f, 8f, 6f));
            T.Check($"the volume cut quads (got {cut})", cut > 0);

            // It must cut where the volume IS. 12x12 m at 4 m quads is ~3 quads across, centred on origin.
            T.Check("the ground under the volume is now a hole", terr.IsHole(WorldGx(terr, 0f), WorldGy(terr, 0f)));

            // ...and NOT somewhere else. A cut that reports quads but puts them at the map corner is the exact
            // failure the clamped-index version had.
            T.Check("ground 200 m away is untouched", !terr.IsHole(WorldGx(terr, 200f), WorldGy(terr, 200f)));

            // A volume floating clear above the surface must cut NOTHING -- otherwise a hole volume authored
            // for an overpass would punch through the ground beneath it.
            var terr2 = Terrain.CreateFlat(1, 1, withCollider: false);
            World.AddChild(terr2);
            yield return Ticks(1);
            float g2 = terr2.SampleHeight(0f, 0f);
            T.Check("a volume 500 m above the ground cuts nothing",
                    terr2.CutHoleBox(new Vector3(0f, g2 + 500f, 0f), new Vector3(6f, 8f, 6f)) == 0);
            T.Check("...and left no hole behind", !terr2.IsHole(WorldGx(terr2, 0f), WorldGy(terr2, 0f)));

            // ---- the SHIPPED PEI file, parsed by the real loader
            var cuts = TerrainCuts.Load("terraincuts.tsv");
            T.Check($"PEI ships exactly one authored cut (got {cuts.Count})", cuts.Count == 1);
            if (cuts.Count == 1)
            {
                var c = cuts[0];
                // Unity (-773.6877, 54.9054, -769.9827), scale 3.92/5.63/4.22 -> half = scale/2, Z negated.
                T.Check($"centre X ({c.Centre.X:0.##})", Mathf.Abs(c.Centre.X - (-773.6877f)) < 0.01f);
                T.Check($"centre Y ({c.Centre.Y:0.##})", Mathf.Abs(c.Centre.Y - 54.9054f) < 0.01f);
                T.Check($"centre Z is +769.98, not -769.98 ({c.Centre.Z:0.##})", Mathf.Abs(c.Centre.Z - 769.9827f) < 0.01f);
                T.Check($"half is scale/2, not scale ({c.Half.X:0.##} vs 3.92)", Mathf.Abs(c.Half.X - 1.9613f) < 0.01f);
            }
            T.Check("a map with no cut file gets none", TerrainCuts.Load("terraincuts_no_such_map.tsv").Count == 0);
        }

        // Same mapping Terrain uses internally (world Z negated), so the test addresses quads the way the
        // engine does rather than re-deriving it.
        static int WorldGx(Terrain t, float worldX) => Mathf.FloorToInt((worldX - t.BaseX) / Terrain.QuadSize);
        static int WorldGy(Terrain t, float worldZ) => Mathf.FloorToInt((-worldZ - t.BaseZ) / Terrain.QuadSize);
    }
}
