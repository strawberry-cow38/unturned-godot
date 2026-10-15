use super::{Built, DoorPane, Leaf, geometry};
use crate::{
    content::{ContentCache, ObjData},
    vehicle::data::VehicleDefinition,
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use std::{path::Path, sync::Arc};

pub(in crate::vehicle) fn build(
    commands: &mut Commands,
    body: Entity,
    definition: &VehicleDefinition,
    root: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    material: Handle<StandardMaterial>,
) -> Option<(Handle<Mesh>, Built)> {
    let spec = &definition.spec;
    if !geometry::applies(spec) {
        return None;
    }
    let name = spec.body.as_deref()?;
    let path = root.join(name);
    let body_key = root.join(format!("{name}#bifold/body"));
    let a_key = root.join(format!("{name}#bifold/a"));
    let b_key = root.join(format!("{name}#bifold/b"));
    if !cache.meshes.contains_key(&body_key) {
        let (_, original) = cache.load_mesh(&path, meshes).ok()?;
        let parts = geometry::cut(&original, spec)?;
        for (key, data) in [
            (body_key.clone(), Some(parts.body)),
            (a_key.clone(), Some(parts.leaf_a)),
            (b_key.clone(), parts.leaf_b),
        ] {
            if let Some(data) = data {
                cache.meshes.insert(key.clone(), meshes.add(data.mesh()));
                cache.geometry.insert(key, Arc::new(data));
            }
        }
    }
    let pivot_a = Vec3::new(spec.door_hinge_x, 0., spec.door_hinge_z);
    let bx = if spec.door_hinge_bx != 0. {
        spec.door_hinge_bx
    } else {
        spec.door_hinge_x
    };
    let pivot_b = Vec3::new(bx, 0., spec.door_split_z);
    let a = commands
        .spawn((
            Name::new("DoorHingeA"),
            Transform::from_translation(pivot_a),
            Visibility::Inherited,
            ChildOf(body),
        ))
        .id();
    leaf(
        commands,
        a,
        "DoorLeafA",
        cache.meshes[&a_key].clone(),
        &cache.geometry[&a_key],
        -pivot_a,
        material.clone(),
        meshes,
    );
    let b = if let Some(mesh) = cache.meshes.get(&b_key) {
        let b = commands
            .spawn((
                Name::new("DoorHingeB"),
                Transform::from_translation(pivot_b - pivot_a),
                Visibility::Inherited,
                ChildOf(a),
            ))
            .id();
        leaf(
            commands,
            b,
            "DoorLeafB",
            mesh.clone(),
            &cache.geometry[&b_key],
            -pivot_b,
            material,
            meshes,
        );
        Some(b)
    } else {
        None
    };
    Some((
        cache.meshes[&body_key].clone(),
        Built {
            a,
            b,
            offset_a: -pivot_a,
            offset_b: -pivot_b,
            degrees: if spec.door_fold_deg > 0. {
                spec.door_fold_deg
            } else {
                90.
            },
            phase: 0.,
            open: false,
        },
    ))
}
fn leaf(
    commands: &mut Commands,
    parent: Entity,
    name: &'static str,
    mesh: Handle<Mesh>,
    data: &ObjData,
    offset: Vec3,
    material: Handle<StandardMaterial>,
    meshes: &mut Assets<Mesh>,
) {
    let center = (data.min + data.max) * 0.5;
    let half = (data.max - data.min) * 0.5;
    commands.spawn((
        Name::new(name),
        Mesh3d(mesh),
        MeshMaterial3d(material),
        Transform::from_translation(offset),
        Visibility::Inherited,
        ChildOf(parent),
        Leaf {
            center,
            half,
            contact_mesh: meshes.add(Cuboid::new(half.x * 2., half.y * 2., half.z * 2.)),
        },
        Collider::compound(vec![(
            center,
            Quat::IDENTITY,
            Collider::cuboid(half.x, half.y, half.z),
        )]),
        super::groups(true),
        ColliderMassProperties::Mass(0.),
    ));
}

pub(in crate::vehicle) fn pane(commands: &mut Commands, entity: Entity, data: &ObjData) {
    // Door windows inherit the source layer5 switch: bullets/cars still see
    // them, but the player-only HitMesh membership is deliberately absent.
    let indices = (0..data.positions.len() as u32).collect::<Vec<_>>();
    if let Ok(collider) = Collider::trimesh(
        data.positions
            .iter()
            .map(|p| Vec3::from_array(*p))
            .collect(),
        indices
            .chunks_exact(3)
            .map(|t| [t[0], t[1], t[2]])
            .collect(),
    ) {
        commands.entity(entity).insert((
            DoorPane,
            collider,
            super::groups(true),
            ColliderMassProperties::Mass(0.),
        ));
    }
}
