//! Original Bed.cs diagnostic furniture. Retail worlds contain none by default.
//! The explicit CLI switch reproduces WorldBuilder's source demo placement;
//! authority owns claims/occupancy and every peer uses the same authored order.
use super::{WorldConfig, WorldGeometry, WorldState, origin::WorldOrigin};
use crate::{
    bridge::CoreBridge,
    entities::{CollectInteractions, InteractionFocus, ServiceManifest, WorldServicesReady},
    player::{GameSnapshot, PlayerBody, PlayerCamera, PlayerCameraUpdate, WorldSessionEpoch},
    ui::{UiModel, UiState},
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};

const SIZE: Vec3 = Vec3::new(1., 0.45, 2.);
const BED_ID: u32 = 1;
const GEOMETRY_KEY: &str = "source-demo-bed";
#[derive(SystemSet, Clone, Debug, Hash, PartialEq, Eq)]
pub(super) struct DemoFurnitureReady;
#[derive(Component)]
struct Bed {
    seat_id: u32,
    material: Handle<StandardMaterial>,
}
#[derive(Clone)]
struct Definition {
    base: Transform,
    seat_id: u32,
}
impl Definition {
    fn seat(&self) -> Value {
        json!({"netId":self.seat_id,"sourceIndex":usize::MAX,"name":"Bed",
            "position":self.base.transform_point(Vec3::new(0., SIZE.y, 1.)).to_array(),
            "face":(self.base.rotation * Vec3::NEG_Z).to_array(),
            "groundY":self.base.translation.y,"recline":true,"demoBed":true})
    }
    fn body(&self) -> Transform {
        self.base
            .with_translation(self.base.translation + Vec3::Y * SIZE.y * 0.5)
    }
    fn registration(&self) -> Value {
        json!({"beds":[{"netId":BED_ID,"position":self.base.translation.to_array(),"yaw":90.}],
            "seats":[self.seat()]})
    }
    fn geometry(&self) -> Value {
        let data = super::ladder_box_geometry(&crate::content::ObjData {
            min: -SIZE * 0.5,
            max: SIZE * 0.5,
            ..default()
        });
        let pose = self.body();
        let vertices: Vec<_> = data
            .positions
            .into_iter()
            .map(|p| pose.transform_point(Vec3::from_array(p)).to_array())
            .collect();
        json!({"key":GEOMETRY_KEY,"meshes":[{"vertices":vertices}]})
    }
}
#[derive(Resource, Default)]
struct Demo {
    key: Option<(u64, String)>,
    definition: Option<Definition>,
    registered: bool,
    exported: bool,
    geometry_revision: u64,
    seen: bool,
    entity: Option<Entity>,
    last_error: Option<String>,
}
pub(super) struct BedPlugin;
impl Plugin for BedPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Demo>()
            .add_systems(
                Update,
                sync.in_set(DemoFurnitureReady).after(WorldServicesReady),
            )
            .add_systems(
                Update,
                focus.in_set(CollectInteractions).after(PlayerCameraUpdate),
            );
    }
}
/// Source WorldBuilder.InteractableAnchor uses a fresh Godot RandomNumberGenerator
/// with Seed=7, not the player's overridden spawn. PCG-XSH-RR and bounded rejection
/// match Godot 4.6 core/math/random_pcg and thirdparty/misc/pcg.cpp (Apache-2.0).
fn source_pick(count: usize) -> Option<usize> {
    let count = u32::try_from(count).ok().filter(|&n| n > 0)?;
    if count == 1 {
        return Some(0);
    }
    let increment = (1_442_695_040_888_963_407_u64 << 1) | 1;
    let mut state = 0_u64;
    let next = |state: &mut u64| {
        let old = *state;
        *state = old
            .wrapping_mul(6_364_136_223_846_793_005)
            .wrapping_add(increment);
        ((((old >> 18) ^ old) >> 27) as u32).rotate_right((old >> 59) as u32)
    };
    next(&mut state);
    state = state.wrapping_add(7);
    next(&mut state);
    let threshold = count.wrapping_neg() % count;
    loop {
        let r = next(&mut state);
        if r >= threshold {
            return Some((r % count) as usize);
        }
    }
}
fn source_anchor(rows: &[Value]) -> Vec3 {
    source_pick(rows.len())
        .and_then(|i| {
            Some(Vec3::new(
                rows[i]["x"].as_f64()? as f32,
                0.,
                rows[i]["z"].as_f64()? as f32,
            ))
        })
        .filter(|p| p.is_finite())
        .unwrap_or(Vec3::new(0., 0., -350.))
}
fn accepted(snapshot: &Value, table: &str, id: u32) -> bool {
    snapshot["interactables"][table]
        .as_array()
        .into_iter()
        .flatten()
        .any(|row| row["netId"].as_u64() == Some(u64::from(id)))
}
fn cleanup(world: &mut World) {
    let entity = world.resource_mut::<Demo>().entity.take();
    if let Some(entity) = entity {
        let _ = world.despawn(entity);
    }
    let mut services = world.resource_mut::<ServiceManifest>();
    let before = services.seats.len();
    services.seats.retain(|s| s["demoBed"] != true);
    if services.seats.len() != before {
        services.revision = services.revision.wrapping_add(1);
    }
}
fn sync(world: &mut World) {
    let enabled = world.resource::<crate::LaunchOptions>().0.demo_furniture;
    if !enabled
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
    // World services must first supply the complete deterministic authored seat
    // order (the read-only mapBindings path supplies it on a remote client).
    let map = world.resource::<WorldConfig>().map_name.clone();
    if world.resource::<ServiceManifest>().map != map {
        return;
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
                .to_local(source_anchor(rows) + Vec3::new(-5., 0., 2.));
            base.y = world
                .resource::<WorldGeometry>()
                .terrain_height(base.x, base.z)
                .unwrap_or(0.);
            let seat_id = world
                .resource::<ServiceManifest>()
                .seats
                .iter()
                .filter(|s| s["demoBed"] != true)
                .filter_map(|s| s["netId"].as_u64())
                .max()
                .unwrap_or(0)
                .checked_add(1)
                .and_then(|n| u32::try_from(n).ok())
                .ok_or_else(|| anyhow::anyhow!("Demo seat ID exceeds the source ID range"))?;
            world.resource_mut::<Demo>().definition = Some(Definition {
                base: Transform::from_translation(base)
                    .with_rotation(Quat::from_rotation_y(std::f32::consts::FRAC_PI_2)),
                seat_id,
            });
        }
        let def = world.resource::<Demo>().definition.clone().unwrap();
        let owns = crate::network::owns_world(&world.resource::<GameSnapshot>().0);
        if owns && !world.resource::<Demo>().registered {
            // A restored save already owns this claim. RegisterBed is idempotent,
            // but skipping the repeated service spec avoids accumulating saves.
            if !accepted(&world.resource::<GameSnapshot>().0, "beds", BED_ID) {
                world
                    .resource::<CoreBridge>()
                    .request("worldServices", def.registration())?;
            }
            world.resource_mut::<Demo>().registered = true;
        }
        let present = accepted(&world.resource::<GameSnapshot>().0, "beds", BED_ID)
            && accepted(&world.resource::<GameSnapshot>().0, "seats", def.seat_id);
        if !present {
            if world.resource::<Demo>().seen {
                cleanup(world);
                if owns && world.resource::<Demo>().exported {
                    world.resource::<CoreBridge>().request(
                        "worldDynamicGeometry",
                        json!({"key":GEOMETRY_KEY,"meshes":[]}),
                    )?;
                    world.resource_mut::<Demo>().exported = false;
                }
            }
            return Ok(());
        }
        world.resource_mut::<Demo>().seen = true;
        let seat = def.seat();
        if !world
            .resource::<ServiceManifest>()
            .seats
            .iter()
            .any(|s| s == &seat)
        {
            let mut services = world.resource_mut::<ServiceManifest>();
            services.seats.retain(|s| s["demoBed"] != true);
            services.seats.push(seat);
            services.revision = services.revision.wrapping_add(1);
        }
        if world.resource::<Demo>().entity.is_none() {
            let material = world
                .resource_mut::<Assets<StandardMaterial>>()
                .add(StandardMaterial {
                    base_color: Color::srgb(0.55, 0.52, 0.60),
                    perceptual_roughness: 1.,
                    ..default()
                });
            let mesh = world
                .resource_mut::<Assets<Mesh>>()
                .add(Cuboid::from_size(SIZE));
            let entity = world
                .spawn((
                    Name::new("Original demo Bed"),
                    Bed {
                        seat_id: def.seat_id,
                        material: material.clone(),
                    },
                    Mesh3d(mesh),
                    MeshMaterial3d(material),
                    def.body(),
                    RigidBody::Fixed,
                    Collider::cuboid(SIZE.x * 0.5, SIZE.y * 0.5, SIZE.z * 0.5),
                    CollisionGroups::new(Group::GROUP_1, Group::ALL),
                ))
                .id();
            world.resource_mut::<Demo>().entity = Some(entity);
        }
        let geometry_revision = world.resource::<WorldGeometry>().revision;
        if world.resource::<Demo>().geometry_revision != geometry_revision {
            world.resource_mut::<Demo>().exported = false;
        }
        if owns && !world.resource::<Demo>().exported {
            world
                .resource::<CoreBridge>()
                .request("worldDynamicGeometry", def.geometry())?;
            let mut demo = world.resource_mut::<Demo>();
            demo.exported = true;
            demo.geometry_revision = geometry_revision;
        }
        Ok(())
    })();
    if let Err(error) = result {
        let text = format!("Demo bed: {error}");
        if world.resource::<Demo>().last_error.as_ref() != Some(&text) {
            warn!("{text}");
            world.resource_mut::<UiModel>().notice(text.clone());
            world.resource_mut::<Demo>().last_error = Some(text);
        }
    }
}
fn intent(snapshot: &Value, seat_id: u32) -> (&'static str, &'static str, u32) {
    let player = snapshot["playerId"].as_u64().unwrap_or(0);
    let owner = snapshot["interactables"]["beds"]
        .as_array()
        .into_iter()
        .flatten()
        .find(|b| b["netId"] == BED_ID)
        .and_then(|b| b["owner"].as_u64())
        .unwrap_or(0);
    let free = snapshot["interactables"]["seats"]
        .as_array()
        .into_iter()
        .flatten()
        .find(|s| s["netId"] == seat_id)
        .is_some_and(|s| s["occupant"].as_u64() == Some(0));
    if player != 0 && owner == player && free {
        ("Lie down", "sitSeat", seat_id)
    } else if owner != 0 && owner != player {
        ("Someone else's bed", "claimBed", BED_ID)
    } else {
        ("Claim bed", "claimBed", BED_ID)
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
    beds: Query<(Entity, &Bed)>,
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
            2.6, // PlayerController.LookReach; authority reach is independently 4m.
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
    for (entity, bed) in &beds {
        let focused = target.is_some_and(|(hit, _)| hit == entity);
        if let Some(mut material) = materials.get_mut(&bed.material) {
            let emission = if focused {
                Color::srgb(0.20, 0.24, 0.35).to_linear()
            } else {
                LinearRgba::BLACK
            };
            if material.emissive != emission {
                material.emissive = emission;
            }
        }
        if focused {
            let (label, action, id) = intent(&snapshot.0, bed.seat_id);
            focus.offer(target.unwrap().1, label, action, json!({"netId":id}), false);
        }
    }
}
pub(super) fn shift_origin(world: &mut World, shift: Vec3) {
    if let Some(mut demo) = world.get_resource_mut::<Demo>() {
        if let Some(def) = &mut demo.definition {
            def.base.translation -= shift;
        }
    }
}
#[cfg(test)]
mod tests;
