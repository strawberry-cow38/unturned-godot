//! First-person arms and held content, using the original rig, hook transforms and
//! gun visual tables. The separate 60° camera keeps held meshes clear of world walls.
use crate::{
    content::{ContentCache, ContentError},
    entities::{CoreEvent, ItemCatalog},
    player::{GameSnapshot, PlayerCamera, PlayerUpdate},
    rig::{RigAdditive, RigAnimation, RigLibrary, RigPlayback, RigSkin, spawn_rig},
    settings::{GameAction, Settings},
    ui::{Screen, UiState},
    world::WorldConfig,
};
use bevy::{
    camera::visibility::RenderLayers, mesh::skinning::SkinnedMeshInverseBindposes, prelude::*,
};
use std::{collections::HashMap, fs, path::Path};

mod driving;
mod motion;
mod parts;
mod tools;
mod weapon_fx;
const LAYER: usize = 1;
const ARMS_POSITION: Vec3 = Vec3::new(0., -1.75, 0.12);
#[derive(Clone, Debug)]
pub struct GunVisual {
    pub name: String,
    pub mesh: String,
    pub texture: String,
    pub muzzle: Vec3,
    pub ejects: bool,
    pub aim: Vec3,
    pub sight: Option<String>,
    pub sight_position: Vec3,
    pub sight_color: Color,
    pub sight_texture: Option<String>,
    pub magazine: Option<String>,
    pub magazine_position: Vec3,
    pub tint: Color,
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
                ejects: f[3].trim() == "1",
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
                tint: f
                    .get(5)
                    .filter(|s| !s.trim().is_empty())
                    .map(|s| vector(s).map(|v| Color::srgb(v.x, v.y, v.z)))
                    .transpose()?
                    .unwrap_or(Color::WHITE),
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
                // The original table keeps zero sentinels for the two legacy
                // rifles; Viewmodel.cs resolves them to this authored mount.
                if gun.sight_position == Vec3::ZERO {
                    gun.sight_position = Vec3::new(0., 0.1312, -0.118);
                }
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
    frame: Option<Entity>,
    arms: Option<Entity>,
    held: Option<u16>,
    gun: Option<Entity>,
    visuals: HashMap<String, GunVisual>,
    pending: Option<PendingHeld>,
    hold_clip: String,
    asset: String,
    is_gun: bool,
    equip_elapsed: f32,
    aim_progress: f32,
    sprinting: bool,
    shot_hold: f32,
    use_clip: String,
    aim_hook: Vec3,
    motion: motion::Motion,
    position_offset: Vec3,
    rotation_offset: Vec3,
    previous_look: Option<Vec2>,
    hands_off: bool,
    hammer_delay: Option<(f32, f32)>,
    driving: bool,
    seated: bool,
    tool_active: bool,
    tool_random: u64,
}
#[derive(Resource, Default)]
pub struct ViewmodelStatus {
    pub held: u16,
    pub ready: bool,
    pub aiming: bool,
    pub aim_alpha: f32,
    tool: tools::Kind,
    tool_active: bool,
}
struct PendingHeld {
    mesh: Option<String>,
    texture: Option<String>,
    gun: Option<GunVisual>,
    asset: String,
    left_hook: bool,
}
#[derive(Component)]
struct ViewmodelCamera;
/// The original viewport world. Head-look rotates its camera independently;
/// already-ejected casings and sparks remain in this torso frame.
#[derive(Component)]
struct ViewmodelFrame;
#[derive(Component)]
struct HeldMesh;
pub struct ViewmodelPlugin;
impl Plugin for ViewmodelPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<ViewmodelState>()
            .init_resource::<ViewmodelStatus>()
            .add_plugins((tools::ToolPlugin, weapon_fx::WeaponEffectsPlugin))
            .add_systems(
                Update,
                (build_viewmodel, attach_held_mesh, animate_viewmodel)
                    .chain()
                    .after(PlayerUpdate)
                    .after(crate::entities::ApplyInteractions)
                    .before(RigAnimation),
            )
            .add_systems(
                Update,
                pose_viewmodel
                    .after(RigAnimation)
                    .after(crate::player::PlayerCameraUpdate),
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
    hand_use: Res<crate::use_items::HandUse>,
    occupancy: Res<crate::vehicle::Occupancy>,
) {
    let visible = !matches!(ui.screen, Screen::MainMenu | Screen::Dead)
        && snapshot.0["connected"].as_bool().unwrap_or(false)
        && snapshot.0["player"]["alive"].as_bool().unwrap_or(true)
        && occupancy.seat.is_none_or(|s| s.driver && !s.aircraft);
    for mut camera in &mut active_cameras {
        camera.is_active = visible;
    }
    let Ok(main_camera) = cameras.single() else {
        return;
    };
    if state.camera.is_none() {
        let frame = commands
            .spawn((
                Name::new("First-person viewport frame"),
                ViewmodelFrame,
                Transform::IDENTITY,
                Visibility::Inherited,
                ChildOf(main_camera),
            ))
            .id();
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
                ChildOf(frame),
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
            ChildOf(frame),
        ));
        state.camera = Some(camera);
        state.frame = Some(frame);
        match load_gun_visuals(&config.content_root) {
            Ok(table) => state.visuals = table,
            Err(e) => warn!("viewmodel table: {e}"),
        }
    }
    if !visible {
        return;
    }
    let held = hand_use
        .visual_item()
        .unwrap_or_else(|| snapshot.0["player"]["heldItemId"].as_u64().unwrap_or(0) as u16);
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
    let consumable = held_clips(&config.content_root, &asset);
    let requested = if held == 0 {
        "Punch_Left".into()
    } else if let Some(clips) = &consumable {
        clips.0.clone()
    } else if item.is_some_and(|i| i.is_deployable) {
        "Deploy_Equip".into()
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
            commands.entity(root).insert(ChildOf(state.frame.unwrap()));
            state.arms = Some(root);
            state.held = Some(held);
            state.hold_clip = clip;
            state.asset = asset.clone();
            state.is_gun = gun.is_some();
            state.equip_elapsed = 0.;
            state.aim_progress = 0.;
            state.sprinting = false;
            state.shot_hold = 0.;
            state.hands_off = false;
            state.hammer_delay = None;
            state.driving = false;
            state.seated = false;
            state.tool_active = false;
            state.motion = default();
            state.previous_look = None;
            state.use_clip = consumable
                .as_ref()
                .map_or_else(String::new, |clips| clips.1.clone());
            state.aim_hook = gun.as_ref().map_or(Vec3::ZERO, |gun| gun.aim);
            state.pending = Some(PendingHeld {
                mesh: if gun.is_some() {
                    None
                } else {
                    item.and_then(|i| i.mesh.clone())
                },
                texture: item.and_then(|i| i.texture.clone()),
                gun,
                asset,
                left_hook: item.is_some_and(|i| i.gun["leftHook"].as_bool().unwrap_or(false))
                    || consumable.is_some_and(|clips| clips.2),
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
    let left_hook = pending.left_hook;
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
    let cached = cache.material(texture, false, &assets, &mut materials);
    let material = if let Some(gun) = &pending.gun {
        let mut material = materials.get(&cached).cloned().unwrap_or_default();
        material.base_color = gun.tint;
        material.cull_mode = None;
        materials.add(material)
    } else {
        cached
    };
    let gun_entity = commands
        .spawn((
            Name::new("Held item"),
            HeldMesh,
            Mesh3d(handle),
            MeshMaterial3d(material.clone()),
            Transform::from_rotation(Quat::from_rotation_z(std::f32::consts::FRAC_PI_2)),
            RenderLayers::layer(LAYER),
            ChildOf(hook),
        ))
        .id();
    state.gun = Some(gun_entity);
    if pending.gun.is_none() {
        if let Ok(source) = fs::read_to_string(
            config
                .content_root
                .join(format!("{}_parts.tsv", pending.asset)),
        ) {
            match parts::spawn(
                &source,
                &config.content_root,
                gun_entity,
                material,
                &mut playback,
                &mut commands,
                &mut cache,
                &mut meshes,
                &mut materials,
                &assets,
            ) {
                Ok(count) if count > 0 => {
                    commands
                        .entity(gun_entity)
                        .remove::<(Mesh3d, MeshMaterial3d<StandardMaterial>)>()
                        .insert(Transform::IDENTITY);
                }
                Ok(_) => {}
                Err(error) => warn!("Held item hierarchy {}: {error}", pending.asset),
            }
        }
    }
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
                cull_mode: None,
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
fn held_clips(root: &Path, asset: &str) -> Option<(String, String, bool)> {
    for table in ["consumable_anims.tsv", "throwable_anims.tsv"] {
        let Ok(table) = fs::read_to_string(root.join(table)) else {
            continue;
        };
        for line in table.lines() {
            let f: Vec<_> = line.split('\t').map(str::trim).collect();
            if f.len() >= 3 && f[0] == asset {
                return Some((f[1].into(), f[2].into(), f.get(4) == Some(&"Left")));
            }
        }
    }
    None
}
fn own_clip(rig: &RigPlayback, asset: &str, suffix: &str, fallback: &str) -> String {
    let cap = asset.chars().next().map_or_else(String::new, |first| {
        first.to_uppercase().to_string() + &asset[first.len_utf8()..]
    });
    let own = format!("{cap}{suffix}");
    if rig.data.anims.contains_key(&own) {
        return own;
    }
    if asset == "mac10" {
        let donor = format!("Bulldog{suffix}");
        if rig.data.anims.contains_key(&donor) {
            return donor;
        }
    }
    fallback.into()
}
fn play_once(rig: &mut RigPlayback, clip: &str, speed: f32) {
    if rig.play(clip, false) {
        rig.time = 0.;
        rig.speed = speed;
    }
}
fn aim_ease(progress: f32) -> f32 {
    let t = progress.clamp(0., 1.);
    let s = t * t * t * (t * (t * 6. - 15.) + 10.);
    1. - (1. - s) * (1. - s)
}
fn animate_viewmodel(
    mut events: MessageReader<CoreEvent>,
    mut hand_events: MessageReader<crate::use_items::HandEvent>,
    hand_use: Res<crate::use_items::HandUse>,
    mut state: ResMut<ViewmodelState>,
    mut status: ResMut<ViewmodelStatus>,
    mut rigs: Query<&mut RigPlayback>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    time: Res<Time>,
    ui: Res<UiState>,
    recoil: Res<crate::recoil::Recoil>,
    occupancy: Res<crate::vehicle::Occupancy>,
) {
    let Some(root) = state.arms else {
        events.clear();
        return;
    };
    let Ok(mut rig) = rigs.get_mut(root) else {
        return;
    };
    rig.paused = crate::player::simulation_paused(&ui);
    let dt = if rig.paused { 0. } else { time.delta_secs() };
    if let Some(seat) = occupancy.seat {
        state.seated = true;
        let driving = seat.driver && !seat.aircraft;
        if driving && (!state.driving || rig.clip != "Idle_Drive") {
            rig.play("Idle_Drive", true);
        }
        state.driving = driving;
        state.aim_progress = 0.;
        state.sprinting = false;
        state.hammer_delay = None;
        state.tool_active = false;
        state.position_offset = Vec3::ZERO;
        state.rotation_offset = Vec3::ZERO;
        rig.additive = None;
        *status = ViewmodelStatus {
            held: state.held.unwrap_or(0),
            ..default()
        };
        events.clear();
        hand_events.clear();
        return;
    }
    if state.seated {
        state.seated = false;
        state.driving = false;
        let clip = state.hold_clip.clone();
        play_once(&mut rig, &clip, 1.);
        rig.time = rig.data.anims.get(&clip).map_or(0., |c| c.length);
        state.equip_elapsed = rig.time;
        state.motion = default();
        state.previous_look = None;
    }
    state.equip_elapsed += dt;
    state.shot_hold = (state.shot_hold - dt).max(0.);
    let held = state.held.unwrap_or(0);
    let item = catalog.items.get(&held);
    for event in hand_events.read() {
        use crate::use_items::HandEvent;
        match event {
            HandEvent::Start(id) if *id == held => {
                let clip = state.use_clip.clone();
                play_once(&mut rig, &clip, 1.);
            }
            HandEvent::Finish(id) if *id == held => {
                let clip = state.hold_clip.clone();
                play_once(&mut rig, &clip, 1.);
                state.equip_elapsed = 0.;
            }
            HandEvent::Cancel => {
                let clip = state.hold_clip.clone();
                play_once(&mut rig, &clip, 1.);
                rig.time = rig.data.anims.get(&clip).map_or(0., |c| c.length);
            }
            _ => {}
        }
    }
    for event in events
        .read()
        .filter(|e| e.data["playerId"] == snapshot.0["playerId"])
    {
        match event.kind.as_str() {
            "melee" if !item.is_some_and(|i| i.melee["repeated"].as_bool() == Some(true)) => {
                let strong = event.data["strong"].as_bool().unwrap_or(false);
                let clip = if held == 0 {
                    if strong { "Punch_Right" } else { "Punch_Left" }.into()
                } else {
                    own_clip(
                        &rig,
                        &state.asset,
                        if strong { "_Strong" } else { "_Weak" },
                        if strong { "Melee_Strong" } else { "Melee_Weak" },
                    )
                };
                play_once(&mut rig, &clip, 1.);
            }
            "reload" => {
                let clip = own_clip(&rig, &state.asset, "_Reload", "Gun_Reload");
                play_once(
                    &mut rig,
                    &clip,
                    event.data["speed"].as_f64().unwrap_or(1.) as f32,
                );
            }
            "rechamber" => {
                state.hammer_delay = weapon_fx::hammer_timing(&rig, &state.asset, &event.data)
            }
            "fire" => {
                state.shot_hold = 0.25;
                if let Some(item) = item {
                    let value = |key: &str| item.gun[key].as_f64().unwrap_or(0.) as f32;
                    let sample = |min: &str, max: &str, salt: u64| {
                        let x = event
                            .sequence
                            .wrapping_add(salt)
                            .wrapping_mul(6364136223846793005)
                            .wrapping_add(1442695040888963407);
                        value(min) + (value(max) - value(min)) * ((x >> 40) as f32 / 16777216.)
                    };
                    state.motion.shake.position += Vec3::new(
                        sample("shakeMinX", "shakeMaxX", 1),
                        sample("shakeMinY", "shakeMaxY", 2),
                        sample("shakeMinZ", "shakeMaxZ", 3),
                    ) * recoil.stance_multiplier();
                }
            }
            _ => {}
        }
    }
    let stance = snapshot.0["player"]["stance"].as_str().unwrap_or("stand");
    if let Some((delay, speed)) = &mut state.hammer_delay {
        *delay -= dt;
        if *delay <= 0. {
            let speed = *speed;
            state.hammer_delay = None;
            let clip = own_clip(&rig, &state.asset, "_Hammer", "");
            play_once(&mut rig, &clip, speed);
        }
    }
    let reloading = snapshot.0["weapon"]["reloading"].as_bool().unwrap_or(false);
    let hammering = snapshot.0["weapon"]["rechambering"]
        .as_bool()
        .unwrap_or(false);
    let moving = !ui.captures_input()
        && [
            GameAction::MoveLeft,
            GameAction::MoveRight,
            GameAction::MoveForward,
            GameAction::MoveBack,
        ]
        .iter()
        .any(|a| settings.pressed(*a, &keys, &mouse));
    status.held = held;
    status.ready = !hand_use.busy()
        && (held == 0
            || state.equip_elapsed
                >= rig
                    .data
                    .anims
                    .get(&state.hold_clip)
                    .map_or(0., |c| c.length));
    status.tool = if snapshot.0["connected"].as_bool().unwrap_or(false)
        && snapshot.0["player"]["alive"].as_bool().unwrap_or(true)
        && !matches!(stance, "swim" | "climb")
    {
        tools::Kind::of(item)
    } else {
        tools::Kind::None
    };
    status.tool_active = status.tool != tools::Kind::None
        && status.ready
        && !ui.captures_input()
        && settings.pressed(GameAction::Fire, &keys, &mouse)
        && !settings.pressed(GameAction::FreeLook, &keys, &mouse);
    if status.tool_active != state.tool_active {
        tools::set_active(&mut rig, &state.asset, status.tool_active);
        state.tool_active = status.tool_active;
    }
    if status.tool == tools::Kind::Saw && dt > 0. {
        let shake = tools::shake(&mut state.tool_random, status.tool_active);
        state.motion.shake.position += shake;
    }
    status.aiming = state.is_gun
        && status.ready
        && !ui.captures_input()
        && !settings.pressed(GameAction::FreeLook, &keys, &mouse)
        && !reloading
        && !hammering
        && !matches!(stance, "swim" | "climb" | "driving" | "sitting")
        && !(stance == "sprint" && moving)
        && settings.pressed(GameAction::Aim, &keys, &mouse);
    state.aim_progress =
        (state.aim_progress + if status.aiming { dt / 0.25 } else { -dt / 0.25 }).clamp(0., 1.);
    status.aim_alpha = aim_ease(state.aim_progress);
    rig.additive = state.is_gun.then(|| RigAdditive {
        clip: own_clip(&rig, &state.asset, "_Aim", "Gun_Aim"),
        weight: status.aim_alpha,
    });
    let sprint = state.is_gun
        && status.ready
        && !status.aiming
        && !reloading
        && !hammering
        && state.shot_hold <= 0.
        && ((stance == "sprint" && moving) || snapshot.0["weapon"]["firemode"] == 0);
    if sprint && !state.sprinting {
        let clip = own_clip(&rig, &state.asset, "_Sprint_Start", "");
        play_once(&mut rig, &clip, 1.);
    } else if !sprint && state.sprinting && !reloading && !hammering {
        let clip = state.hold_clip.clone();
        play_once(&mut rig, &clip, 1.);
        rig.time = rig.data.anims.get(&clip).map_or(0., |c| c.length);
    }
    state.sprinting = sprint;
    let hands_off = matches!(stance, "swim" | "climb");
    if hands_off {
        let clip = match (stance, moving) {
            ("swim", true) => "Move_Swim",
            ("swim", false) => "Idle_Swim",
            (_, true) => "Move_Climb",
            _ => "Idle_Climb",
        };
        rig.play(clip, true);
    } else if state.hands_off {
        let clip = state.hold_clip.clone();
        play_once(&mut rig, &clip, 1.);
        if held == 0 {
            rig.time = rig.data.anims.get(&clip).map_or(0., |c| c.length);
        } else {
            state.equip_elapsed = 0.;
        }
    }
    state.hands_off = hands_off;
    let look = Vec2::new(
        snapshot.0["player"]["pitch"].as_f64().unwrap_or(0.) as f32,
        snapshot.0["player"]["yaw"].as_f64().unwrap_or(0.) as f32,
    );
    let delta = state.previous_look.map_or(Vec2::ZERO, |last| {
        Vec2::new(
            look.x - last.x,
            (look.y - last.y + 180.).rem_euclid(360.) - 180.,
        )
    });
    state.previous_look = Some(look);
    let pressed = |a| settings.pressed(a, &keys, &mouse) as i32;
    let movement = if moving {
        Vec2::new(
            (pressed(GameAction::MoveRight) - pressed(GameAction::MoveLeft)) as f32,
            (pressed(GameAction::MoveForward) - pressed(GameAction::MoveBack)) as f32,
        )
    } else {
        Vec2::ZERO
    };
    let (position, rotation) = state
        .motion
        .update(dt, stance, movement, status.aim_alpha, delta);
    state.position_offset = position;
    state.rotation_offset = rotation;
}
fn relative_pose(
    entity: Entity,
    root: Entity,
    transforms: &Query<&Transform>,
    parents: &Query<&ChildOf>,
) -> Option<Mat4> {
    let mut entity = entity;
    let mut pose = Mat4::IDENTITY;
    for _ in 0..64 {
        if entity == root {
            return Some(pose);
        }
        pose = transforms.get(entity).ok()?.to_matrix() * pose;
        entity = parents.get(entity).ok()?.parent();
    }
    None
}
fn pose_viewmodel(
    state: Res<ViewmodelState>,
    status: Res<ViewmodelStatus>,
    hand_use: Res<crate::use_items::HandUse>,
    parents: Query<&ChildOf>,
    mut transforms: ParamSet<(Query<&Transform>, Query<&mut Transform>)>,
    mut cameras: Query<&mut Projection, (With<ViewmodelCamera>, Without<PlayerCamera>)>,
    mut held_visibility: Query<&mut Visibility, With<HeldMesh>>,
    occupancy: Res<crate::vehicle::Occupancy>,
    rigs: Query<&RigPlayback>,
    main_camera: Query<(Entity, &Projection), (With<PlayerCamera>, Without<ViewmodelCamera>)>,
    vehicles: Query<&crate::vehicle::VehicleBody>,
) {
    for mut projection in &mut cameras {
        if let Projection::Perspective(p) = &mut *projection {
            // Source OversizeFov takes a vertical 60 degrees and converts it to
            // the oversized viewport's horizontal FOV. At native screen size
            // the same framing is simply a vertical 60 degrees.
            p.fov = 60_f32.to_radians();
        }
    }
    let Some(root) = state.arms else { return };
    let mut aim = None;
    if let Some(gun) = state.gun {
        if let Ok(mut visibility) = held_visibility.get_mut(gun) {
            *visibility = if state.driving || state.hands_off || hand_use.hide_item() {
                Visibility::Hidden
            } else {
                Visibility::Inherited
            };
        }
        if state.is_gun && !state.driving {
            if let Some(parent_pose) = parents
                .get(gun)
                .ok()
                .and_then(|p| relative_pose(p.parent(), root, &transforms.p0(), &parents))
            {
                let (_, parent_rotation, _) = parent_pose.to_scale_rotation_translation();
                let rr =
                    state.motion.recoil.position * (1. - status.aim_alpha) * std::f32::consts::PI
                        / 180.;
                let recoil = Quat::from_euler(EulerRot::YXZ, rr.y, rr.x, rr.z);
                let rotation = parent_rotation.inverse()
                    * recoil
                    * parent_rotation
                    * Quat::from_rotation_z(std::f32::consts::FRAC_PI_2);
                let gun_pose = Transform::from_rotation(rotation);
                aim = Some(
                    ARMS_POSITION
                        + (parent_pose * gun_pose.to_matrix()).transform_point3(state.aim_hook),
                );
                if let Ok(mut transform) = transforms.p1().get_mut(gun) {
                    *transform = gun_pose;
                }
            }
        }
    }
    if state.driving {
        let rig = rigs.get(root).ok();
        let t = transforms.p0();
        let bone = |name: &str| {
            rig.and_then(|rig| rig.bones.get(name))
                .and_then(|entity| relative_pose(*entity, root, &t, &parents))
                .map(|p| p.transform_point3(Vec3::ZERO))
        };
        let hands = bone("Left_Hand")
            .zip(bone("Right_Hand"))
            .map(|(l, r)| (l + r) * 0.5);
        let skull = bone("Skull");
        let pinned = occupancy.seat.and_then(|seat| {
            let (pivot, axis, steer) = vehicles.get(seat.vehicle).ok()?.steering_wheel()?;
            let (camera, projection) = main_camera.single().ok()?;
            let camera = t.get(camera).ok()?;
            let vehicle = t.get(seat.vehicle).ok()?;
            let Projection::Perspective(projection) = projection else {
                return None;
            };
            let inverse = camera.to_matrix().inverse();
            let wheel = inverse.transform_point3(vehicle.transform_point(pivot));
            let basis = camera.rotation.inverse() * vehicle.rotation;
            Some(driving::pin(
                driving::wheel_target(wheel, projection.fov),
                hands?,
                basis,
                basis * axis,
                steer,
            ))
        });
        let pose = pinned.unwrap_or_else(|| {
            Transform::from_translation(
                skull
                    .filter(|p| p.length_squared() >= 0.01)
                    .map_or(ARMS_POSITION, |p| -(p + Vec3::new(0., 0.16, -0.10))),
            )
        });
        if let Ok(mut transform) = transforms.p1().get_mut(root) {
            *transform = pose;
        }
        return;
    }
    if let Ok(mut transform) = transforms.p1().get_mut(root) {
        transform.translation =
            ARMS_POSITION + state.position_offset - aim.unwrap_or(Vec3::ZERO) * status.aim_alpha;
        let r = state.rotation_offset * std::f32::consts::PI / 180.;
        transform.rotation = Quat::from_euler(EulerRot::YXZ, r.y, r.x, r.z);
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn real_rig_enters_driving_hides_held_item_and_restores_hold_after_passenger_exit() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins,TransformPlugin,AssetPlugin::default()))
            .init_asset::<Mesh>().init_asset::<Image>().init_asset::<StandardMaterial>()
            .init_asset::<SkinnedMeshInverseBindposes>()
            .init_resource::<WorldConfig>().init_resource::<ContentCache>().init_resource::<ItemCatalog>()
            .init_resource::<crate::world::WorldGeometry>().init_resource::<crate::impact::surfaces::SurfaceTable>()
            .init_resource::<crate::player::WorldSessionEpoch>().init_asset::<AudioSource>()
            .init_resource::<Settings>().init_resource::<crate::use_items::HandUse>()
            .init_resource::<crate::vehicle::Occupancy>().init_resource::<crate::recoil::Recoil>()
            .init_resource::<ButtonInput<KeyCode>>().init_resource::<ButtonInput<MouseButton>>()
            .add_message::<CoreEvent>().add_message::<crate::use_items::HandEvent>()
            .insert_resource(UiState{screen:Screen::Playing,..default()})
            .insert_resource(GameSnapshot(serde_json::json!({"connected":true,"playerId":1,"player":{"alive":true,"heldItemId":0,"stance":"stand"}})))
            .add_plugins((crate::rig::RigPlugin,ViewmodelPlugin));
        app.world_mut().spawn((
            PlayerCamera,
            Camera::default(),
            Projection::default(),
            Transform::IDENTITY,
        ));
        app.update();
        app.update();
        let root = app.world().resource::<ViewmodelState>().arms.unwrap();
        let camera = app.world().resource::<ViewmodelState>().camera.unwrap();
        let held = app
            .world_mut()
            .spawn((
                HeldMesh,
                Visibility::Inherited,
                Transform::IDENTITY,
                ChildOf(root),
            ))
            .id();
        app.world_mut().resource_mut::<ViewmodelState>().gun = Some(held);
        let vehicle = app.world_mut().spawn(Transform::IDENTITY).id();
        app.world_mut()
            .resource_mut::<crate::vehicle::Occupancy>()
            .seat = Some(crate::vehicle::OccupiedSeat {
            vehicle,
            net_id: 7,
            seat: 0,
            body_pose: Transform::IDENTITY,
            eye_pose: Transform::IDENTITY,
            velocity: Vec3::ZERO,
            driver: true,
            aircraft: false,
        });
        app.update();
        app.update();
        assert_eq!(
            app.world().get::<RigPlayback>(root).unwrap().clip,
            "Idle_Drive"
        );
        assert_eq!(
            *app.world().get::<Visibility>(held).unwrap(),
            Visibility::Hidden
        );
        assert!(app.world().get::<Camera>(camera).unwrap().is_active);
        assert!(!app.world().resource::<ViewmodelStatus>().ready);
        // Switching to a passenger must hide the overlay; exiting afterwards
        // still restores the equipped pose rather than leaving driving arms.
        app.world_mut()
            .resource_mut::<crate::vehicle::Occupancy>()
            .seat
            .as_mut()
            .unwrap()
            .driver = false;
        app.update();
        assert!(!app.world().get::<Camera>(camera).unwrap().is_active);
        app.world_mut()
            .resource_mut::<crate::vehicle::Occupancy>()
            .seat = None;
        app.update();
        assert_eq!(
            app.world().get::<RigPlayback>(root).unwrap().clip,
            "Punch_Left"
        );
        assert_eq!(
            *app.world().get::<Visibility>(held).unwrap(),
            Visibility::Inherited
        );
        assert!(app.world().get::<Camera>(camera).unwrap().is_active);
        assert!(app.world().resource::<ViewmodelStatus>().ready);
    }
    #[test]
    fn aim_ease_and_consumable_hand_clips_match_source() {
        assert_eq!(aim_ease(-1.), 0.);
        assert_eq!(aim_ease(0.5), 0.75);
        assert_eq!(aim_ease(2.), 1.);
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        assert_eq!(
            held_clips(&root, "bag_chips"),
            Some(("CE_2".into(), "CU_2".into(), true))
        );
        let data = crate::rig::RigData::load(&root.join("rig.json")).unwrap();
        assert!(data.anims.contains_key("CU_2"));
        assert!(data.anims.contains_key("TU_0"));
        assert!(
            data.anims["CU_2"]
                .tracks
                .keys()
                .any(|key| key.starts_with("Item_Root/"))
        );
    }
    #[test]
    fn original_gun_mount_tables_resolve_without_substitutes() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        let guns = load_gun_visuals(&root).unwrap();
        assert!(guns.len() > 50);
        let e = &guns["eaglefire"];
        assert_eq!(e.mesh, "eaglefire_gun.txt");
        assert_eq!(e.sight_position, Vec3::new(0., 0.1312, -0.118));
        assert!((e.magazine_position - Vec3::new(0., 0.0166, 0.0238)).length() < 0.0001);
        assert!(root.join(e.sight.as_ref().unwrap()).is_file());
        assert!(!guns.contains_key("nonexistent"));
    }
}
