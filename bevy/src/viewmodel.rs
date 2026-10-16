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

pub(crate) mod attachments;
mod driving;
mod inspect;
pub(crate) mod lighting;
pub(crate) mod lights;
mod motion;
mod parts;
pub(crate) mod scopes;
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
    pub sight_hook: Option<Vec3>,
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
                sight_hook: None,
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
    if let Ok(source) = fs::read_to_string(root.join("guns_sighthook.tsv")) {
        for line in source.lines() {
            let fields: Vec<_> = line.split('\t').collect();
            if fields.len() >= 2 && !fields[1].trim().is_empty() {
                if let Some(gun) = result.get_mut(fields[0]) {
                    gun.sight_hook = Some(vector(fields[1])?);
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
    selection: serde_json::Value,
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
    scope_zoom: f32,
    scope_sway: motion::ScopeSway,
    motion: motion::Motion,
    position_offset: Vec3,
    rotation_offset: Vec3,
    hands_off: bool,
    hammer_delay: Option<(f32, f32)>,
    driving: bool,
    mounted: bool,
    seated: bool,
    attaching: bool,
    tool_active: bool,
    tool_random: u64,
    inspect_time: Option<f32>,
}
#[derive(Resource, Default)]
pub struct ViewmodelStatus {
    pub held: u16,
    pub ready: bool,
    pub aiming: bool,
    pub aim_alpha: f32,
    pub scope_zoom: f32,
    pub scope_sway: Vec2,
    pub look_delta: Vec2,
    pub muzzle_offset: Option<Vec3>,
    pub inspecting: bool,
    inspect_gun: bool,
    cancel_inspect: bool,
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
pub(crate) struct ViewmodelCamera;
/// The original viewport world. Head-look rotates its camera independently;
/// already-ejected casings and sparks remain in this torso frame.
#[derive(Component)]
struct ViewmodelFrame;
#[derive(Component)]
pub(crate) struct HeldMesh;
#[derive(Resource, Default)]
pub struct AttachmentAnchors(pub HashMap<String, Vec2>);
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ProjectAttachmentHooks;
impl ViewmodelStatus {
    /// Original StartFire consumes the click that cancels a gun inspection.
    pub fn cancel_inspect_from_fire(&mut self) -> bool {
        if self.inspecting && self.inspect_gun {
            self.cancel_inspect = true;
            true
        } else {
            false
        }
    }
    pub fn look_sensitivity(&self) -> f32 {
        scopes::sensitivity(self.aim_alpha, self.scope_zoom)
    }
}
pub struct ViewmodelPlugin;
impl Plugin for ViewmodelPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<AttachmentAnchors>()
            .init_resource::<crate::entities::InteractionFocus>()
            .init_resource::<ViewmodelState>()
            .init_resource::<ViewmodelStatus>()
            .init_resource::<attachments::Mounted>()
            .add_plugins((
                tools::ToolPlugin,
                weapon_fx::WeaponEffectsPlugin,
                scopes::ScopePlugin,
                lights::LightPlugin,
                lighting::LightingPlugin,
            ))
            .add_systems(
                Update,
                (
                    build_viewmodel,
                    attach_held_mesh,
                    attachments::sync,
                    inspect::reset_after_session,
                    animate_viewmodel,
                )
                    .chain()
                    .after(PlayerUpdate)
                    .after(crate::entities::ApplyInteractions)
                    .before(RigAnimation),
            )
            .add_systems(
                PostUpdate,
                (project_attachment_hooks, project_muzzle)
                    .in_set(ProjectAttachmentHooks)
                    .after(bevy::camera::CameraUpdateSystems)
                    .after(bevy::transform::TransformSystems::Propagate),
            )
            .add_systems(
                PostUpdate,
                attachments::billboard.after(bevy::transform::TransformSystems::Propagate),
            )
            .add_systems(
                Update,
                (head_look, pose_viewmodel)
                    .chain()
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
    perspective: Option<Res<crate::avatar::Perspective>>,
    mounted: Option<Res<crate::vehicle::turrets::Operator>>,
) {
    state.mounted = mounted.as_ref().is_some_and(|m| m.active().is_some());
    let body_enabled = !matches!(ui.screen, Screen::MainMenu | Screen::Dead)
        && snapshot.0["connected"].as_bool().unwrap_or(false)
        && snapshot.0["player"]["alive"].as_bool().unwrap_or(true);
    let visible = body_enabled && !state.mounted && occupancy.seat.is_none_or(|s| !s.driver || !s.aircraft);
    for mut camera in &mut active_cameras {
        camera.is_active = visible && perspective.as_ref().is_none_or(|p| p.first_person);
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
                    is_active: visible && perspective.as_ref().is_none_or(|p| p.first_person),
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
        lighting::spawn(&mut commands, frame, camera);
        state.camera = Some(camera);
        state.frame = Some(frame);
        match load_gun_visuals(&config.content_root) {
            Ok(table) => state.visuals = table,
            Err(e) => warn!("viewmodel table: {e}"),
        }
    }
    if !body_enabled {
        return;
    }
    let held = hand_use
        .visual_item()
        .unwrap_or_else(|| snapshot.0["player"]["heldItemId"].as_u64().unwrap_or(0) as u16);
    if state.held == Some(held) && state.selection == snapshot.0["weapon"]["selectionVersion"] {
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
    // Vehicle hand tools carry their own Equip/Use pair (EquipHeldSpraypaint/Tire/Carjack).
    let consumable =
        held_clips(&config.content_root, &asset).or_else(|| item.and_then(hand_tool_clips));
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
            commands.entity(root).insert((
                ChildOf(state.frame.unwrap()),
                crate::avatar::FirstPersonArms,
            ));
            state.arms = Some(root);
            state.held = Some(held);
            state.selection = snapshot.0["weapon"]["selectionVersion"].clone();
            state.attaching = false;
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
            state.inspect_time = None;
            state.motion = default();
            state.use_clip = consumable
                .as_ref()
                .map_or_else(String::new, |clips| clips.1.clone());
            state.aim_hook = gun.as_ref().map_or(Vec3::ZERO, |gun| gun.aim);
            state.pending = Some(PendingHeld {
                mesh: if gun.is_some() {
                    None
                } else {
                    item.and_then(|i| i.mesh.clone().or_else(|| tool_path(i, "mesh")))
                },
                texture: item.and_then(|i| i.texture.clone().or_else(|| tool_path(i, "texture"))),
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
    catalog: Res<ItemCatalog>,
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
    let light_item = catalog
        .items
        .get(&state.held.unwrap_or(0))
        .filter(|i| i.melee["light"].as_bool() == Some(true));
    let cached = cache.material(texture, false, &assets, &mut materials);
    // EquipHeldSpraypaint: the can is tinted flat by the paint it sprays.
    let paint_tint = catalog
        .items
        .get(&state.held.unwrap_or(0))
        .and_then(|i| i.hand_tool["paintRgb"].as_u64());
    let material = if let Some(gun) = &pending.gun {
        let mut material = materials.get(&cached).cloned().unwrap_or_default();
        material.base_color = gun.tint;
        material.cull_mode = None;
        materials.add(material)
    } else if let Some(rgb) = paint_tint {
        let mut material = materials.get(&cached).cloned().unwrap_or_default();
        let [r, g, b, _] = crate::vehicle::hand_tools::paint_colour(rgb as u32);
        material.base_color = Color::srgb(r, g, b);
        materials.add(material)
    } else if light_item.is_some() {
        let material = materials.get(&cached).cloned().unwrap_or_default();
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
    if let Some(item) = light_item {
        commands
            .entity(gun_entity)
            .insert(lights::LightSurface(item.id));
    }
    if let Some(gun) = pending.gun {
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
fn hand_tool_clips(item: &crate::entities::ItemVisual) -> Option<(String, String, bool)> {
    let tool = &item.hand_tool;
    Some((tool["equip"].as_str()?.into(), tool["use"].as_str()?.into(), false))
}
fn tool_path(item: &crate::entities::ItemVisual, key: &str) -> Option<String> {
    item.hand_tool[key].as_str().map(Into::into)
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
pub(crate) fn own_clip(rig: &RigPlayback, asset: &str, suffix: &str, fallback: &str) -> String {
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
    focus: Res<crate::entities::InteractionFocus>,
) {
    status.scope_zoom = state.scope_zoom;
    let Some(root) = state.arms else {
        events.clear();
        return;
    };
    let Ok(mut rig) = rigs.get_mut(root) else {
        return;
    };
    rig.paused = crate::player::simulation_paused(&ui);
    let dt = if rig.paused { 0. } else { time.delta_secs() };
    if let Some(seat) = occupancy.seat.filter(|seat| seat.driver || state.mounted) {
        state.seated = true;
        let driving = seat.driver && !seat.aircraft && !state.mounted;
        if driving && (!state.driving || rig.clip != "Idle_Drive") {
            rig.play("Idle_Drive", true);
        }
        state.driving = driving;
        state.aim_progress = 0.;
        state.sprinting = false;
        state.hammer_delay = None;
        state.tool_active = false;
        state.inspect_time = None;
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
    }
    state.equip_elapsed += dt;
    state.shot_hold = (state.shot_hold - dt).max(0.);
    let held = state.held.unwrap_or(0);
    let item = catalog.items.get(&held);
    if std::mem::take(&mut status.cancel_inspect) && state.inspect_time.take().is_some() {
        inspect::ready_hold(&mut rig, &state.hold_clip);
    }
    if let Some(remaining) = &mut state.inspect_time {
        *remaining -= dt;
        if *remaining <= 0. {
            state.inspect_time = None;
        }
    }
    for event in hand_events.read() {
        use crate::use_items::HandEvent;
        match event {
            HandEvent::Start(id) if *id == held => {
                state.inspect_time = None;
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
                state.inspect_time = None;
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
                state.inspect_time = None;
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
            "fire"
                if (event.data["selectionVersion"].is_null()
                    || event.data["selectionVersion"] == state.selection)
                    && event.data["gun"]
                        .as_str()
                        .is_none_or(|gun| gun == state.asset) =>
            {
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
    let attaching = ui.screen == Screen::Attachments && state.is_gun;
    // Accepted reloads temporarily replace the attachment pose. Restore its
    // authored clip once both the authority clock and rendered action finish;
    // otherwise a held menu can strand its projected choices below the viewport.
    let resume_attachment = attaching
        && state.attaching
        && !reloading
        && !hammering
        && snapshot.0["weapon"]["unloading"] != true
        && state.hammer_delay.is_none()
        && (rig.clip.ends_with("_Reload") || rig.clip.ends_with("_Hammer"))
        && rig.data.anims.get(&rig.clip).is_some_and(|clip| rig.time >= clip.length);
    if attaching != state.attaching || resume_attachment {
        if attaching {
            state.inspect_time = None;
            let clip = own_clip(&rig, &state.asset, "_AttachStart", "");
            if !clip.is_empty() {
                play_once(&mut rig, &clip, 1.);
            }
        } else {
            let clip = state.hold_clip.clone();
            play_once(&mut rig, &clip, 1.);
            rig.time = rig.data.anims.get(&clip).map_or(0., |c| c.length);
        }
        state.attaching = attaching;
    }
    let moving = occupancy.seat.is_none()
        && !matches!(stance, "sitting" | "driving")
        && !ui.captures_input()
        && [
            GameAction::MoveLeft,
            GameAction::MoveRight,
            GameAction::MoveForward,
            GameAction::MoveBack,
        ]
        .iter()
        .any(|a| settings.pressed(*a, &keys, &mouse));
    let sprinting_now = stance == "sprint" && moving;
    let alive = snapshot.0["connected"].as_bool().unwrap_or(false)
        && snapshot.0["player"]["alive"].as_bool().unwrap_or(true);
    if state.inspect_time.is_some()
        && (sprinting_now || !alive || matches!(stance, "swim" | "climb"))
    {
        state.inspect_time = None;
        inspect::ready_hold(&mut rig, &state.hold_clip);
    }
    let wants_inspect = alive
        && !ui.captures_input()
        && occupancy.seat.is_none()
        && !focus.has_candidate()
        && settings.just_pressed(GameAction::Interact, &keys, &mouse);
    let started_inspect = wants_inspect
        && !reloading
        && !sprinting_now
        && !attaching
        && !matches!(stance, "swim" | "climb")
        && state.inspect_time.is_none()
        && inspect::clip(
            &rig,
            &state.asset,
            state.is_gun,
            item.is_some_and(|i| !i.melee.is_null()),
        )
        .is_some_and(|clip| {
            state.inspect_time = Some(rig.data.anims[&clip].length);
            play_once(&mut rig, &clip, 1.);
            true
        });
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
        && occupancy.seat.is_none()
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
        && !started_inspect
        && status.ready
        && !ui.captures_input()
        && !settings.pressed(GameAction::FreeLook, &keys, &mouse)
        && !reloading
        && !hammering
        && !matches!(stance, "swim" | "climb" | "driving")
        && !(stance == "sprint" && moving)
        && settings.pressed(GameAction::Aim, &keys, &mouse);
    if status.aiming && state.inspect_time.take().is_some() {
        inspect::ready_hold(&mut rig, &state.hold_clip);
    }
    status.inspecting = state.inspect_time.is_some();
    status.inspect_gun = state.is_gun;
    state.aim_progress =
        (state.aim_progress + if status.aiming { dt / 0.25 } else { -dt / 0.25 }).clamp(0., 1.);
    status.aim_alpha = aim_ease(state.aim_progress);
    rig.additive = state.is_gun.then(|| RigAdditive {
        clip: own_clip(&rig, &state.asset, "_Aim", "Gun_Aim"),
        weight: status.aim_alpha,
    });
    let sprint = state.is_gun
        && !attaching
        && status.ready
        && !status.aiming
        && !reloading
        && !hammering
        && !status.inspecting
        && state.shot_hold <= 0.
        && ((stance == "sprint" && moving) || snapshot.0["weapon"]["firemode"] == 0);
    if sprint && !state.sprinting {
        let clip = own_clip(&rig, &state.asset, "_Sprint_Start", "");
        play_once(&mut rig, &clip, 1.);
    } else if !sprint
        && state.sprinting
        && !reloading
        && !hammering
        && !attaching
        && !status.inspecting
    {
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
    let pressed = |a| settings.pressed(a, &keys, &mouse) as i32;
    let movement = if moving {
        Vec2::new(
            (pressed(GameAction::MoveRight) - pressed(GameAction::MoveLeft)) as f32,
            (pressed(GameAction::MoveForward) - pressed(GameAction::MoveBack)) as f32,
        )
    } else {
        Vec2::ZERO
    };
    let (position, rotation) = state.motion.update(
        dt,
        stance,
        movement,
        status.aim_alpha,
        status.look_delta,
        status.aiming && status.scope_zoom > 1.,
    );
    status.scope_sway = state.scope_sway.update(
        dt,
        status.aiming,
        status.scope_zoom,
        stance,
        item.and_then(|i| i.gun["scopeSwayScale"].as_f64())
            .unwrap_or(1.) as f32,
        snapshot.0["steady"]["rateScale"].as_f64().unwrap_or(1.) as f32,
    );
    state.position_offset = position;
    state.rotation_offset = rotation;
}
fn project_attachment_hooks(
    state: Res<ViewmodelState>,
    ui: Res<UiState>,
    cameras: Query<(&Camera, &GlobalTransform), With<ViewmodelCamera>>,
    transforms: Query<&GlobalTransform>,
    mut anchors: ResMut<AttachmentAnchors>,
    viewport: Option<Res<crate::hdr::viewport::ViewmodelViewport>>,
) {
    anchors.0.clear();
    if ui.screen != Screen::Attachments {
        return;
    }
    let Some(gun) = state.gun.and_then(|e| transforms.get(e).ok()) else {
        return;
    };
    let Ok((camera, view)) = cameras.single() else {
        return;
    };
    for (slot, hook) in [
        ("Sight", Vec3::new(0., -0.2398, -0.1386)),
        ("Tactical", Vec3::new(-0.0601, 0.3815, -0.0851)),
        ("Barrel", Vec3::new(0., 0.7307, -0.0818)),
        ("Grip", Vec3::new(0., 0.2595, -0.0226)),
        ("Magazine", Vec3::new(0., 0.0166, 0.0238)),
    ] {
        if let Ok(screen) = camera.world_to_viewport(view, gun.transform_point(hook)) {
            anchors.0.insert(
                slot.into(),
                viewport
                    .as_ref()
                    .map_or(screen, |viewport| viewport.viewport_to_screen(screen)),
            );
        }
    }
}
fn project_muzzle(
    state: Res<ViewmodelState>,
    catalog: Res<ItemCatalog>,
    cameras: Query<(&Camera, &GlobalTransform), With<ViewmodelCamera>>,
    world_cameras: Query<(&Camera, &GlobalTransform), With<PlayerCamera>>,
    transforms: Query<&GlobalTransform>,
    mut status: ResMut<ViewmodelStatus>,
    viewport: Option<Res<crate::hdr::viewport::ViewmodelViewport>>,
) {
    status.muzzle_offset = None;
    if !state.is_gun || state.hands_off || state.driving || state.mounted {
        return;
    }
    let Some(visual) = state.visuals.get(&state.asset) else {
        return;
    };
    let Some(gun) = state.gun.and_then(|entity| transforms.get(entity).ok()) else {
        return;
    };
    let (Ok((camera, view)), Ok((world_camera, world_view))) =
        (cameras.single(), world_cameras.single())
    else {
        return;
    };
    if !camera.is_active {
        return;
    }
    let Some(metadata) = &catalog.bullet_visuals else {
        return;
    };
    status.muzzle_offset = crate::bullets::muzzle_projection(
        gun.transform_point(visual.muzzle),
        view.to_matrix(),
        viewport.as_ref().map_or(camera.clip_from_view(), |viewport| {
            viewport.cropped_projection(camera.clip_from_view())
        }),
        world_camera.clip_from_view(),
        world_view.rotation(),
        metadata.muzzle_anchor_depth,
    );
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
            // Nominal source framing; HDR expands the actual target/FOV in
            // PostUpdate before Bevy updates camera projection matrices.
            p.fov = crate::hdr::viewport::SOURCE_FOV;
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
fn head_look(
    view: Option<Res<crate::avatar::Perspective>>,
    input: Option<Res<crate::player::PlayerInput>>,
    occupancy: Res<crate::vehicle::Occupancy>,
    mut frames: Query<&mut Transform, (With<ViewmodelFrame>, Without<ViewmodelCamera>)>,
    mut cameras: Query<&mut Transform, (With<ViewmodelCamera>, Without<ViewmodelFrame>)>,
) {
    let rotation = view
        .zip(input)
        .filter(|(view, _)| view.first_person && occupancy.seat.is_none())
        .map_or(Quat::IDENTITY, |(view, input)| {
            let body = Quat::from_euler(
                EulerRot::YXZ,
                input.yaw().to_radians(),
                input.pitch().to_radians(),
                0.,
            );
            let head = Quat::from_euler(
                EulerRot::YXZ,
                (input.yaw() + view.head.x).to_radians(),
                (input.pitch() + view.head.y).clamp(-89., 89.).to_radians(),
                0.,
            );
            body.inverse() * head
        });
    for mut frame in &mut frames {
        frame.rotation = rotation.inverse();
    }
    for mut camera in &mut cameras {
        camera.rotation = rotation;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn alt_look_rotates_view_camera_while_held_frame_remains_on_body_heading() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, TransformPlugin))
            .init_resource::<crate::avatar::Perspective>()
            .init_resource::<crate::player::PlayerInput>()
            .init_resource::<crate::vehicle::Occupancy>()
            .add_systems(Update, head_look);
        let main = app
            .world_mut()
            .spawn(Transform::from_rotation(Quat::from_rotation_y(
                90_f32.to_radians(),
            )))
            .id();
        let frame = app
            .world_mut()
            .spawn((ViewmodelFrame, Transform::IDENTITY, ChildOf(main)))
            .id();
        let camera = app
            .world_mut()
            .spawn((ViewmodelCamera, Transform::IDENTITY, ChildOf(frame)))
            .id();
        app.world_mut()
            .resource_mut::<crate::avatar::Perspective>()
            .head = Vec2::new(90., 0.);
        app.update();
        let world_frame = app
            .world()
            .get::<GlobalTransform>(frame)
            .unwrap()
            .rotation();
        let world_camera = app
            .world()
            .get::<GlobalTransform>(camera)
            .unwrap()
            .rotation();
        assert!(
            (world_frame * Vec3::NEG_Z - Vec3::NEG_Z).length() < 1e-5,
            "gun stays on original body heading while head turns"
        );
        assert!(
            (world_camera * Vec3::NEG_Z - Vec3::NEG_X).length() < 1e-5,
            "camera follows the ninety-degree head turn"
        );
        assert!(
            (world_camera * Vec3::Y - Vec3::Y).length() < 1e-5,
            "head turn preserves camera up"
        );
        app.world_mut()
            .resource_mut::<crate::avatar::Perspective>()
            .head = Vec2::ZERO;
        app.update();
        assert_eq!(
            app.world().get::<Transform>(frame).unwrap().rotation,
            Quat::IDENTITY
        );
        assert_eq!(
            app.world().get::<Transform>(camera).unwrap().rotation,
            Quat::IDENTITY
        );
    }

    #[test]
    fn real_rig_enters_driving_then_restores_visible_ready_weapon_in_passenger_seat() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins,TransformPlugin,AssetPlugin::default(),
            bevy::camera::visibility::VisibilityPlugin))
            .init_asset::<Mesh>().init_asset::<Image>().init_asset::<StandardMaterial>()
            .init_asset::<SkinnedMeshInverseBindposes>()
            .init_resource::<WorldConfig>().init_resource::<ContentCache>().init_resource::<ItemCatalog>()
            .init_resource::<crate::world::WorldGeometry>().init_resource::<crate::impact::surfaces::SurfaceTable>()
            .init_resource::<crate::player::WorldSessionEpoch>().init_asset::<AudioSource>()
            .init_resource::<Settings>().init_resource::<crate::use_items::HandUse>()
            .init_resource::<crate::vehicle::Occupancy>().init_resource::<crate::recoil::Recoil>()
            .init_resource::<crate::vehicle::turrets::Operator>()
            .init_resource::<ButtonInput<KeyCode>>().init_resource::<ButtonInput<MouseButton>>()
            .add_message::<CoreEvent>().add_message::<crate::use_items::HandEvent>()
            .insert_resource(UiState{screen:Screen::Playing,..default()})
            .insert_resource(GameSnapshot(serde_json::json!({"connected":true,"playerId":1,"player":{"alive":true,"heldItemId":0,"stance":"stand"}})))
            .add_systems(Update, (
                |snapshot: Res<GameSnapshot>, epoch: Res<crate::player::WorldSessionEpoch>,
                 mut operator: ResMut<crate::vehicle::turrets::Operator>| {
                    operator.refresh(&snapshot.0, epoch.0);
                }
            ).in_set(PlayerUpdate))
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
        // The source Fire/VehicleSeatTests contract permits armed passengers.
        // Switching seat must restore the held rig immediately, not wait for exit.
        app.world_mut()
            .resource_mut::<crate::vehicle::Occupancy>()
            .seat
            .as_mut()
            .unwrap()
            .driver = false;
        app.update();
        assert!(app.world().get::<Camera>(camera).unwrap().is_active);
        assert_eq!(
            app.world().get::<RigPlayback>(root).unwrap().clip,
            "Punch_Left"
        );
        assert_eq!(
            *app.world().get::<Visibility>(held).unwrap(),
            Visibility::Inherited
        );
        assert!(app.world().resource::<ViewmodelStatus>().ready);
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
        // Two physical rifles sharing an asset still need separate equip and
        // attachment lifecycles; the first rig must not survive the replacement.
        let item = serde_json::from_value(
            serde_json::json!({"id":4,"name":"Rifle","type":"gun","asset":"eaglefire","gun":{}}),
        )
        .unwrap();
        app.world_mut()
            .resource_mut::<ItemCatalog>()
            .items
            .insert(4, item);
        app.world_mut().resource_mut::<GameSnapshot>().0["player"]["heldItemId"] =
            serde_json::json!(4);
        app.world_mut().resource_mut::<GameSnapshot>().0["weapon"] =
            serde_json::json!({"selectionVersion":1});
        app.update();
        app.update();
        let first = app.world().resource::<ViewmodelState>().arms.unwrap();
        app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["selectionVersion"] =
            serde_json::json!(2);
        app.update();
        app.update();
        assert_ne!(app.world().resource::<ViewmodelState>().arms, Some(first));
        assert!(app.world().get_entity(first).is_err());
        assert_eq!(app.world().resource::<ViewmodelState>().held, Some(4));
        app.world_mut()
            .resource_mut::<crate::vehicle::Occupancy>()
            .seat = Some(crate::vehicle::OccupiedSeat {
            vehicle,
            net_id: 7,
            seat: 1,
            body_pose: Transform::IDENTITY,
            eye_pose: Transform::IDENTITY,
            velocity: Vec3::ZERO,
            driver: false,
            aircraft: false,
        });
        app.world_mut().resource_mut::<GameSnapshot>().0["player"]["stance"] =
            serde_json::json!("sitting");
        app.world_mut()
            .resource_mut::<ViewmodelState>()
            .equip_elapsed = 100.;
        app.world_mut()
            .resource_mut::<ButtonInput<MouseButton>>()
            .press(MouseButton::Right);
        app.update();
        assert!(
            app.world().resource::<ViewmodelStatus>().aiming,
            "armed passenger can aim the held gun"
        );
        let rifle_root = app.world().resource::<ViewmodelState>().arms.unwrap();
        let rifle = app.world().resource::<ViewmodelState>().gun.unwrap();
        let seat = |app: &mut App, key: &str, net_id: u32, driver: u32, gunner: u32, local_seat: u8| {
            let definition = crate::vehicle::data::named(key).unwrap();
            let mut snapshot = app.world_mut().resource_mut::<GameSnapshot>();
            snapshot.0["vehicles"] = serde_json::json!([{
                "netId":net_id,"typeId":definition.type_id,
                "driverPlayerId":driver,"passengers":[gunner]
            }]);
            snapshot.0["vehicleMounts"] = serde_json::json!([{
                "vehicleNetId":net_id,"headOut":false,"mounts":[
                    {"index":0,"yaw":0.,"pitch":0.,"ammo":60,"cooldownSeconds":0.,"crewAlive":true},
                    {"index":1,"yaw":0.,"pitch":0.,"ammo":400,"cooldownSeconds":0.,"crewAlive":true}
                ]
            }]);
            let mut occupancy = app.world_mut().resource_mut::<crate::vehicle::Occupancy>();
            let seat = occupancy.seat.as_mut().unwrap();
            seat.net_id = net_id;
            seat.seat = local_seat;
            seat.driver = local_seat == 0;
        };
        // An accepted empty gunner seat hands both mounts to the driver. The
        // real camera must disappear, and held rifle state must survive.
        seat(&mut app, "tank", 7, 1, 0, 0);
        app.update();
        assert!(app.world().resource::<crate::vehicle::turrets::Operator>().active().is_some());
        assert!(!app.world().get::<Camera>(camera).unwrap().is_active);
        assert!(!app.world().resource::<ViewmodelStatus>().ready);
        assert!(!app.world().resource::<ViewmodelStatus>().aiming);
        assert_eq!(app.world().resource::<ViewmodelStatus>().muzzle_offset, None);
        assert_eq!(app.world().resource::<ViewmodelState>().arms, Some(rifle_root));
        assert_eq!(app.world().resource::<ViewmodelState>().gun, Some(rifle));
        assert_eq!(app.world().resource::<ViewmodelState>().held, Some(4));
        // A different player taking the gunner seat removes the driver's mount
        // immediately and restores source driving arms, not the held gun.
        seat(&mut app, "tank", 7, 1, 2, 0);
        app.update();
        assert!(app.world().resource::<crate::vehicle::turrets::Operator>().active().is_none());
        assert!(app.world().get::<Camera>(camera).unwrap().is_active);
        assert_eq!(app.world().get::<RigPlayback>(rifle_root).unwrap().clip, "Idle_Drive");
        assert_eq!(*app.world().get::<Visibility>(rifle).unwrap(), Visibility::Hidden);
        // Becoming the gunner hides that camera again without replacing the rig.
        seat(&mut app, "tank", 7, 2, 1, 1);
        app.update();
        assert!(!app.world().get::<Camera>(camera).unwrap().is_active);
        assert!(!app.world().resource::<ViewmodelStatus>().ready);
        // An ordinary passenger can use the same rifle immediately: no exit,
        // re-equip command, new rig, stale drive clip or inherited mount state.
        seat(&mut app, "jeep", 8, 2, 1, 1);
        app.update();
        assert!(app.world().resource::<crate::vehicle::turrets::Operator>().active().is_none());
        assert!(app.world().get::<Camera>(camera).unwrap().is_active);
        assert_eq!(app.world().resource::<ViewmodelState>().arms, Some(rifle_root));
        assert_eq!(app.world().resource::<ViewmodelState>().gun, Some(rifle));
        assert_eq!(*app.world().get::<Visibility>(rifle).unwrap(), Visibility::Inherited);
        assert_eq!(app.world().get::<RigPlayback>(rifle_root).unwrap().clip,
            app.world().resource::<ViewmodelState>().hold_clip);
        let status = app.world().resource::<ViewmodelStatus>();
        assert!(status.ready && status.aiming);
        assert_eq!(status.held, 4);
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
