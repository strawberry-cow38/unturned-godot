using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The map's authored terrain cuts (retail LandscapeHoleVolume), read from content/terraincuts*.tsv.
    ///
    /// Retail punches holes in the landscape with placed VOLUMES, not with a brush -- EditorTerrain says as much
    /// where it explains that the port's own Dig/Fill brush is "ours, not Devkit's". The port had the whole hole
    /// machinery already: a per-quad mask, SetHole/IsHole, mesh and collider rebuild that honour it, an editor
    /// brush, a save sidecar. What it did not have was anything that read the MAP's volumes, and the sidecar only
    /// loads beside a saved editor sculpt (LoadHeightmap -> LoadHoles), which retail PEI has none of. So PEI's
    /// authored cut was solid ground every load, and the terrain cuts "did not work" on the one map we ship.
    ///
    /// tools/parse_hierarchy_holes.py lifts the volumes out of Level.hierarchy at author time, same TSV-in-content
    /// pattern and same Z-negation as the node, location and deadzone parsers.
    ///
    /// ⚠ The retail type is SDG.Framework.<b>Landscapes</b>.LandscapeHoleVolume -- NOT .Devkit., which is where
    /// DeadzoneVolume lives. Assuming the sibling namespace produced a parser that matched nothing and wrote an
    /// empty file, which is a silent no-op; the extractor prints its count so that cannot pass unnoticed again.</summary>
    public static class TerrainCuts
    {
        /// <summary>Set by Main per map, like FoliageField.MapDir. A map with no file gets NO cuts -- never
        /// another map's, which would punch holes at coordinates that mean nothing here.</summary>
        public static string MapFile = "terraincuts.tsv";

        public readonly struct Cut
        {
            public readonly Vector3 Centre, Half;
            public Cut(Vector3 c, Vector3 h) { Centre = c; Half = h; }
        }

        public static List<Cut> Load(string file = null)
        {
            var list = new List<Cut>();
            string path = ProjectSettings.GlobalizePath("res://content/" + (file ?? MapFile));
            if (!System.IO.File.Exists(path)) return list;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var c = line.Split('\t');
                if (c.Length < 3) { Log.Err($"[terraincut] malformed row (want 3 columns): {line}"); continue; }
                var p = c[1].Split(','); var h = c[2].Split(',');
                if (p.Length < 3 || h.Length < 3) { Log.Err($"[terraincut] malformed vector: {line}"); continue; }
                if (!float.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out float px) ||
                    !float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out float py) ||
                    !float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out float pz) ||
                    !float.TryParse(h[0], System.Globalization.NumberStyles.Float, ci, out float hx) ||
                    !float.TryParse(h[1], System.Globalization.NumberStyles.Float, ci, out float hy) ||
                    !float.TryParse(h[2], System.Globalization.NumberStyles.Float, ci, out float hz))
                { Log.Err($"[terraincut] unparseable number: {line}"); continue; }
                // Shape column is carried for symmetry with the deadzone file; every landscape hole retail
                // authors is a Box, and a Sphere would need its own quad test rather than silently using this one.
                if (!c[0].Trim().Equals("Box", System.StringComparison.OrdinalIgnoreCase))
                { Log.Err($"[terraincut] unsupported shape '{c[0]}' -- only Box is implemented; skipping"); continue; }
                list.Add(new Cut(new Vector3(px, py, pz), new Vector3(hx, hy, hz)));
            }
            return list;
        }

        /// <summary>Apply the current map's cuts to a freshly loaded terrain. MUST run before the first
        /// RebuildAll: the rebuild is what turns the mask into missing faces and the matching collider, and a
        /// cut applied afterwards would leave collision standing in a hole you can see through.
        /// Returns the number of quads actually cut.</summary>
        public static int Apply(Terrain terr, string file = null)
        {
            if (terr == null) return 0;
            int quads = 0, vols = 0;
            foreach (var c in Load(file)) { quads += terr.CutHoleBox(c.Centre, c.Half); vols++; }
            if (vols > 0) Log.Print($"[terraincut] {vols} volume(s) from {file ?? MapFile} -> {quads} quad(s) cut");
            return quads;
        }
    }
}
