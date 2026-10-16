//! Source restraints: handcuffs, the cable tie and the handcuffs key
//! (PlayerController.EquipHeldRestraint, TryRestraintUse, TickArrest and
//! RestraintUseSeconds; RemotePlayers.AimedPlayer and GestureOf). The client
//! only picks the player it is aimed at, gives the source reason when it can
//! already tell, plays the restraint's Use clip and names the target: commands
//! 52/53 are validated and applied by core (`ServerTransactions`, `ArrestSim`),
//! and the cuffs arrive on everyone's screen as the replicated ARREST_START.
//! The struggle (command 54) is the lean edge in `player::lean`; the cuffed
//! player's bound hands are the bridge's equip gate.
use crate::{
    avatar::remotes::RemotePlayer,
    bridge::CoreBridge,
    entities::{ItemCatalog, ItemVisual},
    player::{GameSnapshot, PlayerCamera, WorldSessionEpoch},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState, input_order::FrameInput},
    use_items::{Cell, HandEvent},
};
use bevy::{
    input::ButtonState,
    prelude::*,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow, WindowEvent},
};
use serde_json::{Value, json};

/// PlayerController.ArrestAimRange: retail UseableArrestStart raycasts 3 m;
/// the server's own reach is the looser 7 m.
pub(crate) const AIM_RANGE: f32 = 3.5;
/// RemotePlayers.AimedPlayer: "~60 degrees off centre, no further".
const AIM_MIN_DOT: f32 = 0.5;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Kind {
    /// Handcuffs or the cable tie (Arrest_Start): LMB a SURRENDERING player.
    Restraint,
    /// The handcuffs key (Arrest_End): LMB a CUFFED player.
    Key,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Held {
    pub cell: Cell,
    pub kind: Kind,
    /// RestraintUseSeconds: the Use clip, else 1.300 / 1.633 (Catalog.HandTool).
    pub use_seconds: f32,
}
impl Held {
    pub(crate) fn of(cell: Cell, item: &ItemVisual) -> Option<Self> {
        let tool = &item.hand_tool;
        Some(Self {
            cell,
            kind: match tool["kind"].as_str()? {
                "restraint" => Kind::Restraint,
                "restraintKey" => Kind::Key,
                _ => return None,
            },
            use_seconds: tool["useSeconds"].as_f64()? as f32,
        })
    }
}

/// RemotePlayers.AimedPlayer: the puppet best in front of `from` (flattened
/// direction, dot above 0.5) within `max` of it. Not a ray: a body behind a
/// wall still counts, and the server re-checks only its 7 m reach.
pub(crate) fn aimed_player(from: Vec3, forward: Vec3, max: f32, players: impl IntoIterator<Item = (u16, Vec3)>) -> u16 {
    let flat = Vec3::new(forward.x, 0., forward.z).normalize_or_zero();
    let (mut best, mut best_dot) = (0, AIM_MIN_DOT);
    for (id, position) in players {
        let mut d = position - from;
        if d.length_squared() > max * max {
            continue;
        }
        d.y = 0.;
        if d.length_squared() < 1e-4 {
            continue;
        }
        let dot = d.normalize().dot(flat);
        if dot > best_dot {
            best_dot = dot;
            best = id;
        }
    }
    best
}

/// The two gestures that gate the use (GestureRules: SURRENDER_START, ARREST_START).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Gestures {
    pub surrender: u8,
    pub arrest: u8,
}
impl Gestures {
    pub(crate) fn of(catalog: &[crate::avatar::gestures::Definition]) -> Option<Self> {
        let id = |name: &str| catalog.iter().find(|d| d.name == name).map(|d| d.id);
        Some(Self { surrender: id("surrender_start")?, arrest: id("arrest_start")? })
    }
}

#[derive(Debug, PartialEq)]
pub(crate) enum Outcome {
    /// Viewmodel.PlayConsumeUse: the held restraint's Cuff_Use / Key_Use.
    Use(u16),
    Send(&'static str, Value),
    /// HUD.Alert.
    Notice(&'static str),
}

#[derive(Resource, Default)]
pub(crate) struct Restraints {
    /// `_arrestPendingTarget`, `_arrestPendingT`.
    pending: Option<(u16, f32)>,
    epoch: u64,
    /// Every intent handed to the bridge, newest last (diagnostics and tests).
    pub(crate) sent: Vec<(String, Value)>,
}
impl Restraints {
    pub(crate) fn pending(&self) -> bool {
        self.pending.is_some()
    }
    /// One frame. `aim` is RemotePlayers.AimedPlayer from the camera, `gesture`
    /// RemotePlayers.GestureOf (the replicated gesture of that player).
    pub(crate) fn step(
        &mut self,
        dt: f32,
        held: Option<Held>,
        press: bool,
        gestures: Gestures,
        aim: impl FnOnce() -> u16,
        gesture: impl Fn(u16) -> u8,
    ) -> Vec<Outcome> {
        let mut out = Vec::new();
        // TickArrest: the cuffs go on when the swing finishes, if a restraint
        // (of either end) is still in the hand. Nothing else is re-checked here;
        // the server re-checks the surrender, the reach and the bag.
        if let Some((target, remaining)) = &mut self.pending {
            *remaining -= dt;
            if *remaining <= 0. {
                let target = *target;
                self.pending = None;
                if target != 0 && held.is_some() {
                    out.push(Outcome::Send("arrestPlayer", json!({"targetPlayerId":target})));
                }
            }
        }
        // TryRestraintUse: one attempt at a time (source: equipment.isBusy).
        let Some(held) = held.filter(|_| press && self.pending.is_none()) else {
            return out;
        };
        let target = aim();
        let key = held.kind == Kind::Key;
        if target == 0 {
            out.push(Outcome::Notice(if key { "Aim at someone who's cuffed." } else { "Aim at someone surrendering." }));
            return out;
        }
        let theirs = gesture(target);
        if key {
            if theirs != gestures.arrest {
                out.push(Outcome::Notice("They aren't cuffed."));
                return out;
            }
            // Key_Use plays, and the unlock is asked for at once.
            out.push(Outcome::Use(held.cell.id));
            out.push(Outcome::Send("unlockArrest", json!({"targetPlayerId":target})));
            return out;
        }
        if theirs != gestures.surrender {
            out.push(Outcome::Notice("They have to surrender first."));
            return out;
        }
        // UseableArrestStart arrests in simulate() once isUseable: at the end of Cuff_Use.
        self.pending = Some((target, held.use_seconds));
        out.push(Outcome::Use(held.cell.id));
        out
    }
}

pub(crate) struct RestraintsPlugin;
impl Plugin for RestraintsPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Restraints>().add_systems(Update, input);
    }
}

fn press(
    active: bool,
    events: Option<&FrameInput>,
    settings: Option<&Settings>,
    window: Option<Entity>,
) -> bool {
    let (true, Some(events), Some(settings), Some(window)) = (active, events, settings, window) else {
        return false;
    };
    events.events.iter().any(|event| {
        event.window.is_none_or(|w| w == window)
            && match &event.event {
                WindowEvent::KeyboardInput(e) => !e.repeat && e.state == ButtonState::Pressed,
                WindowEvent::MouseButtonInput(e) => e.state == ButtonState::Pressed,
                _ => false,
            }
            && settings.bindings[&GameAction::Fire].matches_window_event(&event.event)
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
    remotes: Query<(&RemotePlayer, &GlobalTransform)>,
    (epoch, bridge, time): (Res<WorldSessionEpoch>, Option<Res<CoreBridge>>, Res<Time>),
    mut restraints: ResMut<Restraints>,
    mut hand_events: MessageWriter<HandEvent>,
) {
    if restraints.epoch != epoch.0 || !model.connected {
        *restraints = Restraints { epoch: epoch.0, ..default() };
        return;
    }
    let player = &snapshot.0["player"];
    let alive = player["alive"] == true;
    let held = Cell::held(&snapshot.0)
        .filter(|_| alive)
        .and_then(|cell| Held::of(cell, catalog.items.get(&cell.id)?));
    let window = windows.single().ok();
    // The LMB dispatch is on-foot only (driving honks, riding does nothing).
    let active = ui.as_ref().is_some_and(|u| !u.captures_input())
        && alive
        && player["vehicleNetId"].as_u64().unwrap_or(0) == 0
        && window.is_some_and(|(_, w, c)| w.focused && !c.visible && c.grab_mode != CursorGrabMode::None);
    let press = press(active, events.as_deref(), settings.as_deref(), window.map(|w| w.0));
    let paused = ui.as_ref().is_some_and(|u| crate::player::simulation_paused(u));
    let dt = if paused { 0. } else { time.delta_secs() };
    let Some(gestures) = Gestures::of(&catalog.gestures) else {
        return;
    };
    let rows = snapshot.0["players"].as_array();
    let aim = || {
        let Ok(eye) = camera.single() else { return 0 };
        let players = remotes.iter().map(|(p, t)| (p.id, t.translation()));
        aimed_player(eye.translation(), *eye.forward(), AIM_RANGE, players)
    };
    let gesture = |id: u16| {
        rows.into_iter()
            .flatten()
            .find(|r| r["playerId"].as_u64() == Some(u64::from(id)))
            .and_then(|r| r["gesture"].as_u64())
            .unwrap_or(0) as u8
    };
    for outcome in restraints.step(dt, held, press, gestures, aim, gesture) {
        match outcome {
            Outcome::Use(item) => {
                hand_events.write(HandEvent::Start(item));
            }
            Outcome::Send(name, args) => {
                info!("[arrest] {name} {args}");
                if let Some(bridge) = &bridge
                    && let Err(error) = bridge.request("action", json!({"name":name,"args":args.clone()}))
                {
                    model.notice(error.to_string());
                }
                restraints.sent.push((name.into(), args));
                if restraints.sent.len() > 16 {
                    restraints.sent.remove(0);
                }
            }
            Outcome::Notice(text) => model.notice(text),
        }
    }
}

#[cfg(test)]
mod tests;
