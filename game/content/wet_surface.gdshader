// Wet hard-surface shader for rain: up-facing faces darken + go glossy as `rain_wetness` rises, and animated splash
// ripple rings play on them (raindrop impacts). Driven by the global uniform `rain_wetness` (WeatherManager sets it;
// the --raintest harness sets it directly). Master 2026-08-29: raindrop impact splashes + hard-surface wetness.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

global uniform float rain_wetness;                       // 0 dry .. 1 soaked

uniform vec3 dry_albedo : source_color = vec3(0.30, 0.32, 0.35);
uniform float dry_roughness = 0.85;
uniform float splash_scale = 1.0;                        // world-space frequency of the ripple cells

varying vec3 world_pos;

float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }

// Splash rings: tile world-XZ into cells; each cell fires an expanding-then-fading ring on its own phase, so the
// surface reads as a field of overlapping raindrop impacts. Two octaves so it doesn't look grid-locked.
float splashes(vec2 wxz, float t){
    float acc = 0.0;
    for (int k = 0; k < 2; k++){
        float sc = (2.2 + float(k) * 3.3) * splash_scale;
        vec2 g = wxz * sc + float(k) * 21.0;
        vec2 base = floor(g);
        // Voronoi-style: scan the 3x3 neighbourhood, each cell has a JITTERED ripple source -> scattered, not a grid,
        // and rings from neighbouring cells overlap freely (no cell-edge clipping).
        for (int dy = -1; dy <= 1; dy++){
            for (int dx = -1; dx <= 1; dx++){
                vec2 id = base + vec2(float(dx), float(dy));
                float seed = h21(id);
                vec2 center = id + vec2(h21(id + 1.3), h21(id + 7.7));   // random source point inside the cell
                float life = fract(t * (0.65 + seed * 0.7) + seed);     // 0..1 ripple lifecycle
                float rad = life * (0.32 + seed * 0.40);                // varied max radius
                float ring = smoothstep(0.06, 0.0, abs(length(g - center) - rad));
                acc += ring * (1.0 - life) * step(0.40, seed) * (0.55 + seed * 0.45);   // fade out; vary brightness; not every cell fires
            }
        }
    }
    return acc;
}

void vertex(){ world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }

void fragment(){
    float wet = clamp(rain_wetness, 0.0, 1.0) * smoothstep(0.45, 0.80, NORMAL.y);   // only up-facing faces get wet
    vec3 alb = dry_albedo * mix(1.0, 0.58, wet);            // wet darkens the surface
    float rough = mix(dry_roughness, 0.10, wet);           // wet = smooth/glossy -> catches highlights + reflections
    float sp = splashes(world_pos.xz, TIME) * wet;         // bright ripple crests
    ALBEDO = alb + sp * 0.38;
    ROUGHNESS = rough;
    METALLIC = wet * 0.1;
    SPECULAR = 0.5 + wet * 0.45;
}
