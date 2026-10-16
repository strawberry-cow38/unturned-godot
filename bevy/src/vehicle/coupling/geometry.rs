//! Vehicle.Build's separate landing legs and tow-cab sleeper surface.
use crate::vehicle::data::{VehicleDefinition, VehicleSpec};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;

/// Native query groups differ from Godot's layer numbers. This extra membership
/// is reserved for a trailer in the source SetTowGhost state; ordinary chassis
/// never scan the separate sleeper, while players retain the vehicle surface.
pub(crate) const GHOST_TRAILER: Group = Group::GROUP_16;

#[derive(Component)]
pub(in crate::vehicle) struct LandingLegs;

#[derive(Component)]
pub(in crate::vehicle) struct SleeperHull(pub Entity);

pub(super) fn leg_transform(spec: &VehicleSpec) -> Transform {
    let scale = spec.landing_leg_scale_y;
    if scale > 0. && scale != 1. {
        Transform::from_xyz(0., spec.landing_leg_pivot_y * (1. - scale), 0.)
            .with_scale(Vec3::new(1., scale, 1.))
    } else {
        Transform::IDENTITY
    }
}

pub(in crate::vehicle) fn build(
    commands: &mut Commands,
    owner: Entity,
    definition: &VehicleDefinition,
    legs: Option<Handle<Mesh>>,
    material: Handle<StandardMaterial>,
) {
    let spec = &definition.spec;
    if let Some(mesh) = legs {
        commands.spawn((
            Name::new("LandingLegs"),
            LandingLegs,
            Mesh3d(mesh),
            MeshMaterial3d(material),
            leg_transform(spec),
            Visibility::Inherited,
            ChildOf(owner),
        ));
    }
    if spec.fifth_wheel != [0.; 3]
        && let Some((size, center)) = definition.roof
    {
        // An independent copy remains solid against the trailer after the cab's
        // chassis pair is excepted. The original roof remains on the cab too.
        commands.spawn((
            Name::new("SleeperHull"),
            SleeperHull(owner),
            RigidBody::Fixed,
            Collider::cuboid(size[0] * 0.5, size[1] * 0.5, size[2] * 0.5),
            CollisionGroups::new(Group::GROUP_4, Group::GROUP_2 | GHOST_TRAILER),
            Transform::from_translation(Vec3::from_array(center)),
            ChildOf(owner),
        ));
    }
}
