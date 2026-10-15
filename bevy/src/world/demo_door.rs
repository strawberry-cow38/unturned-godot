//! Door.cs fallback furniture. Source maps keep this OFF; --demo-furniture
//! opts into the same independently numbered door1 on every matching peer.
use super::{WorldConfig, WorldGeometry, WorldState, origin::WorldOrigin};
use crate::{
    bridge::CoreBridge,
    entities::{ApplyInteractions, CollectInteractions, InteractionFocus, WorldServicesReady},
    player::{GameSnapshot, PlayerBody, PlayerCamera, PlayerCameraUpdate, WorldSessionEpoch},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState},
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};

mod keys;

const SIZE: Vec3 = Vec3::new(1., 2., 0.12);
const ID: u32 = 1;
const KEY: &str = "source-demo-door";
const SWING_SECONDS: f32 = 0.45;
#[derive(Clone, Copy)]
struct Definition {
    base: Vec3,
    yaw: f32,
}
impl Definition {
    fn hinge(self) -> Vec3 {
        self.base + Quat::from_rotation_y(self.yaw) * Vec3::NEG_X * SIZE.x * 0.5
    }
    fn pose(self, swing: f32) -> Transform {
        let rotation = Quat::from_rotation_y(self.yaw + std::f32::consts::FRAC_PI_2 * swing);
        Transform::from_translation(
            self.hinge() + rotation * Vec3::X * SIZE.x * 0.5 + Vec3::Y * SIZE.y * 0.5,
        )
        .with_rotation(rotation)
    }
    fn registration(self) -> Value {
        json!({"doors":[{"netId":ID,"position":self.base.to_array(),"hingeOffset":(self.hinge()-self.base).to_array(),"owner":1,
            "geometryKey":KEY}]})
    }
    fn geometry(self, swing: f32) -> Value {
        let data = super::ladder_box_geometry(&crate::content::ObjData {
            min: -SIZE * 0.5,
            max: SIZE * 0.5,
            ..default()
        });
        let pose = self.pose(swing);
        let vertices: Vec<_> = data
            .positions
            .into_iter()
            .map(|p| pose.transform_point(Vec3::from_array(p)).to_array())
            .collect();
        json!({"key":KEY,"meshes":[{"vertices":vertices}]})
    }
}
#[derive(Component)]
struct Door {
    material: Handle<StandardMaterial>,
}
#[derive(Resource, Default)]
struct Demo {
    key: Option<(u64, String)>,
    definition: Option<Definition>,
    entity: Option<Entity>,
    registered: bool,
    seen: bool,
    swing: f32,
    exported: Option<(u64, u32)>,
    last_error: Option<String>,
}
#[derive(Resource, Default)]
struct Hold {
    epoch: u64,
    elapsed: Option<f32>,
}
pub(super) struct DemoDoorPlugin;
impl Plugin for DemoDoorPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<keys::Bank>()
            .init_resource::<Demo>()
            .init_resource::<Hold>()
            .add_systems(
                Update,
                sync.after(WorldServicesReady).before(CollectInteractions),
            )
            .add_systems(FixedUpdate, animate.after(crate::player::PublishPlayer))
            .add_systems(
                Update,
                focus.in_set(CollectInteractions).after(PlayerCameraUpdate),
            )
            .add_systems(
                Update,
                (input, keys::update).chain().after(ApplyInteractions),
            );
    }
}
fn state(snapshot: &Value) -> Option<(bool, bool)> {
    let door = snapshot["interactables"]["doors"]
        .as_array()?
        .iter()
        .find(|d| d["netId"].as_u64() == Some(ID.into()))?;
    Some((door["open"].as_bool()?, door["locked"].as_bool()?))
}
fn cleanup(world: &mut World) {
    if let Some(entity) = world.resource_mut::<Demo>().entity.take() {
        let _ = world.despawn(entity);
    }
}
fn sync(world: &mut World) {
    if !world.resource::<crate::LaunchOptions>().0.demo_furniture
        || !world.resource::<UiModel>().connected
        || world.resource::<GameSnapshot>().0.is_null()
    {
        cleanup(world);
        *world.resource_mut::<Demo>() = default();
        return;
    }
    if !world.resource::<WorldState>().ready {
        return;
    }
    let key = (
        world.resource::<WorldSessionEpoch>().0,
        world.resource::<WorldConfig>().map_name.clone(),
    );
    if world.resource::<Demo>().key.as_ref() != Some(&key) {
        cleanup(world);
        *world.resource_mut::<Demo>() = Demo {
            key: Some(key),
            ..default()
        };
    }
    let result = (|| -> anyhow::Result<()> {
        if world.resource::<Demo>().definition.is_none() {
            let config = world.resource::<WorldConfig>();
            let root = config
                .map_root
                .as_ref()
                .map(|p| p.to_string_lossy().into_owned())
                .unwrap_or_default();
            let reply = world
                .resource::<CoreBridge>()
                .request("worldSpawns", json!({"mapRoot":root}))?;
            let rows = reply["result"]["players"]
                .as_array()
                .map(Vec::as_slice)
                .unwrap_or_default();
            let mut base = world
                .resource::<WorldOrigin>()
                .to_local(super::beds::source_anchor(rows) + Vec3::new(-3., 0., 2.));
            base.y = world
                .resource::<WorldGeometry>()
                .terrain_height(base.x, base.z)
                .unwrap_or(0.);
            world.resource_mut::<Demo>().definition = Some(Definition { base, yaw: 0. });
        }
        let def = world.resource::<Demo>().definition.unwrap();
        let owns = crate::network::owns_world(&world.resource::<GameSnapshot>().0);
        if owns && !world.resource::<Demo>().registered {
            if state(&world.resource::<GameSnapshot>().0).is_none() {
                world
                    .resource::<CoreBridge>()
                    .request("worldServices", def.registration())?;
            }
            world.resource_mut::<Demo>().registered = true;
        }
        let Some(_) = state(&world.resource::<GameSnapshot>().0) else {
            if world.resource::<Demo>().seen {
                cleanup(world);
                if owns && world.resource::<Demo>().exported.is_some() {
                    world
                        .resource::<CoreBridge>()
                        .request("worldDynamicGeometry", json!({"key":KEY,"meshes":[]}))?;
                    world.resource_mut::<Demo>().exported = None;
                }
            }
            return Ok(());
        };
        world.resource_mut::<Demo>().seen = true;
        if world.resource::<Demo>().entity.is_none() {
            let material = world
                .resource_mut::<Assets<StandardMaterial>>()
                .add(StandardMaterial {
                    base_color: Color::srgb(0.42, 0.29, 0.17),
                    perceptual_roughness: 0.9,
                    ..default()
                });
            let mesh = world
                .resource_mut::<Assets<Mesh>>()
                .add(Cuboid::from_size(SIZE));
            let swing = world.resource::<Demo>().swing;
            let entity = world
                .spawn((
                    Name::new("Original demo Door"),
                    Door {
                        material: material.clone(),
                    },
                    Mesh3d(mesh),
                    MeshMaterial3d(material),
                    def.pose(swing),
                    RigidBody::Fixed,
                    Collider::cuboid(SIZE.x * 0.5, SIZE.y * 0.5, SIZE.z * 0.5),
                    CollisionGroups::new(Group::GROUP_1, Group::ALL),
                    crate::impact::surfaces::SurfaceOverride(
                        crate::impact::surfaces::Surface::Wood,
                    ),
                ))
                .id();
            world.resource_mut::<Demo>().entity = Some(entity);
        }
        let stamp = (
            world.resource::<WorldGeometry>().revision,
            world.resource::<Demo>().swing.to_bits(),
        );
        if owns && world.resource::<Demo>().exported != Some(stamp) {
            world.resource::<CoreBridge>().request(
                "worldDynamicGeometry",
                def.geometry(f32::from_bits(stamp.1)),
            )?;
            world.resource_mut::<Demo>().exported = Some(stamp);
        }
        Ok(())
    })();
    if let Err(error) = result {
        let text = format!("Demo door: {error}");
        if world.resource::<Demo>().last_error.as_ref() != Some(&text) {
            warn!("{text}");
            world.resource_mut::<UiModel>().notice(text.clone());
            world.resource_mut::<Demo>().last_error = Some(text);
        }
    }
}
fn animate(
    time: Res<Time<Fixed>>,
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    mut demo: ResMut<Demo>,
    mut doors: Query<&mut Transform, With<Door>>,
) {
    if crate::player::simulation_paused(&ui) {
        return;
    }
    let (Some((open, _)), Some(def), Some(entity)) =
        (state(&snapshot.0), demo.definition, demo.entity)
    else {
        return;
    };
    let want = if open { 1. } else { 0. };
    demo.swing = demo
        .swing
        .move_towards(want, time.delta_secs() / SWING_SECONDS);
    if let Ok(mut transform) = doors.get_mut(entity) {
        *transform = def.pose(demo.swing);
    }
}
#[allow(clippy::too_many_arguments)]
fn focus(
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    occupancy: Res<super::interactables::PropOccupancy>,
    vehicle: Res<crate::vehicle::Occupancy>,
    mut focus: ResMut<InteractionFocus>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    doors: Query<(Entity, &Door)>,
    context: ReadRapierContext,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let target = if ui.captures_input() || occupancy.seat.is_some() || vehicle.seat.is_some() {
        None
    } else if let (Ok(camera), Ok(player), Ok(context)) =
        (camera.single(), player.single(), context.single())
    {
        context.cast_ray(
            camera.translation,
            *camera.forward(),
            2.6,
            true,
            QueryFilter::default()
                .exclude_collider(player)
                .exclude_sensors()
                .groups(CollisionGroups::new(
                    Group::ALL,
                    Group::GROUP_1 | Group::GROUP_4,
                )),
        )
    } else {
        None
    };
    for (entity, door) in &doors {
        let focused = target.is_some_and(|(hit, _)| hit == entity);
        if let Some(mut material) = materials.get_mut(&door.material) {
            let color = if focused {
                Color::srgb(0.35, 0.30, 0.12).to_linear()
            } else {
                LinearRgba::BLACK
            };
            if material.emissive != color {
                material.emissive = color;
            }
        }
        if focused {
            if let Some((open, locked)) = state(&snapshot.0) {
                let label = format!(
                    "{} door{} | Hold to {}",
                    if open { "Close" } else { "Open" },
                    if locked { " (locked)" } else { "" },
                    if locked { "unlock" } else { "lock" }
                );
                focus.offer_manual(target.unwrap().1, label, "toggleDoor", json!({"netId":ID}));
            }
        }
    }
}
#[allow(clippy::too_many_arguments)]
fn input(
    time: Res<Time>,
    epoch: Res<WorldSessionEpoch>,
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    focus: Res<InteractionFocus>,
    bridge: Res<CoreBridge>,
    mut hold: ResMut<Hold>,
    mut model: ResMut<UiModel>,
    mut commands: Commands,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut keyring: ResMut<keys::Bank>,
) {
    if hold.epoch != epoch.0 {
        *hold = Hold {
            epoch: epoch.0,
            ..default()
        };
    }
    let valid = !ui.captures_input()
        && snapshot.0["player"]["alive"].as_bool() != Some(false)
        && focus.selected("toggleDoor", ID);
    if !valid {
        hold.elapsed = None;
        return;
    }
    let pressed = settings.pressed(GameAction::Interact, &keys, &mouse);
    if settings.just_pressed(GameAction::Interact, &keys, &mouse) {
        hold.elapsed = Some(0.);
    }
    let Some(elapsed) = &mut hold.elapsed else {
        return;
    };
    let action = if !pressed {
        hold.elapsed = None;
        Some(("toggleDoor", json!({"netId":ID})))
    } else {
        *elapsed += time.delta_secs();
        if *elapsed >= 0.2 {
            model.interaction = format!(
                "{} the door... {}%",
                if state(&snapshot.0).is_some_and(|s| s.1) {
                    "Unlocking"
                } else {
                    "Locking"
                },
                ((*elapsed * 100.) as u32).min(99)
            );
        }
        if *elapsed >= 1. {
            hold.elapsed = None;
            Some((
                "setDoorLocked",
                json!({"netId":ID,"locked":!state(&snapshot.0).is_some_and(|s|s.1)}),
            ))
        } else {
            None
        }
    };
    if let Some((name, args)) = action {
        match bridge.request("action", json!({"name":name,"args":args})) {
            Err(error) => model.notice(error.to_string()),
            Ok(_) if name == "setDoorLocked" => {
                if let Some(at) = crate::player::vector(&snapshot.0["player"]["position"]) {
                    keys::play(&mut commands, &mut keyring, &config, &assets, at, epoch.0);
                }
            }
            _ => {}
        }
    }
}
pub(super) fn shift_origin(world: &mut World, shift: Vec3) {
    if let Some(mut demo) = world.get_resource_mut::<Demo>() {
        if let Some(def) = &mut demo.definition {
            def.base -= shift;
        }
        demo.exported = None;
    }
}
#[cfg(test)]
mod tests;
