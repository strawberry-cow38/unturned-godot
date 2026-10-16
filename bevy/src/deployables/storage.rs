//! Placed storage: the `Refrigerator` body (`FridgeDeploy` / the replica view's
//! IsStorage branch) and `PlayerController.OpenNearestCrate`. A fridge is a
//! StorageCrate with a consumer port, not a Deployable: it takes no look focus,
//! no hold-F pickup and no readout. Its grid, like the campfire's
//! DeployableCrate, is the host crate registered under the accepted NetId, so
//! F opens it through the existing Command `openStorage` and storage UI.
use super::*;
use crate::{
    entities::{CollectInteractions, InteractionFocus},
    player::PlayerCamera,
    ui::UiState,
};
#[cfg(test)]
mod tests;
#[cfg(test)]
mod visual;
use bevy::{
    camera::visibility::NoFrustumCulling,
    image::ImageSampler,
    light::{NotShadowCaster, NotShadowReceiver},
    text::TextPipeline,
};

/// `Refrigerator` BuildVisual/_Ready: the 0.7 x 1.7 x 0.7 body standing on the
/// accepted point (also its StaticBody box).
pub(super) const BODY: Vec3 = Vec3::new(0.7, 1.7, 0.7);
/// `StorageCrate.RenderDist`: meshes and label cut off past 80 m.
const RENDER_DISTANCE: f32 = 80.;
/// `OpenNearestCrate`: within 2.5 m of the player's origin (squared 6.25).
pub(super) const OPEN_REACH: f32 = 2.5;
/// Label3D: PixelSize 0.006, FontSize 56, OutlineSize 10, at y 1.95.
const LABEL_PIXEL: f32 = 0.006;

#[derive(Component)]
pub(crate) struct Fridge;
/// A Label3D (billboard, no depth test) the label system rasters once.
#[derive(Component)]
pub(super) struct CrateLabel {
    text: String,
    color: Color,
    built: bool,
}
#[derive(Resource, Default)]
pub(super) struct Art {
    body: Option<(Handle<Mesh>, Handle<StandardMaterial>)>,
    handle: Option<(Handle<Mesh>, Handle<StandardMaterial>)>,
}

/// The fridge's box in its own frame: authority geometry and the collider.
pub(super) fn bounds() -> geometry::Bounds {
    geometry::Bounds {
        min: Vec3::new(-BODY.x * 0.5, 0., -BODY.z * 0.5),
        max: Vec3::new(BODY.x * 0.5, BODY.y, BODY.z * 0.5),
    }
}
/// `Refrigerator.Spawn`: the node at the point, yawed, never stood up.
pub(super) fn transform(pose: Pose) -> Transform {
    Transform::from_translation(pose.point).with_rotation(Quat::from_rotation_y(pose.yaw.to_radians()))
}
pub(super) fn spawn(
    commands: &mut Commands,
    def: &Definition,
    id: u32,
    pose: Pose,
    art: &mut Art,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
) -> Entity {
    let (body_mesh, body_material) = art
        .body
        .get_or_insert_with(|| {
            (
                meshes.add(Cuboid::from_size(BODY)),
                materials.add(StandardMaterial {
                    base_color: Color::srgb(0.86, 0.88, 0.9),
                    metallic: 0.3,
                    perceptual_roughness: 0.35,
                    ..default()
                }),
            )
        })
        .clone();
    let (handle_mesh, handle_material) = art
        .handle
        .get_or_insert_with(|| {
            (
                meshes.add(Cuboid::new(0.04, 0.5, 0.04)),
                materials.add(StandardMaterial {
                    base_color: Color::srgb(0.3, 0.3, 0.32),
                    metallic: 0.5,
                    perceptual_roughness: 0.3,
                    ..default()
                }),
            )
        })
        .clone();
    let root = commands
        .spawn((
            Name::new(format!("{} #{id}", def.name)),
            Deployed {
                net_id: id,
                def_id: def.id,
            },
            Fridge,
            transform(pose),
            Visibility::Inherited,
            RigidBody::Fixed,
            bounds().collider(),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    commands.spawn((
        Mesh3d(body_mesh),
        MeshMaterial3d(body_material),
        Transform::from_xyz(0., BODY.y * 0.5, 0.),
        ChildOf(root),
    ));
    commands.spawn((
        Mesh3d(handle_mesh),
        MeshMaterial3d(handle_material),
        Transform::from_xyz(0.28, 1.0, 0.36),
        ChildOf(root),
    ));
    commands.spawn((
        Name::new("Refrigerator label"),
        CrateLabel {
            text: "Refrigerator".into(),
            color: Color::srgb(0.85, 0.9, 0.95),
            built: false,
        },
        Transform::from_xyz(0., 1.95, 0.),
        Visibility::Inherited,
        ChildOf(root),
    ));
    root
}

pub(super) fn install(app: &mut App) {
    app.init_resource::<Art>()
        .add_systems(
            Update,
            open_nearest
                .in_set(CollectInteractions)
                .after(crate::player::PlayerCameraUpdate),
        )
        .add_systems(Update, range.after(super::sync))
        .add_systems(
            Update,
            label
                .after(super::sync)
                .run_if(resource_exists::<TextPipeline>)
                .run_if(resource_exists::<Assets<crate::info_billboard::PanelMaterial>>),
        );
}

/// The crate the source `OpenNearestCrate` would open: nearest within 2.5 m of
/// the player's origin, by the crate node's own origin (the fridge at its
/// point, a campfire's DeployableCrate at its seated body origin).
pub(super) fn nearest(
    feet: Vec3,
    crates: impl Iterator<Item = (u32, Vec3)>,
) -> Option<u32> {
    crates
        .map(|(id, at)| (id, feet.distance_squared(at)))
        .filter(|(_, d)| *d < OPEN_REACH * OPEN_REACH)
        .min_by(|a, b| a.1.total_cmp(&b.1))
        .map(|(id, _)| id)
}
/// The F chain's late fallback, below crop harvest and every look focus: no
/// prompt (the source shows none), dispatched with the storage UI flag.
fn open_nearest(
    ui: Option<Res<UiState>>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    bodies: Query<(&Deployed, &Transform)>,
    mut focus: Option<ResMut<InteractionFocus>>,
) {
    let Some(focus) = focus.as_mut() else {
        return;
    };
    if ui.is_none_or(|u| u.captures_input())
        || snapshot.0["player"]["alive"] != true
        || snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) != 0
    {
        return;
    }
    let Some(feet) = crate::player::vector(&snapshot.0["player"]["position"]) else {
        return;
    };
    let crates = bodies.iter().filter_map(|(d, t)| {
        let def = catalog.deployables.iter().find(|x| x.id == d.def_id)?;
        (def.is_storage || (def.storage_w > 0 && def.storage_h > 0)).then_some((d.net_id, t.translation))
    });
    if let Some(id) = nearest(feet, crates) {
        focus.offer_hidden(f32::MAX, "openStorage", json!({"netId":id}), true);
    }
}

/// `ApplyRenderDistance`: every mesh and the label stop drawing past 80 m.
fn range(
    cameras: Query<&Transform, With<PlayerCamera>>,
    mut fridges: Query<(&Transform, &mut Visibility), (With<Fridge>, Without<PlayerCamera>)>,
) {
    let Ok(camera) = cameras.single() else {
        return;
    };
    for (pose, mut visibility) in &mut fridges {
        let wanted = if pose.translation.distance(camera.translation) <= RENDER_DISTANCE {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if *visibility != wanted {
            *visibility = wanted;
        }
    }
}

/// Label3D (Billboard Enabled, NoDepthTest, Modulate, OutlineSize 10 black).
fn label(
    mut commands: Commands,
    mut labels: Query<(Entity, &mut CrateLabel)>,
    mut raster: crate::character::nameplate::raster::Raster,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<crate::info_billboard::PanelMaterial>>,
) {
    for (entity, mut label) in &mut labels {
        if label.built {
            continue;
        }
        let Some(mut bitmap) = raster.render_fitted(&label.text, 56., f32::MAX, 10., 1.) else {
            continue;
        };
        bitmap.image.sampler = ImageSampler::linear();
        let size = Vec2::new(bitmap.image.width() as f32, bitmap.image.height() as f32) * LABEL_PIXEL;
        // Raster offsets are in nameplate units (0.0025 m per pixel).
        let offset = bitmap.offset * (LABEL_PIXEL / 0.0025);
        let image = raster.images.add(bitmap.image);
        commands.spawn((
            Mesh3d(meshes.add(Rectangle::from_size(size))),
            MeshMaterial3d(materials.add(bevy::pbr::ExtendedMaterial {
                base: StandardMaterial {
                    base_color: label.color,
                    base_color_texture: Some(image),
                    alpha_mode: AlphaMode::Blend,
                    unlit: true,
                    double_sided: true,
                    cull_mode: None,
                    ..default()
                },
                extension: crate::info_billboard::Face {},
            })),
            Transform::from_translation(offset.extend(0.)),
            ChildOf(entity),
            NotShadowCaster,
            NotShadowReceiver,
            NoFrustumCulling,
        ));
        label.built = true;
    }
}
