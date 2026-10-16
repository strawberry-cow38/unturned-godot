//! `BarricadePlacer.SetDef/Apply/Freeze`: the shared translucent ghost
//! (`DeployablePlacer.ValidMat/InvalidMat`) with blue/red port arrows. It owns
//! no collider and never stands in for an accepted body.
use super::*;
use crate::deployables::ports::art as port_art;

/// `DeployablePlacer.ValidMat`/`InvalidMat`: alpha 0.25, unshaded, double-sided.
pub(super) const VALID: Color = Color::srgba(0.30, 0.62, 1., 0.25);
pub(super) const INVALID: Color = Color::srgba(1., 0.28, 0.28, 0.25);
/// `ConnectionPort.ArrowBlue/ArrowRed` at the ghost's 0.92 alpha.
pub(super) const ARROW_VALID: Color = Color::srgba(0.30, 0.62, 1., 0.92);
pub(super) const ARROW_INVALID: Color = Color::srgba(1., 0.28, 0.28, 0.92);

#[derive(Component)]
pub(crate) struct GhostRoot;
#[derive(Resource, Default)]
pub(crate) struct Ghost {
    def: Option<u16>,
    pub(crate) root: Option<Entity>,
    mesh: Option<Entity>,
    /// (arrow node, billboard quad) per def port, children of the ghost mesh.
    arrows: Vec<(Entity, Entity)>,
    valid: Option<Handle<StandardMaterial>>,
    invalid: Option<Handle<StandardMaterial>>,
    pub(crate) arrow: Option<Handle<StandardMaterial>>,
    pub(crate) tinted_valid: Option<bool>,
    ports: Option<port_art::Art>,
    error: Option<String>,
}
fn ghost_material(color: Color) -> StandardMaterial {
    StandardMaterial {
        base_color: color,
        unlit: true,
        alpha_mode: AlphaMode::Blend,
        double_sided: true,
        cull_mode: None,
        ..default()
    }
}

#[derive(SystemParam)]
pub(super) struct Content<'w> {
    config: Res<'w, WorldConfig>,
    replicas: ResMut<'w, Replicas>,
    cache: ResMut<'w, ContentCache>,
    assets: Res<'w, AssetServer>,
    meshes: ResMut<'w, Assets<Mesh>>,
    materials: ResMut<'w, Assets<StandardMaterial>>,
    images: ResMut<'w, Assets<Image>>,
}

pub(super) fn present(
    mut commands: Commands,
    placer: Res<Placer>,
    catalog: Res<ItemCatalog>,
    cameras: Query<&Transform, With<PlayerCamera>>,
    mut content: Content,
    mut ghost: ResMut<Ghost>,
    mut poses: Query<&mut Transform, Without<PlayerCamera>>,
) {
    if ghost.root.is_some_and(|r| poses.get(r).is_err()) {
        // A world teardown despawned the ghost under us.
        ghost.root = None;
        ghost.def = None;
    }
    if ghost.def != placer.def {
        if let Some(root) = ghost.root.take() {
            commands.entity(root).despawn();
        }
        ghost.mesh = None;
        ghost.arrows.clear();
        ghost.tinted_valid = None;
        ghost.def = placer.def;
        if let Some(def) = placer
            .def
            .and_then(|id| catalog.deployables.iter().find(|d| d.id == id))
        {
            spawn(&mut commands, def, &mut content, &mut ghost);
        }
    }
    let (Some(root), Some(def)) = (
        ghost.root,
        placer
            .def
            .and_then(|id| catalog.deployables.iter().find(|d| d.id == id)),
    ) else {
        return;
    };
    let Some(bounds) = content.replicas.art.get(&def.id).map(|a| a.bounds) else {
        return;
    };
    let pose = placer.pose();
    let transform = rules::ghost_transform(def, bounds, Mount::of(def), &pose);
    let shown = placer.shown();
    commands.entity(root).insert((
        transform,
        if shown {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        },
    ));
    if ghost.tinted_valid != Some(pose.valid) {
        ghost.tinted_valid = Some(pose.valid);
        if let (Some(mesh), Some(material)) = (
            ghost.mesh,
            if pose.valid {
                ghost.valid.clone()
            } else {
                ghost.invalid.clone()
            },
        ) {
            commands.entity(mesh).insert(MeshMaterial3d(material));
        }
        if let Some(arrow) = ghost.arrow.as_ref()
            && let Some(mut m) = content.materials.get_mut(arrow)
        {
            m.base_color = if pose.valid {
                ARROW_VALID
            } else {
                ARROW_INVALID
            };
        }
    }
    // AxisBillboard: each arrow quad turns about its flow axis to the camera.
    let (Ok(camera), Some(mesh)) = (cameras.single(), ghost.mesh) else {
        return;
    };
    let Ok(mesh_local) = poses.get(mesh).copied() else {
        return;
    };
    let base = transform.compute_affine() * mesh_local.compute_affine();
    for (node, quad) in ghost.arrows.clone() {
        let Ok(local) = poses.get(node).copied() else {
            continue;
        };
        let arrow = base * local.compute_affine();
        let eye = arrow.inverse().transform_point3(camera.translation);
        if let Ok(mut quad) = poses.get_mut(quad) {
            port_art::face_camera(&mut quad, eye);
        }
    }
}

fn spawn(commands: &mut Commands, def: &Definition, content: &mut Content, ghost: &mut Ghost) {
    if !content.replicas.art.contains_key(&def.id) {
        match presentation::load(
            def,
            &content.config.content_root,
            &mut content.cache,
            &content.assets,
            &mut content.meshes,
            &mut content.materials,
        ) {
            Ok(art) => {
                content.replicas.art.insert(def.id, art);
            }
            Err(error) => {
                let message = format!("Cannot build the {} placement ghost: {error}", def.name);
                if ghost.error.as_ref() != Some(&message) {
                    warn!("{message}");
                }
                ghost.error = Some(message);
                return;
            }
        }
    }
    ghost
        .valid
        .get_or_insert_with(|| content.materials.add(ghost_material(VALID)));
    let invalid = ghost
        .invalid
        .get_or_insert_with(|| content.materials.add(ghost_material(INVALID)))
        .clone();
    let root = commands
        .spawn((
            Name::new(format!("{} placement ghost", def.name)),
            GhostRoot,
            Transform::default(),
            Visibility::Hidden,
        ))
        .id();
    let mesh = presentation::spawn_ghost_mesh(
        commands,
        &content.replicas.art[&def.id],
        root,
        invalid,
    );
    // SetDef: the ghost's own arrow material, children of the ghost mesh at
    // the raw def port positions (the mesh frame, inside its fixup).
    let art = ghost
        .ports
        .get_or_insert_with(|| port_art::Art::load(&mut content.meshes, &mut content.images));
    let (quad_mesh, texture) = (art.quad.clone(), art.texture.clone());
    let arrow = content.materials.add(StandardMaterial {
        base_color: ARROW_INVALID,
        base_color_texture: Some(texture),
        unlit: true,
        alpha_mode: AlphaMode::Blend,
        double_sided: true,
        cull_mode: None,
        ..default()
    });
    ghost.arrows.clear();
    for port in &def.ports {
        let at = Vec3::from(port.pos);
        let local = Transform::from_translation(at) * port_art::arrow_pose(port, at);
        let node = commands
            .spawn((local, Visibility::Inherited, ChildOf(mesh)))
            .id();
        let quad = commands
            .spawn((
                Mesh3d(quad_mesh.clone()),
                MeshMaterial3d(arrow.clone()),
                Transform::default(),
                Visibility::Inherited,
                bevy::light::NotShadowCaster,
                ChildOf(node),
            ))
            .id();
        ghost.arrows.push((node, quad));
    }
    ghost.root = Some(root);
    ghost.mesh = Some(mesh);
    ghost.arrow = Some(arrow);
    ghost.tinted_valid = Some(false);
}
