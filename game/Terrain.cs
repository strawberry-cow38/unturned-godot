using Godot;
using System.IO;

namespace UnturnedGodot
{
    // Loads a real Unturned map's terrain from its Landscape tile heightmaps (source Framework/Landscapes/LandscapeTile.cs).
    // Each Tile_X_Y_Source.heightmap = HEIGHTMAP_RESOLUTION^2 (257x257) BIG-ENDIAN ushorts, x outer / y inner; a sample =
    // raw/65535 (normalized 0..1). Tile = TILE_SIZE 1024 m, samples HEIGHTMAP_WORLD_UNIT 4 m apart; TILE_HEIGHT 2048 m, so
    // world height = h*2048 - 1024 (0.5 = sea level 0). Tile at landscape coord (cx,cy) spans world x[cx*1024 .. +1024],
    // z[cy*1024 .. +1024]. Unity->Godot: negate Z (the port's convention).
    public partial class Terrain : Node3D
    {
        const int RES = 257;
        const int SRES = 256, SLAYERS = 8;   // Landscape SPLATMAP_RESOLUTION + SPLATMAP_LAYERS (per-texel layer weights, 1 byte each)

        // Set by Main per map: PEI -> "terrain", others -> "terrain_<key>". The 8 layer ALBEDOS differ per map
        // (Washington = Yukon dirt/gravel + Washington grass + Russia road/shore + PEI stone), so the splatmap is
        // painted with THIS map's textures. tools/terrain_map.py bakes them from the tile "Materials" GUID palette.
        public static string MapDir = "terrain";
        const float TILE_SIZE = 1024f, TILE_HEIGHT = 2048f, UNIT = 4f;
        const float BRUSH_FALLOFF = 0.5f;   // source Devkit brushFalloff: full strength inside this radius fraction, then linear to 0 at the edge
        static float BrushAlpha(float normDist) => normDist <= BRUSH_FALLOFF ? 1f : (1f - normDist) / (1f - BRUSH_FALLOFF);   // source TerrainEditor.getBrushAlpha

        // The 8 shared terrain material layers, colored as a stand-in until real splatmap texture blending. Inferred from
        // the PEI splatmap layout (layer 5 = ocean/water dominant, 2 = grass/ground, 3 = the road network, 0/7 = forest).
        static Color LayerColor(byte l) => l switch
        {
            // source-accurate: avg colour of each layer's REAL albedo (extracted from core.masterbundle via UnityPy).
            // Layer->material mapping read from PEI Level.hierarchy (see reference_unturned_world memory).
            0 => new Color(0.545f, 0.325f, 0.224f),   // PEI_Dirt_01
            1 => new Color(0.690f, 0.627f, 0.404f),   // PEI_Farm_Wheat_00 (crop field)
            2 => new Color(0.220f, 0.443f, 0.224f),   // PEI_Grass_00
            3 => new Color(0.494f, 0.314f, 0.247f),   // PEI_Gravel_00
            4 => new Color(0.290f, 0.294f, 0.290f),   // Russia_Road_00 (paved, shared)
            5 => new Color(0.170f, 0.310f, 0.470f),   // PEI_Sand_01 (real avg sand=0.69,0.55,0.36) but shown OCEAN BLUE: layer 5 is mostly underwater seabed; real water plane at seaLevel*256 is TODO, then this reverts to sand
            6 => new Color(0.714f, 0.714f, 0.714f),   // Yukon_Snow_00 (shared)
            _ => new Color(0.553f, 0.306f, 0.184f),   // 7: PEI_Stone_01
        };

        // Real per-layer albedo textures (extracted from core.masterbundle), dominant-layer selected + world-tiled.
        // UV samples the dominant-layer index map; the chosen albedo tiles by world XZ at the source scale (texW*0.25 = 16u).
        // Layer 5 (sand seabed) -> ocean blue until a real water plane exists.
        const string TERRAIN_SHADER = @"
shader_type spatial;
uniform sampler2DArray albedos : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D splat0 : filter_linear;
uniform sampler2D splat1 : filter_linear;
uniform float tileWorld = 16.0;
uniform float sea_level = 25.6;                                  // world-Y of the ocean surface -> caustics show only below it
uniform vec3 caustic_tint : source_color = vec3(0.55, 0.9, 1.0);
uniform float caustic_strength = 0.15;   // toned down 70% (master)
global uniform float rain_wetness;                              // 0..1 wet soak (WeatherManager drives it) -> darken + gloss up-facing terrain
global uniform float rain_intensity;                           // 0..1 raindrop-impact splash density/brightness
global uniform sampler2D rain_roof;                             // RainRoofMap (rain_streak.gdshader): roofed ground stays dry
global uniform vec4 rain_roof_rect;
varying vec3 wpos;
void vertex() { wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
// --- caustics: gradient (Perlin) noise so the web is smooth, not blocky; projected in world XZ onto underwater terrain ---
float chashv(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }   // precision-robust at large world coords
vec2 cgrad(vec2 ip) { float h = chashv(ip) * 6.2831853; return vec2(cos(h), sin(h)); }
float cnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = dot(cgrad(i), f), b = dot(cgrad(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(cgrad(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0)), d = dot(cgrad(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 0.8 + 0.5;
}
float cfbm(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 4; i++) { s += a * cnoise(p); p *= 2.03; a *= 0.5; } return s; }
// Splash rings, VERBATIM from wet_surface.gdshader (the road props' shader). Copied rather than shared because
// Godot has no shader include for a string-embedded shader -- but it must stay in step: terrain road and road
// props meet at the kerb, and two subtly different ripple fields there is worse than none.
float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }
float splashes(vec2 wxz, float t, float amt){
    float acc = 0.0;
    float gate = 1.0 - clamp(amt, 0.0, 1.0) * 0.20;
    for (int k = 0; k < 2; k++){
        float sc = 1.6 + float(k) * 2.8;
        vec2 g = wxz * sc + float(k) * 21.0;
        vec2 base = floor(g);
        for (int dy = -1; dy <= 1; dy++){
            for (int dx = -1; dx <= 1; dx++){
                vec2 id = base + vec2(float(dx), float(dy));
                float seed = h21(id);
                float tt = t * (0.65 + seed * 0.7) + seed;
                float cyc = floor(tt);
                float life = fract(tt);
                vec2 q = id + cyc * 13.7;
                vec2 center = id + vec2(h21(q + 1.3), h21(q + 7.7));
                float rad = life * (0.16 + seed * 0.20);
                float ring = smoothstep(0.05, 0.0, abs(length(g - center) - rad));
                acc += ring * (1.0 - life) * step(gate, h21(q)) * (0.55 + seed * 0.45);
            }
        }
    }
    return acc;
}
float caustics(vec2 p, float t) {
    float a = cfbm(p + vec2(t, t * 0.4)), b = cfbm(p * 1.31 + vec2(-t * 0.7, t * 0.55) + 17.3);
    return pow(clamp(1.0 - abs(a - b) * 4.0, 0.0, 1.0), 4.0);
}
void fragment() {
    vec4 w0 = texture(splat0, UV);
    vec4 w1 = texture(splat1, UV);
    vec2 tuv = wpos.xz / tileWorld;
    // WEIGHTED BLEND (strawberry 2026-09-05 ""switch our terrain to blend instead of best wins""). This replaces a
    // winner-take-all pick whose comment said the hard edges matched reference shots -- a deliberate call being
    // deliberately reversed, not a bug being fixed.
    //
    // Weights are normalised so the sum is exactly 1: a splatmap texel does NOT reliably sum to 1 (bilinear
    // filtering between texels of different totals guarantees it), and summing unnormalised weights would make
    // the ground breathe brighter and darker across every blend seam.
    //
    // The `w > 0.004` skip is what keeps this affordable. A blend is 8 array fetches per pixel where the pick was
    // 1, but a splat texel almost always has 1-2 layers actually present, so the branch drops most of them. The
    // threshold is low enough that a layer is never visibly clipped as it fades in.
    float ws[8];
    ws[0] = w0.r; ws[1] = w0.g; ws[2] = w0.b; ws[3] = w0.a;
    ws[4] = w1.r; ws[5] = w1.g; ws[6] = w1.b; ws[7] = w1.a;
    float wsum = 0.0;
    for (int i = 0; i < 8; i++) wsum += ws[i];
    wsum = max(wsum, 1e-4);
    vec3 blended = vec3(0.0);
    for (int i = 0; i < 8; i++) {
        float w = ws[i] / wsum;
        if (w > 0.004) blended += w * texture(albedos, vec3(tuv, float(i))).rgb;
    }
    ALBEDO = blended;
    ROUGHNESS = 1.0;
    // Layer 4 is Russia_Road_00, the PAVED road (layer 3 is gravel -- the older comment upstream calling 3 ""the road
    // network"" predates the real albedo table below it). Carried out of the blend as a 0..1 weight so the wet block
    // can treat a road pixel like the road PROPS do, and a road/grass boundary fades between the two behaviours
    // instead of switching at a hard line -- which is the whole point of blending in the first place.
    float roadw = ws[4] / wsum;
    // `best` survives for the grass/dirt/stone rain gate below: with a blend there is no single dominant layer, so
    // it now means ""which layer is MOST present here"" rather than ""the layer being drawn"".
    int best = 0; float bw = ws[0];
    for (int i = 1; i < 8; i++) { if (ws[i] > bw) { bw = ws[i]; best = i; } }
    // caustics on underwater terrain: a light web projected in world XZ, faded with depth (master 2026-08-17)
    float cdepth = sea_level - wpos.y;
    if (cdepth > 0.0) {
        vec2 cp = mat2(vec2(0.87, 0.5), vec2(-0.5, 0.87)) * (wpos.xz * 0.11);
        cp += 0.8 * (vec2(cfbm(cp * 0.5), cfbm(cp * 0.5 + 7.0)) - 0.5);
        float caust = caustics(cp, TIME * 0.25);
        caust = max(caust, caustics(cp * 1.6 + 9.0, -TIME * 0.2));
        float cfade = clamp(cdepth / 0.4, 0.0, 1.0) * (1.0 - clamp(cdepth / 9.0, 0.0, 1.0));
        // ADD to ALBEDO (not EMISSION) so the scene's sun/sky LIGHTS the caustics -> bright by day, its colour, and
        // FADE to nothing at night instead of glowing nuclear (master). Real caustics are just focused sunlight.
        ALBEDO += caustic_tint * caust * caustic_strength * cfade;
    }
    // RAIN: up-facing terrain soaks dark + glossy and shows raindrop-impact rings (globals set by WeatherManager).
    // GATED on the globals, like the caustics above -- rsplash is ~800 ALU/px and would run on every terrain fragment
    // every frame in clear weather otherwise, for a result that's multiplied by zero (tinyclaw). Both globals sit at 0
    // in fair weather, so the whole block skips and clear weather costs nothing.
    if (rain_intensity > 0.0 || rain_wetness > 0.0) {
        vec3 wn = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);   // world normal -> upness is camera-independent
        float r_up = smoothstep(0.35, 0.75, wn.y);
        if (best == 2 || best == 0 || best == 7) r_up = 0.0;   // GRASS (+ forest floor) never takes the wet look or the rings (strawberry 2026-09-04) -- only sand/road/rock/dirt soak
        // ROOF MAP: ground under a roof / canopy / car stays dry (RainRoofMap; rect.z = 0 -> no map)
        if (rain_roof_rect.z > 0.0) {
            vec2 ruv = (wpos.xz - rain_roof_rect.xy) / (2.0 * rain_roof_rect.z) + 0.5;
            if (ruv.x >= 0.0 && ruv.x <= 1.0 && ruv.y >= 0.0 && ruv.y <= 1.0) {
                ivec2 rsz = textureSize(rain_roof, 0);
                ivec2 rt = clamp(ivec2(ruv * vec2(rsz)), ivec2(0), rsz - ivec2(1));
                if (wpos.y < texelFetch(rain_roof, rt, 0).r - 0.3) r_up = 0.0;   // nearest texel: no invented mid-air heights at roof edges
            }
        }
        float r_wet = clamp(rain_wetness, 0.0, 1.0) * r_up;
        ALBEDO *= mix(1.0, 0.60, r_wet);                             // wet ground darkens
        // PAVED ROAD gets the road PROPS' treatment (strawberry 2026-09-05 ""make the concrete/road terrain material
        // recieve reflections and water ripples""). This reverses the old ""NO splash impacts on terrain"" rule, which
        // was right when terrain was one flat pick of grass-or-dirt and wrong once a road layer is drawn by it.
        // Scaled by roadw, so a road edge fades into the surrounding dirt instead of ending at a hard line.
        float road_wet = r_wet * roadw;
        // Roughness lands between damp ground (0.72) and the road props' wet value (0.42) in proportion to how much
        // road is under this pixel -- the SAME 0.42 cow tools tuned for the props, so asphalt reads identically
        // whether it is a road prop or painted terrain, and reflections match across the kerb.
        ROUGHNESS = mix(mix(1.0, 0.72, r_wet), mix(1.0, 0.42, r_wet), roadw);
        if (rain_intensity > 0.0 && road_wet > 0.0) {
            // ~800 ALU: gated on there being both rain AND road here, so grass and clear weather pay nothing.
            float sp = splashes(wpos.xz, TIME, rain_intensity) * rain_intensity * road_wet;
            ALBEDO += sp * 0.18;                                     // impact_opacity from the prop shader -- subtle glints, not paint
        }
        SPECULAR = 0.5 + r_wet * 0.06 + road_wet * 0.06;             // no metallic -- wet asphalt is not chrome
    }
}
";

        static ShaderMaterial BuildTerrainMaterial(Texture2D splat0, Texture2D splat1)
        {
            var imgs = new Godot.Collections.Array<Image>();
            for (int l = 0; l < SLAYERS; l++)
            {
                var img = new Image();
                if (!ContentProvider.LoadOk(img, ProjectSettings.GlobalizePath($"res://content/{MapDir}/layer{l}.png"))) { GD.Print($"[TERRAIN] texture load FAILED: {MapDir}/layer{l}"); return null; }
                img.Convert(Image.Format.Rgba8);
                img.GenerateMipmaps();
                imgs.Add(img);
            }
            var arr = new Texture2DArray();
            if (arr.CreateFromImages(imgs) != Error.Ok) return null;

            RainSystem3D.EnsureGlobals();   // the shader reads the rain_wetness/rain_intensity globals -- they MUST exist before it compiles
            var mat = new ShaderMaterial { Shader = new Shader { Code = TERRAIN_SHADER } };
            mat.SetShaderParameter("albedos", arr);
            mat.SetShaderParameter("splat0", splat0);
            mat.SetShaderParameter("splat1", splat1);
            mat.SetShaderParameter("tileWorld", 16f);
            mat.SetShaderParameter("sea_level", SeaLevelY);   // caustics show below this (PEI default 25.6 == the uniform default)
            return mat;
        }

        // Merged-map height grid + placement, stashed so gameplay can sample the ground height at a world XZ (spawns etc.).
        float[,] _grid; int _gw, _gh; float _bx, _bz;
        byte[,] _dom; int _dw, _dh;   // dominant splatmap layer per texel -> SampleDominantLayer (grassy-spawn picking)

        // TERRAIN HOLES. One flag per QUAD (not per vertex): a hole is a missing face, and faces are what both the
        // render mesh and the collider are made of. Retail stores bool[256,256] per 1024-unit tile at
        // HOLES_RESOLUTION = heightmap resolution minus one, which is the same "one per cell, not per corner"
        // choice -- Landscape.cs:35.
        //
        // Null until something actually digs. A map with no holes allocates nothing, rebuilds nothing, and keeps
        // the fast collider path everywhere; `_anyHoles` is the cheap test the hot paths ask (retail carries the
        // same idea as `hasAnyHolesData`, and for the same reason).
        bool[,] _holes; bool _anyHoles;

        /// <summary>Is the quad whose low corner is grid (gx,gy) dug out? Out-of-range is solid, so callers can
        /// probe edges without bounds-checking first.</summary>
        public bool IsHole(int gx, int gy) =>
            _anyHoles && _holes != null && gx >= 0 && gy >= 0 && gx < _gw - 1 && gy < _gh - 1 && _holes[gx, gy];

        /// <summary>Dig or fill one quad. Returns true if it changed, so a brush can skip a no-op rebuild.</summary>
        public bool SetHole(int gx, int gy, bool dug)
        {
            if (_grid == null || gx < 0 || gy < 0 || gx >= _gw - 1 || gy >= _gh - 1) return false;
            if (!dug && !_anyHoles) return false;              // filling a map with no holes: nothing to do
            _holes ??= new bool[_gw - 1, _gh - 1];
            if (_holes[gx, gy] == dug) return false;
            JournalHole(gx, gy);
            _holes[gx, gy] = dug;
            if (dug) _anyHoles = true;
            return true;
        }

        /// <summary>Does this chunk contain any dug quad? Decides which COLLIDER this chunk gets, so it is asked
        /// on every chunk rebuild -- see ApplyChunkShape for why that choice is expensive to get wrong.</summary>
        bool ChunkHasHole(int cxi, int cyi)
        {
            if (!_anyHoles || _holes == null) return false;
            int x0 = cxi * CHUNK, y0 = cyi * CHUNK;
            int x1 = System.Math.Min(x0 + CHUNK, _gw - 1), y1 = System.Math.Min(y0 + CHUNK, _gh - 1);
            for (int gx = x0; gx < x1; gx++)
                for (int gy = y0; gy < y1; gy++)
                    if (_holes[gx, gy]) return true;
            return false;
        }

        /// <summary>Total dug quads. For tests and the editor's status line.</summary>
        public int HoleCount
        {
            get
            {
                if (!_anyHoles || _holes == null) return 0;
                int n = 0;
                for (int x = 0; x < _gw - 1; x++) for (int y = 0; y < _gh - 1; y++) if (_holes[x, y]) n++;
                return n;
            }
        }
        MeshInstance3D[,] _chunkMi; StaticBody3D[,] _chunkBody; int _chunksX, _chunksY; Material _terrMat; bool _withCollider;   // editor sculpt: per-chunk meshes so a stroke rebuilds ONLY the touched chunks
        const int CHUNK = 48;   // grid cells per chunk side (chunks share edge verts, so no seams)
        Image _s0Img, _s1Img; ImageTexture _s0Tex, _s1Tex;   // editor splat paint: the live 8-layer weight textures (splat0=layers 0-3, splat1=4-7)

        // Paint the splat map: set every texel in a world-radius brush to a single dominant layer, then re-upload the
        // splat textures (the shader is winner-take-all, so one layer at 1.0 = that material shows). Also updates _dom
        // (gameplay's SampleDominantLayer). Layer 0 Dirt / 1 Wheat / 2 Grass / 3 Gravel / 4 Road / 5 Sand / 6 Snow / 7 Stone.
        public void PaintSplat(float worldX, float worldZ, float radiusWorld, int layer)
        {
            if (_dom == null || _s0Img == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            int cgx = Mathf.RoundToInt(cx), cgy = Mathf.RoundToInt(cy);
            var c0 = new Color(layer == 0 ? 1 : 0, layer == 1 ? 1 : 0, layer == 2 ? 1 : 0, layer == 3 ? 1 : 0);
            var c1 = new Color(layer == 4 ? 1 : 0, layer == 5 ? 1 : 0, layer == 6 ? 1 : 0, layer == 7 ? 1 : 0);
            for (int gx = System.Math.Max(0, cgx - rg); gx <= System.Math.Min(_dw - 1, cgx + rg); gx++)
                for (int gy = System.Math.Max(0, cgy - rg); gy <= System.Math.Min(_dh - 1, cgy + rg); gy++)
                {
                    float dx = (gx - cx) * UNIT, dy = (gy - cy) * UNIT;
                    if (Mathf.Sqrt(dx * dx + dy * dy) > radiusWorld) continue;
                    _dom[gx, gy] = (byte)layer;
                    _s0Img.SetPixel(gx, gy, c0); _s1Img.SetPixel(gx, gy, c1);
                }
            UpdateSplat(_s0Tex, _s0Img); UpdateSplat(_s1Tex, _s1Img);   // guarded: an EMPTY splat image (a map with one splat) used to hit RenderingServer's "p_image is empty" error
        }

        // --- live heightmap sculpt (map editor Terrain tab) ---
        // Raise/lower _grid samples inside a world-radius brush (radial falloff), then rebuild the mesh + collider.
        public void EditHeight(float worldX, float worldZ, float radiusWorld, float deltaWorldY)
        {
            if (_grid == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;   // brush centre in grid space (world Z negated, matching SampleHeight)
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            int cgx = Mathf.RoundToInt(cx), cgy = Mathf.RoundToInt(cy);
            float dNorm = deltaWorldY / TILE_HEIGHT;   // world Y delta -> normalized grid delta
            for (int gx = System.Math.Max(0, cgx - rg); gx <= System.Math.Min(_gw - 1, cgx + rg); gx++)
                for (int gy = System.Math.Max(0, cgy - rg); gy <= System.Math.Min(_gh - 1, cgy + rg); gy++)
                {
                    float dx = (gx - cx) * UNIT, dy = (gy - cy) * UNIT;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > radiusWorld) continue;
                    float falloff = BrushAlpha(dist / radiusWorld);   // source linear falloff (getBrushAlpha)
                    JournalH(gx, gy);
                    _grid[gx, gy] = Mathf.Clamp(_grid[gx, gy] + dNorm * falloff, 0f, 1f);
                }
            _dirty = true;
            RebuildChunksIn(cgx - rg, cgx + rg, cgy - rg, cgy + rg);
        }

        bool _dirty;
        public bool Dirty => _dirty;

        /// <summary>Holes live BESIDE the heightmap, not inside it. The heightmap file is read by the port
        /// translator and by anything else expecting `gw, gh, floats`; appending a second section to it would
        /// break every existing reader for a feature most maps do not use. Retail keeps them separate too
        /// (Landscape/Holes/ per tile).</summary>
        static string HolesPathFor(string heightmapPath) => heightmapPath + ".holes";
        static string RiversPathFor(string heightmapPath) => heightmapPath + ".rivers";

        /// <summary>Persist the carved river segments.
        ///
        /// The CUT already survives on its own -- it lives in the hole mask, which has its own sidecar. What
        /// does not is the BED: it is generated geometry, not grid data, so a reloaded map came back with the
        /// channel correctly cut and nothing underneath it. You could see through the world.
        ///
        /// Segments rather than meshes: the bed is a pure function of (a, b, halfWidth, depth) and the terrain
        /// heights, so storing four numbers per segment and rebuilding beats serialising vertices, and it stays
        /// correct if the bed geometry is ever improved. Same reasoning as not saving the collider.</summary>
        public void SaveRivers(string path)
        {
            if (_rivers.Count == 0)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            // FORMAT 3: the ANCHORS the user placed, per river. v2 stored the sampled polyline plus baked bed
            // vertices -- both derived data, both large, and storing the samples is what made the spline
            // uneditable. This is the smallest thing that can regenerate everything else.
            w.Write(3);
            w.Write(_rivers.Count);
            foreach (var r in _rivers)
            {
                w.Write(r.Anchors.Count);
                foreach (var a in r.Anchors) { w.Write(a.X); w.Write(a.Y); w.Write(a.Z); }
                w.Write(r.Half); w.Write(r.Depth); w.Write(r.Material);
            }
        }

        public void LoadRivers(string path)
        {
            _rivers.Clear();
            if (_riverBeds != null) { _riverBeds.QueueFree(); _riverBeds = null; }   // drop the previous map's beds
            if (!System.IO.File.Exists(path)) return;
            try
            {
                using var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
                int ver = r.ReadInt32();
                if (ver == 3)
                {
                    int n = r.ReadInt32();
                    if (n < 0 || n > 100000) { GD.PushWarning($"[terrain] implausible river count {n}; ignoring"); return; }
                    for (int i = 0; i < n; i++)
                    {
                        int ac = r.ReadInt32();
                        if (ac < 0 || ac > 100000) { GD.PushWarning("[terrain] implausible anchor count; ignoring"); return; }
                        var riv = new River();
                        for (int k = 0; k < ac; k++) riv.Anchors.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                        riv.Half = r.ReadSingle(); riv.Depth = r.ReadSingle(); riv.Material = r.ReadInt32();
                        _rivers.Add(riv);
                    }
                }
                else if (ver == 2)
                {
                    // LEGACY: v2 stored the SAMPLED polyline, one entry per ~4 m segment, plus baked bed
                    // geometry. Chained same-width runs are folded back into one river so the map still opens
                    // and still carves -- but its "anchors" are the old samples, so it will have a node every
                    // few metres until it is redrawn. Better than dropping the map's rivers on the floor.
                    int n = r.ReadInt32();
                    if (n < 0 || n > 200000) { GD.PushWarning($"[terrain] implausible river count {n}; ignoring"); return; }
                    River cur = null; Vector3 last = default;
                    for (int i = 0; i < n; i++)
                    {
                        var a2 = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        var b2 = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        float hf = r.ReadSingle(), dp = r.ReadSingle();
                        bool chains = cur != null && cur.Half == hf && cur.Depth == dp && a2.DistanceSquaredTo(last) < 0.0001f;
                        if (!chains) { cur = new River { Half = hf, Depth = dp }; cur.Anchors.Add(a2); _rivers.Add(cur); }
                        cur.Anchors.Add(b2); last = b2;
                    }
                    int meshes = r.ReadInt32();   // baked beds: read past them, never replayed
                    if (meshes >= 0 && meshes <= 100000)
                        for (int m = 0; m < meshes; m++)
                        {
                            int vc = r.ReadInt32();
                            if (vc < 0 || vc > 20_000_000) break;
                            for (int v = 0; v < vc; v++) { r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); }
                        }
                }
                else { GD.PushWarning($"[terrain] rivers format v{ver} not understood; ignoring"); return; }
            }
            catch (System.Exception e) { GD.PushWarning($"[terrain] bad rivers file, ignoring: {e.Message}"); }
            RebuildRiverField();   // the field is derived, so it has to be rebuilt from the recipe on load
            RebuildRiverWater();
        }

        /// <summary>Write the hole mask, bit-packed 8 quads per byte (retail packs the same way). Writes
        /// NOTHING and deletes any stale file when the map has no holes, so an untouched map costs zero bytes
        /// and cannot resurrect holes from a previous save -- retail's `hasAnyHolesData` guard, same reasoning.</summary>
        public void SaveHoles(string path)
        {
            if (!_anyHoles || _holes == null || HoleCount == 0)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            int hw = _gw - 1, hh = _gh - 1;
            w.Write(hw); w.Write(hh);
            byte acc = 0; int bit = 0;
            for (int x = 0; x < hw; x++)
                for (int y = 0; y < hh; y++)
                {
                    if (_holes[x, y]) acc |= (byte)(1 << bit);
                    if (++bit == 8) { w.Write(acc); acc = 0; bit = 0; }
                }
            if (bit != 0) w.Write(acc);   // the tail: hw*hh is not a multiple of 8 in general
        }

        /// <summary>Read the hole mask. A MISSING file means "no holes", not an error -- that is the normal
        /// state for every map nobody has dug in.</summary>
        public void LoadHoles(string path)
        {
            _holes = null; _anyHoles = false;
            if (!System.IO.File.Exists(path)) return;
            try
            {
                using var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
                int hw = r.ReadInt32(), hh = r.ReadInt32();
                // Refuse a mask that does not match this grid rather than indexing off the end of it: a
                // heightmap and its holes file can drift apart if one is regenerated without the other.
                if (hw != _gw - 1 || hh != _gh - 1)
                { GD.PushWarning($"[terrain] holes {hw}x{hh} do not match grid {_gw - 1}x{_gh - 1}; ignoring"); return; }
                var m = new bool[hw, hh];
                byte acc = 0; int bit = 8; bool any = false;
                for (int x = 0; x < hw; x++)
                    for (int y = 0; y < hh; y++)
                    {
                        if (bit == 8) { acc = r.ReadByte(); bit = 0; }
                        if ((acc & (1 << bit)) != 0) { m[x, y] = true; any = true; }
                        bit++;
                    }
                _holes = m; _anyHoles = any;
            }
            catch (System.Exception e) { GD.PushWarning($"[terrain] bad holes file, ignoring: {e.Message}"); _holes = null; _anyHoles = false; }
        }

        /// <summary>Replace this terrain's heights with a generated island and rebuild the meshes. Operates on
        /// the SAME grid the sculpt brushes edit, so the result is immediately hand-editable and saves through
        /// SaveHeightmap like any other map -- generation is a starting point, not a separate kind of map.</summary>
        /// <summary>The last generated network: which monuments join to which, and the gates on their edges.
        /// Held here rather than returned so the road/rail stages can pick them up without the caller having to
        /// thread them through -- they are read-only outputs of the same generate.</summary>
        public System.Collections.Generic.List<ProcIsland.Link> IslandLinks => _islandLinks;
        public System.Collections.Generic.List<ProcIsland.Connector> IslandConnectors => _islandConnectors;
        System.Collections.Generic.List<ProcIsland.Link> _islandLinks = new();
        System.Collections.Generic.List<ProcIsland.Connector> _islandConnectors = new();
        public System.Collections.Generic.List<ProcIsland.Route> IslandRoutes => _islandRoutes;
        System.Collections.Generic.List<ProcIsland.Route> _islandRoutes = new();
        public System.Collections.Generic.List<ProcIsland.MonumentTile> IslandTiles => _islandTiles;
        readonly System.Collections.Generic.List<ProcIsland.MonumentTile> _islandTiles = new();
        public System.Collections.Generic.List<ProcIsland.MonumentBuilding> IslandBuildings => _islandBuildings;
        readonly System.Collections.Generic.List<ProcIsland.MonumentBuilding> _islandBuildings = new();

        public System.Collections.Generic.List<ProcIsland.Poi> GenerateIsland(int seed)
        {
            var none = new System.Collections.Generic.List<ProcIsland.Poi>();
            if (_grid == null) return none;
            var pars = ProcIsland.Params.Default(seed);
            ProcIsland.Fill(_grid, _gw, _gh, pars);
            // POIs are placed AFTER the terrain exists and BEFORE the mesh is built: they read the heights to
            // choose somewhere buildable, then rewrite them to flatten their pads. Returned rather than stored
            // because the caller is what knows where they need to go next (the road/building stages read these).
            var pois = ProcIsland.PlacePois(_grid, _gw, _gh, pars);
            _islandLinks = ProcIsland.BuildLinks(pois);
            // Snap BEFORE routing: the routes start at the gates, so moving a gate afterwards would leave the
            // road pointing at where the gate used to be.
            _islandConnectors = ProcIsland.SnapConnectorsToLattice(pois, ProcIsland.BuildConnectors(pois, _islandLinks));
            _islandTiles.Clear();
            for (int i = 0; i < pois.Count; i++) _islandTiles.AddRange(ProcIsland.BuildMonument(i, pois[i], _islandConnectors));
            _islandBuildings.Clear();
            for (int i = 0; i < pois.Count; i++) _islandBuildings.AddRange(ProcIsland.PlaceBuildings(i, pois[i], _islandTiles, pars));
            // Routed and carved BEFORE RebuildAll, because carving edits the same grid the meshes are built from.
            _islandRoutes = ProcIsland.CarveRoutes(_grid, _gw, _gh, pois, _islandLinks, _islandConnectors, pars);
            RebuildAll();
            return pois;
        }

        public void SaveHeightmap(string path)   // the edited merged grid (port translator; writing the retail .heightmap tiles would clobber the install)
        {
            if (_grid == null) return;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            w.Write(_gw); w.Write(_gh);
            for (int x = 0; x < _gw; x++) for (int y = 0; y < _gh; y++) w.Write(_grid[x, y]);
            SaveHoles(HolesPathFor(path));
            SaveRivers(RiversPathFor(path));
        }

        public bool LoadHeightmap(string path)   // apply a saved sculpt over the freshly-built retail terrain (dims must match)
        {
            if (_grid == null || !System.IO.File.Exists(path)) return false;
            using var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
            if (r.ReadInt32() != _gw || r.ReadInt32() != _gh) return false;
            for (int x = 0; x < _gw; x++) for (int y = 0; y < _gh; y++) _grid[x, y] = r.ReadSingle();
            // Holes load HERE rather than at each call site, so a caller cannot load a sculpt and silently get
            // its holes filled in -- which would look like the dug map simply lost them. Same pairing as
            // SaveHeightmap -> SaveHoles. Must run BEFORE RebuildAll, since the rebuild is what decides each
            // chunk's collider from the mask.
            LoadHoles(HolesPathFor(path));
            RebuildAll();
            LoadRivers(RiversPathFor(path));   // AFTER RebuildAll: the beds read SampleHeight for their banks
            return true;
        }

        /// <summary>Dig or fill every quad under the brush. Unlike the height brushes this is NOT dt-scaled --
        /// a quad is dug or it is not, there is no partial hole, so holding the mouse over one re-applies the
        /// same state instead of deepening it.
        ///
        /// Rebuilds WITH colliders rather than deferring them to mouse-up like the sculpt brushes do. A sculpt
        /// leaves stale collision that is merely the wrong HEIGHT for a moment; a hole leaves collision where
        /// the player can now see a gap, so they walk on invisible ground until they release the button.</summary>
        public void EditHoles(float worldX, float worldZ, float radiusWorld, bool dig)
        {
            if (_grid == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;
            int cgx = Mathf.RoundToInt(cx), cgy = Mathf.RoundToInt(cy);
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            int gx0 = Mathf.Max(0, cgx - rg), gx1 = Mathf.Min(_gw - 2, cgx + rg);
            int gy0 = Mathf.Max(0, cgy - rg), gy1 = Mathf.Min(_gh - 2, cgy + rg);
            bool changed = false;
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    // Measure to the quad's CENTRE (+0.5), because the flag covers the cell, not the corner.
                    // Using the corner makes the dug region sit half a cell off from the ring the player aimed with.
                    float dx = (gx + 0.5f - cx) * UNIT, dy = (gy + 0.5f - cy) * UNIT;
                    if (dx * dx + dy * dy > radiusWorld * radiusWorld) continue;
                    if (SetHole(gx, gy, dig)) changed = true;
                }
            if (!changed) return;
            _dirty = true;
            RebuildChunksIn(gx0, gx1, gy0, gy1, withCollider: true);
        }

        public void EditFlatten(float worldX, float worldZ, float radiusWorld, float strength)   // pull heights toward the brush centre's height (Devkit FLATTEN)
        {
            if (_grid == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;
            int cgx = Mathf.Clamp(Mathf.RoundToInt(cx), 0, _gw - 1), cgy = Mathf.Clamp(Mathf.RoundToInt(cy), 0, _gh - 1);
            float target = _grid[cgx, cgy];
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            for (int gx = System.Math.Max(0, cgx - rg); gx <= System.Math.Min(_gw - 1, cgx + rg); gx++)
                for (int gy = System.Math.Max(0, cgy - rg); gy <= System.Math.Min(_gh - 1, cgy + rg); gy++)
                {
                    float dx = (gx - cx) * UNIT, dy = (gy - cy) * UNIT; float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > radiusWorld) continue;
                    float f = BrushAlpha(dist / radiusWorld);   // source linear falloff
                    JournalH(gx, gy);
                    _grid[gx, gy] = Mathf.Lerp(_grid[gx, gy], target, Mathf.Clamp(strength * f, 0f, 1f));
                }
            _dirty = true; RebuildChunksIn(cgx - rg, cgx + rg, cgy - rg, cgy + rg);
        }

        public void EditSmooth(float worldX, float worldZ, float radiusWorld, float strength)   // average each sample with its 4 neighbours (Devkit SMOOTH)
        {
            if (_grid == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;
            int cgx = Mathf.Clamp(Mathf.RoundToInt(cx), 0, _gw - 1), cgy = Mathf.Clamp(Mathf.RoundToInt(cy), 0, _gh - 1);
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            var next = new System.Collections.Generic.List<(int, int, float)>();
            for (int gx = System.Math.Max(1, cgx - rg); gx <= System.Math.Min(_gw - 2, cgx + rg); gx++)
                for (int gy = System.Math.Max(1, cgy - rg); gy <= System.Math.Min(_gh - 2, cgy + rg); gy++)
                {
                    float dx = (gx - cx) * UNIT, dy = (gy - cy) * UNIT; float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > radiusWorld) continue;
                    float f = BrushAlpha(dist / radiusWorld);   // source linear falloff
                    float avg = (_grid[gx - 1, gy] + _grid[gx + 1, gy] + _grid[gx, gy - 1] + _grid[gx, gy + 1]) * 0.25f;
                    next.Add((gx, gy, Mathf.Lerp(_grid[gx, gy], avg, Mathf.Clamp(strength * f, 0f, 1f))));
                }
            foreach (var (gx, gy, nv) in next) { JournalH(gx, gy); _grid[gx, gy] = nv; }
            _dirty = true; RebuildChunksIn(cgx - rg, cgx + rg, cgy - rg, cgy + rg);
        }

        // Devkit RAMP (source handleHeightmapWriteRamp): a linear height grade between two clicked world points, in a
        // corridor of half-width radiusWorld (falloff on the cross axis). One-shot -- begin.Y..end.Y are the target heights.
        /// <summary>A river as the user DREW it: the anchors, plus its width and depth.
        ///
        /// The recipe used to be the SAMPLED POLYLINE -- CarveRiverPath walked the bezier at grid spacing and
        /// stored every ~4 m segment, so a 400 m river became ~100 entries and the anchors the user actually
        /// placed were gone. That is why "i want it as an editable spline" could not be built: there was
        /// nothing left to edit. Dragging one of a hundred sampled points is not editing a spline, and moving
        /// it would not re-curve its neighbours.
        ///
        /// Storing anchors instead makes the polyline a DERIVED thing, regenerated from RiverPathPoints
        /// wherever it is needed -- which is the same shape as everything else here: the displacement, the
        /// paint and the water are all derived, nothing is baked.</summary>
        public sealed class River
        {
            public readonly System.Collections.Generic.List<Vector3> Anchors = new();
            public float Half = 8f, Depth = 4f;
            public int Material = 5;
        }
        readonly System.Collections.Generic.List<River> _rivers = new();
        public int RiverCount => _rivers.Count;

        /// <summary>Move one anchor and regenerate everything downstream of it.
        ///
        /// Expensive on purpose -- it re-derives the field, the paint, the water and every affected chunk --
        /// so the editor commits on mouse RELEASE and shows a cheap preview during the drag. Doing this per
        /// mouse-move would rebuild the terrain sixty times a second.</summary>
        public void MoveRiverAnchor(int river, int anchor, Vector3 to)
        {
            if (river < 0 || river >= _rivers.Count) return;
            var r = _rivers[river];
            if (anchor < 0 || anchor >= r.Anchors.Count) return;
            r.Anchors[anchor] = to;
            RegenerateRivers();
        }

        /// <summary>Delete a river outright. The ground comes back exactly, because the displacement was never
        /// baked into the heightmap -- it is regenerated from the anchors every time, so removing them removes
        /// the river.</summary>
        public void RemoveRiver(int river)
        {
            if (river < 0 || river >= _rivers.Count) return;
            _rivers.RemoveAt(river);
            RegenerateRivers();
        }

        /// <summary>Drop and rebuild every derived thing a river owns: displacement, water, splat, geometry.</summary>
        public void RegenerateRivers()
        {
            RebuildRiverField();
            RebuildRiverWater();
            foreach (var (pts, half, _) in RiverPolylines()) PaintRiverBed(pts, half, RiverMaterial);
            _dirty = true;
            RebuildAll();
        }
        // ---- SCULPT UNDO JOURNAL ----
        // Terrain brushes are held-drag and apply per FRAME, so undo has to be per STROKE or one Ctrl+Z would
        // rewind a single frame of a drag. Heightmap writes are scattered across five call sites with no single
        // choke point, so instead of a whole-grid snapshot (megabytes per step) each write site records the
        // cell's ORIGINAL value the first time that cell is touched in a stroke. Only touched cells are stored,
        // first write wins, and when no stroke is recording every one of these calls is a null check.
        System.Collections.Generic.Dictionary<(int, int), float> _strokeH;
        System.Collections.Generic.Dictionary<(int, int), bool> _strokeHole;

        public void BeginSculptStroke()
        {
            _strokeH = new System.Collections.Generic.Dictionary<(int, int), float>();
            _strokeHole = new System.Collections.Generic.Dictionary<(int, int), bool>();
        }

        /// <summary>Close the stroke and return an action that puts every touched cell back, or null when the
        /// stroke changed nothing -- the caller uses null to avoid pushing an undo step that does nothing, which
        /// is the bug where Ctrl+Z appears to be ignored because it silently consumed an empty step.</summary>
        public System.Action EndSculptStroke()
        {
            var h = _strokeH; var hole = _strokeHole;
            _strokeH = null; _strokeHole = null;
            if ((h == null || h.Count == 0) && (hole == null || hole.Count == 0)) return null;
            return () =>
            {
                if (h != null) foreach (var kv in h) _grid[kv.Key.Item1, kv.Key.Item2] = kv.Value;
                if (hole != null && _holes != null)
                {
                    foreach (var kv in hole) _holes[kv.Key.Item1, kv.Key.Item2] = kv.Value;
                    _anyHoles = false;
                    for (int x = 0; x < _gw - 1 && !_anyHoles; x++)
                        for (int y = 0; y < _gh - 1 && !_anyHoles; y++) if (_holes[x, y]) _anyHoles = true;
                }
                _dirty = true;
                RebuildAll();
                FlushColliders();
            };
        }

        void JournalH(int gx, int gy)
        {
            if (_strokeH == null) return;
            var k = (gx, gy);
            if (!_strokeH.ContainsKey(k)) _strokeH[k] = _grid[gx, gy];
        }
        void JournalHole(int gx, int gy)
        {
            if (_strokeHole == null || _holes == null) return;
            var k = (gx, gy);
            if (!_strokeHole.ContainsKey(k)) _strokeHole[k] = _holes[gx, gy];
        }

        public System.Collections.Generic.IReadOnlyList<River> Rivers => _rivers;

        /// <summary>Deep-copy every river, for undo. This is CHEAP -- a river is a handful of anchors plus
        /// three numbers, not a heightmap -- and it is cheap specifically because the displacement is derived
        /// from the anchors rather than baked into the grid (see RemoveRiver). The editor comment that called
        /// river undo "a heightmap snapshot per carve" predates that change and is no longer true.</summary>
        public System.Collections.Generic.List<River> SnapshotRivers()
        {
            var copy = new System.Collections.Generic.List<River>(_rivers.Count);
            foreach (var r in _rivers)
            {
                var c = new River { Half = r.Half, Depth = r.Depth, Material = r.Material };
                c.Anchors.AddRange(r.Anchors);   // Vector3 is a value type, so this is a real copy
                copy.Add(c);
            }
            return copy;
        }

        /// <summary>Replace every river with a snapshot and rebuild. Restores the ground exactly, for the same
        /// reason RemoveRiver does.</summary>
        public void RestoreRivers(System.Collections.Generic.List<River> snap)
        {
            if (snap == null) return;
            _rivers.Clear();
            _rivers.AddRange(snap);
            RegenerateRivers();
        }

        /// <summary>Every river's centreline, sampled. Rebuilt on demand rather than cached, because it is
        /// cheap next to what consumes it and a cache here would be a third thing to invalidate when an anchor
        /// moves.</summary>
        System.Collections.Generic.List<(System.Collections.Generic.List<Vector3> pts, float half, float depth)> RiverPolylines()
        {
            var outp = new System.Collections.Generic.List<(System.Collections.Generic.List<Vector3>, float, float)>();
            foreach (var r in _rivers)
                if (r.Anchors.Count >= 2) outp.Add((RiverPathPoints(r.Anchors), r.Half, r.Depth));
            return outp;
        }
        Node3D _riverBeds;

        /// <summary>Carve a river along a path: CUT the terrain surface out and build a riverbed under it.
        ///
        /// strawberry_cow: "cuts the terrain surface out, and creates a riverbed below the surface... actual
        /// CUT the terrain, dont morph it." So this does NOT lower the heightmap -- a morph leaves a smooth
        /// valley whose floor is still the terrain surface, which is a ditch, not a river. It marks the quads
        /// inside the channel as HOLES (the mask added for terrain holes: gone from the render mesh AND from
        /// the collider), then lays a separate bed mesh below the gap. You can fall in.
        ///
        /// The bed is its own geometry rather than more terrain because the terrain grid cannot express it:
        /// a heightmap has exactly one height per column, so a channel with banks steeper than the grid spacing
        /// is not representable at all. Cutting a hole and putting real geometry underneath is the only way to
        /// get a bank you cannot walk up.
        ///
        /// Depth is measured DOWN FROM THE LOWEST bank height along each segment, not from either endpoint --
        /// a river carved across sloping ground must stay below the surface for its whole length, and keying
        /// off one end leaves the other end floating above the hillside.</summary>
        public void CarveRiver(Vector3 begin, Vector3 end, float halfWidth, float depth)
        {
            _pendingAnchors = new System.Collections.Generic.List<Vector3> { begin, end };
            CarveRiverPolyline(new System.Collections.Generic.List<Vector3> { begin, end }, halfWidth, depth);
        }

        /// <summary>Over-carve margin. The cut is a per-quad MASK, so its boundary is stair-stepped at cell
        /// resolution and never lands on the true bank circle. strawberry_cow: "carve slightly more than we
        /// need, and then re-install terrain tris to fill the gaps perfectly." So the mask is cut WIDER than
        /// the river and the bed's own geometry is extended back out to meet intact terrain -- the apron IS
        /// the re-installed tris.</summary>
        // ZERO now that the cut tests for OVERLAP rather than centre-inside: expanding the test by
        // QuadHalfDiag already removes every quad that could hang over the channel, so an extra flat margin on
        // top only widens the apron. That matters visually -- the apron is drawn in the BED material, so every
        // metre of it is a metre of dirt collar around the river. Kept as a named constant rather than deleted
        // because "how much wider than the bank do we cut" is a real dial someone will want.
        const float RiverCutMargin = 0f;
        /// <summary>Half a quad's diagonal. A quad is a UNIT square, so this is how far its corner reaches
        /// past its own centre -- the number that decides whether a centre-based test can leave geometry
        /// hanging over the channel.</summary>
        const float QuadHalfDiag = 0.70711f * UNIT;
        /// <summary>How far past the CUT radius the apron reaches. A quad is holed when its CENTRE falls
        /// inside the cut radius, and such a quad extends up to half its diagonal (0.71 * UNIT) beyond that
        /// circle -- so geometry stopping at the cut radius would still leave a ragged gap. Overshooting into
        /// intact terrain is free because the rim sits ON the terrain surface.</summary>
        const float RiverRimOvershoot = 2f * QuadHalfDiag;
        /// <summary>How far the bed's wall top sits ABOVE the sampled terrain height.
        ///
        /// The shelf this replaces is gone: the terrain clips itself to the bank now, so there is no overlap
        /// band to hide and no depth-order contest to lose -- which is what four previous constants here were
        /// all trying and failing to manage. What is left is a sub-cell mismatch, because the terrain's clipped
        /// edge runs through quad-edge crossings while the wall runs through bed stations. Millimetres of
        /// overlap close that; a gap would show.</summary>
        const float RiverBankOverlap = 0.03f;

        /// <summary>Cut and bed a river along a whole polyline in ONE pass.
        ///
        /// Per-SEGMENT was the old shape and it could not satisfy any of strawberry's three asks at once: each
        /// segment computed its own lateral direction, so at a bend the two cross-sections met at an angle and
        /// left a notch; each measured depth from its own lowest bank, so a run downhill stepped; and each
        /// stopped at the stair-stepped mask boundary. Walking the whole path once fixes all three, because a
        /// station can see its NEIGHBOURS.</summary>
        System.Collections.Generic.List<Vector3> _pendingAnchors;   // set by the public entry points; see below

        void CarveRiverPolyline(System.Collections.Generic.IReadOnlyList<Vector3> path, float half, float depth)
        {
            if (_grid == null || path == null || path.Count < 2) return;

            // NO HOLE MASK ANY MORE. The cut used to punch whole quads out and lay a shelf over the ragged
            // boundary that left; the terrain now CLIPS ITSELF to the bank from the river field instead, so
            // what a quad contributes is decided per VERTEX and the edge lands exactly on the channel line.
            // See _riverField. The dug-hole mask still exists and is untouched -- that is a different feature.
            float reach = half + RiverShelfOuterFor(half);
            int gx0 = _gw, gx1 = -1, gy0 = _gh, gy1 = -1;
            foreach (var pt in path)
            {
                int a0 = Mathf.FloorToInt((pt.X - reach - _bx) / UNIT), a1 = Mathf.CeilToInt((pt.X + reach - _bx) / UNIT);
                int b0 = Mathf.FloorToInt((-pt.Z - reach - _bz) / UNIT), b1 = Mathf.CeilToInt((-pt.Z + reach - _bz) / UNIT);
                gx0 = System.Math.Min(gx0, a0); gx1 = System.Math.Max(gx1, a1);
                gy0 = System.Math.Min(gy0, b0); gy1 = System.Math.Max(gy1, b1);
            }
            gx0 = Mathf.Clamp(gx0, 0, _gw - 2); gx1 = Mathf.Clamp(gx1, 0, _gw - 2);
            gy0 = Mathf.Clamp(gy0, 0, _gh - 2); gy1 = Mathf.Clamp(gy1, 0, _gh - 2);
            if (gx1 < gx0 || gy1 < gy0) return;   // wholly off-map

            // The ANCHORS are the record, not the samples. `path` here is already the sampled centreline;
            // CarveRiverPath hands the anchors down separately so the recipe stays editable.
            if (_pendingAnchors != null)
            {
                var riv = new River { Half = half, Depth = depth, Material = RiverMaterial };
                riv.Anchors.AddRange(_pendingAnchors);
                _rivers.Add(riv);
                _pendingAnchors = null;
            }
            RebuildRiverField();
            PaintRiverBed(path, half, RiverMaterial);
            BuildRiverWater(path, half, depth);
            // NO BED GEOMETRY. The terrain IS the riverbed now -- it is displaced by the U profile rather than
            // cut away and replaced. Every seam this feature has had came from two surfaces meeting; there is
            // one surface now, so there is nothing to meet.
            _dirty = true;
            RebuildChunksIn(gx0, gx1, gy0, gy1, withCollider: true);
        }

        /// <summary>Turn a carve path into the stations the BED is actually built from.
        ///
        /// Two things the raw path cannot do, both of which strawberry saw as "still overhangs" on rivers that
        /// were NEW -- so my first answer, that the fix could not reach already-saved rivers, was wrong:
        ///
        ///   DENSITY. A Straight river is TWO anchors, so RiverPathPoints hands back two stations and the bed
        ///   becomes a single quad -- a PLANE between the endpoint heights. It cannot follow the contour it was
        ///   just asked to follow, and anywhere the ground rises in between, that plane sits BELOW the surface
        ///   and terrain pokes up through the bed. Which looks exactly like an overhanging quad. Resampling to
        ///   the grid pitch makes the floor track the ground for a straight river the same way it already did
        ///   for a curved one.
        ///
        ///   ENDS. The cut is a union of DISCS walked along the path, so the hole reaches a full radius PAST
        ///   each endpoint, while the bed stopped exactly AT it -- leaving an uncovered round hole at both ends
        ///   of every river. The path is extended by that radius along the end tangents so the bed covers what
        ///   the cut actually removed.</summary>
        static System.Collections.Generic.List<Vector3> BedStations(System.Collections.Generic.IReadOnlyList<Vector3> path, float holeRForEnds)
        {
            var pts = new System.Collections.Generic.List<Vector3>();
            if (path == null || path.Count < 2) return pts;

            // Extend both ends along their own tangent so the bed reaches as far as the cut did.
            Vector3 d0 = path[1] - path[0]; d0.Y = 0f;
            Vector3 d1 = path[^1] - path[^2]; d1.Y = 0f;
            Vector3 start = d0.LengthSquared() > 1e-8f ? path[0] - d0.Normalized() * holeRForEnds : path[0];
            Vector3 end = d1.LengthSquared() > 1e-8f ? path[^1] + d1.Normalized() * holeRForEnds : path[^1];

            var work = new System.Collections.Generic.List<Vector3> { start };
            work.AddRange(path);
            work.Add(end);

            // Resample to the grid pitch. Finer than a cell buys nothing -- the cut is a per-quad mask, so the
            // floor cannot express detail below UNIT anyway -- and coarser is what let the plane happen.
            pts.Add(work[0]);
            for (int i = 0; i < work.Count - 1; i++)
            {
                Vector3 a = work[i], b = work[i + 1];
                var flat = new Vector2(b.X - a.X, b.Z - a.Z);
                float len = flat.Length();
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / UNIT));
                for (int k = 1; k <= steps; k++) pts.Add(a.Lerp(b, (float)k / steps));
            }
            return pts;
        }

        /// <summary>One continuous bed for the whole path: mitred joins, contour-following floor, and an apron
        /// that lands on intact terrain.</summary>
        void BuildRiverBedPolyline(System.Collections.Generic.IReadOnlyList<Vector3> path, float half, float depth)
        {
            int n = path.Count;
            float outer = half + RiverCutMargin + RiverRimOvershoot;
            var right = new Vector3[n];

            // MITRE. A station's lateral is the bisector of the segments meeting there, not either one's own
            // normal -- that is what makes consecutive cross-sections share an edge instead of crossing at a
            // bend. strawberry: "modify each segment to smoothly connect to eachother".
            for (int i = 0; i < n; i++)
            {
                Vector3 din = i > 0 ? path[i] - path[i - 1] : path[1] - path[0];
                Vector3 dout = i < n - 1 ? path[i + 1] - path[i] : path[n - 1] - path[n - 2];
                din.Y = 0f; dout.Y = 0f;
                if (din.LengthSquared() < 1e-8f) din = dout;
                if (dout.LengthSquared() < 1e-8f) dout = din;
                if (din.LengthSquared() < 1e-8f) { right[i] = Vector3.Right; continue; }
                Vector3 bis = din.Normalized() + dout.Normalized();
                // A near-180 degree reversal makes the bisector vanish; fall back to the outgoing normal.
                Vector3 f = bis.LengthSquared() < 1e-6f ? dout.Normalized() : bis.Normalized();
                // MITRE LENGTH. An offset of d along the bisector sits d from the VERTEX but only
                // d*cos(theta/2) from each adjoining segment -- while the cut is a constant radius from the
                // centreline everywhere. Past a sharp bend the apron would therefore stop short of the hole it
                // is meant to cover. Clamped: at a hairpin the exact correction goes to infinity.
                float cosHalf = Mathf.Max(0.25f, f.Dot(dout.Normalized()));
                right[i] = new Vector3(-f.Z, 0f, f.X) / cosHalf;
            }

            // CROSS-SECTION: floor, a VERTICAL wall at the bank, then a flat shelf out to the cut edge.
            //
            // It used to be floor + a single sloped apron running from the bank up to terrain height at the
            // OUTER rim. Measured (river.overhang_probe) that is exactly the bug strawberry kept reporting:
            // the mask is correct -- no terrain survives inside the channel, closest surviving corner 8.94 m
            // against an 8 m half-width -- but terrain survives from 8.94 m OUTWARD at full height, while the
            // ramp was still climbing toward terrain height at 13.66 m. Every quad in between sat above the
            // ramp with nothing beneath it. The overhang was never uncut terrain; it was terrain my own apron
            // passed underneath.
            //
            // A vertical wall plus a flat shelf cannot reproduce that, because everything outside the bank is
            // AT terrain height -- shelf and surviving quads alike -- so there is no band for terrain to hang
            // over. It also gives the bank the original design wanted: one you cannot walk up, which a heightmap
            // cannot express and is the whole reason the bed is separate geometry.
            var fl = new Vector3[n]; var fr = new Vector3[n];   // floor edges
            var wl = new Vector3[n]; var wr = new Vector3[n];   // wall tops, at terrain height ON the bank line
            for (int i = 0; i < n; i++)
            {
                Vector3 c = path[i], r = right[i];
                float hC = SampleHeight(c.X, c.Z);
                float lx = c.X - r.X * half, lz = c.Z - r.Z * half;
                float rx = c.X + r.X * half, rz = c.Z + r.Z * half;
                float hL = SampleHeight(lx, lz), hR = SampleHeight(rx, rz);
                // Floor a FIXED depth below the local terrain, minimum across the section so a sideways slope
                // cannot leave it above the low bank.
                float floorY = Mathf.Min(hC, Mathf.Min(hL, hR)) - depth;

                fl[i] = new Vector3(lx, floorY, lz);
                fr[i] = new Vector3(rx, floorY, rz);
                wl[i] = new Vector3(lx, hL, lz);
                wr[i] = new Vector3(rx, hR, rz);

                // The wall top is lifted a hair above terrain. The terrain's clipped edge is a polyline through
                // the quad-edge crossings while the wall is a polyline through the bed stations, so the two
                // agree to well under a cell but not exactly; a few millimetres of overlap closes the slivers
                // and is invisible, where a gap would not be.
                wl[i].Y += RiverBankOverlap;
                wr[i].Y += RiverBankOverlap;
            }

            void Quad(Vector3 a, Vector3 b, Vector3 c2, Vector3 e)
            { _bedVerts.Add(a); _bedVerts.Add(b); _bedVerts.Add(c2); _bedVerts.Add(c2); _bedVerts.Add(b); _bedVerts.Add(e); }

            for (int i = 0; i < n - 1; i++)
            {
                Quad(fl[i], fr[i], fl[i + 1], fr[i + 1]);      // floor
                Quad(wl[i], fl[i], wl[i + 1], fl[i + 1]);      // left wall: vertical, floor -> the terrain's own edge
                Quad(fr[i], wr[i], fr[i + 1], wr[i + 1]);      // right wall
            }
        }

        /// <summary>Carve a river along a CURVE through the given anchors.
        ///
        /// strawberry_cow: "give it real curves. should mirror road tools, the new road tools." So it uses the
        /// same curve RoadField does -- Catmull-Rom tangents converted to cubic Bezier with the /6 factor
        /// (RoadField.RetangentRoad) -- rather than inventing a second spline convention that would drift from
        /// the roads' as either is tuned. A straight run of evenly spaced anchors comes out actually straight
        /// under this, which a naive tangent does not.
        ///
        /// The curve is then carved as many SHORT straight segments. That is not an approximation of the
        /// feature, it is how the carve works at all: the cut is a per-quad mask on a grid, so the finest
        /// channel expressible is one cell wide however smooth the source curve is. Sampling finer than the
        /// grid buys nothing but time.</summary>
        public void CarveRiverPath(System.Collections.Generic.IReadOnlyList<Vector3> anchors, float halfWidth, float depth)
        {
            if (_grid == null || anchors == null || anchors.Count < 2) return;
            // ONE polyline for the whole curve, in a single pass -- a segment that cannot see its neighbours
            // cannot line up with them. The ANCHORS ride along separately so the river stays editable.
            _pendingAnchors = new System.Collections.Generic.List<Vector3>(anchors);
            CarveRiverPolyline(RiverPathPoints(anchors), halfWidth, depth);
        }

        /// <summary>The curve itself, as a polyline -- the exact points CarveRiverPath cuts between.
        ///
        /// Public and shared with the editor's live preview ON PURPOSE. The preview's whole job is to promise
        /// where the cut will land, so it must not own a second copy of this arithmetic: two copies of a spline
        /// convention drift the moment either is tuned, and the failure mode is a preview that quietly lies.
        /// Same reason the curve is Catmull-Rom-to-Bezier with the /6 factor rather than a new convention --
        /// it matches RoadField.RetangentRoad.</summary>
        public System.Collections.Generic.List<Vector3> RiverPathPoints(System.Collections.Generic.IReadOnlyList<Vector3> anchors)
        {
            var outPts = new System.Collections.Generic.List<Vector3>();
            if (anchors == null || anchors.Count < 2) return outPts;
            outPts.Add(anchors[0]);
            if (anchors.Count == 2) { outPts.Add(anchors[1]); return outPts; }

            for (int i = 0; i < anchors.Count - 1; i++)
            {
                Vector3 p0 = anchors[i], p1 = anchors[i + 1];
                Vector3 prev = i > 0 ? anchors[i - 1] : p0;
                Vector3 next = i < anchors.Count - 2 ? anchors[i + 2] : p1;
                Vector3 t0 = (p1 - prev) / 6f, t1 = (next - p0) / 6f;   // same /6 as RoadField.RetangentRoad
                // Step count from the chord, so a long span is not carved coarser than a short one.
                int steps = Mathf.Max(2, Mathf.CeilToInt(p0.DistanceTo(p1) / Mathf.Max(2f, UNIT)));
                for (int k = 1; k <= steps; k++)
                {
                    float t = (float)k / steps;
                    float u = 1f - t;
                    outPts.Add(u * u * u * p0
                             + 3f * u * u * t * (p0 + t0)
                             + 3f * u * t * t * (p1 - t1)
                             + t * t * t * p1);
                }
            }
            return outPts;
        }

        readonly System.Collections.Generic.List<Vector3> _bedVerts = new();   // accumulated across a whole carve

        /// <summary>Append one segment's U-shaped bed to the pending buffer.
        ///
        /// APPEND, not build. This used to create a MeshInstance3D AND a StaticBody3D with its own trimesh per
        /// segment -- and a curved river is dozens of segments, so a single river cost dozens of draw calls and
        /// dozens of separate collider BVHs. They are now merged into one mesh and one body per carve
        /// (strawberry_cow 2026-08-24: "if we can save a single cpu cycle / ms of frame time / ms of loading by
        /// shoving stuff on the disk, thats a win" -- the same principle applies to not making the runtime pay
        /// for geometry that could have been merged once).</summary>
        /// <summary>Turn the accumulated bed triangles into ONE mesh and ONE collider. Called once per carve,
        /// not once per segment -- see BuildRiverBed.</summary>
        void CommitRiverBed()
        {
            if (_bedVerts.Count < 3) { _bedVerts.Clear(); return; }
            _riverBeds ??= AddOwned(new Node3D { Name = "RiverBeds" });
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var v in _bedVerts) st.AddVertex(v);
            st.GenerateNormals();
            var mesh = st.Commit();
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.26f, 0.20f), Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } };
            _riverBeds.AddChild(mi);
            var body = new StaticBody3D { CollisionLayer = 1u << 0 };
            body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Dirt);
            // Shape built straight from the TRIANGLE LIST, not from the mesh. CreateTrimeshShape() round-trips
            // through the committed surface and produced a shape that registered in the tree and collided with
            // nothing -- measured: rays through the channel hit NOTHING while the terrain either side answered
            // normally. The vertices are already a flat triangle soup, which is exactly what
            // ConcavePolygonShape3D wants, so the round trip was never buying anything.
            // BACKFACE COLLISION ON. A trimesh is ONE-SIDED to the physics engine, and the bed's quads are
            // emitted in whatever winding the cross-section walk produces -- which the RENDER hides, because
            // the bed material sets CullMode.Disabled. So the mesh looked correct and collided with nothing:
            // measured, rays down the middle of the channel hit NOTHING while the terrain either side answered
            // normally. You would have fallen through every river.
            //
            // Fixed by making the shape two-sided rather than by chasing the winding, because the winding is
            // not knowable per-quad here (a river bends both ways) and a one-sided floor is not something the
            // feature ever wants.
            body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = _bedVerts.ToArray(), BackfaceCollision = true } });
            _riverBeds.AddChild(body);
            _bedMeshes.Add(_bedVerts.ToArray());   // kept for saving: the exact geometry, not a recipe for it
            _bedVerts.Clear();
        }
        readonly System.Collections.Generic.List<Vector3[]> _bedMeshes = new();

        Node3D AddOwned(Node3D n) { AddChild(n); return n; }

        Node3D _riverWater;

        /// <summary>Rebuild every river's water from the recipe. The surface is derived like the displacement
        /// is -- nothing about it is saved -- so a load has to regenerate it or a map opens with carved
        /// channels and no water in them. Groups chained same-width segments back into runs first, because the
        /// LEVEL is per-run: feeding segments in one at a time would give each one its own surface and step
        /// them down the river.</summary>
        public void RebuildRiverWater()
        {
            if (_riverWater != null) { _riverWater.QueueFree(); _riverWater = null; }
            foreach (var (pts, half, depth) in RiverPolylines()) BuildRiverWater(pts, half, depth);
        }

        /// <summary>A translucent surface spanning bank to bank, plus arrows showing which way it flows.
        ///
        /// LEVEL IS FLAT PER RIVER, at the LOWEST bank along the run minus a little freeboard. Water that
        /// follows the bed is not water, it is wet paint -- a real surface is level, and picking the lowest
        /// bank is what guarantees it never stands above the ground somewhere along the way. The cost is that a
        /// river drawn down a hillside pools at the bottom and leaves the top dry, which is the honest result
        /// of asking for one flat surface over sloping ground; draw it in shorter runs to terrace it.</summary>
        void BuildRiverWater(System.Collections.Generic.IReadOnlyList<Vector3> path, float half, float depth)
        {
            if (path == null || path.Count < 2) return;
            _riverWater ??= AddOwned(new Node3D { Name = "RiverWater" });

            var pts = BedStations(path, holeRForEnds: 0f);
            if (pts.Count < 2) return;

            // THE SURFACE FOLLOWS THE BED at a fixed height above it, and is then SMOOTHED along the run.
            //
            // strawberry: "make the water level blend smoothly along the river. and it should stay a fixed
            // height above the riverbed, even it results in a nonsensical river design." That overrules the
            // flat level I shipped, deliberately and with the consequence stated -- water running uphill is
            // wrong hydrology and right for a game where a river should look like a river everywhere along it
            // rather than pooling at one end and leaving the rest dry.
            //
            // The offset is a fraction of DEPTH, not a constant, so a shallow stream is not brim-full and a
            // deep one is not a puddle at the bottom. 0.5 against a bank that sits 0.65 * depth above the bed
            // leaves real freeboard at every station.
            var level = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                level[i] = SurfaceHeightWorld(pts[i].X, pts[i].Z) + depth * 0.5f;

            // SMOOTHING, and it is not cosmetic. The bed follows terrain that has its own noise, so a surface
            // pinned to it inherits every bump as a ripple in what should read as still water. Three passes of
            // neighbour averaging, ENDS PINNED so the run does not shrink away from where it meets the ground.
            var tmp = new float[level.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                System.Array.Copy(level, tmp, level.Length);
                for (int i = 1; i < level.Length - 1; i++)
                    level[i] = (tmp[i - 1] + tmp[i] * 2f + tmp[i + 1]) * 0.25f;
            }

            var verts = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            float run = 0f;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector3 c0 = pts[i], c1 = pts[i + 1];
                Vector3 f0 = c1 - c0; f0.Y = 0f;
                if (f0.LengthSquared() < 1e-8f) continue;
                float seg = f0.Length();
                f0 = f0.Normalized();
                var r0 = new Vector3(-f0.Z, 0f, f0.X);
                // Slightly INSIDE the bank, so the surface meets the bed under water rather than clipping
                // through the bank face where the two are within centimetres of each other.
                float w = half * 0.97f;
                float y0 = level[i], y1 = level[i + 1];
                Vector3 l0 = new Vector3(c0.X - r0.X * w, y0, c0.Z - r0.Z * w);
                Vector3 rr0 = new Vector3(c0.X + r0.X * w, y0, c0.Z + r0.Z * w);
                Vector3 l1 = new Vector3(c1.X - r0.X * w, y1, c1.Z - r0.Z * w);
                Vector3 rr1 = new Vector3(c1.X + r0.X * w, y1, c1.Z + r0.Z * w);
                float v0 = run / (half * 2f), v1 = (run + seg) / (half * 2f);
                verts.Add(l0); verts.Add(rr0); verts.Add(l1);
                uvs.Add(new Vector2(0f, v0)); uvs.Add(new Vector2(1f, v0)); uvs.Add(new Vector2(0f, v1));
                verts.Add(l1); verts.Add(rr0); verts.Add(rr1);
                uvs.Add(new Vector2(0f, v1)); uvs.Add(new Vector2(1f, v0)); uvs.Add(new Vector2(1f, v1));
                run += seg;
            }
            if (verts.Count < 3) return;

            var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            arr[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.22f, 0.42f, 0.55f, 0.62f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Metallic = 0.2f, Roughness = 0.12f,
            };
            _riverWater.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });

            BuildFlowArrows(pts, level, half);
        }

        /// <summary>Flow-direction arrows on the surface. Editor-facing: which way a river runs is a property
        /// you set by drawing it, and until it is drawn on the water there is nothing in the scene that says
        /// it. Spaced by river WIDTH rather than a fixed distance, so a wide river does not get a dense line of
        /// them and a narrow one does not get none.</summary>
        void BuildFlowArrows(System.Collections.Generic.IReadOnlyList<Vector3> pts, float[] level, float half)
        {
            float spacing = Mathf.Max(12f, half * 4f);
            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.85f, 0.95f, 1f, 0.75f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            float acc = spacing;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector3 c0 = pts[i], c1 = pts[i + 1];
                Vector3 f = c1 - c0; f.Y = 0f;
                float seg = f.Length();
                if (seg < 1e-4f) continue;
                f = f.Normalized();
                acc += seg;
                if (acc < spacing) continue;
                acc = 0f;
                var r = new Vector3(-f.Z, 0f, f.X);
                float aLen = Mathf.Min(half * 0.8f, 6f), aWide = aLen * 0.45f;
                Vector3 mid = new Vector3(c0.X, level[i] + 0.06f, c0.Z);   // rides the surface, just above it or it z-fights
                var tri = new System.Collections.Generic.List<Vector3>
                {
                    mid + f * aLen,                    // tip, pointing DOWNSTREAM
                    mid - f * aLen * 0.4f + r * aWide,
                    mid - f * aLen * 0.4f - r * aWide,
                };
                var aarr = new Godot.Collections.Array(); aarr.Resize((int)Mesh.ArrayType.Max);
                aarr[(int)Mesh.ArrayType.Vertex] = tri.ToArray();
                var am = new ArrayMesh(); am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, aarr);
                _riverWater.AddChild(new MeshInstance3D { Mesh = am, MaterialOverride = mat });
            }
        }

        // RIVER FIELD: signed distance from the channel, per GRID VERTEX. >0 outside, <0 inside, metres.
        //
        // strawberry, after four attempts at hiding the seam: "why arent you modify-blending the terrain tris
        // INTO the apron or something". Right, and it is the whole approach that was wrong. Cutting a hole and
        // laying separate geometry over it can only ever produce an overlap band, and every fix I shipped was a
        // way of making that band less visible -- sink it 2 cm, lift it 6 cm, widen it, make it vertical.
        //
        // With a field the terrain mesh CLIPS ITSELF to the bank: boundary quads emit the polygon outside the
        // channel with their edge vertices moved onto the f=0 curve. No overlap, no depth-order to lose, the
        // terrain's own material and splat colours right up to the water, and the visible edge is the smooth
        // bank instead of a 4 m staircase.
        //
        // Derived from the anchors rather than saved: the recipe already persists, so a load rebuilds this and
        // there is no third file to keep in step with the other two.
        // RIVER CUT DEPTH, per grid vertex, in metres to subtract from the surface.
        //
        // strawberry, after a seam that would not die: "we should have the river bed become a smoother U
        // shape, and the banks either side be smooth ramps." That request removes the constraint the whole
        // previous design existed for. The bed was separate geometry because of "actual CUT the terrain, dont
        // morph it" -- a heightmap has one height per column and cannot express a bank you can't walk up.
        // A smooth ramp IS expressible, so the river becomes a displacement of the terrain itself.
        //
        // Which makes the seam impossible rather than fixed. There is no hole, no clipped boundary, no bed
        // mesh and no second surface to line up: one continuous terrain, one set of vertices. Every previous
        // attempt here -- stair-step margins, an apron, a vertical wall, a lifted shelf, self-clipping tris --
        // was managing a seam between two surfaces that now do not both exist.
        //
        // Stored as DISPLACEMENT rather than distance so overlapping rivers compose by max (the deepest wins)
        // and so a re-carve is idempotent: this is rebuilt from the recipe, never accumulated into _grid.
        float[,] _riverCut;

        /// <summary>Depth profile across the channel. u = 0 at the centreline, 1 at the bank.
        ///
        /// (1-u^2)^2 rather than a simpler (1-u^2): both reach zero depth at the bank, but this one also has
        /// zero SLOPE there, so the bed meets the surrounding ground tangentially instead of at a crease. That
        /// is what makes the bank read as a ramp rather than a lip, and it is the same reason the floor comes
        /// out as a smooth U instead of a trough with corners.</summary>
        static float RiverProfile(float u)
        {
            if (u >= 1f) return 0f;
            float k = 1f - u * u;
            return k * k;
        }

        /// <summary>How far past the bank the river keeps affecting the ground, as a multiple of half-width.
        /// strawberry: "i just want it to have an effect on the surrounding terrain as WELL". Without this the
        /// ground is untouched right up to the bank and then drops, so a river reads as something dropped ONTO
        /// the landscape rather than something the landscape grew around.</summary>
        const float RiverBlendFactor = 2.5f;
        /// <summary>How deep the dish is where it meets the bank, as a fraction of the channel depth. The
        /// approach slopes gently down toward the water and the channel proper does the rest.</summary>
        const float RiverBlendDrop = 0.35f;

        /// <summary>The APPROACH, outside the bank: a shallow dish easing the surrounding ground toward the
        /// river. u runs 0 at the bank to 1 at the outer edge of the influence.
        ///
        /// Same (1-u^2)^2 shape and for the same reason as the channel: zero value AND zero slope at its outer
        /// end, so the influence dies out into untouched terrain with no ring or crease marking where it
        /// stopped. Inverted here -- deepest at the bank, nothing at the rim.</summary>
        static float RiverBlendProfile(float u)
        {
            if (u >= 1f) return 0f;
            float k = 1f - u * u;
            return k * k;
        }

        void RebuildRiverField()
        {
            _riverCut = null;
            if (_grid == null || _rivers.Count == 0) return;
            var f = new float[_gw, _gh];

            foreach (var (pts, half, depth) in RiverPolylines())
            for (int pi = 0; pi < pts.Count - 1; pi++)
            {
                Vector3 a = pts[pi], b = pts[pi + 1];
                float blend = half * RiverBlendFactor;
                float reach = blend;
                float minX = Mathf.Min(a.X, b.X) - reach, maxX = Mathf.Max(a.X, b.X) + reach;
                float minZ = Mathf.Min(a.Z, b.Z) - reach, maxZ = Mathf.Max(a.Z, b.Z) + reach;
                int gx0 = Mathf.Max(0, Mathf.FloorToInt((minX - _bx) / UNIT));
                int gx1 = Mathf.Min(_gw - 1, Mathf.CeilToInt((maxX - _bx) / UNIT));
                int gy0 = Mathf.Max(0, Mathf.FloorToInt((-maxZ - _bz) / UNIT));
                int gy1 = Mathf.Min(_gh - 1, Mathf.CeilToInt((-minZ - _bz) / UNIT));
                var A = new Vector2(a.X, a.Z); var B = new Vector2(b.X, b.Z);
                var ab = B - A; float abLen2 = Mathf.Max(1e-6f, ab.LengthSquared());
                for (int gx = gx0; gx <= gx1; gx++)
                    for (int gy = gy0; gy <= gy1; gy++)
                    {
                        var p = new Vector2(_bx + gx * UNIT, -(_bz + gy * UNIT));
                        float t = Mathf.Clamp((p - A).Dot(ab) / abLen2, 0f, 1f);
                        float d = (p - (A + ab * t)).Length();
                        // Two zones. INSIDE the bank it is the channel itself; OUTSIDE it is the approach,
                        // starting exactly where the channel ends so the two meet at one height with no step.
                        float cut;
                        float rim = depth * RiverBlendDrop;   // how far the ground has already dropped by the bank
                        if (d <= half)
                        {
                            // The channel is dug from the DISHED surface, and `depth` stays the total depth of
                            // the river rather than becoming depth-plus-dish. Interpolating between rim at the
                            // bank and depth at the centreline is what keeps the number the user typed meaning
                            // what they typed -- the first cut of this added the two and a depth-4 river came
                            // out 5.40 deep, which the probe caught immediately.
                            cut = rim + (depth - rim) * RiverProfile(d / Mathf.Max(0.001f, half));
                        }
                        else
                        {
                            float v = (d - half) / Mathf.Max(0.001f, blend - half);
                            cut = rim * RiverBlendProfile(v);
                        }
                        if (cut > f[gx, gy]) f[gx, gy] = cut;   // overlapping rivers: the deepest wins
                    }
            }
            _riverCut = f;
        }

        /// <summary>Metres to drop this grid vertex by. Applied where the surface is BUILT -- the render mesh,
        /// the collider and SampleHeight -- rather than baked into _grid, so a river is idempotent, removable,
        /// and never compounds when the recipe is replayed.</summary>
        float RiverCutAt(int gx, int gy) =>
            _riverCut == null ? 0f : _riverCut[Mathf.Clamp(gx, 0, _gw - 1), Mathf.Clamp(gy, 0, _gh - 1)];

        /// <summary>The same displacement, evaluated ANALYTICALLY at any world point rather than read off the
        /// grid.
        ///
        /// strawberry: "its a smooth round bottom rather than a hard U shape". The profile was never the
        /// problem -- measured, a half-width-8 river on a 4 m grid gets TWO samples between centreline and
        /// bank, so the mesh can only draw two straight segments and the roundness has nowhere to live. Error
        /// against the true curve peaks at 0.25 m mid-slope, which is exactly the crease you can see.
        ///
        /// So river quads get SUBDIVIDED and their heights come from here, off the real curve, instead of
        /// being interpolated between grid corners.</summary>
        public float RiverCutWorld(float worldX, float worldZ)
        {
            if (_rivers.Count == 0) return 0f;
            var p = new Vector2(worldX, worldZ);
            float best = 0f;
            foreach (var (pts, half, depth) in RiverPolylines())
            for (int pi = 0; pi < pts.Count - 1; pi++)
            {
                Vector3 a = pts[pi], b = pts[pi + 1];
                var A = new Vector2(a.X, a.Z); var B = new Vector2(b.X, b.Z);
                var ab = B - A;
                float t = Mathf.Clamp((p - A).Dot(ab) / Mathf.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
                float d = (p - (A + ab * t)).Length();
                float blend = half * RiverBlendFactor;
                if (d >= blend) continue;
                float rim = depth * RiverBlendDrop;
                float cut = d <= half
                    ? rim + (depth - rim) * RiverProfile(d / Mathf.Max(0.001f, half))
                    : rim * RiverBlendProfile((d - half) / Mathf.Max(0.001f, blend - half));
                if (cut > best) best = cut;
            }
            return best;
        }

        /// <summary>Sub-quads per grid quad inside a river. 4 puts a vertex every metre on a 4 m grid, which
        /// takes the worst-case error against the true curve from 0.25 m to under 0.02 m -- and it is applied
        /// ONLY to quads the river touches, so the rest of the map keeps its cheap geometry.</summary>
        const int RiverSubdiv = 4;

        /// <summary>Base terrain height at a world point, WITHOUT the river -- bilinear across the same grid
        /// corners the mesh uses, so a subdivided patch lands exactly on the untouched surface at its edges
        /// and there is no step where subdivision starts.</summary>
        float BaseHeightWorld(float worldX, float worldZ)
        {
            float fx = (worldX - _bx) / UNIT, fy = (-worldZ - _bz) / UNIT;
            int xi = Mathf.FloorToInt(fx), yi = Mathf.FloorToInt(fy);
            float tx = fx - xi, ty = fy - yi;
            int x0 = Mathf.Clamp(xi, 0, _gw - 1), x1 = Mathf.Clamp(xi + 1, 0, _gw - 1);
            int y0 = Mathf.Clamp(yi, 0, _gh - 1), y1 = Mathf.Clamp(yi + 1, 0, _gh - 1);
            float h0 = Mathf.Lerp(_grid[x0, y0], _grid[x1, y0], tx);
            float h1 = Mathf.Lerp(_grid[x0, y1], _grid[x1, y1], tx);
            return Mathf.Lerp(h0, h1, ty) * TILE_HEIGHT - TILE_HEIGHT / 2f;
        }

        /// <summary>Surface height at a world point: base terrain minus the analytic river. The one expression
        /// the render mesh, the collider and SampleHeight all go through, so they cannot disagree about where
        /// the ground is.</summary>
        public float SurfaceHeightWorld(float worldX, float worldZ) =>
            BaseHeightWorld(worldX, worldZ) - RiverCutWorld(worldX, worldZ);

        /// <summary>Does this chunk carry any river displacement? The heightfield collider path hands Jolt
        /// _grid DIRECTLY and so cannot see the cut -- such a chunk would collide with the un-carved surface
        /// while rendering the carved one, i.e. you would walk on the water. Those chunks take the trimesh
        /// path, same as dug holes.</summary>
        bool QuadHasRiver(int gx, int gy) =>
            _riverCut != null && (RiverCutAt(gx, gy) > 0f || RiverCutAt(gx + 1, gy) > 0f
                               || RiverCutAt(gx, gy + 1) > 0f || RiverCutAt(gx + 1, gy + 1) > 0f);

        bool ChunkHasRiver(int cxi, int cyi)
        {
            if (_riverCut == null) return false;
            int x0 = cxi * CHUNK, y0 = cyi * CHUNK;
            int x1 = System.Math.Min(x0 + CHUNK, _gw - 1), y1 = System.Math.Min(y0 + CHUNK, _gh - 1);
            for (int gx = x0; gx <= x1; gx++)
                for (int gy = y0; gy <= y1; gy++)
                    if (RiverCutAt(gx, gy) > 0f) return true;
            return false;
        }

        /// <summary>Sampled segments across every river -- what the old recipe stored one-for-one. Kept as a
        /// number because tests and the editor panel report it.</summary>
        public int RiverSegmentCount
        {
            get { int n = 0; foreach (var r in _rivers) if (r.Anchors.Count >= 2) n += RiverPathPoints(r.Anchors).Count - 1; return n; }
        }

        /// <summary>Splat layer painted into the bed, and how far past the bank the overspray reaches as a
        /// fraction of the blend zone. Layer 5 = Sand by the table on PaintSplat.</summary>
        public int RiverMaterial = 5;
        const float RiverOversprayFrac = 0.6f;

        /// <summary>Paint the bed, and fade it out over the bank.
        ///
        /// The splat shader is WINNER-TAKE-ALL -- one layer at 1.0 is the material, there is no blend weight to
        /// ramp -- so a soft edge cannot be painted as a gradient. It is DITHERED instead: past the bank each
        /// texel is painted with a probability that falls to zero at the overspray limit, which reads as spray
        /// at any distance you actually look at terrain from. A hard circle would read as a decal.
        ///
        /// Deterministic on position, not random: a repaint of the same river must produce the same speckle,
        /// or every rebuild reshuffles the bank.</summary>
        public void PaintRiverBed(System.Collections.Generic.IReadOnlyList<Vector3> path, float half, int layer)
        {
            if (_dom == null || path == null || path.Count < 2) return;
            float outer = half * (1f + (RiverBlendFactor - 1f) * RiverOversprayFrac);
            for (int e = 0; e < path.Count - 1; e++)
            {
                Vector3 a = path[e], b = path[e + 1];
                var d = new Vector2(b.X - a.X, b.Z - a.Z);
                float len = d.Length();
                if (len < 0.001f) continue;
                d /= len;
                for (float t = 0f; t <= len; t += UNIT * 0.5f)
                {
                    float wx = a.X + d.X * t, wz = a.Z + d.Y * t;
                    int cgx = Mathf.RoundToInt((wx - _bx) / UNIT), cgy = Mathf.RoundToInt((-wz - _bz) / UNIT);
                    int rg = Mathf.CeilToInt(outer / UNIT) + 1;
                    for (int gx = System.Math.Max(0, cgx - rg); gx <= System.Math.Min(_dw - 1, cgx + rg); gx++)
                        for (int gy = System.Math.Max(0, cgy - rg); gy <= System.Math.Min(_dh - 1, cgy + rg); gy++)
                        {
                            float px = _bx + gx * UNIT, pz = -(_bz + gy * UNIT);
                            float dist = new Vector2(px - wx, pz - wz).Length();
                            if (dist > outer) continue;
                            if (dist > half)
                            {
                                float k = 1f - (dist - half) / Mathf.Max(0.001f, outer - half);   // 1 at the bank -> 0 at the limit
                                // Hash on the TEXEL, so the same river repaints to the same speckle.
                                uint h = (uint)(gx * 73856093) ^ (uint)(gy * 19349663);
                                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                                if ((h & 0xFFFF) / 65535f > k * k) continue;
                            }
                            PaintTexel(gx, gy, layer);
                        }
                }
            }
            UpdateSplat(_s0Tex, _s0Img); UpdateSplat(_s1Tex, _s1Img);   // guarded: an EMPTY splat image (a map with one splat) used to hit RenderingServer's "p_image is empty" error
        }

        void PaintTexel(int gx, int gy, int layer)
        {
            var c0 = new Color(layer == 0 ? 1 : 0, layer == 1 ? 1 : 0, layer == 2 ? 1 : 0, layer == 3 ? 1 : 0);
            var c1 = new Color(layer == 4 ? 1 : 0, layer == 5 ? 1 : 0, layer == 6 ? 1 : 0, layer == 7 ? 1 : 0);
            _dom[gx, gy] = (byte)layer;
            _s0Img.SetPixel(gx, gy, c0); _s1Img.SetPixel(gx, gy, c1);
        }

        /// <summary>How far the bed's flat shelf reaches from the centreline for a given half-width. Exposed so
        /// a probe can compare it against the cut it is meant to cover rather than re-deriving the constants
        /// and agreeing with itself.</summary>
        public static float RiverShelfOuterFor(float half) => half + RiverCutMargin + RiverRimOvershoot;

        /// <summary>Highest bed vertex within `radius` of a world XZ point, or float.MinValue if the bed has
        /// none there. Exposed for the overhang probe: "does the cut reach far enough" and "is the bed AT
        /// terrain height out here" are different questions, and only the second one catches a bed that passes
        /// UNDERNEATH surviving terrain -- which is the bug that shipped twice.</summary>
        public float BedTopNear(float worldX, float worldZ, float radius)
        {
            float best = float.MinValue;
            float r2 = radius * radius;
            foreach (var verts in _bedMeshes)
                foreach (var v in verts)
                {
                    float dx = v.X - worldX, dz = v.Z - worldZ;
                    if (dx * dx + dz * dz <= r2 && v.Y > best) best = v.Y;
                }
            return best;
        }

        /// <summary>Re-cut and re-bed every river from its stored RECIPE, discarding the baked geometry.
        ///
        /// Needed because the save format bakes the bed verts and the hole mask rides in its own file, so
        /// LoadRivers replays a river EXACTLY as it was carved -- which is the point (no bezier walk, no
        /// SampleHeight scan on load) and also means a fix to the carve cannot reach a river that already
        /// exists. strawberry hit this immediately: "still overhangs", on a river carved before the fix.
        ///
        /// Consecutive segments that chain end-to-end with the same width and depth are regrouped into one
        /// polyline first, because that is what the carve needs to re-mitre the joins -- feeding the segments
        /// back in one at a time would rebuild the notches the polyline pass exists to remove.
        ///
        /// Returns the number of rivers (polylines) rebuilt.</summary>
        public int RebuildRiversFromRecipe()
        {
            if (_grid == null || _rivers.Count == 0) return 0;
            // Everything downstream of the anchors is derived, so a "rebuild" is just: drop the derived state
            // and regenerate it. There is no geometry to free and no recipe to reconstruct any more -- which
            // is the whole point of storing anchors rather than samples.
            RebuildRiverField();
            RebuildRiverWater();
            foreach (var (pts, half, _) in RiverPolylines()) PaintRiverBed(pts, half, RiverMaterial);
            _dirty = true;
            RebuildAll();
            int rebuilt = _rivers.Count;
            GD.Print($"[river] rebuilt {rebuilt} river(s) from their anchors");
            return rebuilt;
        }

        public void EditRamp(Vector3 begin, Vector3 end, float radiusWorld)
        {
            if (_grid == null) return;
            var rampOffset = new Vector2(end.X - begin.X, end.Z - begin.Z);
            float rampMag = rampOffset.Length();
            if (rampMag < 1f) return;
            var rampDir = rampOffset / rampMag;
            var rampCross = new Vector2(-rampDir.Y, rampDir.X);
            float beginH = (begin.Y + TILE_HEIGHT / 2f) / TILE_HEIGHT, endH = (end.Y + TILE_HEIGHT / 2f) / TILE_HEIGHT;
            float minX = Mathf.Min(begin.X, end.X) - radiusWorld, maxX = Mathf.Max(begin.X, end.X) + radiusWorld;
            float minZ = Mathf.Min(begin.Z, end.Z) - radiusWorld, maxZ = Mathf.Max(begin.Z, end.Z) + radiusWorld;
            int gx0 = Mathf.Clamp(Mathf.FloorToInt((minX - _bx) / UNIT), 0, _gw - 1), gx1 = Mathf.Clamp(Mathf.CeilToInt((maxX - _bx) / UNIT), 0, _gw - 1);
            int gy0 = Mathf.Clamp(Mathf.FloorToInt((-maxZ - _bz) / UNIT), 0, _gh - 1), gy1 = Mathf.Clamp(Mathf.CeilToInt((-minZ - _bz) / UNIT), 0, _gh - 1);
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    float wx = _bx + gx * UNIT, wz = -(_bz + gy * UNIT);
                    var wo = new Vector2(wx - begin.X, wz - begin.Z);
                    float wMag = wo.Length();
                    if (wMag < 0.001f) { JournalH(gx, gy); _grid[gx, gy] = Mathf.Clamp(Mathf.Lerp(_grid[gx, gy], beginH, 1f), 0f, 1f); continue; }
                    var wDir = wo / wMag;
                    float alongAlign = wDir.Dot(rampDir);
                    if (alongAlign < 0f) continue;                                   // behind the ramp begin
                    float alongDist = wMag * alongAlign / rampMag;
                    if (alongDist > 1f) continue;                                    // past the ramp end
                    float crossDist = Mathf.Abs(wMag * wDir.Dot(rampCross) / radiusWorld);
                    if (crossDist > 1f) continue;                                    // outside the corridor
                    float alpha = BrushAlpha(crossDist);
                    float target = Mathf.Lerp(beginH, endH, alongDist);
                    JournalH(gx, gy);
                    _grid[gx, gy] = Mathf.Clamp(Mathf.Lerp(_grid[gx, gy], target, alpha), 0f, 1f);
                }
            _dirty = true; RebuildChunksIn(gx0, gx1, gy0, gy1); FlushColliders();
        }

        readonly System.Collections.Generic.HashSet<(int, int)> _dirtyChunks = new();   // chunks whose collider went stale mid-stroke (flushed on mouse-up)

        // Rebuild ONE chunk's mesh (+ optional trimesh collider) from the (global) _grid. Reads neighbour cells for edge normals.
        public void RebuildChunk(int cxi, int cyi, bool withCollider = true)
        {
            if (_grid == null || _chunkMi == null || cxi < 0 || cyi < 0 || cxi >= _chunksX || cyi >= _chunksY) return;
            int x0 = cxi * CHUNK, y0 = cyi * CHUNK;
            int x1 = System.Math.Min(x0 + CHUNK, _gw - 1), y1 = System.Math.Min(y0 + CHUNK, _gh - 1);
            int nx = x1 - x0 + 1, ny = y1 - y0 + 1;
            if (nx < 2 || ny < 2) return;
            int nv = nx * ny;
            var verts = new Vector3[nv]; var norms = new Vector3[nv]; var uvs = new Vector2[nv]; var cols = new Color[nv];
            for (int lx = 0; lx < nx; lx++)
                for (int ly = 0; ly < ny; ly++)
                {
                    int gx = x0 + lx, gy = y0 + ly; int i = lx * ny + ly;
                    verts[i] = new Vector3(_bx + gx * UNIT, _grid[gx, gy] * TILE_HEIGHT - TILE_HEIGHT / 2f - RiverCutAt(gx, gy), -(_bz + gy * UNIT));
                    uvs[i] = new Vector2(gx / (float)(_gw - 1), gy / (float)(_gh - 1));
                    cols[i] = _dom != null ? LayerColor(_dom[System.Math.Min(gx, _dw - 1), System.Math.Min(gy, _dh - 1)]) : new Color(0.34f, 0.42f, 0.26f);
                    float hl = _grid[System.Math.Max(0, gx - 1), gy], hr = _grid[System.Math.Min(_gw - 1, gx + 1), gy];
                    float hd = _grid[gx, System.Math.Max(0, gy - 1)], hu = _grid[gx, System.Math.Min(_gh - 1, gy + 1)];
                    norms[i] = new Vector3(-(hr - hl) * TILE_HEIGHT, 2f * UNIT, (hu - hd) * TILE_HEIGHT).Normalized();
                }
            // Subdivided river quads need vertices that are not grid corners, so the buffers grow.
            var vList = new System.Collections.Generic.List<Vector3>(verts);
            var nList = new System.Collections.Generic.List<Vector3>(norms);
            var uList = new System.Collections.Generic.List<Vector2>(uvs);
            var cList = new System.Collections.Generic.List<Color>(cols);
            var iList = new System.Collections.Generic.List<int>((nx - 1) * (ny - 1) * 6);

            for (int lx = 0; lx < nx - 1; lx++)
                for (int ly = 0; ly < ny - 1; ly++)
                {
                    int qgx = x0 + lx, qgy = y0 + ly;
                    // A hole is a quad that emits no triangles. The VERTS stay in the buffer -- dropping them
                    // would renumber every index after them, and the neighbouring quads still reference these
                    // corners. Unreferenced verts cost a few bytes and nothing else.
                    if (_anyHoles && IsHole(qgx, qgy)) continue;
                    int i00 = lx * ny + ly, i10 = (lx + 1) * ny + ly, i01 = lx * ny + (ly + 1), i11 = (lx + 1) * ny + (ly + 1);

                    if (!QuadHasRiver(qgx, qgy))
                    {
                        iList.Add(i00); iList.Add(i01); iList.Add(i10);
                        iList.Add(i10); iList.Add(i01); iList.Add(i11);
                        continue;
                    }

                    // SUBDIVIDED, and every sub-vertex takes its height from the analytic curve. The corners
                    // land on exactly the same points the neighbouring un-subdivided quads use, so there is no
                    // crack where subdivision starts -- the shared edge is sampled from the same function.
                    const int S = RiverSubdiv;
                    int b0 = vList.Count;
                    for (int sy = 0; sy <= S; sy++)
                        for (int sx = 0; sx <= S; sx++)
                        {
                            float gxf = qgx + sx / (float)S, gyf = qgy + sy / (float)S;
                            float wx = _bx + gxf * UNIT, wz = -(_bz + gyf * UNIT);
                            vList.Add(new Vector3(wx, SurfaceHeightWorld(wx, wz), wz));
                            uList.Add(new Vector2(gxf / (_gw - 1), gyf / (_gh - 1)));
                            float fx2 = sx / (float)S, fy2 = sy / (float)S;
                            cList.Add(cols[i00].Lerp(cols[i10], fx2).Lerp(cols[i01].Lerp(cols[i11], fx2), fy2));
                            // Normal from the surface itself by central difference, so the shading follows the
                            // carved bed rather than the flat grid it was cut out of.
                            const float e = 0.5f;
                            float hl = SurfaceHeightWorld(wx - e, wz), hr = SurfaceHeightWorld(wx + e, wz);
                            float hd = SurfaceHeightWorld(wx, wz - e), hu = SurfaceHeightWorld(wx, wz + e);
                            nList.Add(new Vector3(hl - hr, 2f * e, hd - hu).Normalized());
                        }
                    for (int sy = 0; sy < S; sy++)
                        for (int sx = 0; sx < S; sx++)
                        {
                            int a0 = b0 + sy * (S + 1) + sx, a1 = a0 + 1;
                            int a2 = a0 + (S + 1), a3 = a2 + 1;
                            iList.Add(a0); iList.Add(a2); iList.Add(a1);
                            iList.Add(a1); iList.Add(a2); iList.Add(a3);
                        }
                }

            verts = vList.ToArray(); norms = nList.ToArray(); uvs = uList.ToArray(); cols = cList.ToArray();
            var idx = iList.ToArray(); int t = idx.Length;

            if (t == 0)   // every quad in this chunk is dug: no surface at all
            {
                var empty = _chunkMi[cxi, cyi];
                if (empty != null) empty.Mesh = null;
                if (_withCollider && withCollider && _chunkBody != null && _chunkBody[cxi, cyi] != null)
                    foreach (var c in _chunkBody[cxi, cyi].GetChildren()) if (c is CollisionShape3D ecs) ecs.Shape = null;
                return;
            }
            var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts; arr[(int)Mesh.ArrayType.Normal] = norms;
            arr[(int)Mesh.ArrayType.TexUV] = uvs; arr[(int)Mesh.ArrayType.Color] = cols; arr[(int)Mesh.ArrayType.Index] = idx;
            var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            var mi = _chunkMi[cxi, cyi];
            if (mi == null) { mi = new MeshInstance3D { MaterialOverride = _terrMat }; _chunkMi[cxi, cyi] = mi; AddChild(mi); }
            mi.Mesh = mesh;
            if (_withCollider && withCollider)
            {
                var body = _chunkBody[cxi, cyi];
                if (body == null) { body = new StaticBody3D { CollisionLayer = 1u << 0 }; body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Grass); body.AddToGroup("terrain"); body.AddChild(new CollisionShape3D()); _chunkBody[cxi, cyi] = body; AddChild(body); }
                foreach (var c in body.GetChildren()) if (c is CollisionShape3D cs) ApplyChunkShape(cs, cxi, cyi);
            }
        }

        // The terrain IS a regular height grid, so a trimesh threw away the one property that makes it cheap: it made
        // Jolt build a BVH over ~4600 loose triangles PER CHUNK (484 chunks on PEI). HeightMapShape3D hands Jolt the
        // heights directly and needs no BVH -- measured on PEI, the collider build went ~5340ms -> ~117ms (~45x), and
        // 78 MB of triangle soup became 4.6 MB of floats. Both paths were verified to produce identical geometry
        // (terrain.collider_matches_sampled_height, which probes chunk seams and phantom walls too).
        //
        // Two indexing traps, both of which produce a collider that LOOKS placed but is mirrored or transposed --
        // it renders correctly and drops the player through the world, and NOTHING else in the suite notices:
        //   1. HeightMapShape3D is ROW-MAJOR IN Z: data[z * width + x]. The mesh verts above are X-MAJOR
        //      (i = lx * ny + ly). Copying the vert order across transposes the chunk.
        //   2. This port NEGATES Z (vert z = -(_bz + gy * UNIT)), so world Z DECREASES as the grid index gy rises.
        //      The shape's local +Z must therefore walk gy DOWNWARD: gy = y1 - hz.
        // Both are covered by the test -- it was confirmed to FAIL against each bug injected deliberately.
        //
        // Sample spacing is fixed at 1 unit by the shape, so the node carries scale (UNIT, 1, UNIT) -- Jolt requires
        // X and Z scale to match on a heightfield, which they do. Heights are already absolute world Y, so the body
        // stays at Y=0 and only X/Z get centred.
        void ApplyChunkShape(CollisionShape3D cs, int cxi, int cyi)
        {
            int x0 = cxi * CHUNK, y0 = cyi * CHUNK;
            int x1 = System.Math.Min(x0 + CHUNK, _gw - 1), y1 = System.Math.Min(y0 + CHUNK, _gh - 1);
            int nx = x1 - x0 + 1, ny = y1 - y0 + 1;
            if (nx < 2 || ny < 2) return;

            // HOLES FORCE A TRIMESH, AND ONLY FOR THE CHUNKS THAT HAVE THEM.
            //
            // Godot's HeightMapShape3D is a dense field of heights -- there is no "absent" sample, so a hole
            // cannot be expressed in it at all. Unity's TerrainData supports holes natively, which is why retail
            // gets this for free and we do not.
            //
            // The heightfield is here for a measured reason (see the comment below): on PEI the collider build
            // was ~5340ms as trimesh vs ~117ms as heightfield, and 78 MB of triangle soup vs 4.6 MB of floats.
            // So a blanket switch to trimesh would hand back a 45x regression to buy a feature most chunks do
            // not use. Per-chunk keeps that: a chunk with a hole pays trimesh, every other chunk keeps the fast
            // path, and holes are rare and local by nature.
            // A heightfield cannot express a hole OR a clipped bank -- one height per column, no absent
            // samples, no partial quads -- so any chunk carrying either swaps to a trimesh.
            if (ChunkHasHole(cxi, cyi) || ChunkHasRiver(cxi, cyi)) { ApplyChunkTrimesh(cs, cxi, cyi, x0, y0, x1, y1); return; }

            var data = new float[nx * ny];
            for (int hz = 0; hz < ny; hz++)
            {
                int gy = y1 - hz;   // trap 2: local +Z walks the grid backwards
                for (int hx = 0; hx < nx; hx++)
                    data[hz * nx + hx] = _grid[x0 + hx, gy] * TILE_HEIGHT - TILE_HEIGHT / 2f;   // same height expression as the mesh verts
            }
            cs.Shape = new HeightMapShape3D { MapWidth = nx, MapDepth = ny, MapData = data };
            cs.Scale = new Vector3(UNIT, 1f, UNIT);
            cs.Position = new Vector3(_bx + x0 * UNIT + UNIT * (nx - 1) / 2f, 0f, -_bz - y1 * UNIT + UNIT * (ny - 1) / 2f);
        }

        /// <summary>The holed-chunk collider: the same surface as the render mesh, minus the dug quads.
        ///
        /// This MUST be built from the same height expression and the same quad set the mesh uses, or the player
        /// collides with something they cannot see. The two indexing traps that apply to the heightfield path do
        /// NOT apply here -- a trimesh carries absolute vertex positions, so there is no row-major-vs-x-major
        /// order to transpose and no negated-Z walk to invert. That is worth stating because the natural instinct
        /// is to copy the `gy = y1 - hz` line across, and doing so here would mirror the collider.</summary>
        void ApplyChunkTrimesh(CollisionShape3D cs, int cxi, int cyi, int x0, int y0, int x1, int y1)
        {
            var tris = new System.Collections.Generic.List<Vector3>();
            for (int gx = x0; gx < x1; gx++)
                for (int gy = y0; gy < y1; gy++)
                {
                    if (IsHole(gx, gy)) continue;
                    if (QuadHasRiver(gx, gy))
                    {
                        // SUBDIVIDED EXACTLY LIKE THE RENDER MESH, off the same SurfaceHeightWorld. Getting
                        // this wrong is ground you can see but not stand on -- and the coarse version would be
                        // wrong by up to a quarter of a metre mid-slope, which is a visible hover.
                        const int S = RiverSubdiv;
                        var sub = new Vector3[(S + 1) * (S + 1)];
                        for (int sy = 0; sy <= S; sy++)
                            for (int sx = 0; sx <= S; sx++)
                            {
                                float gxf = gx + sx / (float)S, gyf = gy + sy / (float)S;
                                float wx = _bx + gxf * UNIT, wz = -(_bz + gyf * UNIT);
                                sub[sy * (S + 1) + sx] = new Vector3(wx, SurfaceHeightWorld(wx, wz), wz);
                            }
                        for (int sy = 0; sy < S; sy++)
                            for (int sx = 0; sx < S; sx++)
                            {
                                Vector3 s00 = sub[sy * (S + 1) + sx], s10 = sub[sy * (S + 1) + sx + 1];
                                Vector3 s01 = sub[(sy + 1) * (S + 1) + sx], s11 = sub[(sy + 1) * (S + 1) + sx + 1];
                                tris.Add(s00); tris.Add(s01); tris.Add(s10);
                                tris.Add(s10); tris.Add(s01); tris.Add(s11);
                            }
                        continue;
                    }
                    Vector3 V(int ax, int ay) => new Vector3(
                        _bx + ax * UNIT,
                        _grid[ax, ay] * TILE_HEIGHT - TILE_HEIGHT / 2f - RiverCutAt(ax, ay),   // identical to the mesh vert expression, river included
                        -(_bz + ay * UNIT));
                    Vector3 v00 = V(gx, gy), v10 = V(gx + 1, gy), v01 = V(gx, gy + 1), v11 = V(gx + 1, gy + 1);
                    // Same winding + same diagonal as the render mesh (i00,i01,i10 / i10,i01,i11), so the
                    // collision surface matches the visible one on the split too, not just at the corners.
                    tris.Add(v00); tris.Add(v01); tris.Add(v10);
                    tris.Add(v10); tris.Add(v01); tris.Add(v11);
                }
            if (tris.Count == 0) { cs.Shape = null; return; }
            // BackfaceCollision so a body shoved past this ONE-SIDED river/hole surface (e.g. a heavy vehicle
            // depenetrating the player downward) is caught by the back face and pushed back out instead of
            // tunnelling into the -1030 void. The heightfield path is already solid both ways; this trimesh
            // fallback was the one genuinely THIN patch the "terrain collision is very thin" report bit on. It
            // stays a surface (holes are cut out as absent quads), so two-siding it traps nothing. (master 2026-08-31)
            cs.Shape = new ConcavePolygonShape3D { Data = tris.ToArray(), BackfaceCollision = true };
            // Absolute world positions above -> the shape must NOT carry the heightfield path's scale/offset.
            cs.Scale = Vector3.One;
            cs.Position = Vector3.Zero;
        }

        // Rebuild every chunk overlapping a grid cell range (a brush edit) -- 1-chunk margin so shared edges/normals update.
        // withCollider=false (default for strokes): MESH ONLY (fast) + mark the chunk dirty; FlushColliders rebuilds the heavy trimesh on mouse-up.
        void RebuildChunksIn(int gx0, int gx1, int gy0, int gy1, bool withCollider = false)
        {
            int cx0 = System.Math.Max(0, gx0 / CHUNK - 1), cx1 = System.Math.Min(_chunksX - 1, gx1 / CHUNK);
            int cy0 = System.Math.Max(0, gy0 / CHUNK - 1), cy1 = System.Math.Min(_chunksY - 1, gy1 / CHUNK);
            for (int cx = cx0; cx <= cx1; cx++) for (int cy = cy0; cy <= cy1; cy++) { RebuildChunk(cx, cy, withCollider); if (!withCollider) _dirtyChunks.Add((cx, cy)); }
        }

        public void RebuildAll() { if (_chunkMi != null) for (int cx = 0; cx < _chunksX; cx++) for (int cy = 0; cy < _chunksY; cy++) RebuildChunk(cx, cy, true); }   // full build (mesh + collider)

        /// <summary>World-space XZ extent the grid covers. Z is negated relative to the grid, so grid y=0 is maxZ.</summary>
        public (float MinX, float MaxX, float MinZ, float MaxZ) WorldBoundsXZ()
            => _grid == null ? (0f, 0f, 0f, 0f) : (_bx, _bx + (_gw - 1) * UNIT, -(_bz + (_gh - 1) * UNIT), -_bz);

        public void FlushColliders()   // stroke end (mouse-up): rebuild colliders only for the chunks the drag touched
        {
            if (_withCollider)
                foreach (var (cx, cy) in _dirtyChunks)
                {
                    var body = _chunkBody[cx, cy];
                    if (body != null)
                        foreach (var c in body.GetChildren()) if (c is CollisionShape3D cs) ApplyChunkShape(cs, cx, cy);
                }
            _dirtyChunks.Clear();
        }
        public float SampleHeight(float worldX, float worldZ)
        {
            if (_grid == null) return 0f;
            // bilinear across the 4 surrounding grid verts so callers (roads etc.) follow the SMOOTH terrain
            // instead of a nearest-neighbour stepped height -- that RoundToInt stepping WAS the road's jagged edges.
            float fx = (worldX - _bx) / UNIT;
            float fy = (-worldZ - _bz) / UNIT;   // world Z is negated
            int xi = Mathf.FloorToInt(fx), yi = Mathf.FloorToInt(fy);
            float tx = fx - xi, ty = fy - yi;
            int x0 = Mathf.Clamp(xi, 0, _gw - 1), x1 = Mathf.Clamp(xi + 1, 0, _gw - 1);
            int y0 = Mathf.Clamp(yi, 0, _gh - 1), y1 = Mathf.Clamp(yi + 1, 0, _gh - 1);
            float h0 = Mathf.Lerp(_grid[x0, y0], _grid[x1, y0], tx);
            float h1 = Mathf.Lerp(_grid[x0, y1], _grid[x1, y1], tx);
            // ...minus the river, bilinear through the same corners. Everything that follows the ground -- road
            // splines, prop placement, the editor's own preview -- reads this, and a river the surface knows
            // about but SampleHeight does not is a river props stand over in mid-air.
            // The river comes off the ANALYTIC curve, not off interpolated grid samples -- the mesh is
            // subdivided inside a river, so a SampleHeight that read the coarse grid would disagree with the
            // ground the player is standing on by up to a quarter of a metre mid-slope.
            return Mathf.Lerp(h0, h1, ty) * TILE_HEIGHT - TILE_HEIGHT / 2f - RiverCutWorld(worldX, worldZ);
        }
        // dominant splatmap layer at a world point (2=grass, 0/7=forest, 1=sand, 3=road, 4=rock, 5=water, 6=dirt); 255 = no splats
        public byte SampleDominantLayer(float worldX, float worldZ)
        {
            if (_dom == null) return 255;
            int gx = Mathf.Clamp(Mathf.RoundToInt((worldX - _bx) / UNIT), 0, _dw - 1);
            int gy = Mathf.Clamp(Mathf.RoundToInt((-worldZ - _bz) / UNIT), 0, _dh - 1);
            return _dom[gx, gy];
        }
        public static bool IsWater(byte layer) => layer == 5;   // splat layer 5 = ocean; every other layer is drivable land

        public static Terrain Active;   // most-recently-built terrain -> bullet impacts sample the ground material off its splatmap

        // Water surface (the global ocean plane). Retail models water as WaterVolume boxes; PEI's ocean is one
        // box whose surface sits at seaLevel*256 world-Y (source: LevelLighting legacy water). The port has a
        // single global plane, so submersion = point.y < SeaLevelY and the swim surface elevation IS SeaLevelY.
        // HasWater is false under UG_NOWATER (no plane built) so swimming disables cleanly.
        //
        // water-splash arrived carrying its own `WaterLevelY` with a -inf sentinel for "no water" -- the same
        // idea under a second name. Folded into this pair rather than kept alongside it: two notions of where
        // the water is will drift, and the one that drifts is whichever nobody happens to be looking at.
        /// <summary>ImageTexture.Update with an empty/null image is a RenderingServer error (texture_storage.cpp _texture_2d_update
        /// "p_image.is_null() || p_image->is_empty()") -- 4 of them on every PEI load (strawberry 2026-09-03 "fix those errors").</summary>
        static void UpdateSplat(ImageTexture tex, Image img)
        {
            if (tex == null || img == null || img.IsEmpty() || img.GetWidth() == 0 || img.GetHeight() == 0) return;
            tex.Update(img);
        }
        public static float SeaLevelY = 25.6f;   // = 0.1(PEI seaLevel) * 256; overwritten per-build
        public static bool HasWater;
        /// <summary>Is this world point below the ocean surface? (the port's WaterUtility.isPointUnderwater).</summary>
        public static bool IsPointUnderwater(float worldY) => HasWater && worldY < SeaLevelY;
        /// <summary>Visual water-surface world-Y at a world point = flat sea level + the WaveField swell (the CPU twin of
        /// water.gdshader). Buoyancy / bobbing samples THIS so floaters ride the same waves the shader draws. Gameplay
        /// submersion still keys off the flat SeaLevelY above (a wave slopping over your head shouldn't drown you).</summary>
        public static float WaterSurfaceY(Vector3 p) => SeaLevelY + (HasWater ? WaveField.Height(p.X, p.Z) : 0f);

        // Ocean surface as a subdivided grid that OMITS cells buried under land (master 2026-08-24: "kill the water plane
        // effects under the terrain"). A cell is kept only if a corner sits below sea level, so the swell VERTEX shader
        // runs on visible water instead of ~half its verts on hidden seabed. Local space (centered, Y=0; the MeshInstance
        // lifts it to sea level). Matches PlaneMesh's UV [0,1] + up normal + density so water.gdshader is unchanged.
        static ArrayMesh BuildOceanMesh(Terrain terr, float wsx, float wsz, int subX, int subZ, float cx, float cz, float seaY)
        {
            int nx = subX + 1, nz = subZ + 1;
            var verts = new Vector3[nx * nz];
            var uvs = new Vector2[nx * nz];
            var norms = new Vector3[nx * nz];
            var wet = new bool[nx * nz];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    float lx = -wsx * 0.5f + i * (wsx / subX);
                    float lz = -wsz * 0.5f + j * (wsz / subZ);
                    int vi = j * nx + i;
                    verts[vi] = new Vector3(lx, 0f, lz);
                    uvs[vi] = new Vector2((float)i / subX, (float)j / subZ);
                    norms[vi] = Vector3.Up;
                    wet[vi] = terr.SampleHeight(cx + lx, cz + lz) < seaY;   // corner below sea level = water/shore here
                }
            var idx = new System.Collections.Generic.List<int>();
            for (int j = 0; j < subZ; j++)
                for (int i = 0; i < subX; i++)
                {
                    int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                    if (wet[a] || wet[b] || wet[c] || wet[d])   // ANY corner wet -> keep the cell (covers the shoreline)
                    { idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d); }
                }
            var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts;
            arr[(int)Mesh.ArrayType.Normal] = norms;
            arr[(int)Mesh.ArrayType.TexUV] = uvs;
            arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
            var m = new ArrayMesh();
            if (idx.Count >= 3) m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            GD.Print($"[terrain] ocean masked: {idx.Count / 3}/{subX * subZ * 2} tris kept (buried-under-land cells dropped)");
            return m;
        }
        public const float MinFishDepth = 4f;   // retail UseableFisher minimumDepth: a bobber needs >=4m of water below the surface
        // The bullet-impact surface material at a world point, from the dominant splat layer (so shooting sand kicks up sand,
        // road/rock = concrete chips, dirt = dirt, grass/forest = foliage -- instead of one flat guess for the whole island).
        public PlayerController.Surf SurfAt(float worldX, float worldZ) => SampleDominantLayer(worldX, worldZ) switch
        {
            1 => PlayerController.Surf.Sand,      // PEI_Sand
            3 => PlayerController.Surf.Concrete,  // road network
            4 => PlayerController.Surf.Concrete,  // rock / cliff
            5 => PlayerController.Surf.Sand,      // underwater seabed (sand)
            6 => PlayerController.Surf.Dirt,      // dirt
            _ => PlayerController.Surf.Grass,     // 2 grass, 0/7 forest, 255 none
        };

        // Build one landscape tile's mesh (+ optional trimesh collider) from its .heightmap file, placed at its coord.
        public static Node3D LoadTile(string heightmapPath, int coordX, int coordY, bool withCollider = true)
        {
            byte[] data = File.ReadAllBytes(heightmapPath);
            var h = new float[RES, RES];
            int i = 0;
            for (int x = 0; x < RES; x++)
                for (int y = 0; y < RES; y++)
                {
                    ushort raw = (ushort)((data[i] << 8) | data[i + 1]); i += 2;   // big-endian, source SHA1Stream.ReadByte pairs
                    h[x, y] = raw / (float)ushort.MaxValue;
                }

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int x = 0; x < RES; x++)
                for (int y = 0; y < RES; y++)
                {
                    st.SetUV(new Vector2(x / (float)(RES - 1), y / (float)(RES - 1)));
                    st.AddVertex(new Vector3(coordX * TILE_SIZE + y * UNIT, h[x, y] * TILE_HEIGHT - TILE_HEIGHT / 2f, -(coordY * TILE_SIZE + x * UNIT)));   // y-index = world X, x-index = world Z
                }
            for (int x = 0; x < RES - 1; x++)
                for (int y = 0; y < RES - 1; y++)
                {
                    int i00 = x * RES + y, i10 = (x + 1) * RES + y, i01 = x * RES + (y + 1), i11 = (x + 1) * RES + (y + 1);
                    st.AddIndex(i00); st.AddIndex(i01); st.AddIndex(i10);   // winding reversed to compensate the Z-flip
                    st.AddIndex(i10); st.AddIndex(i01); st.AddIndex(i11);
                }
            st.GenerateNormals();
            var mesh = st.Commit();

            var node = new Node3D { Name = $"Tile_{coordX}_{coordY}" };
            node.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.28f), Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
            if (withCollider)
            {
                var body = new StaticBody3D { CollisionLayer = 1u << 0 };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Grass);   // bullet impacts on the ground kick up grass/dirt
                body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
                node.AddChild(body);
            }
            return node;
        }

        // NEW MAP: a flat, all-grass, sculptable terrain (no heightmap file). Same chunked mesh + splat material as a
        // loaded map, so every editor tool (sculpt/ramp/paint) works on it. tiles = size in 1024-unit Landscape tiles.
        public static Terrain CreateFlat(int tilesX = 3, int tilesZ = 3, bool withCollider = true)
        {
            var terr = new Terrain { Name = "Terrain" };
            Active = terr;
            int GW = tilesX * 256 + 1, GH = tilesZ * 256 + 1, GWs = tilesX * SRES, GHs = tilesZ * SRES;
            float flat = (30f + TILE_HEIGHT / 2f) / TILE_HEIGHT;   // flat land ~Y30 (above the 25.6 sea level)
            var g = new float[GW, GH];
            for (int x = 0; x < GW; x++) for (int y = 0; y < GH; y++) g[x, y] = flat;
            var dom = new byte[GWs, GHs];
            for (int x = 0; x < GWs; x++) for (int y = 0; y < GHs; y++) dom[x, y] = 2;   // layer 2 = grass
            var sbuf0 = new byte[GWs * GHs * 4]; var sbuf1 = new byte[GWs * GHs * 4];
            for (int i = 0; i < GWs * GHs; i++) sbuf0[i * 4 + 2] = 255;   // splat0 B channel = layer 2 (grass) weight 1
            var splat0Img = Image.CreateFromData(GWs, GHs, false, Image.Format.Rgba8, sbuf0);
            var splat1Img = Image.CreateFromData(GWs, GHs, false, Image.Format.Rgba8, sbuf1);
            var s0t = ImageTexture.CreateFromImage(splat0Img); var s1t = ImageTexture.CreateFromImage(splat1Img);
            var texMat = BuildTerrainMaterial(s0t, s1t);
            // NOTE: deliberately does NOT touch the HasWater / SeaLevelY statics. CreateFlat is a library call
            // the tests use directly, and mutating global water state here would leak a sea into every later
            // test in the boot -- which is the exact failure that had five unrelated tests red earlier today
            // (a leaked container, same shape of bug). The new-map flow sets water where it belongs, in
            // Main.BuildEditorNew.
            terr._grid = g; terr._gw = GW; terr._gh = GH; terr._bx = 0f; terr._bz = 0f;
            terr._dom = dom; terr._dw = GWs; terr._dh = GHs;
            terr._s0Img = splat0Img; terr._s1Img = splat1Img; terr._s0Tex = s0t; terr._s1Tex = s1t;
            terr._terrMat = texMat != null ? (Material)texMat : new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f };
            terr._withCollider = withCollider;
            terr._chunksX = (GW - 2) / CHUNK + 1; terr._chunksY = (GH - 2) / CHUNK + 1;
            terr._chunkMi = new MeshInstance3D[terr._chunksX, terr._chunksY];
            terr._chunkBody = new StaticBody3D[terr._chunksX, terr._chunksY];
            terr.RebuildAll();
            GD.Print($"[terrain] flat NEW map {tilesX}x{tilesZ} tiles ({GW}x{GH} verts)");
            return terr;
        }

        // Load every Tile_*_Source.heightmap in a map's Landscape/Heightmaps folder into one Terrain node (the whole island).
        public static Terrain LoadMap(string heightmapsDir, bool withCollider = true)
        {
            var t = new Terrain { Name = "Terrain" };
            Active = t;
            foreach (var path in Directory.GetFiles(heightmapsDir, "Tile_*_Source.heightmap"))
            {
                // "Tile_<cx>_<cy>_Source.heightmap"
                string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out int cx) && int.TryParse(parts[2], out int cy))
                    t.AddChild(LoadTile(path, cx, cy, withCollider));
            }
            return t;
        }

        // Whole-map terrain as ONE SEAMLESS mesh: stitch all tiles into a global (tw*256+1)x(th*256+1) height grid so
        // adjacent tiles SHARE their edge vertices (no per-tile-mesh seams), then one ArrayMesh via bulk arrays (fast --
        // SurfaceTool per-vertex would be far too slow at ~1M verts) with heightfield-gradient normals.
        public static Terrain LoadMapMerged(string heightmapsDir, bool withCollider = true)
        {
            if (!Directory.Exists(heightmapsDir))   // real map terrain is read live from a local Unturned install (not shipped in-repo)
            {
                GD.PrintErr($"[map] Unturned map terrain not found at '{heightmapsDir}'. Install Unturned via Steam, or set the UG_UNTURNED_DIR env var to your Unturned folder if it's in a non-default location.");
                return null;
            }
            // UG_TERRAIN_PROF=1 breaks the load down by phase. Off by default (it is noise in a normal boot), but the
            // [loadprof] line only reports Terrain as ONE number, so this is the way in when that number moves.
            // Each _phase() call records the time since the PREVIOUS call -- so it belongs AFTER the work it names.
            bool _prof = System.Environment.GetEnvironmentVariable("UG_TERRAIN_PROF") == "1";
            var _sw = System.Diagnostics.Stopwatch.StartNew(); long _last = 0;
            void _phase(string n) { long ms = _sw.ElapsedMilliseconds; if (_prof) GD.Print($"[terrain-prof] {n,-22} {ms - _last,6} ms"); _last = ms; }
            var tiles = new System.Collections.Generic.Dictionary<(int, int), float[,]>();
            var splats = new System.Collections.Generic.Dictionary<(int, int), byte[,]>();   // dominant splatmap layer per 256x256 texel
            var splatRaw = new System.Collections.Generic.Dictionary<(int, int), byte[]>();   // raw 256x256x8 layer weights per tile, for the blend shader
            string splatDir = Path.Combine(Path.GetDirectoryName(heightmapsDir), "Splatmaps");
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var path in Directory.GetFiles(heightmapsDir, "Tile_*_Source.heightmap"))
            {
                string[] p = Path.GetFileNameWithoutExtension(path).Split('_');
                if (p.Length < 3 || !int.TryParse(p[1], out int cx) || !int.TryParse(p[2], out int cy)) continue;
                byte[] d = File.ReadAllBytes(path);
                var hh = new float[RES, RES]; int k = 0;
                for (int x = 0; x < RES; x++) for (int y = 0; y < RES; y++) { hh[x, y] = (ushort)((d[k] << 8) | d[k + 1]) / (float)ushort.MaxValue; k += 2; }
                tiles[(cx, cy)] = hh;
                minX = System.Math.Min(minX, cx); minY = System.Math.Min(minY, cy); maxX = System.Math.Max(maxX, cx); maxY = System.Math.Max(maxY, cy);
                // matching splatmap -> the winning (dominant) layer per texel (256x256x8 bytes, weight = raw/255; source readSplatmap)
                string sp = Path.Combine(splatDir, $"Tile_{cx}_{cy}_Source.splatmap");
                if (File.Exists(sp))
                {
                    byte[] sd = File.ReadAllBytes(sp); var dm = new byte[SRES, SRES]; int sk = 0;
                    for (int sx = 0; sx < SRES; sx++) for (int sy = 0; sy < SRES; sy++) { byte bl = 0, bv = 0; for (byte L = 0; L < SLAYERS; L++) { byte w = sd[sk++]; if (w > bv) { bv = w; bl = L; } } dm[sx, sy] = bl; }
                    splats[(cx, cy)] = dm; splatRaw[(cx, cy)] = sd;
                }
            }
            _phase("read+decode tiles");
            var terr = new Terrain { Name = "Terrain" };
            Active = terr;
            if (tiles.Count == 0) return terr;

            int GW = (maxX - minX + 1) * 256 + 1, GH = (maxY - minY + 1) * 256 + 1;
            var g = new float[GW, GH];
            foreach (var kv in tiles)
            {
                int ox = (kv.Key.Item1 - minX) * 256, oy = (kv.Key.Item2 - minY) * 256;
                for (int x = 0; x < RES; x++) for (int y = 0; y < RES; y++) g[ox + y, oy + x] = kv.Value[x, y];   // heightmap y-index = world X, x-index = world Z (verified: adjacent tiles' edges only match swapped) -> shared edges coincide, seamless
            }

            _phase("merge heightmaps");
            int GWs = (maxX - minX + 1) * SRES, GHs = (maxY - minY + 1) * SRES;   // global splatmap grid (256/tile, no shared edge)
            var dom = new byte[GWs, GHs];
            foreach (var kv in splats)
            {
                int ox = (kv.Key.Item1 - minX) * SRES, oy = (kv.Key.Item2 - minY) * SRES;
                for (int sx = 0; sx < SRES; sx++) for (int sy = 0; sy < SRES; sy++) dom[ox + sy, oy + sx] = kv.Value[sx, sy];   // same y->worldX, x->worldZ transpose
            }

            // bake the 8 raw layer weights into 2 RGBA8 textures (splat0 = layers 0-3, splat1 = 4-7) for the blend shader
            _phase("merge dominant");
            byte[] sbuf0 = new byte[GWs * GHs * 4], sbuf1 = new byte[GWs * GHs * 4];
            foreach (var kv in splatRaw)
            {
                int ox = (kv.Key.Item1 - minX) * SRES, oy = (kv.Key.Item2 - minY) * SRES; byte[] sd = kv.Value;
                for (int sx = 0; sx < SRES; sx++) for (int sy = 0; sy < SRES; sy++)
                {
                    int di = ((oy + sx) * GWs + (ox + sy)) * 4, b = (sx * SRES + sy) * SLAYERS;   // merged pos, same y->X/x->Z transpose as dom
                    sbuf0[di] = sd[b]; sbuf0[di + 1] = sd[b + 1]; sbuf0[di + 2] = sd[b + 2]; sbuf0[di + 3] = sd[b + 3];
                    sbuf1[di] = sd[b + 4]; sbuf1[di + 1] = sd[b + 5]; sbuf1[di + 2] = sd[b + 6]; sbuf1[di + 3] = sd[b + 7];
                }
            }
            _phase("bake splat buffers");
            var splat0Img = Image.CreateFromData(GWs, GHs, false, Image.Format.Rgba8, sbuf0);
            var splat1Img = Image.CreateFromData(GWs, GHs, false, Image.Format.Rgba8, sbuf1);

            float baseX = minX * TILE_SIZE, baseZ = minY * TILE_SIZE;
            ImageTexture s0t = splats.Count > 0 ? ImageTexture.CreateFromImage(splat0Img) : null;
            ImageTexture s1t = splats.Count > 0 ? ImageTexture.CreateFromImage(splat1Img) : null;
            var texMat = splats.Count > 0 ? BuildTerrainMaterial(s0t, s1t) : null;   // real per-layer albedos, blended by per-texel splat weights
            GD.Print(texMat != null ? "[TERRAIN] weight-blended albedo shader ACTIVE" : "[TERRAIN] vertex-colour fallback");
            _phase("images+textures");
            terr._grid = g; terr._gw = GW; terr._gh = GH; terr._bx = baseX; terr._bz = baseZ;   // SampleHeight (spawns) + chunk sculpt
            terr._dom = dom; terr._dw = GWs; terr._dh = GHs;   // SampleDominantLayer + chunk vertex colours
            terr._s0Img = splat0Img; terr._s1Img = splat1Img; terr._s0Tex = s0t; terr._s1Tex = s1t;   // live splat paint
            terr._terrMat = texMat != null ? (Material)texMat : new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f };
            terr._withCollider = withCollider;
            // CHUNKED mesh: one MeshInstance per chunk so a sculpt stroke rebuilds ONLY the touched chunks (smooth held-drag).
            terr._chunksX = (GW - 2) / CHUNK + 1; terr._chunksY = (GH - 2) / CHUNK + 1;
            terr._chunkMi = new MeshInstance3D[terr._chunksX, terr._chunksY];
            terr._chunkBody = new StaticBody3D[terr._chunksX, terr._chunksY];
            _phase("setup");
            terr.RebuildAll();   // builds every chunk's mesh + collider from _grid
            _phase($"RebuildAll ({terr._chunksX}x{terr._chunksY} chunks, collider={withCollider})");

            // translucent ocean surface at the map's REAL sea level (source: Environment/Lighting.dat seaLevel float @+18; PEI v12 = 0.1)
            // UG_NOWATER=1 skips the water plane -> see a map's raw terrain/textures from above (esp. flat custom maps below sea level)
            HasWater = System.Environment.GetEnvironmentVariable("UG_NOWATER") != "1";
            if (HasWater)
            {
                // Per-map sea level: read the map's OWN seaLevel (0..1) from Environment/Lighting.dat instead of
                // assuming PEI's 0.1 -- Washington and other maps float their ocean at a different height. maproot =
                // heightmapsDir up two (Heightmaps -> Landscape -> map). PEI's 0.1 stays the fallback when the file's
                // absent (no local install) or the float reads out of the valid 0..1 range.
                float seaLevel01 = 0.1f;
                try
                {
                    string ldat = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(heightmapsDir)), "Environment", "Lighting.dat");
                    if (File.Exists(ldat))
                    {
                        byte[] lb = File.ReadAllBytes(ldat);
                        if (lb.Length >= 22)
                        {
                            float sv = System.BitConverter.ToSingle(lb, 18);   // version(1)+azimuth/bias/fade/time(16)+moon(1) -> seaLevel@18
                            if (float.IsFinite(sv) && sv >= 0f && sv <= 1f) seaLevel01 = sv;
                        }
                    }
                }
                catch { /* keep PEI 0.1 */ }
                // seaLevel 1.0 = the "no legacy ocean" sentinel: the map's water is PER-VOLUME (lakes/rivers via
                // WaterVolumes -- e.g. Yukon's Kluane Lake -- not a global sea; its Config has no Use_Legacy_Water).
                // A global plane at 1.0*256 = 256 floods the ENTIRE map -> the player swims everywhere (master, Yukon
                // 2026-08-12). Skip the plane + leave HasWater false so submersion is off. TODO: model the WaterVolumes.
                if (seaLevel01 >= 0.99f)
                {
                    HasWater = false;
                    GD.Print($"[terrain] seaLevel {seaLevel01:F3} = no legacy ocean -> global water plane SKIPPED (map water is per-volume)");
                }
                else
                {
                float waterY = seaLevel01 * 256f;   // Unturned water surface = seaLevel * Level.TERRAIN(256), Use_Legacy_Water path
                SeaLevelY = waterY;                 // swim submersion (PlayerController water state) + explosion splashes
                GD.Print($"[terrain] sea level {seaLevel01:F3} -> world-Y {waterY:F1}");
                float wsx = (maxX - minX + 1) * TILE_SIZE + 400f, wsdz = (maxY - minY + 1) * TILE_SIZE + 400f;
                // subdivide so the vertex-displaced waves have geometry to move (~5 m quads); capped for perf on huge maps
                int subX = Mathf.Clamp((int)(wsx / 4f), 64, 600), subZ = Mathf.Clamp((int)(wsdz / 4f), 64, 600);   // ~4 m quads; per-pixel normal in the shader hides the rest of the facets
                float wcx = baseX + GW * UNIT * 0.5f, wcz = -(baseZ + GH * UNIT * 0.5f);
                // Mask the plane to WET cells only (master): the full grid spanned the whole map incl UNDER the land,
                // displacing swell on ~half its verts for buried water. One mesh still = one draw call.
                var water = new MeshInstance3D { Mesh = BuildOceanMesh(terr, wsx, wsdz, subX, subZ, wcx, wcz, waterY) };
                water.Position = new Vector3(wcx, waterY, wcz);
                // waves + crest foam (on the peaks) + depth-based shore foam at every coastline (master 2026-08-16)
                water.MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/water.gdshader") };
                water.Layers = WaterReflection.WaterLayer;   // keep the ocean OUT of its own mirror pass (self-occlusion)
                terr.AddChild(water);
                // PLANAR REFLECTION (WaterReflection.cs): a mirror-camera SubViewport feeds the shader's reflection_tex.
                // Opt-in via UG_REFLECT=1 for now so on/off frametime is a clean A/B; flip to default-on once proven.
                if (System.Environment.GetEnvironmentVariable("UG_REFLECT") == "1")
                {
                    var refl = new WaterReflection();
                    terr.AddChild(refl);
                    refl.Setup((ShaderMaterial)water.MaterialOverride, waterY, new Vector2I(1024, 1024));
                }
                // Bullets-only splash collider on a dedicated layer (bit9): the bullet raycast checks it, but player/
                // vehicles don't mask bit9 so it never blocks movement/swimming. Shooting the ocean -> Water_Static splash.
                var wbody = new StaticBody3D { CollisionLayer = 1u << 9, Position = water.Position };
                wbody.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Water);
                var wsize = new Vector2(wsx, wsdz);   // plane dims (the visual mesh is now a masked ArrayMesh, not a PlaneMesh); splash box stays the full flat sea
                wbody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(wsize.X, 0.2f, wsize.Y) } });
                terr.AddChild(wbody);
                }
            }
            _phase("water plane");
            if (_prof) GD.Print($"[terrain-prof] TOTAL {_sw.ElapsedMilliseconds} ms");
            return terr;   // (grid/dom/material/chunks all stored above, before RebuildAll)
        }
    }
}
