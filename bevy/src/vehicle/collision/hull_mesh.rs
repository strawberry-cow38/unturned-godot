//! Vehicle.Build's ship HullMesh: exact all-corners MeshRegion, independent
//! static world body, and an explicit exception for its own carrier.
use super::*;
use bevy::ecs::system::SystemParam;
use bevy_rapier3d::{
    pipeline::{BevyPhysicsHooks, PairFilterContextView},
    rapier::prelude::SolverFlags,
};

#[derive(Component)]
pub(in crate::vehicle) struct HullMesh(pub Entity);

#[derive(SystemParam)]
pub(crate) struct Hooks<'w, 's> {
    hulls: Query<'w, 's, &'static HullMesh>,
    exceptions: Option<Res<'w, super::super::coupling::physics::Exceptions>>,
}
impl BevyPhysicsHooks for Hooks<'_, '_> {
    fn filter_contact_pair(&self, context: PairFilterContextView) -> Option<SolverFlags> {
        if let (Some(a), Some(b), Some(exceptions)) = (context.rigid_body1(), context.rigid_body2(), self.exceptions.as_ref()) {
            if exceptions.contains(a,b) { return None; }
        }
        for (collider, other) in [
            (context.collider1(), context.rigid_body2()),
            (context.collider2(), context.rigid_body1()),
        ] {
            if self
                .hulls
                .get(collider)
                .is_ok_and(|hull| Some(hull.0) == other)
            {
                return None;
            }
        }
        Some(SolverFlags::COMPUTE_RIGID_IMPULSES)
    }
}

pub(in crate::vehicle) fn build(
    commands: &mut Commands,
    owner: Entity,
    definition: &VehicleDefinition,
    source: Option<&crate::content::ObjData>,
) -> bool {
    let spec = &definition.spec;
    if !super::authored::enabled(spec) {
        return false;
    }
    let Some((min, max)) = spec.hull_trimesh else {
        return false;
    };
    let Some(source) = source else {
        return false;
    };
    // Same inclusive ALL-three-corners selection as Vehicle.MeshRegion10707;
    // centroid selection drags enormous deck triangles into the deckhouse.
    let mut faces = source.clone();
    for vertex in &mut faces.positions {
        *vertex = source_face_vertex(*vertex).to_array();
    }
    let (_, region) = faces.split_zones(&[(Vec3::from_array(min), Vec3::from_array(max))]);
    if region.positions.is_empty() {
        return false;
    }
    let vertices = region
        .positions
        .into_iter()
        .map(Vec3::from_array)
        .collect::<Vec<_>>();
    let indices = (0..vertices.len() as u32)
        .collect::<Vec<_>>()
        .chunks_exact(3)
        .map(|t| [t[0], t[1], t[2]])
        .collect();
    let Ok(collider) = Collider::trimesh(vertices, indices) else {
        return false;
    };
    commands.spawn((
        Name::new("HullMesh"),
        HullMesh(owner),
        RigidBody::Fixed,
        collider,
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ActiveHooks::FILTER_CONTACT_PAIRS,
        Transform::default(),
        ChildOf(owner),
    ));
    true
}
