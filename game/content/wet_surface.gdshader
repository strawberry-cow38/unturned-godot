// Wet hard-surface shader for rain: up-facing faces darken + go glossy as `rain_wetness` rises, and animated splash
// ripple rings play on them (raindrop impacts). Driven by the global uniform `rain_wetness` (WeatherManager sets it;
// the --raintest harness sets it directly). Master 2026-08-29: raindrop impact splashes + hard-surface wetness.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx, cull_disabled;
#include "res://content/puddles.gdshaderinc"
#include "res://content/rain_impacts.gdshaderinc"   // h21 + rain_impacts(): the ring AND the splashback crown, shared with the terrain shader

global uniform float rain_wetness;                       // 0 dry .. 1 soaked (surface darken/gloss)
global uniform float rain_intensity;                     // 0 .. 1 -- drives the splash-impact density + brightness to MATCH the falling rain
global uniform float rain_puddle;                        // 0 .. 1 standing water, minutes behind the rain (WeatherManager)
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
uniform float splashback_amount : hint_range(0.0, 1.0) = 1.0;   // 0 = rings only, no splashback (per-surface kill switch)
uniform float splashback_gain = 4.5;                          // crown brightness RELATIVE to impact_opacity -- a splash is brighter than the ring it comes from
uniform float impact_opacity : hint_range(0.0, 1.0) = 0.18;  // splash brightness/opacity -- LOW so impacts read as subtle wet glints, not paint (master: more transparent; was a hard 0.38)
uniform float puddle_amount : hint_range(0.0, 1.0) = 0.0;    // standing water on this surface (see puddles.gdshaderinc). DEFAULT OFF: only roads
                                                             // and the big dock opt in (master 2026-09-06 "ONLY have puddles on Road, road spline,
                                                             // road material, the large dock prop. NOT on all props")
uniform float puddle_max_distance = 60.0;                    // past this there is no water at all -- and no noise field evaluated for it

varying vec3 world_pos;

void vertex(){ world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; vcol = COLOR; }

void fragment(){
	// Godot does NOT flip NORMAL for a back face in a CUSTOM spatial shader the way it does for a built-in
	// material, and this one draws cull_disabled -- so without this every back-facing fragment is lit with a
	// normal pointing the wrong way. Found on the vehicles (7b1ceae5), where it made every car look
	// permanently in shadow; master: "fix em".
	NORMAL = FRONT_FACING ? NORMAL : -NORMAL;
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
    // PUDDLES first: the impact rings are gated behind them (master 2026-09-06 "gate water ripple impacts behind being
    // on a puddle"), so a ring only ever lands on standing water -- which is where you actually see one.
    float prange = 1.0 - smoothstep(puddle_max_distance * 0.7, puddle_max_distance, length(VERTEX));
    // `level` must be the BARE global: it is the one argument puddle_mask branches on, and a branch on anything
    // that varies within a quad (shelter does, at a roof edge) leaves the fwidth() in there undefined. Shelter
    // scales the mask instead, via the upness argument -- under a roof there is no puddle either way.
    float pud = puddle_mask(world_pos.xz, clamp(rain_puddle, 0.0, 1.0), upness * sheltered, prange) * puddle_amount;
    float sp = 0.0, crown = 0.0;
    // RING AND CROWN NO LONGER SHARE A GATE (strawberry 2026-09-09: "allow the splashback (but not the ripple)
    // effect on any 'hard' surface"). They used to be computed under one `pud > 0.01` test, so a splash could
    // only exist where standing water already was -- which is right for a RIPPLE, since a ring in nothing is a
    // ring on dry stone, and wrong for the SPLASHBACK, which is rain hitting a hard surface and bouncing. Every
    // paved thing does that, puddle or not.
    //
    // The distance gate survives, and has to: rain_impacts is ~800 ALU of voronoi, and `pud > 0.01` was doing
    // double duty as the far cull. prange takes that job so the cost stays bounded now the crown is not
    // puddle-limited. `sheltered` moves onto the crown explicitly -- it used to arrive for free inside pud.
    if (rint > 0.0 && impact_amount > 0.0 && prange > 0.01) {
        // The crown leans away from the VIEWER, so it needs the camera on the ground plane. A degenerate direction
        // (camera exactly overhead) disables the crown rather than feeding normalize() a zero vector.
        vec2 aw = world_pos.xz - INV_VIEW_MATRIX[3].xz;
        float awl = length(aw);
        float m = rint * upness * impact_amount;
        rain_impacts(world_pos.xz, TIME, rint, splash_scale, awl > 1e-3 ? aw / awl : vec2(0.0), sp, crown);
        sp *= m * pud;                                                  // RIPPLE: standing water only, unchanged
        crown *= m * splashback_amount * sheltered * prange;            // SPLASHBACK: any hard surface the sky can reach
    }
    // Standing water: MORE reflective than the damp around it (master: "puddles of more reflective blobs"). The damp
    // surface stays deliberately dull -- the contrast between the two is the whole effect.
    vec3 alb2 = mix(alb, alb * 0.62, pud);                 // water over it reads darker than the wet surface
    float rough2 = mix(rough, 0.13, pud);                  // a puddle is REFLECTIVE, not a mirror -- 0.06 was chrome (master 2026-09-07 "make puddles slightly less reflective")
    ALBEDO = alb2 + sp * impact_opacity * 2.5 + crown * impact_opacity * splashback_gain;   // crown rides the SAME opacity as its ring, so tuning one cannot desync them
    ROUGHNESS = rough2;
    METALLIC = 0.0;                                        // no metallic: asphalt is not chrome
    SPECULAR = mix(0.5 + wet * 0.12, 0.78, pud);           // a hint of sheen on damp ground; a real one on water, but standing water is not polished metal
}
