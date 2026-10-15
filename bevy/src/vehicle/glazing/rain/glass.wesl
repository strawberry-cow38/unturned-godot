// Port of content/rain_glass.gdshader. The two coordinate frames and all field
// constants are original. Source face_ok is diagnostic only, despite its header.
import bevy_pbr::render::{
    forward_io::{VertexOutput, FragmentOutput},
    pbr_fragment::pbr_input_from_vertex_output,
    pbr_functions::{apply_pbr_lighting, main_pass_post_lighting_processing},
    pbr_types::{STANDARD_MATERIAL_FLAGS_ALPHA_MODE_BLEND, STANDARD_MATERIAL_FLAGS_DOUBLE_SIDED_BIT, STANDARD_MATERIAL_FLAGS_FOG_ENABLED_BIT},
    mesh_view_bindings::{view, view_transmission_texture, view_transmission_sampler},
};

struct GlassUniform {
    world_from_local: mat4x4<f32>,
    local_from_world: mat4x4<f32>,
    tint: vec4<f32>,
    axis: vec4<f32>,
    weather: vec4<f32>,
    surface: vec4<f32>,
}
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> glass: GlassUniform;

fn h21(input: vec2<f32>) -> f32 {
    var p = fract(input * vec2<f32>(127.32, 311.7));
    p += vec2<f32>(dot(p, p + vec2<f32>(34.53)));
    return fract(p.x * p.y);
}
// GLSL accepts descending smoothstep edges; express that polynomial explicitly
// so the original 14->5m detail and .030->0 runner head remain portable WGSL.
fn ramp(low: f32, high: f32, x: f32) -> f32 {
    let t = clamp((x - low) / (high - low), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

@fragment
fn fragment(in: VertexOutput, @builtin(front_facing) front: bool) -> FragmentOutput {
    let model = glass.world_from_local;
    let obj_pos = (glass.local_from_world * in.world_position).xyz;
    let world_position = in.world_position.xyz;
    // Transform the geometric normal back to local space before deriving the
    // empty-mesh fallback. No second back-face flip (PBR handles cull_disabled).
    var fnormal = (transpose(model) * vec4<f32>(in.world_normal, 0.0)).xyz;
    if length(fnormal) > 0.5 { fnormal = normalize(fnormal); } else { fnormal = vec3<f32>(0.0, 0.0, 1.0); }
    var n = fnormal;
    if length(glass.axis.xyz) > 0.5 { n = normalize(glass.axis.xyz); }
    let face_ok = step(0.75, abs(dot(fnormal, n)));
    var reference = vec3<f32>(0.0, 0.0, 1.0);
    if abs(n.y) < 0.9 { reference = vec3<f32>(0.0, 1.0, 0.0); }
    let v = reference - n * dot(reference, n);
    var pane_v: vec3<f32>;
    if length(v) > 1e-4 { pane_v = normalize(v); }
    else if abs(n.x) < 0.9 { pane_v = normalize(vec3<f32>(1.0, 0.0, 0.0) - n * n.x); }
    else { pane_v = normalize(vec3<f32>(0.0, 0.0, 1.0) - n * n.z); }
    let pane_u = normalize(cross(n, pane_v));
    let su = (view.view_from_world * model * vec4<f32>(pane_u, 0.0)).xy;
    let sv = (view.view_from_world * model * vec4<f32>(pane_v, 0.0)).xy;
    let odown = normalize((transpose(model) * vec4<f32>(0.0, -1.0, 0.0, 0.0)).xyz);
    let inplane = odown - n * dot(odown, n);
    let fall_tilt = clamp(length(inplane), 0.0, 1.0);
    var fall_dir = vec2<f32>(0.0, 1.0);
    if fall_tilt > 1e-4 { fall_dir = normalize(vec2<f32>(dot(inplane, pane_u), dot(inplane, pane_v))); }

    let time = glass.weather.w;
    let rint = clamp(glass.weather.y, 0.0, 1.0);
    let wet = clamp(glass.weather.x, 0.0, 1.0);
    let open = 1.0 - clamp(glass.weather.z, 0.0, 1.0);
    let dist = length((view.view_from_world * in.world_position).xyz);
    let range = 1.0 - ramp(22.0 * 0.75, 22.0, dist);
    let detail = ramp(14.0, 5.0, dist) * range;
    let amt = wet * open * range;
    var su_n = vec2<f32>(1.0, 0.0);
    var sv_n = vec2<f32>(0.0, -1.0);
    if length(su) > 1e-4 { su_n = normalize(su); }
    if length(sv) > 1e-4 { sv_n = normalize(sv); }
    var off = vec2<f32>(0.0);
    var cov = 0.0;
    var rim = 0.0;

    if amt > 0.001 && detail > 0.001 {
        let g = vec2<f32>(dot(obj_pos, pane_u), dot(obj_pos, pane_v));
        let cellp = g * 70.0;
        let base = floor(cellp);
        let run_cells = 1.8;
        for (var dy = -2; dy <= 2; dy++) {
            for (var dx = -1; dx <= 1; dx++) {
                let id = base + vec2<f32>(f32(dx), f32(dy));
                let rate = 0.10 + 0.28 * h21(id + vec2<f32>(5.1));
                let tt = time * rate + h21(id + vec2<f32>(2.7)) * 9.0;
                let cyc = floor(tt);
                let life = fract(tt);
                let q = id + vec2<f32>(cyc * 17.3);
                let present = step(1.0 - 0.55 * amt, h21(q));
                if present < 0.5 { continue; }
                let c = id + vec2<f32>(h21(q + vec2<f32>(3.1)), h21(q + vec2<f32>(7.9)));
                let rs = h21(q + vec2<f32>(11.3));
                let r = 0.042 + 0.078 * rs * rs + 0.055 * step(0.965, rs);
                let hold = 0.34 + 0.34 * h21(q + vec2<f32>(19.7));
                let slip = max(0.0, life - hold) / max(0.0001, 1.0 - hold);
                let fall = slip * slip * run_cells * ramp(0.25, 0.6, fall_tilt);
                let c_now = c + fall_dir * fall;
                let env = ramp(0.0, 0.04, life) * (1.0 - ramp(0.86, 1.0, life));
                let pa = cellp - c;
                let ba = c_now - c;
                var hseg = 0.0;
                if fall > 1e-5 { hseg = clamp(dot(pa, ba) / max(1e-5, dot(ba, ba)), 0.0, 1.0); }
                let trail_w = r * mix(0.30, 1.0, hseg);
                let dseg = pa - ba * hseg;
                let d_trail = length(dseg) / max(1e-5, trail_w);
                let dp = (cellp - c_now) / r;
                let d_bead = length(dp);
                if min(d_bead, d_trail) > 1.25 { continue; }
                let bead = 1.0 - ramp(0.86, 1.04, d_bead);
                let trail = (1.0 - ramp(0.80, 1.05, d_trail)) * mix(0.26, 0.80, hseg) * step(1e-5, fall);
                let inside = env * detail * max(bead, trail);
                off += -(dp.x * su_n + dp.y * sv_n) * 0.020 * env * detail * bead;
                cov = max(cov, inside);
                rim += env * detail * ramp(0.62, 0.94, d_bead) * (1.0 - ramp(0.98, 1.08, d_bead));
            }
        }
    }
    var rcov = 0.0;
    var rrim = 0.0;
    let run = rint * open * glass.surface.z * range * ramp(0.25, 0.6, fall_tilt);
    let gx = dot(obj_pos, pane_u);
    let gy = world_position.y;
    // Compute derivatives before the variable coverage branch (same values,
    // avoids undefined derivatives in a non-uniform WGSL control-flow path).
    let rsu = normalize(vec2<f32>(dpdx(gx), dpdy(gx)) + vec2<f32>(1e-6, 0.0));
    let rsv = normalize(vec2<f32>(dpdx(gy), dpdy(gy)) + vec2<f32>(0.0, -1e-6));
    if run > 0.001 {
        let cx = gx * 14.0;
        let cid = floor(cx);
        let cs = h21(vec2<f32>(cid, 5.7));
        let running_column = step(1.0 - 0.55 * run, cs);
        let x0 = 0.5 + (cs - 0.5) * 0.5;
        let w = 0.045 + 0.035 * h21(vec2<f32>(cid, 2.3));
        let dxs = (fract(cx) - x0) / w;
        let dx = abs(dxs);
        let period = 1.1 + 1.4 * h21(vec2<f32>(cid, 9.1));
        let speed = 0.30 + 0.40 * h21(vec2<f32>(cid, 4.4));
        let sfall = fract((time * speed + cs * period + gy) / period);
        let body = running_column * (1.0 - ramp(0.0, 0.22 + 0.18 * cs, sfall));
        let tail = body * (1.0 - ramp(0.6, 1.0, dx));
        let head_amt = running_column * ramp(0.030, 0.0, sfall);
        let head = head_amt * (1.0 - ramp(0.9, 1.6, dx));
        rcov = max(tail * 0.7, head) * 0.5;
        rrim = (body * ramp(0.35, 0.8, dx) * (1.0 - ramp(0.9, 1.15, dx)) * 0.7
             + head_amt * ramp(0.8, 1.3, dx) * (1.0 - ramp(1.5, 1.9, dx))) * 0.5;
        off += -(dxs * 0.6 * rsu + 0.5 * rsv) * 0.038 * rcov;
    }
    var col = glass.tint.rgb;
    var alpha = glass.tint.a;
    var emit = vec3<f32>(0.0);
    let water = clamp(max(cov, rcov), 0.0, 1.0);
    if water > 0.002 {
        let screen_uv = in.position.xy / vec2<f32>(textureDimensions(view_transmission_texture));
        let bg = textureSampleLevel(view_transmission_texture, view_transmission_sampler,
            clamp(screen_uv + off, vec2<f32>(0.001), vec2<f32>(0.999)), 0.0).rgb;
        let k = water * 0.9;
        emit = bg * k * (1.0 + rrim * 0.35);
        col = mix(col, vec3<f32>(0.0), k);
        alpha = mix(alpha, 0.95, water);
    }
    emit += vec3<f32>(rim * 0.20 * amt + rrim * 0.55);
    if glass.surface.w == 1.0 { col = vec3<f32>(0.0); emit = vec3<f32>(face_ok, fract(dot(obj_pos, pane_v) * 4.0), fract(dot(obj_pos, pane_u) * 4.0)); alpha = 1.0; }
    else if glass.surface.w == 2.0 { col = vec3<f32>(0.0); emit = vec3<f32>(rcov, rrim, rint); alpha = 1.0; }
    else if glass.surface.w == 3.0 { col = vec3<f32>(0.0); emit = vec3<f32>(cov, amt, detail); alpha = 1.0; }

    var pbr = pbr_input_from_vertex_output(in, front, true);
    pbr.material.base_color = vec4<f32>(col, alpha);
    // Godot scene_forward_clustered sums EMISSION without alpha before its
    // final alpha blend. Bevy's PBR helper premultiplies its emission by base
    // alpha even for Blend, so keep this source emission outside that helper.
    pbr.material.emissive = vec4<f32>(0.0);
    pbr.material.metallic = glass.surface.x;
    pbr.material.perceptual_roughness = mix(glass.surface.y, 0.03, water);
    pbr.material.reflectance = vec3<f32>(0.5);
    pbr.material.flags = STANDARD_MATERIAL_FLAGS_ALPHA_MODE_BLEND | STANDARD_MATERIAL_FLAGS_DOUBLE_SIDED_BIT | STANDARD_MATERIAL_FLAGS_FOG_ENABLED_BIT;
    var out: FragmentOutput;
    let lit = apply_pbr_lighting(pbr);
    // Scene-copy color is already exposed. Both it and the source rim emission
    // go through common fog/output exactly once, then the one alpha blend.
    out.color = main_pass_post_lighting_processing(pbr, vec4<f32>(lit.rgb + emit, lit.a));
    return out;
}
