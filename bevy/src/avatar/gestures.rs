//! Source GestureRules metadata plus accepted upper-body presentation. Requests
//! never start an animation locally; only replica changes and server facts do.
use super::{LocalAvatar, held};
use crate::{
    entities::{CoreEvent, ItemCatalog},
    player::{GameSnapshot, WorldSessionEpoch, simulation_paused},
    rig::{RigData, RigPlayback},
    settings::{GameAction, Settings},
    ui::{UiIntent, UiModel, UiState},
};
use bevy::prelude::*;
use serde::Deserialize;
use serde_json::json;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Definition {
    pub id: u8,
    pub name: String,
    #[serde(default)]
    pub clip: Option<String>,
    pub loops: bool,
    pub stops: u8,
    pub requestable: bool,
    pub broadcast: bool,
    #[serde(default)]
    pub required_stance: Option<String>,
}
fn definition(catalog: &[Definition], id: u8) -> Option<&Definition> {
    catalog.iter().find(|d| d.id == id)
}
#[derive(Clone, Debug)]
struct Active {
    clip: String,
    time: f32,
    looping: bool,
}
/// Shared by local and remote bodies. Loop state and one-shot facts are separate:
/// a wave never writes a new authoritative gesture state.
#[derive(Component, Default, Debug)]
pub struct Playback {
    accepted: Option<u8>,
    active: Option<Active>,
}
impl Playback {
    pub fn adopt(&mut self, accepted: u8, definitions: &[Definition], rig: &RigData) {
        if self.accepted == Some(accepted) {
            return;
        }
        self.accepted = Some(accepted);
        if accepted == 0 {
            self.active = None;
        } else {
            self.play(accepted, definitions, rig);
        }
    }
    pub fn play(&mut self, gesture: u8, definitions: &[Definition], rig: &RigData) {
        let Some(def) = definition(definitions, gesture) else {
            return;
        };
        let Some(name) = &def.clip else {
            // Source ApplyGesture does not synthesize a T-pose for null clips.
            // Preserve that boundary instead of inventing an animation.
            if !self
                .accepted
                .is_some_and(|id| definition(definitions, id).is_some_and(|d| d.loops))
            {
                self.active = None;
            }
            return;
        };
        let Some(clip) = rig.anims.get(name).filter(|c| c.length > 0.) else {
            warn!("Gesture {} has no source rig clip {name}", def.name);
            return;
        };
        let _ = clip;
        self.active = Some(Active {
            clip: name.clone(),
            time: 0.,
            looping: def.loops,
        });
    }
    pub fn advance(&mut self, dt: f32, definitions: &[Definition], rig: &RigData) {
        let Some(active) = &mut self.active else {
            return;
        };
        let Some(clip) = rig.anims.get(&active.clip).filter(|c| c.length > 0.) else {
            self.active = None;
            return;
        };
        active.time += dt;
        if active.looping {
            active.time = active.time.rem_euclid(clip.length);
        } else if active.time >= clip.length {
            // Exact TickGesture edge: a one-shot over an existing loop leaves
            // its terminal overlay, rather than silently changing server state.
            if self
                .accepted
                .is_some_and(|id| definition(definitions, id).is_some_and(|d| d.loops))
            {
                active.time = clip.length;
            } else {
                self.active = None;
            }
        }
    }
    pub fn apply(
        &self,
        rig: &RigPlayback,
        preserve_spine: bool,
        transforms: &mut Query<&mut Transform>,
    ) {
        let Some(active) = &self.active else { return };
        let Some(clip) = rig.data.anims.get(&active.clip) else {
            return;
        };
        held::apply_upper_body(rig, clip, active.time, preserve_spine, transforms);
    }
    pub fn clip(&self) -> Option<&str> {
        self.active.as_ref().map(|a| a.clip.as_str())
    }
}
#[derive(Resource, Default)]
pub(super) struct Local {
    playback: Playback,
    epoch: u64,
    last_event: u64,
}
fn selected(action: GameAction, current: u8, catalog: &[Definition]) -> Option<u8> {
    let (name, toggles) = match action {
        GameAction::Surrender => ("surrender_start", true),
        GameAction::GestureWave => ("wave", false),
        GameAction::GestureSalute => ("salute", false),
        GameAction::GesturePoint => ("point", false),
        GameAction::GestureFacepalm => ("facepalm", false),
        GameAction::GestureRest => ("rest_start", true),
        _ => return None,
    };
    let def = catalog.iter().find(|d| d.name == name)?;
    Some(if toggles && current == def.id {
        def.stops
    } else {
        def.id
    })
}
pub(super) fn controls(
    ui: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    mut intents: MessageWriter<UiIntent>,
) {
    if ui.captures_input() || !model.connected || !model.alive {
        return;
    }
    let current = snapshot.0["player"]["gesture"].as_u64().unwrap_or(0) as u8;
    for action in [
        GameAction::Surrender,
        GameAction::GestureWave,
        GameAction::GestureSalute,
        GameAction::GesturePoint,
        GameAction::GestureFacepalm,
        GameAction::GestureRest,
    ] {
        if settings.just_pressed(action, &keys, &mouse) {
            if let Some(id) = selected(action, current, &catalog.gestures) {
                intents.write(UiIntent::Action {
                    name: "requestGesture".into(),
                    args: json!({"gesture":id}),
                });
            }
            break;
        }
    }
}
/// Source DevConsole selector, including current-loop-specific stop. None means
/// this is another command; Some(None) has printed a local listing/refusal.
pub(crate) fn console(
    text: &str,
    current: u8,
    catalog: &[Definition],
    model: &mut UiModel,
) -> Option<Option<u8>> {
    let mut parts = text.trim().splitn(2, char::is_whitespace);
    if !parts
        .next()
        .is_some_and(|v| v.eq_ignore_ascii_case("gesture"))
    {
        return None;
    }
    let argument = parts.next().unwrap_or("").trim().to_ascii_lowercase();
    let current_name = definition(catalog, current).map_or("none", |d| d.name.as_str());
    if argument.is_empty() || argument == "?" {
        model.notice("gesture wave | salute | point | facepalm | surrender | rest | tpose | inventory | stop");
        model.notice(format!("currently: {current_name}"));
        return Some(None);
    }
    if argument == "stop" {
        let stop = definition(catalog, current).map_or(0, |d| d.stops);
        if stop == 0 {
            model.notice(format!("nothing to stop ({current_name})"));
            return Some(None);
        }
        return Some(Some(stop));
    }
    let name = match argument.as_str() {
        "surrender" => "surrender_start",
        "rest" => "rest_start",
        "tpose" => "t_pose_start",
        "inventory" => "inventory_start",
        "wave" | "salute" | "point" | "facepalm" => argument.as_str(),
        _ => {
            model.notice(format!(
                "no gesture '{argument}' -- try `gesture` for the list"
            ));
            return Some(None);
        }
    };
    let id = catalog.iter().find(|d| d.name == name).map(|d| d.id);
    if id.is_none() {
        model.notice("Gesture metadata is unavailable from this core runtime");
    }
    Some(id)
}
pub(super) fn receive(
    time: Res<Time>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    epoch: Res<WorldSessionEpoch>,
    mut state: ResMut<Local>,
    mut events: MessageReader<CoreEvent>,
    rigs: Query<&RigPlayback, With<LocalAvatar>>,
) {
    if state.epoch != epoch.0 {
        *state = Local {
            epoch: epoch.0,
            ..default()
        };
    }
    if !model.connected || !model.alive {
        state.playback = default();
        events.clear();
        return;
    }
    let Ok(rig) = rigs.single() else {
        events.clear();
        return;
    };
    let owner = snapshot.0["playerId"].as_u64().unwrap_or(0);
    let accepted = snapshot.0["player"]["gesture"].as_u64().unwrap_or(0) as u8;
    state.playback.adopt(accepted, &catalog.gestures, &rig.data);
    for event in events.read() {
        if event.sequence <= state.last_event {
            continue;
        }
        state.last_event = event.sequence;
        if event.kind == "gesture" && event.data["playerId"].as_u64() == Some(owner) {
            let id = event.data["gesture"].as_u64().unwrap_or(0) as u8;
            if definition(&catalog.gestures, id).is_some_and(|d| !d.loops && d.clip.is_some()) {
                state.playback.play(id, &catalog.gestures, &rig.data);
            }
        }
    }
    if !simulation_paused(&ui) {
        state
            .playback
            .advance(time.delta_secs(), &catalog.gestures, &rig.data);
    }
}
pub(super) fn animate(
    state: Res<Local>,
    snapshot: Res<GameSnapshot>,
    rigs: Query<&RigPlayback, With<LocalAvatar>>,
    mut transforms: Query<&mut Transform>,
) {
    let Ok(rig) = rigs.single() else { return };
    if rig.paused || snapshot.0["player"]["heldItemId"].as_u64().unwrap_or(0) != 0 {
        return;
    }
    state.playback.apply(
        rig,
        matches!(
            snapshot.0["player"]["stance"].as_str(),
            Some("crouch" | "prone")
        ),
        &mut transforms,
    );
}

#[cfg(test)]
mod tests;
