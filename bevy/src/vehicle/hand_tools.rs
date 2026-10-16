//! Source vehicle hand tools: the spraypaint, the spare tire and the carjack
//! (PlayerController EquipHeldSpraypaint/TrySprayVehicle/ApplySpray,
//! EquipHeldTire/TryFitTire/TickTire, EquipHeldCarjack/TryCarjack/ApplyCarjack;
//! Vehicle.SetPaint/Carjack/JackableNow; VehiclePuppet.ApplyReplicatedPaint).
//! The client only times the swing and names the vehicle; commands 50/55/56
//! are validated and applied by core, the paint and tire mask come back on the
//! vehicle row, and the carjack's server-rolled shove is applied only by the
//! physics owner, after Vehicle.Carjack's own empty/wreck/JackableNow gate.
use super::{
    VehicleBody,
    render::{BodyPaint, body_material},
};
use crate::{
    bridge::CoreBridge,
    content::ContentCache,
    entities::{CoreEvent, InteractionFocus, ItemCatalog, ItemVisual},
    player::{GameSnapshot, PlayerBody},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState, input_order::FrameInput},
    use_items::{Cell, HandEvent},
    world::WorldConfig,
};
use bevy::{
    audio::{SpatialScale, Volume},
    input::ButtonState,
    prelude::*,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow, WindowEvent},
};
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};

/// Vehicle carjack constants (CarjackRetailMass, CarjackStepSeconds,
/// JackSettledSpeed, JackSettledDwell, JackRisingSpeed, JackGroundReach) and
/// ProbeAgl's TurbAglReach/TurbFullAgl.
pub(crate) const CARJACK_RETAIL_MASS: f32 = 2.;
pub(crate) const CARJACK_STEP_SECONDS: f32 = 0.02;
pub(crate) const JACK_SETTLED_SPEED: f32 = 1.5;
pub(crate) const JACK_SETTLED_DWELL: f32 = 0.5;
pub(crate) const JACK_RISING_SPEED: f32 = 1.;
pub(crate) const JACK_GROUND_REACH: f32 = 3.;
const AGL_REACH: f32 = 260.;
const AGL_OPEN_AIR: f32 = 140.;
/// SoundBus.Emit(tree, GlobalPosition, 8f) in TryCarjack (source AlertTool.alert(.., 8)).
const CARJACK_NOISE: f32 = 8.;
const SPRAY_SOUND: &str = "audio/misc/vehicle_spraypaint.wav";
const JACK_SOUND: &str = "audio/items/tools_carjack_use.wav";

/// The source `_focusVehicle`, published by `access::focus`.
#[derive(Resource, Default)]
pub(crate) struct LookedVehicle(pub Option<Entity>);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Kind {
    Paint,
    Tire,
    Carjack,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Tool {
    pub kind: Kind,
    pub use_seconds: f32,
    pub apply_fraction: f32,
    pub paint_rgb: Option<u32>,
}
impl Tool {
    pub(crate) fn of(item: &ItemVisual) -> Option<Self> {
        let t = &item.hand_tool;
        Some(Self {
            kind: match t["kind"].as_str()? {
                "paint" => Kind::Paint,
                "tire" => Kind::Tire,
                "carjack" => Kind::Carjack,
                _ => return None,
            },
            use_seconds: t["useSeconds"].as_f64()? as f32,
            apply_fraction: t["applyFraction"].as_f64()? as f32,
            paint_rgb: t["paintRgb"].as_u64().map(|v| v as u32),
        })
    }
}
/// The source focus as each tool reads it. `jackable` is known only where this
/// process owns the physics (the source's local `Vehicle`, not a puppet).
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub(crate) struct Target {
    pub net_id: u32,
    pub position: Vec3,
    pub wreck: bool,
    pub occupied: bool,
    pub popped: u8,
    pub speed_sq: f32,
    pub jackable: Option<bool>,
}
impl Target {
    /// Vehicle.TiresReplaceable: `!Exploded && LinearVelocity.LengthSquared() < 1`.
    fn tires_replaceable(&self) -> bool {
        !self.wreck && self.speed_sq < 1.
    }
}
#[derive(Debug, PartialEq)]
pub(crate) enum Outcome {
    /// The Use clip and its start sound (spray: the can; jack: the ratchet at the car).
    Swing { kind: Kind, item: u16, at: Option<Vec3> },
    Send(&'static str, Value),
    /// HUD.Alert.
    Notice(&'static str),
    /// Log.Print only; the source shows nothing.
    Log(&'static str),
}
#[derive(Clone, Copy, Debug)]
struct Swing {
    cell: Cell,
    tool: Tool,
    elapsed: f32,
    apply_at: f32,
    busy: f32,
    applied: bool,
    target: u32,
}
#[derive(Resource, Default)]
pub(crate) struct HandTools {
    swing: Option<Swing>,
    epoch: u64,
}
impl HandTools {
    pub(crate) fn busy(&self) -> bool {
        self.swing.is_some()
    }
    /// One frame of the source timers. `look` is the focus now; `find` resolves
    /// the vehicle a spare was started on (TickTire re-reads that same car).
    pub(crate) fn step(
        &mut self,
        dt: f32,
        held: Option<(Cell, Tool)>,
        press: bool,
        look: Option<Target>,
        find: impl Fn(u32) -> Option<Target>,
    ) -> (Vec<Outcome>, bool) {
        let mut out = Vec::new();
        // Switching away mid-swing drops the pending effect (ClearHeldSpraypaint,
        // the carjack's `_heldCarjackItem == null` reset, TickTire's held test).
        if self.swing.is_some_and(|s| held.is_none_or(|(c, _)| c != s.cell)) {
            self.swing = None;
            return (out, true);
        }
        if press && self.swing.is_none() && let Some((cell, tool)) = held {
            let apply_at = tool.use_seconds * tool.apply_fraction;
            let mut start = |target: u32, busy: f32, at: Option<Vec3>, out: &mut Vec<Outcome>| {
                self.swing = Some(Swing { cell, tool, elapsed: 0., apply_at, busy, applied: false, target });
                out.push(Outcome::Swing { kind: tool.kind, item: cell.id, at });
            };
            match tool.kind {
                // TrySprayVehicle: no target test until the paint lands.
                Kind::Paint if tool.paint_rgb.is_some() => start(0, tool.use_seconds, None, &mut out),
                Kind::Paint => {}
                // TryFitTire (local car): validate at the click, busy until the wheel goes on.
                Kind::Tire => match look {
                    None => out.push(Outcome::Notice("Aim at a vehicle.")),
                    Some(v) if !v.tires_replaceable() => out.push(Outcome::Notice(if v.wreck {
                        "That one's a wreck."
                    } else {
                        "Stop it moving first."
                    })),
                    Some(v) if v.popped == 0 => out.push(Outcome::Notice("No flat tires on that.")),
                    Some(v) => start(v.net_id, apply_at, None, &mut out),
                },
                // TryCarjack: refuse before the wind-up; a puppet cannot be tested.
                Kind::Carjack => match look {
                    None => out.push(Outcome::Log("[carjack] aim at a vehicle")),
                    Some(v) if v.jackable.is_some() && v.wreck => out.push(Outcome::Log("[carjack] that is a wreck")),
                    Some(v) if v.jackable == Some(false) => {
                        out.push(Outcome::Log("[carjack] it is still in the air -- let it come down"))
                    }
                    Some(v) => start(v.net_id, tool.use_seconds, Some(v.position), &mut out),
                },
            }
        }
        let Some(swing) = &mut self.swing else {
            return (out, false);
        };
        swing.elapsed += dt;
        if !swing.applied && swing.elapsed >= swing.apply_at {
            swing.applied = true;
            match swing.tool.kind {
                // ApplySpray re-reads the focus at 85% of the sweep.
                Kind::Paint => match look {
                    None => out.push(Outcome::Log("[paint] aim at a vehicle")),
                    Some(v) if v.wreck => out.push(Outcome::Log("[paint] that one is a burnt-out wreck")),
                    Some(v) => out.push(Outcome::Send(
                        "paintVehicle",
                        json!({"netId":v.net_id,"itemId":swing.cell.id}),
                    )),
                },
                // TickTire: the same car, still replaceable and still flat.
                Kind::Tire => {
                    if let Some(v) = find(swing.target).filter(|v| v.tires_replaceable() && v.popped != 0) {
                        out.push(Outcome::Send(
                            "fitTire",
                            json!({"vehicleNetId":v.net_id,"wheelIndex":v.popped.trailing_zeros()}),
                        ));
                    }
                }
                // ApplyCarjack re-reads the focus when the stroke finishes.
                Kind::Carjack => match look {
                    None => out.push(Outcome::Log("[carjack] aim at a vehicle")),
                    Some(v) if v.jackable.is_some() && v.wreck => out.push(Outcome::Log("[carjack] that is a wreck")),
                    Some(v) if v.jackable == Some(false) => {
                        out.push(Outcome::Log("[carjack] it is still in the air -- let it come down"))
                    }
                    Some(v) if v.jackable.is_some() && v.occupied => {
                        out.push(Outcome::Log("[carjack] somebody is in it"))
                    }
                    Some(v) => out.push(Outcome::Send("carjack", json!({"vehicleNetId":v.net_id}))),
                },
            }
        }
        if swing.applied && swing.elapsed >= swing.busy {
            self.swing = None;
        }
        (out, false)
    }
}

/// Vehicle._jackSlowFor, counted in PhysicsTick above every early-out.
#[derive(Component, Default, Clone, Copy, Debug)]
pub(crate) struct JackState {
    pub slow_for: f32,
}
/// Vehicle.JackableNow: a parked or wedged car passes however odd its pose;
/// a moving one must not be rising and must be within reach of the ground.
pub(crate) fn jackable(slow_for: f32, rising: f32, agl: impl FnOnce() -> f32) -> bool {
    if slow_for >= JACK_SETTLED_DWELL {
        return true;
    }
    if rising > JACK_RISING_SPEED {
        return false;
    }
    agl() <= JACK_GROUND_REACH
}
/// Vehicle.Carjack's impulse: retail's one-step AddForce dv on mass 2.0,
/// re-expressed for this body's real mass.
pub(crate) fn carjack_impulse(force: Vec3, torque: Vec3, mass: f32) -> (Vec3, Vec3) {
    let k = CARJACK_STEP_SECONDS / CARJACK_RETAIL_MASS * mass;
    (force * k, torque * k)
}
/// ProbeAgl: world layer only, no hit reads as open air.
fn probe_agl(context: &RapierContext, entity: Entity, from: Vec3) -> f32 {
    let filter = QueryFilter::default()
        .exclude_sensors()
        .exclude_rigid_body(entity)
        .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1))
        .predicate(&crate::player::world_solid);
    context
        .cast_ray(from, Vec3::NEG_Y, AGL_REACH, true, filter)
        .map_or(AGL_OPEN_AIR, |(_, d)| d.max(0.))
}

#[derive(Resource, Default)]
struct PendingJacks(Vec<(u32, Vec3, Vec3)>);

pub(super) fn install(app: &mut App) {
    app.init_resource::<LookedVehicle>()
        .init_resource::<HandTools>()
        .init_resource::<PendingJacks>()
        .add_systems(
            Update,
            input
                .after(super::access::VehicleAccess)
                .before(crate::entities::ApplyInteractions),
        )
        .add_systems(Update, (read_jacks, repaint).after(super::VehicleUpdate))
        .add_systems(
            FixedUpdate,
            apply_jacks
                .after(super::step_bodies)
                .before(PhysicsSet::SyncBackend),
        );
}

type TargetQuery<'w, 's> = Query<
    'w,
    's,
    (
        Entity,
        &'static VehicleBody,
        &'static Transform,
        Option<&'static Velocity>,
        Option<&'static JackState>,
        Option<&'static RigidBody>,
    ),
>;
fn target(
    (entity, body, pose, velocity, jack, rigid): (Entity, &VehicleBody, &Transform, Option<&Velocity>, Option<&JackState>, Option<&RigidBody>),
    owns: bool,
    context: Option<&RapierContext>,
    probe: bool,
) -> Target {
    let linear = velocity.map_or(Vec3::ZERO, |v| v.linear);
    let simulated = owns && rigid == Some(&RigidBody::Dynamic);
    Target {
        net_id: body.net_id,
        position: pose.translation,
        wreck: body.flags & 32 != 0,
        occupied: body.driver_player_id != 0 || !body.passengers.is_empty(),
        popped: body.popped_tire_mask,
        speed_sq: linear.length_squared(),
        jackable: (probe && simulated).then(|| {
            jackable(jack.map_or(0., |j| j.slow_for), linear.y, || {
                context.map_or(AGL_OPEN_AIR, |c| probe_agl(c, entity, pose.translation))
            })
        }),
    }
}

#[allow(clippy::too_many_arguments)]
fn input(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    mut model: ResMut<UiModel>,
    ui: Option<Res<UiState>>,
    events: Option<Res<FrameInput>>,
    settings: Option<Res<Settings>>,
    windows: Query<(Entity, &Window, &CursorOptions), With<PrimaryWindow>>,
    (looked, focus): (Res<LookedVehicle>, Option<Res<InteractionFocus>>),
    bodies: TargetQuery,
    players: Query<&Transform, With<PlayerBody>>,
    context: ReadRapierContext,
    (epoch, bridge, config, assets, time): (
        Res<crate::player::WorldSessionEpoch>,
        Res<CoreBridge>,
        Res<WorldConfig>,
        Res<AssetServer>,
        Res<Time>,
    ),
    mut tools: ResMut<HandTools>,
    mut hand_events: MessageWriter<HandEvent>,
) {
    if tools.epoch != epoch.0 || !model.connected {
        *tools = HandTools { epoch: epoch.0, ..default() };
        return;
    }
    let held = Cell::held(&snapshot.0).and_then(|cell| {
        let tool = Tool::of(catalog.items.get(&cell.id)?)?;
        Some((cell, tool))
    });
    let window = windows.single().ok();
    let alive = snapshot.0["player"]["alive"] == true;
    let seated = snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) != 0;
    // The LMB dispatch is on-foot only (driving honks, riding does nothing).
    let active = ui.as_ref().is_some_and(|u| !u.captures_input())
        && alive
        && !seated
        && window.is_some_and(|(_, w, c)| w.focused && !c.visible && c.grab_mode != CursorGrabMode::None);
    let mut press = false;
    let mut siphon = false;
    if let (true, Some(events), Some(settings), Some((window, ..))) =
        (active, events.as_ref(), settings.as_ref(), window)
    {
        for event in &events.events {
            if event.window.is_some_and(|w| w != window) {
                continue;
            }
            let down = match &event.event {
                WindowEvent::KeyboardInput(e) if !e.repeat => e.state == ButtonState::Pressed,
                WindowEvent::MouseButtonInput(e) => e.state == ButtonState::Pressed,
                _ => continue,
            };
            press |= down && settings.bindings[&GameAction::Fire].matches_window_event(&event.event);
            siphon |= down && settings.bindings[&GameAction::Aim].matches_window_event(&event.event);
        }
    }
    let owns = crate::network::owns_world(&snapshot.0);
    let physics = context.single().ok();
    let rapier = physics.as_ref();
    let jack = held.is_some_and(|(_, t)| t.kind == Kind::Carjack);
    // ResolveFocus has one winner: a nearer item or deployable clears _focusVehicle.
    let look = looked
        .0
        .filter(|_| active)
        .and_then(|e| bodies.get(e).ok())
        .filter(|row| focus.as_ref().is_none_or(|f| f.selected_net_id() == Some(u64::from(row.1.net_id))))
        .map(|row| target(row, owns, rapier, jack));
    let find = |id: u32| {
        bodies.iter().find(|r| r.1.net_id == id).map(|row| target(row, owns, rapier, false))
    };
    let paused = ui.as_ref().is_some_and(|u| crate::player::simulation_paused(u));
    let dt = if paused { 0. } else { time.delta_secs() };
    let alive_held = held.filter(|_| alive);
    let (outcomes, cancelled) = tools.step(dt, alive_held, press, look, find);
    if cancelled {
        hand_events.write(HandEvent::Cancel);
    }
    let master = settings.as_ref().map_or(1., |s| s.master_volume);
    // TryDepositFuel (LMB) / TryExtractFuel (RMB), vehicle branches: generators
    // and pumps are the deployable interaction's; this is the car focus.
    let can = Cell::held(&snapshot.0)
        .filter(|_| alive)
        .and_then(|c| Some((c, catalog.items.get(&c.id).filter(|i| i.fuel_capacity > 0.)?.fuel_capacity)));
    if let (Some((cell, capacity)), Some(v)) = (can, look)
        && let Some((_, body, ..)) = bodies.iter().find(|r| r.1.net_id == v.net_id)
    {
        let tank_max = super::data::definition(body.type_id).map_or(0., |d| d.spec.fuel);
        for pour in [true, false].into_iter().filter(|p| if *p { press } else { siphon }) {
            // The exact can's accepted fuel (the owner view; the snapshot omits it).
            let fuel = bridge.request("inventoryView", json!({})).ok().and_then(|view| {
                view["result"]["items"].as_array()?.iter().find(|i| {
                    let a = &i["address"];
                    a["page"] == cell.page && a["x"] == cell.x && a["y"] == cell.y && a["id"] == cell.id
                })?["fuel"].as_f64()
            });
            let Some(fuel) = fuel else { continue };
            match fuel_transfer(pour, fuel as f32, capacity, body.fuel, tank_max) {
                Ok(()) => {
                    let name = if pour { "pourVehicleFuel" } else { "siphonVehicleFuel" };
                    let args = json!({"vehicleNetId":v.net_id,"page":cell.page,"x":cell.x,"y":cell.y,"id":cell.id});
                    if let Err(error) = bridge.request("action", json!({"name":name,"args":args})) {
                        model.notice(error.to_string());
                    }
                    // GameAudio.Play2D(.., FuelPour(id), -5f): the can glugging, either direction.
                    let sound = fuel_sound(cell.id);
                    if config.content_root.join(sound).is_file() {
                        commands.spawn((
                            AudioPlayer::new(assets.load(sound)),
                            PlaybackSettings::DESPAWN.with_volume(Volume::Linear(db_gain(-5.) * master)),
                        ));
                    }
                }
                Err(log) if !log.is_empty() => info!("{log}"),
                Err(_) => {}
            }
        }
    }
    for outcome in outcomes {
        match outcome {
            Outcome::Swing { kind, item, at } => {
                hand_events.write(HandEvent::Start(item));
                match kind {
                    // GameAudio.Play2D(.., "misc/vehicle_spraypaint", -4f) at the start of the sweep.
                    Kind::Paint if config.content_root.join(SPRAY_SOUND).is_file() => {
                        commands.spawn((
                            AudioPlayer::new(assets.load(SPRAY_SOUND)),
                            PlaybackSettings::DESPAWN.with_volume(Volume::Linear(db_gain(-4.) * master)),
                        ));
                    }
                    Kind::Carjack => {
                        // GameAudio.PlayAt(.., "items/tools_carjack_use", at, -3f, 6f, 40f).
                        if let Some(at) = at
                            && config.content_root.join(JACK_SOUND).is_file()
                        {
                            commands.spawn((
                                AudioPlayer::new(assets.load(JACK_SOUND)),
                                PlaybackSettings::DESPAWN
                                    .with_spatial(true)
                                    .with_spatial_scale(SpatialScale::new(1. / 6.))
                                    .with_volume(Volume::Linear(db_gain(-3.) * master)),
                                Transform::from_translation(at),
                            ));
                        }
                        // SoundBus.Emit: the local zombie field, which the listen host owns.
                        if owns && let Ok(player) = players.single()
                            && let Err(error) = bridge.request(
                                "worldZombieNoise",
                                json!({"position":player.translation.to_array(),"radius":CARJACK_NOISE}),
                            )
                        {
                            warn!("Carjack noise: {error}");
                        }
                    }
                    _ => {}
                }
            }
            Outcome::Send(name, args) => {
                info!("[vehicle tool] {name} {args}");
                if let Err(error) = bridge.request("action", json!({"name":name,"args":args})) {
                    model.notice(error.to_string());
                }
            }
            Outcome::Notice(text) => model.notice(text),
            Outcome::Log(text) => info!("{text}"),
        }
    }
}
fn db_gain(db: f32) -> f32 {
    10_f32.powf(db / 20.)
}
/// The source refusals of the vehicle pour/siphon, in the source order, from
/// accepted state. Only the host's answer moves any fuel.
pub(crate) fn fuel_transfer(pour: bool, can: f32, capacity: f32, tank: f32, tank_max: f32) -> Result<(), &'static str> {
    let can = can.max(0.); // a fresh can's -1 reads as empty
    if pour {
        if can <= 0.01 {
            return Err("[fuel] can is empty");
        }
        if tank_max <= 0. {
            return Err("");
        }
        if tank_max - tank <= 0.01 {
            return Err("[fuel] that tank is full");
        }
    } else {
        if capacity - can <= 0.01 {
            return Err("[fuel] can is full");
        }
        if tank_max <= 0. {
            return Err("");
        }
        if (capacity - can).min(tank) <= 0.01 {
            return Err("[fuel] that vehicle is empty");
        }
    }
    Ok(())
}
/// GameAudio.FuelPour.
pub(crate) fn fuel_sound(item: u16) -> &'static str {
    match item {
        1440 => "audio/items/fuels_gas_large_use.wav",
        1114 => "audio/items/fuels_jerrycan_maple_use.wav",
        1115 => "audio/items/fuels_jerrycan_birch_use.wav",
        1116 => "audio/items/fuels_jerrycan_pine_use.wav",
        _ => "audio/items/fuels_gas_use.wav",
    }
}

fn read_jacks(mut events: MessageReader<CoreEvent>, mut pending: ResMut<PendingJacks>) {
    for event in events.read().filter(|e| e.kind == "carjack") {
        let vector = |key: &str| {
            serde_json::from_value::<[f32; 3]>(event.data[key].clone()).ok().map(Vec3::from_array)
        };
        if let (Some(id), Some(force), Some(torque)) =
            (event.data["netId"].as_u64(), vector("force"), vector("torque"))
        {
            pending.0.push((id as u32, force, torque));
        }
    }
}

/// VehicleNetSync.ApplyCarjackForce -> Vehicle.Carjack on the physics owner.
#[allow(clippy::type_complexity)]
fn apply_jacks(
    mut commands: Commands,
    ui: Option<Res<UiState>>,
    context: ReadRapierContext,
    mut pending: ResMut<PendingJacks>,
    mut bodies: Query<(
        Entity,
        &VehicleBody,
        &Transform,
        &Velocity,
        &RigidBody,
        &mut ExternalImpulse,
        Option<&mut JackState>,
    )>,
) {
    if ui.as_ref().is_some_and(|u| crate::player::simulation_paused(u)) {
        return;
    }
    for (entity, _, _, velocity, _, _, jack) in &mut bodies {
        let slow = velocity.linear.length_squared() <= JACK_SETTLED_SPEED * JACK_SETTLED_SPEED;
        match jack {
            Some(mut jack) => jack.slow_for = if slow { jack.slow_for + 0.02 } else { 0. },
            None => {
                commands.entity(entity).insert(JackState::default());
            }
        }
    }
    let rapier = context.single().ok();
    for (id, force, torque) in pending.0.drain(..) {
        let Some((entity, body, pose, velocity, rigid, mut impulse, jack)) =
            bodies.iter_mut().find(|r| r.1.net_id == id)
        else {
            continue;
        };
        // Only the body this process simulates can be shoved; everyone else
        // sees the launch in the ordinary transform stream.
        if *rigid != RigidBody::Dynamic
            || body.flags & 32 != 0
            || body.driver_player_id != 0
            || !body.passengers.is_empty()
            || !jackable(jack.map_or(0., |j| j.slow_for), velocity.linear.y, || {
                rapier.as_ref().map_or(AGL_OPEN_AIR, |c| probe_agl(c, entity, pose.translation))
            })
        {
            continue;
        }
        let Some(d) = super::data::definition(body.type_id) else {
            continue;
        };
        let (linear, angular) = carjack_impulse(force, torque, d.spec.mass());
        impulse.impulse += linear;
        impulse.torque_impulse += angular;
    }
}

/// VehiclePuppet.ApplyReplicatedPaint / Vehicle.SetPaint: only the paintable
/// palette texels change, so a livery keeps its fixed colours.
#[allow(clippy::too_many_arguments)]
fn repaint(
    mut commands: Commands,
    config: Res<WorldConfig>,
    mut cache: ResMut<ContentCache>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
    mut bodies: Query<(Entity, &VehicleBody, &mut BodyPaint)>,
    children: Query<&Children>,
    parts: Query<&MeshMaterial3d<StandardMaterial>>,
) {
    for (entity, body, mut paint) in &mut bodies {
        // A wreck is charred over (damage.rs); the source never sprays one.
        if paint.rgb == body.paint_rgb || body.flags & 32 != 0 {
            continue;
        }
        let Some(d) = super::data::definition(body.type_id) else {
            continue;
        };
        let (colour, key) = match body.paint_rgb {
            Some(rgb) => (paint_colour(rgb), format!("vehicle:{}:rgb:{rgb:06x}", d.key)),
            None => (d.paints[body.variant as usize], format!("vehicle:{}:{}", d.key, body.variant)),
        };
        let material =
            body_material(d, colour, &key, &config.content_root, &mut cache, &mut materials, &mut images);
        let mut swapped = 0;
        for part in children.iter_descendants(entity) {
            if parts.get(part).is_ok_and(|m| m.0 == paint.material) {
                commands.entity(part).insert(MeshMaterial3d(material.clone()));
                swapped += 1;
            }
        }
        // Children spawned this frame are not in the hierarchy yet: retry.
        if swapped > 0 {
            paint.material = material;
            paint.rgb = body.paint_rgb;
        }
    }
}
/// 0xRRGGBB, as VehiclePuppet.ApplyReplicatedPaint unpacks it (sRGB bytes).
pub(crate) fn paint_colour(rgb: u32) -> [f32; 4] {
    [
        ((rgb >> 16) & 255) as f32 / 255.,
        ((rgb >> 8) & 255) as f32 / 255.,
        (rgb & 255) as f32 / 255.,
        1.,
    ]
}

#[cfg(test)]
mod tests;
