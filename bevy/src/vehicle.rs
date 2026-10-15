//! Native single-body vehicle host. Seating and owner state use the original wire commands.
mod collision;
pub mod data;
mod dynamics;
mod exit;
mod flight;
mod geometry;
mod render;
mod spawns;
#[cfg(test)]
mod tests;

use crate::{
    bridge::CoreBridge,
    content::ContentCache,
    player::{GameSnapshot, PlayerCamera, PlayerUpdate},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState},
    world::{WorldConfig, WorldGeometry, WorldState},
};
use bevy::{input::mouse::AccumulatedMouseMotion, prelude::*};
use bevy_rapier3d::prelude::*;
use dynamics::{Buoyancy, Controls, Drivetrain};
use serde::Deserialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};

#[derive(SystemSet, Debug, Clone, PartialEq, Eq, Hash)]
pub struct VehicleUpdate;
#[derive(SystemSet, Debug, Clone, PartialEq, Eq, Hash)]
pub struct VehiclePublish;
#[derive(Resource, Default)]
pub struct Occupancy {
    pub seat: Option<OccupiedSeat>,
}
#[derive(Clone, Copy, Debug)]
pub struct OccupiedSeat {
    pub vehicle: Entity,
    pub net_id: u32,
    pub seat: u8,
    pub body_pose: Transform,
    pub eye_pose: Transform,
    pub velocity: Vec3,
    pub driver: bool,
    pub aircraft: bool,
}
#[derive(Component)]
pub struct VehicleBody {
    pub net_id: u32,
    pub type_id: u8,
    pub variant: u8,
    pub flags: u8,
    pub fuel: f32,
    pub health: f32,
    pub battery: f32,
    pub driver_player_id: u32,
    pub passengers: Vec<u32>,
    recov_counter: u8,
    pub controls: Controls,
    drivetrain: Drivetrain,
    buoyancy: Buoyancy,
    flight: flight::Flight,
}
#[derive(Resource, Default)]
struct Vehicles {
    map: String,
    points: Vec<spawns::SpawnPoint>,
    registered: HashSet<usize>,
    entities: HashMap<u32, Entity>,
    tick: u64,
}
#[derive(Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct Replica {
    net_id: u32,
    type_id: u8,
    variant: u8,
    position: [f32; 3],
    euler_degrees: [f32; 3],
    lin_vel: [f32; 3],
    ang_vel: [f32; 3],
    flags: u8,
    fuel: f32,
    health: f32,
    battery: f32,
    driver_player_id: u32,
    passengers: Option<Vec<u32>>,
    recov_counter: u8,
}
pub struct VehiclePlugin;
impl Plugin for VehiclePlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Occupancy>()
            .init_resource::<Vehicles>()
            .init_resource::<collision::Hulls>()
            .init_resource::<geometry::GeometryUploads>()
            .add_systems(
                Update,
                (
                    register_spawns,
                    sync_vehicles,
                    adopt_occupancy,
                    render::animate_wheels,
                    render::aircraft::animate,
                    collision::update,
                )
                    .chain()
                    .in_set(VehicleUpdate)
                    .after(PlayerUpdate),
            )
            .add_systems(
                Update,
                read_controls
                    .in_set(crate::entities::CollectInteractions)
                    .after(crate::player::PlayerCameraUpdate),
            )
            .add_systems(FixedUpdate, step_bodies.before(PhysicsSet::SyncBackend))
            .add_systems(
                PostUpdate,
                geometry::upload.after(TransformSystems::Propagate),
            )
            .add_systems(
                FixedUpdate,
                publish_bodies
                    .after(PhysicsSet::Writeback)
                    .in_set(VehiclePublish),
            );
    }
}
fn register_spawns(
    bridge: Res<CoreBridge>,
    config: Res<WorldConfig>,
    world: Res<WorldState>,
    geometry: Res<WorldGeometry>,
    model: Res<UiModel>,
    options: Res<crate::LaunchOptions>,
    snapshot: Res<GameSnapshot>,
    mut vehicles: ResMut<Vehicles>,
    mut commands: Commands,
    mut occupancy: ResMut<Occupancy>,
) {
    if !world.ready || !model.connected {
        return;
    }
    let tick = snapshot.0["tick"].as_u64().unwrap_or(0);
    if vehicles.map != config.map_name || tick < vehicles.tick {
        for (_, entity) in vehicles.entities.drain() {
            commands.entity(entity).despawn();
        }
        vehicles.registered.clear();
        occupancy.seat = None;
        vehicles.points = config
            .map_root
            .as_ref()
            .and_then(|p| std::fs::read(p.join("Spawns/Vehicles.dat")).ok())
            .and_then(|b| match spawns::decode(&b) {
                Ok(p) => Some(p),
                Err(e) => {
                    warn!("Vehicle spawn file: {e}");
                    None
                }
            })
            .unwrap_or_default();
        vehicles.map = config.map_name.clone();
    }
    vehicles.tick = tick;
    let mut indices = Vec::new();
    let mut requests = Vec::new();
    for point in &vehicles.points {
        if vehicles.registered.contains(&point.index) {
            continue;
        }
        // Only live terrain has native collision; later streamed regions add their original points.
        if point.position.xz().distance(geometry.loaded_center.xz()) > geometry.loaded_radius {
            continue;
        }
        let height = if point.water {
            world.sea_level.map(|s| s + 0.5)
        } else {
            geometry
                .terrain_height(point.position.x, point.position.z)
                .map(|h| h + 1.2)
        };
        let Some(height) = height else {
            continue;
        };
        let d = data::definition(point.type_id).unwrap();
        requests.push(registration(
            d,
            format!("{}:vehicle:{}", config.map_name, point.index),
            Vec3::new(point.position.x, height, point.position.z),
            point.yaw,
            point.index as u8,
        ));
        indices.push(point.index);
    }
    if !vehicles.registered.contains(&usize::MAX) {
        if let Some(d) = options.0.vehicle.as_deref().and_then(data::named) {
            let yaw = world.spawn_yaw;
            let rear =
                (d.spec.box_center[2] + d.spec.box_size[2] * 0.5).max(d.spec.tail_rotor_hub[2]);
            let mut at =
                world.spawn + Quat::from_rotation_y(yaw) * Vec3::NEG_Z * (rear + 6.).max(6.);
            at.y = geometry.terrain_height(at.x, at.z).unwrap_or(world.spawn.y)
                + if d.spec.heli || d.spec.plane {
                    d.spec.ground_clearance() + 0.05
                } else {
                    1.2
                };
            requests.push(registration(
                d,
                format!("{}:vehicle:harness:{}", config.map_name, d.key),
                at,
                yaw.to_degrees(),
                0,
            ));
            indices.push(usize::MAX);
        }
    }
    if requests.is_empty() {
        return;
    }
    match bridge.request(
        "worldVehicles",
        json!({"replace":false,"vehicles":requests}),
    ) {
        Ok(_) => {
            vehicles.registered.extend(indices);
        }
        Err(e) => {
            warn!("Vehicle registration: {e}");
        }
    }
}
fn registration(
    d: &data::VehicleDefinition,
    key: String,
    at: Vec3,
    yaw: f32,
    variant: u8,
) -> serde_json::Value {
    let s = &d.spec;
    let trunk = d.access.iter().any(|a| a.kind == "Trunk");
    let hood = d.access.iter().any(|a| a.kind == "Hood");
    let labels = d.seat_labels();
    let seats: Vec<_> = labels
        .iter()
        .take(12)
        .map(|label| json!({"width":if hood{4}else{0},"height":if hood{2}else{0},"label":label}))
        .collect();
    let mut hit_regions = Vec::new();
    if s.heli {
        let main = if s.main_hub_box != [0.; 3] {
            s.main_hub_box
        } else {
            [(s.rotor_radius * 0.14).max(0.22); 3]
        };
        let tail = if s.tail_hub_box != [0.; 3] {
            s.tail_hub_box
        } else {
            [(s.tail_rotor_radius * 0.42).max(0.16); 3]
        };
        hit_regions.push(json!({"bodyPart":"mainRotor","center":s.rotor_hub,"size":main,"health":if s.main_rotor_hp > 0. { s.main_rotor_hp } else { s.health * 0.45 }}));
        hit_regions.push(json!({"bodyPart":"tailRotor","center":s.tail_rotor_hub,"size":tail,"health":if s.tail_rotor_hp > 0. { s.tail_rotor_hp } else { s.health * 0.28 }}));
    }
    json!({"key":key,"typeId":d.type_id,"variant":variant,
        "position":at.to_array(),"yaw":yaw,"health":s.health*10.,"fuel":s.fuel,"battery":10000,
        "speedMax":s.speed_max(),"climbMax":s.heli_climb_max,"fallMax":s.heli_fall_max,"seatCount":d.seats.len(),"seatOffsets":d.seats,"hasDoors":true,
        "hitRegions":hit_regions,"engineRegion":{"rear":s.rear_engine,"threshold":s.box_center[2] + if s.rear_engine { 0.15 } else { -0.15 } * s.box_size[2],"health":s.health * 10. * 0.4},
        "storage":{"trunkWidth":if trunk{6}else{0},"trunkHeight":if trunk{4}else{0},"gloveboxWidth":if hood{3}else{0},"gloveboxHeight":if hood{2}else{0},"seats":seats}})
}
fn sync_vehicles(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    model: Res<UiModel>,
    config: Res<WorldConfig>,
    mut vehicles: ResMut<Vehicles>,
    mut query: Query<(&mut VehicleBody, &mut Transform, &mut Velocity)>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
    assets: Res<AssetServer>,
    mut intents: MessageReader<crate::ui::UiIntent>,
) {
    let restored = intents
        .read()
        .any(|intent| matches!(intent, crate::ui::UiIntent::Load));
    if !model.connected {
        return;
    }
    let rows: Vec<Replica> =
        serde_json::from_value(snapshot.0["vehicles"].clone()).unwrap_or_default();
    let local_player = snapshot.0["playerId"].as_u64().unwrap_or(0) as u32;
    let live: HashSet<_> = rows.iter().map(|v| v.net_id).collect();
    vehicles.entities.retain(|id, e| {
        if live.contains(id) {
            true
        } else {
            commands.entity(*e).despawn();
            false
        }
    });
    for row in rows {
        let Some(d) = data::definition(row.type_id) else {
            continue;
        };
        if let Some(entity) = vehicles.entities.get(&row.net_id) {
            if let Ok((mut body, mut transform, mut velocity)) = query.get_mut(*entity) {
                // Owner ignition/light intent is streamed with its physical pose. A stale
                // replica arriving before the next fixed tick must not cancel that intent.
                if row.driver_player_id != local_player
                    || body.driver_player_id != local_player
                    || restored
                    || row.flags & 32 != 0
                {
                    body.flags = row.flags;
                }
                body.fuel = row.fuel;
                body.health = row.health;
                body.battery = row.battery;
                body.driver_player_id = row.driver_player_id;
                body.passengers = row.passengers.clone().unwrap_or_default();
                // Normal replicas never fight the one host physics body. Only recovery teleports it.
                if body.recov_counter != row.recov_counter || restored {
                    *transform = replica_pose(&row);
                    velocity.linear = Vec3::from_array(row.lin_vel);
                    velocity.angular = Vec3::from_array(row.ang_vel);
                    body.recov_counter = row.recov_counter;
                }
            }
            continue;
        }
        let pose = replica_pose(&row);
        let spec = &d.spec;
        let entity = commands
            .spawn((
                Name::new(format!("Vehicle {} #{}", d.key, row.net_id)),
                VehicleBody {
                    net_id: row.net_id,
                    type_id: row.type_id,
                    variant: row.variant,
                    flags: row.flags,
                    fuel: row.fuel,
                    health: row.health,
                    battery: row.battery,
                    driver_player_id: row.driver_player_id,
                    passengers: row.passengers.unwrap_or_default(),
                    recov_counter: row.recov_counter,
                    controls: default(),
                    drivetrain: Drivetrain::new(spec),
                    buoyancy: Buoyancy::new(spec),
                    flight: flight::Flight::new(
                        spec,
                        flight::ignition_duration(spec, &config.content_root),
                    ),
                },
                pose,
                Visibility::default(),
                RigidBody::Dynamic,
                Velocity {
                    linear: Vec3::from_array(row.lin_vel),
                    angular: Vec3::from_array(row.ang_vel),
                },
                mass_properties(spec),
                ExternalForce::default(),
                ExternalImpulse::default(),
                Ccd::enabled(),
                Damping {
                    linear_damping: 0.1,
                    angular_damping: 0.1,
                },
                spec.controller(),
            ))
            .id();
        render::build(
            &mut commands,
            entity,
            d,
            row.variant,
            &config.content_root,
            &mut cache,
            &mut meshes,
            &mut materials,
            &mut images,
            &assets,
        );
        vehicles.entities.insert(row.net_id, entity);
    }
}
fn replica_pose(r: &Replica) -> Transform {
    Transform::from_translation(Vec3::from_array(r.position)).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        r.euler_degrees[1].to_radians(),
        r.euler_degrees[0].to_radians(),
        r.euler_degrees[2].to_radians(),
    ))
}
fn mass_properties(spec: &data::VehicleSpec) -> AdditionalMassProperties {
    let b = Vec3::from_array(spec.box_size);
    let m = spec.mass();
    let inertia = if spec.heli || spec.plane {
        Vec3::splat(m * 0.9)
    } else {
        Vec3::new(
            b.y * b.y + b.z * b.z,
            b.x * b.x + b.z * b.z,
            b.x * b.x + b.y * b.y,
        ) * (m / 12.)
    };
    AdditionalMassProperties::MassProperties(MassProperties {
        local_center_of_mass: spec.center_of_mass().0,
        mass: m,
        principal_inertia: inertia.max(Vec3::splat(0.01)),
        principal_inertia_local_frame: Quat::IDENTITY,
    })
}
fn adopt_occupancy(
    snapshot: Res<GameSnapshot>,
    query: Query<(Entity, &VehicleBody, &Transform, &Velocity)>,
    mut occupancy: ResMut<Occupancy>,
) {
    let player = snapshot.0["playerId"].as_u64().unwrap_or(0) as u32;
    occupancy.seat = None;
    if player == 0 {
        return;
    }
    for (entity, body, pose, velocity) in &query {
        let seat = if body.driver_player_id == player {
            Some(0)
        } else {
            body.passengers
                .iter()
                .position(|p| *p == player)
                .map(|n| n + 1)
        };
        let Some(seat) = seat else {
            continue;
        };
        let d = data::definition(body.type_id).unwrap();
        let Some(seat_offset) = d.seats.get(seat) else {
            continue;
        };
        let body_offset = d.body_seats.get(seat).unwrap_or(seat_offset);
        // Prefab seat empties are seat-pan height. Preserve PlayerController's
        // explicit fallback until the native seated skull pose is available.
        let eye = if seat == 0 {
            if d.spec.driver_eye != [0.; 3] {
                Vec3::from_array(d.spec.driver_eye)
            } else {
                Vec3::new(-0.4, 1.85, 0.4)
            }
        } else {
            Vec3::from_array(*seat_offset) + Vec3::Y * 1.05
        };
        occupancy.seat = Some(OccupiedSeat {
            vehicle: entity,
            net_id: body.net_id,
            seat: seat as u8,
            driver: seat == 0,
            aircraft: d.spec.heli || d.spec.plane,
            velocity: velocity.linear,
            eye_pose: Transform::from_translation(pose.transform_point(eye))
                .with_rotation(pose.rotation),
            body_pose: Transform::from_translation(
                pose.transform_point(Vec3::from_array(*body_offset)),
            )
            .with_rotation(pose.rotation),
        });
        break;
    }
}
fn read_controls(
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    occupancy: Res<Occupancy>,
    bridge: Res<CoreBridge>,
    camera: Query<&Transform, With<PlayerCamera>>,
    mut query: Query<(Entity, &mut VehicleBody, &Transform)>,
    mut focus: ResMut<crate::entities::InteractionFocus>,
    context: ReadRapierContext,
    players: Query<Entity, With<crate::player::PlayerBody>>,
    cache: Res<ContentCache>,
    config: Res<WorldConfig>,
    motion: Res<AccumulatedMouseMotion>,
    time: Res<Time>,
) {
    let active = ui.screen == crate::ui::Screen::Playing && model.alive;
    let pressed = |action| settings.pressed(action, &keys, &mouse);
    let edge = |action| settings.just_pressed(action, &keys, &mouse);
    for (_, mut body, _) in &mut query {
        body.controls = Controls {
            engine: body.flags & 1 != 0,
            ..default()
        };
    }
    if !active {
        return;
    }
    if let Some(seat) = occupancy.seat {
        if edge(GameAction::Interact) {
            if let (Ok((_, body, pose)), Ok(context), Ok(player)) =
                (query.get(seat.vehicle), context.single(), players.single())
            {
                let d = data::definition(body.type_id).unwrap();
                let doors = exit::doors(d, seat.seat as usize, pose, &cache, &config.content_root);
                let (clear, position) = exit::probe(
                    &context,
                    seat.vehicle,
                    player,
                    pose,
                    Vec3::from_array(d.seats[seat.seat as usize]),
                    &doors,
                );
                if bridge.request("worldVehicleExit",json!({"netId":seat.net_id,"seat":seat.seat,"clear":clear,"position":position.to_array()})).is_ok() {
                    let _=bridge.request("action",json!({"name":"exitVehicle","args":{}}));
                }
            }
            return;
        }
        if seat.driver {
            if let Ok((_, mut body, _)) = query.get_mut(seat.vehicle) {
                let spec = &data::definition(body.type_id).unwrap().spec;
                let throttle = pressed(GameAction::MoveForward) as u8 as f32
                    - pressed(GameAction::MoveBack) as u8 as f32;
                if edge(GameAction::VehicleIgnition) {
                    if body.flags & 1 != 0 {
                        body.flags &= !1;
                    } else if body.battery >= 400. && body.health > 0. && body.flags & 32 == 0 {
                        body.flags |= 1;
                    }
                }
                if throttle.abs() > 0.01
                    && body.battery >= 400.
                    && body.health > 0.
                    && body.flags & 32 == 0
                {
                    body.flags |= 1;
                }
                if edge(GameAction::VehicleLights) {
                    body.flags ^= 2;
                }
                let mut cyclic = Vec2::ZERO;
                if spec.heli || spec.plane {
                    let delta = if pressed(GameAction::FreeLook) {
                        Vec2::ZERO
                    } else {
                        motion.delta
                    };
                    let arrows = Vec2::new(
                        keys.pressed(KeyCode::ArrowDown) as u8 as f32
                            - keys.pressed(KeyCode::ArrowUp) as u8 as f32,
                        keys.pressed(KeyCode::ArrowRight) as u8 as f32
                            - keys.pressed(KeyCode::ArrowLeft) as u8 as f32,
                    );
                    cyclic = body.flight.input(
                        delta,
                        arrows,
                        if spec.plane {
                            settings.invert_plane_pitch
                        } else {
                            settings.invert_heli_pitch
                        },
                        settings.heli_sensitivity,
                        time.delta_secs(),
                    );
                    if spec.plane && edge(GameAction::LandingGear) {
                        body.flight.gear_down = !body.flight.gear_down;
                    }
                }
                let steer = pressed(GameAction::MoveRight) as u8 as f32
                    - pressed(GameAction::MoveLeft) as u8 as f32;
                body.controls = Controls {
                    throttle,
                    steer,
                    handbrake: pressed(GameAction::VehicleHandbrake),
                    engine: body.flags & 1 != 0,
                    pitch: cyclic.x,
                    roll: cyclic.y,
                    yaw: steer,
                    ground_mode: spec.plane
                        && (keys.pressed(KeyCode::ControlLeft)
                            || keys.pressed(KeyCode::ControlRight)),
                    ..default()
                };
            }
        }
        return;
    }
    let Ok(eye) = camera.single() else {
        return;
    };
    let mut nearest = None;
    let mut distance = 3.5;
    for (_, body, pose) in &query {
        let d = data::definition(body.type_id).unwrap();
        let local = pose
            .compute_affine()
            .inverse()
            .transform_point3(eye.translation);
        let direction = pose.rotation.inverse() * (*eye.forward());
        for zone in &d.access {
            if zone.kind != "Door" {
                continue;
            }
            let center = Vec3::from_array(zone.center);
            let half = Vec3::from_array(zone.size) * 0.5;
            if let Some(hit) = ray_box(local, direction, center - half, center + half, distance) {
                distance = hit;
                nearest = Some((body.net_id, zone.seat.max(0) as u8, d.spec.name.clone()));
            }
        }
    }
    if let Some((id, seat, name)) = nearest {
        focus.offer(
            distance,
            format!("Enter {name}"),
            "enterVehicle",
            json!({"netId":id,"seat":seat}),
            false,
        );
    }
}
fn ray_box(origin: Vec3, dir: Vec3, min: Vec3, max: Vec3, limit: f32) -> Option<f32> {
    let mut near = 0f32;
    let mut far = limit;
    for i in 0..3 {
        if dir[i].abs() < 1e-7 {
            if origin[i] < min[i] || origin[i] > max[i] {
                return None;
            }
            continue;
        }
        let a = (min[i] - origin[i]) / dir[i];
        let b = (max[i] - origin[i]) / dir[i];
        near = near.max(a.min(b));
        far = far.min(a.max(b));
        if near > far {
            return None;
        }
    }
    Some(near)
}
fn step_bodies(
    world: Res<WorldState>,
    geometry: Option<Res<WorldGeometry>>,
    context: ReadRapierContext,
    terrain: Query<(), With<crate::world::WorldTerrain>>,
    mut query: Query<(
        Entity,
        &mut VehicleBody,
        &Transform,
        &mut Velocity,
        &mut RayCastVehicleController,
        &mut ExternalForce,
        &mut ExternalImpulse,
        &mut RigidBody,
        &mut Damping,
    )>,
) {
    let context = context.single().ok();
    for (
        entity,
        mut body,
        pose,
        mut velocity,
        mut wheels,
        mut force,
        mut impulse,
        mut rigid_body,
        mut damping,
    ) in &mut query
    {
        // A saved/streamed car must not fall through terrain that has intentionally
        // left the collision window. The authority retains its parked pose, and the
        // original single body wakes when its region is physically present again.
        let outside = body.driver_player_id == 0
            && geometry.as_ref().is_some_and(|g| {
                g.loaded_radius > 0.
                    && pose.translation.xz().distance(g.loaded_center.xz()) > g.loaded_radius
            });
        if outside {
            *rigid_body = RigidBody::Fixed;
            *velocity = Velocity::default();
            *force = ExternalForce::default();
            *impulse = ExternalImpulse::default();
            for wheel in &mut wheels.wheels {
                wheel.engine_force = 0.;
                wheel.brake = 0.;
            }
            continue;
        }
        if *rigid_body == RigidBody::Fixed {
            *rigid_body = RigidBody::Dynamic;
        }
        let spec = &data::definition(body.type_id).unwrap().spec;
        let controls = body.controls;
        *force = ExternalForce::default();
        *impulse = ExternalImpulse::default();
        if !spec.heli && !spec.plane {
            let (f, i) = body
                .drivetrain
                .step(spec, &mut wheels, pose, &velocity, controls, 0.02);
            *force = f;
            *impulse = i;
        }
        let mut afloat = false;
        if spec.water == "Boat" {
            if let Some(sea) = world.sea_level {
                let (water, wet) =
                    body.buoyancy
                        .step(spec, pose, &mut velocity, controls, sea, 0.02);
                force.force += water.force;
                force.torque += water.torque;
                afloat = wet;
            }
        }
        if spec.heli || spec.plane {
            let mut contact = flight::AirContact {
                afloat,
                ..default()
            };
            if let Some(context) = context.as_ref() {
                contact.grounded = context
                    .cast_ray(
                        pose.translation,
                        Vec3::NEG_Y,
                        spec.ground_clearance() + 0.45,
                        true,
                        QueryFilter::default().exclude_collider(entity),
                    )
                    .is_some();
                if spec.heli {
                    let origin = pose.transform_point(Vec3::from_array(spec.rotor_hub));
                    let only_terrain = |entity, _: &bevy_rapier3d::rapier::geometry::Collider| {
                        terrain.contains(entity)
                    };
                    let hit = context.cast_ray(
                        origin,
                        Vec3::NEG_Y,
                        spec.rotor_radius * 2.,
                        true,
                        QueryFilter::default().predicate(&only_terrain),
                    );
                    contact.ground_effect =
                        flight::ground_effect(spec.rotor_radius, hit.map(|(_, distance)| distance));
                }
            }
            let driven = body.driver_player_id != 0;
            let exploded = body.flags & 32 != 0;
            let flight = body.flight.step(
                spec,
                pose,
                &velocity,
                controls,
                contact,
                &mut wheels,
                driven,
                exploded,
                0.02,
            );
            force.force += flight.force.force;
            force.torque += flight.force.torque;
            *damping = flight.damping;
            if flight.park {
                *rigid_body = RigidBody::Fixed;
                *velocity = Velocity::default();
            }
        }
    }
}
fn publish_bodies(
    bridge: Res<CoreBridge>,
    model: Res<UiModel>,
    occupancy: Res<Occupancy>,
    query: Query<(&VehicleBody, &Transform, &Velocity)>,
) {
    if !model.connected {
        return;
    }
    let mut unattended = Vec::new();
    for (body, pose, velocity) in &query {
        let (yaw, pitch, roll) = pose.rotation.to_euler(EulerRot::YXZ);
        let angles = [pitch.to_degrees(), yaw.to_degrees(), roll.to_degrees()];
        if occupancy
            .seat
            .is_some_and(|s| s.driver && s.net_id == body.net_id)
        {
            if let Err(e)=bridge.request("action",json!({"name":"vehicleState","args":{"vehicleNetId":body.net_id,"pos":pose.translation.to_array(),"eulerDegrees":angles,
                "linVel":velocity.linear.to_array(),"angVel":velocity.angular.to_array(),"steerDegrees":body.drivetrain.steering,"throttle":body.controls.throttle,"steer":body.controls.steer,"handbrake":body.controls.handbrake,"flags":body.flags,"recovAck":body.recov_counter}})) {warn!("Vehicle state: {e}");}
        } else {
            unattended.push(json!({"netId":body.net_id,"position":pose.translation.to_array(),"eulerDegrees":angles,"linVel":velocity.linear.to_array(),"angVel":velocity.angular.to_array(),
            "steerDegrees":body.drivetrain.steering,"flags":body.flags}));
        }
    }
    if !unattended.is_empty() {
        if let Err(e) = bridge.request("worldVehicleState", json!({"vehicles":unattended})) {
            warn!("Host vehicle states: {e}");
        }
    }
}
