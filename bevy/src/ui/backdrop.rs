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
    pub(super) fade: f32,
    style: Style,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Style {
    AmmoPie,
    Dashboard,
}
impl Backdrop {
    pub(crate) fn ammo_pie(fade: f32) -> Self {
        Self::new(fade, Style::AmmoPie)
    }
    pub(crate) fn dashboard(fade: f32) -> Self {
        Self::new(fade, Style::Dashboard)
    }
    fn new(fade: f32, style: Style) -> Self {
        Self {
            fade: if fade.is_finite() {
                fade.clamp(0., 1.)
            } else {
                0.
            },
            style,
        }
    }
}
#[derive(Component)]
pub(crate) struct ModalCamera;
#[derive(Component)]
struct DashboardCamera;
/// Original HUD vitals are layer12, above the dashboard's layer11 frost and
/// panels. When no dashboard is present they stay in the ordinary HUD, so an
/// AmmoPie layer60 screen copy still includes them.
#[derive(Component)]
pub(super) struct AboveDashboard;
pub(crate) const CONSOLE_ORDER: isize = super::layers::CONSOLE_ORDER;
#[derive(Component)]
struct Routed;
#[derive(Component, Clone, Copy, ExtractComponent, ShaderType)]
#[extract_app(RenderApp)]
struct Settings {
    // source fade, screen mip LOD, spread in physical pixels, style selector
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
fn camera_bundle(style: Style) -> impl Bundle {
    (
        Name::new(if style == Style::AmmoPie {
            "Source modal canvas layer60 camera"
        } else {
            "Source dashboard canvas layers11/12 camera"
        }),
        Camera2d,
        Camera {
            order: if style == Style::AmmoPie { super::layers::MODAL_ORDER } else { super::layers::DASHBOARD_ORDER },
            clear_color: ClearColorConfig::None,
            ..default()
        },
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
        settings(style, 0.),
    )
}
fn spawn_camera(mut commands: Commands) {
    commands.spawn((camera_bundle(Style::AmmoPie), ModalCamera));
    commands.spawn((camera_bundle(Style::Dashboard), DashboardCamera));
}
fn settings(style: Style, fade: f32) -> Settings {
    Settings {
        parameters: match style {
            Style::AmmoPie => Vec4::new(fade, 1.8, 3., 0.),
            Style::Dashboard => Vec4::new(fade, 1.5, 3., 1.),
        },
    }
}
fn route(
    mut commands: Commands,
    cameras: Query<Entity, With<ModalCamera>>,
    dashboards: Query<Entity, With<DashboardCamera>>,
    roots: Query<(Entity, &Backdrop, Option<&UiTargetCamera>)>,
    vitals: Query<(Entity, Option<&UiTargetCamera>), With<AboveDashboard>>,
    retired: Query<Entity, (With<Routed>, Without<Backdrop>, Without<AboveDashboard>)>,
) {
    let camera = cameras.iter().next().unwrap_or_else(|| {
        commands
            .spawn((camera_bundle(Style::AmmoPie), ModalCamera))
            .id()
    });
    let dashboard = dashboards.iter().next().unwrap_or_else(|| {
        commands
            .spawn((camera_bundle(Style::Dashboard), DashboardCamera))
            .id()
    });
    let mut modal_fade = 0_f32;
    let mut dashboard_fade = 0_f32;
    let mut dashboard_active = false;
    for (entity, backdrop, target) in &roots {
        let target_camera = match backdrop.style {
            Style::AmmoPie => {
                modal_fade = modal_fade.max(backdrop.fade);
                camera
            }
            Style::Dashboard => {
                dashboard_active = true;
                dashboard_fade = dashboard_fade.max(backdrop.fade);
                dashboard
            }
        };
        if target.is_none_or(|target| target.0 != target_camera) {
            commands
                .entity(entity)
                .insert((UiTargetCamera(target_camera), Routed));
        }
    }
    for (entity, target) in &vitals {
        if dashboard_active {
            if target.is_none_or(|target| target.0 != dashboard) {
                commands
                    .entity(entity)
                    .insert((UiTargetCamera(dashboard), Routed));
            }
        } else if target.is_some() {
            commands.entity(entity).remove::<(UiTargetCamera, Routed)>();
        }
    }
    for entity in &retired {
        commands.entity(entity).remove::<(UiTargetCamera, Routed)>();
    }
    commands
        .entity(camera)
        .insert(settings(Style::AmmoPie, modal_fade));
    commands
        .entity(dashboard)
        .insert(settings(Style::Dashboard, dashboard_fade));
}
