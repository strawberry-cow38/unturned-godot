//! InventoryUI's local interaction state. Every shared-state change is emitted
//! as an existing accepted command; a drag never moves the displayed replica.
use super::{
    api, commands,
    gesture::{Edge, Gesture},
    input, layout, magazines,
    model::*,
    transfer,
};
use crate::ui::{
    InventoryItem, InventoryPage, Screen, UiIntent, UiModel, UiState, input_order::FrameInput,
};
use crate::{
    bridge::CoreBridge,
    player::{GameSnapshot, WorldSessionEpoch},
    settings::{GameAction, Settings},
};
use bevy::{prelude::*, window::PrimaryWindow};
use serde_json::json;
use std::collections::HashSet;

#[derive(Resource, Default)]
pub(super) struct Menu {
    pub view: api::View,
    pub pages: Vec<InventoryPage>,
    pub gesture: Gesture,
    pub hits: HitLayout,
    pub scroll: layout::Scroll,
    pub folded: HashSet<u8>,
    pub magazines: magazines::Magazines,
    pub transfers: transfer::Transfers,
    pub viewport: Vec2,
    pub open: bool,
    pub revision: u64,
    pub epoch: u64,
    pub pending_equip: Option<commands::PendingEquip>,
    pub pending_selected: Option<u64>,
    pub bindings: [Option<(Cell, u16)>; 8],
    pub notice: String,
    pub round_sounds: Vec<i32>,
    pub scrolling: Option<super::scrollbar::Drag>,
    pub offset: Vec2,
    pub typing: bool,
    pub spin: super::spin::State,
    pub load_hint: Option<(Cell, api::Load)>,
    load_request: Option<(Cell, Cell)>,
    pub pending_actions: Vec<Action>,
    poll: f32,
    event_seq: u64,
    // A source timed transfer mutates immediately. Across the accepted wire,
    // reserve that identity until the replica changes or H is released.
    transferred: Vec<(api::Address, u16)>,
}
#[derive(Component, Clone, Debug)]
pub(super) enum Action {
    Fold(u8),
    Context(String),
    Cooker,
    CloseContext,
    Split(u16),
}
impl Menu {
    pub fn dirty(&mut self) {
        self.revision = self.revision.wrapping_add(1);
    }
    pub fn worn(&self) -> [Option<InventoryItem>; 7] {
        std::array::from_fn(|slot| {
            self.view
                .tiles
                .worn
                .iter()
                .find(|w| w.slot == slot as u8)
                .and_then(|w| w.tile.as_ref())
                .map(|t| InventoryItem {
                    id: t.id,
                    name: t.name.clone(),
                    width: t.width,
                    height: t.height,
                    amount: t.amount,
                    quality: t.quality,
                    icon: t.icon.clone(),
                    ..default()
                })
        })
    }
    pub fn dispatch(&mut self, out: commands::Dispatch, intents: &mut MessageWriter<UiIntent>) {
        intents.write_batch(out.commands);
        if out.select_token.is_some() {
            self.pending_selected = out.select_token;
        }
        if out.pending.is_some() {
            self.pending_equip = out.pending;
        }
        self.poll = 0.;
    }
    fn intent(
        &mut self,
        intent: Intent,
        bridge: &CoreBridge,
        craft: &mut crate::ui::crafting::Menu,
        ui: &mut UiState,
        intents: &mut MessageWriter<UiIntent>,
    ) {
        match intent {
            Intent::Lookup { id, uses } => {
                craft.lookup(uses, id);
                ui.screen = Screen::Crafting;
            }
            Intent::Bind { from, hotbar } => {
                if let Some(item) = at(&self.pages, from) {
                    if from.page < OWN_PAGES {
                        self.bindings[usize::from(hotbar - 3)] = Some((from, item.id));
                    }
                }
            }
            Intent::LoadOrMove { from, to, rotation } => {
                match bridge.request(
                    "inventoryView",
                    json!({"loadFrom":commands::cell_args(from),"loadTo":commands::cell_args(to)}),
                ) {
                    Ok(reply) => {
                        let load = serde_json::from_value::<api::View>(reply["result"].clone())
                            .ok()
                            .and_then(|v| v.load);
                        if let Some(load) = load.filter(|l| l.consumed) {
                            if !load.allowed {
                                self.notice = load.message;
                            } else if let Some(row) = at(&self.pages, to)
                                .and_then(|item| self.view.at(Cell::item(to.page, item)))
                            {
                                if !self.magazines.load(row, &load) {
                                    self.notice =
                                        "Take the magazine into your inventory first.".into();
                                }
                            }
                        } else {
                            self.dispatch(
                                commands::dispatch(
                                    Intent::Move { from, to, rotation },
                                    &self.view,
                                    &self.pages,
                                ),
                                intents,
                            );
                        }
                    }
                    Err(e) => self.notice = e.to_string(),
                }
            }
            other => self.dispatch(commands::dispatch(other, &self.view, &self.pages), intents),
        }
    }
    fn context(&mut self, action: Action, intents: &mut MessageWriter<UiIntent>) {
        match action {
            Action::Fold(page) => {
                if !self.folded.remove(&page) {
                    self.folded.insert(page);
                }
            }
            Action::CloseContext => self.gesture.selected = None,
            Action::Split(amount) => self.gesture.split_amount = amount,
            Action::Cooker => {
                if let Some(cooker) = &self.view.cooker {
                    intents.write(commands::action(
                        "setCookerOn",
                        json!({"netId":cooker.net_id,"on":!cooker.on}),
                    ));
                    self.poll = 0.;
                }
            }
            Action::Context(action) => {
                if let Some(cell) = self.gesture.selected {
                    if let Some(row) = self.view.at(cell) {
                        if let Some(reason) = commands::context_refusal(&action, row) {
                            self.notice = reason.into();
                        } else if action == "unload" {
                            if !self.magazines.unload(&self.view, cell) {
                                self.notice = "Take the magazine into your inventory first.".into();
                            }
                        } else {
                            let out = commands::context(&action, row, &self.view, &self.pages);
                            self.dispatch(out, intents);
                        }
                    }
                    if action != "autoDrink" {
                        self.gesture.selected = None;
                    }
                }
            }
        }
        self.dirty();
    }
}
pub(super) fn activate(
    trigger: On<bevy::ui_widgets::Activate>,
    buttons: Query<&Action>,
    ui: Res<UiState>,
    mut menu: ResMut<Menu>,
) {
    if ui.screen == Screen::Inventory
        && let Ok(action) = buttons.get(trigger.entity)
    {
        menu.pending_actions.push(action.clone());
    }
}
#[allow(clippy::too_many_arguments)]
pub(super) fn update(
    bridge: Res<CoreBridge>,
    time: Res<Time>,
    epoch: Res<WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    mut ui: ResMut<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    frame: Res<FrameInput>,
    windows: Query<(Entity, &Window), With<PrimaryWindow>>,
    roots: Query<(&UiTransform, &crate::ui::swoop::Swoop)>,
    mut menu: ResMut<Menu>,
    mut craft: ResMut<crate::ui::crafting::Menu>,
    mut intents: MessageWriter<UiIntent>,
    mut portrait: (
        ResMut<super::paperdoll::Portrait>,
        ResMut<super::paperdoll::stage::Surface>,
    ),
    eye: (
        Option<Res<crate::player::CraftingEye>>,
        Option<Res<crate::world::origin::WorldOrigin>>,
    ),
) {
    if menu.epoch != epoch.0 || !model.connected {
        *menu = Menu {
            epoch: epoch.0,
            ..default()
        };
    }
    let open = ui.screen == Screen::Inventory && model.connected && model.alive;
    if open != menu.open {
        menu.open = open;
        menu.poll = 0.;
        menu.dirty();
        if !open {
            menu.gesture.close();
            menu.transfers.clear();
            menu.pending_actions.clear();
            menu.pending_equip = None;
            menu.pending_selected = None;
            menu.scrolling = None;
            menu.spin.cancel();
        } else {
            menu.scroll = default();
            menu.notice.clear();
        }
    }
    // Source keeps the independent viewport drawing through the closing swoop.
    portrait.0.active = open
        || (model.connected && model.alive && roots.iter().any(|(_, s)| s.0 == Screen::Inventory));
    if let Some(events) = snapshot.0["events"].as_array() {
        for event in events {
            let seq = event["seq"].as_u64().unwrap_or(0);
            if seq <= menu.event_seq {
                continue;
            }
            menu.event_seq = seq;
            if event["kind"] == "magazineTransfer"
                && let Ok(fact) = serde_json::from_value(event["data"].clone())
            {
                let view = menu.view.clone();
                let count = menu.magazines.operations.len();
                if let Some(capacity) = menu.magazines.receive(&fact, &view) {
                    menu.round_sounds.push(capacity);
                    menu.poll = 0.;
                    menu.dirty();
                }
                if menu.magazines.operations.len() != count {
                    menu.dirty();
                }
            }
        }
    }
    if !open {
        return;
    }
    let Ok((window_entity, window)) = windows.single() else {
        return;
    };
    let viewport = Vec2::new(window.width(), window.height());
    if viewport != menu.viewport {
        menu.viewport = viewport;
        menu.dirty();
    }
    let dashboard = layout::Dashboard::new(viewport);
    portrait.1.size = dashboard.paperdoll.size().as_uvec2().max(UVec2::ONE);
    menu.poll -= time.delta_secs();
    if menu.poll <= 0. {
        menu.poll = 0.1;
        // Read both projections from one accepted owner state. There is no Tick
        // in this request, so cells/tokens and the replica's pages stay coherent.
        let mut request = json!({"includeState":true,"includeRecipes":false});
        if let Some(position) = eye
            .0
            .as_ref()
            .zip(eye.1.as_ref())
            .and_then(|(eye, origin)| eye.request(origin, &epoch))
        {
            request["eye"] = json!(position);
        }
        match bridge.request("inventoryView", request) {
            Ok(reply) => {
                if let (Ok(view), Ok(pages)) = (
                    serde_json::from_value::<api::View>(reply["result"].clone()),
                    serde_json::from_value::<Vec<InventoryPage>>(
                        reply["state"]["inventory"]["pages"].clone(),
                    ),
                ) {
                    let changed = menu.view.tiles != view.tiles
                        || menu.view.items != view.items
                        || menu.view.cooker != view.cooker
                        || menu.view.quick_crafts != view.quick_crafts
                        || menu.pages != pages;
                    menu.view = view;
                    menu.pages = pages;
                    if changed {
                        menu.load_request = None;
                        menu.dirty();
                    }
                    if menu.pending_selected.is_some_and(|token| {
                        menu.view
                            .items
                            .iter()
                            .any(|row| row.address.token == token && row.hand_action == "Dequip")
                    }) {
                        menu.pending_selected = None;
                        ui.screen = Screen::Playing;
                    }
                    if menu
                        .gesture
                        .selected
                        .is_some_and(|cell| menu.view.at(cell).is_none())
                    {
                        menu.gesture.selected = None;
                    }
                    if let Some(intent) = menu
                        .pending_equip
                        .as_ref()
                        .and_then(|p| p.accepted(&menu.view))
                    {
                        menu.pending_selected = Some(
                            menu.view
                                .at(menu.pending_equip.as_ref().unwrap().cell)
                                .unwrap()
                                .address
                                .token,
                        );
                        intents.write(intent);
                        menu.pending_equip = None;
                    }
                }
            }
            Err(e) => menu.notice = e.to_string(),
        }
    }
    for action in std::mem::take(&mut menu.pending_actions) {
        menu.context(action, &mut intents);
    }
    let offset =
        roots
            .iter()
            .find(|(_, s)| s.0 == Screen::Inventory)
            .map_or(Vec2::ZERO, |(t, _)| {
                Vec2::new(
                    if let Val::Px(x) = t.translation.x {
                        x
                    } else {
                        0.
                    },
                    if let Val::Px(y) = t.translation.y {
                        y
                    } else {
                        0.
                    },
                )
            });
    menu.offset = offset;
    for event in &frame.events {
        if menu.typing && matches!(event.event, bevy::window::WindowEvent::KeyboardInput(_)) {
            continue;
        }
        let Some(input) = input::map(
            event,
            window_entity,
            menu.gesture.carry.is_some(),
            &settings,
        ) else {
            continue;
        };
        if let Some(field) = menu.hits.number {
            let maximum = menu
                .gesture
                .selected
                .and_then(|cell| menu.view.tile(cell))
                .map_or(1, |tile| tile.amount.saturating_sub(1));
            let editing = menu.typing;
            let mut amount = menu.gesture.split_amount;
            if menu
                .spin
                .input(&input, field, offset, editing, &mut amount, maximum)
            {
                menu.gesture.split_amount = amount;
                continue;
            }
        }
        if super::scrollbar::input(&mut menu, &input, offset, &dashboard) {
            continue;
        }
        match input {
            input::Input::Cancel => {
                menu.gesture.close();
                menu.dirty();
            }
            input::Input::Wheel(point, notches) => {
                if menu.scroll.wheel(&dashboard, point - offset, notches) {
                    menu.dirty();
                }
            }
            input::Input::Gesture(edge, point) => {
                let pages = menu.pages.clone();
                let worn = menu.worn();
                let view = menu.view.clone();
                let hits = std::mem::take(&mut menu.hits);
                let old = menu.gesture.selected;
                let actions =
                    menu.gesture
                        .input(edge, point - offset, &hits, &pages, &worn, |id| {
                            view.rules(id)
                        });
                menu.hits = hits;
                if menu.gesture.selected != old
                    || matches!(edge, Edge::Left { .. } | Edge::Right { .. } | Edge::Rotate)
                {
                    menu.dirty();
                }
                for action in actions {
                    menu.intent(action, &bridge, &mut craft, &mut ui, &mut intents);
                }
            }
        }
        if ui.screen != Screen::Inventory {
            break;
        }
    }
    if let Some(field) = menu.hits.number {
        let maximum = menu
            .gesture
            .selected
            .and_then(|cell| menu.view.tile(cell))
            .map_or(1, |tile| tile.amount.saturating_sub(1));
        let mut amount = menu.gesture.split_amount;
        menu.spin
            .step(time.delta_secs(), field, &mut amount, maximum);
        menu.gesture.split_amount = amount;
    } else {
        menu.spin.cancel();
    }
    portrait.0.yaw = menu.gesture.paperdoll_yaw;
    let load_pair = menu.gesture.carry.as_ref().and_then(|carry| {
        let Source::Grid(from) = carry.source else {
            return None;
        };
        if !menu.view.at(from).is_some_and(|row| row.loose_ammo) {
            return None;
        }
        let (hover, _) = menu.hits.cell(menu.gesture.pointer)?;
        let to = Cell::item(hover.page, at(&menu.pages, hover)?);
        menu.view
            .at(to)
            .is_some_and(|row| row.magazine)
            .then_some((from, to))
    });
    if load_pair != menu.load_request {
        menu.load_request = load_pair;
        menu.load_hint = None;
        if let Some((from, to)) = load_pair {
            if let Ok(reply) = bridge.request(
                "inventoryView",
                json!({"loadFrom":commands::cell_args(from),"loadTo":commands::cell_args(to)}),
            ) {
                menu.load_hint = serde_json::from_value::<api::View>(reply["result"].clone())
                    .ok()
                    .and_then(|v| v.load)
                    .map(|load| (to, load));
            }
        }
        menu.dirty();
    }
    let view = menu.view.clone();
    let count = menu.magazines.operations.len();
    let requests = menu.magazines.step(true, time.delta_secs(), &view);
    if !requests.is_empty() || count != menu.magazines.operations.len() {
        menu.dirty();
    }
    intents.write_batch(requests);
    let held = frame
        .current(window_entity)
        .is_some_and(|b| settings.pressed(GameAction::QuickTransfer, &b.physical, &b.mouse));
    if !held {
        menu.transferred.clear();
    }
    menu.transferred.retain(|(address, amount)| {
        view.at(address.cell())
            .is_some_and(|row| row.address == *address)
            && view
                .tile(address.cell())
                .is_some_and(|tile| tile.amount == *amount)
    });
    let hover = menu
        .hits
        .cell(menu.gesture.pointer)
        .and_then(|(cell, grid)| {
            at(&menu.pages, cell).map(|item| (Cell::item(cell.page, item), grid.slot))
        })
        .filter(|(cell, _)| {
            !menu
                .transferred
                .iter()
                .any(|(address, _)| address.cell() == *cell)
        });
    let pages = menu.pages.clone();
    let selected = menu.gesture.selected.is_some();
    let carry = menu.gesture.carry.as_ref().map(|c| c.source);
    let before = menu.transfers.queue.clone();
    if let Some(intent) =
        menu.transfers
            .step(&pages, time.delta_secs(), selected, held, hover, carry)
    {
        if let Intent::QuickTransfer(cell) = intent
            && let (Some(row), Some(tile)) = (view.at(cell), view.tile(cell))
        {
            menu.transferred.push((row.address, tile.amount));
        }
        menu.intent(intent, &bridge, &mut craft, &mut ui, &mut intents);
        menu.dirty();
    }
    if menu.transfers.queue != before {
        menu.dirty();
    }
}
