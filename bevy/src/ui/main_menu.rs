//! MainMenu.cs dashboard and submenus (MenuDashboardUI / MenuPlayUI /
//! MenuSurvivorsUI / MenuConfigurationUI / MenuWorkshopUI). Retail-only rows
//! without a backend show the source's stub panel. The barn diorama camera is
//! not recreated here; the native menu keeps its existing world view.
use super::godot::{self, Ctx, GOLD, Skin, look};
use super::*;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Page {
    #[default]
    Dashboard,
    Play,
    Singleplayer,
    Survivors,
    Configuration,
    Workshop,
    Stub,
}
#[derive(Debug, Default)]
pub struct MenuState {
    pub page: Page,
    pub stub: String,
    pub sp: super::singleplayer::Selector,
}

#[derive(Component, Clone, Debug)]
pub enum MenuAction {
    Page(Page),
    Stub(&'static str),
    Settings(super::options::Layout, super::options::Tab),
    Back,
}

pub(super) struct MainMenuPlugin;
impl Plugin for MainMenuPlugin {
    fn build(&self, app: &mut App) {
        app.add_observer(activate).add_systems(Startup, open_from_env);
    }
}

/// MainMenu._Ready's UG_MENUOPEN/UG_MENUTAB render hooks (plus the native
/// settings layouts), so an offline capture can show a submenu.
fn open_from_env(mut state: ResMut<UiState>) {
    let Ok(open) = std::env::var("UG_MENUOPEN") else {
        return;
    };
    apply_open(&mut state, &open, std::env::var("UG_MENUTAB").ok().as_deref());
}
pub(crate) fn apply_open(state: &mut UiState, open: &str, tab: Option<&str>) {
    use super::options::{Layout, Tab};
    let page = match open {
        "map" | "play" | "options" | "advanced" => Page::Singleplayer,
        "playmenu" => Page::Play,
        "survivors" => Page::Survivors,
        "config" => Page::Configuration,
        "workshop" => Page::Workshop,
        "graphics" | "settings" | "controls" | "keybinds" => {
            state.settings_return = Screen::MainMenu;
            state.settings_layout = if open == "graphics" { Layout::Combined } else { Layout::Tabs };
            state.settings_tab = match open {
                "controls" => Tab::Controls,
                "keybinds" => Tab::KeyBinds,
                _ => Tab::Graphics,
            };
            state.screen = Screen::Settings;
            return;
        }
        _ => return,
    };
    state.menu.page = page;
    if page == Page::Singleplayer {
        state.menu.sp.ensure_loaded();
        state.menu.sp.advanced = open == "advanced";
        // Source: Advanced opens over the selector while the Config fold stays shut.
        state.menu.sp.config_open = std::env::var("UG_MENUCONFIG").as_deref() == Ok("1");
        if tab == Some("proc") {
            state.menu.sp.category = 1;
        }
        if tab == Some("generate") {
            state.menu.sp.entry = super::singleplayer::Entry::Generate;
        }
    }
}

pub(super) fn activate(event: On<Activate>, actions: Query<&MenuAction>, mut state: ResMut<UiState>) {
    let Ok(action) = actions.get(event.entity) else {
        return;
    };
    match action {
        MenuAction::Page(page) => {
            if *page == Page::Singleplayer {
                state.menu.sp.ensure_loaded();
                // TogglePlayPanel: Advanced starts collapsed on each open.
                state.menu.sp.advanced = false;
            }
            state.menu.page = *page;
        }
        MenuAction::Stub(name) => {
            state.menu.stub = (*name).into();
            state.menu.page = Page::Stub;
        }
        MenuAction::Settings(layout, tab) => {
            state.settings_layout = *layout;
            state.settings_tab = *tab;
            state.settings_return = Screen::MainMenu;
            state.capturing = None;
            state.screen = Screen::Settings;
        }
        MenuAction::Back => state.menu.page = Page::Dashboard,
    }
}

/// The whole main-menu canvas for the current page.
pub(super) fn screen(root: &mut ChildSpawnerCommands, c: &Ctx, state: &UiState, model: &UiModel, assets: &AssetServer) {
    // Title wordmark, centred at the top (MainMenu.BuildUI).
    root.spawn((
        Node {
            position_type: PositionType::Absolute,
            left: px(0),
            right: px(0),
            top: c.px(26.),
            height: c.px(70.),
            justify_content: JustifyContent::Center,
            ..default()
        },
        Pickable::IGNORE,
    ))
    .with_children(|t| {
        t.spawn((
            c.text("UNTURNED", 60., Color::srgb(0.95, 0.94, 0.90)),
            TextShadow {
                offset: Vec2::splat(2. * c.k),
                color: Color::srgba(0., 0., 0., 0.6),
            },
        ));
    });
    root.spawn((
        Node {
            position_type: PositionType::Absolute,
            left: px(0),
            right: px(0),
            top: c.px(94.),
            height: c.px(30.),
            justify_content: JustifyContent::Center,
            ..default()
        },
        Pickable::IGNORE,
    ))
    .with_children(|t| {
        t.spawn(c.text("cow.0", 22., Color::srgb(0.85, 0.78, 0.55)));
    });
    match state.menu.page {
        Page::Dashboard => dashboard(root, c, assets),
        Page::Play => submenu(root, c, assets, "PLAY", |b| {
            row(b, c, assets, "tutorial", "Tutorial", MenuAction::Stub("Tutorial"));
            row(b, c, assets, "singleplayer", "Singleplayer", MenuAction::Page(Page::Singleplayer));
            // OfflineMode: disabled and still visible, so the reason is discoverable.
            let offline = offline_mode();
            row_ui(b, c, assets, "servers", "Multiplayer", UiButton::Open(Screen::Servers), offline);
            row_ui(b, c, assets, "connect", "Direct Connect", UiButton::Open(Screen::DirectConnect), offline);
            // Native addition: the integrated UDP host has no source menu row.
            row_ui(b, c, assets, "servers", "Host", UiButton::Open(Screen::Host), offline);
            back(b, c, assets);
        }),
        Page::Singleplayer => {
            super::singleplayer::panel(root, c, state, assets);
        }
        Page::Survivors => submenu(root, c, assets, "SURVIVORS", |b| {
            row(b, c, assets, "character", "Character", MenuAction::Stub("Character"));
            row_ui(b, c, assets, "appearance", "Appearance", UiButton::Open(Screen::Appearance), false);
            row(b, c, assets, "group", "Group", MenuAction::Stub("Group"));
            row(b, c, assets, "clothing", "Clothing", MenuAction::Stub("Clothing"));
            back(b, c, assets);
        }),
        Page::Configuration => submenu(root, c, assets, "CONFIGURATION", |b| {
            use super::options::{Layout, Tab};
            row(b, c, assets, "graphics", "Graphics", MenuAction::Settings(Layout::Combined, Tab::Graphics));
            row(b, c, assets, "display", "Display", MenuAction::Stub("Display"));
            row(b, c, assets, "audio", "Audio", MenuAction::Stub("Audio"));
            // The source stub names "Keybinds + mouse sensitivity": the shared
            // pause SettingsScreen already has both, so this row opens it.
            row(b, c, assets, "controls", "Controls", MenuAction::Settings(Layout::Tabs, Tab::Controls));
            row(b, c, assets, "options", "Options", MenuAction::Stub("Options"));
            back(b, c, assets);
        }),
        Page::Workshop => workshop(root, c, assets),
        Page::Stub => stub(root, c, assets, &state.menu.stub),
    }
    let idle = UiModel::default().status;
    if matches!(state.menu.page, Page::Dashboard) && !model.status.is_empty() && model.status != idle {
        // Native load/failure status (the source shows a join-error modal instead).
        root.spawn((
            Node { position_type: PositionType::Absolute, left: c.px(22.), bottom: c.px(80.), ..default() },
            Pickable::IGNORE,
        ))
        .with_children(|s| {
            s.spawn(c.text(&model.status, 14., Color::srgba(1., 1., 1., 0.55)));
        });
    }
}

/// MenuDashboardUI: Play 170, Survivors 230, Configuration 290, Workshop 350,
/// Exit anchored 70 above the bottom; 200x50 at x=22, icon + "  Label".
fn dashboard(root: &mut ChildSpawnerCommands, c: &Ctx, assets: &AssetServer) {
    let entries: [(&str, &str, f32, Option<MenuAction>); 4] = [
        ("play", "Play", 170., Some(MenuAction::Page(Page::Play))),
        ("survivors", "Survivors", 230., Some(MenuAction::Page(Page::Survivors))),
        ("configuration", "Configuration", 290., Some(MenuAction::Page(Page::Configuration))),
        ("workshop", "Workshop", 350., Some(MenuAction::Page(Page::Workshop))),
    ];
    for (icon, text, y, action) in entries {
        dash_button(root, c, assets, icon, text, Node { top: c.px(y), ..default() }, action, None);
    }
    dash_button(root, c, assets, "exit", "Exit", Node { bottom: c.px(70. - 50.), ..default() }, None, Some(UiButton::Quit));
}

#[allow(clippy::too_many_arguments)]
fn dash_button(
    root: &mut ChildSpawnerCommands,
    c: &Ctx,
    assets: &AssetServer,
    icon: &str,
    text: &str,
    place: Node,
    action: Option<MenuAction>,
    ui: Option<UiButton>,
) {
    let node = Node {
        position_type: PositionType::Absolute,
        left: c.px(22.),
        width: c.px(200.),
        height: c.px(50.),
        align_items: AlignItems::Center,
        padding: UiRect::horizontal(c.px(4.)),
        column_gap: c.px(4.),
        ..place
    };
    let label = format!("  {text}");
    let content = |b: &mut ChildSpawnerCommands, color: Color| {
        b.spawn(c.icon(assets, &format!("menu/icon_{icon}.png"), 40.));
        b.spawn((c.text(label, 20., color), Pickable::IGNORE));
    };
    match (action, ui) {
        (Some(action), _) => godot::button(root, c, look(Skin::Default), node, Some(action), content),
        (None, ui) => godot::button(root, c, look(Skin::Default), node, ui, content),
    }
}

/// SubPanel: centred PanelContainer, margin 14, gold header 24, rows 300x50.
fn submenu(root: &mut ChildSpawnerCommands, c: &Ctx, _assets: &AssetServer, title: &str, rows: impl FnOnce(&mut ChildSpawnerCommands)) {
    root.spawn((Node { padding: UiRect::all(c.px(14.)), ..default() }, godot::panel_color()))
        .with_children(|panel| {
            panel
                .spawn(Node { flex_direction: FlexDirection::Column, row_gap: c.px(8.), ..default() })
                .with_children(|b| {
                    b.spawn(c.text(title, 24., GOLD));
                    rows(b);
                });
        });
}

fn row_node(c: &Ctx) -> Node {
    Node {
        min_width: c.px(300.),
        height: c.px(50.),
        align_items: AlignItems::Center,
        padding: UiRect::horizontal(c.px(4.)),
        column_gap: c.px(4.),
        ..default()
    }
}
/// SubRow: retail SleekButtonIcon, icon resized to 32x32, "   Text" at 18.
fn row(b: &mut ChildSpawnerCommands, c: &Ctx, assets: &AssetServer, icon: &str, text: &str, action: MenuAction) {
    let label = format!("   {text}");
    godot::button(b, c, look(Skin::Default), row_node(c), Some(action), |p, color| {
        p.spawn(c.icon(assets, &format!("menu/icon_{icon}.png"), 32.));
        p.spawn((c.text(label, 18., color), Pickable::IGNORE));
    });
}
/// OfflineMode.Enabled: the launcher's UG_OFFLINE=1.
pub(crate) fn offline_mode() -> bool {
    std::env::var("UG_OFFLINE").as_deref() == Ok("1")
}
fn row_ui(b: &mut ChildSpawnerCommands, c: &Ctx, assets: &AssetServer, icon: &str, text: &str, action: UiButton, disabled: bool) {
    let label = format!("   {text}");
    let mut row_look = look(Skin::Default);
    row_look.disabled = disabled;
    godot::button(b, c, row_look, row_node(c), Some(action), |p, color| {
        p.spawn(c.icon(assets, &format!("menu/icon_{icon}.png"), 32.));
        p.spawn((c.text(label, 18., color), Pickable::IGNORE));
    });
}
/// AddBackRow: the Exit icon returns to the dashboard.
fn back(b: &mut ChildSpawnerCommands, c: &Ctx, assets: &AssetServer) {
    row(b, c, assets, "exit", "Back", MenuAction::Back);
}

/// BuildWorkshopPanel rows. The map editor is not native yet, so Editor and
/// the showcase open the stub instead of an editor session.
fn workshop(root: &mut ChildSpawnerCommands, c: &Ctx, assets: &AssetServer) {
    root.spawn((Node::default(), godot::panel_color())).with_children(|panel| {
        panel
            .spawn(Node { flex_direction: FlexDirection::Column, row_gap: c.px(10.), ..default() })
            .with_children(|b| {
                b.spawn(c.text("WORKSHOP", 22., Color::WHITE));
                for (icon, text, stub) in [
                    ("browse", "Browse Workshop", "Browse"),
                    ("submit", "Submit Content", "Submit"),
                    ("editor", "Editor", "Editor"),
                    ("editor", "Map Tool Showcase", "Map Tool Showcase"),
                    ("localization", "Localization", "Localization"),
                    ("spawns", "Spawns Editor", "Spawns"),
                    ("subscriptions", "Subscriptions", "Subscriptions"),
                    ("docs", "Docs", "Docs"),
                    ("tutorial", "Tutorial", "Tutorial"),
                ] {
                    row(b, c, assets, icon, text, MenuAction::Stub(stub));
                }
                godot::separator(b, c);
                b.spawn(c.text("NEW MAP / SAVED MAPS \u{2014} the map editor is not native yet", 13., Color::srgb(0.72, 0.78, 0.84)));
                godot::separator(b, c);
                back(b, c, assets);
            });
    });
}

/// BuildStubPanel/ShowStub: "not implemented yet — coming to Cow.0".
fn stub(root: &mut ChildSpawnerCommands, c: &Ctx, assets: &AssetServer, name: &str) {
    root.spawn((Node::default(), godot::panel_color())).with_children(|panel| {
        panel
            .spawn(Node { flex_direction: FlexDirection::Column, row_gap: c.px(8.), ..default() })
            .with_children(|b| {
                b.spawn(c.text(name.to_uppercase(), 22., Color::WHITE));
                b.spawn(c.text("not implemented yet \u{2014} coming to Cow.0", 16., Color::srgb(0.8, 0.8, 0.8)));
                godot::separator(b, c);
                back(b, c, assets);
            });
    });
}
