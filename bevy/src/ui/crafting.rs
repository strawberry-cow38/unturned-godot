//! Original CraftingMenu category grid/detail/queue. Browsing is projected from
//! accepted owner inventory/knowledge; craft and cancel remain server intents.
use super::*;
use crate::{
    bridge::CoreBridge,
    player::{GameSnapshot, WorldSessionEpoch},
};
use bevy::picking::{
    events::{PointerMove, PointerPress, PointerRelease},
    hover::PointerCaptureMap,
    pointer::{PointerButton, PointerId},
};
use bevy::{
    input::mouse::{MouseScrollUnit, MouseWheel},
    picking::Pickable,
    window::PrimaryWindow,
};
use serde::Serialize;

mod render;
mod search;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod visual;

#[derive(Clone, Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct Browse {
    category: String,
    search: String,
    lookup: String,
    item_id: u16,
    selected: i32,
    quantity: i32,
    opening: bool,
}
impl Default for Browse {
    fn default() -> Self {
        Self {
            category: "All".into(),
            search: String::new(),
            lookup: String::new(),
            item_id: 0,
            selected: -1,
            quantity: 1,
            opening: false,
        }
    }
}
#[derive(Resource, Default)]
pub(crate) struct Menu {
    browse: Browse,
    options: Value,
    requested: Option<Browse>,
    epoch: Option<u64>,
    open: bool,
    typing: bool,
    poll: f32,
    scroll: [f32; 3],
    viewport: Vec2,
    click: DoubleClick,
    queue_drag: Option<QueueDrag>,
}
#[derive(Default)]
struct DoubleClick(Option<(f64, Vec2)>);
impl DoubleClick {
    fn press(&mut self, now: f64, position: Vec2) -> bool {
        // Godot4.6 X11's double-click flag survives widget replacement. Keep
        // its400ms/5px press contract independently of a rebuilt entity.
        if self
            .0
            .is_some_and(|(time, at)| now - time < 0.4 && position.distance(at) < 5.)
        {
            self.0 = None;
            true
        } else {
            self.0 = Some((now, position));
            false
        }
    }
}
struct QueueDrag {
    pointer: PointerId,
    x: f32,
    scroll: f32,
    moved: bool,
    released: bool,
    slot: Option<u8>,
    fingerprint: String,
}
impl QueueDrag {
    fn motion(&mut self, x: f32, scale: f32) -> Option<f32> {
        let dx = x - self.x;
        if dx.abs() > 6. * scale {
            self.moved = true;
        }
        self.moved.then_some(self.scroll + dx)
    }
}
impl Menu {
    pub(crate) fn diagnostics(&self) -> Value {
        if !self.open {
            return Value::Null;
        }
        json!({"browse":self.browse,"accepted":self.options,"scroll":self.scroll,
            "viewport":self.viewport.to_array(),"typing":self.typing})
    }
    pub(super) fn typing(&self) -> bool {
        self.open && self.typing
    }
    pub(super) fn lookup(&mut self, uses: bool, id: u16) {
        self.browse.lookup = if uses { "uses" } else { "recipes" }.into();
        self.browse.item_id = id;
        self.browse.search.clear();
        self.browse.selected = -1;
        self.browse.quantity = 1;
        self.options = Value::Null;
        self.requested = None;
        self.typing = false;
        self.scroll = [0.; 3];
    }
    fn queue_fingerprint(&self) -> String {
        format!("{}|{}", self.options["tick"], self.options["queue"])
    }
    fn retain_queue_widgets(&self) -> bool {
        self.queue_drag
            .as_ref()
            .is_some_and(|drag| drag.fingerprint == self.queue_fingerprint())
    }
}
#[derive(Component, Clone)]
enum Action {
    Category(String),
    Select(i32),
    Quantity(i32),
    Craft,
    Cancel(u8),
    Clear,
}
#[derive(Component)]
struct Progress {
    left: f32,
    total: f32,
    tick: u64,
}

pub(super) struct CraftingPlugin;
impl Plugin for CraftingPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Menu>()
            .init_resource::<crate::character::nameplate::OriginalFont>()
            .add_plugins(search::SearchPlugin)
            .add_observer(activate)
            .add_observer(pointer_press)
            .add_observer(pointer_move)
            .add_observer(pointer_release)
            .add_systems(Update, lookup_input.in_set(UiUpdate).before(keyboard_ui))
            .add_systems(
                Update,
                (
                    input,
                    refresh,
                    render::build,
                    queue_scroll,
                    progress,
                    render::tooltip,
                )
                    .chain()
                    .in_set(UiUpdate)
                    .after(keyboard_ui)
                    .before(rebuild_overlay),
            );
    }
}
fn s(value: &Value, key: &str) -> String {
    value[key].as_str().unwrap_or("").into()
}
fn i(value: &Value, key: &str) -> i32 {
    value[key].as_i64().unwrap_or(0) as i32
}
fn f(value: &Value, key: &str) -> f32 {
    value[key].as_f64().unwrap_or(0.) as f32
}
fn actions(menu: &Menu) -> Vec<UiIntent> {
    let selected = &menu.options["view"]["selected"];
    if selected["craftable"] != true || i(selected, "index") != menu.browse.selected {
        return vec![];
    }
    (0..i(selected, "quantity").clamp(1, 65535))
        .map(|_| UiIntent::Action {
            name: "craft".into(),
            args: json!({"index": menu.browse.selected}),
        })
        .collect()
}
fn activate(
    trigger: On<Activate>,
    buttons: Query<&Action>,
    ui: Res<UiState>,
    mut menu: ResMut<Menu>,
    mut intents: MessageWriter<UiIntent>,
) {
    if ui.screen != Screen::Crafting {
        return;
    }
    let Ok(action) = buttons.get(trigger.entity) else {
        return;
    };
    menu.typing = false;
    match action {
        Action::Category(category) => {
            menu.browse.category = category.clone();
            menu.browse.search.clear();
            menu.browse.lookup.clear();
            menu.browse.selected = -1;
            menu.scroll[1] = 0.;
        }
        Action::Select(index) => {
            menu.browse.selected = *index;
            menu.browse.quantity = 1;
        }
        Action::Quantity(delta) => {
            menu.browse.quantity = (menu.browse.quantity + delta).clamp(
                1,
                i(&menu.options["view"]["selected"], "maxQuantity").max(1),
            );
        }
        Action::Craft => {
            for intent in actions(&menu) {
                intents.write(intent);
            }
            menu.browse.quantity = 1;
        }
        Action::Cancel(slot) => {
            // Physical releases are resolved by the queue row's X-only hit
            // contract. ButtonPlugin may also emit Activate; never send twice.
            if menu.queue_drag.is_none() {
                intents.write(UiIntent::Action {
                    name: "craftCancel".into(),
                    args: json!({"slot":slot}),
                });
            }
        }
        Action::Clear => {
            if !menu.browse.lookup.is_empty() {
                menu.browse.category = "All".into();
            }
            menu.browse.search.clear();
            menu.browse.lookup.clear();
            menu.browse.selected = -1;
            menu.scroll[1] = 0.;
        }
    }
    menu.poll = 0.;
}
fn pointer_press(
    mut event: On<PointerPress>,
    buttons: Query<&Action>,
    areas: Query<(), With<render::QueueArea>>,
    ui: Res<UiState>,
    mut menu: ResMut<Menu>,
    time: Res<Time<Real>>,
    mut captures: ResMut<PointerCaptureMap>,
    mut intents: MessageWriter<UiIntent>,
) {
    if ui.screen != Screen::Crafting || event.button != PointerButton::Primary {
        return;
    }
    let action = buttons.get(event.entity).ok();
    let queue = matches!(action, Some(Action::Cancel(_))) || areas.contains(event.entity);
    if action.is_none() && !queue {
        return;
    }
    event.propagate(false);
    let twice = menu
        .click
        .press(time.elapsed_secs_f64(), event.pointer.position);
    match action {
        Some(Action::Select(index)) => {
            if twice {
                menu.browse.selected = *index;
                menu.browse.quantity = 1;
                if menu.options["view"]["tiles"]
                    .as_array()
                    .is_some_and(|tiles| {
                        tiles
                            .iter()
                            .any(|tile| i(tile, "index") == *index && tile["craftable"] == true)
                    })
                {
                    intents.write(UiIntent::Action {
                        name: "craft".into(),
                        args: json!({"index":index}),
                    });
                    menu.poll = 0.;
                }
            }
        }
        _ => {}
    }
    if queue {
        let layout = render::Layout::new(menu.viewport);
        let slot = if let Some(Action::Cancel(slot)) = action {
            Some(*slot)
        } else {
            menu.options["queue"].as_array().and_then(|jobs| {
                jobs.iter().enumerate().find_map(|(index, job)| {
                    let left = layout.queue_row.max.x - (index + 1) as f32 * 92. * layout.scale
                        + menu.scroll[2];
                    (event.pointer.position.x >= left
                        && event.pointer.position.x <= left + 84. * layout.scale)
                        .then_some(i(job, "slot") as u8)
                })
            })
        };
        menu.queue_drag = Some(QueueDrag {
            pointer: event.pointer.id,
            x: event.pointer.position.x,
            scroll: menu.scroll[2],
            moved: false,
            released: false,
            slot,
            fingerprint: menu.queue_fingerprint(),
        });
        captures.capture(event.pointer.id, event.entity, event.hit.clone());
    }
}
fn pointer_move(event: On<PointerMove>, ui: Res<UiState>, mut menu: ResMut<Menu>) {
    if ui.screen != Screen::Crafting {
        return;
    }
    let scale = render::Layout::new(menu.viewport).scale;
    if let Some(scroll) = menu
        .queue_drag
        .as_mut()
        .filter(|drag| drag.pointer == event.pointer.id)
        .and_then(|drag| drag.motion(event.pointer.position.x, scale))
    {
        menu.scroll[2] = scroll.max(0.);
    }
}
fn pointer_release(
    mut event: On<PointerRelease>,
    ui: Res<UiState>,
    mut menu: ResMut<Menu>,
    mut intents: MessageWriter<UiIntent>,
) {
    if ui.screen != Screen::Crafting || event.button != PointerButton::Primary {
        return;
    }
    let fingerprint = menu.queue_fingerprint();
    if let Some(drag) = menu
        .queue_drag
        .as_mut()
        .filter(|d| d.pointer == event.pointer.id && !d.released)
    {
        event.propagate(false);
        drag.released = true;
        if !drag.moved
            && drag.fingerprint == fingerprint
            && let Some(slot) = drag.slot
        {
            intents.write(UiIntent::Action {
                name: "craftCancel".into(),
                args: json!({"slot":slot}),
            });
            menu.poll = 0.;
        }
    }
}
fn queue_scroll(
    mut menu: ResMut<Menu>,
    mut strips: Query<&mut UiTransform, With<render::QueueStrip>>,
) {
    let layout = render::Layout::new(menu.viewport);
    let count = menu.options["queue"].as_array().map_or(0, Vec::len);
    menu.scroll[2] = menu.scroll[2].clamp(
        0.,
        (count as f32 * 92. * layout.scale - layout.queue_row.width()).max(0.),
    );
    for mut strip in &mut strips {
        strip.translation.x = px(menu.scroll[2]);
    }
}
fn lookup_input(
    mut ui: ResMut<UiState>,
    mut menu: ResMut<Menu>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    buttons: Query<(&UiButton, &Hovered)>,
) {
    if ui.screen != Screen::Inventory || ui.selected_item.is_some() {
        return;
    }
    let uses = settings.just_pressed(GameAction::ItemUses, &keys, &mouse);
    if !uses && !settings.just_pressed(GameAction::ItemRecipes, &keys, &mouse) {
        return;
    }
    let item = buttons.iter().find_map(|(action, hover)| {
        let UiButton::Select(page, x, y) = action else {
            return None;
        };
        if !hover.get() {
            return None;
        }
        model
            .pages
            .iter()
            .find(|p| p.index == *page)?
            .items
            .iter()
            .find(|i| i.x == *x && i.y == *y)
            .map(|i| i.id)
    });
    if let Some(id) = item {
        menu.lookup(uses, id);
        ui.screen = Screen::Crafting;
    }
}
fn input(
    state: Res<UiState>,
    model: Res<UiModel>,
    epoch: Res<WorldSessionEpoch>,
    mut menu: ResMut<Menu>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut wheel: MessageReader<MouseWheel>,
    roots: Query<&UiTransform, With<render::Root>>,
    mouse: Res<ButtonInput<MouseButton>>,
) {
    if menu.epoch != Some(epoch.0) || !model.connected {
        *menu = Menu {
            epoch: Some(epoch.0),
            ..default()
        };
    }
    let open = state.screen == Screen::Crafting;
    if open && !menu.open {
        // Source Open preserves valid category/search/selection; it resets only
        // the temporary lookup and queue offset. lookup_input sets its lookup
        // after the logical Open, before this update consumes it.
        menu.browse.opening = true;
        menu.poll = 0.;
        menu.requested = None;
        menu.scroll[2] = 0.;
    }
    if !open {
        menu.typing = false;
        menu.queue_drag = None;
        if menu.open {
            menu.browse.lookup.clear();
        }
    }
    menu.open = open;
    if !open {
        wheel.clear();
        return;
    }
    if !mouse.pressed(MouseButton::Left) {
        menu.queue_drag = None;
    }
    let Ok(window) = windows.single() else {
        return;
    };
    menu.viewport = Vec2::new(window.width(), window.height());
    let layout = render::Layout::new(menu.viewport);
    let offset = roots.iter().next().map_or(Vec2::ZERO, |t| {
        Vec2::new(
            if let Val::Px(value) = t.translation.x {
                value
            } else {
                0.
            },
            if let Val::Px(value) = t.translation.y {
                value
            } else {
                0.
            },
        )
    });
    if let Some(cursor) = window.cursor_position().map(|p| p - offset) {
        for event in wheel.read() {
            let delta = event.y
                * if event.unit == MouseScrollUnit::Line {
                    42.
                } else {
                    1.
                };
            let index = if layout.categories.contains(cursor) {
                Some(0)
            } else if layout.grid.contains(cursor) {
                Some(1)
            } else if layout.queue.contains(cursor) {
                Some(2)
            } else {
                None
            };
            if let Some(index) = index {
                menu.scroll[index] = (menu.scroll[index] - delta).max(0.);
            }
        }
    } else {
        wheel.clear();
    }
}
fn refresh(
    state: Res<UiState>,
    bridge: Res<CoreBridge>,
    time: Res<Time>,
    mut menu: ResMut<Menu>,
    mut model: ResMut<UiModel>,
) {
    if state.screen != Screen::Crafting || !model.connected {
        return;
    }
    menu.poll -= time.delta_secs();
    if menu.requested.as_ref() == Some(&menu.browse) && menu.poll > 0. {
        return;
    }
    menu.poll = 0.15;
    match bridge.request("craftingView", serde_json::to_value(&menu.browse).unwrap()) {
        Ok(reply) => {
            menu.options = reply["result"].clone();
            menu.requested = Some(menu.browse.clone());
            menu.browse.category = s(&menu.options["view"], "category");
            menu.browse.opening = false;
            if !menu.options["view"]["selected"].is_null() {
                menu.browse.quantity = i(&menu.options["view"]["selected"], "quantity");
                menu.browse.selected = i(&menu.options["view"]["selected"], "index");
            } else {
                menu.browse.selected = -1;
            }
        }
        Err(error) => {
            menu.poll = 1.;
            model.notice(format!("Crafting: {error}"));
        }
    }
}
fn progress(snapshot: Res<GameSnapshot>, mut bars: Query<(&Progress, &mut Node)>) {
    let tick = snapshot.0["tick"].as_u64().unwrap_or(0);
    for (job, mut node) in &mut bars {
        let left = (job.left - tick.saturating_sub(job.tick) as f32 * 0.02).max(0.);
        node.width = percent((1. - left / job.total.max(0.01)).clamp(0., 1.) * 100.);
    }
}
