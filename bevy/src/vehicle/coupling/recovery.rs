//! Reliable owner recovery applies the exact cab AND trailer pose once. Snapshot
//! recovery counters are byte-sized and can repeat after a driver leaves/re-enters.
use super::super::*;
use crate::entities::CoreEvent;

#[derive(Clone, Copy, Component)]
pub(in crate::vehicle) struct AdoptPose {
    pub pose: Transform,
    pub velocity: Velocity,
}
#[derive(Resource, Default)]
pub(super) struct Recoveries(Vec<serde_json::Value>);

pub(super) fn receive(mut events: MessageReader<CoreEvent>, mut pending: ResMut<Recoveries>) {
    for event in events.read().filter(|event| event.kind == "vehicleRecov") {
        pending.0.push(event.data.clone());
    }
}
fn read(value: &serde_json::Value) -> Option<AdoptPose> {
    let pose = Transform::from_translation(crate::player::vector(&value["pos"])?).with_rotation(
        Quat::from_euler(
            EulerRot::YXZ,
            value["yawDegrees"].as_f64()? as f32 * std::f32::consts::PI / 180.,
            value["pitchDegrees"].as_f64()? as f32 * std::f32::consts::PI / 180.,
            value["rollDegrees"].as_f64()? as f32 * std::f32::consts::PI / 180.,
        ),
    );
    let velocity = Velocity {
        linear: crate::player::vector(&value["linVel"])?,
        angular: crate::player::vector(&value["angVel"]).unwrap_or(Vec3::ZERO),
    };
    pose.rotation
        .is_finite()
        .then_some(AdoptPose { pose, velocity })
}
pub(super) fn apply(world: &mut World) {
    let pending = std::mem::take(&mut world.resource_mut::<Recoveries>().0);
    let local = world.resource::<GameSnapshot>().0["playerId"]
        .as_u64()
        .unwrap_or(0) as u32;
    for value in pending {
        let id = value["netId"].as_u64().unwrap_or(0) as u32;
        let bodies: Vec<_> = world
            .query::<(Entity, &VehicleBody)>()
            .iter(world)
            .map(|(e, b)| (e, b.net_id, b.driver_player_id, b.coupling))
            .collect();
        let Some(&(cab, _, owner, link)) = bodies.iter().find(|(_, net, _, _)| *net == id) else {
            continue;
        };
        if local == 0 || owner != local {
            continue;
        }
        let Some(pose) = read(&value) else {
            continue;
        };
        // An old reliable recovery from a retired relationship cannot rewind a
        // newly linked body. Reject the entire pair rather than applying half.
        let trailer = if link.trailer != 0 {
            if value["trailer"]["netId"].as_u64() != Some(link.trailer.into()) {
                continue;
            }
            let Some(&(entity, _, _, other)) =
                bodies.iter().find(|(_, net, _, _)| *net == link.trailer)
            else {
                continue;
            };
            if other.cab != id {
                continue;
            }
            let Some(pose) = read(&value["trailer"]) else {
                continue;
            };
            Some((entity, pose))
        } else {
            if !value["trailer"].is_null() {
                continue;
            }
            None
        };
        world.entity_mut(cab).insert(pose);
        if let Some((entity, pose)) = trailer {
            world.entity_mut(entity).insert(pose);
        }
        world.get_mut::<VehicleBody>(cab).unwrap().recov_counter =
            value["recovCounter"].as_u64().unwrap_or(0) as u8;
    }
    apply_adoptions(world);
}
pub(in crate::vehicle) fn teleport(world: &mut World, entity: Entity, adopted: AdoptPose) {
    *world.get_mut::<Transform>(entity).unwrap() = adopted.pose;
    if let Some(mut velocity) = world.get_mut::<Velocity>(entity) {
        *velocity = adopted.velocity;
    }
    if let Some(body) = world.get::<VehicleBody>(entity) {
        let spec = &data::definition(body.type_id).unwrap().spec;
        if let Some(mut contacts) = world.get_mut::<contacts::ContactState>(entity) {
            contacts.adopt_velocity(spec, adopted.velocity.linear);
        }
    }
    crate::world::origin::move_physics_owner(world, entity, adopted.pose.translation);
    let handle = world.get::<RapierRigidBodyHandle>(entity).map(|h| h.0);
    for mut bodies in world.query::<&mut RapierRigidBodySet>().iter_mut(world) {
        if let Some(body) = handle.and_then(|h| bodies.bodies.get_mut(h)) {
            body.set_linvel(adopted.velocity.linear, true);
            body.set_angvel(adopted.velocity.angular, true);
        }
    }
}
pub(in crate::vehicle) fn apply_adoptions(world: &mut World) {
    let adoptions: Vec<_> = world
        .query::<(Entity, &AdoptPose)>()
        .iter(world)
        .map(|(e, p)| (e, *p))
        .collect();
    for (entity, pose) in adoptions {
        world.entity_mut(entity).remove::<AdoptPose>();
        teleport(world, entity, pose);
    }
}
