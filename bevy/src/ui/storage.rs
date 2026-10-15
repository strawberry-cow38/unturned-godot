//! Source OpenInventory/OnReplicatedStorageOpened lifecycle and container column.
//! Requests are not acknowledgements: only accepted owner state supplies grids.
use super::*;
use crate::{
    bridge::CoreBridge,
    entities::{ApplyInteractions, InteractionFocus},
    player::{GameSnapshot, WorldSessionEpoch},
};
use bevy::{input::mouse::MouseWheel, ui::ScrollPosition, window::PrimaryWindow};

#[derive(Clone, Debug, Deserialize, Default, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct View {
    pub revision: u64,
    pub net_id: u32,
    pub label: String,
    pub compartments: Vec<Compartment>,
}
#[derive(Clone, Debug, Deserialize, Default, PartialEq)]
#[serde(default)]
pub struct Compartment {
    pub page: u8,
    pub width: u8,
    pub height: u8,
    pub label: String,
}

#[derive(Clone, Copy, PartialEq)]
enum Pending {
    Trunk,
    Cabin,
}
#[derive(Resource, Default)]
struct Lifecycle {
    epoch: u64,
    revision: u64,
    previous: Screen,
    pending: Option<Pending>,
}
#[derive(Resource, Default)]
struct Scrolls([f32; 2]);

pub(super) struct StoragePlugin;
impl Plugin for StoragePlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Lifecycle>()
            .init_resource::<Scrolls>()
            .add_systems(
                Update,
                (
                    open_trunk
                        .after(crate::vehicle::access::VehicleAccess)
                        .before(ApplyInteractions),
                    adopt.after(ApplyInteractions),
                    scroll.after(UiUpdate),
                ),
            );
    }
}

fn request(bridge: &CoreBridge, vehicle: u32, kind: Pending) -> anyhow::Result<bool> {
    let r = bridge.request(
        "action",
        json!({"name":"openVehicleStorage", "args":{
            "vehicleNetId":vehicle,"kind":if kind == Pending::Cabin { 1 } else { 0 }
        }}),
    )?;
    Ok(r["result"]["queued"].as_bool().unwrap_or(false))
}
fn close(bridge: &CoreBridge, model: &mut UiModel) {
    if let Err(e) = bridge.request("action", json!({"name":"closeStorage","args":{}})) {
        model.notice(e.to_string());
    }
}
fn open_trunk(
    bridge: Res<CoreBridge>,
    focus: Res<InteractionFocus>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    ui: Res<UiState>,
    bodies: Query<&crate::vehicle::VehicleBody>,
    mut model: ResMut<UiModel>,
    mut life: ResMut<Lifecycle>,
) {
    if ui.captures_input() || !settings.just_pressed(GameAction::Interact, &keys, &mouse) {
        return;
    }
    let Some(id) = bodies
        .iter()
        .map(|b| b.net_id)
        .find(|id| focus.selected("openVehicleTrunk", *id))
    else {
        return;
    };
    match request(&bridge, id, Pending::Trunk) {
        Ok(true) => life.pending = Some(Pending::Trunk),
        Err(e) => model.notice(e.to_string()),
        _ => {}
    }
}
fn adopt(
    bridge: Res<CoreBridge>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    mut model: ResMut<UiModel>,
    mut ui: ResMut<UiState>,
    mut life: ResMut<Lifecycle>,
    mut scrolls: ResMut<Scrolls>,
) {
    if epoch.0 != life.epoch || !model.connected {
        *life = Lifecycle {
            epoch: epoch.0,
            previous: ui.screen,
            ..default()
        };
        *scrolls = default();
        if !model.connected {
            return;
        }
    }
    if life.previous == Screen::Inventory && ui.screen != Screen::Inventory {
        close(&bridge, &mut model);
        // Keep the pending cabin marker until its accepted reply arrives. A
        // late reply must be closed, not reopen a dashboard the owner put away.
    }
    if model.storage.revision != life.revision {
        life.revision = model.storage.revision;
        if model.storage.net_id != 0 {
            if life.pending == Some(Pending::Cabin) && ui.screen != Screen::Inventory {
                close(&bridge, &mut model);
            } else {
                ui.screen = Screen::Inventory;
                ui.selected_item = None;
                *scrolls = default();
            }
        }
        life.pending = None;
    }
    if life.previous != Screen::Inventory
        && ui.screen == Screen::Inventory
        && model.storage.net_id == 0
    {
        let vehicle = snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) as u32;
        if vehicle != 0 {
            match request(&bridge, vehicle, Pending::Cabin) {
                Ok(true) => life.pending = Some(Pending::Cabin),
                Err(e) => model.notice(e.to_string()),
                _ => {}
            }
        }
    }
    life.previous = ui.screen;
}

pub(super) fn container_pages(model: &UiModel) -> Vec<(u8, String)> {
    let mut result = vec![
        (9, "Freezer".into()),
        (
            7,
            if model.storage.label.is_empty() {
                "Storage".into()
            } else {
                model.storage.label.clone()
            },
        ),
    ];
    for index in 0..12u8 {
        let page = 10 + index;
        let name = model
            .storage
            .compartments
            .iter()
            .find(|c| c.page == page)
            .map(|c| c.label.as_str())
            .filter(|s| !s.is_empty());
        result.push((
            page,
            name.map(str::to_owned)
                .unwrap_or_else(|| format!("Compartment {}", index + 1)),
        ));
    }
    result.push((8, "Nearby".into()));
    result.retain(|(index, _)| {
        *index == 8
            || model
                .pages
                .iter()
                .any(|p| p.index == *index && p.width > 0 && p.height > 0)
    });
    result
}
#[derive(Component)]
struct Column(usize);
pub(super) fn columns(
    panel: &mut ChildSpawnerCommands,
    state: &UiState,
    model: &UiModel,
    assets: &AssetServer,
) {
    panel
        .spawn((Node {
            width: percent(100),
            height: px(310),
            flex_shrink: 0.,
            column_gap: px(10),
            ..default()
        },))
        .with_children(|row| {
            for right in [false, true] {
                let pages: Vec<_> = if right {
                    container_pages(model)
                } else {
                    model
                        .pages
                        .iter()
                        .filter(|p| p.index < 7 && p.width > 0 && p.height > 0)
                        .map(|p| (p.index, page_name(p.index)))
                        .collect()
                };
                row.spawn((
                    Column(usize::from(right)),
                    ScrollPosition::default(),
                    Node {
                        width: percent(50),
                        height: percent(100),
                        overflow: Overflow::scroll_y(),
                        flex_direction: FlexDirection::Column,
                        row_gap: px(10),
                        ..default()
                    },
                ))
                .with_children(|column| {
                    for (index, title) in pages {
                        column.spawn((
                            text(title, 18., ACCENT),
                            Node {
                                flex_shrink: 0.,
                                ..default()
                            },
                        ));
                        if let Some(page) = model
                            .pages
                            .iter()
                            .find(|p| p.index == index && p.width > 0 && p.height > 0)
                        {
                            let cell = (360. / f32::from(page.width)).min(40.);
                            inventory_grid(column, page, state, assets, cell);
                        }
                    }
                });
            }
        });
}
fn scroll(
    ui: Res<UiState>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut wheel: MessageReader<MouseWheel>,
    mut columns: Query<(
        Ref<Column>,
        &ComputedNode,
        &UiGlobalTransform,
        &mut ScrollPosition,
    )>,
    mut saved: ResMut<Scrolls>,
) {
    if ui.screen != Screen::Inventory {
        wheel.clear();
        return;
    }
    let delta: f32 = wheel
        .read()
        .map(|w| {
            w.y * if w.unit == bevy::input::mouse::MouseScrollUnit::Line {
                24.
            } else {
                1.
            }
        })
        .sum();
    let cursor = windows
        .single()
        .ok()
        .and_then(Window::physical_cursor_position);
    for (column, node, transform, mut position) in &mut columns {
        if column.is_added() {
            position.y = saved.0[column.0];
        }
        if cursor.is_some_and(|p| node.contains_point(*transform, p)) {
            position.y = (position.y - delta).clamp(
                0.,
                (node.content_size().y - node.size().y).max(0.) * node.inverse_scale_factor(),
            );
            saved.0[column.0] = position.y;
        }
    }
}

#[cfg(test)]
mod tests;
