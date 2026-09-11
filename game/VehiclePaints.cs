using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The vehicle spraypaints: 32 retail cans, each a single colour (item .dat `PaintColor`).
    ///
    /// The PAINT SYSTEM was already here -- vehicle_paint.gdshader tints the paintable texels of a body
    /// palette by `paint_color`, and Vehicle.SpawnPaint ports getDefaultPaintColor so cars spawn in their
    /// faction or random-hue colours. What was missing was the only way a PLAYER could change one: the
    /// Vehicle_Paint_Tool item type, which the port collapsed to GENERIC along with everything else outside
    /// its 22-value EItemType. 32 items -- the largest unhandled type in the whole catalog -- that equipped
    /// as nothing and did nothing.
    ///
    /// Table from tools/extract_vehicle_paints.py -> content/vehicle_paints.tsv (id, hex, name). Keyed by id
    /// rather than by a new EItemType value: the colour is per-ITEM, so the id is the lookup either way, and
    /// widening a wire-visible enum for a client-side cosmetic is a worse trade than a table.</summary>
    public static class VehiclePaints
    {
        static Dictionary<ushort, Color> _byId;
        static Dictionary<ushort, string> _nameById;

        public static int Count { get { Load(); return _byId.Count; } }

        static void Load()
        {
            if (_byId != null) return;
            _byId = new Dictionary<ushort, Color>();
            _nameById = new Dictionary<ushort, string>();
            string p = ProjectSettings.GlobalizePath("res://content/vehicle_paints.tsv");
            if (!System.IO.File.Exists(p)) { Log.Print("[paint] no vehicle_paints.tsv"); return; }
            foreach (var ln in System.IO.File.ReadAllLines(p))
            {
                var c = ln.Split('\t');
                if (c.Length < 2 || !ushort.TryParse(c[0].Trim(), out ushort id)) continue;
                string hex = c[1].Trim();
                if (hex.Length != 6) continue;
                _byId[id] = new Color("#" + hex);
                if (c.Length >= 3) _nameById[id] = c[2].Trim();
            }
            Log.Print($"[paint] {_byId.Count} vehicle spraypaints");
        }

        /// <summary>The can's colour, or null if this item is not a spraypaint. Doubles as the "is it one"
        /// test so there is one answer rather than two that can disagree.</summary>
        public static Color? For(ushort itemId)
        {
            Load();
            return _byId.TryGetValue(itemId, out var c) ? c : (Color?)null;
        }

        public static bool Is(ushort itemId) => For(itemId).HasValue;

        public static string NameOf(ushort itemId)
        {
            Load();
            return _nameById.TryGetValue(itemId, out var n) ? n : null;
        }

        public static void Clear() { _byId = null; _nameById = null; }
    }
}
