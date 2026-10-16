use super::*;
use geometry::{Bounds, mesh_rotation};
use std::path::Path;

pub(super) struct Art {
    mesh: Handle<Mesh>,
    /// `Deployable.BuildMesh`'s whole source mesh, before `SplitLampLens`.
    whole: Handle<Mesh>,
    material: Handle<StandardMaterial>,
    mesh_rotation: Quat,
    pub bounds: Bounds,
    label: Option<(Handle<Mesh>, Handle<StandardMaterial>)>,
    switch_lamp: Option<switch::Art>,
    /// Upright procedural turbine: the tower sits at (0, H/2, 0) in the body.
    turbine: Option<(Vec3, turbine::Art)>,
    lamps: Option<lamps::Art>,
}
/// The body mesh child (`Deployable._mesh`), for presentation that moves it.
#[derive(Component)]
pub(super) struct BodyMesh {
    pub net_id: u32,
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
    if def.is_wind_turbine {
        let (mesh, material, bounds, art) = turbine::load(meshes, materials);
        return Ok(Art {
            whole: mesh.clone(),
            mesh,
            material,
            mesh_rotation: Quat::IDENTITY,
            bounds,
            label: None,
            switch_lamp: None,
            turbine: Some((Vec3::new(0., 1.3, 0.), art)),
            lamps: None,
        });
    }
    let size = Vec3::from(def.size);
    let mut lamps = None;
    let (mesh, whole, bounds) = if def.proc_box {
        let mesh = meshes.add(Cuboid::from_size(size));
        (
            mesh.clone(),
            mesh,
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
        lamps = lamps::Art::load(def, Some(&data), meshes);
        (
            // SplitLampLens takes the lens triangles out of the body mesh.
            lamps.as_ref().and_then(|l| l.body.clone()).unwrap_or(mesh.clone()),
            mesh,
            // The collider and seat still use the whole original mesh AABB.
            Bounds {
                min: data.min,
                max: data.max,
            },
        )
    };
    if lamps.is_none() {
        lamps = lamps::Art::load(def, None, meshes);
    }
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
        whole,
        material,
        mesh_rotation: rotation,
        bounds,
        label,
        switch_lamp,
        turbine: None,
        lamps,
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
    let mesh_transform =
        Transform::from_translation(art.turbine.as_ref().map_or(Vec3::ZERO, |t| t.0))
            .with_rotation(art.mesh_rotation);
    let mesh = commands
        .spawn((
            Mesh3d(art.mesh.clone()),
            MeshMaterial3d(art.material.clone()),
            mesh_transform,
            BodyMesh { net_id: id },
            ChildOf(root),
        ))
        .id();
    if let Some((_, turbine)) = &art.turbine {
        turbine.spawn(commands, mesh, Some(id));
    }
    if let Some(lamps) = &art.lamps {
        lamps::spawn(
            commands,
            def,
            lamps,
            root,
            id,
            mesh_transform,
            art.material.clone(),
        );
    }
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

/// `Deployable.BuildMesh` as the placement ghost uses it: one tinted mesh node in
/// the placer's frame carrying `MeshBasis`, with the children BuildMesh hangs on
/// it (battery label, turbine nacelle/hub) keeping their own materials, since a
/// Godot `MaterialOverride` does not reach child instances. The ghost overwrites
/// the node's own transform, so a turbine tower loses its (0, H/2, 0) offset and
/// stands half below the aim point, as the source ghost does.
pub(super) fn spawn_ghost_mesh(
    commands: &mut Commands,
    art: &Art,
    root: Entity,
    material: Handle<StandardMaterial>,
) -> Entity {
    let mesh = commands
        .spawn((
            Name::new("Placement ghost mesh"),
            Mesh3d(art.whole.clone()),
            MeshMaterial3d(material),
            Transform::from_rotation(art.mesh_rotation),
            Visibility::Inherited,
            bevy::light::NotShadowCaster,
            ChildOf(root),
        ))
        .id();
    if let Some((_, turbine)) = &art.turbine {
        turbine.spawn(commands, mesh, None);
    }
    if let Some((mesh_handle, label)) = &art.label {
        commands.spawn((
            Mesh3d(mesh_handle.clone()),
            MeshMaterial3d(label.clone()),
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
    mesh
}
