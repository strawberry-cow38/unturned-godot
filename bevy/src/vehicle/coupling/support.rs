//! LandingGear is the last authored compound shape, separate from the body
//! decomposition. Keep both forms so accepted retraction also survives a bake.
use super::super::data::VehicleDefinition;
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;

#[derive(Component)]
pub(super) struct Supports {
    pub deployed: Collider,
    pub retracted: Collider,
    pub raised: bool,
}
pub(in crate::vehicle) fn install(
    commands: &mut Commands,
    entity: Entity,
    definition: &VehicleDefinition,
    collider: Collider,
) {
    if definition.spec.landing_gear_size != [0.; 3] {
        let compound = collider
            .as_compound()
            .expect("original landing support compound");
        let parts = compound.raw.shapes();
        let retracted = Collider::compound(
            parts[..parts.len() - 1]
                .iter()
                .map(|(pose, shape)| {
                    (
                        pose.translation,
                        pose.rotation,
                        Collider::from(shape.clone()),
                    )
                })
                .collect(),
        );
        commands.entity(entity).insert(Supports {
            deployed: collider.clone(),
            retracted,
            raised: false,
        });
    }
    commands.entity(entity).insert(collider);
}
