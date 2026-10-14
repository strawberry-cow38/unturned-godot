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
    pub map_name: String,
    pub health: f32,
    pub alive: bool,
    pub food: f32,
    pub water: f32,
    pub stamina: f32,
    pub infection: f32,
    pub ammo: i32,
    pub held_item_id: u16,
    pub stance: String,
    pub pages: Vec<InventoryPage>,
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
            map_name: String::new(),
            health: 0.0,
            alive: true,
            food: 0.0,
            water: 0.0,
            stamina: 0.0,
            infection: 0.0,
            ammo: -1,
            held_item_id: 0,
            stance: String::new(),
            pages: vec![],
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
        self.recipes.clear();
    }
    pub fn adopt_snapshot(&mut self, snapshot: &Value) {
        self.connected = snapshot["connected"].as_bool().unwrap_or(false);
        let p = &snapshot["player"];
        self.health = number(&p["hp"]);
        self.alive = p["alive"].as_bool().unwrap_or(true);
        self.food = number(&p["food"]);
        self.water = number(&p["water"]);
        self.stamina = number(&p["stamina"]);
        self.infection = number(&p["infection"]);
        self.ammo = p["ammo"].as_i64().unwrap_or(-1) as i32;
        self.held_item_id = p["heldItemId"].as_u64().unwrap_or(0) as u16;
        self.stance = p["stance"]
            .as_str()
            .map(str::to_owned)
            .unwrap_or_else(|| p["stance"].to_string());
        if let Ok(pages) = serde_json::from_value(snapshot["inventory"]["pages"].clone()) {
            self.pages = pages;
        }
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
    Play { map: String },
    Resume,
    Save,
    Load,
    Disconnect,
    Quit,
    Action { name: String, args: Value },
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum Screen {
    #[default]
    MainMenu,
    Playing,
    Paused,
    Dead,
    Inventory,
    Crafting,
    Settings,
    Console,
    Chat,
}

#[derive(Resource, Debug)]
pub struct UiState {
    pub screen: Screen,
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
    pub fn captures_input(&self) -> bool {
        self.screen != Screen::Playing
    }

    pub fn close_overlay(&mut self) {
        self.screen = match self.screen {
            Screen::Settings => self.settings_return,
            Screen::MainMenu => Screen::MainMenu,
            Screen::Dead => Screen::Dead,
            _ => Screen::Playing,
        };
        self.capturing = None;
    }
}

#[derive(Clone, Component)]
enum UiButton {
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
struct OverlayRoot;
#[derive(Component)]
struct HudRoot;
#[derive(Component)]
struct VitalsText;
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
            .init_resource::<UiModel>()
            .init_resource::<Settings>()
            .add_message::<UiIntent>()
            .add_systems(Startup, spawn_ui_camera)
            .add_systems(Startup, spawn_hud)
            .add_systems(
                Update,
                (
                    follow_life_state,
                    keyboard_ui,
                    rebuild_overlay,
                    update_hud,
                    button_appearance,
                )
                    .chain()
                    .in_set(UiUpdate),
            )
            .add_observer(activate_button);
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
                VitalsText,
                text("", 16.0, TEXT),
                Node {
                    position_type: PositionType::Absolute,
                    left: px(24),
                    bottom: px(24),
                    ..default()
                },
                BackgroundColor(Color::srgba(0.05, 0.06, 0.07, 0.55)),
            ));
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
            p.spawn((
                text("+", 22.0, Color::srgba(0.9, 0.92, 0.95, 0.7)),
                Node {
                    position_type: PositionType::Absolute,
                    left: percent(50),
                    top: percent(50),
                    ..default()
                },
            ));
        });
}

fn update_hud(
    state: Res<UiState>,
    model: Res<UiModel>,
    catalog: Res<crate::entities::ItemCatalog>,
    mut roots: Query<&mut Visibility, With<HudRoot>>,
    mut labels: ParamSet<(
        Query<&mut Text, With<VitalsText>>,
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
        **t = if model.connected {
            format!(
                "HEALTH {:3.0}    FOOD {:3.0}%    WATER {:3.0}%    STAMINA {:3.0}%",
                model.health,
                model.food * 100.0,
                model.water * 100.0,
                model.stamina * 100.0
            )
        } else {
            model.status.clone()
        };
    }
    for mut t in &mut labels.p1() {
        **t = if let Some(item) = catalog.items.get(&model.held_item_id) {
            if item.item_type == "gun" && model.ammo >= 0 {
                format!("{}  /  {}", item.name, model.ammo)
            } else {
                item.name.clone()
            }
        } else {
            String::new()
        };
    }
    for mut t in &mut labels.p2() {
        **t = model.interaction.clone();
    }
    for mut t in &mut labels.p3() {
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

fn keyboard_ui(
    mut state: ResMut<UiState>,
    mut settings: ResMut<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    mut characters: MessageReader<KeyboardInput>,
    mut intents: MessageWriter<UiIntent>,
) {
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
    if state.screen == Screen::Playing {
        if settings.just_pressed(GameAction::Inventory, &keys, &mouse)
            || keys.just_pressed(KeyCode::Tab)
        {
            state.screen = Screen::Inventory;
        } else if settings.just_pressed(GameAction::Craft, &keys, &mouse) {
            state.screen = Screen::Crafting;
        } else if settings.just_pressed(GameAction::Console, &keys, &mouse)
            || keys.just_pressed(KeyCode::F1)
        {
            state.screen = Screen::Console;
        } else if settings.just_pressed(GameAction::Chat, &keys, &mouse) {
            state.screen = Screen::Chat;
        }
    } else if state.screen == Screen::Inventory {
        if settings.just_pressed(GameAction::Inventory, &keys, &mouse)
            || keys.just_pressed(KeyCode::Tab)
        {
            state.screen = Screen::Playing;
        } else if keys.just_pressed(KeyCode::KeyR) {
            state.rotation = (state.rotation + 1) % 2;
        }
    } else if state.screen == Screen::Crafting
        && settings.just_pressed(GameAction::Craft, &keys, &mouse)
    {
        state.screen = Screen::Playing;
    }
}

fn activate_button(
    event: On<Activate>,
    buttons: Query<&UiButton>,
    mut state: ResMut<UiState>,
    mut settings: ResMut<Settings>,
    model: Res<UiModel>,
    mut intents: MessageWriter<UiIntent>,
) {
    let Ok(action) = buttons.get(event.entity) else {
        return;
    };
    match action {
        UiButton::Open(screen) => {
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
        UiButton::Drop | UiButton::Consume | UiButton::Equip => {
            if let Some((page, x, y)) = state.selected_item {
                let name = match action {
                    UiButton::Drop => "dropItem",
                    UiButton::Consume => "consume",
                    _ => "equipItem",
                };
                intents.write(UiIntent::Action {
                    name: name.into(),
                    args: json!({"page":page,"x":x,"y":y,"slot":0}),
                });
                state.selected_item = None;
            }
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
    roots: Query<Entity, With<OverlayRoot>>,
    state: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    assets: Res<AssetServer>,
    mut cache: Local<String>,
) {
    // Do not destroy and rebuild the widgets for every 50 Hz player snapshot.
    let detail = match state.screen {
        Screen::Inventory => format!("{:?}", model.pages),
        Screen::Crafting => format!("{:?}", model.recipes),
        Screen::Console | Screen::Chat => format!("{:?}", model.notices),
        Screen::MainMenu => model.status.clone(),
        _ => String::new(),
    };
    let signature = format!("{state:?}|{settings:?}|{detail}");
    if *cache == signature {
        return;
    }
    *cache = signature;
    for root in &roots {
        commands.entity(root).despawn();
    }
    if state.screen == Screen::Playing {
        return;
    }
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
                if state.screen == Screen::MainMenu {
                    0.62
                } else {
                    0.48
                },
            )),
        ))
        .with_children(|root| {
            root.spawn((
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
                Screen::Paused => pause_panel(panel),
                Screen::Dead => death_panel(panel),
                Screen::Inventory => inventory_panel(panel, &state, &model, &assets),
                Screen::Crafting => crafting_panel(panel, &state, &model),
                Screen::Settings => settings_panel(panel, &state, &settings),
                Screen::Console | Screen::Chat => console_panel(panel, &state, &model),
                Screen::Playing => {}
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
        button(p, "Settings", UiButton::Open(Screen::Settings));
        button(p, "Quit", UiButton::Quit);
    });
}

fn death_panel(panel: &mut ChildSpawnerCommands) {
    panel.spawn(text("You died", 32.0, TEXT));
    panel.spawn(text("Respawn now to return to the world.", 15.0, DIM));
    panel.spawn(row()).with_children(|p| {
        button(p, "Respawn", UiButton::Respawn);
        button(p, "Leave world", UiButton::Disconnect);
    });
}

fn pause_panel(panel: &mut ChildSpawnerCommands) {
    panel.spawn(text("PAUSED", 28.0, TEXT));
    panel.spawn(row()).with_children(|p| {
        button(p, "Resume", UiButton::Resume);
        button(p, "Save world", UiButton::Save);
        button(p, "Load world", UiButton::Load);
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
) {
    panel.spawn(row()).with_children(|p| {
        p.spawn(text("INVENTORY", 26.0, TEXT));
        button(p, "Crafting", UiButton::Open(Screen::Crafting));
        button(p, "Close", UiButton::Resume);
    });
    let pages = visible_pages(model);
    if pages.is_empty() {
        panel.spawn(text("Waiting for inventory...", 16.0, DIM));
        return;
    }
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
    panel
        .spawn(Node {
            width: px(cell * f32::from(page.width)),
            height: px(cell * f32::from(page.height)),
            position_type: PositionType::Relative,
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
                button(p, "Equip", UiButton::Equip);
                button(p, "Use", UiButton::Consume);
                button(p, "Drop", UiButton::Drop);
            });
        }
    }
}

fn crafting_panel(panel: &mut ChildSpawnerCommands, state: &UiState, model: &UiModel) {
    panel.spawn(row()).with_children(|p| {
        p.spawn(text("CRAFTING", 26.0, TEXT));
        button(p, "Inventory", UiButton::Open(Screen::Inventory));
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
            matches!(&messages[0],UiIntent::Action {name,args} if name=="consume" && args==&json!({"page":6,"x":1,"y":2,"slot":0}))
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
