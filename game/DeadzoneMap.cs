using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>The map's authored deadzone volumes, read from content/deadzones*.tsv.
    ///
    /// Retail keeps these in Level.hierarchy as SDG.Framework.Devkit.DeadzoneVolume blocks;
    /// tools/parse_hierarchy_deadzones.py lifts them into a TSV at author time, the same shape and the same
    /// Z-negation the named-location and node parsers use. Runtime reads the TSV rather than the hierarchy
    /// so the dependency on a local Unturned install stays an AUTHORING one.
    ///
    /// Why this exists: DeadzoneField, DeadzoneSim, the net sync, the HUD icon and the overlay shader were
    /// all finished and wired, and nothing ever put a volume in the world -- the demo volume was deleted on
    /// 2026-08-19 with a note that real ones belong in map data, and the map-data half was never written. So
    /// every one of those parts worked and a player could not encounter a deadzone anywhere on PEI.
    ///
    /// ⚠ RATES ARE OURS, GEOMETRY IS THE MAP'S. The hierarchy carries retail's numbers
    /// (UnprotectedRadiationPerSecond 6.25 on PEI) and they are NOT in our units -- DeadzoneDef's rates are
    /// dose rates re-derived against our infection model, where the tuned unprotected rate is 0.020. The map
    /// types PEI's zone "DefaultRadiation", which means "the default radiation profile", so we read the KIND
    /// and apply DeadzoneDef.Default for it.
    public static class DeadzoneMap
    {
        /// <summary>Set by Main per map, exactly like FoliageField.MapDir: PEI -> "deadzones.tsv",
        /// others -> "deadzones_<key>.tsv". A map with no file gets NO deadzones, which is correct --
        /// most maps have none, and falling back to PEI's would put a radiation sphere in the sea.</summary>
        public static string MapFile = "deadzones.tsv";

        public readonly struct Entry
        {
            public readonly Vector3 Center, HalfExtent;
            public readonly DeadzoneShape Shape;
            public readonly DeadzoneKind Kind;
            public Entry(Vector3 c, Vector3 h, DeadzoneShape s, DeadzoneKind k)
            { Center = c; HalfExtent = h; Shape = s; Kind = k; }
        }

        /// <summary>Parse the TSV. Returns an empty list for a missing file (a map with no deadzones) and
        /// skips a malformed row rather than dropping the whole map's zones for one bad line.</summary>
        public static List<Entry> Load(string file = null)
        {
            var list = new List<Entry>();
            string path = ProjectSettings.GlobalizePath("res://content/" + (file ?? MapFile));
            if (!System.IO.File.Exists(path)) return list;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var c = line.Split('\t');
                if (c.Length < 4) { Log.Err($"[deadzone] malformed row (want 4 columns): {line}"); continue; }
                var p = c[1].Split(','); var h = c[2].Split(',');
                if (p.Length < 3 || h.Length < 3) { Log.Err($"[deadzone] malformed vector in row: {line}"); continue; }
                if (!float.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out float px) ||
                    !float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out float py) ||
                    !float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out float pz) ||
                    !float.TryParse(h[0], System.Globalization.NumberStyles.Float, ci, out float hx) ||
                    !float.TryParse(h[1], System.Globalization.NumberStyles.Float, ci, out float hy) ||
                    !float.TryParse(h[2], System.Globalization.NumberStyles.Float, ci, out float hz))
                { Log.Err($"[deadzone] unparseable number in row: {line}"); continue; }

                var shape = c[0].Trim().Equals("Sphere", System.StringComparison.OrdinalIgnoreCase)
                    ? DeadzoneShape.Sphere : DeadzoneShape.Box;
                var kind = c[3].Trim().Equals("Radiation", System.StringComparison.OrdinalIgnoreCase)
                    ? DeadzoneKind.Radiation : DeadzoneKind.Radiation;   // one kind so far; parsed so the column means something when a second lands
                list.Add(new Entry(new Vector3(px, py, pz), new Vector3(hx, hy, hz), shape, kind));
            }
            return list;
        }

        /// <summary>Load the current map's zones into a field. Returns how many landed.</summary>
        public static int Populate(DeadzoneField field, string file = null)
        {
            if (field == null) return 0;
            int n = 0;
            foreach (var e in Load(file)) { field.AddVolume(e.Center, e.HalfExtent, e.Shape, DeadzoneDef.Default(e.Kind)); n++; }
            return n;
        }
    }
}
