//! Shared GameAudio/PlayerController/RemotePlayers stride and material rules.
use crate::impact::surfaces::Surface;
use crate::sound::Bank;
use bevy::prelude::*;
use std::{collections::HashMap, path::Path};

pub(super) fn foot_surface(surface: Surface) -> &'static str {
    match surface {
        Surface::Grass => "grass",
        Surface::Dirt => "dirt",
        Surface::Metal => "metallow",
        Surface::Wood => "wood",
        Surface::Sand => "sand",
        Surface::Water => "water",
        Surface::Gravel => "gravel",
        Surface::Snow => "snow",
        Surface::Ice => "ice",
        _ => "concrete",
    }
}
pub(super) fn land_surface(surface: Surface) -> &'static str {
    if surface == Surface::Metal {
        "metal"
    } else {
        foot_surface(surface)
    }
}
pub(super) fn stride(stance: &str) -> f32 {
    match stance {
        "sprint" => 2.,
        "crouch" => 1.,
        "prone" => 0.9,
        _ => 1.5,
    }
}
pub(super) fn step_db(stance: &str) -> f32 {
    -4. + match stance {
        "prone" => -14.,
        "crouch" => -8.,
        "sprint" => 0.,
        _ => -3.,
    }
}
pub(super) fn advance(acc: &mut f32, distance: f32, stride: f32) -> bool {
    *acc += distance;
    if *acc < stride {
        return false;
    }
    *acc = 0.; // Source emits once per tick, with no burst after a long frame.
    true
}
pub(super) fn landing_db(vertical_speed: f32) -> f32 {
    (-9. + (-vertical_speed - 2.5) * 1.2).clamp(-9., 2.)
}
pub(super) fn swim_bank(speed: f32) -> &'static str {
    if speed > 3.6 {
        "heavywading"
    } else if speed > 2. {
        "mediumwading"
    } else {
        "lightwading"
    }
}

#[derive(Resource, Default)]
pub(super) struct Banks(HashMap<(&'static str, String), Bank>);
impl Banks {
    pub fn load(root: &Path) -> Self {
        let mut result = Self::default();
        for material in [
            "concrete", "grass", "dirt", "metallow", "wood", "sand", "water", "gravel", "snow",
            "ice",
        ] {
            for gait in ["walk", "run"] {
                let key = format!("{material}_{gait}");
                result.0.insert(
                    ("footsteps", key.clone()),
                    Bank::load(root, "footsteps", &key),
                );
            }
            let key = if material == "metallow" {
                "metal"
            } else {
                material
            };
            result
                .0
                .insert(("landing", key.into()), Bank::load(root, "landing", key));
        }
        for key in ["lightwading", "mediumwading", "heavywading"] {
            result
                .0
                .insert(("swim", key.into()), Bank::load(root, "swim", key));
        }
        result
    }
    pub fn pick(&mut self, folder: &'static str, key: &str, seed: u64) -> Option<String> {
        self.0
            .get_mut(&(folder, key.into()))?
            .pick(seed)
            .map(str::to_owned)
    }
    pub fn step(&mut self, surface: Surface, run: bool, seed: u64) -> Option<String> {
        let material = foot_surface(surface);
        (if run {
            self.pick("footsteps", &format!("{material}_run"), seed)
        } else {
            None
        })
        .or_else(|| self.pick("footsteps", &format!("{material}_walk"), seed))
        .or_else(|| {
            self.pick(
                "footsteps",
                if run { "concrete_run" } else { "concrete_walk" },
                seed,
            )
        })
    }
}

// PuddleField.cs mirrors the shader's point sample. Deliberately no fwidth:
// the source takes the crisp centre of the antialiased shoreline on the CPU.
fn fract(v: f32) -> f32 {
    v - v.floor()
}
fn gradient(p: Vec2) -> Vec2 {
    let mut v = Vec3::new(
        fract(p.x * 0.1031),
        fract(p.y * 0.1031),
        fract(p.x * 0.1031),
    );
    let d = v.x * (v.y + 33.33) + v.y * (v.z + 33.33) + v.z * (v.x + 33.33);
    v += Vec3::splat(d);
    let angle = fract((v.x + v.y) * v.z) * std::f32::consts::TAU;
    Vec2::new(angle.cos(), angle.sin())
}
fn noise(p: Vec2) -> f32 {
    let i = p.floor();
    let f = p - i;
    let u = f * f * f * (f * (f * 6. - Vec2::splat(15.)) + Vec2::splat(10.));
    let a = gradient(i).dot(f);
    let b = gradient(i + Vec2::X).dot(f - Vec2::X);
    let c = gradient(i + Vec2::Y).dot(f - Vec2::Y);
    let d = gradient(i + Vec2::ONE).dot(f - Vec2::ONE);
    (a + (b - a) * u.x + ((c + (d - c) * u.x) - (a + (b - a) * u.x)) * u.y) * 0.8 + 0.5
}
pub(super) fn puddle_wet(shader_xz: Vec2, level: f32) -> bool {
    if level <= 0.01 {
        return false;
    }
    let depth = noise(shader_xz * 0.19) * 0.72 + noise(shader_xz * 0.62) * 0.28;
    let line = 0.1 + (0.46 - 0.1) * level.clamp(0., 1.);
    let t = (level / 0.12).clamp(0., 1.);
    depth < line && t * t * (3. - 2. * t) > 0.5
}
