//! DialogueUI, TradeUI, QuestLogPanel and QuestTracker. The managed read model
//! evaluates the original rules against accepted replicas; native widgets emit intents only.
use super::*;
use crate::{
    bridge::CoreBridge,
    entities::npcs::CurrentNpcSpeaker,
    player::{GameSnapshot, WorldSessionEpoch},
};
use bevy::{
    input::mouse::{MouseScrollUnit, MouseWheel},
    picking::hover::HoverMap,
};

pub(super) struct NpcUiPlugin;
impl Plugin for NpcUiPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Menu>()
            .add_systems(
                Update,
                (console, refresh, input, draw, scroll)
                    .chain()
                    .in_set(UiUpdate)
                    .after(keyboard_ui)
                    .before(rebuild_overlay),
            )
            .add_observer(activate);
    }
}

pub(crate) fn is_console_command(text: &str) -> bool {
    text.split_whitespace().next().is_some_and(|word| {
        word.eq_ignore_ascii_case("trade") || word.eq_ignore_ascii_case("tradepick")
    })
}

fn console(
    mut events: MessageReader<UiIntent>,
    bridge: Option<Res<CoreBridge>>,
    mut menu: ResMut<Menu>,
    mut state: ResMut<UiState>,
    mut model: ResMut<UiModel>,
) {
    for intent in events.read() {
        let UiIntent::Action { name, args } = intent else {
            continue;
        };
        let text = args["text"].as_str().unwrap_or("");
        if name != "console" || !is_console_command(text) {
            continue;
        }
        if !model.connected {
            model.notice("Join a world before opening a vendor");
            continue;
        }
        let mut words = text.split_whitespace();
        let verb = words.next().unwrap_or("");
        let argument = words.collect::<Vec<_>>().join(" ");
        if verb.eq_ignore_ascii_case("tradepick") {
            let index = argument.parse::<u8>().unwrap_or(0);
            if menu
                .view
                .trade
                .as_ref()
                .is_some_and(|v| v.selling.iter().any(|r| r.index == index))
            {
                menu.selection = Some((false, index));
            } else {
                model.notice("No vendor row to select -- open a vendor first");
            }
            continue;
        }
        let Some(bridge) = &bridge else { continue };
        if argument.is_empty() {
            match bridge.request("npcCatalog", json!({})) {
                Ok(reply) => {
                    if let Some(vendors) = reply["result"]["vendors"].as_array() {
                        for vendor in vendors {
                            let key = vendor["key"].as_str().unwrap_or("");
                            model.notice(format!(
                                "{key}: {} for sale, {} wanted",
                                vendor["selling"].as_array().map_or(0, Vec::len),
                                vendor["buying"].as_array().map_or(0, Vec::len)
                            ));
                        }
                    }
                }
                Err(error) => model.notice(error.to_string()),
            }
            continue;
        }
        match bridge.request(
            "npcView",
            json!({"vendor":argument,"pinnedQuest":menu.pinned}),
        ) {
            Ok(reply) => {
                if let Ok(view) = serde_json::from_value::<View>(reply["result"].clone()) {
                    if let Some(vendor) = &view.trade {
                        menu.vendor = vendor.guid.clone();
                        menu.selection = None;
                        menu.poll = 0.2;
                        model.notice(format!(
                            "{}: {} for sale, {} wanted",
                            vendor.name,
                            vendor.selling.len(),
                            vendor.buying.len()
                        ));
                        menu.view = view;
                        state.screen = Screen::Trade;
                    } else {
                        model.notice(format!("No vendor '{argument}' -- try trade for the list"));
                    }
                }
            }
            Err(error) => model.notice(error.to_string()),
        }
    }
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct View {
    revision: u64,
    open_dialogue: u32,
    vendor_revision: u64,
    vendor_opened: String,
    dialogue: Option<Dialogue>,
    direct_vendor: Option<VendorLink>,
    trade: Option<Trade>,
    wallet: i32,
    reputation: i32,
    quests: Vec<Quest>,
    unknown_quests: usize,
    tracker: Option<Quest>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
struct Dialogue {
    id: u32,
    pages: Vec<String>,
    responses: Vec<Response>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct Response {
    index: u8,
    text: String,
    ends_conversation: bool,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
struct VendorLink {
    guid: String,
    name: String,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
struct Trade {
    guid: String,
    key: String,
    name: String,
    description: String,
    selling: Vec<TradeLine>,
    buying: Vec<TradeLine>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct TradeLine {
    index: u8,
    item_id: u16,
    name: String,
    icon: Option<String>,
    cost: i32,
    held: i32,
    vehicle: bool,
    enabled: bool,
    max_count: u16,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
struct Quest {
    id: u32,
    name: String,
    description: String,
    status: String,
    objectives: Vec<Objective>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
struct Objective {
    text: String,
    done: bool,
}
#[derive(Resource, Default, Debug)]
struct Menu {
    view: View,
    page: usize,
    vendor: String,
    selection: Option<(bool, u8)>,
    pinned: u32,
    epoch: u64,
    poll: f32,
    dismissed_revision: Option<u64>,
    message: String,
    scroll: [f32; 3],
}
impl Menu {
    fn adopt(&mut self, view: View, state: &mut UiState) {
        if self.view.open_dialogue != view.open_dialogue {
            self.page = 0;
            self.dismissed_revision = None;
            if view.open_dialogue == 0 {
                self.vendor.clear();
                self.selection = None;
                if matches!(state.screen, Screen::Dialogue | Screen::Trade) {
                    state.screen = Screen::Playing;
                }
            }
        }
        if view.open_dialogue != 0
            && view.dialogue.is_some()
            && view.revision != self.view.revision
            && self.dismissed_revision != Some(view.revision)
            && !matches!(state.screen, Screen::Trade | Screen::Dead)
        {
            state.screen = Screen::Dialogue;
        }
        if view.vendor_revision != self.view.vendor_revision && !view.vendor_opened.is_empty() {
            self.vendor = view.vendor_opened.clone();
            self.selection = None;
            self.poll = 0.;
            state.screen = Screen::Trade;
        }
        self.view = view;
        self.page = self.page.min(
            self.view
                .dialogue
                .as_ref()
                .map_or(0, |d| d.pages.len().saturating_sub(1)),
        );
    }
    fn more_pages(&self) -> bool {
        self.view
            .dialogue
            .as_ref()
            .is_some_and(|d| self.page + 1 < d.pages.len())
    }
    fn close_trade(&mut self, state: &mut UiState) {
        self.vendor.clear();
        self.selection = None;
        state.screen = if self.view.open_dialogue != 0 {
            Screen::Dialogue
        } else {
            Screen::Playing
        };
        self.poll = 0.;
    }
    fn close_dialogue(&mut self, state: &mut UiState, intents: &mut MessageWriter<UiIntent>) {
        intents.write(UiIntent::Action {
            name: "npcClose".into(),
            args: json!({}),
        });
        self.dismissed_revision = Some(self.view.revision);
        self.vendor.clear();
        state.screen = Screen::Playing;
    }
    fn choose(&self, index: u8, intents: &mut MessageWriter<UiIntent>) {
        if self.more_pages() {
            return;
        }
        if let Some(d) = &self.view.dialogue {
            if d.responses.iter().any(|r| r.index == index) {
                intents.write(UiIntent::Action {
                    name: "npcChoose".into(),
                    args: json!({"dialogue":d.id,"response":index}),
                });
            }
        }
    }
    fn trade(&self, all: bool, intents: &mut MessageWriter<UiIntent>) {
        let Some((sell, index)) = self.selection else {
            return;
        };
        let Some(t) = &self.view.trade else { return };
        let rows = if sell { &t.buying } else { &t.selling };
        if let Some(row) = rows.iter().find(|r| r.index == index && r.enabled) {
            let count = if sell && all {
                row.max_count.min(100)
            } else {
                1
            };
            if count == 0 {
                return;
            }
            intents.write(UiIntent::Action {
                name: "npcTrade".into(),
                args: json!({"vendor":t.guid,"sell":sell,"index":index,"count":count}),
            });
        }
    }
}
fn refresh(
    time: Res<Time>,
    bridge: Option<Res<CoreBridge>>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    speaker: Option<Res<CurrentNpcSpeaker>>,
    mut state: ResMut<UiState>,
    mut menu: ResMut<Menu>,
) {
    if menu.epoch != epoch.0 || snapshot.0["connected"] != true {
        if matches!(
            state.screen,
            Screen::Dialogue | Screen::Trade | Screen::Quests
        ) {
            state.screen = Screen::Playing
        }
        *menu = Menu {
            epoch: epoch.0,
            ..default()
        };
        return;
    }
    if snapshot.0["player"]["alive"] == false {
        return;
    }
    // Untouched owners have no quest tracker or conversation. Avoid repeatedly
    // evaluating the rule view until an accepted NPC fact or a panel
    // actually needs it.
    let accepted_revision = snapshot.0["npc"]["revision"].as_u64().unwrap_or(0);
    if accepted_revision == 0
        && menu.view.revision == 0
        && !matches!(
            state.screen,
            Screen::Dialogue | Screen::Trade | Screen::Quests
        )
    {
        return;
    }
    menu.poll -= time.delta_secs();
    let changed = accepted_revision != menu.view.revision;
    if menu.poll > 0. && !changed {
        return;
    }
    menu.poll = 0.2;
    let Some(bridge) = bridge else { return };
    match bridge.request("npcView",json!({"speakerKey":speaker.as_ref().map(|s|s.key.as_str()).unwrap_or(""),"vendor":menu.vendor,"pinnedQuest":menu.pinned})) {
        Ok(reply)=>match serde_json::from_value::<View>(reply["result"].clone()) {
            Ok(view)=>{menu.message.clear();menu.adopt(view,&mut state)},
            Err(error)=>menu.message=format!("NPC presentation: {error}"),
        },
        Err(error)=>menu.message=error.to_string(),
    }
}
fn input(
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    mut state: ResMut<UiState>,
    mut menu: ResMut<Menu>,
    mut intents: MessageWriter<UiIntent>,
) {
    match state.screen {
        Screen::Dialogue => {
            if keys.just_pressed(KeyCode::Escape) {
                menu.close_dialogue(&mut state, &mut intents);
                return;
            }
            if menu.more_pages() {
                if keys.just_pressed(KeyCode::Space)
                    || keys.just_pressed(KeyCode::Enter)
                    || mouse.just_pressed(MouseButton::Left)
                {
                    menu.page += 1
                }
            } else {
                for (n, key) in [
                    KeyCode::Digit1,
                    KeyCode::Digit2,
                    KeyCode::Digit3,
                    KeyCode::Digit4,
                    KeyCode::Digit5,
                    KeyCode::Digit6,
                    KeyCode::Digit7,
                    KeyCode::Digit8,
                    KeyCode::Digit9,
                ]
                .into_iter()
                .enumerate()
                {
                    if keys.just_pressed(key) {
                        if let Some(r) =
                            menu.view.dialogue.as_ref().and_then(|d| d.responses.get(n))
                        {
                            menu.choose(r.index, &mut intents)
                        }
                    }
                }
            }
        }
        Screen::Trade => {
            if keys.just_pressed(KeyCode::Escape) {
                menu.close_trade(&mut state)
            }
        }
        Screen::Quests => {
            if keys.just_pressed(KeyCode::Escape) || keys.just_pressed(KeyCode::Tab) {
                state.screen = Screen::Inventory
            }
        }
        _ => {}
    }
}
#[derive(Component, Clone, Debug)]
enum Action {
    CloseDialogue,
    CloseTrade,
    OpenShop(String),
    Choose(u8),
    Select(bool, u8),
    Trade(bool),
    Pin(u32),
    Inventory,
}
fn activate(
    event: On<Activate>,
    actions: Query<&Action>,
    mut state: ResMut<UiState>,
    mut menu: ResMut<Menu>,
    mut intents: MessageWriter<UiIntent>,
) {
    let Ok(action) = actions.get(event.entity) else {
        return;
    };
    match action {
        Action::CloseDialogue if state.screen == Screen::Dialogue => {
            menu.close_dialogue(&mut state, &mut intents)
        }
        Action::CloseTrade if state.screen == Screen::Trade => menu.close_trade(&mut state),
        Action::OpenShop(guid) if state.screen == Screen::Dialogue => {
            menu.vendor = guid.clone();
            menu.selection = None;
            menu.poll = 0.;
            state.screen = Screen::Trade
        }
        Action::Choose(index) if state.screen == Screen::Dialogue => {
            menu.choose(*index, &mut intents)
        }
        Action::Select(sell, index) if state.screen == Screen::Trade => {
            menu.selection = Some((*sell, *index))
        }
        Action::Trade(all) if state.screen == Screen::Trade => menu.trade(*all, &mut intents),
        Action::Pin(id) if state.screen == Screen::Quests => {
            menu.pinned = if menu.pinned == *id { 0 } else { *id };
            menu.poll = 0.
        }
        Action::Inventory if state.screen == Screen::Quests => state.screen = Screen::Inventory,
        _ => {}
    }
}
#[derive(Component)]
struct Root;
#[derive(Component)]
struct Scroller(usize);
fn action_button(
    p: &mut ChildSpawnerCommands,
    label: impl Into<String>,
    action: Action,
    enabled: bool,
    selected: bool,
) {
    let mut entity = p.spawn((
        Node {
            min_height: px(34),
            width: percent(100),
            padding: UiRect::axes(px(12), px(7)),
            border: UiRect::all(px(1)),
            ..default()
        },
        BackgroundColor(if selected {
            Color::srgb(0.26, 0.31, 0.38)
        } else {
            BUTTON
        }),
        BorderColor::all(if selected { ACCENT } else { EDGE }),
    ));
    if enabled {
        entity.insert((Button, action));
    }
    entity.with_children(|p| {
        p.spawn(text(label, 16., if enabled { TEXT } else { DIM }));
    });
}
fn column() -> Node {
    Node {
        flex_direction: FlexDirection::Column,
        row_gap: px(6),
        ..default()
    }
}
fn objectives(p: &mut ChildSpawnerCommands, quest: &Quest) {
    for line in &quest.objectives {
        p.spawn(text(
            format!("{} {}", if line.done { "[x]" } else { "[ ]" }, line.text),
            14.,
            if line.done {
                Color::srgb(0.55, 0.8, 0.6)
            } else {
                TEXT
            },
        ));
    }
}
fn dialogue(p: &mut ChildSpawnerCommands, menu: &Menu, speaker: &str) {
    p.spawn(text(
        if speaker.is_empty() {
            "Survivor"
        } else {
            speaker
        },
        20.,
        ACCENT,
    ));
    let Some(d) = &menu.view.dialogue else { return };
    p.spawn(text(
        d.pages.get(menu.page).map(String::as_str).unwrap_or("..."),
        16.,
        Color::srgb(0.79, 0.79, 0.79),
    ));
    if menu.more_pages() {
        p.spawn(text(
            format!(
                "Space / click to continue ({}/{})",
                menu.page + 1,
                d.pages.len()
            ),
            14.,
            DIM,
        ));
        return;
    }
    for (i, response) in d.responses.iter().enumerate() {
        action_button(
            p,
            format!("{}.  {}", i + 1, response.text),
            Action::Choose(response.index),
            true,
            false,
        );
    }
    if let Some(shop) = &menu.view.direct_vendor {
        action_button(
            p,
            format!("Trade | {}", shop.name),
            Action::OpenShop(shop.guid.clone()),
            true,
            false,
        );
    }
    action_button(p, "Leave [Esc]", Action::CloseDialogue, true, false);
}
fn trade(p: &mut ChildSpawnerCommands, menu: &Menu, assets: &AssetServer) {
    let Some(t) = &menu.view.trade else {
        p.spawn(text("Loading vendor...", 20., TEXT));
        return;
    };
    p.spawn(text(&t.name, 20., ACCENT));
    p.spawn(text(&t.description, 16., TEXT));
    p.spawn(text(format!("Wallet: ${}", menu.view.wallet), 19., TEXT));
    p.spawn(Node {
        flex_direction: FlexDirection::Row,
        column_gap: px(16),
        width: percent(100),
        ..default()
    })
    .with_children(|row| {
        for (sell, title, rows) in [(false, "BUY", &t.selling), (true, "SELL", &t.buying)] {
            row.spawn(Node {
                width: percent(50),
                ..column()
            })
            .with_children(|col| {
                col.spawn(text(title, 18., DIM));
                col.spawn((
                    Scroller(usize::from(sell)),
                    ScrollPosition(Vec2::new(0., menu.scroll[usize::from(sell)])),
                    Node {
                        height: px(420),
                        overflow: Overflow::scroll_y(),
                        ..column()
                    },
                ))
                .with_children(|list| {
                    for line in rows {
                        list.spawn(Node {
                            flex_direction: FlexDirection::Row,
                            align_items: AlignItems::Center,
                            column_gap: px(6),
                            min_height: px(42),
                            flex_shrink: 0.,
                            ..default()
                        })
                        .with_children(|r| {
                            if let Some(icon) = &line.icon {
                                r.spawn((
                                    ImageNode::new(assets.load(icon.clone())),
                                    Node {
                                        width: px(32),
                                        height: px(32),
                                        flex_shrink: 0.,
                                        ..default()
                                    },
                                ));
                            }
                            let label = if line.vehicle {
                                format!("{} - not deliverable yet", line.name)
                            } else if sell {
                                format!("{} (x{}) | ${} each", line.name, line.held, line.cost)
                            } else {
                                format!("{} | ${}", line.name, line.cost)
                            };
                            action_button(
                                r,
                                label,
                                Action::Select(sell, line.index),
                                line.enabled,
                                menu.selection == Some((sell, line.index)),
                            );
                        });
                    }
                });
            });
        }
    });
    let selection = menu.selection.and_then(|(sell, index)| {
        let rows = if sell { &t.buying } else { &t.selling };
        rows.iter().find(|r| r.index == index).map(|r| (sell, r))
    });
    if let Some((sell, line)) = selection {
        action_button(
            p,
            if sell {
                format!("Sell 1 for ${}", line.cost)
            } else {
                format!("Buy 1 for ${}", line.cost)
            },
            Action::Trade(false),
            line.enabled,
            false,
        );
        if sell {
            action_button(
                p,
                format!(
                    "Sell all ({}) for ${}",
                    line.max_count,
                    line.cost * line.max_count as i32
                ),
                Action::Trade(true),
                line.enabled,
                false,
            )
        }
    }
    action_button(p, "Back [Esc]", Action::CloseTrade, true, false);
}
fn quests(p: &mut ChildSpawnerCommands, menu: &Menu) {
    p.spawn(text("QUESTS", 24., TEXT));
    p.spawn((
        Scroller(2),
        ScrollPosition(Vec2::new(0., menu.scroll[2])),
        Node {
            max_height: px(470),
            overflow: Overflow::scroll_y(),
            ..column()
        },
    ))
    .with_children(|list| {
        for quest in &menu.view.quests {
            action_button(
                list,
                format!(
                    "{}{} | {}",
                    if menu.pinned == quest.id { "* " } else { "" },
                    quest.name,
                    quest.status
                ),
                Action::Pin(quest.id),
                true,
                menu.pinned == quest.id,
            );
            if quest.status == "completed" {
                list.spawn(text(&quest.description, 14., DIM));
            } else {
                objectives(list, quest)
            }
        }
        if menu.view.quests.is_empty() {
            list.spawn(text("No quests yet", 16., DIM));
        }
    });
    p.spawn(text(
        format!(
            "{} not discovered | Click a quest to pin/unpin",
            menu.view.unknown_quests
        ),
        13.,
        DIM,
    ));
    action_button(p, "Inventory [Esc]", Action::Inventory, true, false);
}
fn draw(
    mut commands: Commands,
    roots: Query<Entity, With<Root>>,
    state: Res<UiState>,
    menu: Res<Menu>,
    speaker: Option<Res<CurrentNpcSpeaker>>,
    assets: Res<AssetServer>,
    mut cache: Local<String>,
) {
    let header = speaker.as_ref().map(|s| s.name.as_str()).unwrap_or("");
    let signature = format!(
        "{:?}|{:?}|{}|{:?}|{}|{}|{}",
        state.screen, menu.view, menu.page, menu.selection, menu.pinned, header, menu.message
    );
    if *cache == signature {
        return;
    }
    *cache = signature;
    for entity in &roots {
        commands.entity(entity).despawn();
    }
    if state.screen == Screen::Playing {
        if let Some(quest) = &menu.view.tracker {
            commands
                .spawn((
                    Root,
                    Node {
                        position_type: PositionType::Absolute,
                        right: px(14),
                        top: px(34),
                        width: px(420),
                        padding: UiRect::all(px(10)),
                        ..column()
                    },
                    BackgroundColor(PANEL),
                    GlobalZIndex(11),
                ))
                .with_children(|p| {
                    p.spawn(text(
                        format!(
                            "{}{}",
                            quest.name,
                            if quest.status == "ready" {
                                " - Ready"
                            } else {
                                ""
                            }
                        ),
                        18.,
                        ACCENT,
                    ));
                    objectives(p, quest);
                });
        }
        return;
    }
    if !matches!(
        state.screen,
        Screen::Dialogue | Screen::Trade | Screen::Quests
    ) {
        return;
    }
    let talk = state.screen == Screen::Dialogue;
    commands
        .spawn((
            Root,
            Node {
                position_type: PositionType::Absolute,
                width: percent(100),
                height: percent(100),
                justify_content: JustifyContent::Center,
                align_items: if talk {
                    AlignItems::End
                } else {
                    AlignItems::Center
                },
                padding: UiRect::bottom(px(if talk { 56 } else { 0 })),
                ..default()
            },
            BackgroundColor(Color::srgba(0., 0., 0., if talk { 0.35 } else { 0.72 })),
            GlobalZIndex(if state.screen == Screen::Trade {
                92
            } else {
                91
            }),
        ))
        .with_children(|root| {
            root.spawn((
                Node {
                    width: px(if state.screen == Screen::Trade {
                        1000
                    } else {
                        860
                    }),
                    max_width: percent(90),
                    max_height: percent(94),
                    padding: UiRect::all(px(18)),
                    ..column()
                },
                BackgroundColor(PANEL),
            ))
            .with_children(|p| {
                match state.screen {
                    Screen::Dialogue => dialogue(p, &menu, header),
                    Screen::Trade => trade(p, &menu, &assets),
                    Screen::Quests => quests(p, &menu),
                    _ => {}
                }
                if !menu.message.is_empty() {
                    p.spawn(text(&menu.message, 13., DIM));
                }
            });
        });
}
fn scroll(
    mut wheel: MessageReader<MouseWheel>,
    hover: Res<HoverMap>,
    parents: Query<&ChildOf>,
    mut nodes: Query<(&mut ScrollPosition, &ComputedNode, &Scroller)>,
    mut menu: ResMut<Menu>,
) {
    for event in wheel.read() {
        let delta = -event.y
            * if event.unit == MouseScrollUnit::Line {
                28.
            } else {
                1.
            };
        let mut handled = std::collections::HashSet::new();
        for pointer in hover.values() {
            for entity in pointer.keys() {
                let mut current = *entity;
                loop {
                    if let Ok((mut scroll, node, kind)) = nodes.get_mut(current) {
                        if handled.insert(current) {
                            let maximum = ((node.content_size().y - node.size().y)
                                * node.inverse_scale_factor())
                            .max(0.);
                            scroll.y = (scroll.y + delta).clamp(0., maximum);
                            menu.scroll[kind.0] = scroll.y;
                        }
                        break;
                    }
                    let Ok(parent) = parents.get(current) else {
                        break;
                    };
                    current = parent.parent();
                }
            }
        }
    }
}
#[cfg(test)]
mod tests;
