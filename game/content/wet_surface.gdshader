// Wet hard-surface shader for rain: up-facing faces darken + go glossy as `rain_wetness` rises, and animated splash
// ripple rings play on them (raindrop impacts). Driven by the global uniform `rain_wetness` (WeatherManager sets it;
// the --raintest harness sets it directly). Master 2026-08-29: raindrop impact splashes + hard-surface wetness.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

global uniform float rain_wetness;                       // 0 dry .. 1 soaked (surface darken/gloss)
global uniform float rain_intensity;                     // 0 .. 1 -- drives the splash-impact density + brightness to MATCH the falling rain

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

void vertex(){ world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }

void fragment(){
    // world normal -> upness is camera-independent (view-space NORMAL.y dried the surface on a steep look-down shot;
    // the terrain shader already transforms to world space -- match it here so harness/demo surfaces don't dry on tilt).
    float upness = smoothstep(0.45, 0.80, normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz).y);
    float wet = clamp(rain_wetness, 0.0, 1.0) * upness;
    float rint = clamp(rain_intensity, 0.0, 1.0);
    vec3 alb = dry_albedo * mix(1.0, 0.58, wet);            // wet darkens the surface
    float rough = mix(dry_roughness, 0.10, wet);           // wet = smooth/glossy -> catches highlights + reflections
    float sp = 0.0;
    if (rint > 0.0 && impact_amount > 0.0) sp = splashes(world_pos.xz, TIME, rint) * rint * upness * impact_amount;   // gate the ~800-ALU voronoi -- zero in clear weather OR on impact-disabled surfaces (terrain)
    ALBEDO = alb + sp * impact_opacity;
    ROUGHNESS = rough;
    METALLIC = wet * 0.1;
    SPECULAR = 0.5 + wet * 0.45;
}
