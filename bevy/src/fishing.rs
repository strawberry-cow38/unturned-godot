//! Source fishing rod (PlayerController.EquipHeldFisher, FisherPrimary, FisherRelease, TickFishing,
//! SpawnBobber, UpdateFishLine, BobberOverFishableWater, ClearFisher; FishingSim). The hold is the
//! EmptyHands viewmodel. The casting client keeps what the source shows: the 50 Hz strength gauge
//! (no HUD in the source), the red bobber's flight, the fishable-water test, the bob on the swell,
//! the tug in the catch window and the line from the rod tip. The authority owns the line itself
//! (v80, `bridge/FishingServices.cs`): the bobber's landing is sent as command 103, every LMB press
//! and release on a line as command 104, and Event78 says where the server's line is and which fish
//! it landed. The fish arrives through the replicated bag; nothing is granted locally.
use crate::{
    bridge::CoreBridge,
    entities::{CoreEvent, ItemCatalog},
    player::{GameSnapshot, PlayerCamera, WorldSessionEpoch},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState, input_order::FrameInput},
    use_items::Cell,
    world::WorldState,
};
use bevy::{
    asset::RenderAssetUsages,
    input::ButtonState,
    mesh::PrimitiveTopology,
    prelude::*,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow, WindowEvent},
};
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};

/// FishingSim's catch window: it opens WARNING_DURATION after the bite and stays CATCH_WINDOW.
const WARNING: f32 = 1.0;
const CATCH_WINDOW: f32 = 1.4;
/// Terrain.MinFishDepth (retail minimumDepth).
pub(crate) const MIN_FISH_DEPTH: f32 = 4.;
/// TickFishing: gravity on the bobber until it splashes down.
const BOBBER_GRAVITY: f32 = 20.;
/// The server's FishingStateEvent states.
const IDLE: u8 = 0;
const LINE: u8 = 1;
const BITE: u8 = 2;
const CHALLENGE: u8 = 3;

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) enum Phase {
    Idle,
    /// FishingSim.PreparingToCast: LMB held, the gauge oscillating.
    Charging,
    /// Casting: the bobber in flight.
    Flying { velocity: Vec3 },
    /// Landed and sent; the server's answer is on its way.
    Cast,
    /// LineDeployed on the server; `bite` is the time since its bite notification.
    Line { bite: Option<f32> },
    /// CatchChallenge: the fish is fighting on the line.
    Challenge,
}

#[derive(Resource)]
pub(crate) struct Fishing {
    pub(crate) phase: Phase,
    held: Option<Cell>,
    strength_time: u32,
    pub(crate) strength: f32,
    tock: f32,
    pub(crate) bobber: Vec3,
    clock: f32,
    epoch: u64,
    last_event: u64,
    /// Every intent handed to the bridge, newest last (diagnostics and tests).
    pub(crate) sent: Vec<(String, Value)>,
}
impl Default for Fishing {
    fn default() -> Self {
        Self {
            phase: Phase::Idle,
            held: None,
            strength_time: 0,
            strength: 0.,
            tock: 0.,
            bobber: Vec3::ZERO,
            clock: 0.,
            epoch: 0,
            last_event: 0,
            sent: Vec::new(),
        }
    }
}
/// FishingSim.Tock's gauge: the period grows with the FISHING skill (100 + level * 20 tocks).
pub(crate) fn gauge(strength_time: u32, skill: u8) -> f32 {
    let period = 100 + u32::from(skill) * 20;
    let m = 1. - ((((strength_time + period / 2) % period) as f32 / period as f32) * std::f32::consts::PI).sin().abs();
    m * m
}
/// What one frame asks of the bridge.
#[derive(Debug, PartialEq)]
pub(crate) enum Send {
    Cast { bobber: Vec3, strength: f32 },
    /// FishingInputCommand: 0 press, 1 release, 2 the rod put away.
    Input(u8),
}
pub(crate) struct Frame<'a> {
    pub dt: f32,
    pub held: Option<Cell>,
    pub skill: u8,
    pub press: bool,
    pub release: bool,
    /// The camera's eye and look direction (SpawnBobber).
    pub eye: (Vec3, Vec3),
    pub sea: Option<f32>,
    /// BobberOverFishableWater: at least MIN_FISH_DEPTH of water under this surface point.
    pub fishable: &'a dyn Fn(Vec3) -> bool,
}
impl Fishing {
    fn line_out(&self) -> bool {
        matches!(self.phase, Phase::Cast | Phase::Line { .. } | Phase::Challenge)
    }
    pub(crate) fn bite_window_open(&self) -> bool {
        matches!(self.phase, Phase::Line { bite: Some(t) } if (WARNING..=WARNING + CATCH_WINDOW).contains(&t))
    }
    /// The server's line moved (Event78).
    pub(crate) fn adopt(&mut self, state: u8) {
        if self.held.is_none() {
            return;
        }
        self.phase = match state {
            LINE => Phase::Line { bite: None },
            BITE => Phase::Line { bite: Some(0.) },
            CHALLENGE => Phase::Challenge,
            _ => Phase::Idle,
        };
    }
    pub(crate) fn step(&mut self, frame: Frame) -> Vec<Send> {
        let mut out = Vec::new();
        self.clock += frame.dt;
        // ClearFisher: switching items ends the cast; a line in the water is reeled in on the server.
        if frame.held != self.held {
            if self.line_out() {
                out.push(Send::Input(2));
            }
            self.held = frame.held;
            self.phase = Phase::Idle;
        }
        if self.held.is_none() {
            return out;
        }
        // FisherPrimary / FisherRelease.
        if frame.press {
            match self.phase {
                Phase::Idle => {
                    self.phase = Phase::Charging;
                    self.strength_time = 0;
                    self.strength = 0.;
                    self.tock = 0.;
                }
                Phase::Cast | Phase::Line { .. } | Phase::Challenge => out.push(Send::Input(0)),
                _ => {}
            }
        }
        if frame.release {
            match self.phase {
                // SpawnBobber at the release: 0.6 m out along the aim, Lerp(12, 26, strength) forward and 3 up.
                Phase::Charging => {
                    let (eye, forward) = frame.eye;
                    self.bobber = eye + forward * 0.6;
                    let speed = 12. + (26. - 12.) * self.strength;
                    self.phase = Phase::Flying { velocity: forward * speed + Vec3::Y * 3. };
                }
                Phase::Challenge => out.push(Send::Input(1)),
                _ => {}
            }
        }
        match &mut self.phase {
            Phase::Charging => {
                // The 50 Hz gauge tock, framerate independent.
                self.tock += frame.dt;
                while self.tock >= 0.02 {
                    self.tock -= 0.02;
                    self.strength_time += 1;
                    self.strength = gauge(self.strength_time, frame.skill);
                }
            }
            Phase::Flying { velocity } => {
                velocity.y -= BOBBER_GRAVITY * frame.dt;
                self.bobber += *velocity * frame.dt;
                match frame.sea {
                    Some(sea) if self.bobber.y < sea => {
                        if (frame.fishable)(Vec3::new(self.bobber.x, sea, self.bobber.z)) {
                            self.bobber.y = sea;
                            self.phase = Phase::Cast;
                            out.push(Send::Cast { bobber: self.bobber, strength: self.strength });
                        } else {
                            self.phase = Phase::Idle; // dry land or too shallow: no cast
                        }
                    }
                    // Fell into a dry gap or off the map.
                    _ if self.bobber.y < frame.sea.unwrap_or(frame.eye.0.y) - 60. => self.phase = Phase::Idle,
                    _ => {}
                }
            }
            Phase::Line { bite: Some(t) } => *t += frame.dt,
            _ => {}
        }
        out
    }
    /// The bobber's height over the swell: a gentle bob, pulled under in the catch window and
    /// held under while the fish fights.
    pub(crate) fn bobber_height(&self, sea: f32) -> f32 {
        match self.phase {
            Phase::Challenge => sea - 0.5,
            Phase::Line { .. } if self.bite_window_open() => sea - 0.35,
            Phase::Line { .. } | Phase::Cast => sea + (self.clock * 4.).sin() * 0.06,
            _ => self.bobber.y,
        }
    }
    pub(crate) fn bobber_visible(&self) -> bool {
        matches!(self.phase, Phase::Flying { .. } | Phase::Cast | Phase::Line { .. } | Phase::Challenge)
    }
}

#[derive(Component)]
pub(crate) struct Bobber;
#[derive(Component)]
pub(crate) struct FishLine;

pub(crate) struct FishingPlugin;
impl Plugin for FishingPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Fishing>().add_systems(Update, (input, present).chain());
    }
}

/// The held rod, if the snapshot's living hand holds a FISHER.
pub(crate) fn held_rod(snapshot: &Value, catalog: &ItemCatalog) -> Option<Cell> {
    Cell::held(snapshot).filter(|cell| {
        snapshot["player"]["alive"] != false
            && snapshot["player"]["vehicleNetId"].as_u64().unwrap_or(0) == 0
            && catalog.items.get(&cell.id).is_some_and(|i| i.hand_kind == "fisher")
    })
}

#[allow(clippy::too_many_arguments)]
fn input(
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    mut model: ResMut<UiModel>,
    ui: Option<Res<UiState>>,
    events: Option<Res<FrameInput>>,
    settings: Option<Res<Settings>>,
    windows: Query<(Entity, &Window, &CursorOptions), With<PrimaryWindow>>,
    camera: Query<&GlobalTransform, With<PlayerCamera>>,
    world: Option<Res<WorldState>>,
    context: ReadRapierContext,
    mut core_events: MessageReader<CoreEvent>,
    (epoch, bridge, time): (Res<WorldSessionEpoch>, Option<Res<CoreBridge>>, Res<Time>),
    mut fishing: ResMut<Fishing>,
) {
    if fishing.epoch != epoch.0 || !model.connected {
        *fishing = Fishing { epoch: epoch.0, ..default() };
        return;
    }
    for event in core_events.read().filter(|e| e.kind == "fishingState") {
        if event.sequence <= fishing.last_event {
            continue;
        }
        fishing.last_event = event.sequence;
        let caught = event.data["caughtItemId"].as_u64().unwrap_or(0);
        if caught != 0 {
            // GrantFish's Log.Print; the fish itself arrives in the replicated bag.
            info!("[fishing] caught #{caught}");
        }
        fishing.adopt(event.data["state"].as_u64().unwrap_or(0) as u8);
    }
    let window = windows.single().ok();
    let active = ui.as_ref().is_some_and(|u| !u.captures_input())
        && window.is_some_and(|(_, w, c)| w.focused && !c.visible && c.grab_mode != CursorGrabMode::None);
    let (mut press, mut release) = (false, false);
    if let (true, Some(events), Some(settings), Some((window, ..))) = (active, events.as_ref(), settings.as_ref(), window) {
        for event in &events.events {
            if event.window.is_some_and(|w| w != window)
                || !settings.bindings[&GameAction::Fire].matches_window_event(&event.event)
            {
                continue;
            }
            match &event.event {
                WindowEvent::MouseButtonInput(e) => {
                    press |= e.state == ButtonState::Pressed;
                    release |= e.state == ButtonState::Released;
                }
                WindowEvent::KeyboardInput(e) if !e.repeat => {
                    press |= e.state == ButtonState::Pressed;
                    release |= e.state == ButtonState::Released;
                }
                _ => {}
            }
        }
    }
    let paused = ui.as_ref().is_some_and(|u| crate::player::simulation_paused(u));
    let eye = camera.single().map_or((Vec3::ZERO, Vec3::NEG_Z), |c| (c.translation(), *c.forward()));
    let physics = context.single().ok();
    // BobberOverFishableWater: the floor at least MIN_FISH_DEPTH under the surface (no world
    // collision here trusts the plane, as the source does without a terrain).
    let fishable = |surface: Vec3| {
        physics.as_ref().is_none_or(|c| {
            let filter = QueryFilter::default()
                .exclude_sensors()
                .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1))
                .predicate(&crate::player::world_solid);
            c.cast_ray(surface, Vec3::NEG_Y, MIN_FISH_DEPTH, true, filter).is_none()
        })
    };
    let skill = snapshot.0["skills"]["groups"][2][4]["level"].as_u64().unwrap_or(0) as u8;
    let sends = fishing.step(Frame {
        dt: if paused { 0. } else { time.delta_secs() },
        held: held_rod(&snapshot.0, &catalog),
        skill,
        press,
        release,
        eye,
        sea: world.as_ref().and_then(|w| w.sea_level),
        fishable: &fishable,
    });
    for send in sends {
        let (name, args) = match send {
            Send::Cast { bobber, strength } => ("fishingCast", json!({"bobber":bobber.to_array(),"strength":strength})),
            Send::Input(kind) => ("fishingInput", json!({"kind":kind})),
        };
        info!("[fishing] {name} {args}");
        if let Some(bridge) = &bridge
            && let Err(error) = bridge.request("action", json!({"name":name,"args":args.clone()}))
        {
            model.notice(error.to_string());
        }
        fishing.sent.push((name.into(), args));
        if fishing.sent.len() > 32 {
            fishing.sent.remove(0);
        }
    }
}

/// SpawnBobber's red sphere and UpdateFishLine's unshaded line from the rod tip.
#[allow(clippy::too_many_arguments)]
fn present(
    mut commands: Commands,
    fishing: Res<Fishing>,
    world: Option<Res<WorldState>>,
    camera: Query<&GlobalTransform, With<PlayerCamera>>,
    mut bobbers: Query<(Entity, &mut Transform), (With<Bobber>, Without<FishLine>)>,
    lines: Query<(Entity, &Mesh3d), With<FishLine>>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    if !fishing.bobber_visible() {
        for (entity, _) in &bobbers {
            commands.entity(entity).despawn();
        }
        for (entity, _) in &lines {
            commands.entity(entity).despawn();
        }
        return;
    }
    let sea = world.as_ref().and_then(|w| w.sea_level).unwrap_or(fishing.bobber.y);
    let at = Vec3::new(fishing.bobber.x, fishing.bobber_height(sea), fishing.bobber.z);
    match bobbers.iter_mut().next() {
        Some((_, mut transform)) => transform.translation = at,
        None => {
            commands.spawn((
                Name::new("Fishing bobber"),
                Bobber,
                Mesh3d(meshes.add(Sphere::new(0.08))),
                MeshMaterial3d(materials.add(Color::srgb(0.9, 0.2, 0.15))),
                Transform::from_translation(at),
            ));
        }
    }
    let Ok(eye) = camera.single() else { return };
    // "rod tip ~ from the hand/camera; good enough without the rod mesh"
    let tip = eye.translation() + *eye.forward() * 0.5 + *eye.right() * 0.25 - *eye.up() * 0.2;
    let mut mesh = Mesh::new(PrimitiveTopology::LineList, RenderAssetUsages::default());
    mesh.insert_attribute(Mesh::ATTRIBUTE_POSITION, vec![tip.to_array(), at.to_array()]);
    match lines.iter().next() {
        Some((_, handle)) => {
            if let Some(mut existing) = meshes.get_mut(&handle.0) {
                *existing = mesh;
            }
        }
        None => {
            commands.spawn((
                Name::new("Fishing line"),
                FishLine,
                Mesh3d(meshes.add(mesh)),
                MeshMaterial3d(materials.add(StandardMaterial {
                    base_color: Color::srgb(0.85, 0.85, 0.8),
                    unlit: true,
                    ..default()
                })),
                Transform::IDENTITY,
            ));
        }
    }
}

#[cfg(test)]
mod tests;
