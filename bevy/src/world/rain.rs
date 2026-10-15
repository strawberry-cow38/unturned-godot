//! Source RainSystem3D, RainRoofMap and wet material presentation.
mod audio;
mod field;
pub(crate) mod lightning;
pub mod roof;
pub(super) mod wet;
mod wind;
use super::WorldState;
use crate::{
    player::{GameSnapshot, PlayerCamera, PlayerCameraUpdate},
    ui::UiState,
};
use bevy::prelude::*;
use wind::WindField;

#[derive(Resource)]
pub struct RainState {
    pub intensity: f32,
    pub wetness: f32,
    pub puddle: f32,
    pub snow: bool,
    pub wind: Vec4,
    pub slant: Vec2,
    pub gravity: Vec3,
    pub daylight: f32,
    pub sea_level: f32,
    pub elapsed: f32,
    wind_field: WindField,
    splash_lean: f32,
}
impl Default for RainState {
    fn default() -> Self {
        Self {
            intensity: 0.,
            wetness: 0.,
            puddle: 0.,
            snow: false,
            wind: Vec4::ZERO,
            slant: Vec2::ZERO,
            gravity: Vec3::new(5., -22., 0.),
            daylight: 1.,
            sea_level: -100000.,
            elapsed: 0.,
            wind_field: default(),
            splash_lean: env("UG_SPLASHLEAN").unwrap_or(-0.35),
        }
    }
}
pub struct RainPlugin;
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct RainUpdate;
impl Plugin for RainPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<RainState>()
            .init_resource::<roof::RoofMap>()
            .add_plugins((
                field::FieldPlugin,
                wet::WetPlugin,
                audio::RainAudioPlugin,
                lightning::LightningPlugin,
            ))
            .add_systems(Startup, roof::setup)
            .add_systems(
                FixedUpdate,
                roof::update.after(bevy_rapier3d::plugin::PhysicsSet::Writeback),
            )
            .add_systems(
                Update,
                conditions.after(PlayerCameraUpdate).in_set(RainUpdate),
            );
    }
}
fn env(name: &str) -> Option<f32> {
    std::env::var(name)
        .ok()?
        .parse::<f32>()
        .ok()
        .filter(|v| v.is_finite())
}
fn conditions(
    mut rain: ResMut<RainState>,
    snapshot: Res<GameSnapshot>,
    time: Res<Time>,
    ui: Res<UiState>,
    camera: Query<&Transform, With<PlayerCamera>>,
    world: Res<WorldState>,
) {
    let weather = &snapshot.0["weather"];
    rain.intensity = weather["rainIntensity"].as_f64().unwrap_or(0.) as f32;
    rain.wetness = weather["rainWetness"].as_f64().unwrap_or(0.) as f32;
    rain.puddle = weather["rainPuddle"].as_f64().unwrap_or(0.) as f32;
    rain.snow = weather["isSnowing"].as_bool().unwrap_or(false);
    rain.sea_level = world.sea_level.unwrap_or(-100000.);
    let dt = if crate::player::simulation_paused(&ui) {
        0.
    } else {
        time.delta_secs()
    };
    rain.elapsed += dt;
    let camera_position = camera.single().ok().map(|p| p.translation);
    let at = camera_position.unwrap_or(Vec3::ZERO);
    let wind = weather["windMain"].as_f64().unwrap_or(0.) as f32;
    let elapsed = rain.elapsed;
    rain.wind = rain.wind_field.advance(at, elapsed, wind, dt);
    let direction = camera_position.map_or(Vec2::X, |_| rain.wind.xy());
    let drift = 5. + 21. * wind.clamp(0., 1.);
    rain.gravity = Vec3::new(direction.x * drift, -22., direction.y * drift);
    rain.slant = rain.gravity.xz() / 22. * rain.splash_lean;
    let day = env("UG_TIME").unwrap_or_else(|| {
        snapshot.0["worldClock"]["timeOfDay"]
            .as_f64()
            .unwrap_or(0.35) as f32
    });
    rain.daylight = crate::environment::daylight(day);
}
pub(super) fn shift_origin(world: &mut World, shift: Vec3) {
    audio::shift_origin(world, shift);
    if let Some(mut roof) = world.get_resource_mut::<roof::RoofMap>() {
        roof.shift(shift);
    }
    if let Some(mut field) = world.get_resource_mut::<field::Field>() {
        field.shift(shift);
    }
}
