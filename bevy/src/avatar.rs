//! The local player's original full-body rig and first-person clothes. Inventory
//! and the owner collision body remain authoritative outside this renderer.
use crate::{
    character::{Appearance, CharacterLayers, CharacterUpdate},
    player::{
        GameSnapshot, PlayerBody, PlayerCameraUpdate, PlayerInput, PlayerUpdate, WorldSessionEpoch,
        simulation_paused,
    },
    rig::{RigAnimation, RigLibrary, RigPlayback, spawn_rig},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState},
    world::WorldConfig,
};
use bevy::{
    camera::visibility::RenderLayers, input::mouse::AccumulatedMouseMotion,
    mesh::skinning::SkinnedMeshInverseBindposes, prelude::*,
};
use serde_json::Value;
pub mod camera;
mod death;
mod held;
mod pose;
pub(crate) mod remotes;
pub use camera::Perspective;
pub use held::AvatarMuzzleUpdate;

pub struct AvatarPlugin;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct AvatarUpdate;
impl Plugin for AvatarPlugin {
    fn build(&self, app: &mut App) {
        app.add_plugins(remotes::RemotePlayersPlugin)
            .init_resource::<Perspective>()
            .init_resource::<AvatarState>()
            .init_resource::<held::Mirror>()
            .init_resource::<death::Death>()
            .add_systems(
                Update,
                controls.after(crate::ui::UiUpdate).before(PlayerUpdate),
            )
            .add_systems(
                Update,
                (spawn, update)
                    .chain()
                    .after(PlayerCameraUpdate)
                    .before(RigAnimation)
                    .in_set(AvatarUpdate),
            )
            .add_systems(
                Update,
                death::update.after(AvatarUpdate).before(RigAnimation),
            )
            .add_systems(
                Update,
                (held::mirror, held::animate, pose::apply)
                    .chain()
                    .after(RigAnimation)
                    .before(CharacterUpdate),
            )
            .add_systems(Update, tag_lights.after(CharacterUpdate))
            .add_systems(
                PostUpdate,
                held::muzzle
                    .in_set(AvatarMuzzleUpdate)
                    .after(bevy::transform::TransformSystems::Propagate),
            );
    }
}
/// Layer15 is source OwnBodyLayer. Only your first-person camera excludes it;
/// the sun and future security/remote cameras can still see the whole body.
pub const OWN_BODY_LAYER: usize = 15;
#[derive(Component)]
pub struct LocalAvatar;
#[derive(Component)]
pub struct FirstPersonArms;
#[derive(Resource, Default)]
struct AvatarState {
    root: Option<Entity>,
    epoch: u64,
}
/// Third-person weapon attachments populate this; light and shot presentation
/// can share the same actual hook rather than guessing a muzzle offset.
#[derive(Component)]
pub struct AvatarMuzzle(pub Vec3, pub Vec3);

fn controls(
    time: Res<Time>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    motion: Res<AccumulatedMouseMotion>,
    scroll: Option<Res<bevy::input::mouse::AccumulatedMouseScroll>>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    occupancy: Res<crate::vehicle::Occupancy>,
    mut view: ResMut<Perspective>,
) {
    if ui.captures_input() || !model.connected || !model.alive {
        return;
    }
    if settings.just_pressed(GameAction::ToggleFirstPerson, &keys, &mouse) {
        view.first_person = !view.first_person;
    }
    let seated = occupancy.seat;
    let vehicle = seated.map(|seat| seat.net_id);
    if view.vehicle != vehicle {
        view.vehicle = vehicle;
        if vehicle.is_some() {
            view.drive = Vec2::new(0., 15.);
            view.fly = Vec2::ZERO;
        }
    }
    let free = settings.pressed(GameAction::FreeLook, &keys, &mouse);
    if let Some(seat) = seated {
        if !view.first_person {
            if let Some(scroll) = scroll {
                view.drive_zoom =
                    (view.drive_zoom * 0.88_f32.powf(scroll.delta.y)).clamp(0.35, 1.8);
            }
            if !seat.aircraft {
                view.drive.x -= motion.delta.x * settings.mouse_sensitivity;
                view.drive.y =
                    (view.drive.y + motion.delta.y * settings.mouse_sensitivity).clamp(-25., 70.);
            } else if free {
                view.fly.x = (view.fly.x - motion.delta.x * settings.mouse_sensitivity + 180.)
                    .rem_euclid(360.)
                    - 180.;
                view.fly.y =
                    (view.fly.y + motion.delta.y * settings.mouse_sensitivity).clamp(-70., 70.);
            } else {
                view.fly = (view.fly * (1. - (7. * time.delta_secs()).min(1.)))
                    .map(|n| if n.abs() < 0.05 { 0. } else { n });
            }
        }
    } else {
        if free {
            let delta = motion.delta * settings.mouse_sensitivity;
            if view.first_person {
                view.head.x = (view.head.x - delta.x).clamp(-150., 150.);
                view.head.y = (view.head.y
                    + delta.y * if settings.invert_look_y { 1. } else { -1. })
                .clamp(-80., 80.);
            } else {
                view.orbit.x = (view.orbit.x - delta.x + 180.).rem_euclid(360.) - 180.;
                view.orbit.y = (view.orbit.y + delta.y).clamp(-75., 60.);
            }
        } else {
            let k = (7. * time.delta_secs()).min(1.);
            let relax = |v: Vec2| (v * (1. - k)).map(|n| if n.abs() < 0.05 { 0. } else { n });
            view.head = relax(view.head);
            view.orbit = relax(view.orbit);
        }
    }
}
#[allow(clippy::too_many_arguments)]
fn spawn(
    mut commands: Commands,
    mut state: ResMut<AvatarState>,
    epoch: Res<WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    config: Res<WorldConfig>,
    mut rigs: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    if state.epoch != epoch.0 {
        if let Some(root) = state.root.take() {
            commands.entity(root).try_despawn();
        }
        state.epoch = epoch.0;
    }
    if state.root.is_some() || !snapshot.0["connected"].as_bool().unwrap_or(false) {
        return;
    }
    match spawn_rig(
        &mut commands,
        &mut rigs,
        &mut meshes,
        &mut binds,
        &config.content_root.join("rig.json"),
        materials.add(StandardMaterial::default()),
        Transform::IDENTITY,
        false,
        "Idle_Stand",
    ) {
        Ok(root) => {
            commands.entity(root).insert((
                Name::new("Local player appearance"),
                LocalAvatar,
                Appearance::default(),
                CharacterLayers(RenderLayers::layer(OWN_BODY_LAYER)),
                pose::TorsoPose::default(),
            ));
            state.root = Some(root);
        }
        Err(e) => error!("Player body rig: {e}"),
    }
}
fn outfit(state: &Value, arms: bool, catalog: &crate::entities::ItemCatalog) -> Appearance {
    outfit_parts(
        &state["inventory"]["worn"],
        &state["inventory"]["wornQuality"],
        state["player"]["profile"]["face"].as_u64().unwrap_or(0).min(31) as u8,
        arms,
        catalog,
    )
}
fn outfit_parts(
    worn: &Value,
    quality: &Value,
    face: u8,
    arms: bool,
    catalog: &crate::entities::ItemCatalog,
) -> Appearance {
    let id = |slot: &str| worn[slot].as_i64().filter(|n| *n > 0).unwrap_or(-1) as i32;
    let wear = |slot: &str| {
        if !catalog
            .items
            .get(&(id(slot) as u16))
            .is_some_and(|item| item.has_condition)
        {
            return 0.;
        }
        let q = quality[slot].as_f64().unwrap_or(100.) as f32;
        ((1. - q.clamp(0., 100.) / 100.) * 50.).round_ties_even() / 50.
    };
    Appearance {
        shirt: id("shirt"),
        pants: id("pants"),
        gear: if arms {
            Vec::new()
        } else {
            ["hat", "glasses", "mask", "vest", "backpack"]
                .map(id)
                .into_iter()
                .filter(|id| *id > 0)
                .collect()
        },
        face: if arms { None } else { Some(face) },
        wear: Vec2::new(wear("shirt"), wear("pants")),
        ..default()
    }
}
fn locomotion(speed: f32, stance: &str) -> &'static str {
    match (stance, speed < 0.2, speed < 4.5) {
        ("crouch", true, _) => "Idle_Crouch",
        ("crouch", false, _) => "Move_Crouch",
        ("prone", true, _) => "Idle_Prone",
        ("prone", false, _) => "Move_Prone",
        ("swim", true, _) => "Idle_Swim",
        ("swim", false, _) => "Move_Swim",
        (_, true, _) => "Idle_Stand",
        (_, false, true) => "Move_Walk",
        _ => "Move_Run",
    }
}
#[allow(clippy::too_many_arguments)]
fn update(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    catalog: Res<crate::entities::ItemCatalog>,
    ui: Res<UiState>,
    view: Res<Perspective>,
    input: Res<PlayerInput>,
    lean: Option<Res<crate::player::lean::LeanState>>,
    body: Query<(&Transform, &PlayerBody), Without<LocalAvatar>>,
    occupancy: Res<crate::vehicle::Occupancy>,
    prop: Res<crate::world::interactables::PropOccupancy>,
    mut avatar: Query<
        (
            &mut Transform,
            &mut RigPlayback,
            &mut Appearance,
            &mut CharacterLayers,
            &mut Visibility,
            &mut pose::TorsoPose,
        ),
        With<LocalAvatar>,
    >,
    arms: Query<(Entity, Option<&Appearance>), (With<FirstPersonArms>, Without<LocalAvatar>)>,
) {
    let appearance = outfit(&snapshot.0, false, &catalog);
    let sleeves = outfit(&snapshot.0, true, &catalog);
    for (entity, old) in &arms {
        if old != Some(&sleeves) {
            commands.entity(entity).insert(sleeves.clone());
        }
    }
    let Ok((physical, body)) = body.single() else {
        return;
    };
    for (mut transform, mut rig, mut old, mut layers, mut visibility, mut pose) in &mut avatar {
        if *old != appearance {
            *old = appearance.clone();
        }
        let wanted = RenderLayers::layer(if view.first_person { OWN_BODY_LAYER } else { 0 });
        if layers.0 != wanted {
            layers.0 = wanted;
        }
        let alive = snapshot.0["player"]["alive"].as_bool().unwrap_or(true);
        *visibility = if alive {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        rig.paused = simulation_paused(&ui) || !alive;
        if let Some(seat) = occupancy.seat {
            *transform = seat.body_pose;
            rig.play(
                if seat.driver {
                    "Idle_Drive"
                } else {
                    "Idle_Sit"
                },
                true,
            );
            *pose = pose::TorsoPose {
                pitch: input.pitch(),
                spine_scale: 0.,
                lean: 0.,
            };
        } else if let Some(seat) = prop.seat {
            *transform = seat.body_pose;
            rig.play("Idle_Sit", true);
            *pose = pose::TorsoPose {
                pitch: input.pitch(),
                ..default()
            };
        } else {
            let yaw = Quat::from_rotation_y(input.yaw().to_radians());
            *transform = Transform::from_translation(
                physical.translation
                    + if view.first_person {
                        yaw * Vec3::Z * 0.42
                    } else {
                        Vec3::ZERO
                    },
            )
            .with_rotation(yaw);
            rig.play(
                locomotion(body.velocity().xz().length(), body.stance()),
                true,
            );
            *pose = pose::TorsoPose {
                pitch: input.pitch(),
                lean: lean.as_ref().map_or(0., |lean| lean.angle.to_degrees()),
                ..default()
            };
        }
    }
}
#[cfg(test)]
mod tests;

fn tag_lights(
    mut commands: Commands,
    gear: Query<(Entity, &crate::character::GearItem), Added<crate::character::GearItem>>,
    parents: Query<&ChildOf>,
    local: Query<(), With<LocalAvatar>>,
) {
    for (entity, item) in &gear {
        if !matches!(item.0, 1199 | 334 | 1044) {
            continue;
        }
        let mut at = entity;
        for _ in 0..32 {
            if local.get(at).is_ok() {
                commands
                    .entity(entity)
                    .insert(crate::viewmodel::lights::LightSurface(item.0 as u16));
                break;
            }
            let Ok(parent) = parents.get(at) else {
                break;
            };
            at = parent.parent();
        }
    }
}
