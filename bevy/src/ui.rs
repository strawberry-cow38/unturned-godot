//! Native Bevy presentation. Every gameplay button emits an intent; only snapshots
//! from the authoritative client update the inventory and vitals displayed here.
use crate::settings::{Binding, GameAction, Settings, mouse_number};
use bevy::{
    input::keyboard::{Key, KeyboardInput},
    picking::hover::Hovered,
    prelude::*,
    ui::Pressed,
    ui_widgets::{Activate, Button},
};
use serde::{Deserialize, Deserializer};
use serde_json::{Value, json};

mod appearance;
pub(crate) mod backdrop;
mod attachments;
mod clothing;
mod storage;
mod crosshair;
mod map;
mod network_menu;
mod swoop;
pub(crate) use map::MapDiagnostics;
pub(crate) mod npc;
mod vitals;
mod vehicle_controls;
pub(crate) mod tank_optics;

const TEXT: Color = Color::srgb(0.88, 0.88, 0.91);
const DIM: Color = Color::srgb(0.55, 0.56, 0.60);
const ACCENT: Color = Color::srgb(0.62, 0.78, 0.94);
const PANEL: Color = Color::srgba(0.13, 0.13, 0.14, 0.96);
const BUTTON: Color = Color::srgb(0.19, 0.19, 0.20);
const EDGE: Color = Color::srgb(0.28, 0.29, 0.31);

#[derive(Clone, Debug, Deserialize, Default, PartialEq)]
#[serde(default)]
pub struct InventoryItem {
    pub id: u16,
    pub name: String,
    pub x: u8,
    pub y: u8,
    pub rot: u8,
    pub width: u8,
    pub height: u8,
    pub amount: u16,
    pub quality: u8,
    #[serde(deserialize_with = "null_string")]
    pub icon: String,
    #[serde(rename = "gunAmmo")]
    pub gun_ammo: i32,
}

#[derive(Clone, Debug, Deserialize, Default, PartialEq)]
#[serde(default)]
pub struct InventoryPage {
    pub index: u8,
    pub width: u8,
    pub height: u8,
    pub items: Vec<InventoryItem>,
}

#[derive(Clone, Debug, Deserialize, Default, PartialEq)]
#[serde(default)]
pub struct Recipe {
    pub index: u16,
    pub name: String,
    pub craftable: bool,
    #[serde(deserialize_with = "null_string")]
    pub reason: String,
    pub seconds: f32,
    pub operation: String,
}

fn null_string<'de, D: Deserializer<'de>>(deserializer: D) -> Result<String, D::Error> {
    Ok(Option::<String>::deserialize(deserializer)?.unwrap_or_default())
}

#[derive(Resource, Debug)]
pub struct UiModel {
    pub connected: bool,
    pub network: crate::network::State,
    pub map_name: String,
    pub health: f32,
    pub alive: bool,
    pub food: f32,
    pub water: f32,
    pub stamina: f32,
    pub infection: f32,
    pub oxygen: f32,
    pub ammo: i32,
    pub held_item_id: u16,
    pub stance: String,
    pub pages: Vec<InventoryPage>,
    pub storage: storage::View,
    pub worn: [u16; 7],
    pub recipes: Vec<Recipe>,
    pub notices: Vec<String>,
    pub interaction: String,
    pub status: String,
    last_event_seq: u64,
}

impl Default for UiModel {
    fn default() -> Self {
        Self {
            connected: false,
            network: default(),
            map_name: String::new(),
            health: 0.0,
            alive: true,
            food: 0.0,
            water: 0.0,
            stamina: 0.0,
            infection: 0.0,
            oxygen: 1.0,
            ammo: -1,
            held_item_id: 0,
            stance: String::new(),
            pages: vec![],
            storage: default(),
            worn: [0; 7],
            recipes: vec![],
            notices: vec![],
            interaction: String::new(),
            status: "Choose a world to begin".into(),
            last_event_seq: 0,
        }
    }
}

impl UiModel {
    pub fn reset_session(&mut self) {
        self.last_event_seq = 0;
        self.notices.clear();
        self.pages.clear();
        self.storage = default();
        self.worn = [0; 7];
        self.recipes.clear();
    }
    pub fn adopt_snapshot(&mut self, snapshot: &Value) {
        self.connected = snapshot["connected"].as_bool().unwrap_or(false);
        self.network = crate::network::State::read(snapshot);
        let p = &snapshot["player"];
        self.health = number(&p["hp"]);
        self.alive = p["alive"].as_bool().unwrap_or(true);
        self.food = number(&p["food"]);
        self.water = number(&p["water"]);
        self.stamina = number(&p["stamina"]);
        self.infection = number(&p["infection"]);
        self.oxygen = p["oxygen"].as_f64().unwrap_or(1.) as f32;
        self.ammo = p["ammo"].as_i64().unwrap_or(-1) as i32;
        self.held_item_id = p["heldItemId"].as_u64().unwrap_or(0) as u16;
        self.stance = p["stance"]
            .as_str()
            .map(str::to_owned)
            .unwrap_or_else(|| p["stance"].to_string());
        if let Ok(storage) = serde_json::from_value(snapshot["storage"].clone()) {
            self.storage = storage;
        }
        if let Ok(pages) = serde_json::from_value(snapshot["inventory"]["pages"].clone()) {
            self.pages = pages;
        }
        self.worn = clothing::SLOTS
            .map(|slot| snapshot["inventory"]["worn"][slot].as_u64().unwrap_or(0) as u16);
        if let Ok(recipes) = serde_json::from_value(snapshot["recipes"].clone()) {
            self.recipes = recipes;
        }
        if let Some(events) = snapshot["events"].as_array() {
            for event in events {
                if let Some(seq) = event["seq"].as_u64() {
                    if seq <= self.last_event_seq {
                        continue;
                    }
                    self.last_event_seq = seq;
                }
                if let Some(text) = event["data"]["text"].as_str() {
                    if let Some(name) = event["data"]["name"]
                        .as_str()
                        .filter(|name| !name.is_empty())
                    {
                        self.notice(format!("{name}: {text}"));
                    } else {
                        self.notice(text);
                    }
                }
            }
        }
    }

    pub fn notice(&mut self, message: impl Into<String>) {
        let message = message.into();
        if self.notices.last() == Some(&message) {
            return;
        }
        self.notices.push(message);
        if self.notices.len() > 10 {
            self.notices.remove(0);
        }
    }
}

fn number(value: &Value) -> f32 {
    value.as_f64().unwrap_or(0.0) as f32
}

#[derive(Clone, Debug, Message)]
pub enum UiIntent {
    Play {
        map: String,
    },
    StartSession {
        map: String,
        target: crate::network::Target,
    },
    Resume,
    Save,
    Load,
    Disconnect,
    Quit,
    Action {
        name: String,
        args: Value,
    },
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum Screen {
    #[default]
    MainMenu,
    Connecting,
    DirectConnect,
    Servers,
    Host,
    Appearance,
    Playing,
    Paused,
    Dead,
    Inventory,
    Crafting,
    Information,
    Dialogue,
    Trade,
    Quests,
    Attachments,
    Lightbar,
    Mechanics,
    Settings,
    Console,
    Chat,
}

#[derive(Resource, Debug)]
pub struct UiState {
    pub screen: Screen,
    /// MapUI leaves the cursor free until its closing MenuSwoop completes.
    pub(crate) map_closing: bool,
    pub networked: bool,
    pub network_menu: network_menu::Menu,
    pub map: String,
    pub page: usize,
    pub recipe_page: usize,
    pub selected_item: Option<(u8, u8, u8)>,
    pub rotation: u8,
    pub console_input: String,
    pub settings_return: Screen,
    pub capturing: Option<GameAction>,
    pub binding_page: usize,
    pub settings_message: String,
}

impl Default for UiState {
    fn default() -> Self {
        Self {
            screen: Screen::MainMenu,
            map_closing: false,
            networked: false,
            network_menu: default(),
            map: "PEI".into(),
            page: 0,
            recipe_page: 0,
            selected_item: None,
            rotation: 0,
            console_input: String::new(),
            settings_return: Screen::MainMenu,
            capturing: None,
            binding_page: 0,
            settings_message: String::new(),
        }
    }
}

impl UiState {
    /// Pregame subpanels retain the source menu camera instead of jumping to an
    /// uninitialized player body. Settings inherits the screen it came from.
    pub fn menu_camera(&self) -> bool {
        let screen = if self.screen == Screen::Settings {
            self.settings_return
        } else {
            self.screen
        };
        matches!(
            screen,
            Screen::MainMenu
                | Screen::Appearance
                | Screen::DirectConnect
                | Screen::Servers
                | Screen::Host
        )
    }

    pub fn captures_input(&self) -> bool {
        self.screen != Screen::Playing || self.map_closing
    }

    pub fn close_overlay(&mut self) {
        if self.screen == Screen::Information {
            self.map_closing = true;
        }
        self.screen = match self.screen {
            Screen::Settings => self.settings_return,
            Screen::MainMenu => Screen::MainMenu,
            Screen::Appearance => Screen::MainMenu,
            Screen::Connecting => Screen::Connecting,
            Screen::DirectConnect | Screen::Servers | Screen::Host => {
                self.network_menu.cancel();
                Screen::MainMenu
            }
            Screen::Dead => Screen::Dead,
            _ => Screen::Playing,
        };
        self.capturing = None;
    }
}

#[derive(Clone, Component)]
enum UiButton {
    Network(network_menu::Action),
    Open(Screen),
    Play,
    Quit,
    Resume,
    Save,
    Load,
    Disconnect,
    Map(String),
    Page(i32),
    Recipes(i32),
    Select(u8, u8, u8),
    Move(u8, u8, u8),
    Drop,
    Consume,
    Equip,
    Wear(u8),
    Unwear(u8),
    Craft(u16),
    Sensitivity(f32),
    Fov(f32),
    Volume(f32),
    Invert,
    Vsync,
    SaveSettings,
    Bind(GameAction),
    BindingPage(i32),
    ResetBindings,
    Respawn,
}

#[derive(Component)]
pub(crate) struct OverlayRoot;
#[derive(Component)]
struct HudRoot;
#[derive(Component)]
struct WeaponText;
#[derive(Component)]
struct PromptText;
#[derive(Component)]
struct NoticesText;
#[derive(Component)]
struct ButtonBaseColor(Color);

/// Gameplay input must run after UI input so the key opening a menu is consumed.
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct UiUpdate;

pub struct GameUiPlugin;

impl Plugin for GameUiPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<UiState>()
            .init_resource::<crate::profile::LocalProfile>()
            .init_resource::<UiModel>()
            .init_resource::<Settings>()
            .add_message::<UiIntent>()
            .add_plugins((
                HudPlugin,
                backdrop::BackdropPlugin,
                swoop::SwoopPlugin,
                attachments::AttachmentMenuPlugin,
                vitals::VitalsPlugin,
                crosshair::CrosshairPlugin,
                network_menu::NetworkMenuPlugin,
                npc::NpcUiPlugin,
                map::MapPlugin,
                tank_optics::TankOpticsPlugin,
                vehicle_controls::VehicleControlsPlugin,
                storage::StoragePlugin,
            ))
            .add_systems(Startup, spawn_ui_camera)
            .add_systems(
                PreUpdate,
                suppress_repeat_edges.after(bevy::input::InputSystems),
            )
            .add_systems(
                Update,
                (
                    follow_life_state,
                    keyboard_ui,
                    rebuild_overlay,
                    button_appearance,
                )
                    .chain()
                    .in_set(UiUpdate),
            )
            .add_observer(activate_button);
    }
}

// Presentation follows accepted player/Operator adoption and input selection.
// Keeping this separate from UiUpdate avoids delaying input while ensuring the
// label rendered this frame matches the accepted shot/slot captured this frame.
struct HudPlugin;
impl Plugin for HudPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Startup, spawn_hud).add_systems(
            PostUpdate,
            update_hud.before(bevy::ui::UiSystems::Layout),
        );
    }
}

// Bevy's automatic UI target includes inactive cameras. The viewmodel camera is
// inactive on the menu, but its order would still steal the UI's render target.
// An explicit UI camera also keeps overlays above the first-person gun pass.
fn spawn_ui_camera(mut commands: Commands) {
    commands.spawn((
        Name::new("Interface camera"),
        Camera2d,
        Camera {
            order: 2,
            clear_color: ClearColorConfig::None,
            ..default()
        },
        IsDefaultUiCamera,
    ));
}

fn text(value: impl Into<String>, size: f32, color: Color) -> impl Bundle {
    (
        Text::new(value.into().replace(" · ", " | ").replace(" — ", " - ")),
        TextFont {
            font_size: FontSize::Px(size),
            ..default()
        },
        TextColor(color),
    )
}

fn row() -> Node {
    Node {
        flex_direction: FlexDirection::Row,
        column_gap: px(8),
        align_items: AlignItems::Center,
        flex_wrap: FlexWrap::Wrap,
        ..default()
    }
}

fn button(parent: &mut ChildSpawnerCommands, label: impl Into<String>, action: UiButton) {
    parent
        .spawn((
            Button,
            Hovered::default(),
            action,
            Node {
                min_height: px(34),
                padding: UiRect::axes(px(14), px(7)),
                border: UiRect::all(px(1)),
                border_radius: BorderRadius::all(px(3)),
                justify_content: JustifyContent::Center,
                align_items: AlignItems::Center,
                ..default()
            },
            BackgroundColor(BUTTON),
            ButtonBaseColor(BUTTON),
            BorderColor::all(EDGE),
        ))
        .with_children(|p| {
            p.spawn(text(label, 14.0, TEXT));
        });
}

fn spawn_hud(mut commands: Commands) {
    commands
        .spawn((
            HudRoot,
            Node {
                width: percent(100),
                height: percent(100),
                position_type: PositionType::Absolute,
                ..default()
            },
        ))
        .with_children(|p| {
            p.spawn((
                WeaponText,
                text("", 20.0, TEXT),
                Node {
                    position_type: PositionType::Absolute,
                    right: px(24),
                    bottom: px(24),
                    ..default()
                },
            ));
            p.spawn((
                PromptText,
                text("", 16.0, TEXT),
                Node {
                    position_type: PositionType::Absolute,
                    left: percent(37),
                    top: percent(57),
                    ..default()
                },
            ));
            p.spawn((
                NoticesText,
                text("", 13.0, TEXT),
                Node {
                    position_type: PositionType::Absolute,
                    left: px(24),
                    top: px(24),
                    max_width: percent(60),
                    ..default()
                },
            ));
        });
}

fn update_hud(
    state: Res<UiState>,
    model: Res<UiModel>,
    catalog: Res<crate::entities::ItemCatalog>,
    mounted: Option<Res<crate::vehicle::turrets::Operator>>,
    mut roots: Query<&mut Visibility, With<HudRoot>>,
    mut labels: ParamSet<(
        Query<&mut Text, With<WeaponText>>,
        Query<&mut Text, With<PromptText>>,
        Query<&mut Text, With<NoticesText>>,
    )>,
) {
    for mut visibility in &mut roots {
        *visibility = if state.screen == Screen::MainMenu {
            Visibility::Hidden
        } else {
            Visibility::Inherited
        };
    }
    for mut t in &mut labels.p0() {
        **t = if let Some(mount) = mounted.as_ref().and_then(|m| m.active()) {
            format!("{}  /  {}", mount.definition.display_name,
                mount.ammo.map_or_else(|| "…".into(), |n| n.to_string()))
        } else if let Some(item) = catalog.items.get(&model.held_item_id) {
            if item.item_type == "gun" && model.ammo >= 0 {
                format!("{}  /  {}", item.name, model.ammo)
            } else {
                item.name.clone()
            }
        } else {
            String::new()
        };
    }
    for mut t in &mut labels.p1() {
        **t = model.interaction.clone();
    }
    for mut t in &mut labels.p2() {
        **t = model
            .notices
            .iter()
            .rev()
            .take(4)
            .rev()
            .cloned()
            .collect::<Vec<_>>()
            .join("\n");
    }
}

fn follow_life_state(model: Res<UiModel>, mut state: ResMut<UiState>) {
    if model.connected && !model.alive && state.screen != Screen::MainMenu {
        state.screen = Screen::Dead;
        state.selected_item = None;
        state.capturing = None;
    } else if model.connected && model.alive && state.screen == Screen::Dead {
        state.screen = Screen::Playing;
    }
}

/// X11 may deliver a release/repeat-press pair while a key is held. Keep the
/// held resource intact but suppress repeat edges for game toggles; text fields
/// still read the original event stream and retain keyboard auto-repeat.
fn suppress_repeat_edges(
    mut events: MessageReader<KeyboardInput>,
    mut keys: ResMut<ButtonInput<KeyCode>>,
) {
    let events: Vec<_> = events.read().collect();
    for event in &events {
        if event.repeat
            && event.state.is_pressed()
            && !events.iter().any(|other| {
                other.key_code == event.key_code && other.state.is_pressed() && !other.repeat
            })
        {
            keys.clear_just_pressed(event.key_code);
        }
    }
}

fn keyboard_ui(
    mut state: ResMut<UiState>,
    mut settings: ResMut<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    mut characters: MessageReader<KeyboardInput>,
    mut intents: MessageWriter<UiIntent>,
    world: Option<Res<crate::world::WorldConfig>>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
) {
    if matches!(
        state.screen,
        Screen::Dialogue | Screen::Trade | Screen::Quests | Screen::Lightbar | Screen::Mechanics
    ) {
        characters.clear();
        return;
    }
    if let Some(action) = state.capturing {
        if keys.just_pressed(KeyCode::Escape) {
            state.capturing = None;
            characters.clear();
            return;
        }
        let binding = if keys.just_pressed(KeyCode::Delete) || keys.just_pressed(KeyCode::Backspace)
        {
            Some(Binding::Unbound)
        } else if let Some(key) = keys.get_just_pressed().next() {
            Some(Binding::Key(format!("{key:?}")))
        } else {
            mouse
                .get_just_pressed()
                .next()
                .map(|button| Binding::Mouse(mouse_number(*button)))
        };
        if let Some(binding) = binding {
            if let Some(conflict) = settings.conflict_with(action, &binding) {
                state.settings_message = format!(
                    "{} is bound to {conflict:?}. Unbind it first.",
                    binding.label()
                );
            } else {
                settings.bindings.insert(action, binding);
                state.settings_message = "Binding updated. Save settings to keep it.".into();
            }
            state.capturing = None;
        }
        characters.clear();
        return;
    }
    if keys.just_pressed(KeyCode::Escape) {
        if state.screen == Screen::Playing {
            state.screen = Screen::Paused;
        } else {
            state.close_overlay();
        }
        characters.clear();
        return;
    }
    if matches!(
        state.screen,
        Screen::DirectConnect | Screen::Servers | Screen::Host
    ) {
        network_menu::typed(&mut state, &mut characters);
        return;
    }
    if matches!(state.screen, Screen::Console | Screen::Chat) {
        for input in characters.read() {
            if !input.state.is_pressed() {
                continue;
            }
            match &input.logical_key {
                Key::Backspace => {
                    state.console_input.pop();
                }
                Key::Space => {
                    if state.console_input.len() < 2048 {
                        state.console_input.push(' ');
                    }
                }
                Key::Enter => {
                    let command = std::mem::take(&mut state.console_input);
                    if !command.trim().is_empty() {
                        intents.write(UiIntent::Action {
                            name: if state.screen == Screen::Chat {
                                "chat"
                            } else {
                                "console"
                            }
                            .into(),
                            args: json!({"text":command}),
                        });
                    }
                    if state.screen == Screen::Chat {
                        state.screen = Screen::Playing;
                    }
                }
                Key::Character(value) => {
                    if state.console_input.len() + value.len() <= 2048
                        && !value.chars().any(char::is_control)
                    {
                        state.console_input.push_str(value);
                    }
                }
                _ => {}
            }
        }
        return;
    }
    characters.clear();
    // Source seat keys precede the vehicle input allow-list. F1 must not open
    // its on-foot console alias before the accepted seat request is dispatched.
    let seated = occupancy.as_ref().is_some_and(|o| o.seat.is_some());
    if state.screen == Screen::Playing && seated
        && crate::vehicle::seats::pressed(&keys).is_some()
        && !keys.just_pressed(KeyCode::Backquote)
    { return; }
    if matches!(
        state.screen,
        Screen::Playing | Screen::Inventory | Screen::Crafting | Screen::Information
    ) {
        let dashboard = if settings.just_pressed(GameAction::Inventory, &keys, &mouse)
            || keys.just_pressed(KeyCode::Tab)
        {
            Some(Screen::Inventory)
        } else if settings.just_pressed(GameAction::Craft, &keys, &mouse) {
            Some(Screen::Crafting)
        } else if settings.just_pressed(GameAction::Map, &keys, &mouse)
            && world
                .as_ref()
                .is_none_or(|world| map::available(&world.map_name))
        {
            Some(Screen::Information)
        } else {
            None
        };
        if let Some(target) = dashboard {
            state.screen = if state.screen == target {
                Screen::Playing
            } else {
                target
            };
        } else if state.screen == Screen::Inventory && keys.just_pressed(KeyCode::KeyR) {
            state.rotation = (state.rotation + 1) % 2;
        } else if state.screen == Screen::Playing {
            if settings.just_pressed(GameAction::Console, &keys, &mouse)
                || keys.just_pressed(KeyCode::Backquote)
                || (!seated && keys.just_pressed(KeyCode::F1))
            {
                state.screen = Screen::Console;
            } else if settings.just_pressed(GameAction::Chat, &keys, &mouse) {
                state.screen = Screen::Chat;
            }
        }
    }
}

fn activate_button(
    event: On<Activate>,
    buttons: Query<&UiButton>,
    mut state: ResMut<UiState>,
    mut settings: ResMut<Settings>,
    model: Res<UiModel>,
    time: Res<Time<Real>>,
    mut options: ResMut<crate::LaunchOptions>,
    mut intents: MessageWriter<UiIntent>,
    world: Option<Res<crate::world::WorldConfig>>,
) {
    let Ok(action) = buttons.get(event.entity) else {
        return;
    };
    match action {
        UiButton::Network(action) => {
            let map = state.map.clone();
            if let Some(intent) = state
                .network_menu
                .action(action, &map, time.elapsed_secs_f64())
            {
                options.0.player_name = state.network_menu.name.clone();
                intents.write(intent);
            }
        }
        UiButton::Open(screen) => {
            if *screen == Screen::Information
                && world
                    .as_ref()
                    .is_some_and(|world| !map::available(&world.map_name))
            {
                return;
            }
            if matches!(
                state.screen,
                Screen::DirectConnect | Screen::Servers | Screen::Host
            ) {
                state.network_menu.cancel();
            }
            if *screen == Screen::Servers {
                state.network_menu.host = "claw.bitvox.me".into();
                state.network_menu.port = crate::network::DEFAULT_PORT.to_string();
                state.network_menu.request = Some(false);
            }
            if *screen == Screen::Settings {
                state.settings_return = state.screen;
            }
            state.screen = *screen;
        }
        UiButton::Play => {
            intents.write(UiIntent::Play {
                map: state.map.clone(),
            });
        }
        UiButton::Respawn => {
            intents.write(UiIntent::Action {
                name: "respawn".into(),
                args: json!({}),
            });
        }
        UiButton::Quit => {
            intents.write(UiIntent::Quit);
        }
        UiButton::Resume => {
            state.close_overlay();
            intents.write(UiIntent::Resume);
        }
        UiButton::Save => {
            intents.write(UiIntent::Save);
        }
        UiButton::Load => {
            intents.write(UiIntent::Load);
        }
        UiButton::Disconnect => {
            intents.write(UiIntent::Disconnect);
            state.screen = Screen::MainMenu;
        }
        UiButton::Map(map) => state.map = map.clone(),
        UiButton::Page(delta) => {
            let len = visible_pages(&model).len().max(1);
            state.page = (state.page as i32 + delta).rem_euclid(len as i32) as usize;
        }
        UiButton::Recipes(delta) => {
            let len = model.recipes.len().div_ceil(7).max(1);
            state.recipe_page = (state.recipe_page as i32 + delta).rem_euclid(len as i32) as usize;
        }
        UiButton::Select(page, x, y) => {
            state.selected_item = Some((*page, *x, *y));
            state.rotation = model
                .pages
                .iter()
                .find(|p| p.index == *page)
                .and_then(|p| p.items.iter().find(|i| i.x == *x && i.y == *y))
                .map_or(0, |i| i.rot);
        }
        UiButton::Move(page, x, y) => {
            if let Some((page0, x0, y0)) = state.selected_item {
                intents.write(UiIntent::Action { name:"moveItem".into(), args:json!({"page0":page0,"x0":x0,"y0":y0,"page1":page,"x1":x,"y1":y,"rot1":state.rotation}) });
                state.selected_item = None;
            }
        }
        UiButton::Drop | UiButton::Consume | UiButton::Equip | UiButton::Wear(_) => {
            if let Some((page, x, y)) = state.selected_item {
                let name = match action {
                    UiButton::Drop => "dropItem",
                    UiButton::Consume => "consume",
                    UiButton::Wear(_) => "wearClothing",
                    _ => "equipAndHold",
                };
                let mut args = json!({"page":page,"x":x,"y":y});
                if let UiButton::Wear(slot) = action {
                    args["slot"] = json!(slot);
                }
                intents.write(UiIntent::Action {
                    name: name.into(),
                    args,
                });
                state.selected_item = None;
            }
        }
        UiButton::Unwear(slot) => {
            intents.write(UiIntent::Action {
                name: "unwearClothing".into(),
                args: json!({"slot":slot}),
            });
            state.selected_item = None;
        }
        UiButton::Craft(index) => {
            intents.write(UiIntent::Action {
                name: "craft".into(),
                args: json!({"index":index}),
            });
        }
        UiButton::Sensitivity(delta) => {
            settings.mouse_sensitivity += delta;
            settings.normalize();
        }
        UiButton::Fov(delta) => {
            settings.field_of_view += delta;
            settings.normalize();
        }
        UiButton::Volume(delta) => {
            settings.master_volume += delta;
            settings.normalize();
        }
        UiButton::Invert => settings.invert_look_y = !settings.invert_look_y,
        UiButton::Vsync => settings.vsync = !settings.vsync,
        UiButton::SaveSettings => {
            state.settings_message = match settings.save() {
                Ok(()) => "Settings saved".into(),
                Err(e) => format!("Could not save settings: {e}"),
            }
        }
        UiButton::Bind(action) => state.capturing = Some(*action),
        UiButton::BindingPage(delta) => {
            let len = settings.bindings.len().div_ceil(6);
            state.binding_page =
                (state.binding_page as i32 + delta).rem_euclid(len as i32) as usize;
        }
        UiButton::ResetBindings => {
            settings.bindings = Settings::default().bindings;
        }
    }
}

fn visible_pages(model: &UiModel) -> Vec<&InventoryPage> {
    model
        .pages
        .iter()
        .filter(|p| p.width > 0 && p.height > 0)
        .collect()
}

fn page_name(index: u8) -> String {
    match index {
        0 => "Primary".into(),
        1 => "Secondary".into(),
        2 => "Hands".into(),
        3 => "Backpack".into(),
        4 => "Vest".into(),
        5 => "Shirt".into(),
        6 => "Pants".into(),
        7 => "Storage".into(),
        8 => "Nearby".into(),
        9 => "Secondary storage".into(),
        _ => format!("Compartment {}", index - 9),
    }
}

fn rebuild_overlay(
    mut commands: Commands,
    roots: Query<(Entity, Option<&swoop::Swoop>), With<OverlayRoot>>,
    state: Res<UiState>,
    model: Res<UiModel>,
    catalog: Res<crate::entities::ItemCatalog>,
    settings: Res<Settings>,
    assets: Res<AssetServer>,
    profile: Res<crate::profile::LocalProfile>,
    mut cache: Local<String>,
) {
    // Do not destroy and rebuild the widgets for every 50 Hz player snapshot.
    let detail = match state.screen {
        Screen::Inventory => format!("{:?}|{:?}|{:?}|{}", model.pages, model.worn, model.storage, catalog.items.len()),
        Screen::Crafting => format!("{:?}", model.recipes),
        Screen::Console | Screen::Chat => format!("{:?}", model.notices),
        Screen::MainMenu | Screen::Connecting => model.status.clone(),
        Screen::Appearance => format!("{profile:?}"),
        _ => String::new(),
    };
    let signature = format!("{state:?}|{settings:?}|{detail}");
    if *cache == signature {
        return;
    }
    *cache = signature;
    for (root, animation) in &roots {
        swoop::replace::<OverlayRoot>(&mut commands, root, animation, &state, &model);
    }
    if matches!(
        state.screen,
        Screen::Playing
            | Screen::Attachments
            | Screen::Lightbar
            | Screen::Mechanics
            | Screen::Dialogue
            | Screen::Trade
            | Screen::Quests
            | Screen::Information
    ) {
        return;
    }
    let dashboard = matches!(state.screen, Screen::Inventory | Screen::Crafting);
    commands
        .spawn((
            OverlayRoot,
            Node {
                width: percent(100),
                height: percent(100),
                position_type: PositionType::Absolute,
                justify_content: JustifyContent::Center,
                align_items: AlignItems::Center,
                ..default()
            },
            GlobalZIndex(20),
            BackgroundColor(Color::srgba(
                0.03,
                0.04,
                0.04,
                if dashboard {
                    0.
                } else if state.screen == Screen::MainMenu {
                    0.62
                } else {
                    0.48
                },
            )),
        ))
        .insert_if(backdrop::Backdrop::dashboard(1.), || dashboard)
        .insert_if(swoop::Swoop(state.screen), || dashboard)
        .with_children(|root| {
            root.spawn((
                vitals::MenuPanel,
                Node {
                    width: px(if state.screen == Screen::Inventory {
                        860
                    } else {
                        720
                    }),
                    max_width: percent(94),
                    max_height: percent(94),
                    padding: UiRect::all(px(24)),
                    flex_direction: FlexDirection::Column,
                    row_gap: px(12),
                    border: UiRect::all(px(1)),
                    border_radius: BorderRadius::all(px(5)),
                    ..default()
                },
                BackgroundColor(PANEL),
                BorderColor::all(EDGE),
            ))
            .with_children(|panel| match state.screen {
                Screen::MainMenu => menu_panel(panel, &state, &model, &assets),
                Screen::Appearance => appearance::panel(panel, &profile, &assets),
                Screen::DirectConnect | Screen::Servers | Screen::Host => {
                    network_menu::panel(panel, &state)
                }
                Screen::Connecting => {
                    panel.spawn(text(&model.status, 20., TEXT));
                    button(panel, "Cancel", UiButton::Disconnect);
                }
                Screen::Paused => pause_panel(panel, &model),
                Screen::Dead => death_panel(panel, &model),
                Screen::Inventory => inventory_panel(panel, &state, &model, &assets, &catalog),
                Screen::Crafting => crafting_panel(panel, &state, &model),
                Screen::Settings => settings_panel(panel, &state, &settings),
                Screen::Console | Screen::Chat => console_panel(panel, &state, &model),
                Screen::Playing
                | Screen::Attachments
                | Screen::Lightbar
                | Screen::Mechanics
                | Screen::Dialogue
                | Screen::Trade
                | Screen::Quests
                | Screen::Information => {}
            });
        });
}

fn menu_panel(
    panel: &mut ChildSpawnerCommands,
    state: &UiState,
    model: &UiModel,
    assets: &AssetServer,
) {
    panel.spawn(text("UNTURNED", 44.0, TEXT));
    panel.spawn(text("SURVIVE. BUILD. EXPLORE.", 13.0, DIM));
    panel.spawn(row()).with_children(|p| {
        button(p, "Multiplayer", UiButton::Open(Screen::Servers));
        button(p, "Direct Connect", UiButton::Open(Screen::DirectConnect));
        button(p, "Host", UiButton::Open(Screen::Host));
    });
    panel.spawn(text("World", 16.0, ACCENT));
    panel.spawn(row()).with_children(|p| {
        for map in ["PEI", "Washington", "Yukon", "Procedural"] {
            button(
                p,
                if state.map == map {
                    format!("* {map}")
                } else {
                    map.into()
                },
                UiButton::Map(map.into()),
            );
        }
    });
    if state.map != "Procedural" {
        panel.spawn((
            ImageNode::new(assets.load(format!("menu/mapicon_{}.png", state.map.to_lowercase()))),
            Node {
                width: px(240),
                height: px(90),
                flex_shrink: 0.0,
                ..default()
            },
        ));
    }
    panel.spawn(text(&model.status, 13.0, DIM));
    panel.spawn(row()).with_children(|p| {
        button(p, "Play", UiButton::Play);
        button(p, "Appearance", UiButton::Open(Screen::Appearance));
        button(p, "Settings", UiButton::Open(Screen::Settings));
        button(p, "Quit", UiButton::Quit);
    });
}

fn death_panel(panel: &mut ChildSpawnerCommands, model: &UiModel) {
    panel.spawn(text("You died", 32.0, TEXT));
    panel.spawn(text(
        if model.network.owns_world {
            "Respawn now to return to the world."
        } else {
            "Waiting for the server to respawn you."
        },
        15.0,
        DIM,
    ));
    panel.spawn(row()).with_children(|p| {
        if model.network.owns_world {
            button(p, "Respawn", UiButton::Respawn);
        }
        button(p, "Leave world", UiButton::Disconnect);
    });
}

fn pause_panel(panel: &mut ChildSpawnerCommands, model: &UiModel) {
    panel.spawn(text(
        if model.network.networked() {
            "MENU - WORLD CONTINUES"
        } else {
            "PAUSED"
        },
        28.0,
        TEXT,
    ));
    panel.spawn(row()).with_children(|p| {
        button(p, "Resume", UiButton::Resume);
        if model.network.owns_world {
            button(p, "Save world", UiButton::Save);
        }
        if !model.network.networked() {
            button(p, "Load world", UiButton::Load);
        }
    });
    panel.spawn(row()).with_children(|p| {
        button(p, "Settings", UiButton::Open(Screen::Settings));
        button(p, "Leave world", UiButton::Disconnect);
        button(p, "Quit", UiButton::Quit);
    });
}

fn inventory_panel(
    panel: &mut ChildSpawnerCommands,
    state: &UiState,
    model: &UiModel,
    assets: &AssetServer,
    catalog: &crate::entities::ItemCatalog,
) {
    panel.spawn(row()).with_children(|p| {
        p.spawn(text("INVENTORY", 26.0, TEXT));
        button(p, "Crafting", UiButton::Open(Screen::Crafting));
        if map::available(&model.map_name) {
            button(p, "Information", UiButton::Open(Screen::Information));
        }
        button(p, "Close", UiButton::Resume);
    });
    clothing::worn_buttons(panel, model);
    let pages = visible_pages(model);
    if pages.is_empty() {
        panel.spawn(text("Waiting for inventory...", 16.0, DIM));
        return;
    }
    if model.storage.net_id != 0 {
        storage::columns(panel, state, model, assets);
    } else {
        let page = pages[state.page % pages.len()];
        panel.spawn(row()).with_children(|p| {
            button(p, "<", UiButton::Page(-1));
            p.spawn(text(
                format!(
                    "{}  |  {} x {}",
                    page_name(page.index),
                    page.width,
                    page.height
                ),
                18.0,
                ACCENT,
            ));
            button(p, ">", UiButton::Page(1));
        });
        let cell = (650.0 / f32::from(page.width.max(1)))
            .min(40.0)
            .min(310.0 / f32::from(page.height.max(1)));
        inventory_grid(panel, page, state, assets, cell);
    }
    panel.spawn(text(
        "Select an item, then an empty cell to move it. R rotates the selected item.",
        13.0,
        DIM,
    ));
    if let Some((pg, x, y)) = state.selected_item {
        if let Some(item) = model
            .pages
            .iter()
            .find(|p| p.index == pg)
            .and_then(|p| p.items.iter().find(|i| i.x == x && i.y == y))
        {
            panel.spawn(text(
                format!(
                    "{}  |  {}% condition  |  {} deg",
                    item.name,
                    item.quality,
                    u16::from(state.rotation) * 90
                ),
                16.0,
                ACCENT,
            ));
            panel.spawn(row()).with_children(|p| {
                if let Some(slot) = catalog
                    .items
                    .get(&item.id)
                    .and_then(|item| clothing::slot(&item.item_type))
                {
                    button(p, "Wear", UiButton::Wear(slot));
                } else {
                    button(p, "Equip", UiButton::Equip);
                    button(p, "Use", UiButton::Consume);
                }
                button(p, "Drop", UiButton::Drop);
            });
        }
    }
}

fn inventory_grid(
    panel: &mut ChildSpawnerCommands,
    page: &InventoryPage,
    state: &UiState,
    assets: &AssetServer,
    cell: f32,
) {
    panel
        .spawn(Node {
            width: px(cell * f32::from(page.width)),
            height: px(cell * f32::from(page.height)),
            position_type: PositionType::Relative,
            flex_shrink: 0.,
            ..default()
        })
        .with_children(|grid| {
            for y in 0..page.height {
                for x in 0..page.width {
                    grid.spawn((
                        Button,
                        Hovered::default(),
                        UiButton::Move(page.index, x, y),
                        Node {
                            position_type: PositionType::Absolute,
                            left: px(f32::from(x) * cell),
                            top: px(f32::from(y) * cell),
                            width: px(cell - 2.0),
                            height: px(cell - 2.0),
                            border: UiRect::all(px(1)),
                            ..default()
                        },
                        BackgroundColor(Color::srgb(0.23, 0.24, 0.25)),
                        ButtonBaseColor(Color::srgb(0.23, 0.24, 0.25)),
                        BorderColor::all(EDGE),
                    ));
                }
            }
            for item in &page.items {
                let (w, h) = if item.rot % 2 == 0 {
                    (item.width.max(1), item.height.max(1))
                } else {
                    (item.height.max(1), item.width.max(1))
                };
                let selected = state.selected_item == Some((page.index, item.x, item.y));
                grid.spawn((
                    Button,
                    Hovered::default(),
                    UiButton::Select(page.index, item.x, item.y),
                    Node {
                        position_type: PositionType::Absolute,
                        left: px(f32::from(item.x) * cell),
                        top: px(f32::from(item.y) * cell),
                        width: px(f32::from(w) * cell - 2.0),
                        height: px(f32::from(h) * cell - 2.0),
                        padding: UiRect::all(px(3)),
                        border: UiRect::all(px(if selected { 2 } else { 1 })),
                        overflow: Overflow::clip(),
                        ..default()
                    },
                    BackgroundColor(Color::srgb(0.32, 0.35, 0.37)),
                    ButtonBaseColor(Color::srgb(0.32, 0.35, 0.37)),
                    BorderColor::all(if selected { ACCENT } else { EDGE }),
                ))
                .with_children(|p| {
                    if !item.icon.is_empty() {
                        // Icons are authored in the unrotated item's aspect ratio. Rotate the
                        // image around its center along with the occupied grid footprint.
                        let iw = f32::from(item.width.max(1)) * cell - 8.0;
                        let ih = f32::from(item.height.max(1)) * cell - 8.0;
                        p.spawn((
                            ImageNode::new(assets.load(item.icon.clone())),
                            Node {
                                position_type: PositionType::Absolute,
                                width: px(iw),
                                height: px(ih),
                                left: px((f32::from(w) * cell - 2.0 - iw) * 0.5),
                                top: px((f32::from(h) * cell - 2.0 - ih) * 0.5),
                                ..default()
                            },
                            UiTransform::from_rotation(Rot2::radians(if item.rot % 2 == 0 {
                                0.0
                            } else {
                                std::f32::consts::FRAC_PI_2
                            })),
                        ));
                        p.spawn((
                            text(format!("x{}", item.amount), 11.0, TEXT),
                            Node {
                                position_type: PositionType::Absolute,
                                right: px(2),
                                bottom: px(2),
                                ..default()
                            },
                            BackgroundColor(Color::srgba(0.0, 0.0, 0.0, 0.7)),
                        ));
                    } else {
                        p.spawn(text(format!("{}\nx{}", item.name, item.amount), 11.0, TEXT));
                    }
                });
            }
        });
}

fn crafting_panel(panel: &mut ChildSpawnerCommands, state: &UiState, model: &UiModel) {
    panel.spawn(row()).with_children(|p| {
        p.spawn(text("CRAFTING", 26.0, TEXT));
        button(p, "Inventory", UiButton::Open(Screen::Inventory));
        if map::available(&model.map_name) {
            button(p, "Information", UiButton::Open(Screen::Information));
        }
        button(p, "Close", UiButton::Resume);
    });
    if model.recipes.is_empty() {
        panel.spawn(text("No recipes in the loaded catalog.", 16.0, DIM));
        return;
    }
    let count = model.recipes.len().div_ceil(7);
    panel.spawn(row()).with_children(|p| {
        button(p, "<", UiButton::Recipes(-1));
        p.spawn(text(
            format!("Page {} / {}", state.recipe_page % count + 1, count),
            14.0,
            DIM,
        ));
        button(p, ">", UiButton::Recipes(1));
    });
    for recipe in model
        .recipes
        .iter()
        .skip((state.recipe_page % count) * 7)
        .take(7)
    {
        panel
            .spawn((row(), BackgroundColor(BUTTON)))
            .with_children(|p| {
                p.spawn((
                    text(
                        format!("{}  |  {:.1}s", recipe.name, recipe.seconds),
                        15.0,
                        TEXT,
                    ),
                    Node {
                        width: px(400),
                        ..default()
                    },
                ));
                if recipe.craftable {
                    button(p, "Craft", UiButton::Craft(recipe.index));
                } else {
                    p.spawn(text(
                        if recipe.reason.is_empty() {
                            "Requirements not met"
                        } else {
                            &recipe.reason
                        },
                        12.0,
                        DIM,
                    ));
                }
            });
    }
}

fn settings_panel(panel: &mut ChildSpawnerCommands, state: &UiState, settings: &Settings) {
    panel.spawn(row()).with_children(|p| {
        p.spawn(text("SETTINGS", 26.0, TEXT));
        button(p, "Back", UiButton::Resume);
        button(p, "Save", UiButton::SaveSettings);
    });
    panel.spawn(row()).with_children(|p| {
        p.spawn(text(
            format!("Look sensitivity  {:.2}", settings.mouse_sensitivity),
            15.0,
            TEXT,
        ));
        button(p, "-", UiButton::Sensitivity(-0.02));
        button(p, "+", UiButton::Sensitivity(0.02));
        button(
            p,
            if settings.invert_look_y {
                "Y: Inverted"
            } else {
                "Y: Regular"
            },
            UiButton::Invert,
        );
    });
    panel.spawn(row()).with_children(|p| {
        p.spawn(text(
            format!("Field of view  {:.0} deg", settings.field_of_view),
            15.0,
            TEXT,
        ));
        button(p, "-", UiButton::Fov(-5.0));
        button(p, "+", UiButton::Fov(5.0));
        button(
            p,
            if settings.vsync {
                "VSync: On"
            } else {
                "VSync: Off"
            },
            UiButton::Vsync,
        );
    });
    panel.spawn(row()).with_children(|p| {
        p.spawn(text(
            format!("Volume  {:.0}%", settings.master_volume * 100.0),
            15.0,
            TEXT,
        ));
        button(p, "-", UiButton::Volume(-0.1));
        button(p, "+", UiButton::Volume(0.1));
    });
    panel.spawn(row()).with_children(|p| {
        p.spawn(text("KEY BINDS", 17.0, ACCENT));
        button(p, "<", UiButton::BindingPage(-1));
        button(p, ">", UiButton::BindingPage(1));
        button(p, "Reset", UiButton::ResetBindings);
    });
    for (action, binding) in settings
        .bindings
        .iter()
        .skip(state.binding_page * 6)
        .take(6)
    {
        panel.spawn(row()).with_children(|p| {
            p.spawn((
                text(format!("{action:?}"), 14.0, TEXT),
                Node {
                    width: px(230),
                    ..default()
                },
            ));
            button(
                p,
                if state.capturing == Some(*action) {
                    "Press a key...".into()
                } else {
                    binding.label()
                },
                UiButton::Bind(*action),
            );
        });
    }
    panel.spawn(text(
        if state.capturing.is_some() {
            "Press a key or mouse button. Esc cancels; Delete unbinds."
        } else {
            &state.settings_message
        },
        12.0,
        DIM,
    ));
}

fn console_panel(panel: &mut ChildSpawnerCommands, state: &UiState, model: &UiModel) {
    panel.spawn(text(
        if state.screen == Screen::Chat {
            "CHAT"
        } else {
            "CONSOLE"
        },
        24.0,
        TEXT,
    ));
    for line in model.notices.iter().rev().take(7).rev() {
        panel.spawn(text(line, 14.0, DIM));
    }
    panel.spawn((
        text(format!("> {}_", state.console_input), 16.0, ACCENT),
        Node {
            min_height: px(36),
            padding: UiRect::all(px(8)),
            ..default()
        },
        BackgroundColor(Color::srgb(0.08, 0.08, 0.09)),
    ));
    panel.spawn(text("Enter sends  |  Esc closes", 12.0, DIM));
}

fn button_appearance(
    mut buttons: Query<
        (
            &Hovered,
            Has<Pressed>,
            &ButtonBaseColor,
            &mut BackgroundColor,
        ),
        With<Button>,
    >,
) {
    for (hovered, pressed, base, mut color) in &mut buttons {
        if pressed {
            color.0 = Color::srgb(0.35, 0.45, 0.54);
        } else if hovered.get() {
            color.0 = Color::srgb(0.29, 0.32, 0.35);
        } else {
            color.0 = base.0;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn production_hud_renders_accepted_mount_ammo_and_slot_on_the_same_frame() {
        use crate::{player::{GameSnapshot, PlayerUpdate}, vehicle::turrets::Operator};
        #[derive(Resource, Default)]
        struct Selection(usize);
        #[derive(Resource, Default)]
        struct AtLayout(String);
        fn adopt(
            snapshot: Res<GameSnapshot>, selection: Res<Selection>,
            mut operator: ResMut<Operator>, mut model: ResMut<UiModel>,
        ) {
            model.adopt_snapshot(&snapshot.0);
            operator.refresh(&snapshot.0, 0);
            operator.select(selection.0);
        }
        fn observe_at_layout(label: Query<&Text, With<WeaponText>>, mut seen: ResMut<AtLayout>) {
            seen.0 = label.single().unwrap().0.clone();
        }
        let tank = crate::vehicle::data::named("tank").unwrap();
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, HudPlugin))
            .insert_resource(UiState {screen:Screen::Playing,..default()})
            .init_resource::<UiModel>()
            .init_resource::<crate::entities::ItemCatalog>()
            .init_resource::<Operator>()
            .init_resource::<Selection>()
            .init_resource::<AtLayout>()
            .insert_resource(GameSnapshot(serde_json::json!({
                "connected":true,"playerId":1,"player":{"alive":true,"heldItemId":4,"ammo":30},
                "vehicles":[{"netId":7,"typeId":tank.type_id,"driverPlayerId":1,"passengers":[0]}],
                "vehicleMounts":[{"vehicleNetId":7,"headOut":false,"mounts":[
                    {"index":0,"yaw":0.,"pitch":0.,"ammo":60,"cooldownSeconds":0.,"crewAlive":true},
                    {"index":1,"yaw":0.,"pitch":0.,"ammo":400,"cooldownSeconds":0.,"crewAlive":true}
                ]}]
            })))
            .configure_sets(Update, PlayerUpdate.after(UiUpdate))
            .add_systems(Update, adopt.in_set(PlayerUpdate))
            .add_systems(PostUpdate, observe_at_layout.in_set(bevy::ui::UiSystems::Layout));
        app.world_mut().resource_mut::<crate::entities::ItemCatalog>().items.insert(
            4,serde_json::from_value(serde_json::json!({"id":4,"name":"Rifle","type":"gun"})).unwrap());
        app.update();
        assert_eq!(app.world().resource::<AtLayout>().0, "Tank Cannon  /  60");
        app.world_mut().resource_mut::<GameSnapshot>().0["vehicleMounts"][0]["mounts"][0]["ammo"] = serde_json::json!(59);
        app.update();
        assert_eq!(app.world().resource::<AtLayout>().0, "Tank Cannon  /  59",
            "accepted shot must update before this frame's text layout");
        app.world_mut().resource_mut::<Selection>().0 = 1;
        app.world_mut().resource_mut::<GameSnapshot>().0["vehicleMounts"][0]["mounts"][1]["ammo"] = serde_json::json!(399);
        app.update();
        assert_eq!(app.world().resource::<AtLayout>().0, "HMG  /  399",
            "new slot and accepted belt must appear together without a stale frame");
        app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"] = serde_json::json!([]);
        app.update();
        assert_eq!(app.world().resource::<AtLayout>().0, "Rifle  /  30",
            "accepted exit must restore held equipment without the prior mount label");
    }

    #[test]
    fn vehicle_function_keys_reserve_seats_while_backquote_and_onfoot_alias_open_console() {
        let mut app=App::new();
        app.init_resource::<Settings>().init_resource::<ButtonInput<KeyCode>>()
            .init_resource::<ButtonInput<MouseButton>>()
            .insert_resource(UiState {screen:Screen::Playing,..default()})
            .init_resource::<crate::vehicle::Occupancy>()
            .add_message::<KeyboardInput>().add_message::<UiIntent>()
            .add_systems(Update,keyboard_ui);
        app.world_mut().resource_mut::<crate::vehicle::Occupancy>().seat=Some(crate::vehicle::OccupiedSeat {
            vehicle:Entity::PLACEHOLDER,net_id:71,seat:1,driver:false,aircraft:false,
            velocity:Vec3::ZERO,eye_pose:default(),body_pose:default(),
        });
        for key in [KeyCode::F1,KeyCode::F2,KeyCode::F12] {
            app.world_mut().resource_mut::<ButtonInput<KeyCode>>().reset_all();
            app.world_mut().resource_mut::<ButtonInput<KeyCode>>().press(key);
            app.update();
            assert_eq!(app.world().resource::<UiState>().screen,Screen::Playing);
        }
        app.world_mut().resource_mut::<ButtonInput<KeyCode>>().reset_all();
        app.world_mut().resource_mut::<ButtonInput<KeyCode>>().press(KeyCode::Backquote);
        app.update();assert_eq!(app.world().resource::<UiState>().screen,Screen::Console);
        app.world_mut().resource_mut::<UiState>().screen=Screen::Playing;
        app.world_mut().resource_mut::<crate::vehicle::Occupancy>().seat=None;
        app.world_mut().resource_mut::<ButtonInput<KeyCode>>().reset_all();
        app.world_mut().resource_mut::<ButtonInput<KeyCode>>().press(KeyCode::F1);
        app.update();assert_eq!(app.world().resource::<UiState>().screen,Screen::Console);
    }

    #[test]
    fn console_keeps_named_space_keys_in_real_command_messages() {
        let mut app = App::new();
        app.init_resource::<Settings>()
            .insert_resource(UiState {
                screen: Screen::Console,
                ..default()
            })
            .init_resource::<ButtonInput<KeyCode>>()
            .init_resource::<ButtonInput<MouseButton>>()
            .add_message::<KeyboardInput>()
            .add_message::<UiIntent>()
            .add_systems(Update, keyboard_ui);
        for logical_key in [
            Key::Character("give".into()),
            Key::Space,
            Key::Character("4".into()),
            Key::Enter,
        ] {
            app.world_mut().write_message(KeyboardInput {
                key_code: KeyCode::Unidentified(bevy::input::keyboard::NativeKeyCode::Unidentified),
                logical_key,
                state: bevy::input::ButtonState::Pressed,
                text: None,
                repeat: false,
                window: Entity::PLACEHOLDER,
            });
        }
        app.update();
        let messages: Vec<_> = app
            .world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .collect();
        assert!(
            matches!(&messages[..], [UiIntent::Action {name,args}] if name == "console" && args["text"] == "give 4")
        );
    }

    #[test]
    fn death_blocks_input_until_authoritative_respawn() {
        let mut app = App::new();
        app.insert_resource(UiState {
            screen: Screen::Inventory,
            ..default()
        });
        app.insert_resource(UiModel {
            connected: true,
            alive: false,
            ..default()
        });
        app.add_systems(Update, follow_life_state);
        app.update();
        let mut state = app.world_mut().resource_mut::<UiState>();
        assert_eq!(state.screen, Screen::Dead);
        state.close_overlay();
        assert_eq!(state.screen, Screen::Dead);
        assert!(state.captures_input());
        app.world_mut().resource_mut::<UiModel>().alive = true;
        app.update();
        assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    }

    #[test]
    fn inactive_viewmodel_camera_cannot_steal_interface_target() {
        let mut app = App::new();
        app.add_systems(Startup, spawn_ui_camera);
        app.world_mut().spawn((
            Camera3d::default(),
            Camera {
                order: 1,
                is_active: false,
                ..default()
            },
        ));
        app.update();
        let expected = app
            .world_mut()
            .query_filtered::<Entity, With<IsDefaultUiCamera>>()
            .single(app.world())
            .unwrap();
        let mut system =
            bevy::ecs::system::SystemState::<bevy::ui::DefaultUiCamera>::new(app.world_mut());
        assert_eq!(system.get(app.world()).unwrap().get(), Some(expected));
        let camera = app.world().get::<Camera>(expected).unwrap();
        assert!(camera.is_active);
        assert!(camera.order > 1);
    }

    #[test]
    fn snapshot_keeps_compartment_and_item_state() {
        let mut model = UiModel::default();
        model.adopt_snapshot(&json!({"connected":true,"player":{"hp":73,"food":0.5,"ammo":8},
            "inventory":{"pages":[{"index":21,"width":4,"height":3,"items":[{"id":4,"name":"Eaglefire","x":1,"y":0,"rot":1,"width":2,"height":1,"amount":1,"quality":65,"gunAmmo":8,"icon":null}]}]},
            "recipes":[{"index":0,"name":"Plank","reason":null,"craftable":true}],
            "events":[{"kind":"console","data":{"text":"Item granted"}}]}));
        assert!(model.connected);
        assert_eq!(model.health, 73.0);
        assert_eq!(model.pages[0].index, 21);
        assert_eq!(model.pages[0].items[0].quality, 65);
        assert_eq!(model.pages[0].items[0].gun_ammo, 8);
        assert!(model.recipes[0].craftable);
        assert_eq!(model.notices, ["Item granted"]);
    }

    #[test]
    fn closing_settings_returns_to_the_correct_screen() {
        let mut state = UiState {
            screen: Screen::Settings,
            settings_return: Screen::MainMenu,
            ..default()
        };
        state.close_overlay();
        assert_eq!(state.screen, Screen::MainMenu);
        assert!(state.captures_input());
        state.screen = Screen::Inventory;
        state.close_overlay();
        assert!(!state.captures_input());
    }

    #[test]
    fn item_buttons_emit_intents_without_mutating_replicated_inventory() {
        let mut app = App::new();
        app.init_resource::<Settings>()
            .init_resource::<UiModel>()
            .init_resource::<UiState>()
            .add_message::<UiIntent>()
            .init_resource::<Time<Real>>()
            .insert_resource(crate::LaunchOptions(crate::cli::Options::default()))
            .add_observer(activate_button);
        app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&json!({"connected":true,
            "inventory":{"pages":[{"index":6,"width":3,"height":3,"items":[{"id":13,"x":1,"y":2,"amount":2}]}]}}));
        app.world_mut().resource_mut::<UiState>().selected_item = Some((6, 1, 2));
        let entity = app.world_mut().spawn(UiButton::Consume).id();
        app.world_mut().trigger(Activate { entity });
        let messages: Vec<_> = app
            .world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .collect();
        assert!(
            matches!(&messages[0],UiIntent::Action {name,args} if name=="consume" && args==&json!({"page":6,"x":1,"y":2}))
        );
        assert_eq!(
            app.world().resource::<UiModel>().pages[0].items[0].amount,
            2
        );
    }

    #[test]
    fn move_intent_preserves_original_coordinates_and_requested_rotation() {
        let mut app = App::new();
        app.init_resource::<Settings>()
            .init_resource::<UiModel>()
            .init_resource::<UiState>()
            .add_message::<UiIntent>()
            .init_resource::<Time<Real>>()
            .insert_resource(crate::LaunchOptions(crate::cli::Options::default()))
            .add_observer(activate_button);
        {
            let mut state = app.world_mut().resource_mut::<UiState>();
            state.selected_item = Some((2, 3, 4));
            state.rotation = 1;
        }
        let entity = app.world_mut().spawn(UiButton::Move(21, 0, 2)).id();
        app.world_mut().trigger(Activate { entity });
        let messages: Vec<_> = app
            .world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .collect();
        assert!(
            matches!(&messages[0],UiIntent::Action {name,args} if name=="moveItem" && args==&json!({"page0":2,"x0":3,"y0":4,"page1":21,"x1":0,"y1":2,"rot1":1}))
        );
    }

    #[test]
    fn retained_events_are_presented_once_until_session_reset() {
        let mut model = UiModel::default();
        let state = json!({"events":[{"seq":1,"kind":"console","data":{"text":"First"}},{"seq":2,"kind":"console","data":{"text":"Second"}}]});
        model.adopt_snapshot(&state);
        model.adopt_snapshot(&state);
        assert_eq!(model.notices, ["First", "Second"]);
        model.reset_session();
        model.adopt_snapshot(&state);
        assert_eq!(model.notices, ["First", "Second"]);
    }
}

#[cfg(test)]
mod network_input_tests {
    use super::*;
    #[test]
    fn x11_repeat_edges_do_not_retoggle_pause_but_text_repeat_still_types() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, bevy::input::InputPlugin))
            .init_resource::<Settings>()
            .insert_resource(UiState {
                screen: Screen::Playing,
                ..default()
            })
            .add_message::<UiIntent>()
            .add_systems(
                PreUpdate,
                suppress_repeat_edges.after(bevy::input::InputSystems),
            )
            .add_systems(Update, keyboard_ui);
        let event = |state, repeat, key| KeyboardInput {
            key_code: KeyCode::Escape,
            logical_key: key,
            state,
            text: None,
            repeat,
            window: Entity::PLACEHOLDER,
        };
        app.world_mut()
            .write_message(event(bevy::input::ButtonState::Pressed, false, Key::Escape));
        app.update();
        assert_eq!(app.world().resource::<UiState>().screen, Screen::Paused);
        app.world_mut()
            .write_message(event(bevy::input::ButtonState::Released, true, Key::Escape));
        app.world_mut()
            .write_message(event(bevy::input::ButtonState::Pressed, true, Key::Escape));
        app.update();
        assert_eq!(app.world().resource::<UiState>().screen, Screen::Paused);
        app.world_mut().resource_mut::<UiState>().screen = Screen::Console;
        app.world_mut().write_message(KeyboardInput {
            key_code: KeyCode::KeyA,
            logical_key: Key::Character("a".into()),
            state: bevy::input::ButtonState::Pressed,
            text: None,
            repeat: true,
            window: Entity::PLACEHOLDER,
        });
        app.update();
        assert_eq!(app.world().resource::<UiState>().console_input, "a");
    }
}
