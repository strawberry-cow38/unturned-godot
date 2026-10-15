//! Original terrain collision around an inbound NPC must outlive the player's
//! render window. These are supplemental static terrain shapes, never aircraft
//! bodies, and cells already in the ordinary world collider are excluded.
use super::*;
use crate::content::ObjData;
use bevy_rapier3d::prelude::RigidBody;

#[derive(Resource, Default)]
pub(super) struct Support {
    pub(super) entity: Option<Entity>,
    centers: Vec<(u32, IVec2)>,
    revision: u64,
    epoch: u64,
}
#[derive(Component)]
pub(super) struct NpcTerrain;

pub(super) fn support(
    mut commands: Commands,
    geometry: Res<WorldGeometry>,
    pilots: Res<Autopilots>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    bodies: Query<(&VehicleBody, &Transform)>,
    existing: Query<(), With<NpcTerrain>>,
    mut support: ResMut<Support>,
) {
    let mut centers = Vec::new();
    if snapshot.0["connected"] == true && crate::network::owns_world(&snapshot.0) {
        for (body, pose) in &bodies {
            if pilots.managed(body.net_id)
                && pose.translation.is_finite()
                && pose.translation.xz().distance(geometry.loaded_center.xz())
                    > geometry.loaded_radius - 96.
            {
                centers.push((
                    body.net_id,
                    (pose.translation.xz() / 16.).floor().as_ivec2(),
                ));
            }
        }
    }
    centers.sort_unstable_by_key(|c| c.0);
    if centers == support.centers
        && support.revision == geometry.revision
        && support.epoch == epoch.0
        && support.entity.is_none_or(|e| existing.contains(e))
    {
        return;
    }
    if let Some(entity) = support.entity.take() {
        commands.entity(entity).try_despawn();
    }
    let mut data = ObjData::default();
    let mut cells = HashSet::new();
    for (_, center) in &centers {
        let center = center.as_vec2() * 16. + Vec2::splat(8.);
        for patch in &geometry.terrain {
            for layer in patch.mesh_by_layer(Vec3::new(center.x, 0., center.y), 112.) {
                for cell in layer.positions.chunks_exact(6) {
                    let origin = Vec2::new(cell[0][0], cell[0][2]);
                    if origin.distance(geometry.loaded_center.xz()) <= geometry.loaded_radius
                        || !cells.insert((
                            origin.x.to_bits(),
                            origin.y.to_bits(),
                            patch.spacing.to_bits(),
                        ))
                    {
                        continue;
                    }
                    data.positions.extend_from_slice(cell);
                }
            }
        }
    }
    if !data.positions.is_empty() {
        if let Some(shape) = crate::world::trimesh_collider(&data) {
            support.entity = Some(
                commands
                    .spawn((
                        Name::new("NPC flight terrain collision"),
                        NpcTerrain,
                        crate::world::WorldTerrain,
                        Transform::IDENTITY,
                        RigidBody::Fixed,
                        shape,
                    ))
                    .id(),
            );
        }
    }
    support.centers = centers;
    support.revision = geometry.revision;
    support.epoch = epoch.0;
}
