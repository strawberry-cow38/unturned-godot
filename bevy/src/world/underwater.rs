//! Original Underwater.cs eye-height hysteresis and underwater.gdshader optics.
//! The world camera receives the pass; first-person arms and UI composite later.
mod render;

#[derive(bevy::prelude::SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct UnderwaterPass;

use super::WorldState;
use crate::player::{GameSnapshot, PlayerCamera, PlayerCameraUpdate};
use bevy::{
    asset::embedded_asset,
    core_pipeline::prepass::DepthPrepass,
    prelude::*,
    render::{
        RenderApp,
        extract_component::{ExtractComponent, ExtractComponentPlugin, UniformComponentPlugin},
        render_resource::ShaderType,
    },
};

pub const WOBBLE: f32 = 0.006;
pub const WOBBLE_SPEED: f32 = 1.4;
const ON_DEPTH: f32 = 0.06;
const CLEAR_EXTINCTION: Vec3 = Vec3::new(0.19, 0.048, 0.028);
const STORM_EXTINCTION: Vec3 = Vec3::new(0.34, 0.17, 0.13);

/// Shared with later outline compositing so its silhouette follows the same warp.
#[derive(Resource, Default, Debug)]
pub struct UnderwaterState {
    pub active: bool,
    pub depth: f32,
}
impl UnderwaterState {
    fn drive(&mut self, below: Option<f32>, forced: Option<f32>) {
        if let Some(depth) = forced {
            self.active = true;
            self.depth = depth;
            return;
        }
        let Some(depth) = below.filter(|depth| depth.is_finite() && *depth > 0.) else {
            self.active = false;
            self.depth = 0.;
            return;
        };
        if depth >= ON_DEPTH {
            self.active = true;
        }
        self.depth = if self.active { depth } else { 0. };
    }
}

#[derive(Resource)]
struct OpticsConfig {
    forced: Option<f32>,
    optics: f32,
}
impl Default for OpticsConfig {
    fn default() -> Self {
        Self {
            forced: std::env::var("UG_UNDERWATER")
                .ok()
                .filter(|s| !s.is_empty())
                .map(|s| {
                    s.parse::<f32>()
                        .ok()
                        .filter(|v| v.is_finite() && *v > 0.)
                        .unwrap_or(4.)
                }),
            optics: std::env::var("UG_WATEROPTICS")
                .ok()
                .and_then(|s| s.parse::<f32>().ok())
                .filter(|v| v.is_finite() && *v >= 0.)
                .unwrap_or(1.),
        }
    }
}

#[derive(Component, Clone, Copy, ExtractComponent, ShaderType)]
#[extract_app(RenderApp)]
struct OpticsUniform {
    clip_to_view: Mat4,
    /// eye depth, source optics A/B control, elapsed seconds, binary submersion
    state: Vec4,
    extinction: Vec4,
    /// Shared warp amplitude and speed; source owns these outside its shader.
    warp: Vec4,
}

pub struct UnderwaterPlugin;
impl Plugin for UnderwaterPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "underwater.wesl");
        embedded_asset!(app, "underwater_mip.wesl");
        app.init_resource::<UnderwaterState>()
            .init_resource::<OpticsConfig>()
            .add_plugins((
                ExtractComponentPlugin::<OpticsUniform>::default(),
                UniformComponentPlugin::<OpticsUniform>::default(),
            ))
            .add_systems(Update, drive.after(PlayerCameraUpdate));
        render::install(app);
    }
}
fn extinction(rain: f32) -> Vec3 {
    CLEAR_EXTINCTION.lerp(STORM_EXTINCTION, rain.clamp(0., 1.))
}
fn drive(
    mut commands: Commands,
    time: Res<Time>,
    world: Res<WorldState>,
    snapshot: Res<GameSnapshot>,
    config: Res<OpticsConfig>,
    mut state: ResMut<UnderwaterState>,
    cameras: Query<(Entity, &Transform, &Projection), With<PlayerCamera>>,
) {
    let Ok((camera, pose, projection)) = cameras.single() else {
        state.drive(None, None);
        return;
    };
    state.drive(
        world.sea_level.map(|sea| sea - pose.translation.y),
        config.forced,
    );
    let rain = snapshot.0["weather"]["rainIntensity"]
        .as_f64()
        .unwrap_or(0.) as f32;
    commands.entity(camera).insert((
        DepthPrepass,
        OpticsUniform {
            clip_to_view: projection.get_clip_from_view().inverse(),
            state: Vec4::new(
                state.depth,
                config.optics,
                time.elapsed_secs(),
                if state.active { 1. } else { 0. },
            ),
            extinction: extinction(rain).extend(0.),
            warp: Vec4::new(WOBBLE, WOBBLE_SPEED, 0., 0.),
        },
    ));
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn submersion_uses_eye_hysteresis_and_clears_on_world_teardown() {
        let mut state = UnderwaterState::default();
        for depth in [0., 0.02, 0.059] {
            state.drive(Some(depth), None);
            assert!(!state.active);
        }
        state.drive(Some(0.06), None);
        assert!(state.active);
        state.drive(Some(0.001), None);
        assert!(
            state.active,
            "a crest inside the hysteresis band must not flicker"
        );
        state.drive(Some(0.), None);
        assert!(!state.active);
        state.drive(Some(2.), None);
        state.drive(None, None);
        assert!(!state.active);
        assert_eq!(state.depth, 0.);
        state.drive(None, Some(4.));
        assert!(state.active);
        assert_eq!(state.depth, 4.);
    }
    #[test]
    fn source_water_absorbs_red_first_and_storm_sediment_increases_green_loss() {
        let clear = extinction(0.);
        let storm = extinction(1.);
        assert_eq!(clear, CLEAR_EXTINCTION);
        assert!((storm - STORM_EXTINCTION).length() < 0.000001);
        assert!((extinction(0.5) - (clear + storm) * 0.5).length() < 0.000001);
        let transmitted = (-clear * 25.).exp();
        assert!((transmitted.y - 0.3011942).abs() < 0.000001);
        assert!(transmitted.x < transmitted.y && transmitted.y < transmitted.z);
        assert!((-storm.y * 25.).exp() < transmitted.y);
    }
    #[test]
    fn reverse_depth_reconstruction_keeps_near_geometry_sharp() {
        let projection = Mat4::perspective_infinite_reverse_rh(70_f32.to_radians(), 16. / 9., 0.1);
        for distance in [0.25, 1., 12., 100.] {
            let clip = projection * Vec4::new(0.2 * distance, -0.1 * distance, -distance, 1.);
            let view = projection.inverse() * (clip / clip.w);
            assert!((-view.z / view.w - distance).abs() < 0.0001);
        }
    }
    #[test]
    fn camera_driver_publishes_optics_only_on_the_world_camera() {
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .insert_resource(WorldState {
                sea_level: Some(25.6),
                ..default()
            })
            .insert_resource(OpticsConfig {
                forced: None,
                optics: 1.,
            })
            .init_resource::<GameSnapshot>()
            .init_resource::<UnderwaterState>()
            .add_systems(Update, drive);
        let camera = app
            .world_mut()
            .spawn((
                PlayerCamera,
                Transform::from_xyz(0., 24., 0.),
                Projection::default(),
            ))
            .id();
        let arms = app
            .world_mut()
            .spawn((Transform::default(), Projection::default()))
            .id();
        app.update();
        let uniform = app.world().get::<OpticsUniform>(camera).unwrap();
        assert_eq!(uniform.state.w, 1.);
        assert!((uniform.state.x - 1.6).abs() < 0.00001);
        assert_eq!(uniform.warp.xy(), Vec2::new(WOBBLE, WOBBLE_SPEED));
        assert!(app.world().get::<DepthPrepass>(camera).is_some());
        assert!(app.world().get::<OpticsUniform>(arms).is_none());
        app.world_mut()
            .get_mut::<Transform>(camera)
            .unwrap()
            .translation
            .y = 26.;
        app.update();
        assert_eq!(
            app.world().get::<OpticsUniform>(camera).unwrap().state.w,
            0.
        );
        assert!(!app.world().resource::<UnderwaterState>().active);
    }
}
