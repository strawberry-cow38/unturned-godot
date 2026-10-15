//! ResourceField's final fruit slot owns a look-only box. It never joins the
//! physics or combat scene; accepted resource state alone hides its fruit.
use super::*;
use bevy_rapier3d::prelude::{Collider, ColliderDisabled};

#[derive(Component)]
pub(crate) struct Part {
    pub last: bool,
}

#[derive(Component)]
pub(super) struct Focus {
    shape: Collider,
    center: Vec3,
}
impl Focus {
    fn source(name: &str, scale: Vec3) -> Self {
        // Original ResourceField447: radial max preserves the authored trigger
        // under nonuniform placement; the body's basis is orthonormalized.
        let mushroom = name.starts_with("Mushroom");
        let size = if mushroom {
            Vec3::new(0.8, 0.6, 0.8)
        } else {
            Vec3::new(2.28, 2.05, 2.30)
        };
        let center = if mushroom {
            Vec3::new(0., 0.11, 0.)
        } else {
            Vec3::new(-0.114, 1.141, 0.064)
        };
        let scale = scale.abs();
        let source_scale = Vec3::new(scale.x.max(scale.z), scale.y, scale.x.max(scale.z));
        let half = size * source_scale * 0.5;
        Self {
            shape: Collider::cuboid(half.x, half.y, half.z),
            center: center * source_scale,
        }
    }
    pub(super) fn ray(
        &self,
        pose: &Transform,
        from: Vec3,
        direction: Vec3,
        limit: f32,
    ) -> Option<f32> {
        self.shape.cast_ray(
            pose.translation + pose.rotation * self.center,
            pose.rotation,
            from,
            direction,
            limit,
            true,
        )
    }
}

pub(super) fn prepare(
    mut commands: Commands,
    services: Res<ServiceManifest>,
    parts: Query<(
        Entity,
        &crate::world::WorldResource,
        &Part,
        &Transform,
        Option<&Focus>,
    )>,
) {
    for (entity, resource, part, pose, focus) in &parts {
        let forage = services
            .resources
            .get(&(resource.name.clone(), resource.instance_index))
            .is_some_and(|row| row["rewardItemId"].as_u64().unwrap_or(0) > 0);
        if part.last && forage {
            if focus.is_none() {
                commands
                    .entity(entity)
                    .insert(Focus::source(&resource.name, pose.scale));
            }
        } else if focus.is_some() {
            commands.entity(entity).remove::<Focus>();
        }
    }
}

pub(super) type Plants<'w, 's> = Query<
    'w,
    's,
    (
        &'static crate::world::WorldResource,
        &'static Transform,
        &'static Focus,
    ),
    Without<ColliderDisabled>,
>;

pub(super) fn offer(
    plants: &Plants,
    services: &ServiceManifest,
    catalog: &ItemCatalog,
    from: Vec3,
    direction: Vec3,
    wall_distance: f32,
    focus: &mut InteractionFocus,
) {
    for (resource, pose, shape) in plants.iter() {
        let Some(row) = services
            .resources
            .get(&(resource.name.clone(), resource.instance_index))
        else {
            continue;
        };
        let Some(reward) = row["rewardItemId"].as_u64().filter(|id| *id > 0) else {
            continue;
        };
        let Some(distance) = shape.ray(pose, from, direction, wall_distance) else {
            continue;
        };
        let label = catalog
            .items
            .get(&(reward as u16))
            .map(|v| v.name.clone())
            .unwrap_or_else(|| {
                resource
                    .name
                    .replace("_Snow", "")
                    .replace("_0", "")
                    .replace("Bush_", "")
                    + " Bush"
            });
        focus.offer(
            distance,
            format!("Harvest {label}"),
            "forageResource",
            json!({"index":row["index"]}),
            false,
        );
    }
}

#[cfg(test)]
mod tests;
