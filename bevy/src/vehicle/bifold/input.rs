use crate::{
    entities::InteractionFocus,
    player::GameSnapshot,
    settings::{GameAction, Settings},
    ui::{Screen, UiIntent, UiState},
};
use bevy::prelude::*;
use serde_json::{Value, json};

pub(super) fn input(
    snapshot: Res<GameSnapshot>,
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    mut intents: MessageWriter<UiIntent>,
) {
    let state = &snapshot.0;
    // Source discrete _UnhandledInput resumes when Map.Close clears _open;
    // its 85ms outgoing pixels keep only continuous camera/drive captured.
    if ui.screen != Screen::Playing
        || state["connected"] != true
        || state["player"]["alive"] != true
        || state["player"]["vehicleSeat"].as_i64() != Some(0)
        || !settings.just_pressed(GameAction::VehicleDoor, &keys, &mouse)
    {
        return;
    }
    let Some(id) = state["player"]["vehicleNetId"]
        .as_u64()
        .filter(|id| *id != 0)
    else {
        return;
    };
    let Some(row) = state["vehicles"]
        .as_array()
        .and_then(|rows| rows.iter().find(|v| v["netId"].as_u64() == Some(id)))
    else {
        return;
    };
    if row["hasBiFoldDoor"] != true || moving(row) {
        return;
    }
    intents.write(UiIntent::Action {
        name: "vehicleDoor".into(),
        args: json!({"netId": id, "driver":true}),
    });
}
fn moving(row: &Value) -> bool {
    row["doorFold"].as_f64().unwrap_or(0.) != if row["doorOpen"] == true { 1. } else { 0. }
}
pub(in crate::vehicle) fn offer(
    focus: &mut InteractionFocus,
    state: &Value,
    id: u32,
    distance: f32,
    origin: Vec3,
    direction: Vec3,
) {
    let Some(row) = state["vehicles"]
        .as_array()
        .and_then(|rows| rows.iter().find(|v| v["netId"].as_u64() == Some(id.into())))
    else {
        return;
    };
    if row["hasBiFoldDoor"] != true {
        return;
    }
    if moving(row) {
        focus.offer_status(
            distance,
            if row["doorOpen"] == true {
                "door opening"
            } else {
                "door closing"
            },
            "vehicleDoorMoving",
            json!({"netId":id}),
        );
    } else {
        focus.offer(
            distance,
            if row["doorOpen"] == true {
                "close door"
            } else {
                "open door"
            },
            "vehicleDoor",
            json!({"netId":id,"origin":origin.to_array(),"direction":direction.to_array()}),
            false,
        );
    }
}
