//! Remaining scalar Math utility bodies from the same original Godot source.
use std::f64::consts::TAU;
fn lerp(a: f64, b: f64, t: f64) -> f64 {
    a + (b - a) * t
}
pub(super) fn cubic(a: f64, b: f64, pre: f64, post: f64, t: f64) -> f64 {
    0.5 * (2. * a
        + (-pre + b) * t
        + (2. * pre - 5. * a + 4. * b - post) * (t * t)
        + (-pre + 3. * a - 3. * b + post) * (t * t * t))
}
pub(super) fn angles(a: f64, b: f64, pre: f64, post: f64) -> [f64; 4] {
    let from = a % TAU;
    let pre_diff = (pre - from) % TAU;
    let pre = from + (2. * pre_diff) % TAU - pre_diff;
    let to_diff = (b - from) % TAU;
    let to = from + (2. * to_diff) % TAU - to_diff;
    let post_diff = (post - to) % TAU;
    let post = to + (2. * post_diff) % TAU - post_diff;
    [from, to, pre, post]
}
pub(super) fn timed(
    [a, b, pre, post]: [f64; 4],
    weight: f64,
    to_t: f64,
    pre_t: f64,
    post_t: f64,
) -> f64 {
    let t = lerp(0., to_t, weight);
    let a1 = lerp(
        pre,
        a,
        if pre_t == 0. {
            0.
        } else {
            (t - pre_t) / -pre_t
        },
    );
    let a2 = lerp(a, b, if to_t == 0. { 0.5 } else { t / to_t });
    let a3 = lerp(
        b,
        post,
        if post_t - to_t == 0. {
            1.
        } else {
            (t - to_t) / (post_t - to_t)
        },
    );
    let b1 = lerp(
        a1,
        a2,
        if to_t - pre_t == 0. {
            0.
        } else {
            (t - pre_t) / (to_t - pre_t)
        },
    );
    let b2 = lerp(a2, a3, if post_t == 0. { 1. } else { t / post_t });
    lerp(b1, b2, if to_t == 0. { 0.5 } else { t / to_t })
}
pub(super) fn decimals(step: f64) -> i64 {
    // VariantUtilityFunctions takes float, Math takes double.
    let abs = (step as f32 as f64).abs();
    let decs = abs - (abs as i32) as f64;
    [
        0.9999,
        0.09999,
        0.009999,
        0.0009999,
        0.00009999,
        0.000009999,
        0.0000009999,
        0.00000009999,
        0.000000009999,
        0.0000000009999,
    ]
    .iter()
    .position(|step| decs >= *step)
    .unwrap_or(0) as i64
}
