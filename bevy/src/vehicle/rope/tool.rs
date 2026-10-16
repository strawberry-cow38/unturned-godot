//! Original rope tool (PlayerController UpdateRopeLook/RopeLmb/RopeManageArm/
//! UpdateRopeManage). Ties and unties are intents; nubs, highlights and the
//! committed rope reflect only the accepted relationship.
use super::*;
use crate::{
    deployables::ToolReadouts,
    entities::ItemCatalog,
    player::{GameSnapshot, PlayerCamera},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState, input_order::FrameInput},
};
use bevy::{
    input::ButtonState,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow, WindowEvent},
};

/// Source PlayerController / Vehicle constants.
pub(crate) const ROPE_REACH: f32 = 6.;
pub(crate) const ROPE_PICK_RADIUS: f32 = 0.7;
pub(crate) const TOW_ATTACH_REACH: f32 = 4.5;
pub(crate) const TOW_REST_MIN: f32 = 2.;
const CLEAR_TIME: f32 = 1.;
const CLICK_MAX: f32 = 0.28;

#[derive(Resource, Default)]
pub(crate) struct RopeTool {
    /// Pending tie from this tower's rear node (`_roping`/`_ropeSrc`).
    pub start: Option<u32>,
    /// Aimed node: vehicle and rear (true) or front.
    pub looked: Option<(u32, bool)>,
    /// Armed RMB manage on a roped vehicle and its hold time.
    pub clear: Option<(u32, f32)>,
    pub hud: String,
    epoch: u64,
}
#[derive(Component)]
pub(crate) struct TowNub {
    pub rear: bool,
    material: Handle<StandardMaterial>,
}
#[derive(Component)]
struct NubsBuilt;
#[derive(Component)]
struct PreviewSegment;
#[derive(Resource, Default)]
struct Preview(Vec<Entity>);

pub(super) fn install(app: &mut App) {
    app.init_resource::<RopeTool>()
        .init_resource::<Preview>()
        .add_systems(
            Update,
            (nubs, input, present)
                .chain()
                .after(crate::player::PlayerCameraUpdate)
                .before(crate::entities::ApplyInteractions)
                .before(crate::deployables::ToolHud),
        );
}

fn held(snapshot: &serde_json::Value, catalog: &ItemCatalog) -> bool {
    crate::use_items::Cell::held(snapshot)
        .and_then(|cell| catalog.items.get(&cell.id))
        .is_some_and(|item| item.tool["kind"] == "Rope")
}

/// Vehicle.Build: blue front (towed end) and green rear (tower end) 16 cm nubs.
fn nubs(
    mut commands: Commands,
    bodies: Query<(Entity, &VehicleBody), Without<NubsBuilt>>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut cube: Local<Option<Handle<Mesh>>>,
) {
    for (entity, body) in &bodies {
        let Some(d) = data::definition(body.type_id) else { continue };
        let mesh = cube.get_or_insert_with(|| meshes.add(Cuboid::from_length(0.16))).clone();
        for (rear, color) in [(false, Color::srgb(0.30, 0.62, 1.)), (true, Color::srgb(0.25, 0.85, 0.30))] {
            let material = materials.add(StandardMaterial {
                base_color: color,
                metallic: 0.,
                perceptual_roughness: 0.6,
                ..default()
            });
            commands.spawn((
                Name::new(if rear { "Rear tow nub" } else { "Front tow nub" }),
                TowNub { rear, material: material.clone() },
                Mesh3d(mesh.clone()),
                MeshMaterial3d(material),
                bevy::light::NotShadowCaster,
                Transform::from_translation(tow_local(&d.spec, rear)),
                Visibility::Hidden,
                ChildOf(entity),
            ));
        }
        commands.entity(entity).insert(NubsBuilt);
    }
}

/// PickTowNode/ConsiderTowNode: analytic aim ray against the tow points.
pub(crate) fn pick(
    from: Vec3,
    forward: Vec3,
    nodes: impl Iterator<Item = (u32, bool, Vec3)>,
) -> Option<(u32, bool)> {
    let mut best = None;
    let mut best_perp = ROPE_PICK_RADIUS;
    for (id, rear, point) in nodes {
        let t = (point - from).dot(forward);
        if !(0. ..=ROPE_REACH).contains(&t) {
            continue;
        }
        let perp = (point - (from + forward * t)).length();
        if perp < best_perp {
            best_perp = perp;
            best = Some((id, rear));
        }
    }
    best
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
    cameras: Query<&Transform, With<PlayerCamera>>,
    bodies: Query<(&VehicleBody, &Transform)>,
    epoch: Res<crate::player::WorldSessionEpoch>,
    bridge: Res<CoreBridge>,
    time: Res<Time>,
    mut tool: ResMut<RopeTool>,
) {
    let shown = model.connected && held(&snapshot.0, &catalog);
    if tool.epoch != epoch.0 || !shown {
        *tool = RopeTool { epoch: epoch.0, ..default() };
        return;
    }
    tool.hud.clear();
    let window = windows.single().ok();
    let active = ui.as_ref().is_some_and(|u| !u.captures_input())
        && snapshot.0["player"]["alive"] == true
        && snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) == 0
        && window.is_some_and(|(_, w, c)| w.focused && !c.visible && c.grab_mode != CursorGrabMode::None);
    let camera = cameras.single().ok().copied();
    // Accepted state per vehicle: tow points and roped/wrecked flags.
    let rows: Vec<(u32, Vec3, Vec3, bool, bool)> = bodies
        .iter()
        .filter_map(|(body, pose)| {
            let spec = &data::definition(body.type_id)?.spec;
            Some((
                body.net_id,
                pose.transform_point(tow_local(spec, true)),
                pose.transform_point(tow_local(spec, false)),
                body.rope.roped(),
                body.flags & 32 != 0,
            ))
        })
        .collect();
    let row = |id: u32| rows.iter().find(|r| r.0 == id);
    tool.looked = match (active, camera) {
        (true, Some(cam)) => pick(
            cam.translation,
            *cam.forward(),
            rows.iter()
                .filter(|r| !r.4)
                .flat_map(|r| [(r.0, true, r.1), (r.0, false, r.2)]),
        ),
        _ => None,
    };
    // TowValid: a source car that despawned mid-tie drops the pending rope.
    if tool.start.is_some_and(|s| row(s).is_none()) {
        tool.start = None;
    }
    let send = |name: &str, args: serde_json::Value, notices: &mut UiModel| {
        if let Err(error) = bridge.request("action", json!({"name":name,"args":args})) {
            notices.notice(error.to_string());
        }
    };
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
            if !down {
                continue;
            }
            if settings.bindings[&GameAction::Fire].matches_window_event(&event.event) {
                // RopeLmb: start on an unroped rear node; complete on another car's front.
                match (tool.start, tool.looked) {
                    (None, Some((id, true))) if row(id).is_some_and(|r| !r.3) => {
                        tool.start = Some(id);
                    }
                    (Some(start), Some((id, false))) if id != start => {
                        send("attachTow", json!({"towerNetId":start,"towedNetId":id}), &mut model);
                        tool.start = None;
                    }
                    _ => {}
                }
            } else if settings.bindings[&GameAction::Aim].matches_window_event(&event.event) {
                // RMB press: cancel a pending tie, else arm manage on a roped car.
                if tool.start.take().is_none()
                    && let Some((id, _)) = tool.looked
                    && row(id).is_some_and(|r| r.3)
                {
                    tool.clear = Some((id, 0.));
                }
            }
        }
    }
    // UpdateRopeManage: hold to clear, quick release disconnects.
    if let Some((id, elapsed)) = tool.clear.take() {
        let still = active && tool.start.is_none() && tool.looked.map(|l| l.0) == Some(id)
            && row(id).is_some_and(|r| r.3);
        let held = still
            && events.as_ref().zip(settings.as_ref()).zip(window).is_some_and(|((events, settings), (w, ..))| {
                events.current(w).is_some_and(|b| settings.bindings[&GameAction::Aim].pressed(&b.physical, &b.mouse))
            });
        if still && held {
            let elapsed = elapsed + time.delta_secs();
            if elapsed >= CLEAR_TIME {
                send("detachTow", json!({"netId":id}), &mut model);
            } else {
                tool.clear = Some((id, elapsed));
                tool.hud = format!("clearing tow rope... {}%", ((elapsed / CLEAR_TIME * 100.) as i32).clamp(0, 99));
            }
        } else if still && elapsed <= CLICK_MAX {
            send("detachTow", json!({"netId":id}), &mut model);
        }
    }
    // UpdateRopeLook's HUD; UpdateRopeManage owns the label while armed.
    if tool.clear.is_none() {
        tool.hud = match (tool.start, tool.looked, camera) {
            // Shown whenever a tie is pending, like the source (not input-gated).
            (Some(start), looked, Some(cam)) => {
                let dest = looked.filter(|(id, rear)| !rear && *id != start && row(*id).is_some_and(|r| !r.3));
                let tip = dest.and_then(|(id, _)| row(id)).map_or(cam.translation + *cam.forward() * ROPE_REACH, |r| r.2);
                let gap = row(start).map_or(0., |r| r.1.distance(tip));
                format!(
                    "tow rope   {}/{TOW_ATTACH_REACH:.1}m{}",
                    dotnet_one_decimal(gap),
                    if gap > TOW_ATTACH_REACH { "   -- TOO FAR" } else if dest.is_some() { "   [LMB] tie" } else { "" }
                )
            }
            (None, Some((id, _)), _) if row(id).is_some_and(|r| r.3) => {
                "[RMB]  hold: clear rope  ·  tap: disconnect".into()
            }
            (None, Some((id, true)), _) if row(id).is_some_and(|r| !r.3) => "[LMB] start tow".into(),
            _ => String::new(),
        };
    }
}

/// .NET `{0:0.0}` rounds a midpoint away from zero; Rust's `{:.1}` rounds
/// exact ties to even (2.25 -> "2.2" vs the source's "2.3").
pub(crate) fn dotnet_one_decimal(x: f32) -> String {
    format!("{:.1}", (x * 10.).round() / 10.)
}

/// SetTowNodesVisible / SetTowNubHighlighted and the TowRope tie preview.
#[allow(clippy::too_many_arguments)]
fn present(
    mut commands: Commands,
    tool: Res<RopeTool>,
    mut readouts: Option<ResMut<ToolReadouts>>,
    bodies: Query<(&VehicleBody, &Transform)>,
    mut nubs: Query<(&TowNub, &ChildOf, &mut Visibility)>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    cameras: Query<&Transform, With<PlayerCamera>>,
    mut preview: ResMut<Preview>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut art: Local<Option<(Handle<Mesh>, Handle<StandardMaterial>, Handle<StandardMaterial>)>>,
    shown: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    model: Res<UiModel>,
) {
    if let Some(readouts) = readouts.as_mut()
        && readouts.rope != tool.hud
    {
        readouts.rope = tool.hud.clone();
    }
    let on = model.connected && held(&shown.0, &catalog);
    for (nub, parent, mut visibility) in &mut nubs {
        let Ok((body, _)) = bodies.get(parent.parent()) else { continue };
        let visible = on && if nub.rear { body.rope.by == 0 } else { body.rope.towed == 0 };
        let wanted = if visible { Visibility::Inherited } else { Visibility::Hidden };
        if *visibility != wanted {
            *visibility = wanted;
        }
        let lit = on && tool.looked == Some((body.net_id, nub.rear));
        if let Some(mut m) = materials.get_mut(&nub.material) {
            let emissive = if lit { m.base_color.to_linear() } else { LinearRgba::BLACK };
            if m.emissive != emissive {
                m.emissive = emissive;
            }
        }
    }
    let (mesh, ok, bad) = art
        .get_or_insert_with(|| {
            (
                meshes.add(Cylinder::new(0.032, 1.).mesh().resolution(6)),
                materials.add(StandardMaterial { base_color: Color::srgb(0.42, 0.30, 0.16), metallic: 0., perceptual_roughness: 0.9, ..default() }),
                materials.add(StandardMaterial { base_color: Color::srgb(0.90, 0.20, 0.15), metallic: 0., perceptual_roughness: 0.9, ..default() }),
            )
        })
        .clone();
    let endpoints = tool.start.and_then(|start| {
        let (body, pose) = bodies.iter().find(|(b, _)| b.net_id == start)?;
        let a = pose.transform_point(tow_local(&data::definition(body.type_id)?.spec, true));
        let dest = tool.looked.filter(|(id, rear)| !rear && *id != start).and_then(|(id, _)| {
            let (b, p) = bodies.iter().find(|(b, _)| b.net_id == id)?;
            Some(p.transform_point(tow_local(&data::definition(b.type_id)?.spec, false)))
        });
        let cam = cameras.single().ok()?;
        Some((a, dest.unwrap_or(cam.translation + *cam.forward() * ROPE_REACH), dest.is_some()))
    });
    let Some((a, b, valid)) = endpoints else {
        for entity in preview.0.drain(..) {
            commands.entity(entity).despawn();
        }
        return;
    };
    let points = sag_points(a, b, TOW_REST_MIN);
    while preview.0.len() < ROPE_SEGMENTS {
        preview.0.push(commands.spawn((Name::new("Tow rope preview"), PreviewSegment, Mesh3d(mesh.clone()), MeshMaterial3d(ok.clone()), Transform::default(), Visibility::Hidden)).id());
    }
    for (entity, pair) in preview.0.iter().zip(points.windows(2)) {
        let pose = crate::deployables::segment_pose(pair[0], pair[1]);
        commands.entity(*entity).insert((
            pose.unwrap_or_default(),
            if pose.is_some() { Visibility::Inherited } else { Visibility::Hidden },
            MeshMaterial3d(if valid { ok.clone() } else { bad.clone() }),
        ));
    }
}

#[cfg(test)]
mod tests;
