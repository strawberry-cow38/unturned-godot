//! Original RiggedCharacter physical bones, sourced from rig.json's Ragdoll_Player.
//! Physical bodies live under an unrotated world root; rendered bones remain in
//! their skeleton hierarchy. This avoids Rapier rotated-parent writeback drift.
use crate::{
    rig::{RigAnimation, RigPlayback},
    world::WorldConfig,
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde::Deserialize;
use std::collections::HashMap;
#[derive(Component)]
pub struct StartRagdoll(pub Vec3);
#[derive(Component)]
struct Ragdoll {
    bodies: HashMap<usize, Entity>,
}
#[derive(Component)]
struct Container(Entity);
#[derive(Resource, Default)]
struct Library {
    bones: HashMap<String, Bone>,
    loaded: bool,
}
#[derive(Clone, Deserialize)]
struct Bone {
    rb: Option<Body>,
    #[serde(rename = "box")]
    shape: Option<BoxShape>,
    joint: Option<Joint>,
}
#[derive(Clone, Deserialize)]
struct Body {
    mass: f32,
    drag: f32,
    adrag: f32,
}
#[derive(Clone, Deserialize)]
struct BoxShape {
    center: [f32; 3],
    size: [f32; 3],
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Joint {
    low_twist: f32,
    high_twist: f32,
    swing1: f32,
    swing2: f32,
}
pub struct RagdollPlugin;
impl Plugin for RagdollPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Library>()
            .add_systems(Update, (start, cleanup).chain().after(RigAnimation))
            .add_systems(
                PostUpdate,
                pose.before(bevy::transform::TransformSystems::Propagate),
            );
    }
}
fn world_pose(
    entity: Entity,
    transforms: &Query<(&Transform, Option<&ChildOf>)>,
) -> Option<GlobalTransform> {
    let mut current = entity;
    let mut chain = Vec::new();
    for _ in 0..64 {
        let (t, p) = transforms.get(current).ok()?;
        chain.push(*t);
        if let Some(p) = p {
            current = p.parent();
        } else {
            return Some(
                chain
                    .into_iter()
                    .rev()
                    .fold(GlobalTransform::IDENTITY, |a, b| a.mul_transform(b)),
            );
        }
    }
    None
}
fn start(
    mut commands: Commands,
    config: Res<WorldConfig>,
    mut library: ResMut<Library>,
    mut rigs: Query<(Entity, &StartRagdoll, &mut RigPlayback), Without<Ragdoll>>,
    transforms: Query<(&Transform, Option<&ChildOf>)>,
    settings: Option<Res<crate::settings::Settings>>,
) {
    // RiggedCharacter.RagdollStart: with GraphicsOptions.Ragdolls off the body
    // just stops where it is; no physical bones are built.
    if settings.is_some_and(|s| !s.graphics.ragdolls) {
        for (root, _, mut rig) in &mut rigs {
            rig.paused = true;
            commands.entity(root).remove::<StartRagdoll>().insert(Ragdoll { bodies: default() });
        }
        return;
    }
    if !library.loaded {
        library.loaded = true;
        #[derive(Deserialize)]
        struct File {
            #[serde(default)]
            ragdoll: HashMap<String, Bone>,
        }
        match std::fs::read(config.content_root.join("rig.json"))
            .ok()
            .and_then(|v| serde_json::from_slice::<File>(&v).ok())
        {
            Some(data) => library.bones = data.ragdoll,
            None => warn!("Original human ragdoll metadata missing"),
        }
    }
    for (root, request, mut rig) in &mut rigs {
        rig.paused = true;
        let container = commands
            .spawn((
                Name::new("Original ragdoll bodies"),
                Container(root),
                Transform::IDENTITY,
                Visibility::Inherited,
            ))
            .id();
        let mut bodies = HashMap::new();
        let mut poses = HashMap::new();
        for (index, bone) in rig.data.bones.iter().enumerate() {
            let Some(data) = library.bones.get(&bone.name) else {
                continue;
            };
            let Some(bone_entity) = rig.bones.get(&bone.name) else {
                continue;
            };
            let Some(pose) = world_pose(*bone_entity, &transforms) else {
                continue;
            };
            let physical = pose.compute_transform();
            let size = data
                .shape
                .as_ref()
                .map_or(Vec3::splat(0.3), |s| Vec3::from_array(s.size));
            let center = data
                .shape
                .as_ref()
                .map_or(Vec3::ZERO, |s| Vec3::from_array(s.center));
            let impulse = if bone.name == "Spine" {
                request.0
            } else if bone.name == "Skeleton" {
                request.0 * 0.5
            } else {
                Vec3::ZERO
            };
            let entity = commands
                .spawn((
                    Name::new(format!("PB_{}", bone.name)),
                    RigidBody::Dynamic,
                    physical,
                    Collider::compound(vec![(
                        center,
                        Quat::IDENTITY,
                        Collider::cuboid(size.x * 0.5, size.y * 0.5, size.z * 0.5),
                    )]),
                    ColliderMassProperties::Mass(data.rb.as_ref().map_or(1., |b| b.mass.max(0.05))),
                    Damping {
                        linear_damping: data.rb.as_ref().map_or(0.01, |b| b.drag),
                        angular_damping: data.rb.as_ref().map_or(0.05, |b| b.adrag),
                    },
                    ExternalImpulse {
                        impulse,
                        torque_impulse: Vec3::ZERO,
                    },
                    CollisionGroups::new(Group::GROUP_7, Group::GROUP_1 | Group::GROUP_7),
                    ChildOf(container),
                ))
                .id();
            bodies.insert(index, entity);
            poses.insert(index, physical);
        }
        for (index, entity) in &bodies {
            let bone = &rig.data.bones[*index];
            let Some(joint) = library.bones[&bone.name].joint.as_ref() else {
                continue;
            };
            let mut parent = bone.parent;
            while parent >= 0 && !bodies.contains_key(&(parent as usize)) {
                parent = rig.data.bones[parent as usize].parent;
            }
            if parent < 0 {
                continue;
            }
            let p = parent as usize;
            let parent_pose = poses[&p];
            let child_pose = poses[index];
            let swing = joint.swing1.max(joint.swing2).to_radians().max(0.02);
            let twist = ((joint.high_twist - joint.low_twist) * 0.5)
                .to_radians()
                .max(0.02);
            // Godot ConeTwist uses local X for twist. Coupled Y/Z limits retain
            // the source circular swing cone instead of two independent hinges.
            let frame1 =
                Transform::from_matrix(parent_pose.to_matrix().inverse() * child_pose.to_matrix());
            let joint = GenericJointBuilder::new(JointAxesMask::LIN_AXES)
                .local_anchor1(frame1.translation)
                .local_anchor2(Vec3::ZERO)
                .local_basis1(frame1.rotation)
                .local_basis2(Quat::IDENTITY)
                .limits(JointAxis::AngX, [-twist, twist])
                .coupled_axes(JointAxesMask::ANG_Y | JointAxesMask::ANG_Z)
                .limits(JointAxis::AngY, [0., swing])
                .contacts_enabled(false)
                .build();
            commands
                .entity(*entity)
                .insert(ImpulseJoint::new(bodies[&p], joint));
        }
        commands
            .entity(root)
            .remove::<StartRagdoll>()
            .insert(Ragdoll { bodies });
    }
}
fn cleanup(
    mut commands: Commands,
    containers: Query<(Entity, &Container)>,
    rigs: Query<(), With<RigPlayback>>,
) {
    for (e, c) in &containers {
        if rigs.get(c.0).is_err() {
            commands.entity(e).despawn();
        }
    }
}
fn pose(
    rigs: Query<(Entity, &RigPlayback, &Ragdoll)>,
    mut poses: ParamSet<(Query<(&Transform, Option<&ChildOf>)>, Query<&mut Transform>)>,
) {
    for (root, rig, rag) in &rigs {
        let mut desired = Vec::new();
        let mut world = Vec::new();
        {
            let transforms = poses.p0();
            let Some(root_pose) = world_pose(root, &transforms) else {
                continue;
            };
            for (index, bone) in rig.data.bones.iter().enumerate() {
                let parent = if bone.parent < 0 {
                    root_pose
                } else {
                    world[bone.parent as usize]
                };
                let Some(entity) = rig.bones.get(&bone.name) else {
                    continue;
                };
                let Ok((local, _)) = transforms.get(*entity) else {
                    continue;
                };
                if let Some(physical) = rag
                    .bodies
                    .get(&index)
                    .and_then(|e| world_pose(*e, &transforms))
                {
                    let local = physical.reparented_to(&parent);
                    world.push(physical);
                    desired.push((*entity, local));
                } else {
                    world.push(parent.mul_transform(*local));
                }
            }
        }
        let mut transforms = poses.p1();
        for (entity, local) in desired {
            if let Ok(mut transform) = transforms.get_mut(entity) {
                *transform = local;
            }
        }
    }
}
