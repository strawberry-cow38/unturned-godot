//! Original point hitch, approach exception and kingpin-centred yaw stop.
use super::super::*;
use super::{geometry, recovery, state, support::Supports};

#[derive(Resource, Default)]
pub(in crate::vehicle) struct Exceptions {
    pairs: HashSet<(Entity, Entity)>,
    ghosts: HashSet<Entity>,
}
impl Exceptions {
    pub(in crate::vehicle) fn contains(&self, a: Entity, b: Entity) -> bool {
        self.pairs.contains(&(a, b)) || self.pairs.contains(&(b, a))
    }
}
#[derive(Component, Default, PartialEq, Eq, Clone, Copy)]
struct Applied {
    cab: Option<Entity>,
    simulated: bool,
}
#[derive(Component)]
struct GhostGroups(CollisionGroups);
#[derive(Clone, Copy)]
struct Body {
    entity: Entity,
    id: u32,
    kind: u8,
    pose: Transform,
    link: state::Accepted,
    simulate: bool,
}
fn bodies(world: &mut World) -> Vec<Body> {
    let snapshot = world.get_resource::<GameSnapshot>().map(|s| s.0.clone());
    let mut result: Vec<_> = world
        .query::<(Entity, &VehicleBody, &Transform)>()
        .iter(world)
        .map(|(entity, body, pose)| Body {
            entity,
            id: body.net_id,
            kind: body.type_id,
            pose: *pose,
            link: body.coupling,
            simulate: snapshot.as_ref().is_none_or(|s| state::simulates(s, body)),
        })
        .collect();
    // Net IDs preserve original spawn order, including a saved fleet's order.
    result.sort_by_key(|b| b.id);
    result
}
pub(super) fn sync(world: &mut World) {
    let rows = bodies(world);
    for trailer in &rows {
        let spec = &data::definition(trailer.kind).unwrap().spec;
        if spec.kingpin == [0.; 3] {
            continue;
        }
        let cab = rows
            .iter()
            .find(|c| c.id == trailer.link.cab && c.link.trailer == trailer.id);
        let accepted = Applied {
            cab: cab.map(|c| c.entity),
            simulated: trailer.simulate && cab.is_some_and(|c| c.simulate),
        };
        let old = world
            .get::<Applied>(trailer.entity)
            .copied()
            .unwrap_or_default();
        if old != accepted {
            world.entity_mut(trailer.entity).remove::<ImpulseJoint>();
            if accepted.simulated {
                let cab = cab.unwrap();
                let source = &data::definition(cab.kind).unwrap().spec;
                // Both masses stay dynamic. The original pin is a free angular
                // constraint; neither trailer is welded to nor simulated by the cab.
                world.entity_mut(trailer.entity).insert(ImpulseJoint::new(
                    cab.entity,
                    SphericalJointBuilder::new()
                        .local_anchor1(source.fifth_wheel.into())
                        .local_anchor2(spec.kingpin.into())
                        .contacts_enabled(false),
                ));
            }
            if old.cab.is_some() && accepted.cab.is_none() && trailer.simulate {
                let velocity = world
                    .get::<Velocity>(trailer.entity)
                    .copied()
                    .unwrap_or_default();
                recovery::teleport(
                    world,
                    trailer.entity,
                    recovery::AdoptPose {
                        pose: trailer.pose,
                        velocity: Velocity {
                            angular: Vec3::ZERO,
                            ..velocity
                        },
                    },
                );
            }
            world.entity_mut(trailer.entity).insert(accepted);
        }
        let raised = cab.is_some();
        if let Some(mut supports) = world.get_mut::<Supports>(trailer.entity) {
            if supports.raised != raised {
                supports.raised = raised;
                let shape = if raised {
                    supports.retracted.clone()
                } else {
                    supports.deployed.clone()
                };
                world.entity_mut(trailer.entity).insert(shape);
            }
        }
    }
    let raised: HashSet<_> = rows
        .iter()
        .filter(|r| r.link.cab != 0)
        .map(|r| r.entity)
        .collect();
    for (parent, mut visibility) in world
        .query_filtered::<(&ChildOf, &mut Visibility), With<geometry::LandingLegs>>()
        .iter_mut(world)
    {
        *visibility = if raised.contains(&parent.parent()) {
            Visibility::Hidden
        } else {
            Visibility::Inherited
        };
    }
    approach(world);
}

/// Source approach is geometry-only; it does not create an accepted relationship
/// or retract landing legs. Every wheel ray omits ghost trailer rigid bodies.
pub(in crate::vehicle) fn approach(world: &mut World) {
    let rows = bodies(world);
    let mut pairs = HashSet::new();
    let mut ghosts = HashSet::new();
    for cab in &rows {
        let spec = &data::definition(cab.kind).unwrap().spec;
        if spec.fifth_wheel == [0.; 3] {
            continue;
        }
        let near = if cab.link.trailer != 0 {
            rows.iter()
                .find(|t| t.id == cab.link.trailer && t.link.cab == cab.id)
        } else {
            let pin = cab.pose.transform_point(spec.fifth_wheel.into());
            let mut near = None;
            let mut best = 6_f32.powi(2);
            for trailer in &rows {
                let spec = &data::definition(trailer.kind).unwrap().spec;
                if trailer.link.cab != 0 || spec.kingpin == [0.; 3] {
                    continue;
                }
                let distance =
                    pin.distance_squared(trailer.pose.transform_point(spec.kingpin.into()));
                if distance < best {
                    best = distance;
                    near = Some(trailer);
                }
            }
            near
        };
        if let Some(trailer) = near {
            pairs.insert((cab.entity, trailer.entity));
            ghosts.insert(trailer.entity);
        }
    }
    let old_ghosts = world.resource::<Exceptions>().ghosts.clone();
    for row in &rows {
        let Some(groups) = world.get::<CollisionGroups>(row.entity).copied() else {
            continue;
        };
        let saved = world.get::<GhostGroups>(row.entity).map(|g| g.0);
        if ghosts.contains(&row.entity) {
            let base = saved.unwrap_or(groups);
            if saved.is_none() {
                world.entity_mut(row.entity).insert(GhostGroups(base));
            }
            // GROUP4 is native vehicle identity, not source SolidBit. GROUP16
            // marks the source layer swap; wheel/world queries test that meaning.
            let wanted = CollisionGroups::new(
                base.memberships | geometry::GHOST_TRAILER,
                base.filters | Group::GROUP_2 | geometry::GHOST_TRAILER,
            );
            if groups != wanted {
                world.entity_mut(row.entity).insert(wanted);
            }
        } else if let Some(base) = saved {
            world
                .entity_mut(row.entity)
                .insert(base)
                .remove::<GhostGroups>();
        }
        world
            .entity_mut(row.entity)
            .insert(ActiveHooks::FILTER_CONTACT_PAIRS);
        if let Some(mut wheels) = world.get_mut::<RayCastVehicleController>(row.entity) {
            // This subsystem owns only these exclusions; retain other callers'.
            for old in &old_ghosts {
                wheels.exclude_rigid_bodies.remove(old);
            }
            wheels.exclude_rigid_bodies.extend(ghosts.iter().copied());
        }
    }
    *world.resource_mut::<Exceptions>() = Exceptions { pairs, ghosts };
}

pub(in crate::vehicle) fn clamp(world: &mut World) {
    if world
        .get_resource::<UiState>()
        .is_some_and(crate::player::simulation_paused)
    {
        return;
    }
    let rows = bodies(world);
    for cab in &rows {
        if !cab.simulate || cab.link.trailer == 0 {
            continue;
        }
        let Some(trailer) = rows
            .iter()
            .find(|t| t.id == cab.link.trailer && t.link.cab == cab.id)
        else {
            continue;
        };
        let spec = &data::definition(trailer.kind).unwrap().spec;
        let a = (cab.pose.rotation * Vec3::NEG_Z).with_y(0.);
        let b = (trailer.pose.rotation * Vec3::NEG_Z).with_y(0.);
        if a.length_squared() < 1e-4 || b.length_squared() < 1e-4 {
            continue;
        }
        let a = a.normalize();
        let b = b.normalize();
        let yaw = a.cross(b).y.atan2(a.dot(b));
        let limit = if spec.hitch_yaw_limit > 0. {
            90_f32.min(spec.hitch_yaw_limit)
        } else {
            90.
        }
        .to_radians();
        if yaw.abs() <= limit {
            continue;
        }
        let rotation = Quat::from_rotation_y(-(yaw - yaw.signum() * limit));
        let pivot = trailer.pose.transform_point(spec.kingpin.into());
        let pose = Transform {
            translation: pivot + rotation * (trailer.pose.translation - pivot),
            rotation: (rotation * trailer.pose.rotation).normalize(),
            ..trailer.pose
        };
        let mut velocity = world
            .get::<Velocity>(trailer.entity)
            .copied()
            .unwrap_or_default();
        if velocity.angular.y.signum() == yaw.signum() {
            velocity.angular.y = 0.;
        }
        recovery::teleport(
            world,
            trailer.entity,
            recovery::AdoptPose { pose, velocity },
        );
    }
}
