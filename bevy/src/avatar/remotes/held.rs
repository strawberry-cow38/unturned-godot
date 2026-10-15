//! RemotePlayers.ApplyHeld, using accepted equipment and the original body hook.
use super::*;
use crate::{
    content::ContentCache,
    viewmodel::{GunVisual, attachments},
};

#[derive(Resource, Default)]
pub(super) struct Library(Option<HashMap<String, GunVisual>>);

#[derive(Component, Default)]
pub(super) struct Held {
    pub id: u16,
    pub asset: Option<String>,
    pub gun: bool,
    pub root: Option<Entity>,
    signature: Option<Value>,
}

#[derive(Component)]
pub(crate) struct RemoteMuzzle {
    pub player_id: u16,
    pub point: Vec3,
}

#[allow(clippy::too_many_arguments)]
pub(super) fn sync(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut library: ResMut<Library>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut bodies: Query<(&RigPlayback, &RemotePlayer, &mut Held)>,
) {
    let rows: HashMap<_, _> = replicas(&snapshot.0).collect();
    for (rig, player, mut held) in &mut bodies {
        let Some(row) = rows.get(&player.id) else {
            continue;
        };
        let id = row["heldItemId"].as_u64().unwrap_or(0) as u16;
        let signature = serde_json::json!([id, row["attachments"]]);
        if held.signature.as_ref() == Some(&signature) {
            continue;
        }
        if let Some(root) = held.root.take() {
            commands.entity(root).try_despawn();
        }
        *held = Held { id, ..default() };
        if id == 0 {
            held.signature = Some(signature);
            continue;
        }
        // Replica arrival and catalog loading are independently scheduled. An
        // unresolved item is pending, not a successfully dressed empty hand.
        let Some(item) = catalog.items.get(&id) else {
            continue;
        };
        let gun = item.item_type == "gun";
        if !gun && item.melee.is_null() {
            held.signature = Some(signature);
            continue;
        }
        let Some(asset) = item.asset.as_deref().filter(|a| !a.is_empty()) else {
            held.signature = Some(signature);
            continue;
        };
        held.asset = Some(asset.into());
        held.gun = gun;
        let Some(hook) = rig
            .bones
            .get("Right_Hook")
            .or_else(|| rig.bones.get("Right_Hand"))
        else {
            continue;
        };
        if library.0.is_none() {
            library.0 = crate::viewmodel::load_gun_visuals(&config.content_root).ok();
        }
        let Some(visuals) = &library.0 else { continue };
        let visual = gun.then(|| visuals.get(asset)).flatten();
        let (mesh, texture) = if gun {
            let Some(visual) = visual else { continue };
            (visual.mesh.clone(), visual.texture.clone())
        } else {
            (format!("{asset}.txt"), format!("{asset}_albedo.png"))
        };
        let Ok((mesh, _)) = cache.load_mesh(&config.content_root.join(&mesh), &mut meshes) else {
            continue;
        };
        let texture = config
            .content_root
            .join(&texture)
            .is_file()
            .then_some(texture);
        let cached = cache.material(texture.as_deref(), false, &assets, &mut materials);
        let mut material = materials.get(&cached).cloned().unwrap_or_default();
        // The source explicitly disables culling for the ripped held meshes.
        material.cull_mode = None;
        let material = materials.add(material);
        let root = commands
            .spawn((
                Name::new(format!("Remote held item {id}")),
                Mesh3d(mesh),
                MeshMaterial3d(material),
                Transform::from_rotation(Quat::from_rotation_z(std::f32::consts::FRAC_PI_2)),
                RenderLayers::layer(0),
                ChildOf(*hook),
            ))
            .id();
        held.root = Some(root);
        held.signature = Some(signature);
        if let Some(visual) = visual {
            commands.entity(root).insert(RemoteMuzzle {
                player_id: player.id,
                point: visual.muzzle,
            });
            for mut part in attachments::body_parts(visual, &row["attachments"], &catalog) {
                let Ok((mesh, _)) =
                    cache.load_mesh(&config.content_root.join(&part.mesh), &mut meshes)
                else {
                    continue;
                };
                let mut scale = 1.;
                if part.id == 9400 && part.slot == "Barrel" {
                    if let Ok((_, data)) =
                        cache.load_mesh(&config.content_root.join(&visual.mesh), &mut meshes)
                    {
                        (part.position, scale) = attachments::bottle_fit(visual, &data.positions);
                    }
                }
                let material = materials.add(StandardMaterial {
                    base_color: part.color,
                    base_color_texture: part.texture.map(|p| assets.load(p)),
                    perceptual_roughness: 1.,
                    reflectance: 0.,
                    cull_mode: None,
                    ..default()
                });
                commands.spawn((
                    Name::new(format!("Remote {} attachment {}", part.slot, part.id)),
                    Mesh3d(mesh),
                    MeshMaterial3d(material),
                    Transform::from_translation(part.position).with_scale(Vec3::splat(scale)),
                    RenderLayers::layer(0),
                    ChildOf(root),
                ));
            }
        }
    }
}
