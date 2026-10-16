//! One collision body, driven by the original movement simulation. Its resolved
//! position is sent to the existing server authority after each Rapier step.
use crate::{
    LaunchOptions,
    bridge::CoreBridge,
    settings::{GameAction, Settings},
    ui::{Screen, UiIntent, UiModel, UiState},
    world::{WorldReady, WorldState},
};
use bevy::{
    app::AppExit,
    input::mouse::AccumulatedMouseMotion,
    prelude::*,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow},
};
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};

pub(crate) mod lean;
pub(crate) mod flinch;
mod crafting_eye;
pub(crate) use crafting_eye::{CraftingEye, needs_eye};

#[cfg(test)]
mod network_tests;
#[cfg(test)]
mod seated_tests;
#[cfg(test)]
mod mounted_tests;
#[cfg(test)]
mod menu_tests;
#[cfg(test)]
mod collision_tests;
#[cfg(test)]
mod swoop_tests;
mod respawn;

pub struct PlayerPlugin;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PlayerUpdate;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PlayerCameraUpdate;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PublishPlayer;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct SimulatePlayer;
#[derive(Resource, Default)]
pub struct WorldSessionEpoch(pub u64);
impl Plugin for PlayerPlugin {
    fn build(&self, app: &mut App) {
        app.add_plugins(flinch::FlinchPlugin)
            .init_resource::<Session>()
            .init_resource::<WorldSessionEpoch>()
            .init_resource::<crate::recoil::Recoil>()
            .init_resource::<crate::weapons::Trigger>()
            .init_resource::<crate::use_items::HandUse>()
            .add_message::<crate::use_items::HandEvent>()
            .init_resource::<PlayerInput>()
            .init_resource::<lean::LeanState>()
            .init_resource::<GameSnapshot>()
            .init_resource::<CraftingEye>()
            .add_systems(Startup, spawn_player.after(WorldReady))
            .add_systems(
                Update,
                (
                    initialize_session,
                    ui_intents,
                    crate::world::origin::adopt_authority,
                    read_input,
                    physics_configuration,
                )
                    .chain()
                    .in_set(PlayerUpdate)
                    .after(crate::ui::UiUpdate)
                    .after(WorldReady),
            )
            .add_systems(
                Update,
                (sync_seated_body, camera_follow)
                    .chain()
                    .in_set(PlayerCameraUpdate)
                    .after(crate::vehicle::VehicleUpdate)
                    .after(PlayerUpdate),
            )
            .add_systems(FixedUpdate, lean::step.before(simulate))
            .add_systems(
                Update,
                lean::ease.after(PlayerUpdate).before(PlayerCameraUpdate),
            )
            .add_systems(
                FixedUpdate,
                simulate
                    .in_set(SimulatePlayer)
                    .before(PhysicsSet::SyncBackend),
            )
            .add_systems(
                FixedUpdate,
                publish_body
                    .in_set(PublishPlayer)
                    .after(PhysicsSet::Writeback)
                    .after(crate::vehicle::VehiclePublish),
            );
    }
}

#[derive(Resource, Default)]
pub struct GameSnapshot(pub Value);

#[derive(Resource)]
struct Session {
    initialized: bool,
    failed: bool,
    correction_version: u64,
    respawn_look_version: u64,
    connecting_elapsed: f32,
    was_connected: bool,
    reject_retries: u8,
    heights: StanceHeights,
}

impl Default for Session {
    fn default() -> Self {
        Self {
            initialized: false,
            failed: false,
            correction_version: 0,
            respawn_look_version: 0,
            connecting_elapsed: 0.,
            was_connected: false,
            reject_retries: 0,
            heights: default(),
        }
    }
}

#[derive(Clone, Copy)]
pub(crate) struct StanceHeights {
    stand: f32,
    crouch: f32,
    prone: f32,
}
impl Default for StanceHeights {
    fn default() -> Self {
        Self {
            stand: 2.0,
            crouch: 1.2,
            prone: 0.8,
        }
    }
}
impl StanceHeights {
    pub(crate) fn for_stance(self, stance: &str) -> f32 {
        match stance {
            "crouch" => self.crouch,
            "prone" => self.prone,
            _ => self.stand,
        }
    }
    fn from_reply(value: &Value) -> Self {
        let base = Self::default();
        Self {
            stand: valid_height(&value["stand"]).unwrap_or(base.stand),
            crouch: valid_height(&value["crouch"]).unwrap_or(base.crouch),
            prone: valid_height(&value["prone"]).unwrap_or(base.prone),
        }
    }
}
fn valid_height(value: &Value) -> Option<f32> {
    value
        .as_f64()
        .filter(|v| v.is_finite() && *v >= 0.6 && *v <= 3.0)
        .map(|v| v as f32)
}

#[derive(Resource, Default)]
pub(crate) struct PlayerInput {
    movement: Vec2,
    jump: bool,
    jump_held: bool,
    fire: bool,
    fire_pressed: bool,
    secondary_pressed: bool,
    free_look: bool,
    steady_key: bool,
    applied_sway: Vec2,
    selector: bool,
    reload: bool,
    yaw: f32,
    pitch: f32,
    crouch_toggle: bool,
    prone: bool,
    stance: String,
    held_item_id: u16,
    in_seat: bool,
    vehicle_seat: Option<(u32, u8)>,
    seat_yaw: f32,
    prop_exit: Option<(Vec3, Vec3)>,
}

impl PlayerInput {
    pub(crate) fn moving(&self) -> bool {
        self.movement.length_squared() > 0.
    }
    #[cfg(test)]
    pub(crate) fn with_movement(movement: Vec2) -> Self {
        Self {
            movement,
            ..default()
        }
    }
    #[cfg(test)]
    pub(crate) fn with_view(yaw: f32, pitch: f32) -> Self {
        Self { yaw, pitch, ..default() }
    }
    pub(crate) fn yaw(&self) -> f32 {
        self.yaw
    }
    /// Body/look heading before the separate camera gaze (source LookBasis).
    pub(crate) fn world_yaw(&self) -> f32 {
        self.yaw + if self.in_seat { self.seat_yaw } else { 0. }
    }
    pub(crate) fn pitch(&self) -> f32 {
        self.pitch
    }
}
#[derive(Component)]
pub struct PlayerBody {
    velocity: Vec3,
    grounded: bool,
    height: f32,
    stance: String,
    eye_height: f32,
    locomotion: crate::locomotion::Locomotion,
}
impl PlayerBody {
    #[cfg(test)]
    pub(crate) fn for_motion_test(velocity: Vec3, grounded: bool, stance: &str) -> Self {
        Self {
            velocity,
            grounded,
            stance: stance.into(),
            height: 2.,
            eye_height: 1.75,
            locomotion: default(),
        }
    }
    pub(crate) fn velocity(&self) -> Vec3 {
        self.velocity
    }
    pub(crate) fn stance(&self) -> &str {
        &self.stance
    }
    pub(crate) fn grounded(&self) -> bool {
        self.grounded
    }
}
#[derive(Component)]
pub struct PlayerCamera;

fn capsule(height: f32) -> Collider {
    let radius = 0.35;
    Collider::capsule(Vec3::Y * radius, Vec3::Y * (height - radius), radius)
}

fn player_collision_groups() -> CollisionGroups {
    // PlayerController's own walking mask also supplies HeadroomFor/CapsuleFits:
    // world/props, vehicle model surfaces and remote players. Animals/enemies
    // have separate hit layers, and a stripped driving hull is not a surface.
    CollisionGroups::new(
        Group::GROUP_2,
        Group::GROUP_1 | Group::GROUP_4 | Group::GROUP_15,
    )
}

fn spawn_player(mut commands: Commands, world: Res<WorldState>) {
    commands.spawn((
        Name::new("Local player body"),
        PlayerBody {
            velocity: Vec3::ZERO,
            grounded: false,
            height: 2.0,
            stance: "stand".into(),
            eye_height: 1.75,
            locomotion: default(),
        },
        Transform::from_translation(world.spawn),
        RigidBody::KinematicPositionBased,
        capsule(2.0),
        player_collision_groups(),
        KinematicCharacterController {
            filter_groups: Some(player_collision_groups()),
            offset: CharacterLength::Absolute(0.015),
            autostep: Some(CharacterAutostep {
                max_height: CharacterLength::Absolute(0.5),
                min_width: CharacterLength::Absolute(0.15),
                include_dynamic_bodies: false,
            }),
            snap_to_ground: Some(CharacterLength::Absolute(0.5)),
            max_slope_climb_angle: 55.0_f32.to_radians(),
            min_slope_slide_angle: 55.0_f32.to_radians(),
            ..default()
        },
    ));
    commands.spawn((
        Name::new("Player camera"),
        Camera3d::default(),
        PlayerCamera,
        Projection::Perspective(PerspectiveProjection {
            far: 2500.0,
            ..default()
        }),
        Transform::from_translation(world.focus + Vec3::new(26.0, 17.0, 30.0))
            .looking_at(world.focus, Vec3::Y),
        DistanceFog {
            color: Color::srgb(0.58, 0.72, 0.84),
            falloff: FogFalloff::Linear {
                start: 130.0,
                end: 800.0,
            },
            ..default()
        },
    ));
}

fn initialize_session(
    origin: Res<crate::world::origin::WorldOrigin>,
    bridge: Res<CoreBridge>,
    options: Res<LaunchOptions>,
    profile: Option<Res<crate::profile::LocalProfile>>,
    world: Res<WorldState>,
    mut session: ResMut<Session>,
    mut model: ResMut<UiModel>,
    mut ui: ResMut<UiState>,
    mut snapshot: ResMut<GameSnapshot>,
    mut input: ResMut<PlayerInput>,
    mut epoch: ResMut<WorldSessionEpoch>,
    mut bodies: Query<(
        &mut Transform,
        &mut PlayerBody,
        &mut Collider,
        &mut KinematicCharacterController,
    )>,
) {
    if session.initialized || session.failed || !world.ready || !options.0.play {
        return;
    }
    model.map_name = options.0.map.clone();
    ui.map = options.0.map.clone();
    let mut request = json!({"contentRoot":options.0.content,"name":options.0.player_name,"mapId":options.0.map,"spawn":world.spawn.to_array(),"origin":[origin.offset.x,origin.offset.y],"yaw":world.spawn_yaw,"seed":options.0.seed});
    if let Some(path) = options.0.island.as_ref().or(options.0.new_island.as_ref()) {
        request["finiteDirectory"] = json!(path);
    }
    request["useLocalProfile"] = json!(profile.is_some_and(|profile| profile.ready));
    // MainMenuPlay Zombies row -> WorldBuilder.ZombiesOverride, decided by the host.
    request["zombies"] = json!(options.0.zombies);
    options.0.network.configure(&mut request);
    match bridge.request("init", request) {
        Ok(response) if response["result"]["protocolVersion"] == 1 => {
            model.reset_session();
            model.adopt_snapshot(&response["state"]);
            snapshot.0 = response["state"].clone();
            model.status = format!("{} · {} objects", world.description, world.object_count);
            session.initialized = true;
            epoch.0 = epoch.0.wrapping_add(1);
            session.correction_version = response["state"]["player"]["correctionVersion"]
                .as_u64()
                .unwrap_or(0);
            session.respawn_look_version = response["state"]["player"]["respawnLook"]["version"].as_u64().unwrap_or(0);
            session.heights = StanceHeights::from_reply(&response["result"]["movementHeights"]);
            *input = PlayerInput {
                yaw: world.spawn_yaw,
                stance: "stand".into(),
                ..default()
            };
            for (mut transform, mut body, mut collider, mut controller) in &mut bodies {
                transform.translation = world.spawn;
                body.velocity = Vec3::ZERO;
                body.grounded = false;
                body.height = session.heights.stand;
                body.stance = "stand".into();
                body.eye_height = 1.75;
                body.locomotion = default();
                *collider = capsule(body.height);
                controller.translation = None;
            }
            ui.networked = options.0.network.networked();
            session.was_connected = model.connected;
            session.connecting_elapsed = 0.;
            session.reject_retries = 0;
            ui.screen = if model.connected {
                Screen::Playing
            } else {
                Screen::Connecting
            };
            if !model.connected {
                model.status = format!("Connecting - {}", model.network.state);
            }
        }
        Ok(_) => fail(
            &mut session,
            &mut model,
            "Unsupported core bridge protocol".into(),
        ),
        Err(error) => fail(&mut session, &mut model, error.to_string()),
    }
}

fn fail(session: &mut Session, model: &mut UiModel, error: String) {
    session.failed = true;
    model.connected = false;
    model.status = error.clone();
    model.notice(error.clone());
    error!("{error}");
}

fn ui_intents(
    mut intents: MessageReader<UiIntent>,
    bridge: Res<CoreBridge>,
    mut options: ResMut<LaunchOptions>,
    mut ui: ResMut<UiState>,
    mut model: ResMut<UiModel>,
    mut snapshot: ResMut<GameSnapshot>,
    mut exit: MessageWriter<AppExit>,
    mut player: Query<(Entity, &mut Transform, &mut PlayerBody)>,
    mut config: ResMut<crate::world::WorldConfig>,
    mut world: ResMut<WorldState>,
    mut session: ResMut<Session>,
    mut input: ResMut<PlayerInput>,
    mut hand_use: (
        ResMut<crate::use_items::HandUse>,
        ResMut<WorldSessionEpoch>,
        Res<crate::entities::ItemCatalog>,
        ResMut<crate::world::origin::WorldOrigin>,
        Option<Res<CraftingEye>>,
    ),
    mut trigger: ResMut<crate::weapons::Trigger>,
    mut hand_events: MessageWriter<crate::use_items::HandEvent>,
    mut commands: Commands,
) {
    for intent in intents.read() {
        let result = match intent {
            UiIntent::Play { map } | UiIntent::StartSession { map, .. } => {
                let target = match intent {
                    UiIntent::StartSession { target, .. } => target.clone(),
                    _ => crate::network::Target::Local,
                };
                if map != &options.0.map || !model.connected || target != options.0.network {
                    if session.initialized {
                        let _ = bridge.request("disconnect", json!({}));
                    }
                    model.reset_session();
                    model.connected = false;
                    snapshot.0 = Value::Null;
                    if target.networked() {
                        hand_use.3.reset_for_network();
                        world.ready = false;
                    }
                    options.0.network_ready = true;
                    options.0.network = target;
                    ui.networked = options.0.network.networked();
                    hand_use.1.0 = hand_use.1.0.wrapping_add(1);
                    *hand_use.0 = default();
                    *trigger = default();
                    *input = default();
                    for (entity, mut pose, mut body) in &mut player {
                        commands.entity(entity).remove::<ColliderDisabled>();
                        pose.rotation = Quat::IDENTITY;
                        body.locomotion = default();
                    }
                    hand_events.write(crate::use_items::HandEvent::Cancel);
                    if map.eq_ignore_ascii_case("procedural") {
                        match bridge.request(
                            "terrain",
                            json!({"seed":options.0.seed,"x":0,"z":0,"lod":0}),
                        ) {
                            Ok(response) => {
                                let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                                    .join(".local/terrain-selected.json");
                                let saved = std::fs::create_dir_all(path.parent().unwrap())
                                    .and_then(|_| {
                                        std::fs::write(&path, response["result"].to_string())
                                    });
                                if let Err(error) = saved {
                                    model.notice(error.to_string());
                                    continue;
                                }
                                config.procedural_path = Some(path);
                            }
                            Err(error) => {
                                model.notice(error.to_string());
                                continue;
                            }
                        }
                    } else {
                        config.procedural_path = None;
                    }
                    let map_changed = options.0.map != *map;
                    let finite = config.finite_path.is_some() && config.map_name == *map;
                    if !finite {
                        config.finite_path = None;
                        config.finite_spawn = None;
                        options.0.island = None;
                        options.0.new_island = None;
                    }
                    config.map_name = map.clone();
                    config.map_root = if finite { None } else { options
                        .0
                        .unturned
                        .as_ref()
                        .map(|p| p.join("Maps").join(map)) };
                    options.0.map = map.clone();
                    options.0.play = true;
                    if map_changed {
                        world.ready = false;
                    }
                    session.initialized = false;
                    session.failed = false;
                    model.status = format!("Loading {map}");
                    ui.screen = Screen::Connecting;
                } else if model.connected {
                    ui.screen = Screen::Playing;
                }
                continue;
            }
            UiIntent::Resume => {
                continue;
            }
            UiIntent::Quit => {
                exit.write(AppExit::Success);
                continue;
            }
            UiIntent::Disconnect => {
                if session.initialized {
                    let _ = bridge.request("disconnect", json!({}));
                }
                session.initialized = false;
                session.failed = false;
                session.was_connected = false;
                options.0.play = false;
                options.0.network_ready = false;
                options.0.network = crate::network::Target::Local;
                *model = UiModel::default();
                model.status = "Disconnected".into();
                snapshot.0 = Value::Null;
                *input = default();
                *hand_use.0 = default();
                *trigger = default();
                hand_use.1.0 = hand_use.1.0.wrapping_add(1);
                hand_events.write(crate::use_items::HandEvent::Cancel);
                ui.networked = false;
                ui.screen = Screen::MainMenu;
                continue;
            }
            UiIntent::Save => bridge.request("save", json!({"path":save_path(&options.0.map)})),
            UiIntent::Load => bridge.request("load", json!({"path":save_path(&options.0.map)})),
            UiIntent::Action { name, args } => {
                if name == "console"
                    && (crate::entities::npcs::is_console_command(args["text"].as_str().unwrap_or(""))
                        || crate::ui::npc::is_console_command(args["text"].as_str().unwrap_or(""))
                        || crate::bugreport::is_console_command(args["text"].as_str().unwrap_or("")))
                {
                    continue;
                }
                let gesture = if name == "console" {
                    crate::avatar::gestures::console(
                        args["text"].as_str().unwrap_or(""),
                        snapshot.0["player"]["gesture"].as_u64().unwrap_or(0) as u8,
                        &hand_use.2.gestures,
                        &mut model,
                    )
                } else {
                    None
                };
                match gesture {
                    Some(Some(gesture)) => bridge.request(
                        "action",
                        json!({"name":"requestGesture","args":{"gesture":gesture}}),
                    ),
                    Some(None) => continue,
                    None => {
                        let mut args = args.clone();
                        if needs_eye(name) {
                            if let Some(eye) = hand_use.4.as_ref()
                                .and_then(|eye| eye.request(&hand_use.3, &hand_use.1))
                            {
                                args["eye"] = json!(eye);
                            }
                        }
                        bridge.request("action", json!({"name":name,"args":args}))
                    },
                }
            }
        };
        match result {
            Ok(response) => {
                if matches!(intent, UiIntent::Action { name, .. } if matches!(name.as_str(),"holdItem"|"equipAndHold"))
                {
                    if let Some(held) = response["result"]["heldItemId"].as_u64() {
                        input.held_item_id = held as u16;
                    }
                }
                if matches!(intent, UiIntent::Load) {
                    session.respawn_look_version = response["state"]["player"]["respawnLook"]["version"].as_u64().unwrap_or(0);
                    *hand_use.0 = default();
                    *trigger = default();
                    hand_use.1.0 = hand_use.1.0.wrapping_add(1);
                    input.in_seat = false;
                    input.prop_exit = None;
                    input.seat_yaw = 0.;
                    hand_events.write(crate::use_items::HandEvent::Cancel);
                    if let Some(position) = vector(&response["state"]["player"]["position"]) {
                        for (entity, mut transform, mut body) in &mut player {
                            commands.entity(entity).remove::<ColliderDisabled>();
                            transform.rotation = Quat::IDENTITY;
                            transform.translation = position;
                            body.velocity = Vec3::ZERO;
                            body.grounded = false;
                            body.locomotion = default();
                        }
                    }
                    model.notice("World loaded");
                    input.yaw = response["state"]["player"]["yaw"].as_f64().unwrap_or(0.) as f32;
                    input.pitch =
                        response["state"]["player"]["pitch"].as_f64().unwrap_or(0.) as f32;
                    input.held_item_id = response["state"]["player"]["heldItemId"]
                        .as_u64()
                        .unwrap_or(0) as u16;
                } else if matches!(intent, UiIntent::Save) {
                    model.notice("World saved");
                }
                model.adopt_snapshot(&response["state"]);
                snapshot.0 = response["state"].clone();
            }
            Err(error) => model.notice(error.to_string()),
        }
    }
}

pub(crate) fn save_path(map: &str) -> std::path::PathBuf {
    // Tests never touch a real save, even with UG_SAVE_DIR exported.
    let overridden = if cfg!(test) {
        None
    } else {
        std::env::var_os("UG_BEVY_SAVES").or_else(|| std::env::var_os("UG_SAVE_DIR"))
    };
    let base = overridden
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| {
            Settings::path()
                .parent()
                .unwrap_or(std::path::Path::new("."))
                .join("saves")
        });
    base.join(format!(
        "{}.json",
        map.chars()
            .map(|c| if c.is_ascii_alphanumeric() { c } else { '_' })
            .collect::<String>()
    ))
}

fn read_input(
    bridge: Res<CoreBridge>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    motion: Res<AccumulatedMouseMotion>,
    settings: Res<Settings>,
    perspective: Option<Res<crate::avatar::Perspective>>,
    mut viewmodel: ResMut<crate::viewmodel::ViewmodelStatus>,
    ui: Res<UiState>,
    mut model: ResMut<UiModel>,
    mut input: ResMut<PlayerInput>,
    mut windows: Query<(&mut CursorOptions, &mut Window), With<PrimaryWindow>>,
    mut recoil: (
        ResMut<crate::recoil::Recoil>,
        Res<GameSnapshot>,
        Res<crate::entities::ItemCatalog>,
        Res<WorldSessionEpoch>,
        Res<Time>,
    ),
    mut mounted: Option<ResMut<crate::vehicle::turrets::Operator>>,
    optics: Option<Res<crate::ui::tank_optics::TankOptics>>,
) {
    if let Some(mounts) = &mut mounted { mounts.refresh(&recoil.1.0, recoil.3.0); }
    // VehicleUpdate adopts physical seat poses later this frame. Use the already
    // accepted seat for input gates now, including an explicit accepted exit.
    let ride = if let Some(mounts) = &mounted {
        mounts.seat.map(|seat| {
            let aircraft = crate::vehicle::data::definition(seat.type_id)
                .is_some_and(|d| d.spec.heli || d.spec.plane);
            (seat.seat == 0, aircraft)
        })
    } else {
        occupancy.as_ref().and_then(|o| o.seat).map(|seat| (seat.driver, seat.aircraft))
    };
    let playing = !ui.captures_input() && model.connected;
    viewmodel.look_delta = Vec2::ZERO;
    let sway_delta = viewmodel.scope_sway - input.applied_sway;
    input.pitch = (input.pitch + sway_delta.x).clamp(-89., 89.);
    input.yaw = (input.yaw + sway_delta.y).rem_euclid(360.);
    input.applied_sway = viewmodel.scope_sway;
    let kick = recoil.0.update(
        &recoil.1.0,
        &recoil.2,
        recoil.3.0,
        recoil.4.delta_secs(),
        simulation_paused(&ui),
    );
    input.yaw = (input.yaw + kick.x).rem_euclid(360.);
    input.pitch = (input.pitch + kick.y).clamp(-89., 89.);
    for (mut cursor, mut window) in &mut windows {
        cursor.visible = !playing;
        cursor.grab_mode = if playing {
            CursorGrabMode::Locked
        } else {
            CursorGrabMode::None
        };
        window.present_mode = if settings.vsync {
            bevy::window::PresentMode::AutoVsync
        } else {
            bevy::window::PresentMode::AutoNoVsync
        };
    }
    if !playing {
        input.movement = Vec2::ZERO;
        input.fire = false;
        input.fire_pressed = false;
        input.secondary_pressed = false;
        input.selector = false;
        input.jump = false;
        input.jump_held = false;
        input.reload = false;
        input.steady_key = false;
        return;
    }
    let held = |action| settings.pressed(action, &keys, &mouse);
    let pressed = |action| settings.just_pressed(action, &keys, &mouse);
    input.steady_key =
        held(GameAction::Sprint) || std::env::var("UG_STEADY").is_ok_and(|value| value == "1");
    input.movement = Vec2::new(
        (held(GameAction::MoveRight) as i32 - held(GameAction::MoveLeft) as i32) as f32,
        (held(GameAction::MoveForward) as i32 - held(GameAction::MoveBack) as i32) as f32,
    );
    input.jump |= pressed(GameAction::Jump);
    input.jump_held = held(GameAction::Jump);
    input.free_look =
        held(GameAction::FreeLook) && ride.is_none();
    input.fire = held(GameAction::Fire) && !input.free_look;
    input.fire_pressed = !input.free_look && (input.fire_pressed || pressed(GameAction::Fire));
    if mounted.as_ref().is_none_or(|m| m.active().is_none())
        && !input.free_look && pressed(GameAction::Fire) && viewmodel.cancel_inspect_from_fire() {
        // Keep the cancelling click out of every fixed tick before the next
        // input frame. Auto fire may resume on the following held-input frame.
        input.fire = false;
        input.fire_pressed = false;
    }
    input.secondary_pressed =
        !input.free_look && (input.secondary_pressed || pressed(GameAction::Aim));
    input.selector |= pressed(GameAction::Firemode);
    input.reload |= pressed(GameAction::Reload);
    if pressed(GameAction::VehicleDoor) {
        if let Some(mounts) = &mut mounted { mounts.toggle_head_out(); }
    }
    if pressed(GameAction::CrouchToggle) {
        input.crouch_toggle = !input.crouch_toggle;
        input.prone = false;
    }
    if pressed(GameAction::Prone) {
        input.prone = !input.prone;
        input.crouch_toggle = false;
    }
    input.stance = if input.prone {
        "prone"
    } else if held(GameAction::Crouch) || input.crouch_toggle {
        "crouch"
    } else if held(GameAction::Sprint) && model.stamina > 0.0 {
        "sprint"
    } else {
        "stand"
    }
    .into();
    let aircraft_controls = ride.is_some_and(|(driver, aircraft)| driver && aircraft)
        && !held(GameAction::FreeLook);
    if !aircraft_controls
        && !input.free_look
        && !(ride.is_some()
            && perspective.as_ref().is_some_and(|p| !p.first_person))
    {
        let sensitivity = settings.mouse_sensitivity * if ride.is_some() {
            optics.as_ref().map_or(1., |o| o.look_sensitivity())
        } else {
            viewmodel.look_sensitivity()
        };
        if !input.in_seat {
            // Original AddLookDelta receives mouse deltas after sensitivity,
            // not changed camera angles (which also contain recoil and sway).
            viewmodel.look_delta = Vec2::new(motion.delta.y, motion.delta.x) * sensitivity;
        }
        input.yaw = (input.yaw - motion.delta.x * sensitivity).rem_euclid(360.0);
        let sign = if settings.invert_look_y { 1.0 } else { -1.0 };
        input.pitch = (input.pitch + motion.delta.y * sensitivity * sign).clamp(-89.0, 89.0);
        if let Some((driver, _)) = ride {
            // Source ride look wraps before the mount's traverse limit. Ordinary
            // passengers and a crosshair-mount driver retain seated head limits.
            input.yaw = (input.yaw + 180.).rem_euclid(360.) - 180.;
            if mounted.as_ref().is_none_or(|m| !m.look_unlimited()) {
                let limit = if driver { 160. } else { 90. };
                input.yaw = input.yaw.clamp(-limit, limit);
                input.pitch = input.pitch.clamp(-30., 30.);
            }
        }
    }
    for (action, slot) in [(GameAction::Hotbar1, 0), (GameAction::Hotbar2, 1)] {
        if pressed(action) {
            if let Some(mounts) = &mut mounted {
                if mounts.consumes_slot(slot as usize) {
                    mounts.select(slot as usize);
                    continue;
                }
            }
            let held = model
                .pages
                .iter()
                .find(|page| page.index == slot)
                .and_then(|page| page.items.first())
                .map_or(0, |item| item.id);
            if input.held_item_id == held {
                input.held_item_id = 0;
            } else if held != 0 {
                match bridge.request(
                    "action",
                    json!({"name":"holdItem","args":{"page":slot,"x":0,"y":0}}),
                ) {
                    Ok(reply) => {
                        if let Some(id) = reply["result"]["heldItemId"].as_u64() {
                            input.held_item_id = id as u16;
                        }
                    }
                    Err(error) => model.notice(error.to_string()),
                }
            } else {
                input.held_item_id = 0;
            }
        }
    }
}

fn simulate(
    bridge: Res<CoreBridge>,
    ui: Res<UiState>,
    mut session: ResMut<Session>,
    mut input: ResMut<PlayerInput>,
    mut model: ResMut<UiModel>,
    context: ReadRapierContext,
    world: Res<WorldState>,
    occupancy: (
        Option<Res<crate::vehicle::Occupancy>>,
        Option<Res<crate::world::interactables::PropOccupancy>>,
    ),
    catalog: Res<crate::entities::ItemCatalog>,
    snapshot: Res<GameSnapshot>,
    viewmodel: Res<crate::viewmodel::ViewmodelStatus>,
    mut trigger: ResMut<crate::weapons::Trigger>,
    mut hands: (
        ResMut<crate::use_items::HandUse>,
        MessageWriter<crate::use_items::HandEvent>,
        Res<crate::avatar::Perspective>,
        Query<&Transform, (With<PlayerCamera>, Without<PlayerBody>)>,
        Option<Res<lean::LeanState>>,
    ),
    ladders: Query<(&crate::world::WorldObject, &GlobalTransform, &Collider), Without<PlayerBody>>,
    mut bodies: Query<(
        Entity,
        &mut Transform,
        &mut PlayerBody,
        &mut Collider,
        &mut KinematicCharacterController,
    )>,
    mounted: Option<Res<crate::vehicle::turrets::Operator>>,
) {
    if !session.initialized || session.failed || !model.connected || simulation_paused(&ui) {
        return;
    }
    let Ok((entity, mut transform, mut body, mut collider, mut controller)) = bodies.single_mut()
    else {
        return;
    };
    let vehicle_seat = occupancy.0.as_ref().and_then(|o| o.seat);
    let seated = vehicle_seat.is_some() || occupancy.1.is_some_and(|o| o.seat.is_some());
    if !model.alive {
        trigger.cancel();
        let mut events = Vec::new();
        hands.0.cancel(&mut events);
        hands.1.write_batch(events);
        controller.translation = None;
        return;
    }
    // One host body: seat adoption owns its transform. Hand actions and their
    // accepted timers continue independently of the on-foot movement solve.
    if seated {
        controller.translation = None;
        input.jump = false;
    } else {
        let special = context.single().ok().and_then(|physics| {
            body.locomotion.step(
                &physics,
                entity,
                |id| {
                    let (object, transform, collider) = ladders.get(id).ok()?;
                    if !object.asset_name.starts_with("Ladder_") {
                        return None;
                    }
                    let transform = transform.compute_transform();
                    Some(crate::locomotion::LadderGeometry {
                        origin: transform.translation,
                        face: transform.rotation * Vec3::Y,
                        top: collider
                            .aabb(transform.translation, transform.rotation)
                            .max
                            .y,
                    })
                },
                &mut transform.translation,
                input.yaw.to_radians(),
                input.pitch.to_radians(),
                input.movement,
                input.jump_held,
                world.sea_level,
                0.02,
            )
        });
        let wading = world
            .sea_level
            .is_some_and(|sea| transform.translation.y < sea);
        let wanted_stance = if special.is_some()
            || (wading && matches!(input.stance.as_str(), "crouch" | "prone"))
        {
            "stand"
        } else {
            &input.stance
        };
        let desired_height = session.heights.for_stance(wanted_stance);
        let headroom = desired_height <= body.height
            || context.single().is_ok_and(|context| {
                has_headroom(
                    &context,
                    entity,
                    transform.translation,
                    body.height,
                    desired_height,
                )
            });
        let response = bridge.request("simulate",json!({"input":{
        "moveX":input.movement.x,"moveY":input.movement.y,"jump":input.jump,"grounded":body.grounded,
        "stance":wanted_stance,"yaw":input.yaw,"pitch":input.pitch,"velocity":body.velocity.to_array(),"heldItemId":input.held_item_id,
        "headroom":headroom,"capsuleHeight":body.height,"equipmentAllowsSprint":!viewmodel.aiming,
        "steadyKey":input.steady_key,"aiming":viewmodel.aiming,"scopeZoom":viewmodel.scope_zoom
    }}));
        input.jump = false;
        match response {
            Ok(response) => {
                let Some(mut velocity) = vector(&response["result"]["velocity"]) else {
                    fail(
                        &mut session,
                        &mut model,
                        "Core returned invalid movement velocity".into(),
                    );
                    return;
                };
                let Some(height) = valid_height(&response["result"]["height"]) else {
                    fail(
                        &mut session,
                        &mut model,
                        "Core returned invalid capsule height".into(),
                    );
                    return;
                };
                body.stance = response["result"]["stance"]
                    .as_str()
                    .unwrap_or(&input.stance)
                    .to_owned();
                body.eye_height = response["result"]["eyeHeight"]
                    .as_f64()
                    .map(|v| v as f32)
                    .filter(|v| v.is_finite() && *v > 0.0 && *v <= height)
                    .unwrap_or_else(|| eye_height(&body.stance));
                if let Some(special) = &special {
                    velocity = special.velocity;
                    body.stance = special.stance.into();
                    body.eye_height = 1.75;
                    body.grounded = special.stance == "climb";
                    if special.teleported {
                        controller.translation = None;
                    }
                }
                if (height - body.height).abs() > 0.001 {
                    *collider = capsule(height);
                    body.height = height;
                }
                body.velocity = velocity;
                let mut translation = velocity * 0.02;
                if body.grounded && velocity.y <= 0.0 && special.is_none() {
                    translation.y = -0.025;
                }
                controller.translation = Some(translation);
            }
            Err(error) => {
                fail(&mut session, &mut model, error.to_string());
                return;
            }
        }
    }
    trigger.acknowledge(&snapshot.0);
    let item = catalog.items.get(&input.held_item_id);
    let mut buttons = crate::weapons::Buttons {
        fire: input.fire,
        press: std::mem::take(&mut input.fire_pressed),
        strong: std::mem::take(&mut input.secondary_pressed),
        reload: std::mem::take(&mut input.reload),
        selector: std::mem::take(&mut input.selector),
    };
    if viewmodel.inspecting && item.is_some_and(|item| !item.gun.is_null()) {
        buttons.fire = false;
        buttons.press = false;
        trigger.cancel();
    }
    let allowed = !ui.captures_input()
        && !input.free_look
        && !matches!(body.stance.as_str(), "swim" | "climb");
    let (uses, events) = hands.0.step(
        0.02,
        &snapshot.0,
        item,
        &catalog.throw_motion,
        &buttons,
        viewmodel.ready && viewmodel.held == input.held_item_id,
        allowed && vehicle_seat.is_none(),
    );
    hands.1.write_batch(events);
    for use_ in uses {
        use crate::use_items::UseIntent;
        let request = match use_ {
            UseIntent::Consume(cell) => {
                json!({"name":"consume","args":{"page":cell.page,"x":cell.x,"y":cell.y}})
            }
            UseIntent::Hold(cell) => {
                json!({"name":"holdItem","args":{"page":cell.page,"x":cell.x,"y":cell.y}})
            }
            UseIntent::Tool(active) => json!({"name":"toolUse","args":{"active":active}}),
            UseIntent::Frozen => {
                model.notice("This food is frozen solid");
                continue;
            }
            UseIntent::Throw { item, strong } => {
                let (mut eye, mut direction) =
                    aim_eye(transform.translation, &body, &input, hands.4.as_deref());
                if seated {
                    let Ok(camera) = hands.3.single() else {
                        continue;
                    };
                    eye = camera.translation;
                    direction = *camera.forward();
                }
                let probe = catalog.throw_motion["clearProbe"].as_f64().unwrap_or(1.5) as f32;
                let gap = catalog.throw_motion["clearGap"].as_f64().unwrap_or(0.5) as f32;
                let hit = context
                    .single()
                    .ok()
                    .and_then(|p| throw_clearance(&p, entity, eye, direction, probe));
                let origin = crate::use_items::throw_origin(eye, direction, hit, gap);
                let velocity = crate::use_items::throw_velocity(
                    direction,
                    body.velocity,
                    strong,
                    &catalog.throw_motion,
                );
                json!({"name":"grenade","args":{"origin":origin.to_array(),"velocity":velocity.to_array(),"itemId":item}})
            }
        };
        match bridge.request("action", request) {
            Ok(response) => {
                if let Some(id) = response["result"]["heldItemId"].as_u64() {
                    input.held_item_id = id as u16;
                }
            }
            Err(error) => model.notice(error.to_string()),
        }
    }
    let actions = trigger.step(
        0.02,
        input.held_item_id,
        item.map_or(&Value::Null, |i| &i.gun),
        item.map_or(&Value::Null, |i| &i.melee),
        &snapshot.0["weapon"],
        buttons,
        allowed
            && mounted.as_ref().is_none_or(|m| m.active().is_none())
            && seated_weapon_allowed(
                vehicle_seat,
                hands.2.first_person,
                item.is_some_and(|i| !i.gun.is_null()),
            )
            && viewmodel.ready
            && viewmodel.held == input.held_item_id,
    );
    for action in actions {
        let (eyes, mut direction) =
            aim_eye(transform.translation, &body, &input, hands.4.as_deref());
        let world_yaw = if seated {
            let Ok(camera) = hands.3.single() else {
                continue;
            };
            camera.rotation.to_euler(EulerRot::YXZ).0.to_degrees()
        } else {
            input.yaw
        };
        let request = match action {
            crate::weapons::Intent::Fire => {
                let mut origin = eyes;
                if seated {
                    // Seated look is relative to the moving seat, not the
                    // owner's walking yaw. Fire from the actual seated eye.
                    let Ok(camera) = hands.3.single() else {
                        continue;
                    };
                    origin = camera.translation;
                    direction = *camera.forward();
                } else if !hands.2.first_person {
                    if let (Ok(physics), Ok(camera)) = (context.single(), hands.3.single()) {
                        direction =
                            crate::avatar::camera::aim(&physics, entity, origin, camera, direction);
                    }
                }
                json!({"name":"fire","args":{"origin":origin.to_array(),"dir":direction.to_array(),
                    "visual":crate::bullets::fire_context(viewmodel.aim_alpha,viewmodel.muzzle_offset,origin)}})
            }
            crate::weapons::Intent::Melee(strong) => {
                json!({"name":"melee","args":{"strong":strong,"yawDegrees":world_yaw}})
            }
            crate::weapons::Intent::Reload => json!({"name":"reload","args":{}}),
            crate::weapons::Intent::Selector(mode) => {
                json!({"name":"setFiremode","args":{"mode":mode}})
            }
        };
        if let Err(error) = bridge.request("action", request) {
            model.notice(error.to_string());
        }
    }
}

/// Same foot pivot and roll-before-pitch chain as the physical eyes. Reading
/// camera Euler angles here would include Alt gaze and lose lean/pitch coupling.
fn aim_eye(
    feet: Vec3,
    body: &PlayerBody,
    input: &PlayerInput,
    lean: Option<&lean::LeanState>,
) -> (Vec3, Vec3) {
    lean.map_or_else(
        || {
            (
                feet + Vec3::Y * body.eye_height,
                Quat::from_euler(
                    EulerRot::YXZ,
                    input.yaw.to_radians(),
                    input.pitch.to_radians(),
                    0.,
                ) * Vec3::NEG_Z,
            )
        },
        |lean| {
            (
                lean.eyes(feet, input.yaw),
                lean.aim(input.yaw, input.pitch) * Vec3::NEG_Z,
            )
        },
    )
}

fn seated_weapon_allowed(
    seat: Option<crate::vehicle::OccupiedSeat>,
    first_person: bool,
    gun: bool,
) -> bool {
    // PlayerController.Fire + VehicleSeatTests: the driver's hands are busy;
    // passenger gunfire is allowed only from the first-person seated gaze.
    // MeleeAttack explicitly rejects every vehicle seat. Furniture has no such gate.
    seat.is_none_or(|s| !s.driver && first_person && gun)
}

fn publish_body(
    bridge: Res<CoreBridge>,
    mut ui: ResMut<UiState>,
    time: Res<Time>,
    mut input: ResMut<PlayerInput>,
    mut session: ResMut<Session>,
    mut model: ResMut<UiModel>,
    mut snapshot: ResMut<GameSnapshot>,
    mut bodies: Query<(
        &mut Transform,
        &mut PlayerBody,
        Option<&KinematicCharacterControllerOutput>,
    )>,
) {
    if !session.initialized || session.failed || simulation_paused(&ui) {
        return;
    }
    let Ok((mut transform, mut body, output)) = bodies.single_mut() else {
        return;
    };
    // A disabled seated capsule retains its last on-foot controller output.
    // Reading it would replace the vehicle velocity with an old walking step.
    if let Some(output) = output.filter(|_| !input.in_seat && model.alive && model.connected) {
        body.grounded = output.grounded;
        body.velocity = output.effective_translation / 0.02;
        if body.grounded && body.velocity.y < 0.0 {
            body.velocity.y = 0.0;
        }
    }
    let request = if model.connected {
        json!({"input":{"position":transform.translation.to_array(),"velocity":body.velocity.to_array(),"grounded":body.grounded,"yaw":input.yaw,"pitch":input.pitch,"stance":body.stance,"heldItemId":input.held_item_id}})
    } else {
        json!({})
    };
    match bridge.request("tick", request) {
        Ok(response) => {
            let state = &response["state"];
            respawn::adopt(&mut input, &mut session, state);
            if let Some(held) = state["player"]["heldItemId"].as_u64() {
                input.held_item_id = held as u16;
            }
            let version = state["player"]["correctionVersion"].as_u64().unwrap_or(0);
            if version != session.correction_version {
                if let Some(position) = vector(&state["player"]["position"]) {
                    transform.translation = position;
                    body.velocity = Vec3::ZERO;
                    body.grounded = false;
                    body.locomotion = default();
                }
                session.correction_version = version;
            }
            let was_connected = model.connected;
            model.adopt_snapshot(state);
            snapshot.0 = state.clone();
            if model.connected {
                session.connecting_elapsed = 0.;
                session.was_connected = true;
                if !was_connected {
                    input.yaw = state["player"]["yaw"].as_f64().unwrap_or(0.) as f32;
                    input.pitch = state["player"]["pitch"].as_f64().unwrap_or(0.) as f32;
                    ui.screen = Screen::Playing;
                }
            } else if ui.networked {
                session.connecting_elapsed += time.delta_secs();
                model.status = format!(
                    "Connecting - {}{}",
                    model.network.state,
                    if model.network.awaiting_spawn {
                        "; waiting for player"
                    } else {
                        ""
                    }
                );
                ui.screen = Screen::Connecting;
                if !model.network.reject_text.is_empty()
                    && model.network.retryable
                    && !session.was_connected
                    && session.reject_retries < 4
                {
                    match bridge.request("reconnect", json!({})) {
                        Ok(_) => {
                            session.reject_retries += 1;
                            session.connecting_elapsed = 0.;
                            model.status = format!(
                                "{} - retry {} of 4",
                                model.network.reject_text, session.reject_retries
                            );
                        }
                        Err(error) => {
                            fail(&mut session, &mut model, error.to_string());
                            ui.networked = false;
                            ui.screen = Screen::MainMenu;
                        }
                    }
                } else if !model.network.reject_text.is_empty() || session.connecting_elapsed >= 30.
                {
                    let error = if !model.network.reject_text.is_empty() {
                        model.network.reject_text.clone()
                    } else if session.was_connected {
                        "Lost connection: no response for 30 seconds.".into()
                    } else {
                        "No response from server.".into()
                    };
                    let _ = bridge.request("disconnect", json!({}));
                    fail(&mut session, &mut model, error);
                    ui.networked = false;
                    ui.screen = Screen::MainMenu;
                }
            }
        }
        Err(error) => fail(&mut session, &mut model, error.to_string()),
    }
}

#[cfg(test)]
pub(crate) fn test_ready_publisher(app: &mut App) {
    app.insert_resource(Session { initialized: true, ..default() })
        .init_resource::<PlayerInput>()
        .add_systems(FixedUpdate, publish_body.after(PhysicsSet::Writeback)
            .after(crate::vehicle::VehiclePublish));
}

pub(crate) fn sync_seated_body(
    mut commands: Commands,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    prop_occupancy: Option<Res<crate::world::interactables::PropOccupancy>>,
    context: ReadRapierContext,
    snapshot: Res<GameSnapshot>,
    mut input: ResMut<PlayerInput>,
    vehicles: Query<&crate::vehicle::VehicleBody>,
    mut bodies: Query<(
        Entity,
        &mut Transform,
        &mut PlayerBody,
        &mut KinematicCharacterController,
        Option<&ColliderDisabled>,
    )>,
) {
    let vehicle_seat = occupancy.as_ref().and_then(|o| o.seat);
    let vehicle_key = vehicle_seat.map(|s| (s.net_id, s.seat));
    let seat = occupancy
        .as_ref()
        .and_then(|o| o.seat)
        .map(|s| (s.body_pose, s.eye_pose, s.velocity, s.driver, None))
        .or_else(|| {
            prop_occupancy.as_ref().and_then(|o| o.seat).map(|s| {
                (
                    s.body_pose,
                    s.eye_pose,
                    Vec3::ZERO,
                    false,
                    Some((s.anchor, s.exit_position)),
                )
            })
        });
    for (entity, mut transform, mut body, mut controller, disabled) in &mut bodies {
        if let Some((body_pose, eye_pose, velocity, driver, prop_exit)) = seat {
            let yaw = eye_pose.rotation.to_euler(EulerRot::YXZ).0.to_degrees();
            if let Some(seat) = vehicle_seat.filter(|_| !input.in_seat ||
                input.vehicle_seat.map(|s|s.0) != vehicle_key.map(|s|s.0)) {
                // Source EnterVehicle starts at the vehicle's
                // forward gaze. An optic starts level rather than over the hood.
                // TrySwitchSeat deliberately retains this relative gaze.
                input.yaw = 0.;
                input.pitch = if seat.seat <= 1 && vehicles.get(seat.vehicle).ok()
                    .and_then(|b| crate::vehicle::data::definition(b.type_id))
                    .is_some_and(|d| d.spec.turret_meshes.is_some()) {0.} else {-8.75};
            } else if !input.in_seat {
                input.yaw = (input.yaw - yaw).rem_euclid(360.);
            }
            input.in_seat = true;
            input.vehicle_seat = vehicle_key;
            input.seat_yaw = yaw;
            input.prop_exit = prop_exit;
            *transform = body_pose;
            body.velocity = velocity;
            body.grounded = false;
            body.stance = if driver { "driving" } else { "sitting" }.into();
            body.locomotion = default();
            controller.translation = None;
            if disabled.is_none() {
                commands.entity(entity).insert(ColliderDisabled);
            }
        } else if input.in_seat {
            input.in_seat = false;
            input.vehicle_seat = None;
            input.yaw = (input.yaw + input.seat_yaw).rem_euclid(360.);
            if let Some((anchor, exit)) = input.prop_exit.take() {
                transform.translation = context
                    .single()
                    .ok()
                    .and_then(|physics| {
                        prop_exit_position(&physics, entity, anchor, exit, body.height)
                    })
                    .unwrap_or(transform.translation);
            } else if let Some(position) = vector(&snapshot.0["player"]["position"]) {
                transform.translation = position;
            }
            transform.rotation = Quat::IDENTITY;
            body.velocity = Vec3::ZERO;
            body.grounded = false;
            body.stance = "stand".into();
            controller.translation = None;
            // This is a teleport, not a kinematic sweep from the seat through
            // the hull. Rapier's normal Transform sync only assigns the next
            // pose and would push the car at (door-seat)/dt when re-enabled.
            let position = transform.translation;
            commands.queue(move |world: &mut World| {
                crate::world::origin::move_physics_owner(world, entity, position);
            });
            commands.entity(entity).remove::<ColliderDisabled>();
        }
    }
}

fn prop_exit_position(
    context: &RapierContext,
    entity: Entity,
    anchor: Vec3,
    exit: Vec3,
    height: f32,
) -> Option<Vec3> {
    let delta = (exit - anchor).with_y(0.);
    let radius = if delta.length() < 0.5 {
        1.2
    } else {
        delta.length()
    };
    let angle = delta.z.atan2(delta.x);
    let filter = QueryFilter::default()
        .exclude_collider(entity)
        .exclude_sensors()
        .groups(player_collision_groups());
    let floor = filter.groups(CollisionGroups::new(
        Group::GROUP_2,
        Group::GROUP_1 | Group::GROUP_4,
    ));
    let capsule = Collider::capsule(Vec3::Y * (0.25 + 0.35), Vec3::Y * (height - 0.35), 0.35);
    let clamp = |mut point: Vec3| {
        if let Some((_, distance)) =
            context.cast_ray(point + Vec3::Y * 2., Vec3::NEG_Y, 5., true, floor)
        {
            let ground = point.y + 2. - distance;
            if point.y < ground + 0.1 {
                point.y = ground + 0.5;
            }
        }
        point
    };
    let fits = |point| {
        let mut blocked = false;
        context.intersect_shape(point, Quat::IDENTITY, &capsule, filter, |_, _| {
            blocked = true;
            false
        });
        !blocked
    };
    let first = clamp(exit);
    if fits(first) {
        return Some(first);
    }
    for out in [0., 0.55, 1.15] {
        for degrees in [0f32, 40., -40., 80., -80., 120., -120., 160., -160., 180.] {
            let bearing = angle + degrees.to_radians();
            let point = clamp(Vec3::new(
                anchor.x + bearing.cos() * (radius + out),
                exit.y,
                anchor.z + bearing.sin() * (radius + out),
            ));
            if fits(point) {
                return Some(point);
            }
        }
    }
    warn!("No clear chair exit near {anchor}; retaining current position");
    None
}
fn camera_follow(
    origin: Res<crate::world::origin::WorldOrigin>,
    settings: Res<Settings>,
    config: Res<crate::world::WorldConfig>,
    ui: Res<UiState>,
    input: Res<PlayerInput>,
    world: Res<WorldState>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    prop_occupancy: Option<Res<crate::world::interactables::PropOccupancy>>,
    body: Query<(Entity, &Transform, &PlayerBody), Without<PlayerCamera>>,
    mut perspective: ResMut<crate::avatar::Perspective>,
    time: Res<Time>,
    context: ReadRapierContext,
    mut vehicles: crate::avatar::camera::VehicleCameras,
    lean: Option<Res<lean::LeanState>>,
    mut camera: Query<(&mut Transform, &mut Projection), (With<PlayerCamera>, Without<PlayerBody>)>,
    mut crafting_eye: (ResMut<CraftingEye>, Res<WorldSessionEpoch>),
) {
    let Ok((entity, body, posture)) = body.single() else {
        return;
    };
    // Source advances DriverPeekOffset once per frame, including its return
    // toward zero in third person, before choosing FP or chase presentation.
    let vehicle_seat = occupancy.as_ref().and_then(|o| o.seat);
    let vehicle_eye = vehicles
        .seated_eye(
            vehicle_seat,
            &perspective,
            input.yaw,
            time.delta_secs(),
            simulation_paused(&ui),
        )
        .or_else(|| vehicle_seat.map(|seat| seat.eye_pose));
    for (mut transform, mut projection) in &mut camera {
        if let Projection::Perspective(p) = &mut *projection {
            p.fov = settings.field_of_view.to_radians();
        }
        if let Some((position, yaw, pitch)) = config.fixed_camera {
            if let Projection::Perspective(p) = &mut *projection {
                p.fov = 70_f32.to_radians();
                p.far = 6000.;
            }
            *transform = Transform::from_translation(origin.local(position)).with_rotation(
                Quat::from_euler(EulerRot::YXZ, yaw.to_radians(), pitch.to_radians(), 0.),
            );
            crafting_eye.0.set(transform.translation, &origin, &crafting_eye.1);
            continue;
        }
        if !ui.menu_camera() {
            if !perspective.first_person {
                if let Some(pose) = occupancy
                    .as_ref()
                    .and_then(|o| o.seat)
                    .and_then(|s| vehicles.follow(s, &perspective, context.single().ok().as_ref()))
                {
                    *transform = pose;
                    crafting_eye.0.set(transform.translation, &origin, &crafting_eye.1);
                    continue;
                }
                if let Some(seat) = prop_occupancy.as_ref().and_then(|o| o.seat) {
                    // Furniture keeps the original on-foot shoulder camera in
                    // third person; only first person sits at the seat's eye.
                    *transform = crate::avatar::camera::follow(
                        &mut perspective, context.single().ok().as_ref(), entity,
                        seat.body_pose.translation, eye_height("sitting"),
                        StanceHeights::default().for_stance("sitting"),
                        input.seat_yaw + input.yaw, input.pitch, 0., time.delta_secs(),
                    );
                    crafting_eye.0.set(transform.translation, &origin, &crafting_eye.1);
                    continue;
                }
            }
            let look = Quat::from_euler(
                EulerRot::YXZ,
                input.yaw.to_radians(),
                input.pitch.to_radians(),
                0.0,
            );
            if let Some(eye) = vehicle_eye.or_else(|| {
                prop_occupancy
                    .as_ref()
                    .and_then(|o| o.seat)
                    .map(|s| s.eye_pose)
            }) {
                transform.translation = eye.translation;
                transform.rotation = eye.rotation * look;
            } else {
                // Source TP pivot eases toward target stance height itself;
                // only FP uses the separately smoothed physical-eye height.
                let eye = if perspective.first_person {
                    lean.as_ref()
                        .map_or(posture.eye_height, |lean| lean.eye_height)
                } else {
                    posture.eye_height
                };
                *transform = crate::avatar::camera::follow(
                    &mut perspective,
                    context.single().ok().as_ref(),
                    entity,
                    body.translation,
                    eye,
                    posture.height,
                    input.yaw,
                    input.pitch,
                    lean.as_ref().map_or(0., |lean| lean.angle),
                    time.delta_secs(),
                );
            }
        } else {
            *transform = Transform::from_translation(world.focus + Vec3::new(26.0, 17.0, 30.0))
                .looking_at(world.focus, Vec3::Y);
        }
        // CraftingStationTags explicitly uses the completed _cam position,
        // including source shoulder/chase views. Never reuse a previous shot.
        crafting_eye.0.set(transform.translation, &origin, &crafting_eye.1);
    }
}

pub(crate) fn simulation_paused(ui: &UiState) -> bool {
    !ui.networked
        && (ui.menu_camera() || ui.screen == Screen::Paused
            || (ui.screen == Screen::Settings
                && matches!(ui.settings_return, Screen::MainMenu | Screen::Paused)))
}

/// A queued authority teleport can be drained by the origin barrier itself.
/// Adopt it before the next movement solve, using the normal correction contract.
pub(crate) fn adopt_origin_correction(world: &mut World, state: &Value) -> Vec<(Entity, Vec3)> {
    let version = state["player"]["correctionVersion"].as_u64().unwrap_or(0);
    if world
        .get_resource::<Session>()
        .is_some_and(|s| s.correction_version == version)
    {
        return Vec::new();
    }
    let Some(position) = vector(&state["player"]["position"]) else {
        return Vec::new();
    };
    if let Some(mut session) = world.get_resource_mut::<Session>() {
        session.correction_version = version;
    }
    let mut moved = Vec::new();
    for (entity, mut transform, mut body) in world
        .query::<(Entity, &mut Transform, &mut PlayerBody)>()
        .iter_mut(world)
    {
        transform.translation = position;
        body.velocity = Vec3::ZERO;
        body.grounded = false;
        body.locomotion = default();
        moved.push((entity, position));
    }
    moved
}
#[cfg(test)]
pub(crate) fn origin_test_player(world: &mut World, position: Vec3, correction: u64) -> Entity {
    world.insert_resource(Session {
        correction_version: correction,
        ..default()
    });
    world
        .spawn((
            Transform::from_translation(position),
            PlayerBody {
                velocity: Vec3::X * 3.,
                grounded: true,
                height: 2.,
                stance: "stand".into(),
                eye_height: 1.75,
                locomotion: default(),
            },
        ))
        .id()
}

/// Translate private movement anchors; the world-origin pass moves body/camera
/// transforms and Rapier together exactly once.
pub fn shift_origin(world: &mut World, shift: Vec3) {
    if let Some(mut input) = world.get_resource_mut::<PlayerInput>() {
        if let Some((anchor, exit)) = &mut input.prop_exit {
            *anchor -= shift;
            *exit -= shift;
        }
    }
    for mut body in world.query::<&mut PlayerBody>().iter_mut(world) {
        body.locomotion.shift_origin(shift);
    }
}

fn physics_configuration(ui: Res<UiState>, mut configs: Query<&mut RapierConfiguration>) {
    for mut config in &mut configs {
        config.gravity = Vec3::new(0., -9.8, 0.);
        config.physics_pipeline_active = !simulation_paused(&ui);
    }
}

fn has_headroom(
    context: &RapierContext<'_>,
    owner: Entity,
    feet: Vec3,
    current_height: f32,
    wanted_height: f32,
) -> bool {
    if wanted_height <= current_height {
        return true;
    }
    // Original HeadroomFor: omit the bottom 0.25m and slim to radius 0.30m,
    // so the supporting floor is not mistaken for a ceiling.
    let probe = Collider::capsule(Vec3::Y * 0.55, Vec3::Y * (wanted_height - 0.3), 0.3);
    let mut blocked = false;
    context.intersect_shape(
        feet,
        Quat::IDENTITY,
        &probe,
        QueryFilter::default()
            .exclude_collider(owner)
            .exclude_sensors()
            .groups(player_collision_groups()),
        |_, _| {
            blocked = true;
            false
        },
    );
    !blocked
}
/// Shared source bits0|6 predicate. Vehicle hit meshes/glass use other source
/// layers; fallback chassis, ghost world bodies and authored props remain.
pub(crate) fn world_or_prop(
    _: Entity,
    collider: &bevy_rapier3d::rapier::geometry::Collider,
) -> bool {
    let groups = collider.collision_groups();
    let vehicle = groups.memberships.bits() & Group::GROUP_4.bits() != 0;
    let fallback = groups.filter.bits() & (Group::GROUP_2 | Group::GROUP_4).bits()
        == (Group::GROUP_2 | Group::GROUP_4).bits();
    let ghost = groups.memberships.bits() & crate::vehicle::coupling::GHOST_TRAILER.bits() != 0;
    let source_prop = groups.memberships.bits() & crate::world::buildings::SOURCE_PROP.bits() != 0;
    !vehicle || fallback || ghost || source_prop
}
/// Shared source bit0 (SolidBit) predicate for GROUP1|GROUP4 queries: world
/// solids but not bit6 authored props; a vehicle only through a fallback
/// chassis that is not tow-ghosted (HitMesh/SetTowGhost move it off bit0).
pub(crate) fn world_solid(
    _: Entity,
    collider: &bevy_rapier3d::rapier::geometry::Collider,
) -> bool {
    let groups = collider.collision_groups();
    let memberships = groups.memberships.bits();
    if memberships & Group::GROUP_4.bits() != 0 {
        let fallback = groups.filter.bits() & (Group::GROUP_2 | Group::GROUP_4).bits()
            == (Group::GROUP_2 | Group::GROUP_4).bits();
        fallback && memberships & crate::vehicle::coupling::GHOST_TRAILER.bits() == 0
    } else {
        memberships & crate::world::buildings::SOURCE_PROP.bits() == 0
    }
}
fn throw_clearance(
    context: &RapierContext,
    owner: Entity,
    eye: Vec3,
    direction: Vec3,
    length: f32,
) -> Option<f32> {
    // ThrowOrigin uses source bits0|6. HitMesh/panes are bits15|5, so neither
    // those nor a stripped driving hull participate; a fallback chassis keeps
    // its original world bit. The grenade's later bounce query is independent.
    context
        .cast_ray(
            eye,
            direction,
            length,
            true,
            QueryFilter::default()
                .exclude_collider(owner)
                .exclude_sensors()
                .predicate(&world_or_prop)
                .groups(CollisionGroups::new(
                    Group::ALL,
                    Group::GROUP_1 | Group::GROUP_4,
                )),
        )
        .map(|(_, distance)| distance)
}
fn eye_height(stance: &str) -> f32 {
    match stance {
        "crouch" => 1.2,
        "prone" => 0.35,
        _ => 1.75,
    }
}
pub(crate) fn vector(value: &Value) -> Option<Vec3> {
    let v = if let Some(a) = value.as_array() {
        if a.len() != 3 {
            return None;
        }
        Vec3::new(
            a[0].as_f64()? as f32,
            a[1].as_f64()? as f32,
            a[2].as_f64()? as f32,
        )
    } else {
        // Original network event structs retain their {x,y,z} payload, while
        // bridge snapshot adapters deliberately write compact three-vectors.
        Vec3::new(
            value["x"].as_f64()? as f32,
            value["y"].as_f64()? as f32,
            value["z"].as_f64()? as f32,
        )
    };
    v.is_finite().then_some(v)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scope_sway_enters_shared_look_once_and_unwinds_without_consuming_mouse_aim() {
        let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let mut app = App::new();
        let mut model = UiModel::default();
        model.connected = true;
        app.add_plugins(MinimalPlugins)
            .insert_resource(bridge)
            .init_resource::<ButtonInput<KeyCode>>()
            .init_resource::<ButtonInput<MouseButton>>()
            .init_resource::<AccumulatedMouseMotion>()
            .init_resource::<Settings>()
            .init_resource::<crate::viewmodel::ViewmodelStatus>()
            .init_resource::<crate::recoil::Recoil>()
            .init_resource::<GameSnapshot>()
            .init_resource::<crate::entities::ItemCatalog>()
            .init_resource::<WorldSessionEpoch>()
            .insert_resource(UiState {
                screen: Screen::Playing,
                ..default()
            })
            .insert_resource(model)
            .insert_resource(PlayerInput {
                yaw: 30.,
                pitch: 10.,
                ..default()
            })
            .add_systems(Update, read_input);
        app.world_mut()
            .resource_mut::<crate::viewmodel::ViewmodelStatus>()
            .scope_sway = Vec2::new(0.5, 0.75);
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .press(KeyCode::ShiftLeft);
        app.update();
        app.update();
        let input = app.world().resource::<PlayerInput>();
        assert_eq!((input.pitch, input.yaw), (10.5, 30.75));
        assert!(input.steady_key);
        assert_eq!(
            app.world()
                .resource::<crate::viewmodel::ViewmodelStatus>()
                .look_delta,
            Vec2::ZERO
        );
        app.world_mut().resource_mut::<PlayerInput>().yaw += 4.;
        app.world_mut()
            .resource_mut::<crate::viewmodel::ViewmodelStatus>()
            .scope_sway = Vec2::ZERO;
        app.update();
        let input = app.world().resource::<PlayerInput>();
        assert_eq!((input.pitch, input.yaw), (10., 34.));
        app.world_mut()
            .resource_mut::<AccumulatedMouseMotion>()
            .delta = Vec2::new(2., 3.);
        app.update();
        let sensitivity = app.world().resource::<Settings>().mouse_sensitivity;
        assert_eq!(
            app.world()
                .resource::<crate::viewmodel::ViewmodelStatus>()
                .look_delta,
            Vec2::new(3., 2.) * sensitivity
        );
        app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
        app.update();
        assert!(!app.world().resource::<PlayerInput>().steady_key);
        assert_eq!(
            app.world()
                .resource::<crate::viewmodel::ViewmodelStatus>()
                .look_delta,
            Vec2::ZERO
        );
    }
    #[test]
    fn alt_look_stops_continuous_tool_fire_and_discards_pending_attack_edges() {
        let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let mut model = UiModel::default();
        model.connected = true;
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .insert_resource(bridge)
            .init_resource::<ButtonInput<KeyCode>>()
            .init_resource::<ButtonInput<MouseButton>>()
            .init_resource::<AccumulatedMouseMotion>()
            .init_resource::<Settings>()
            .init_resource::<crate::viewmodel::ViewmodelStatus>()
            .init_resource::<crate::recoil::Recoil>()
            .init_resource::<GameSnapshot>()
            .init_resource::<crate::entities::ItemCatalog>()
            .init_resource::<WorldSessionEpoch>()
            .insert_resource(UiState {
                screen: Screen::Playing,
                ..default()
            })
            .insert_resource(model)
            .insert_resource(PlayerInput {
                fire_pressed: true,
                secondary_pressed: true,
                ..default()
            })
            .add_systems(Update, read_input);
        app.world_mut()
            .resource_mut::<ButtonInput<MouseButton>>()
            .press(MouseButton::Left);
        app.update();
        assert!(app.world().resource::<PlayerInput>().fire);
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .press(KeyCode::AltLeft);
        app.update();
        let input = app.world().resource::<PlayerInput>();
        assert!(input.free_look);
        assert!(!input.fire && !input.fire_pressed && !input.secondary_pressed);
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .release(KeyCode::AltLeft);
        app.world_mut()
            .resource_mut::<ButtonInput<MouseButton>>()
            .clear_just_pressed(MouseButton::Left);
        app.update();
        let input = app.world().resource::<PlayerInput>();
        assert!(!input.free_look && input.fire);
        assert!(!input.fire_pressed && !input.secondary_pressed);
    }
    #[test]
    fn source_event_vectors_and_snapshot_vectors_share_one_finite_decoder() {
        let source =
            json!({"pos":{"x":0.,"y":0.109375,"z":-1.609375},"radius":9.3125,"itemId":262});
        let expected = Vec3::new(0., 0.109375, -1.609375);
        assert_eq!(vector(&source["pos"]), Some(expected));
        assert_eq!(vector(&json!(expected.to_array())), Some(expected));
        assert_eq!(vector(&json!({"x":1.,"y":2.})), None);
        assert_eq!(vector(&json!([0., 0., 1e100])), None);
    }
    #[test]
    fn chair_exit_fans_around_a_wall_and_keeps_the_player_when_surrounded() {
        use bevy::ecs::system::SystemState;
        let mut app = App::new();
        app.add_plugins((
            MinimalPlugins,
            TransformPlugin,
            RapierPhysicsPlugin::<NoUserData>::default(),
        ));
        app.insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ));
        let owner = app
            .world_mut()
            .spawn((
                RigidBody::KinematicPositionBased,
                capsule(2.),
                Transform::default(),
                ColliderDisabled,
            ))
            .id();
        app.world_mut().spawn((
            Collider::cuboid(5., 0.1, 5.),
            Transform::from_xyz(0., -0.1, 0.),
        ));
        app.world_mut().spawn((
            Collider::cuboid(0.12, 1.5, 0.55),
            Transform::from_xyz(1.1, 1.5, 0.),
        ));
        for _ in 0..3 {
            app.update();
        }
        {
            let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
            let read = state.get(app.world()).unwrap();
            let exit = prop_exit_position(
                &read.single().unwrap(),
                owner,
                Vec3::ZERO,
                Vec3::new(1.1, 0.1, 0.),
                2.,
            )
            .unwrap();
            assert!(exit.z.abs() > 0.5);
            assert!(exit.y >= 0.1);
        }
        app.world_mut().spawn((
            Collider::cuboid(4., 2., 4.),
            Transform::from_xyz(0., 2., 0.),
        ));
        for _ in 0..3 {
            app.update();
        }
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        let read = state.get(app.world()).unwrap();
        assert!(
            prop_exit_position(
                &read.single().unwrap(),
                owner,
                Vec3::ZERO,
                Vec3::new(1.1, 0.1, 0.),
                2.
            )
            .is_none()
        );
    }

    #[test]
    fn settings_keeps_the_parent_pause_state() {
        let mut ui = UiState {
            screen: Screen::Settings,
            settings_return: Screen::Paused,
            ..default()
        };
        assert!(simulation_paused(&ui));
        ui.settings_return = Screen::MainMenu;
        assert!(simulation_paused(&ui));
        ui.screen = Screen::Inventory;
        assert!(!simulation_paused(&ui));
    }

    #[test]
    fn headroom_probe_rejects_ceiling_but_ignores_own_body_and_supporting_floor() {
        use bevy::ecs::system::SystemState;
        let mut app = App::new();
        app.add_plugins((
            MinimalPlugins,
            TransformPlugin,
            RapierPhysicsPlugin::<NoUserData>::default(),
        ));
        app.insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ));
        let owner = app
            .world_mut()
            .spawn((
                RigidBody::KinematicPositionBased,
                capsule(1.2),
                Transform::default(),
            ))
            .id();
        app.world_mut().spawn((
            Collider::cuboid(5.0, 0.1, 5.0),
            Transform::from_xyz(0.0, -0.1, 0.0),
        ));
        let ceiling = app
            .world_mut()
            .spawn((
                Collider::cuboid(2.0, 0.1, 2.0),
                Transform::from_xyz(0.0, 1.6, 0.0),
            ))
            .id();
        for _ in 0..3 {
            app.update();
        }
        {
            let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
            let read = state.get(app.world()).unwrap();
            let context = read.single().unwrap();
            assert!(!has_headroom(&context, owner, Vec3::ZERO, 1.2, 2.0));
            assert!(has_headroom(&context, owner, Vec3::ZERO, 1.2, 0.8));
        }
        app.world_mut().despawn(ceiling);
        for _ in 0..2 {
            app.update();
        }
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        let read = state.get(app.world()).unwrap();
        let context = read.single().unwrap();
        assert!(has_headroom(&context, owner, Vec3::ZERO, 1.2, 2.0));
    }
}
