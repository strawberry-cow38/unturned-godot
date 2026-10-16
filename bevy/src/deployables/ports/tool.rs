//! Original WireLmb/WireRmb/UpdateWireManage. Routing is cosmetic; only accepted
//! server edges enter the actual graph and only accepted removal disconnects it.
use super::*;
use crate::{
    settings::{GameAction, Settings},
    ui::input_order::FrameInput,
};
use bevy::{
    input::ButtonState,
    window::{PrimaryWindow, WindowEvent},
};

mod hud;
pub(crate) use hud::{ToolHud, ToolReadouts};
#[cfg(test)]
mod tests;
pub(super) fn install_hud(app: &mut App) {
    hud::install(app);
}

#[derive(Resource, Default)]
pub(super) struct Gesture {
    pub start: Option<Address>,
    nodes: Vec<Vec3>,
    clear: Option<(Address, u32, f32)>,
    epoch: u64,
    preview: Vec<Vec3>,
    valid: bool,
    hud: String,
}
impl Gesture {
    pub(in crate::deployables) fn readout(&self) -> &str {
        &self.hud
    }
}
#[derive(Resource, Default)]
pub(super) struct Preview {
    segments: Vec<Entity>,
}

fn source(kind: &str) -> bool {
    matches!(kind, "Output" | "Passthrough" | "DataOut")
}
fn pair(a: &str, b: &str) -> bool {
    (matches!(a, "Output" | "Passthrough") && b == "Consumer") || (a == "DataOut" && b == "DataIn")
}
/// Source CanCompleteWire: only the target must be usable; a start port retired
/// mid-route (0 HP/fire) keeps its route and the server refuses completion.
pub(super) fn complete(a: Option<&Endpoint>, b: Option<&Endpoint>) -> bool {
    a.zip(b).is_some_and(|(a, b)| {
        b.usable
            && a.address.device != b.address.device
            && b.wire.is_none()
            && (pair(&a.definition.kind, &b.definition.kind)
                || pair(&b.definition.kind, &a.definition.kind))
    })
}
fn length(points: &[Vec3]) -> f32 {
    points.windows(2).map(|p| p[0].distance(p[1])).sum()
}
fn points(start: &Endpoint, nodes: &[Vec3], end: Vec3) -> Vec<Vec3> {
    std::iter::once(start.position)
        .chain(nodes.iter().copied())
        .chain(std::iter::once(end))
        .collect()
}
fn info(p: &Endpoint) -> String {
    let name = &p.name;
    let watts = p.definition.watts;
    let live = p.live;
    let draw = p.draw;
    match p.definition.kind.as_str() {
        "Output" => format!("{name} — {watts:.0}w output · {draw:.0}w drawn"),
        "Consumer" if watts > 0. => format!(
            "{name} — {watts:.0}w consumer ({})",
            if p.powered {
                format!("powered, {live:.0}w in")
            } else {
                "unpowered".into()
            }
        ),
        "Consumer" => format!(
            "{name} — input ({})",
            if p.powered {
                format!("{live:.0}w in")
            } else {
                "no power".into()
            }
        ),
        "Passthrough" => format!("{name} — {live:.0}w passthrough"),
        // The retained replicated adapter does not yet solve source data/radio
        // networks. Their authored sockets stay identifiable; live data effects
        // require that distinct authority port rather than a client-side guess.
        "DataOut" => format!("{name} — data out (no signal — needs power)"),
        "DataIn" => format!(
            "{name} — data in ({})",
            if p.wire.is_some() {
                "wired, no signal"
            } else {
                "not connected"
            }
        ),
        _ => name.clone(),
    }
}

pub(super) fn input(
    frame: Res<Frame>,
    events: Option<Res<FrameInput>>,
    settings: Option<Res<Settings>>,
    windows: Query<Entity, With<PrimaryWindow>>,
    epoch: Res<WorldSessionEpoch>,
    bridge: Res<CoreBridge>,
    mut model: ResMut<UiModel>,
    time: Res<Time>,
    mut gesture: ResMut<Gesture>,
) {
    if gesture.epoch != epoch.0 || !frame.shown {
        *gesture = Gesture {
            epoch: epoch.0,
            ..default()
        };
    }
    // Source drops the route only when the start port node is freed (its
    // deployable despawned), not when that port is merely deactivated.
    if gesture
        .start
        .is_some_and(|s| !frame.endpoints.contains_key(&s))
    {
        gesture.start = None;
        gesture.nodes.clear();
    }
    gesture.preview.clear();
    gesture.hud.clear();
    let aimed = frame.hovered.and_then(|a| frame.endpoints.get(&a));
    if gesture.clear.is_some_and(|(a, id, _)| {
        !frame.active
            || frame.hovered != Some(a)
            || frame.endpoints.get(&a).is_none_or(|p| p.wire != Some(id))
    }) {
        gesture.clear = None;
    }
    let send = |name: &str, args: Value, model: &mut UiModel| {
        if let Err(error) = bridge.request("action", json!({"name":name,"args":args})) {
            model.notice(error.to_string());
        }
    };
    if let (Some(events), Some(settings), Ok(window)) =
        (events.as_ref(), settings.as_ref(), windows.single())
    {
        if frame.active {
            for event in &events.events {
                if event.window.is_some_and(|w| w != window) {
                    continue;
                }
                let down = match &event.event {
                    WindowEvent::KeyboardInput(e) if !e.repeat => e.state == ButtonState::Pressed,
                    WindowEvent::MouseButtonInput(e) => e.state == ButtonState::Pressed,
                    _ => continue,
                };
                if settings.bindings[&GameAction::Fire].matches_window_event(&event.event) && down {
                    if let Some(start) = gesture.start.and_then(|s| frame.endpoints.get(&s)) {
                        if complete(Some(start), aimed) {
                            let target = aimed.unwrap();
                            if gesture.nodes.len() <= 20
                                && length(&points(start, &gesture.nodes, target.position)) <= 40.
                            {
                                let (a, b) = if source(&start.definition.kind) {
                                    (start, target)
                                } else {
                                    (target, start)
                                };
                                send(
                                    "connectWire",
                                    json!({"srcId":a.address.device,"srcPort":a.address.index,
                                    "dstId":b.address.device,"dstPort":b.address.index}),
                                    &mut model,
                                );
                                gesture.start = None;
                                gesture.nodes.clear();
                            }
                        } else if gesture.nodes.len() < 20
                            && length(&points(start, &gesture.nodes, frame.free_end)) <= 40.
                        {
                            gesture.nodes.push(frame.free_end);
                        }
                    } else if let Some(port) = aimed.filter(|p| p.usable && p.wire.is_none()) {
                        gesture.start = Some(port.address);
                        gesture.nodes.clear();
                        gesture.clear = None;
                    }
                } else if settings.bindings[&GameAction::Aim].matches_window_event(&event.event) {
                    if down {
                        if gesture.start.is_some() {
                            if gesture.nodes.pop().is_none() {
                                gesture.start = None;
                            }
                        } else if let Some(port) = aimed
                            && let Some(wire) = port.wire
                        {
                            gesture.clear = Some((port.address, wire, 0.));
                        }
                    } else if let Some((_, wire, elapsed)) = gesture.clear.take()
                        && elapsed <= 0.28
                    {
                        send("removeWire", json!({"wireId":wire}), &mut model);
                    }
                }
            }
            let held = events.current(window).is_some_and(|b| {
                settings.bindings[&GameAction::Aim].pressed(&b.physical, &b.mouse)
            });
            if let Some((address, wire, elapsed)) = gesture.clear.take() {
                if held {
                    let elapsed = elapsed + time.delta_secs();
                    if elapsed >= 1. {
                        send("removeWire", json!({"wireId":wire}), &mut model);
                    } else {
                        gesture.clear = Some((address, wire, elapsed));
                    }
                }
            }
        }
    }
    if let Some(start) = gesture.start.and_then(|s| frame.endpoints.get(&s)) {
        let end = if complete(Some(start), aimed) {
            aimed.unwrap().position
        } else {
            frame.free_end
        };
        let preview = points(start, &gesture.nodes, end);
        let len = length(&preview);
        let over = gesture.nodes.len() >= 20 || len > 40.;
        gesture.hud = format!(
            "nodes {}/20    {len:.1}/40m{}",
            gesture.nodes.len(),
            if over { "   -- LIMIT" } else { "" }
        );
        gesture.preview = preview;
        gesture.valid = !over;
    } else if let Some(p) = aimed {
        // Source UpdateWireManage writes "clearing wire... N%", but the later
        // UpdateWireLook in the same ProcessTick always replaces it with this
        // info line, so the percentage is never displayed.
        gesture.hud = info(p)
            + if p.wire.is_some() {
                "   ([RMB] hold: clear · tap: unplug)"
            } else {
                ""
            };
    }
}

pub(super) fn preview(
    mut commands: Commands,
    gesture: Res<Gesture>,
    mut pool: ResMut<Preview>,
    mut art: ResMut<wires::Visuals>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    if gesture.start.is_none() {
        for entity in pool.segments.drain(..) {
            commands.entity(entity).despawn();
        }
        return;
    }
    let (mesh, material) = art.handles(gesture.valid, &mut meshes, &mut materials);
    let pairs: Vec<_> = gesture.preview.windows(2).collect();
    for (i, pair) in pairs.iter().enumerate() {
        let pose = wires::segment::pose(pair[0], pair[1]);
        let visible = if pose.is_some() {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if let Some(entity) = pool.segments.get(i) {
            commands.entity(*entity).insert((
                pose.unwrap_or_default(),
                visible,
                MeshMaterial3d(material.clone()),
            ));
        } else {
            pool.segments.push(
                commands
                    .spawn((
                        Name::new("Source wire routing preview"),
                        Mesh3d(mesh.clone()),
                        MeshMaterial3d(material.clone()),
                        pose.unwrap_or_default(),
                        visible,
                    ))
                    .id(),
            );
        }
    }
    for entity in pool.segments.iter().skip(pairs.len()) {
        commands.entity(*entity).insert(Visibility::Hidden);
    }
}
