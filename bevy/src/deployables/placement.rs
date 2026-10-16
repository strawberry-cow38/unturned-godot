//! Original held-deployable placement: `PlayerController.EquipHeldDeployable`,
//! `TryPlaceDeployable` and `TickDeploy` driving a `BarricadePlacer` ghost.
//! The ghost, its tint, the frozen place gesture and the amber hint are pure
//! presentation. The only state change is Command 7 naming the held jar; the
//! host validates and spends it, and the accepted replica alone draws the body.
use super::*;
use crate::{
    player::{PlayerBody, PlayerCamera, PlayerInput},
    settings::{GameAction, Settings},
    ui::{UiIntent, UiState, input_order::FrameInput},
    use_items::{Cell, HandEvent},
};
use bevy::{
    ecs::system::SystemParam,
    input::ButtonState,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow, WindowEvent},
};
use rules::{Aim, Mount, Probe};

mod ghost;
mod hint;
pub(crate) mod rules;
mod sound;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod udp_tests;
#[cfg(test)]
mod visual;

/// `PlayerController.PlaceTime`: the stand-in when the arms have no Deploy_Use.
const PLACE_TIME: f32 = 0.45;
/// `Terrain.SeaLevelY`'s default (PEI) when a world has no water plane.
const DEFAULT_SEA_LEVEL: f32 = 25.6;

/// The click-frozen target (`_placePoint/_placeNormal/_placeYaw`) while the
/// place gesture plays; the ghost is pinned blue there and aim is ignored.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Gesture {
    pub def: u16,
    pub point: Vec3,
    pub normal: Vec3,
    pub yaw: f32,
    pub remaining: f32,
}
#[derive(Resource, Default)]
pub(crate) struct Placer {
    epoch: u64,
    /// The held deployable def (`_deployable`), from the accepted held jar.
    pub def: Option<u16>,
    last: Option<Cell>,
    last_deployable: bool,
    /// `_revertEquip`: what was held before the first deployable.
    pub revert: Option<Cell>,
    /// `BarricadePlacer.YawOffset`: R adds 90 degrees.
    pub yaw_offset: f32,
    /// The ghost is up and answering the aim (source `active`).
    pub active: bool,
    pub aim: Aim,
    pub gesture: Option<Gesture>,
    /// The completed place intents, in order, for capture evidence.
    pub sent: Vec<Value>,
}
impl Placer {
    /// Whether the ghost is drawn: while aiming, and while the gesture plays.
    pub(crate) fn shown(&self) -> bool {
        self.def.is_some() && (self.active || self.gesture.is_some())
    }
    /// `HUD.PlacementHint(active && !Valid ? Reason : null)`.
    pub(crate) fn hint(&self) -> Option<&'static str> {
        (self.def.is_some() && self.active && self.gesture.is_none() && !self.aim.valid)
            .then_some(self.aim.reason)
            .flatten()
    }
    /// The ghost pose: the live aim, or the frozen click (always valid/blue).
    pub(crate) fn pose(&self) -> Aim {
        self.gesture.map_or(self.aim, |g| Aim {
            valid: true,
            reason: None,
            point: g.point,
            normal: g.normal,
            yaw: g.yaw,
        })
    }
}

pub(super) fn install(app: &mut App) {
    // Owned by the UI and player plugins in the game; harnesses without them
    // still get the hand messages the placer writes.
    app.add_message::<UiIntent>()
        .add_message::<HandEvent>()
        .init_resource::<Placer>()
        .init_resource::<ghost::Ghost>()
        .init_resource::<sound::Queue>()
        .add_systems(
            Update,
            (aim, act, ghost::present, sound::update)
                .chain()
                .after(super::sync)
                .after(crate::player::PlayerCameraUpdate),
        );
    hint::install(app);
}

pub(super) fn held_def<'a>(snapshot: &Value, catalog: &'a ItemCatalog) -> Option<(Cell, &'a Definition)> {
    let cell = Cell::held(snapshot)?;
    catalog
        .items
        .get(&cell.id)
        .is_some_and(|i| i.is_deployable)
        .then(|| catalog.deployables.iter().find(|d| d.id == cell.id))
        .flatten()
        .map(|def| (cell, def))
}

/// The source `_cam`: the rendered camera in first person, the eye camera
/// (`aim_eye`, lean included) behind the third-person shoulder view.
#[derive(SystemParam)]
pub(super) struct Eye<'w, 's> {
    cameras: Query<'w, 's, &'static Transform, With<PlayerCamera>>,
    perspective: Option<Res<'w, crate::avatar::Perspective>>,
    bodies: Query<'w, 's, (&'static Transform, &'static PlayerBody), Without<PlayerCamera>>,
    input: Option<Res<'w, PlayerInput>>,
    lean: Option<Res<'w, crate::player::lean::LeanState>>,
}
impl Eye<'_, '_> {
    fn pose(&self) -> Option<(Vec3, Vec3)> {
        let third = self.perspective.as_ref().is_some_and(|p| !p.first_person);
        if third
            && let (Ok((feet, body)), Some(input)) = (self.bodies.single(), self.input.as_ref())
        {
            return Some(crate::player::aim_eye(
                feet.translation,
                body,
                input,
                self.lean.as_deref(),
            ));
        }
        self.cameras
            .single()
            .ok()
            .map(|c| (c.translation, *c.forward()))
    }
}
#[derive(SystemParam)]
pub(super) struct Probes<'w, 's> {
    context: ReadRapierContext<'w, 's>,
    player: Query<'w, 's, Entity, With<PlayerBody>>,
    parents: Query<'w, 's, &'static ChildOf>,
    terrain: Query<'w, 's, (), With<crate::world::WorldTerrain>>,
    deployed: Query<'w, 's, (), With<Deployed>>,
    vehicles: Query<'w, 's, (), With<crate::vehicle::VehicleBody>>,
    groups: Query<'w, 's, &'static CollisionGroups>,
}
impl Probes<'_, '_> {
    fn lineage(&self, entity: Entity) -> impl Iterator<Item = Entity> + '_ {
        std::iter::once(entity).chain(self.parents.iter_ancestors(entity))
    }
    /// The source ray (mask 1<<0, BARRICADE_INTERACT) and, on a hit, the
    /// clearance sphere and `DefaultAttachable`.
    fn probe(&self, def: &Definition, from: Vec3, dir: Vec3) -> Option<Probe> {
        let context = self.context.single().ok()?;
        let own = self.player.single().ok();
        let solid = |entity: Entity, collider: &bevy_rapier3d::rapier::geometry::Collider| {
            crate::player::world_solid(entity, collider)
                && !own.is_some_and(|p| self.lineage(entity).any(|e| e == p))
        };
        let filter = QueryFilter::default()
            .exclude_sensors()
            .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1 | Group::GROUP_4))
            .predicate(&solid);
        let (hit, ray) = context.cast_ray_and_get_normal(from, dir, def.range, true, filter)?;
        let normal = ray.normal.normalize_or(Vec3::Y);
        // OverlapSphere(point, radius, BLOCK_BARRICADE) excluding the mount
        // surface's body; terrain is never an obstacle (the BarricadePlacer
        // filter drops the "terrain"/"ground"/"structures" groups).
        let body = context.colliders.collider_parent(context.rigidbody_set, hit);
        let mut clear = true;
        context.intersect_shape(
            ray.point + normal * def.offset,
            Quat::IDENTITY,
            &Collider::ball(def.radius.max(0.001)),
            filter,
            |entity, _| {
                if entity == hit
                    || body.is_some_and(|b| {
                        context.colliders.collider_parent(context.rigidbody_set, entity) == Some(b)
                    })
                    || self.lineage(entity).any(|e| self.terrain.contains(e))
                {
                    return true;
                }
                clear = false;
                false
            },
        );
        // A vehicle surface, or another deployable/barricade, is refused.
        let vehicle = self
            .groups
            .get(hit)
            .is_ok_and(|g| g.memberships.contains(Group::GROUP_4))
            || self.lineage(hit).any(|e| self.vehicles.contains(e));
        let attach = !vehicle && !self.lineage(hit).any(|e| self.deployed.contains(e));
        Some(Probe {
            point: ray.point,
            normal,
            clear,
            attach,
        })
    }
}

fn aim(
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    model: Res<UiModel>,
    ui: Option<Res<UiState>>,
    epoch: Res<WorldSessionEpoch>,
    windows: Query<(&Window, &CursorOptions), With<PrimaryWindow>>,
    eye: Eye,
    probes: Probes,
    world: Option<Res<crate::world::WorldState>>,
    mut placer: ResMut<Placer>,
) {
    if placer.epoch != epoch.0 {
        *placer = Placer {
            epoch: epoch.0,
            ..default()
        };
    }
    let held = held_def(&snapshot.0, &catalog);
    let cell = Cell::held(&snapshot.0);
    // EquipHeldDeployable captures the revert only on the switch INTO a
    // deployable; moving between deployables keeps the original fallback.
    if cell != placer.last {
        if held.is_some() && !placer.last_deployable {
            placer.revert = placer.last;
        }
        placer.last = cell;
        placer.last_deployable = held.is_some();
    }
    let def_id = held.map(|(_, d)| d.id);
    if def_id != placer.def {
        // ClearDeployable / a fresh BarricadePlacer: no gesture, no spin.
        placer.def = def_id;
        placer.gesture = None;
        placer.yaw_offset = 0.;
        placer.aim = Aim::default();
    }
    placer.active = held.is_some()
        && model.connected
        && ui.is_some_and(|u| !u.captures_input())
        && snapshot.0["player"]["alive"] == true
        && snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) == 0
        && windows.single().is_ok_and(|(w, c)| {
            w.focused && !c.visible && c.grab_mode != CursorGrabMode::None
        });
    let Some((_, def)) = held else {
        return;
    };
    if !placer.active || placer.gesture.is_some() {
        return;
    }
    let Some((from, dir)) = eye.pose() else {
        return;
    };
    let mount = Mount::of(def);
    // Godot GlobalRotation.Y of a camera looking along `dir`.
    let aim_yaw = (-dir.x).atan2(-dir.z).to_degrees() + placer.yaw_offset;
    let probe = match mount {
        Mount::Window | Mount::Container => None,
        _ => probes.probe(def, from, dir),
    };
    let sea = world
        .and_then(|w| w.sea_level)
        .unwrap_or(DEFAULT_SEA_LEVEL);
    placer.aim = rules::resolve(def, mount, from, dir, aim_yaw, placer.yaw_offset, probe, sea);
}

fn count(snapshot: &Value, id: u16) -> (u32, Option<Cell>) {
    let mut total = 0;
    let mut cells = Vec::new();
    for page in snapshot["inventory"]["pages"].as_array().into_iter().flatten() {
        let Some(index) = page["index"].as_u64().filter(|p| *p < 7) else {
            continue;
        };
        for item in page["items"].as_array().into_iter().flatten() {
            if item["id"].as_u64() == Some(u64::from(id)) {
                total += item["amount"].as_u64().unwrap_or(1) as u32;
                cells.push((
                    Cell {
                        id,
                        page: index as u8,
                        x: item["x"].as_u64().unwrap_or(0) as u8,
                        y: item["y"].as_u64().unwrap_or(0) as u8,
                    },
                    item["amount"].as_u64().unwrap_or(1),
                ));
            }
        }
    }
    let held = Cell::held(snapshot);
    // Another jar of the same def, for a held single that the place spends.
    let stacked = cells
        .iter()
        .any(|(c, amount)| Some(*c) == held && *amount > 1);
    let next = (!stacked)
        .then(|| cells.iter().map(|(c, _)| *c).find(|c| Some(*c) != held))
        .flatten();
    (total, next)
}
fn cell_holds(snapshot: &Value, cell: Cell) -> bool {
    snapshot["inventory"]["pages"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|p| p["index"].as_u64() == Some(u64::from(cell.page)))
        .flat_map(|p| p["items"].as_array().into_iter().flatten())
        .any(|i| {
            i["id"].as_u64() == Some(u64::from(cell.id))
                && i["x"].as_u64() == Some(u64::from(cell.x))
                && i["y"].as_u64() == Some(u64::from(cell.y))
        })
}
fn hold(cell: Cell) -> UiIntent {
    UiIntent::Action {
        name: "holdItem".into(),
        args: json!({"page":cell.page,"x":cell.x,"y":cell.y}),
    }
}
/// `Dequip` / `EquipUnarmed`: the inventory's empty held selection.
fn unarmed() -> UiIntent {
    UiIntent::Action {
        name: "selectHeld".into(),
        args: json!({"page":0,"x":0,"y":0,"itemId":0,"token":0,"equip":false}),
    }
}
/// Deploy_Use's length from the loaded arms rig (`DeployUseLength`).
fn use_length(rigs: Option<&crate::rig::RigLibrary>, config: &WorldConfig) -> f32 {
    rigs.and_then(|r| r.data.get(&config.content_root.join("rig.json")))
        .and_then(|d| d.anims.get("Deploy_Use"))
        .map(|c| c.length)
        .filter(|l| *l > 0.)
        .unwrap_or(PLACE_TIME)
}

#[derive(SystemParam)]
pub(super) struct Out<'w> {
    bridge: Res<'w, CoreBridge>,
    model: ResMut<'w, UiModel>,
    intents: MessageWriter<'w, UiIntent>,
    hands: MessageWriter<'w, HandEvent>,
    sounds: ResMut<'w, sound::Queue>,
}
fn act(
    events: Option<Res<FrameInput>>,
    settings: Option<Res<Settings>>,
    windows: Query<Entity, With<PrimaryWindow>>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    ui: Option<Res<UiState>>,
    time: Res<Time>,
    rigs: Option<Res<crate::rig::RigLibrary>>,
    config: Res<WorldConfig>,
    mut out: Out,
    mut placer: ResMut<Placer>,
) {
    let Some((held, def)) = held_def(&snapshot.0, &catalog) else {
        return;
    };
    if let (Some(events), Some(settings), Ok(window)) = (events, settings, windows.single())
        && placer.active
    {
        for event in &events.events {
            if event.window.is_some_and(|w| w != window) {
                continue;
            }
            let down = match &event.event {
                WindowEvent::KeyboardInput(e) => !e.repeat && e.state == ButtonState::Pressed,
                WindowEvent::MouseButtonInput(e) => e.state == ButtonState::Pressed,
                _ => continue,
            };
            if !down {
                continue;
            }
            if settings.bindings[&GameAction::Fire].matches_window_event(&event.event) {
                // TryPlaceDeployable: only from a valid (blue) spot, frozen at the click.
                if placer.gesture.is_none() && placer.aim.valid {
                    placer.gesture = Some(Gesture {
                        def: def.id,
                        point: placer.aim.point,
                        normal: placer.aim.normal,
                        yaw: placer.aim.yaw,
                        remaining: use_length(rigs.as_deref(), &config),
                    });
                    out.hands.write(HandEvent::Start(held.id));
                }
            } else if settings.bindings[&GameAction::Aim].matches_window_event(&event.event) {
                // RMB cancels placement entirely: empty hands.
                out.intents.write(unarmed());
            } else if settings.bindings[&GameAction::Reload].matches_window_event(&event.event) {
                placer.yaw_offset += 90.;
            }
        }
    }
    let paused = ui.is_some_and(|u| crate::player::simulation_paused(&u));
    let Some(gesture) = placer.gesture.as_mut() else {
        return;
    };
    if !paused {
        gesture.remaining -= time.delta_secs();
    }
    if gesture.remaining > 0. {
        return;
    }
    let gesture = placer.gesture.take().unwrap();
    // HeldDeployableAddress: the jar as it sits now, named for the host to spend.
    let args = json!({"defId":gesture.def,"pos":gesture.point.to_array(),"yawDegrees":gesture.yaw,
        "page":held.page,"x":held.x,"y":held.y});
    if let Err(error) = out.bridge.request(
        "action",
        json!({"name":"placeDeployable","args":args.clone()}),
    ) {
        out.model.notice(error.to_string());
    }
    placer.sent.push(args);
    if let Some(stem) = &def.place_sound {
        out.sounds.0.push((stem.clone(), gesture.point));
    }
    // The revert predicts the echo's spend (count - 1), as the source does.
    let (total, next) = count(&snapshot.0, held.id);
    if total <= 1 {
        let revert = placer
            .revert
            .filter(|c| cell_holds(&snapshot.0, *c) && *c != held);
        out.intents.write(revert.map_or_else(unarmed, hold));
    } else {
        // PlayDeployHold: settle back to the carry hold, still holding the def.
        out.hands.write(HandEvent::Cancel);
        if let Some(next) = next {
            out.intents.write(hold(next));
        }
    }
}
