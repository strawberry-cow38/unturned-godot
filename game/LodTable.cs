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

        /// <summary>Vertical FOV the LOD math is evaluated at -- OptionsSettings.DesiredVerticalFieldOfView /
        /// PreferenceData Field_Of_View_Hip, the same 60 Viewmodel.SourceFov ports.</summary>
        public const float SourceFov = 60f;

        public static float DefaultCullDistance => 256f + Mathf.Clamp(DrawDistance, 0f, 1f) * 256f;
        public static float LodBias => 2f + Mathf.Clamp(DrawDistance, 0f, 1f) * 3f;

        /// <summary>Hard cap on how far placed objects and trees exist at all, independent of the per-layer
        /// cull: retail streams them by REGION and only keeps 3.5 regions of 128m in each direction --
        /// `RegularObjectMaxDistance = Mathf.Min(defaultCullDistance, 447)`, and RegularTreeMaxDistance is
        /// assigned from it. So nothing placed draws past this no matter what its layer or LODGroup says.</summary>
        public static readonly float RegionMaxDistance = RegionMaxOverride();

        /// <summary>UG_REGIONMAX overrides the 447m region cap, so the cost of a longer draw distance can be
        /// MEASURED rather than argued from face counts. Not a shipping knob: the default is retail's value and
        /// nothing in the game writes this.</summary>
        static float RegionMaxOverride()
        {
            var v = System.Environment.GetEnvironmentVariable("UG_REGIONMAX");
            return float.TryParse(v, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out float f) && f > 0f ? f : 447f;
        }

        public static float LayerCull(string layer) => Mathf.Min(RegionMaxDistance, layer switch
        {
            "SMALL" => DefaultCullDistance * 0.125f,
            "MEDIUM" => DefaultCullDistance * 0.5f,
            _ => DefaultCullDistance,       // LARGE -- the 447m region cap is what actually bites here, not the 512m layer
        });
        // NB the landmark extension (landmarkExtraDistance, and the skybox billboards past the cap) is NOT
        // modelled, and does not need to be at retail defaults: LandmarkQuality defaults to OFF and
        // LandmarkDistance to 0.0, so SkyboxTreeMaxDistance/SkyboxObjectMaxDistance both evaluate to 0 and
        // landmarkExtraDistance to 0. The billboards are opt-in, not the default experience.

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
        // Props retail shipped with NO LODGroup have no GUID row here, so the guid-only overload returns null
        // and every generated LOD stays unreachable. This name-keyed fallback is what makes them draw.
        static readonly System.Collections.Generic.Dictionary<string, Entry> _byGeneratedName = new();
        public static int GeneratedCount => _byGeneratedName.Count;
        public static int GeneratedHits, GeneratedMisses;

        public static void LoadGenerated(string path)
        {
            _byGeneratedName.Clear();
            if (!System.IO.File.Exists(path)) return;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                var p = line.Split('\t');
                if (p.Length < 4) continue;
                if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float size)) continue;
                var hs = new System.Collections.Generic.List<float>();
                foreach (var h in p[3].Split(','))
                    if (float.TryParse(h, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out float hv)) hs.Add(hv);
                if (hs.Count == 0) continue;
                _byGeneratedName[p[0]] = new Entry { Layer = p[1], Size = size, Heights = hs.ToArray() };
            }
            GD.Print($"[lod] {_byGeneratedName.Count} generated bands (props retail shipped with no LODGroup)");
        }

        /// <summary>Bands for a prop, preferring retail's authored GUID row and falling back to a generated
        /// name row. Retail always wins where it exists -- nothing Nelson authored is overridden.</summary>
        public static (float Begin, float End)[] LevelRanges(string guid, string name, float fovDeg)
        {
            var r = LevelRanges(guid, fovDeg);
            // "Retail authored something" is not the same as "retail authored a CHAIN". 137 props have a
            // lods.txt row declaring only a cull distance -- one level, no lower mesh -- and the first version
            // of this returned early on `r != null`, so those never reached the generated table at all while
            // the apply block silently rejected them for having Length <= 1. The generated LODs stayed inert
            // for 0 hits and 0 misses: the fallback was not firing, not failing.
            if (r != null && r.Length > 1) return r;                      // a real retail chain: untouchable
            if (name == null || !_byGeneratedName.TryGetValue(name, out var g)) { GeneratedMisses++; return r; }

            GeneratedHits++;
            var gen = RangesFor(g, fovDeg);
            // Retail's own cull distance WINS where it exists. We are adding a LOD split inside the distance
            // Nelson chose, not extending how far the prop draws -- those are different decisions and only the
            // first one was asked for.
            if (r != null && r.Length == 1 && gen.Length > 1)
            {
                float retailCull = r[0].End;
                var last = gen[gen.Length - 1];
                if (last.End > retailCull)
                {
                    gen[gen.Length - 1] = (Mathf.Min(last.Begin, retailCull), retailCull);
                    if (gen[gen.Length - 1].Begin >= retailCull) return r;   // no room for a split inside it
                }
            }
            return gen;
        }

        public static (float Begin, float End)[] LevelRanges(string guid, float fovDeg)
        {
            if (!_byGuid.TryGetValue(guid.ToLowerInvariant(), out var e)) return null;
            return RangesFor(e, fovDeg);
        }

        static (float Begin, float End)[] RangesFor(Entry e, float fovDeg)
        {
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

        // ---- RESOURCES (trees / bushes / rocks) -------------------------------------------------
        // Same two rules as props, different layer. Retail puts resources on LayerMasks.RESOURCE, whose
        // cull is the full defaultCullDistance (256-512m) -- so a tree draws as far as a building, not the
        // 320m the port used to hardcode. Which rule binds flips with size: a 21m birch's LODGroup computes
        // to ~3000m so the LAYER stops it, while a small bush's LODGroup bites well inside 512m.
        static readonly System.Collections.Generic.Dictionary<string, Entry> _byResource = new();
        public static int ResourceCount => _byResource.Count;

        public static void LoadResources(string path)
        {
            _byResource.Clear();
            if (!System.IO.File.Exists(path)) { GD.Print($"[lod] no resource table at {path} -- trees keep the built-in fallback"); return; }
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 3) continue;
                float[] hs = null;
                if (p[2] != "-")
                {
                    var hp = p[2].Split(',', System.StringSplitOptions.RemoveEmptyEntries);
                    hs = new float[hp.Length];
                    for (int i = 0; i < hp.Length; i++) hs[i] = float.Parse(hp[i], System.Globalization.CultureInfo.InvariantCulture);
                }
                // bundle dirs are lowercase (birch_0), the port's content is cased (Birch_0) -- key on lower
                _byResource[p[0].ToLowerInvariant()] = new Entry
                {
                    Layer = "RESOURCE",
                    Size = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                    Heights = hs,
                };
            }
            GD.Print($"[lod] {_byResource.Count} resources, layer cull {DefaultCullDistance:0}m");
        }

        /// <summary>Draw distance for a resource by asset name (case-insensitive). 0 = unknown, caller keeps
        /// its own fallback rather than inventing one.</summary>
        public static float ResourceCull(string name, float fovDeg)
        {
            if (string.IsNullOrEmpty(name) || !_byResource.TryGetValue(name.ToLowerInvariant(), out var e)) return 0f;
            // LayerMasks.RESOURCE gets the full distance, but RegularTreeMaxDistance caps it at the same
            // 447m region limit as objects -- so a big tree stops there, not at the 512m layer distance.
            float layer = Mathf.Min(RegionMaxDistance, DefaultCullDistance);
            if (e.Heights == null || e.Heights.Length == 0 || e.Size <= 0f) return layer;
            return Mathf.Min(layer, DistanceForHeight(e.Size, e.Heights[e.Heights.Length - 1], fovDeg));
        }
    }
}
