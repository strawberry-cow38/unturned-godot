// Wet hard-surface shader for rain: up-facing faces darken + go glossy as `rain_wetness` rises, and animated splash
// ripple rings play on them (raindrop impacts). Driven by the global uniform `rain_wetness` (WeatherManager sets it;
// the --raintest harness sets it directly). Master 2026-08-29: raindrop impact splashes + hard-surface wetness.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx, cull_disabled;

global uniform float rain_wetness;                       // 0 dry .. 1 soaked (surface darken/gloss)
global uniform float rain_intensity;                     // 0 .. 1 -- drives the splash-impact density + brightness to MATCH the falling rain
global uniform sampler2D rain_roof;                      // RainRoofMap (see rain_streak.gdshader): roofed ground stays dry
global uniform vec4 rain_roof_rect;
uniform sampler2D albedo_tex : source_color, filter_nearest_mipmap, repeat_enable;   // ROADS (master 2026-09-04 "not seeing the ripple impacts on the road"): the textured surface variant
uniform bool use_tex = false;
uniform sampler2D albedo_tex_nomip : source_color, filter_nearest, repeat_enable;   // PALETTE props (WetSurface): nearest, no mipmaps, like their StandardMaterial3D
uniform bool use_nomip = false;
uniform bool use_vcol = false;                           // VertexColorUseAsAlbedo (billboards bake their ad colours into vertex colour)
uniform vec4 tint : source_color = vec4(1.0);            // StandardMaterial3D.AlbedoColor modulating the texture
varying vec4 vcol;

// SHELTER, SMOOTHED (master 2026-09-06 "the rainmap seems really jagged on buildings placed on non-cardinals").
// The map stores a HEIGHT per 0.5 m cell and one nearest read gives a hard yes/no, so a roof that is not axis-aligned
// came out as a staircase along its edge. Blending the HEIGHTS is the trap the nearest read was chosen to avoid (it
// invents mid-air roofs at every edge), so the four surrounding cells are each tested SEPARATELY and it is their 0/1
// answers that get bilinearly blended. The edge then runs diagonally, over half a metre, instead of stepping.
float roof_shelter(vec2 xz, float y, float margin) {
    if (rain_roof_rect.z <= 0.0) return 0.0;
    vec2 uv = (xz - rain_roof_rect.xy) / (2.0 * rain_roof_rect.z) + 0.5;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) return 0.0;
    ivec2 sz = textureSize(rain_roof, 0);
    vec2 t = uv * vec2(sz) - 0.5;
    ivec2 b = ivec2(floor(t));
    vec2 f = fract(t);
    float acc = 0.0;
    for (int j = 0; j < 2; j++) {
        for (int i = 0; i < 2; i++) {
            ivec2 c = clamp(b + ivec2(i, j), ivec2(0), sz - ivec2(1));
            float under = (y < texelFetch(rain_roof, c, 0).r - margin) ? 1.0 : 0.0;
            acc += under * mix(1.0 - f.x, f.x, float(i)) * mix(1.0 - f.y, f.y, float(j));
        }
    }
    return acc;   // 0 = open sky .. 1 = fully under something
}

uniform vec3 dry_albedo : source_color = vec3(0.30, 0.32, 0.35);
uniform float dry_roughness = 0.85;
uniform float splash_scale = 1.0;                        // world-space frequency of the ripple cells
uniform float impact_amount : hint_range(0.0, 1.0) = 1.0;    // 0 = wetness only, NO splash impacts (terrain-like ground); 1 = full impacts (props). Master: gate impacts to solid props, not terrain.
uniform float impact_opacity : hint_range(0.0, 1.0) = 0.18;  // splash brightness/opacity -- LOW so impacts read as subtle wet glints, not paint (master: more transparent; was a hard 0.38)

varying vec3 world_pos;

float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }

// Splash rings: tile world-XZ into cells; each cell fires an expanding-then-fading ring on its own phase, so the
// surface reads as a field of overlapping raindrop impacts. Two octaves so it doesn't look grid-locked.
float splashes(vec2 wxz, float t, float amt){
    float acc = 0.0;
    float gate = 1.0 - clamp(amt, 0.0, 1.0) * 0.20;       // more rain -> lower threshold -> more impacts fire (kept SPARSE: heavy ~18%, drizzle ~7% of cells)
    for (int k = 0; k < 2; k++){
        float sc = (1.6 + float(k) * 2.8) * splash_scale;   // larger cells -> fewer impacts per area
        vec2 g = wxz * sc + float(k) * 21.0;
        vec2 base = floor(g);
        // Voronoi-style: scan the 3x3 neighbourhood, each cell has a JITTERED ripple source -> scattered, not a grid,
        // and rings from neighbouring cells overlap freely (no cell-edge clipping).
        for (int dy = -1; dy <= 1; dy++){
            for (int dx = -1; dx <= 1; dx++){
                vec2 id = base + vec2(float(dx), float(dy));
                float seed = h21(id);
                float tt = t * (0.65 + seed * 0.7) + seed;              // rate stays on the STATIC seed -- per-cycle rate feeds back into cyc + stutters the lifecycle (tinyclaw)
                float cyc = floor(tt);                                  // WHICH ripple this cell is on (not where within it)
                float life = fract(tt);                                 // 0..1 within that ripple
                vec2 q = id + cyc * 13.7;                               // re-roll the cell's randomness each cycle
                vec2 center = id + vec2(h21(q + 1.3), h21(q + 7.7));    // splash point MOVES every cycle -> scattered, not frozen at one spot per cell (master: impacts looked fixed)
                float rad = life * (0.16 + seed * 0.20);               // small raindrop-impact radius (~0.22m), not big pond ripples
                float ring = smoothstep(0.05, 0.0, abs(length(g - center) - rad));
                acc += ring * (1.0 - life) * step(gate, h21(q)) * (0.55 + seed * 0.45);   // WHICH cells fire also re-rolls per cycle (was static seed -> same ~18% forever)
            }
        }
    }
    return acc;
}

void vertex(){ world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; vcol = COLOR; }

void fragment(){
    // world normal -> upness is camera-independent (view-space NORMAL.y dried the surface on a steep look-down shot;
    // the terrain shader already transforms to world space -- match it here so harness/demo surfaces don't dry on tilt).
    float upness = smoothstep(0.45, 0.80, normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz).y);
    float sheltered = 1.0 - roof_shelter(world_pos.xz, world_pos.y, 0.3);   // something above this bit of surface -> dry, and the edge of it is not a staircase
    float wet = clamp(rain_wetness, 0.0, 1.0) * upness * sheltered;
    float rint = clamp(rain_intensity, 0.0, 1.0) * sheltered;
    vec3 base = use_tex ? texture(albedo_tex, UV).rgb * tint.rgb : (use_nomip ? texture(albedo_tex_nomip, UV).rgb * tint.rgb : dry_albedo);
    if (use_vcol) base *= vcol.rgb;
    vec3 alb = base * mix(1.0, 0.58, wet);                  // wet darkens the surface
    float rough = mix(dry_roughness, 0.42, wet);           // wet = smoother, but DAMP not mirror (strawberry 2026-09-04: "turn down the reflections on wet surfaces by a lot"; was 0.10)
    float sp = 0.0;
    if (rint > 0.0 && impact_amount > 0.0) sp = splashes(world_pos.xz, TIME, rint) * rint * upness * impact_amount;   // gate the ~800-ALU voronoi -- zero in clear weather OR on impact-disabled surfaces (terrain)
    ALBEDO = alb + sp * impact_opacity;
    ROUGHNESS = rough;
    METALLIC = 0.0;                                        // no metallic: asphalt is not chrome
    SPECULAR = 0.5 + wet * 0.12;                           // a hint of sheen (was +0.45)
}
