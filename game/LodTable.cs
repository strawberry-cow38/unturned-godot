using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Retail's prop draw-distance rules, ported. The port previously rendered every placed prop at full
    // detail from anywhere on the map -- 4329 of them on PEI, including books and mugs visible from across
    // the island. Retail culls them by TWO independent rules and takes whichever bites first:
    //
    //   1. PER-RENDER-LAYER distance -- the dominant one (GraphicsSettings.layerCullDistances):
    //          defaultCullDistance = 256 + normalizedDrawDistance * 256      -> [256, 512]
    //          LARGE = default      MEDIUM = default * 0.5      SMALL = default * 0.125
    //      A prop's layer is simply which Objects/{Large,Medium,Small} bundle folder it ships in, so the
    //      categorisation is retail's own -- extract_lods.py carries it across.
    //
    //   2. PER-PROP Unity LODGroup -- each level's screenRelativeHeight is the fraction of screen height
    //      the prop must cover to keep drawing; below the LAST one Unity culls it. Converted the way
    //      Unity's LODUtility does:
    //          distance = size / (2 * h * tan(fov/2)) * lodBias
    //      with lodBias = 2 + normalizedDrawDistance * 3, i.e. [2, 5] and NOT 1. Using 1 culls everything
    //      two-to-five times too close and props pop in at arm's length.
    //
    // At retail defaults (DrawDistance 1.0 -> cull 512, bias 5) rule 1 binds for 300 of PEI's 338 resolved
    // props and rule 2 tightens 37 more, so both are worth having but the layer is what does the work.
    //
    // Culling is a HARD pop, matching retail -- Unity's layer cull does not cross-fade, and Unturned's
    // pop-in is part of how the game looks. FoliageField/ResourceField already cull the same way.
    public static class LodTable
    {
        public struct Entry
        {
            public string Layer;      // LARGE | MEDIUM | SMALL -- the retail render layer
            public float Size;        // LODGroup m_Size: bounding-sphere diameter at unit scale (0 = no LODGroup)
            public float[] Heights;   // screenRelativeHeight per level, LOD0 first; last = cull threshold
        }

        static readonly Dictionary<string, Entry> _byGuid = new();
        public static int Count => _byGuid.Count;
        public static bool Loaded { get; private set; }

        /// <summary>Retail's normalized draw-distance preference [0,1]. Default 1.0 = GraphicsSettingsData's default.</summary>
        public static float DrawDistance = 1f;

        public static float DefaultCullDistance => 256f + Mathf.Clamp(DrawDistance, 0f, 1f) * 256f;
        public static float LodBias => 2f + Mathf.Clamp(DrawDistance, 0f, 1f) * 3f;

        public static float LayerCull(string layer) => layer switch
        {
            "SMALL" => DefaultCullDistance * 0.125f,
            "MEDIUM" => DefaultCullDistance * 0.5f,
            _ => DefaultCullDistance,       // LARGE (landmarkExtraDistance not modelled: it needs the far-clip setting)
        };

        public static void Load(string path)
        {
            _byGuid.Clear();
            Loaded = false;
            if (!Godot.FileAccess.FileExists(path) && !System.IO.File.Exists(path)) { GD.Print($"[lod] no table at {path} -- props will not cull"); return; }
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5) continue;
                float[] hs = null;
                if (p[4] != "-")
                {
                    var hp = p[4].Split(',', System.StringSplitOptions.RemoveEmptyEntries);
                    hs = new float[hp.Length];
                    for (int i = 0; i < hp.Length; i++) hs[i] = float.Parse(hp[i], System.Globalization.CultureInfo.InvariantCulture);
                }
                _byGuid[p[0].ToLowerInvariant()] = new Entry
                {
                    Layer = p[2],
                    Size = float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
                    Heights = hs,
                };
            }
            Loaded = _byGuid.Count > 0;
            GD.Print($"[lod] {_byGuid.Count} props, cull {DefaultCullDistance:0}m (LARGE) / {DefaultCullDistance * 0.5f:0}m / {DefaultCullDistance * 0.125f:0}m, lodBias {LodBias:0.0}");
        }

        /// <summary>Unity's LODUtility conversion: how far away this prop still covers `h` of the screen height.</summary>
        public static float DistanceForHeight(float size, float h, float fovDeg)
        {
            if (h <= 0f || size <= 0f) return float.MaxValue;
            float tanHalf = Mathf.Tan(Mathf.DegToRad(fovDeg) * 0.5f);
            return size / (2f * h * tanHalf) * LodBias;
        }

        /// <summary>Distance past which this prop stops rendering: the TIGHTER of the layer cull and the
        /// LODGroup's last transition. Returns 0 for an unknown GUID -- the caller leaves those uncalled
        /// rather than guessing a threshold and popping a landmark out of existence.</summary>
        public static float CullDistance(string guid, float fovDeg)
        {
            if (!_byGuid.TryGetValue(guid.ToLowerInvariant(), out var e)) return 0f;
            float layer = LayerCull(e.Layer);
            if (e.Heights == null || e.Heights.Length == 0 || e.Size <= 0f) return layer;
            return Mathf.Min(layer, DistanceForHeight(e.Size, e.Heights[e.Heights.Length - 1], fovDeg));
        }

        /// <summary>The visible distance band for each LOD level, LOD0 first: level i draws in [Begin, End).
        /// Bands are contiguous and monotone, and the LAST End is the cull distance, so this is a superset of
        /// CullDistance. Null for an unknown GUID. A band with Begin == End never draws -- the layer cull
        /// already ended the prop before that level would have started -- and the caller skips it.</summary>
        public static (float Begin, float End)[] LevelRanges(string guid, float fovDeg)
        {
            if (!_byGuid.TryGetValue(guid.ToLowerInvariant(), out var e)) return null;
            float layer = LayerCull(e.Layer);
            if (e.Heights == null || e.Heights.Length == 0 || e.Size <= 0f)
                return new[] { (0f, layer) };
            var r = new (float, float)[e.Heights.Length];
            float prev = 0f;
            for (int i = 0; i < e.Heights.Length; i++)
            {
                float d = Mathf.Min(layer, DistanceForHeight(e.Size, e.Heights[i], fovDeg));
                if (d < prev) d = prev;   // heights are authored descending, but clamping to `layer` can flatten
                                          // two levels onto the same distance; keep the bands monotone regardless
                r[i] = (prev, d);
                prev = d;
            }
            return r;
        }

        public static bool TryGet(string guid, out Entry e) => _byGuid.TryGetValue(guid.ToLowerInvariant(), out e);
    }
}
