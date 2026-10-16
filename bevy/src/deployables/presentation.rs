use super::*;
use geometry::{Bounds, mesh_rotation};
use std::path::Path;

pub(super) struct Art {
    mesh: Handle<Mesh>,
    material: Handle<StandardMaterial>,
    mesh_rotation: Quat,
    pub bounds: Bounds,
    label: Option<(Handle<Mesh>, Handle<StandardMaterial>)>,
    switch_lamp: Option<switch::Art>,
}
#[cfg(test)]
impl Art {
    pub(super) fn material_for_test(&self) -> Handle<StandardMaterial> {
        self.material.clone()
    }
}
pub(super) fn load(
    def: &Definition,
    root: &Path,
    cache: &mut ContentCache,
    assets: &AssetServer,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
) -> anyhow::Result<Art> {
    let size = Vec3::from(def.size);
    let (mesh, bounds) = if def.proc_box {
        (
            meshes.add(Cuboid::from_size(size)),
            Bounds {
                min: size * -0.5,
                max: size * 0.5,
            },
        )
    } else {
        let name = def
            .model
            .as_ref()
            .ok_or_else(|| anyhow::anyhow!("source model absent"))?;
        let (mesh, data) = cache.load_mesh(&root.join(format!("objects/{name}.obj")), meshes)?;
        (
            mesh,
            Bounds {
                min: data.min,
                max: data.max,
            },
        )
    };
    let rotation = mesh_rotation(def);
    // Transform the original mesh AABB's eight corners, as Godot Aabb operator*,
    // rather than recomputing a tighter bound from rotated triangle vertices.
    let bounds = bounds.rotated(rotation);
    anyhow::ensure!(
        bounds.min.is_finite()
            && bounds.max.is_finite()
            && (bounds.max - bounds.min).cmpgt(Vec3::ZERO).all(),
        "invalid source collision bounds"
    );
    let material = if def.proc_box {
        materials.add(StandardMaterial {
            base_color: Color::srgb(0.42, 0.43, 0.45),
            metallic: 0.15,
            perceptual_roughness: 0.7,
            double_sided: true,
            cull_mode: None,
            ..default()
        })
    } else {
        let texture = format!("objects/{}_tex.png", def.model.as_ref().unwrap());
        let shared = cache.material(Some(&texture), false, assets, materials);
        let mut mat = materials.get(&shared).unwrap().clone();
        mat.perceptual_roughness = 1.;
        mat.double_sided = true;
        mat.cull_mode = None;
        materials.add(mat)
    };
    let label = (def.id == 1450).then(|| {
        (
            meshes.add(Rectangle::new(0.35, 0.135)),
            materials.add(StandardMaterial {
                base_color: Color::WHITE,
                perceptual_roughness: 0.9,
                double_sided: true,
                cull_mode: None,
                ..default()
            }),
        )
    });
    let switch_lamp = def.is_switch.then(|| switch::Art::load(meshes, materials));
    Ok(Art {
        mesh,
        material,
        mesh_rotation: rotation,
        bounds,
        label,
        switch_lamp,
    })
}
pub(super) fn spawn(
    commands: &mut Commands,
    def: &Definition,
    art: &Art,
    id: u32,
    transform: Transform,
    on: bool,
) -> Entity {
    let root = commands
        .spawn((
            Name::new(format!("{} #{id}", def.name)),
            Deployed {
                net_id: id,
                def_id: def.id,
            },
            transform,
            Visibility::Inherited,
            RigidBody::Fixed,
            art.bounds.collider(),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    let mesh = commands
        .spawn((
            Mesh3d(art.mesh.clone()),
            MeshMaterial3d(art.material.clone()),
            Transform::from_rotation(art.mesh_rotation),
            ChildOf(root),
        ))
        .id();
    if let Some((mesh_handle, material)) = &art.label {
        commands.spawn((
            Mesh3d(mesh_handle.clone()),
            MeshMaterial3d(material.clone()),
            Transform::from_translation(geometry::env_vector(
                "UG_LBLP",
                Vec3::new(0., 0.179, 0.0375),
            ))
            .with_rotation(geometry::euler(geometry::env_vector(
                "UG_LBLR",
                Vec3::new(-90., 0., 0.),
            ))),
            ChildOf(mesh),
        ));
    }
    if let Some(lamp) = &art.switch_lamp {
        lamp.spawn(commands, root, id, on);
    }
    root
}
