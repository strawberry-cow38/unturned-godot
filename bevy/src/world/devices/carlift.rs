//! `CarLift` on `Car_Lift_0`: the platform the object rip never had
//! (`Car_Lift_0_ramp.obj`, a skinned mesh in the retail prefab), a box collider
//! that the player stands on, and the look target F works. The lift moves only
//! with power on its own consumer port; that port is a NetId-0 fixture no
//! server wire can reach, so in the shipped game the platform stays down and F
//! refuses ("the lift has no power", a line the source's own HUD sweep blanks
//! before it renders). The source draws no outline for it.
use super::*;
use crate::world::WorldConfig;

pub(crate) const PROP: &str = "Car_Lift_0";
pub(crate) const RAMP: &str = "Car_Lift_0_ramp";
/// The platform's look/collision body (`_ramp`, `HitMeta`), at the down
/// position (`UpLocal * 0`): `Toggle` never moves it without power.
#[derive(Component, Debug)]
pub(crate) struct Ramp;

pub(crate) fn attach(
    mut commands: Commands,
    config: Res<WorldConfig>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    objects: Query<
        (Entity, &WorldObject, &MeshMaterial3d<StandardMaterial>),
        (Added<WorldObject>, With<PlacementEuler>),
    >,
) {
    for (entity, object, material) in &objects {
        if object.asset_name != PROP {
            continue;
        }
        let path = config.content_root.join(format!("objects/{RAMP}.obj"));
        let Ok((mesh, data)) = cache.load_mesh(&path, &mut meshes) else {
            continue;
        };
        let half = (data.max - data.min) * 0.5;
        let ramp = commands
            .spawn((
                Name::new("CarLift ramp"),
                Ramp,
                Device(Target::CarLift(object.source_index)),
                Transform::IDENTITY,
                Visibility::default(),
                ChildOf(entity),
            ))
            .id();
        // The platform mesh and its box (BoxShape3D of the mesh AABB), world bit 0.
        commands.spawn((Mesh3d(mesh), MeshMaterial3d(material.0.clone()), Transform::IDENTITY, ChildOf(ramp)));
        commands.spawn((
            bevy_rapier3d::prelude::Collider::cuboid(half.x, half.y, half.z),
            Transform::from_translation((data.min + data.max) * 0.5),
            ChildOf(ramp),
        ));
    }
}
