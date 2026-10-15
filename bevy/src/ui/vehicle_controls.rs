//! Original LightbarRadial/MechanicsPanel: accepted vehicle state is the model;
//! input only requests the shared protocol actions.
use super::*;
use crate::{
    entities::{ApplyInteractions, CollectInteractions, InteractionFocus, ItemCatalog},
    player::{GameSnapshot, PlayerCamera, WorldSessionEpoch},
    vehicle::{VehicleBody, data},
};
use bevy::window::PrimaryWindow;

mod mechanics;
mod radial;
#[cfg(test)]
mod tests;

#[derive(Resource, Default)]
struct Menu {
    epoch: u64,
    held: Option<(u32, u128)>,
    radial: Option<radial::Selection>,
    mechanics: Option<u32>,
}
pub(super) struct VehicleControlsPlugin;
impl Plugin for VehicleControlsPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Menu>()
            .add_plugins(radial::RadialPlugin)
            .add_systems(
                Update,
                input
                    .in_set(UiUpdate)
                    .after(follow_life_state)
                    .before(keyboard_ui),
            )
            .add_systems(
                Update,
                mechanics::open
                    .after(crate::vehicle::access::VehicleAccess)
                    .after(CollectInteractions)
                    .before(ApplyInteractions),
            )
            .add_systems(
                Update,
                (mechanics::build, mechanics::refresh)
                    .chain()
                    .after(ApplyInteractions),
            )
            .add_observer(mechanics::repair);
    }
}
fn row(snapshot: &Value, id: u32) -> Option<&Value> {
    snapshot["vehicles"]
        .as_array()?
        .iter()
        .find(|v| v["netId"].as_u64() == Some(id.into()))
}
fn source_driver(snapshot: &Value) -> Option<u32> {
    if snapshot["connected"] != true
        || snapshot["player"]["alive"] != true
        || snapshot["player"]["vehicleSeat"].as_i64() != Some(0)
    {
        return None;
    }
    let id = u32::try_from(snapshot["player"]["vehicleNetId"].as_u64()?).ok()?;
    let vehicle = row(snapshot, id)?;
    if vehicle["flags"].as_u64()? & 32 != 0 {
        return None;
    }
    let d = data::definition(u8::try_from(vehicle["typeId"].as_u64()?).ok()?)?;
    // PlayerController's bus-door branch claims Ctrl before the lightbar branch.
    if d.access.iter().any(|z| z.kind == "BiFold") {
        return None;
    }
    d.spec
        .parts
        .as_ref()?
        .iter()
        .any(|(name, _)| name.contains("_siren0"))
        .then_some(id)
}
fn intent(intents: &mut MessageWriter<UiIntent>, id: u32, action: &str, value: i32) {
    intents.write(UiIntent::Action {
        name: "vehicleLighting".into(),
        args: json!({"netId":id,"action":action,"value":value}),
    });
}
fn input(
    mut keys: ResMut<ButtonInput<KeyCode>>,
    mut mouse: ResMut<ButtonInput<MouseButton>>,
    settings: Res<Settings>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    time: Res<Time<Real>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut ui: ResMut<UiState>,
    mut menu: ResMut<Menu>,
    mut intents: MessageWriter<UiIntent>,
) {
    let changed = menu.epoch != epoch.0;
    menu.epoch = epoch.0;
    if changed || snapshot.0["connected"] != true || snapshot.0["player"]["alive"] != true {
        menu.held = None;
        menu.radial = None;
        menu.mechanics = None;
        if matches!(ui.screen, Screen::Lightbar | Screen::Mechanics) {
            ui.screen = Screen::Playing;
        }
        return;
    }
    if ui.screen == Screen::Mechanics {
        if menu
            .mechanics
            .is_none_or(|id| row(&snapshot.0, id).is_none())
        {
            menu.mechanics = None;
            ui.screen = Screen::Playing;
            return;
        }
        if keys.just_pressed(KeyCode::Escape)
            || settings.just_pressed(GameAction::Interact, &keys, &mouse)
        {
            keys.clear_just_pressed(KeyCode::Escape);
            // Consume the closing edge before ordinary interaction dispatch;
            // that same press must not immediately re-enter a vehicle.
            menu.mechanics = None;
            ui.screen = Screen::Playing;
            mechanics::consume_interact(&settings, &mut keys, &mut mouse);
        }
        return;
    }
    let current = source_driver(&snapshot.0);
    if ui.screen == Screen::Lightbar {
        let valid = menu.radial.as_ref().is_some_and(|r| Some(r.id) == current);
        if !valid {
            menu.radial = None;
            menu.held = None;
            ui.screen = Screen::Playing;
            return;
        }
        if let Ok(w) = windows.single() {
            if let Some(p) = w.cursor_position() {
                menu.radial
                    .as_mut()
                    .unwrap()
                    .point(p, Vec2::new(w.width(), w.height()));
            }
        }
        let release = (keys.just_released(KeyCode::ControlLeft)
            || keys.just_released(KeyCode::ControlRight))
            && !(keys.pressed(KeyCode::ControlLeft) || keys.pressed(KeyCode::ControlRight));
        let pick = release || mouse.just_pressed(MouseButton::Left);
        let cancel = keys.just_pressed(KeyCode::Escape) || mouse.just_pressed(MouseButton::Right);
        if pick || cancel {
            if pick && !cancel {
                if let Some(r) = &menu.radial {
                    if let Some(value) = r.confirm() {
                        intent(&mut intents, r.id, "SetPattern", value);
                    }
                }
            }
            menu.radial = None;
            menu.held = None;
            ui.screen = Screen::Playing;
            keys.clear_just_pressed(KeyCode::Escape);
            mouse.clear_just_pressed(MouseButton::Left);
            mouse.clear_just_pressed(MouseButton::Right);
        }
        return;
    }
    if ui.screen != Screen::Playing {
        menu.held = None;
        menu.radial = None;
        return;
    }
    let Some(id) = current else {
        menu.held = None;
        return;
    };
    let now = time.elapsed().as_millis();
    if keys.just_pressed(KeyCode::ControlLeft) || keys.just_pressed(KeyCode::ControlRight) {
        if menu.held.is_none() {
            menu.held = Some((id, now));
        }
    }
    if let Some((held_id, start)) = menu.held {
        if held_id != id {
            menu.held = None;
            return;
        }
        let down = keys.pressed(KeyCode::ControlLeft) || keys.pressed(KeyCode::ControlRight);
        if !down {
            menu.held = None;
            if now - start < radial::HOLD_MILLIS {
                intent(&mut intents, id, "ToggleSiren", 0);
            }
        } else if now - start >= radial::HOLD_MILLIS {
            menu.radial = Some(radial::Selection::new(id, row(&snapshot.0, id).unwrap()));
            ui.screen = Screen::Lightbar;
        }
    }
}
