// game/content/ecg.gdshader, line for line: the Science_3 patient monitor's
// screen. Unshaded; every proportion is measured off the vanilla prop's own
// modelled trace (see the source for the derivation). Values are LINEAR, as
// the source writes ALBEDO into a linear buffer.
import bevy_pbr::render::forward_io::VertexOutput;

struct EcgParams {
    // time_s, alive, lit, period
    clock: vec4<f32>,
    // sag, unused x3
    extra: vec4<f32>,
}
@group(constants::MATERIAL_BIND_GROUP) @binding(0) var<uniform> ecg: EcgParams;

const SCREEN_BG: vec3<f32> = vec3<f32>(0.035601, 0.035601, 0.035601);
const TRACE: vec3<f32> = vec3<f32>(0.247, 0.467, 0.204);
const BASELINE: f32 = 0.364;
const VANILLA_SCALE: f32 = 0.80;
const R_PEAK: f32 = 0.358 * 0.80;
const Q_DIP: f32 = 0.176 * 0.80;
const LINE_W: f32 = 0.050;
const PTS: i32 = 13;
const ASPECT: f32 = 1.0713;

fn beat(i: i32) -> vec2<f32> {
    switch i {
        case 0: { return vec2<f32>(0.000, 0.0); }
        case 1: { return vec2<f32>(0.120, 0.0); }
        case 2: { return vec2<f32>(0.160, 0.06); }
        case 3: { return vec2<f32>(0.200, 0.0); }
        case 4: { return vec2<f32>(0.330, 0.0); }
        case 5: { return vec2<f32>(0.375, -Q_DIP); }
        case 6: { return vec2<f32>(0.420, R_PEAK); }
        case 7: { return vec2<f32>(0.465, -0.09); }
        case 8: { return vec2<f32>(0.510, 0.0); }
        case 9: { return vec2<f32>(0.640, 0.0); }
        case 10: { return vec2<f32>(0.700, 0.10); }
        case 11: { return vec2<f32>(0.760, 0.0); }
        default: { return vec2<f32>(1.000, 0.0); }
    }
}

fn seg_dist(p: vec2<f32>, a: vec2<f32>, b: vec2<f32>) -> f32 {
    let ab = b - a;
    let ap = p - a;
    let t = clamp(dot(ap, ab) / max(dot(ab, ab), 1e-9), 0.0, 1.0);
    return length(ap - ab * t);
}

fn draw_line(p: vec2<f32>, aspect: f32) -> f32 {
    var d = 1e9;
    for (var i = 0; i < PTS - 1; i++) {
        let a = vec2<f32>(beat(i).x * aspect, BASELINE + beat(i).y);
        let b = vec2<f32>(beat(i + 1).x * aspect, BASELINE + beat(i + 1).y);
        d = min(d, seg_dist(p, a, b));
    }
    return d;
}

fn fract_g(x: vec2<f32>) -> vec2<f32> {
    return x - floor(x);
}

@fragment
fn fragment(in: VertexOutput) -> @location(0) vec4<f32> {
    if ecg.clock.z < 0.5 {
        discard;
    }
    let uv = in.uv;
    var col = SCREEN_BG;
    let gl = abs(fract_g(uv * vec2<f32>(8.0, 5.0)) - 0.5);
    col += vec3<f32>(0.035, 0.070, 0.048) * (1.0 - smoothstep(0.0, 0.06, min(gl.x, gl.y)));
    let head = fract(ecg.clock.x / max(ecg.clock.w, 0.05));
    var age = head - uv.x;
    if age < 0.0 {
        age += 1.0;
    }
    let trail = 1.0 - smoothstep(0.0, 0.92, age);
    let p = vec2<f32>(uv.x * ASPECT, uv.y);
    var d: f32;
    if ecg.clock.y > 0.5 {
        d = draw_line(p, ASPECT);
    } else {
        d = seg_dist(p, vec2<f32>(0.0, BASELINE), vec2<f32>(ASPECT, BASELINE));
    }
    let on = 1.0 - smoothstep(LINE_W * 0.35, LINE_W * 0.75, d);
    let head_glow = (1.0 - smoothstep(0.0, 0.03, age)) * on;
    col += TRACE * (on * (0.45 + 0.55 * trail) + head_glow * 0.8);
    return vec4<f32>(col * ecg.extra.x, 1.0);
}
