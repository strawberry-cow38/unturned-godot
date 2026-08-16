shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

// Stylised sea for the port: summed sine WAVES (vertex-displaced), white FOAM on the wave crests, and
// depth-based SHORE FOAM where the seabed comes up under the surface (master 2026-08-16). Depth-tinted
// water underneath. Needs a subdivided plane for the vertex waves to have anything to move.

uniform vec4 water_deep : source_color = vec4(0.07, 0.19, 0.33, 0.90);
uniform vec4 water_shallow : source_color = vec4(0.16, 0.42, 0.54, 0.60);
uniform vec4 foam_color : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform float wave_amp = 0.45;      // metres of vertical wave
uniform float wave_len = 10.0;      // metres between crests (largest wave)
uniform float wave_speed = 0.7;
uniform float crest_foam = 0.68;    // wave-height fraction [-1..1] above which crest foam kicks in (higher = only the tallest crests)
uniform float shore_depth = 2.2;    // vertical water depth (m) under which shore foam shows
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear;

varying float v_crest;   // this vertex's normalised wave height, for crest foam

float wave_at(vec2 p, float t) {
    float k = 6.2831853 / wave_len;
    float h  = sin(dot(p, vec2(0.98,  0.20)) * k        + t)        * 0.55;
    h       += sin(dot(p, vec2(-0.38, 0.92)) * k * 1.3  + t * 1.3)  * 0.30;
    h       += sin(dot(p, vec2(0.71, -0.71)) * k * 2.1  + t * 0.8)  * 0.15;
    return h;   // ~[-1, 1]
}

void vertex() {
    vec2 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;   // world XZ (waves are world-anchored so they don't slide with the plane)
    float t = TIME * wave_speed;
    float h = wave_at(wp, t);
    VERTEX.y += h * wave_amp;
    v_crest = h;
    // normal from the wave gradient (finite difference) so lighting + specular ripple with the swell
    float e = 0.8;
    float hx = wave_at(wp + vec2(e, 0.0), t);
    float hz = wave_at(wp + vec2(0.0, e), t);
    NORMAL = normalize(vec3((h - hx) * wave_amp, e, (h - hz) * wave_amp));
}

void fragment() {
    // SHORE FOAM: reconstruct the opaque scene behind the water, foam where the water is shallow over it.
    float raw = texture(depth_tex, SCREEN_UV).r;
    vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
    vp.xyz /= vp.w;
    // VERTICAL water depth (world-Y gap between the surface fragment and the seabed behind it). Angle-independent,
    // so the foam band tracks the whole waterline -- a view-space-z gap only foams the near-camera corner at grazing angles.
    float bed_y = (INV_VIEW_MATRIX * vec4(vp.xyz, 1.0)).y;
    vec3 wpos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;   // this water fragment in world space
    float depth_v = max(wpos.y - bed_y, 0.0);
    float shore = 1.0 - smoothstep(0.0, shore_depth, depth_v);
    shore = pow(shore, 0.6);   // brighten the band so it reads as foam over bright sand, not just a thin lip
    // living foam: break the shore line with a slow world-anchored shimmer instead of a dead-flat gradient
    float shimmer = 0.72 + 0.28 * sin(dot(wpos.xz, vec2(0.6, 0.35)) * 2.0 + TIME * 1.6);
    shore *= shimmer;

    // CREST FOAM: near the tops of the waves.
    float crest = smoothstep(crest_foam, crest_foam + 0.22, v_crest);

    float foam = clamp(max(crest, shore), 0.0, 1.0);

    // depth-tinted water: shallow reads lighter, deep darker.
    float dt = smoothstep(0.0, shore_depth * 3.0, depth_v);
    vec3 base = mix(water_shallow.rgb, water_deep.rgb, dt);
    float a  = mix(water_shallow.a, water_deep.a, dt);

    ALBEDO = mix(base, foam_color.rgb, foam);
    ALPHA = mix(a, 1.0, foam);
    ROUGHNESS = mix(0.05, 0.85, foam);   // glassy water, matte foam
    SPECULAR = 0.6;
    METALLIC = 0.0;
}
