//! Original active-camera focus and hold-F pickup. This submits existing owner
//! intents; accepted replicas alone remove the body or refund its item.
use super::*;
use crate::{
    entities::{ApplyInteractions, CollectInteractions, InteractionFocus},
    info_billboard::{Bar, Content, FocusedInfo, View},
    player::{PlayerBody, PlayerCamera, PlayerCameraUpdate},
    settings::{GameAction, Settings},
    ui::{UiState, input_order::FrameInput},
};
use bevy::{
    input::ButtonState,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow, WindowEvent},
};
use pickup::{Hold, Tap};

const ACTION: &str = "ordinaryDeployable";
const REACH: f32 = 2.6;
#[cfg(test)]
mod tests;

#[derive(Resource, Default)]
struct Hovered(Option<(u32, u16, Vec3)>);
#[derive(Resource, Default)]
struct Gesture {
    hold: Hold,
    levels: HashMap<u32, f32>,
    epoch: u64,
}
pub(super) fn install(app: &mut App) {
    app.init_resource::<Hovered>()
        .init_resource::<Gesture>()
        .init_resource::<InteractionDiagnostics>()
        .init_resource::<FocusedInfo>()
        .add_systems(
            Update,
            collect
                .in_set(CollectInteractions)
                .after(PlayerCameraUpdate),
        )
        .add_systems(
            Update,
            apply.after(CollectInteractions).before(ApplyInteractions),
        );
}
fn collect(
    ui: Option<Res<UiState>>,
    snapshot: Res<GameSnapshot>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    deployed: Query<(&Deployed, &Transform)>,
    parents: Query<&ChildOf>,
    context: ReadRapierContext,
    mut hovered: ResMut<Hovered>,
    mut focus: Option<ResMut<InteractionFocus>>,
    mut diagnostic: ResMut<InteractionDiagnostics>,
    names: Query<&Name>,
) {
    *diagnostic = default();
    diagnostic.gate = "no eligible owner camera".into();
    hovered.0 = None;
    if ui.is_none_or(|ui| ui.captures_input())
        || snapshot.0["player"]["alive"] != true
        || snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) != 0
    {
        return;
    }
    let (Ok(camera), Ok(context), Some(focus)) =
        (camera.single(), context.single(), focus.as_mut())
    else {
        return;
    };
    let own = player.single().ok();
    diagnostic.camera = Some(camera.translation.to_array());
    diagnostic.direction = Some(camera.forward().to_array());
    let exclude_owner = |entity, _: &bevy_rapier3d::rapier::geometry::Collider| {
        !own.is_some_and(|p| entity == p || parents.iter_ancestors(entity).any(|a| a == p))
    };
    let ray = context.cast_ray(
        camera.translation,
        *camera.forward(),
        REACH,
        true,
        QueryFilter::default()
            .exclude_sensors()
            .groups(CollisionGroups::new(
                Group::GROUP_2,
                Group::GROUP_1 | Group::GROUP_4,
            ))
            .predicate(&exclude_owner),
    );
    let Some((hit, distance)) = ray else { return };
    diagnostic.ray_distance = Some(distance);
    diagnostic.hit = Some(
        names
            .get(hit)
            .map_or_else(|_| format!("{hit:?}"), |name| name.as_str().to_owned()),
    );
    diagnostic.gate = "ray stopped on another body".into();
    let owner = std::iter::once(hit)
        .chain(parents.iter_ancestors(hit))
        .find_map(|e| deployed.get(e).ok());
    let Some((body, pose)) = owner else { return };
    hovered.0 = Some((body.net_id, body.def_id, pose.translation));
    diagnostic.offered = Some(body.net_id);
    diagnostic.gate = "offered source body".into();
    // The billboard owns the source hint. Reserve generic interaction dispatch
    // without duplicating its binding text in the bottom-center HUD.
    focus.offer_status(distance, "", ACTION, json!({"netId":body.net_id}));
}

fn apply(
    hovered: Res<Hovered>,
    focus: Option<Res<InteractionFocus>>,
    ui: Option<Res<UiState>>,
    settings: Option<Res<Settings>>,
    events: Option<Res<FrameInput>>,
    windows: Query<(Entity, &CursorOptions), With<PrimaryWindow>>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    epoch: Res<WorldSessionEpoch>,
    time: Res<Time>,
    bridge: Res<CoreBridge>,
    mut gesture: ResMut<Gesture>,
    mut info: ResMut<FocusedInfo>,
    mut model: ResMut<UiModel>,
    mut diagnostic: ResMut<InteractionDiagnostics>,
) {
    if gesture.epoch != epoch.0 {
        *gesture = Gesture {
            epoch: epoch.0,
            ..default()
        };
    }
    let live: HashSet<u32> = snapshot.0["deployables"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|r| r["netId"].as_u64().and_then(|id| u32::try_from(id).ok()))
        .collect();
    gesture.levels.retain(|id, _| live.contains(id));
    // Cosmetic source warmup/cooldown gates quick toggles; it never drains fuel
    // or grants output, both of which remain authoritative backend work.
    let ramp_delta = if ui
        .as_ref()
        .is_some_and(|ui| crate::player::simulation_paused(ui))
    {
        0.
    } else {
        time.delta_secs()
    };
    for row in snapshot.0["deployables"].as_array().into_iter().flatten() {
        let Some(id) = row["netId"].as_u64().and_then(|id| u32::try_from(id).ok()) else {
            continue;
        };
        let def = catalog
            .deployables
            .iter()
            .find(|d| Some(d.id as u64) == row["defId"].as_u64());
        let target = if def.is_some_and(|d| d.fuel > 0.)
            && row["toggledOn"] == true
            && row["onFire"] != true
            && row["fuel"].as_f64().unwrap_or(0.) > 0.
        {
            1_f32
        } else {
            0.
        };
        let level = gesture.levels.entry(id).or_default();
        *level = if *level < target {
            (*level + ramp_delta / 1.3).min(target)
        } else {
            (*level - ramp_delta / 1.1).max(target)
        };
    }
    let (Some(ui), Some(settings), Some(events), Ok((window, cursor))) =
        (ui, settings, events, windows.single())
    else {
        gesture.hold.cancel();
        return;
    };
    let active = !ui.captures_input()
        && !cursor.visible
        && cursor.grab_mode != CursorGrabMode::None
        && model.connected
        && snapshot.0["player"]["alive"] == true
        && snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) == 0;
    let selected = hovered
        .0
        .filter(|(id, _, _)| active && focus.as_ref().is_some_and(|f| f.selected(ACTION, *id)));
    let Some((id, def_id, position)) = selected else {
        if diagnostic.offered.is_some() {
            diagnostic.gate = "input capture or focus arbitration declined".into();
        }
        gesture.hold.cancel();
        return;
    };
    diagnostic.selected = Some(id);
    diagnostic.gate = "selected source body".into();
    let Some(def) = catalog.deployables.iter().find(|d| d.id == def_id) else {
        gesture.hold.cancel();
        return;
    };
    let Some(row) = snapshot.0["deployables"]
        .as_array()
        .into_iter()
        .flatten()
        .find(|r| r["netId"] == id)
    else {
        gesture.hold.cancel();
        return;
    };
    let health = row["health"].as_f64().unwrap_or(0.) as f32;
    let burning = row["onFire"] == true || health <= 0.;
    let toggled = row["toggledOn"] == true;
    let target = if toggled && !burning && def.fuel > 0. && row["fuel"].as_f64().unwrap_or(0.) > 0.
    {
        1.
    } else {
        0.
    };
    let level = gesture.levels.get(&id).copied().unwrap_or(0.);
    let can_toggle =
        !burning && (def.is_switch || (def.fuel > 0. && (level - target).abs() < 0.001));
    let binding = &settings.bindings[&GameAction::Interact];
    if gesture.hold.target().is_some_and(|held| held != id) || burning {
        gesture.hold.cancel();
    }
    let mut send = |name: &str, args: Value| {
        if let Err(error) = bridge.request("action", json!({"name":name,"args":args})) {
            model.notice(error.to_string());
        }
    };
    for event in &events.events {
        if event.window.is_some_and(|w| w != window) {
            continue;
        }
        if matches!(&event.event, WindowEvent::KeyboardFocusLost(_))
            || matches!(&event.event, WindowEvent::WindowFocused(e) if !e.focused)
        {
            gesture.hold.cancel();
            continue;
        }
        if !binding.matches_window_event(&event.event) {
            continue;
        }
        let down = match &event.event {
            WindowEvent::KeyboardInput(e) if !e.repeat => e.state == ButtonState::Pressed,
            WindowEvent::MouseButtonInput(e) => e.state == ButtonState::Pressed,
            _ => continue,
        };
        if down {
            let looking = settings.bindings[&GameAction::FreeLook]
                .pressed(&event.buttons.physical, &event.buttons.mouse);
            if !burning && !looking {
                gesture.hold.begin(id);
            }
        } else if let Some(Tap::Toggle(id)) = gesture.hold.release(can_toggle, false) {
            send("toggleDeployable", json!({"netId":id,"on":!toggled}));
        }
    }
    let held = events
        .current(window)
        .is_some_and(|b| binding.pressed(&b.physical, &b.mouse));
    if let Some(id) = gesture.hold.advance(time.delta_secs(), held && !burning) {
        send("pickupDeployable", json!({"netId":id}));
    }
    let fraction = gesture.hold.progress();
    diagnostic.progress = fraction;
    let prompt = if fraction > 0.01 {
        format!("Picking up... {}%", (fraction * 100.).clamp(0., 99.) as i32)
    } else if burning {
        String::new()
    } else if def.fuel > 0. || def.is_switch {
        format!(
            "[{}] Turn {} · Hold [{}]: pick up",
            binding.label(),
            if toggled { "Off" } else { "On" },
            binding.label()
        )
    } else {
        format!("Hold [{}]: pick up", binding.label())
    };
    info.0 = Some(View {
        position: position + Vec3::Y * 0.5,
        content: Content {
            name: def.name.clone(),
            name_color: Color::srgb(0.82, 0.83, 0.90),
            prompt,
            prompt_color: Color::srgb(0.82, 0.83, 0.90),
            bars: [
                Some(Bar {
                    value: if def.health > 0. {
                        health / def.health
                    } else {
                        0.
                    },
                    color: Color::srgb(191. / 255., 31. / 255., 31. / 255.),
                }),
                (def.fuel > 0.).then(|| Bar {
                    value: row["fuel"].as_f64().unwrap_or(0.) as f32 / def.fuel,
                    color: Color::srgb(220. / 255., 180. / 255., 19. / 255.),
                }),
                None,
                None,
                None,
            ],
        },
    });
}
