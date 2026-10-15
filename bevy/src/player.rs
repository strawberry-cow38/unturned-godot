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

pub struct PlayerPlugin;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PlayerUpdate;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PlayerCameraUpdate;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PublishPlayer;
#[derive(Resource, Default)]
pub struct WorldSessionEpoch(pub u64);
impl Plugin for PlayerPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Session>()
            .init_resource::<WorldSessionEpoch>()
            .init_resource::<crate::weapons::Trigger>()
            .init_resource::<crate::use_items::HandUse>()
            .add_message::<crate::use_items::HandEvent>()
            .init_resource::<PlayerInput>()
            .init_resource::<GameSnapshot>()
            .add_systems(Startup, spawn_player.after(WorldReady))
            .add_systems(
                Update,
                (
                    initialize_session,
                    ui_intents,
                    close_storage_on_exit,
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
            .add_systems(FixedUpdate, simulate.before(PhysicsSet::SyncBackend))
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
    heights: StanceHeights,
}

impl Default for Session {
    fn default() -> Self {
        Self {
            initialized: false,
            failed: false,
            correction_version: 0,
            heights: default(),
        }
    }
}

#[derive(Clone, Copy)]
struct StanceHeights {
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
    fn for_stance(self, stance: &str) -> f32 {
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
struct PlayerInput {
    movement: Vec2,
    jump: bool,
    jump_held: bool,
    fire: bool,
    fire_pressed: bool,
    secondary_pressed: bool,
    selector: bool,
    reload: bool,
    yaw: f32,
    pitch: f32,
    crouch_toggle: bool,
    prone: bool,
    stance: String,
    held_item_id: u16,
    in_seat: bool,
    seat_yaw: f32,
    prop_exit: Option<(Vec3, Vec3)>,
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
#[derive(Component)]
pub struct PlayerCamera;

fn capsule(height: f32) -> Collider {
    let radius = 0.35;
    Collider::capsule(Vec3::Y * radius, Vec3::Y * (height - radius), radius)
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
        CollisionGroups::new(Group::GROUP_2, Group::ALL & !Group::GROUP_3),
        KinematicCharacterController {
            filter_groups: Some(CollisionGroups::new(
                Group::GROUP_2,
                Group::ALL & !Group::GROUP_3,
            )),
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
    bridge: Res<CoreBridge>,
    options: Res<LaunchOptions>,
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
    if session.initialized || session.failed || !world.ready {
        return;
    }
    model.map_name = options.0.map.clone();
    ui.map = options.0.map.clone();
    match bridge.request("init", json!({"contentRoot":options.0.content,"name":"Survivor","mapId":options.0.map,"spawn":world.spawn.to_array(),"yaw":world.spawn_yaw,"seed":options.0.seed})) {
        Ok(response) if response["result"]["protocolVersion"] == 1 => {
            model.reset_session();
            model.adopt_snapshot(&response["state"]); snapshot.0 = response["state"].clone();
            model.status = format!("{} · {} objects", world.description, world.object_count);
            session.initialized = true;
            epoch.0=epoch.0.wrapping_add(1);
            session.correction_version=response["state"]["player"]["correctionVersion"].as_u64().unwrap_or(0);
            session.heights=StanceHeights::from_reply(&response["result"]["movementHeights"]);
            *input=PlayerInput {yaw:world.spawn_yaw,stance:"stand".into(),..default()};
            for (mut transform, mut body,mut collider,mut controller) in &mut bodies {
                transform.translation = world.spawn; body.velocity = Vec3::ZERO; body.grounded = false;
                body.height=session.heights.stand;body.stance="stand".into();body.eye_height=1.75;
                body.locomotion=default();
                *collider=capsule(body.height);controller.translation=None;
            }
            if options.0.play { ui.screen = Screen::Playing; }
        }
        Ok(_) => fail(&mut session, &mut model, "Unsupported core bridge protocol".into()),
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
    mut hand_use: (ResMut<crate::use_items::HandUse>, ResMut<WorldSessionEpoch>),
    mut trigger: ResMut<crate::weapons::Trigger>,
    mut hand_events: MessageWriter<crate::use_items::HandEvent>,
    mut commands: Commands,
) {
    for intent in intents.read() {
        let result = match intent {
            UiIntent::Play { map } => {
                if map != &options.0.map {
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
                    config.map_name = map.clone();
                    config.map_root = options
                        .0
                        .unturned
                        .as_ref()
                        .map(|p| p.join("Maps").join(map));
                    options.0.map = map.clone();
                    options.0.play = true;
                    world.ready = false;
                    session.initialized = false;
                    session.failed = false;
                    model.status = format!("Loading {map}");
                    ui.screen = Screen::MainMenu;
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
                ui.screen = Screen::MainMenu;
                continue;
            }
            UiIntent::Save => bridge.request("save", json!({"path":save_path(&options.0.map)})),
            UiIntent::Load => bridge.request("load", json!({"path":save_path(&options.0.map)})),
            UiIntent::Action { name, args } => {
                bridge.request("action", json!({"name":name,"args":args}))
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

fn close_storage_on_exit(
    bridge: Res<CoreBridge>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    mut previous: Local<Screen>,
) {
    if model.connected && *previous == Screen::Inventory && ui.screen != Screen::Inventory {
        if let Err(error) = bridge.request("action", json!({"name":"closeStorage","args":{}})) {
            warn!("Closing storage: {error}");
        }
    }
    *previous = ui.screen;
}

fn save_path(map: &str) -> std::path::PathBuf {
    let base = std::env::var_os("UG_BEVY_SAVES")
        .or_else(|| std::env::var_os("UG_SAVE_DIR"))
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
    viewmodel: Res<crate::viewmodel::ViewmodelStatus>,
    ui: Res<UiState>,
    mut model: ResMut<UiModel>,
    mut input: ResMut<PlayerInput>,
    mut windows: Query<(&mut CursorOptions, &mut Window), With<PrimaryWindow>>,
) {
    let playing = !ui.captures_input() && model.connected;
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
        return;
    }
    let held = |action| settings.pressed(action, &keys, &mouse);
    let pressed = |action| settings.just_pressed(action, &keys, &mouse);
    input.movement = Vec2::new(
        (held(GameAction::MoveRight) as i32 - held(GameAction::MoveLeft) as i32) as f32,
        (held(GameAction::MoveForward) as i32 - held(GameAction::MoveBack) as i32) as f32,
    );
    input.jump |= pressed(GameAction::Jump);
    input.jump_held = held(GameAction::Jump);
    input.fire = held(GameAction::Fire);
    input.fire_pressed |= pressed(GameAction::Fire);
    input.secondary_pressed |= pressed(GameAction::Aim);
    input.selector |= pressed(GameAction::Firemode);
    input.reload |= pressed(GameAction::Reload);
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
    let aircraft_controls = occupancy
        .as_ref()
        .and_then(|o| o.seat)
        .is_some_and(|seat| seat.driver && seat.aircraft)
        && !held(GameAction::FreeLook);
    if !aircraft_controls {
        let sensitivity = settings.mouse_sensitivity * (1. - 0.35 * viewmodel.aim_alpha);
        input.yaw = (input.yaw - motion.delta.x * sensitivity).rem_euclid(360.0);
        let sign = if settings.invert_look_y { 1.0 } else { -1.0 };
        input.pitch = (input.pitch + motion.delta.y * sensitivity * sign).clamp(-89.0, 89.0);
    }
    for (action, slot) in [(GameAction::Hotbar1, 0), (GameAction::Hotbar2, 1)] {
        if pressed(action) {
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
    ),
    ladders: Query<(&crate::world::WorldObject, &GlobalTransform, &Collider), Without<PlayerBody>>,
    mut bodies: Query<(
        Entity,
        &mut Transform,
        &mut PlayerBody,
        &mut Collider,
        &mut KinematicCharacterController,
    )>,
) {
    if !session.initialized || session.failed || simulation_paused(&ui) {
        return;
    }
    let Ok((entity, mut transform, mut body, mut collider, mut controller)) = bodies.single_mut()
    else {
        return;
    };
    if occupancy.0.is_some_and(|o| o.seat.is_some())
        || occupancy.1.is_some_and(|o| o.seat.is_some())
        || !model.alive
    {
        trigger.cancel();
        let mut events = Vec::new();
        hands.0.cancel(&mut events);
        hands.1.write_batch(events);
        controller.translation = None;
        return;
    }
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
    let wanted_stance =
        if special.is_some() || (wading && matches!(input.stance.as_str(), "crouch" | "prone")) {
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
        "headroom":headroom,"capsuleHeight":body.height,"equipmentAllowsSprint":!viewmodel.aiming
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
            trigger.acknowledge(&snapshot.0);
            let item = catalog.items.get(&input.held_item_id);
            let buttons = crate::weapons::Buttons {
                fire: input.fire,
                press: std::mem::take(&mut input.fire_pressed),
                strong: std::mem::take(&mut input.secondary_pressed),
                reload: std::mem::take(&mut input.reload),
                selector: std::mem::take(&mut input.selector),
            };
            let allowed = !ui.captures_input() && !matches!(body.stance.as_str(), "swim" | "climb");
            let (uses, events) = hands.0.step(
                0.02,
                &snapshot.0,
                item,
                &catalog.throw_motion,
                &buttons,
                viewmodel.ready && viewmodel.held == input.held_item_id,
                allowed,
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
                        let direction = Quat::from_euler(
                            EulerRot::YXZ,
                            input.yaw.to_radians(),
                            input.pitch.to_radians(),
                            0.,
                        ) * Vec3::NEG_Z;
                        let eye = transform.translation + Vec3::Y * body.eye_height;
                        let probe =
                            catalog.throw_motion["clearProbe"].as_f64().unwrap_or(1.5) as f32;
                        let gap = catalog.throw_motion["clearGap"].as_f64().unwrap_or(0.5) as f32;
                        let hit = context
                            .single()
                            .ok()
                            .and_then(|p| {
                                p.cast_ray(
                                    eye,
                                    direction,
                                    probe,
                                    true,
                                    QueryFilter::default().exclude_collider(entity),
                                )
                            })
                            .map(|(_, d)| d);
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
                !ui.captures_input()
                    && viewmodel.ready
                    && viewmodel.held == input.held_item_id
                    && !matches!(body.stance.as_str(), "swim" | "climb"),
            );
            for action in actions {
                let eye = body.eye_height;
                let direction = Quat::from_euler(
                    EulerRot::YXZ,
                    input.yaw.to_radians(),
                    input.pitch.to_radians(),
                    0.0,
                ) * Vec3::NEG_Z;
                let request = match action {
                    crate::weapons::Intent::Fire => {
                        json!({"name":"fire","args":{"origin":(transform.translation+Vec3::Y*eye).to_array(),"dir":direction.to_array()}})
                    }
                    crate::weapons::Intent::Melee(strong) => {
                        json!({"name":"melee","args":{"strong":strong,"yawDegrees":input.yaw}})
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
        Err(error) => fail(&mut session, &mut model, error.to_string()),
    }
}

fn publish_body(
    bridge: Res<CoreBridge>,
    ui: Res<UiState>,
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
    if let Some(output) = output.filter(|_| !input.in_seat && model.alive) {
        body.grounded = output.grounded;
        body.velocity = output.effective_translation / 0.02;
        if body.grounded && body.velocity.y < 0.0 {
            body.velocity.y = 0.0;
        }
    }
    match bridge.request("tick",json!({"input":{"position":transform.translation.to_array(),"velocity":body.velocity.to_array(),"grounded":body.grounded,"yaw":input.yaw,"pitch":input.pitch,"stance":body.stance,"heldItemId":input.held_item_id}})) {
        Ok(response) => {
            let state = &response["state"];
            if let Some(held)=state["player"]["heldItemId"].as_u64() {input.held_item_id=held as u16;}
            let version = state["player"]["correctionVersion"].as_u64().unwrap_or(0);
            if version != session.correction_version {
                if let Some(position) = vector(&state["player"]["position"]) { transform.translation = position; body.velocity = Vec3::ZERO; body.grounded = false; body.locomotion=default(); }
                session.correction_version = version;
            }
            model.adopt_snapshot(state); snapshot.0 = state.clone();
        }
        Err(error) => fail(&mut session,&mut model,error.to_string()),
    }
}

fn sync_seated_body(
    mut commands: Commands,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    prop_occupancy: Option<Res<crate::world::interactables::PropOccupancy>>,
    context: ReadRapierContext,
    snapshot: Res<GameSnapshot>,
    mut input: ResMut<PlayerInput>,
    mut bodies: Query<(
        Entity,
        &mut Transform,
        &mut PlayerBody,
        &mut KinematicCharacterController,
        Option<&ColliderDisabled>,
    )>,
) {
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
            if !input.in_seat {
                input.yaw = (input.yaw - yaw).rem_euclid(360.);
            }
            input.in_seat = true;
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
        .exclude_sensors();
    let capsule = Collider::capsule(Vec3::Y * (0.25 + 0.35), Vec3::Y * (height - 0.35), 0.35);
    let clamp = |mut point: Vec3| {
        if let Some((_, distance)) =
            context.cast_ray(point + Vec3::Y * 2., Vec3::NEG_Y, 5., true, filter)
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
    settings: Res<Settings>,
    config: Res<crate::world::WorldConfig>,
    ui: Res<UiState>,
    input: Res<PlayerInput>,
    world: Res<WorldState>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    prop_occupancy: Option<Res<crate::world::interactables::PropOccupancy>>,
    body: Query<(&Transform, &PlayerBody), Without<PlayerCamera>>,
    mut camera: Query<(&mut Transform, &mut Projection), (With<PlayerCamera>, Without<PlayerBody>)>,
) {
    let Ok((body, posture)) = body.single() else {
        return;
    };
    for (mut transform, mut projection) in &mut camera {
        if let Projection::Perspective(p) = &mut *projection {
            p.fov = settings.field_of_view.to_radians();
        }
        if let Some((position, yaw, pitch)) = config.fixed_camera {
            if let Projection::Perspective(p) = &mut *projection {
                p.fov = 70_f32.to_radians();
                p.far = 6000.;
            }
            *transform = Transform::from_translation(position).with_rotation(Quat::from_euler(
                EulerRot::YXZ,
                yaw.to_radians(),
                pitch.to_radians(),
                0.,
            ));
            continue;
        }
        if ui.screen != Screen::MainMenu
            && !(ui.screen == Screen::Settings && ui.settings_return == Screen::MainMenu)
        {
            let look = Quat::from_euler(
                EulerRot::YXZ,
                input.yaw.to_radians(),
                input.pitch.to_radians(),
                0.0,
            );
            if let Some(eye) = occupancy
                .as_ref()
                .and_then(|o| o.seat)
                .map(|s| s.eye_pose)
                .or_else(|| {
                    prop_occupancy
                        .as_ref()
                        .and_then(|o| o.seat)
                        .map(|s| s.eye_pose)
                })
            {
                transform.translation = eye.translation;
                transform.rotation = eye.rotation * look;
            } else {
                transform.translation = body.translation + Vec3::Y * posture.eye_height;
                transform.rotation = look;
            }
        } else {
            *transform = Transform::from_translation(world.focus + Vec3::new(26.0, 17.0, 30.0))
                .looking_at(world.focus, Vec3::Y);
        }
    }
}

pub(crate) fn simulation_paused(ui: &UiState) -> bool {
    matches!(ui.screen, Screen::MainMenu | Screen::Paused)
        || (ui.screen == Screen::Settings
            && matches!(ui.settings_return, Screen::MainMenu | Screen::Paused))
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
            .exclude_sensors(),
        |_, _| {
            blocked = true;
            false
        },
    );
    !blocked
}
fn eye_height(stance: &str) -> f32 {
    match stance {
        "crouch" => 1.2,
        "prone" => 0.35,
        _ => 1.75,
    }
}
pub(crate) fn vector(value: &Value) -> Option<Vec3> {
    let array: [f32; 3] = serde_json::from_value(value.clone()).ok()?;
    let vector = Vec3::from_array(array);
    vector.is_finite().then_some(vector)
}

#[cfg(test)]
mod tests {
    use super::*;
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
