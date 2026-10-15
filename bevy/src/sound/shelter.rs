//! Shared source ShelterProbe up-ray, used by rain and puddle footsteps.
use crate::world::{WorldObject, rain::roof};
use bevy::{ecs::system::SystemParam, prelude::*};
use bevy_rapier3d::prelude::*;

#[derive(SystemParam)]
pub(crate) struct Shelter<'w, 's> {
    context: ReadRapierContext<'w, 's>,
    colliders: Query<'w, 's, (&'static Collider, &'static GlobalTransform)>,
    hierarchy: Query<'w, 's, (Option<&'static ChildOf>, Option<&'static WorldObject>)>,
}
impl Shelter<'_, '_> {
    pub fn covered(&self, at: Vec3) -> bool {
        let Ok(physics) = self.context.single() else {
            return false;
        };
        let predicate = |entity: Entity, _: &bevy_rapier3d::rapier::geometry::Collider| {
            let mut parent = entity;
            for _ in 0..16 {
                let Ok((relation, object)) = self.hierarchy.get(parent) else {
                    break;
                };
                if object.is_some_and(|o| roof::lamp_fixture(&o.asset_name)) {
                    return false;
                }
                let Some(relation) = relation else { break };
                parent = relation.parent();
            }
            let Ok((collider, global)) = self.colliders.get(entity) else {
                return true;
            };
            let pose = global.compute_transform();
            let aabb = collider.aabb(pose.translation, pose.rotation);
            let size = aabb.max - aabb.min;
            // A thin ceiling covers the listener: only its XZ footprint matters.
            size.x * size.z > 1.5
        };
        physics
            .cast_ray_and_get_normal(
                at,
                Vec3::Y,
                60.,
                false,
                QueryFilter::default()
                    .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1))
                    .exclude_sensors()
                    .predicate(&predicate),
            )
            .is_some_and(|(_, hit)| hit.normal.y.abs() > 0.001)
    }
}
