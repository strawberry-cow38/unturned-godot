//! First-person arms and held content, using the original rig, hook transforms and
//! gun visual tables. The separate 60° camera keeps held meshes clear of world walls.
use crate::{
    content::{ContentCache, ContentError},
    entities::{CoreEvent, ItemCatalog},
    player::{GameSnapshot, PlayerCamera, PlayerUpdate},
    rig::{RigLibrary, RigPlayback, RigSkin, spawn_rig},
    ui::{Screen, UiState},
    world::WorldConfig,
};
use bevy::{
    camera::visibility::RenderLayers, mesh::skinning::SkinnedMeshInverseBindposes, prelude::*,
};
use std::{collections::HashMap, fs, path::Path};

const LAYER: usize = 1;
const ARMS_POSITION: Vec3 = Vec3::new(0., -1.75, 0.12);
#[derive(Clone, Debug)]
pub struct GunVisual {
    pub name: String,
    pub mesh: String,
    pub texture: String,
    pub muzzle: Vec3,
    pub aim: Vec3,
    pub sight: Option<String>,
    pub sight_position: Vec3,
    pub sight_color: Color,
    pub sight_texture: Option<String>,
    pub magazine: Option<String>,
    pub magazine_position: Vec3,
}
fn vector(text: &str) -> Result<Vec3, ContentError> {
    let a: Vec<_> = text.trim().split(',').map(str::parse::<f32>).collect();
    if a.len() != 3
        || a.iter()
            .any(|v| v.as_ref().map_or(true, |v| !v.is_finite()))
    {
        return Err(ContentError(format!("invalid hook vector '{text}'")));
    }
    Ok(Vec3::new(
        *a[0].as_ref().unwrap(),
        *a[1].as_ref().unwrap(),
        *a[2].as_ref().unwrap(),
    ))
}
pub fn load_gun_visuals(root: &Path) -> Result<HashMap<String, GunVisual>, ContentError> {
    let mut result = HashMap::new();
    for line in fs::read_to_string(root.join("guns_visual.tsv"))?.lines() {
        let f: Vec<_> = line.trim_end_matches('\r').split('\t').collect();
        if f.len() < 4 {
            continue;
        }
        result.insert(
            f[0].into(),
            GunVisual {
                name: f[0].into(),
                mesh: format!("{}_gun.txt", f[0]),
                texture: format!("{}_albedo.png", f[0]),
                muzzle: vector(f[1])?,
                aim: vector(f[2])?,
                sight: None,
                sight_position: Vec3::ZERO,
                sight_color: Color::srgb(0.3, 0.3, 0.3),
                sight_texture: None,
                magazine: f
                    .get(4)
                    .filter(|s| !s.trim().is_empty())
                    .map(|s| s.trim().to_string()),
                magazine_position: Vec3::new(0., 0.0166, 0.0238),
            },
        );
    }
    if let Ok(source) = fs::read_to_string(root.join("sights.tsv")) {
        for line in source.lines() {
            let f: Vec<_> = line.trim_end_matches('\r').split('\t').collect();
            if f.len() < 3 {
                continue;
            }
            if let Some(gun) = result.get_mut(f[0]) {
                gun.sight = Some(f[1].into());
                gun.sight_position = vector(f[2])?;
                if let Some(rgb) = f.get(3) {
                    let v = vector(rgb)?;
                    gun.sight_color = Color::srgb(v.x, v.y, v.z);
                }
                gun.sight_texture = f
                    .get(4)
                    .filter(|s| !s.trim().is_empty())
                    .map(|s| s.trim().into());
            }
        }
    }
    if let Ok(source) = fs::read_to_string(root.join("guns_maghook.tsv")) {
        for line in source.lines() {
            let f: Vec<_> = line.trim_end_matches('\r').split('\t').collect();
            if f.len() < 3 || f[1].trim().is_empty() {
                continue;
            }
            if let Some(gun) = result.get_mut(f[0]) {
                gun.magazine_position = vector(f[1])?;
                if !f[2].trim().is_empty() {
                    gun.magazine = Some(format!("mag_{}.txt", f[2].trim().to_ascii_lowercase()));
                }
            }
        }
    }
    Ok(result)
}
#[derive(Resource, Default)]
struct ViewmodelState {
    camera: Option<Entity>,
    arms: Option<Entity>,
    held: Option<u16>,
    gun: Option<Entity>,
    visuals: HashMap<String, GunVisual>,
    pending: Option<PendingHeld>,
    hold_clip: String,
}
struct PendingHeld {
    mesh: Option<String>,
    texture: Option<String>,
    gun: Option<GunVisual>,
    asset: String,
}
#[derive(Component)]
struct ViewmodelCamera;
#[derive(Component)]
struct HeldMesh;
pub struct ViewmodelPlugin;
impl Plugin for ViewmodelPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<ViewmodelState>().add_systems(
            Update,
            (build_viewmodel, attach_held_mesh, animate_viewmodel)
                .chain()
                .after(PlayerUpdate),
        );
    }
}
#[allow(clippy::too_many_arguments)]
fn build_viewmodel(
    mut commands: Commands,
    config: Res<WorldConfig>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    ui: Res<UiState>,
    cameras: Query<Entity, With<PlayerCamera>>,
    mut state: ResMut<ViewmodelState>,
    mut rigs: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut bindposes: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut active_cameras: Query<&mut Camera, With<ViewmodelCamera>>,
) {
    let visible =
        ui.screen != Screen::MainMenu && snapshot.0["connected"].as_bool().unwrap_or(false);
    for mut camera in &mut active_cameras {
        camera.is_active = visible;
    }
    let Ok(main_camera) = cameras.single() else {
        return;
    };
    if state.camera.is_none() {
        let camera = commands
            .spawn((
                Name::new("First-person viewmodel camera"),
                Camera3d::default(),
                Camera {
                    order: 1,
                    clear_color: ClearColorConfig::None,
                    is_active: visible,
                    ..default()
                },
                Projection::Perspective(PerspectiveProjection {
                    fov: 60_f32.to_radians(),
                    near: 0.01,
                    far: 20.,
                    ..default()
                }),
                RenderLayers::layer(LAYER),
                Transform::IDENTITY,
                ChildOf(main_camera),
                ViewmodelCamera,
            ))
            .id();
        commands.spawn((
            Name::new("Viewmodel fill"),
            DirectionalLight {
                illuminance: 2500.,
                shadow_maps_enabled: false,
                ..default()
            },
            Transform::from_rotation(Quat::from_euler(EulerRot::XYZ, -0.5, -0.6, 0.)),
            RenderLayers::layer(LAYER),
            ChildOf(camera),
        ));
        state.camera = Some(camera);
        match load_gun_visuals(&config.content_root) {
            Ok(table) => state.visuals = table,
            Err(e) => warn!("viewmodel table: {e}"),
        }
    }
    if !visible {
        return;
    }
    let held = snapshot.0["player"]["heldItemId"].as_u64().unwrap_or(0) as u16;
    if state.held == Some(held) {
        return;
    }
    if held != 0 && !catalog.items.contains_key(&held) {
        return;
    }
    if let Some(root) = state.arms.take() {
        commands.entity(root).despawn();
    }
    state.gun = None;
    state.pending = None;
    let item = catalog.items.get(&held);
    let asset = item
        .and_then(|i| i.asset.as_deref())
        .unwrap_or("")
        .to_owned();
    let gun = item
        .filter(|i| i.item_type == "gun")
        .and_then(|_| state.visuals.get(&asset))
        .cloned();
    if item.is_some_and(|i| i.item_type == "gun") && gun.is_none() {
        warn!("No original gun visual for held item {held} ({asset}); showing empty hands");
    }
    let cap = if asset.is_empty() {
        String::new()
    } else {
        asset[..1].to_ascii_uppercase() + &asset[1..]
    };
    let requested = if held == 0 {
        "Punch_Left".into()
    } else {
        format!("{cap}_Equip")
    };
    let fallback = if gun.is_some() {
        "Gun_Equip"
    } else {
        "Melee_Equip"
    };
    let path = config.content_root.join("rig.json");
    // Parse once before selecting the available original clip; spawn_rig shares it thereafter.
    if !rigs.data.contains_key(&path) {
        match crate::rig::RigData::load(&path) {
            Ok(data) => {
                rigs.data.insert(path.clone(), std::sync::Arc::new(data));
            }
            Err(e) => {
                error!("viewmodel rig: {e}");
                return;
            }
        }
    }
    let data = &rigs.data[&path];
    let clip = if data.anims.contains_key(&requested) {
        requested
    } else {
        fallback.into()
    };
    let material = materials.add(StandardMaterial {
        base_color: Color::srgb(0.82, 0.66, 0.52),
        perceptual_roughness: 1.,
        reflectance: 0.,
        ..default()
    });
    match spawn_rig(
        &mut commands,
        &mut rigs,
        &mut meshes,
        &mut bindposes,
        &path,
        material,
        Transform::from_translation(ARMS_POSITION),
        true,
        &clip,
    ) {
        Ok(root) => {
            commands.entity(root).insert(ChildOf(state.camera.unwrap()));
            state.arms = Some(root);
            state.held = Some(held);
            state.hold_clip = clip;
            state.pending = Some(PendingHeld {
                mesh: if gun.is_some() {
                    None
                } else {
                    item.and_then(|i| i.mesh.clone())
                },
                texture: item.and_then(|i| i.texture.clone()),
                gun,
                asset,
            });
        }
        Err(e) => error!("viewmodel construction: {e}"),
    }
}
#[allow(clippy::too_many_arguments)]
fn attach_held_mesh(
    mut commands: Commands,
    config: Res<WorldConfig>,
    mut state: ResMut<ViewmodelState>,
    mut rigs: Query<(&mut RigPlayback, &RigSkin)>,
    mut cache: ResMut<ContentCache>,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let Some(root) = state.arms else {
        return;
    };
    let Ok((mut playback, skin)) = rigs.get_mut(root) else {
        return;
    };
    let Some(pending) = state.pending.take() else {
        return;
    };
    commands.entity(skin.0).insert(RenderLayers::layer(LAYER));
    playback.looping = false;
    if state.held == Some(0) {
        playback.time = playback
            .data
            .anims
            .get(&playback.clip)
            .map_or(0., |c| c.length);
        return;
    }
    let left_hook = pending.asset.contains("bow") && !pending.asset.contains("crossbow");
    let Some(hook) = playback
        .bones
        .get(if left_hook { "Left_Hook" } else { "Right_Hook" })
        .copied()
    else {
        return;
    };
    let mesh = pending
        .gun
        .as_ref()
        .map(|g| g.mesh.as_str())
        .or(pending.mesh.as_deref());
    let Some(mesh) = mesh else {
        return;
    };
    let texture = pending
        .gun
        .as_ref()
        .map(|g| g.texture.as_str())
        .or(pending.texture.as_deref());
    let Ok((handle, _)) = cache.load_mesh(&config.content_root.join(mesh), &mut meshes) else {
        warn!("Missing held mesh {mesh}");
        return;
    };
    let texture = texture.filter(|p| config.content_root.join(p).is_file());
    let material = cache.material(texture, false, &assets, &mut materials);
    let gun_entity = commands
        .spawn((
            Name::new("Held item"),
            HeldMesh,
            Mesh3d(handle),
            MeshMaterial3d(material),
            Transform::from_rotation(Quat::from_rotation_z(std::f32::consts::FRAC_PI_2)),
            RenderLayers::layer(LAYER),
            ChildOf(hook),
        ))
        .id();
    state.gun = Some(gun_entity);
    if let Some(gun) = pending.gun {
        for (mesh, position, texture, color) in [
            (
                gun.sight,
                gun.sight_position,
                gun.sight_texture,
                gun.sight_color,
            ),
            (
                gun.magazine,
                gun.magazine_position,
                None,
                Color::srgb(0.07, 0.07, 0.08),
            ),
        ] {
            let Some(mesh) = mesh else {
                continue;
            };
            let Ok((handle, _)) = cache.load_mesh(&config.content_root.join(&mesh), &mut meshes)
            else {
                continue;
            };
            let texture = texture.or_else(|| {
                let candidate = mesh.replace(".txt", "_albedo.png");
                config
                    .content_root
                    .join(&candidate)
                    .is_file()
                    .then_some(candidate)
            });
            let material = materials.add(StandardMaterial {
                base_color: if texture.is_some() {
                    Color::WHITE
                } else {
                    color
                },
                base_color_texture: texture.map(|p| assets.load(p)),
                perceptual_roughness: 1.,
                reflectance: 0.,
                ..default()
            });
            commands.spawn((
                Name::new(mesh),
                Mesh3d(handle),
                MeshMaterial3d(material),
                Transform::from_translation(position),
                RenderLayers::layer(LAYER),
                ChildOf(gun_entity),
            ));
        }
        let extra = gun.mesh.replace("_gun.txt", "_barrel.txt");
        if extra != gun.mesh && config.content_root.join(&extra).is_file() {
            if let Ok((handle, _)) = cache.load_mesh(&config.content_root.join(&extra), &mut meshes)
            {
                let material = cache.material(Some(&gun.texture), false, &assets, &mut materials);
                commands.spawn((
                    Name::new(extra),
                    Mesh3d(handle),
                    MeshMaterial3d(material),
                    RenderLayers::layer(LAYER),
                    ChildOf(gun_entity),
                ));
            }
        }
    }
}
fn animate_viewmodel(
    mut events: MessageReader<CoreEvent>,
    state: Res<ViewmodelState>,
    mut rigs: Query<&mut RigPlayback>,
    snapshot: Res<GameSnapshot>,
) {
    let Some(root) = state.arms else {
        for _ in events.read() {}
        return;
    };
    let Ok(mut rig) = rigs.get_mut(root) else {
        return;
    };
    // These are server-produced facts. A rejected fire intent does not animate a shot.
    for event in events.read() {
        if event.kind == "fire"
            && snapshot.0["playerId"].as_u64()
                == event
                    .data
                    .get("playerId")
                    .or_else(|| event.data.get("PlayerId"))
                    .and_then(|v| v.as_u64())
        {
            if state.held == Some(0) {
                rig.clip = "Punch_Left".into();
                rig.time = 0.;
                rig.looping = false;
            }
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_gun_mount_tables_resolve_without_substitutes() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        let guns = load_gun_visuals(&root).unwrap();
        assert!(guns.len() > 50);
        let e = &guns["eaglefire"];
        assert_eq!(e.mesh, "eaglefire_gun.txt");
        assert!((e.magazine_position - Vec3::new(0., 0.0166, 0.0238)).length() < 0.0001);
        assert!(root.join(e.sight.as_ref().unwrap()).is_file());
        assert!(!guns.contains_key("nonexistent"));
    }
}
