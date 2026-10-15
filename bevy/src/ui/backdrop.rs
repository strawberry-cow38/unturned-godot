//! Source canvas screen-copy frost. The modal view follows the ordinary HUD,
//! so the backdrop includes that HUD while its own UI remains sharp.
mod render;
#[cfg(test)]
mod tests;
use bevy::{
    asset::embedded_asset,
    camera::{CameraUpdateSystems, Hdr},
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    prelude::*,
    render::{
        RenderApp,
        extract_component::{ExtractComponent, ExtractComponentPlugin, UniformComponentPlugin},
        render_resource::ShaderType,
    },
};

/// Put this on a full-screen modal UI root. The renderer owns its target camera;
/// removing the component restores ordinary UI routing. Fade is already eased
/// by the source menu's animation, not advanced by the renderer.
#[derive(Component, Clone, Copy, Debug)]
pub(crate) struct Backdrop {
    fade: f32,
}
impl Backdrop {
    pub(crate) fn ammo_pie(fade: f32) -> Self {
        Self {
            fade: if fade.is_finite() {
                fade.clamp(0., 1.)
            } else {
                0.
            },
        }
    }
}
#[derive(Component)]
pub(crate) struct ModalCamera;
#[derive(Component)]
struct Routed;
#[derive(Component, Clone, Copy, ExtractComponent, ShaderType)]
#[extract_app(RenderApp)]
struct Settings {
    // source fade, screen mip LOD, spread in physical pixels, reserved
    parameters: Vec4,
}
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct BackdropRouting;
pub(super) struct BackdropPlugin;
impl Plugin for BackdropPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "backdrop/mips.wesl");
        embedded_asset!(app, "backdrop/composite.wesl");
        app.add_plugins((
            ExtractComponentPlugin::<Settings>::default(),
            UniformComponentPlugin::<Settings>::default(),
        ))
        .add_systems(Startup, spawn_camera)
        .add_systems(
            PostUpdate,
            route
                .in_set(BackdropRouting)
                .before(bevy::ui::UiSystems::Layout)
                .before(CameraUpdateSystems),
        );
        render::install(app);
    }
}
fn camera_bundle() -> impl Bundle {
    (
        Name::new("Source modal canvas layer60 camera"),
        Camera2d,
        Camera {
            order: 3,
            clear_color: ClearColorConfig::None,
            ..default()
        },
        ModalCamera,
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
        Settings {
            parameters: Vec4::new(0., 1.8, 3., 0.),
        },
    )
}
fn spawn_camera(mut commands: Commands) {
    commands.spawn(camera_bundle());
}
fn route(
    mut commands: Commands,
    cameras: Query<Entity, With<ModalCamera>>,
    roots: Query<(Entity, &Backdrop, Option<&UiTargetCamera>)>,
    retired: Query<Entity, (With<Routed>, Without<Backdrop>)>,
) {
    let camera = cameras
        .iter()
        .next()
        .unwrap_or_else(|| commands.spawn(camera_bundle()).id());
    let mut fade = 0_f32;
    for (entity, backdrop, target) in &roots {
        fade = fade.max(backdrop.fade);
        if target.is_none_or(|target| target.0 != camera) {
            commands
                .entity(entity)
                .insert((UiTargetCamera(camera), Routed));
        }
    }
    for entity in &retired {
        commands.entity(entity).remove::<(UiTargetCamera, Routed)>();
    }
    commands.entity(camera).insert(Settings {
        parameters: Vec4::new(fade, 1.8, 3., 0.),
    });
}
