//! Separate source canvas layers5/6/9 from HUD10's screen-copy boundary.
use bevy::{
    camera::{CameraUpdateSystems, Hdr},
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    prelude::*,
};

/// World/viewmodel composite and night vision finish here, before source layer9
/// UI (e.g. the departing Information panel) and the next camera's HUD effects.
#[derive(Component)]
pub(crate) struct SceneCanvas;
#[derive(Component)]
pub(super) struct BelowHud;
#[derive(Component)]
struct Routed;
pub(crate) const HUD_ORDER: isize = 3;
pub(crate) const DASHBOARD_ORDER: isize = 4;
pub(crate) const TOOL_ORDER: isize = 5;
pub(crate) const MODAL_ORDER: isize = 6;
pub(crate) const CONSOLE_ORDER: isize = 7;
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct CanvasRouting;
pub(crate) struct LayersPlugin;
impl Plugin for LayersPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Startup, spawn).add_systems(
            PostUpdate,
            route
                .in_set(CanvasRouting)
                .after(super::backdrop::BackdropRouting)
                .before(CameraUpdateSystems)
                .before(bevy::ui::UiSystems::Layout),
        );
    }
}
fn bundle() -> impl Bundle {
    (
        Name::new("Source pre-HUD canvas layers5/6/9"),
        SceneCanvas,
        Camera2d,
        Camera {
            order: 2,
            clear_color: ClearColorConfig::None,
            ..default()
        },
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
    )
}
fn spawn(mut commands: Commands) {
    commands.spawn(bundle());
}
fn route(
    mut commands: Commands,
    scene: Query<Entity, With<SceneCanvas>>,
    mut hud: Query<&mut Camera, With<IsDefaultUiCamera>>,
    roots: Query<
        (
            Entity,
            Option<&BelowHud>,
            Has<Routed>,
            Option<&UiTargetCamera>,
        ),
        Or<(With<BelowHud>, With<Routed>)>,
    >,
) {
    let camera = scene
        .single()
        .unwrap_or_else(|_| commands.spawn(bundle()).id());
    for mut hud in &mut hud {
        hud.order = HUD_ORDER;
    }
    for (entity, below, routed, target) in &roots {
        if below.is_some() {
            if target.is_none_or(|target| target.0 != camera) {
                commands.entity(entity).insert(UiTargetCamera(camera));
            }
            if !routed {
                commands.entity(entity).insert(Routed);
            }
        } else {
            commands.entity(entity).remove::<(UiTargetCamera, Routed)>();
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn layer9_survives_camera_replacement_and_returns_to_default_hud_on_removal() {
        let mut app = App::new();
        app.add_systems(PostUpdate, route);
        let hud = app
            .world_mut()
            .spawn((Camera::default(), IsDefaultUiCamera))
            .id();
        let panel = app.world_mut().spawn((Node::default(), BelowHud)).id();
        app.update();
        let first = app.world().get::<UiTargetCamera>(panel).unwrap().0;
        assert_eq!(app.world().get::<Camera>(first).unwrap().order, 2);
        assert_eq!(app.world().get::<Camera>(hud).unwrap().order, HUD_ORDER);
        app.world_mut().entity_mut(first).despawn();
        app.update();
        let next = app.world().get::<UiTargetCamera>(panel).unwrap().0;
        assert_ne!(next, first);
        app.world_mut().entity_mut(panel).remove::<BelowHud>();
        app.update();
        assert!(app.world().get::<UiTargetCamera>(panel).is_none());
        assert!(
            2 < HUD_ORDER
                && HUD_ORDER < DASHBOARD_ORDER
                && DASHBOARD_ORDER < TOOL_ORDER
                && TOOL_ORDER < MODAL_ORDER
                && MODAL_ORDER < CONSOLE_ORDER
        );
    }
}
