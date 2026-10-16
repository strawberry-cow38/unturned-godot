//! Deterministic scalar VariantUtilityFunctions used by the numeric editor.
use super::{Value, interpolation};
use std::f64::consts::{PI, TAU};
fn bounded(x: f64, min: f64, max: f64) -> f64 {
    let value = if x < min { min } else { x };
    if value > max { max } else { value }
}

fn posmod(x: f64, y: f64) -> f64 {
    let v = x % y;
    if v < 0. && y > 0. || v > 0. && y < 0. {
        v + y
    } else {
        v
    }
}
fn snapped(x: f64, step: f64) -> f64 {
    if step != 0. {
        (x / step + 0.5).floor() * step
    } else {
        x
    }
}
fn approx(a: f64, b: f64) -> bool {
    a == b || (a - b).abs() < (0.00001 * a.abs()).max(0.00001)
}
fn angle(a: f64, b: f64) -> f64 {
    let difference = (b - a) % TAU;
    (2. * difference) % TAU - difference
}
pub(super) fn call(name: &str, args: &[Value]) -> Option<Value> {
    use Value::*;
    if args.iter().any(|v| matches!(v, Fault(_))) {
        return Some(Fault(Box::new(Nil)));
    }
    if matches!(name, "int" | "float" | "bool") {
        if args.len() > 1 {
            return None;
        }
        let v = args.first().cloned().unwrap_or(Int(0));
        if matches!(v, Nil | Array(_)) || name == "bool" && matches!(v, Text(_)) {
            return None;
        }
        return Some(match name {
            "bool" => Bool(v.truth()),
            "int" => Int(if let Text(text) = &v {
                string_integer(text)
            } else {
                v.to_float().unwrap_or(0.) as i64
            }),
            _ => Float(v.to_float().unwrap_or(0.)),
        });
    }
    if name == "typeof" && args.len() == 1 {
        return Some(Int(match args[0] {
            Nil => 0,
            Bool(_) => 1,
            Int(_) => 2,
            Float(_) => 3,
            Text(_) => 4,
            Array(_) => 28,
            Fault(_) => return None,
        }));
    }
    if name == "String" && args.len() == 1 {
        return Some(Text(match &args[0] {
            Text(s) => s.clone(),
            Int(v) => v.to_string(),
            Float(v) => v.to_string(),
            Bool(v) => v.to_string(),
            _ => return None,
        }));
    }
    let generic = matches!(
        name,
        "min"
            | "max"
            | "clamp"
            | "wrap"
            | "lerp"
            | "floor"
            | "ceil"
            | "round"
            | "abs"
            | "sign"
            | "snapped"
    );
    let mut argument_error = false;
    let mut v = Vec::new();
    for arg in args {
        if let Some(number) = arg.number() {
            v.push(number);
        } else if !generic {
            if !matches!(arg, Bool(_)) {
                argument_error = true;
            }
            v.push(arg.to_float().unwrap_or(0.));
        } else if matches!(
            name,
            "floor" | "ceil" | "round" | "abs" | "sign" | "snapped"
        ) {
            return Some(Fault(Box::new(Text("Invalid argument".into()))));
        } else {
            return None;
        }
    }
    let integer = |i: usize| matches!(args[i], Int(_));
    if matches!(name, "min" | "max") {
        if v.len() < 2 {
            return None;
        }
        let mut index = 0;
        for i in 1..v.len() {
            if if name == "min" {
                v[i] < v[index]
            } else {
                v[i] > v[index]
            } {
                index = i;
            }
        }
        return Some(args[index].clone());
    }
    let result = match v.as_slice() {
        [x] => match name {
            "sin" => Float(x.sin()),
            "cos" => Float(x.cos()),
            "tan" => Float(x.tan()),
            "sinh" => Float(x.sinh()),
            "cosh" => Float(x.cosh()),
            "tanh" => Float(x.tanh()),
            "asin" => Float(x.asin()),
            "acos" => Float(x.acos()),
            "atan" => Float(x.atan()),
            "asinh" => Float(x.asinh()),
            "acosh" => Float(x.acosh()),
            "atanh" => Float(x.atanh()),
            "sqrt" => Float(x.sqrt()),
            "exp" => Float(x.exp()),
            "log" => Float(x.ln()),
            "floor" if integer(0) => args[0].clone(),
            "floor" | "floorf" => Float(x.floor()),
            "floori" => Int(x.floor() as i64),
            "ceil" if integer(0) => args[0].clone(),
            "ceil" | "ceilf" => Float(x.ceil()),
            "ceili" => Int(x.ceil() as i64),
            "round" if integer(0) => args[0].clone(),
            "round" | "roundf" => Float(x.round()),
            "roundi" => Int(x.round() as i64),
            "abs" if integer(0) => Int(args[0].integer()?.wrapping_abs()),
            "abs" | "absf" => Float(x.abs()),
            "absi" => Int((*x as i64).wrapping_abs()),
            "sign" if integer(0) => Int(args[0].integer()?.signum()),
            "sign" | "signf" => Float(if *x == 0. {
                0.
            } else if *x < 0. {
                -1.
            } else {
                1.
            }),
            "signi" => Int((*x as i64).signum()),
            "is_nan" => Bool(x.is_nan()),
            "is_inf" => Bool(x.is_infinite()),
            "is_finite" => Bool(x.is_finite()),
            "is_zero_approx" => Bool(x.abs() < 0.00001),
            "deg_to_rad" => Float(x * PI / 180.),
            "rad_to_deg" => Float(x * 180. / PI),
            "linear_to_db" => Float(x.ln() * 8.685889638065037),
            "db_to_linear" => Float((x * 0.11512925464970228).exp()),
            "step_decimals" => Int(interpolation::decimals(*x)),
            "nearest_po2" => Int(if *x <= 0. {
                0
            } else {
                (*x as u64).checked_next_power_of_two().unwrap_or(0) as i64
            }),
            _ => return None,
        },
        [a, b] => match name {
            "atan2" => Float(a.atan2(*b)),
            "pow" => Float(a.powf(*b)),
            "fmod" => Float(a % b),
            "fposmod" => Float(posmod(*a, *b)),
            "posmod" => Int(posmod((*a as i64) as f64, (*b as i64) as f64) as i64),
            "snapped" if integer(1) => Int(snapped(*a, *b) as i64),
            "snapped" | "snappedf" => Float(snapped(*a, *b)),
            "snappedi" => Int(snapped(*a, (*b as i64) as f64) as i64),
            "mini" => Int((*a as i64).min(*b as i64)),
            "maxi" => Int((*a as i64).max(*b as i64)),
            "minf" => Float(if a < b { *a } else { *b }),
            "maxf" => Float(if a > b { *a } else { *b }),
            "is_equal_approx" => Bool(approx(*a, *b)),
            "angle_difference" => Float(angle(*a, *b)),
            "pingpong" => Float(if *b == 0. {
                0.
            } else {
                (*b - (posmod(*a, 2. * b) - b).abs()).abs()
            }),
            "ease" => {
                let x = bounded(*a as f32 as f64, 0., 1.);
                let curve = *b as f32 as f64;
                Float(
                    (if curve > 0. {
                        if curve < 1. {
                            1. - (1. - x).powf(1. / curve)
                        } else {
                            x.powf(curve)
                        }
                    } else if curve < 0. {
                        if x < 0.5 {
                            (x * 2.).powf(-curve) * 0.5
                        } else {
                            (1. - (1. - (x - 0.5) * 2.).powf(-curve)) * 0.5 + 0.5
                        }
                    } else {
                        0.
                    }) as f32 as f64,
                )
            }
            _ => return None,
        },
        [a, b, c] => match name {
            "clamp" if args.iter().all(|v| matches!(v, Int(_))) => Int(bounded(*a, *b, *c) as i64),
            "clamp" | "clampf" => Float(bounded(*a, *b, *c)),
            "clampi" => {
                Int(bounded((*a as i64) as f64, (*b as i64) as f64, (*c as i64) as f64) as i64)
            }
            "wrap" if args.iter().all(|v| matches!(v, Int(_))) => {
                Int(if b == c { *b } else { b + posmod(a - b, c - b) } as i64)
            }
            "wrap" | "wrapf" => Float(if approx(*b, *c) {
                *b
            } else {
                b + posmod(a - b, c - b)
            }),
            "wrapi" => Int({
                let (a, b, c) = (*a as i64, *b as i64, *c as i64);
                if b == c {
                    b
                } else {
                    b.wrapping_add(
                        a.wrapping_sub(b)
                            .checked_rem(c.wrapping_sub(b))
                            .unwrap_or(0)
                            .wrapping_add(c.wrapping_sub(b))
                            .checked_rem(c.wrapping_sub(b))
                            .unwrap_or(0),
                    )
                }
            }),
            "lerp" if integer(0) != integer(1) => return None,
            "lerp" | "lerpf" => Float(a + (b - a) * c),
            "inverse_lerp" => Float((c - a) / (b - a)),
            "smoothstep" => {
                let t = bounded((c - a) / (b - a), 0., 1.);
                Float(if approx(*a, *b) {
                    *a
                } else {
                    t * t * (3. - 2. * t)
                })
            }
            "move_toward" => Float(if (b - a).abs() <= *c {
                *b
            } else {
                a + (b - a).signum() * c
            }),
            "lerp_angle" => Float(a + angle(*a, *b) * c),
            "rotate_toward" => {
                let difference = angle(*a, *b);
                Float(
                    a + bounded(*c, difference.abs() - PI, difference.abs()) * difference.signum(),
                )
            }
            _ => return None,
        },
        [a, b, c, d, t] => match name {
            "remap" => Float(d + (t - d) * (a - b) / (c - b)),
            "cubic_interpolate" => Float(interpolation::cubic(*a, *b, *c, *d, *t)),
            "cubic_interpolate_angle" => {
                let [a, b, c, d] = interpolation::angles(*a, *b, *c, *d);
                Float(interpolation::cubic(a, b, c, d, *t))
            }
            "bezier_interpolate" => Float(
                (1. - t).powi(3) * a
                    + 3. * (1. - t).powi(2) * t * b
                    + 3. * (1. - t) * t * t * c
                    + t * t * t * d,
            ),
            "bezier_derivative" => Float(
                3. * (1. - t).powi(2) * (b - a)
                    + 6. * (1. - t) * t * (c - b)
                    + 3. * t * t * (d - c),
            ),
            _ => return None,
        },
        [a, b, c, d, t, to_t, pre_t, post_t] => match name {
            "cubic_interpolate_in_time" => Float(interpolation::timed(
                [*a, *b, *c, *d],
                *t,
                *to_t,
                *pre_t,
                *post_t,
            )),
            "cubic_interpolate_angle_in_time" => Float(interpolation::timed(
                interpolation::angles(*a, *b, *c, *d),
                *t,
                *to_t,
                *pre_t,
                *post_t,
            )),
            _ => return None,
        },
        _ => return None,
    };
    Some(if argument_error {
        Fault(Box::new(result))
    } else {
        result
    })
}
// Original String::to_int ignores nondigits, stops at '.', and permits a sign
// before the first digit (int("1e2") is 12, unlike float("1e2")).
fn string_integer(text: &str) -> i64 {
    let mut value = 0i64;
    let mut negative = false;
    let mut started = false;
    for c in text.chars() {
        if c == '.' {
            break;
        }
        if let Some(digit) = c.to_digit(10) {
            started = true;
            value = value.saturating_mul(10).saturating_add(digit as i64);
        } else if c == '-' && !started {
            negative = true;
        }
    }
    if negative { -value } else { value }
}
