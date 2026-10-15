//! PlayerController.ScanWorldLights and Viewmodel.SetWorldLights.
use super::super::{LAYER, ViewmodelCamera};
use crate::player::{PlayerBody, PlayerCamera, WorldSessionEpoch};
use bevy::{camera::visibility::RenderLayers, prelude::*};

/// Original `dynlight` membership. Deliberately not every native light: player
/// eye lamps, muzzle flashes and ThrowableFx lights do not join that source group.
#[derive(Component)]
pub(crate) struct WorldLight;

/// Shared source light level for the inventory stage, even while unarmed.
#[derive(Resource)]
pub(crate) struct NearbyLight {
    pub energy: f32,
    pub color: Color,
}
impl Default for NearbyLight {
    fn default() -> Self {
        Self {
            energy: 0.,
            color: Color::WHITE,
        }
    }
}
#[derive(Component)]
struct Mirror;
#[derive(Resource, Default)]
struct Scan {
    countdown: u8,
    due: bool,
    epoch: u64,
    mirrors: Vec<Entity>,
    camera: Option<Entity>,
}
#[derive(Clone, Copy)]
struct Candidate {
    distance_squared: f32,
    position: Vec3,
    color: Color,
    energy: f32,
    range: f32,
}

pub(super) fn install(app: &mut App) {
    app.init_resource::<NearbyLight>()
        .init_resource::<Scan>()
        .add_systems(Update, diagnostic_light.after(crate::player::PlayerUpdate))
        .add_systems(FixedLast, request_scan)
        .add_systems(
            PostUpdate,
            scan.after(bevy::transform::TransformSystems::Propagate)
                .before(bevy::camera::visibility::VisibilitySystems::VisibilityPropagate)
                .before(bevy::light::SimulationLightSystems::UpdateBounds),
        );
}
fn request_scan(mut scan: ResMut<Scan>, ui: Option<Res<crate::ui::UiState>>) {
    if ui.as_deref().is_some_and(crate::player::simulation_paused) {
        return;
    }
    scan.countdown = scan.countdown.saturating_sub(1);
    if scan.countdown == 0 {
        scan.countdown = 5;
        scan.due = true;
    }
}

/// Match Godot IsVisibleInTree, including an explicitly-visible emitter under
/// a hidden headlight container. Bevy Visibility::Visible overrides its parent.
pub(crate) fn visible_in_tree(
    mut entity: Entity,
    nodes: &Query<(Option<&Visibility>, Option<&ChildOf>)>,
) -> bool {
    while let Ok((visibility, parent)) = nodes.get(entity) {
        if visibility == Some(&Visibility::Hidden) {
            return false;
        }
        let Some(parent) = parent else {
            break;
        };
        entity = parent.parent();
    }
    true
}
fn scan(
    mut commands: Commands,
    mut state: ResMut<Scan>,
    epoch: Option<Res<WorldSessionEpoch>>,
    mut nearby: ResMut<NearbyLight>,
    main: Query<&GlobalTransform, With<PlayerCamera>>,
    camera: Query<(Entity, &GlobalTransform), With<ViewmodelCamera>>,
    world: Query<
        (
            Entity,
            &GlobalTransform,
            Option<&PointLight>,
            Option<&SpotLight>,
        ),
        With<WorldLight>,
    >,
    visibility: Query<(Option<&Visibility>, Option<&ChildOf>)>,
    mirrors: Query<(), With<Mirror>>,
) {
    let epoch = epoch.as_deref().map_or(0, |e| e.0);
    if state.epoch != epoch {
        state.epoch = epoch;
        state.due = true;
        state.countdown = 5;
    }
    if !state.due {
        return;
    }
    let Ok(main) = main.single() else {
        return;
    };
    state.due = false;
    let mut found = Vec::new();
    for (entity, pose, point, spot) in &world {
        if !visible_in_tree(entity, &visibility) {
            continue;
        }
        let (color, intensity, range) = if let Some(point) = point {
            (point.color, point.intensity, point.range)
        } else if let Some(spot) = spot {
            (spot.color, spot.intensity, spot.range)
        } else {
            continue;
        };
        let energy = intensity / crate::hdr::point_lumens(1.);
        let distance_squared = main.translation().distance_squared(pose.translation());
        if energy > 0.01 && distance_squared < range * range * 4. {
            found.push(Candidate {
                distance_squared,
                position: pose.translation(),
                color,
                energy,
                range,
            });
        }
    }
    *nearby = light_level(&found);
    // No viewmodel must not freeze the shared paperdoll light level.
    let Ok((camera, camera_pose)) = camera.single() else {
        return;
    };
    if state.camera != Some(camera) {
        for entity in state.mirrors.drain(..) {
            if mirrors.contains(entity) {
                commands.entity(entity).despawn();
            }
        }
        state.camera = Some(camera);
    } else {
        state.mirrors.retain(|entity| mirrors.contains(*entity));
    }
    found.sort_by(|a, b| a.distance_squared.total_cmp(&b.distance_squared));
    found.truncate(4);
    commands.entity(camera).insert(Visibility::Inherited);
    for (i, light) in found.iter().enumerate() {
        let local =
            Transform::from_translation(main.affine().inverse().transform_point3(light.position));
        // Native world and viewmodel cameras both look along -Z. The source's
        // extra X/Z negation cancels its own 180-degree viewport camera rotation.
        let global = camera_pose.mul_transform(local);
        let lamp = PointLight {
            color: light.color,
            intensity: crate::hdr::point_lumens(light.energy),
            range: light.range,
            shadow_maps_enabled: false,
            ..default()
        };
        if let Some(&entity) = state.mirrors.get(i) {
            commands.entity(entity).insert((
                lamp,
                local,
                global,
                Visibility::Inherited,
                ChildOf(camera),
            ));
        } else {
            let entity = commands
                .spawn((
                    Name::new(format!("Viewmodel world-light mirror {i}")),
                    Mirror,
                    lamp,
                    local,
                    global,
                    Visibility::Inherited,
                    RenderLayers::layer(LAYER),
                    ChildOf(camera),
                ))
                .id();
            state.mirrors.push(entity);
        }
    }
    for &entity in &state.mirrors[found.len()..] {
        commands.entity(entity).insert(Visibility::Hidden);
    }
}
fn light_level(lights: &[Candidate]) -> NearbyLight {
    let mut energy = 0.;
    let mut rgb = Vec3::ZERO;
    for light in lights {
        if light.range <= 0.01 {
            continue;
        }
        let t = (1. - light.distance_squared.sqrt() / light.range).clamp(0., 1.);
        let weight = light.energy * t * t;
        energy += weight;
        // Godot Color stores source sRGB channels here; averaging linear colours
        // would change the hue of mixed lights before the material conversion.
        let color = light.color.to_srgba();
        rgb += Vec3::new(color.red, color.green, color.blue) * weight;
    }
    NearbyLight {
        energy: energy.min(4.),
        color: if energy > 0.001 {
            let rgb = rgb / energy;
            Color::srgb(rgb.x, rgb.y, rgb.z)
        } else {
            Color::WHITE
        },
    }
}

#[derive(Component)]
struct DiagnosticLight;
/// The original WorldBuilder's opt-in red lamp, useful for parity captures.
fn diagnostic_light(
    mut commands: Commands,
    players: Query<Entity, With<PlayerBody>>,
    input: Option<Res<crate::player::PlayerInput>>,
    mut lights: Query<&mut Transform, With<DiagnosticLight>>,
) {
    if std::env::var("UG_TESTLIGHT").as_deref() != Ok("1") {
        return;
    }
    // The native capsule stays axis-aligned; original PlayerController rotates
    // its root by yaw, so reproduce that basis for this source child offset.
    let yaw = input
        .as_deref()
        .map_or(0., |input| input.yaw().to_radians());
    let position = Quat::from_rotation_y(yaw) * Vec3::new(1.1, 0.1, 0.);
    if let Ok(mut pose) = lights.single_mut() {
        pose.translation = position;
        return;
    }
    let Ok(player) = players.single() else {
        return;
    };
    // Physics-only native bodies otherwise have no visibility hierarchy.
    commands.entity(player).insert(Visibility::Inherited);
    commands.spawn((
        Name::new("Source UG_TESTLIGHT"),
        WorldLight,
        DiagnosticLight,
        PointLight {
            range: 6.,
            color: Color::srgb(1., 0.1, 0.1),
            intensity: crate::hdr::point_lumens(8.),
            shadow_maps_enabled: false,
            ..default()
        },
        Transform::from_translation(position),
        ChildOf(player),
    ));
}
#[cfg(test)]
mod tests;
