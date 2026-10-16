//! Settings screen, main-menu navigation and singleplayer entry regressions.
use super::main_menu::{MenuAction, Page};
use super::options::{self, Action, Layout, Row, SliderKind, Tab, Tone};
use super::singleplayer::{self, Entry, MapOptions, SpAction};
use super::*;
use crate::settings::{AntiAliasing, Graphics, Quality};
use std::sync::Mutex;

/// Tests that read back the persisted file share the per-process temp path.
static FILE: Mutex<()> = Mutex::new(());

fn menu_app() -> App {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .init_resource::<Settings>()
        .init_resource::<UiModel>()
        .init_resource::<UiState>()
        .init_resource::<singleplayer::Pending>()
        .add_message::<UiIntent>()
        .insert_resource(crate::LaunchOptions(crate::cli::Options::default()))
        .add_observer(activate_button)
        .add_observer(options::activate)
        .add_observer(super::main_menu::activate)
        .add_observer(singleplayer::activate);
    app
}
fn press(app: &mut App, component: impl Component) {
    let entity = app.world_mut().spawn(component).id();
    app.world_mut().trigger(Activate { entity });
    app.world_mut().despawn(entity);
}
fn intents(app: &mut App) -> Vec<UiIntent> {
    app.world_mut().resource_mut::<Messages<UiIntent>>().drain().collect()
}

#[test]
fn graphics_rows_keep_the_source_defaults_orders_and_labels() {
    let s = Settings::default();
    let g = &s.graphics;
    assert_eq!(g.anti_aliasing, AntiAliasing::Fxaa);
    assert_eq!((g.shadows, g.shadow_distance, g.anisotropy), (Quality::High, 120., 4));
    assert_eq!((g.planar_reflection, g.scope_quality, g.effect_quality), (Quality::Medium, Quality::High, Quality::High));
    assert!(g.bloom && g.outline && g.wind && g.ragdolls && g.grass_displacement && g.render_thread);
    assert!(!s.vsync && !g.ambient_occlusion && !g.chromatic_aberration && !g.sun_shafts);
    let label = |row: Row, s: &Settings| row.value(s, None, None);
    let mut s = s;
    let mut seen = vec![label(Row::AntiAliasing, &s)];
    for _ in 0..6 {
        Row::AntiAliasing.advance(&mut s);
        seen.push(label(Row::AntiAliasing, &s));
    }
    assert_eq!(seen, ["FXAA", "MSAA 2x", "MSAA 4x", "MSAA 8x", "TAA", "Off", "FXAA"]);
    for _ in 0..3 {
        Row::Scope.advance(&mut s);
    }
    assert_eq!(label(Row::Scope, &s), "Medium", "Scope quality cycles without Off");
    assert_eq!(label(Row::Resolution, &s), "Native");
    Row::Resolution.advance(&mut s);
    assert_eq!(label(Row::Resolution, &s), "1280 x 720");
    assert_eq!(label(Row::TargetFps, &s), "Uncapped");
    assert_eq!(label(Row::RenderScale, &s), "Native");
    assert_eq!(label(Row::ShadowDistance, &s), "120 m");
    assert_eq!(label(Row::UiScale, &s), "100%");
    // An unlisted value starts the order (GraphicsOptions.Next).
    s.graphics.shadow_distance = 99.;
    Row::ShadowDistance.advance(&mut s);
    assert_eq!(s.graphics.shadow_distance, Graphics::SHADOW_DISTANCES[0]);
    // Boot-time rows say "(restart)" until the running process matches.
    let boot = crate::graphics::Boot::from(&Settings::default(), false);
    Row::RenderThread.advance(&mut s);
    Row::FogQuality.advance(&mut s);
    assert_eq!(Row::RenderThread.value(&s, Some(&boot), None), "Single (restart)");
    assert_eq!(Row::FogQuality.value(&s, Some(&boot), None), "High (restart)");
    Row::FogQuality.advance(&mut s);
    assert_eq!(Row::FogQuality.value(&s, Some(&boot), None), "Low (restart)");
    Row::FogQuality.advance(&mut s);
    assert_eq!(Row::FogQuality.value(&s, Some(&boot), None), "Medium");
}

#[test]
fn every_row_persists_immediately_and_reloads() {
    let _guard = FILE.lock().unwrap();
    let mut app = menu_app();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Settings;
    press(&mut app, Action::Cycle(Row::AntiAliasing));
    press(&mut app, Action::Cycle(Row::Wind));
    let loaded = Settings::load().unwrap();
    assert_eq!(loaded.graphics.anti_aliasing, AntiAliasing::Msaa2x);
    assert!(!loaded.graphics.wind);
    press(&mut app, Action::ResetBinds);
    assert_eq!(Settings::load().unwrap().bindings, Settings::default().bindings);
}

#[test]
fn key_capture_follows_keybind_menu_messages_and_refuses_conflicts() {
    let _guard = FILE.lock().unwrap();
    let mut app = App::new();
    app.init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .insert_resource(UiState { screen: Screen::Settings, settings_tab: Tab::KeyBinds, ..default() })
        .add_message::<KeyboardInput>()
        .add_message::<UiIntent>()
        .add_systems(Update, keyboard_ui);
    let key = |app: &mut App, code: KeyCode| {
        let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
        keys.reset_all();
        keys.press(code);
        app.update();
    };
    app.world_mut().resource_mut::<UiState>().capturing = Some(GameAction::Jump);
    key(&mut app, KeyCode::KeyW);
    let state = app.world().resource::<UiState>();
    assert_eq!(state.settings_message, "W is already bound to Move Forward \u{2014} free it first");
    assert_eq!((state.settings_tone, state.capturing), (Tone::Bad, None));
    assert_eq!(app.world().resource::<Settings>().bindings[&GameAction::Jump], Binding::Key("Space".into()));
    app.world_mut().resource_mut::<UiState>().capturing = Some(GameAction::Jump);
    key(&mut app, KeyCode::KeyK);
    let state = app.world().resource::<UiState>();
    assert_eq!(state.settings_message, "Jump \u{2192} K");
    assert_eq!(state.settings_tone, Tone::Good);
    assert_eq!(Settings::load().unwrap().bindings[&GameAction::Jump], Binding::Key("KeyK".into()), "Keybinds.Set saves");
    app.world_mut().resource_mut::<UiState>().capturing = Some(GameAction::Jump);
    key(&mut app, KeyCode::Delete);
    assert_eq!(app.world().resource::<UiState>().settings_message, "Jump unbound");
    assert_eq!(app.world().resource::<Settings>().bindings[&GameAction::Jump], Binding::Unbound);
    app.world_mut().resource_mut::<UiState>().capturing = Some(GameAction::Jump);
    key(&mut app, KeyCode::Escape);
    let state = app.world().resource::<UiState>();
    assert_eq!((state.capturing, state.screen), (None, Screen::Settings), "Esc cancels the capture, not the screen");
    assert_eq!(state.settings_message, options::HINT_CANCELLED);
    let mut s = Settings::default();
    s.bindings.insert(GameAction::Jump, Binding::Unbound);
    s.save().unwrap();
}

#[test]
fn source_display_names_contexts_and_key_labels() {
    assert_eq!(GameAction::MoveForward.display_name(), "Move Forward");
    assert_eq!(GameAction::Hotbar10.display_name(), "Hotbar10");
    assert_eq!(GameAction::ToggleFirstPerson.display_name(), "Toggle First Person");
    assert_eq!(GameAction::Jump.context_label(), "on foot");
    assert_eq!(GameAction::VehicleHandbrake.context_label(), "in vehicle");
    assert_eq!(GameAction::BuildSalvage.context_label(), "while building");
    assert_eq!(GameAction::QuickTransfer.context_label(), "in inventory");
    assert_eq!(GameAction::Fire.context_label(), "");
    for (binding, label) in [
        (Binding::Key("Digit1".into()), "1"),
        (Binding::Key("ShiftLeft".into()), "Shift"),
        (Binding::Key("ControlLeft".into()), "Ctrl"),
        (Binding::Key("Backquote".into()), "QuoteLeft"),
        (Binding::Key("KeyG".into()), "G"),
        (Binding::Mouse(5), "Mouse 5"),
        (Binding::Unbound, "\u{2014}"),
    ] {
        assert_eq!(binding.source_label(), label);
    }
}

#[test]
fn sliders_snap_to_the_source_steps() {
    assert!((SliderKind::Look.snap(0.123) - 0.12).abs() < 1e-5);
    assert!((SliderKind::Look.snap(9.) - 0.5).abs() < 1e-6);
    assert!((SliderKind::Heli.snap(1.02) - 1.0).abs() < 1e-5);
    assert_eq!(SliderKind::Heli.label(1.), "1.00x");
    assert_eq!(SliderKind::Look.label(0.12), "0.12");
}

#[test]
fn dashboard_submenus_and_settings_round_trip_to_the_dashboard() {
    let mut app = menu_app();
    press(&mut app, MenuAction::Page(Page::Configuration));
    assert_eq!(app.world().resource::<UiState>().menu.page, Page::Configuration);
    press(&mut app, MenuAction::Settings(Layout::Combined, Tab::Graphics));
    let state = app.world().resource::<UiState>();
    assert_eq!((state.screen, state.settings_layout, state.settings_return), (Screen::Settings, Layout::Combined, Screen::MainMenu));
    press(&mut app, Action::Back);
    let state = app.world().resource::<UiState>();
    assert_eq!((state.screen, state.menu.page), (Screen::MainMenu, Page::Dashboard), "BackToDashboard");
    press(&mut app, MenuAction::Page(Page::Play));
    press(&mut app, UiButton::Open(Screen::Servers));
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Servers);
    app.world_mut().resource_mut::<UiState>().close_overlay();
    assert_eq!(app.world().resource::<UiState>().menu.page, Page::Dashboard);
    press(&mut app, MenuAction::Stub("Tutorial"));
    let state = app.world().resource::<UiState>();
    assert_eq!((state.menu.page, state.menu.stub.as_str()), (Page::Stub, "Tutorial"));
    // Pause -> Settings is always the tabbed SettingsScreen.
    app.world_mut().resource_mut::<UiState>().screen = Screen::Paused;
    press(&mut app, UiButton::Open(Screen::Settings));
    let state = app.world().resource::<UiState>();
    assert_eq!((state.settings_layout, state.settings_return), (Layout::Tabs, Screen::Paused));
}

#[test]
fn retail_rows_play_their_map_and_carry_the_zombies_world_rule() {
    let mut app = menu_app();
    app.world_mut().resource_mut::<UiState>().menu.sp.loaded = true;
    press(&mut app, SpAction::Select(Entry::Official(4)));
    press(&mut app, SpAction::Step(singleplayer::Opt::Zombies, 1));
    press(&mut app, SpAction::Play);
    assert!(matches!(&intents(&mut app)[..], [UiIntent::Play { map }] if map == "Yukon"));
    assert!(!app.world().resource::<crate::LaunchOptions>().0.zombies, "Zombies: Off reaches the host init");
    assert_eq!(app.world().resource::<UiState>().map, "Yukon");
    press(&mut app, SpAction::Step(singleplayer::Opt::Zombies, 1));
    press(&mut app, SpAction::Select(Entry::Official(2)));
    press(&mut app, SpAction::Play);
    assert!(intents(&mut app).is_empty());
    assert!(app.world().resource::<crate::LaunchOptions>().0.zombies);
    let sp = &app.world().resource::<UiState>().menu.sp;
    assert_eq!(sp.note.as_deref(), Some("Russia isn't ported yet \u{2014} only PEI is playable right now."));
    press(&mut app, SpAction::Select(Entry::Playground));
    press(&mut app, SpAction::Play);
    assert!(intents(&mut app).is_empty(), "the gun range has no native world yet");
}

#[test]
fn generated_entries_validate_the_seed_and_queue_the_cli_equivalent_entry() {
    let mut app = menu_app();
    app.world_mut().resource_mut::<UiState>().menu.sp.loaded = true;
    press(&mut app, SpAction::Select(Entry::Infinite));
    app.world_mut().resource_mut::<UiState>().menu.sp.seed = "12x".into();
    press(&mut app, SpAction::Play);
    assert!(intents(&mut app).is_empty());
    assert_eq!(app.world().resource::<UiState>().menu.sp.note.as_deref(), Some("Seed must be a whole number."));
    app.world_mut().resource_mut::<UiState>().menu.sp.seed = "77".into();
    press(&mut app, SpAction::Play);
    assert!(matches!(&intents(&mut app)[..], [UiIntent::Play { map }] if map == "Procedural"));
    assert_eq!(app.world().resource::<crate::LaunchOptions>().0.seed, 77, "--map Procedural --seed 77");
    press(&mut app, SpAction::Select(Entry::Generate));
    press(&mut app, SpAction::Lakes);
    press(&mut app, SpAction::Play);
    assert!(intents(&mut app).is_empty(), "generation waits for the loading frame");
    let request = app.world().resource::<singleplayer::Pending>().request.clone().unwrap();
    assert!(request.create && request.lakes && request.seed == 77);
    assert_eq!(request.directory, singleplayer::maps_root().join(&request.name));
    assert!(request.name.starts_with("Island 77"));
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Connecting);
}

#[test]
fn reset_data_arms_then_deletes_only_the_selected_retail_save() {
    let _guard = FILE.lock().unwrap();
    let mut app = menu_app();
    app.world_mut().resource_mut::<UiState>().menu.sp.loaded = true;
    let save = crate::player::save_path("Washington");
    std::fs::create_dir_all(save.parent().unwrap()).unwrap();
    std::fs::write(&save, "{}").unwrap();
    press(&mut app, SpAction::Select(Entry::Official(1)));
    press(&mut app, SpAction::Reset);
    assert!(app.world().resource::<UiState>().menu.sp.reset_armed && save.is_file());
    // Any selection change disarms (DisarmReset).
    press(&mut app, SpAction::Select(Entry::Generate));
    assert!(!app.world().resource::<UiState>().menu.sp.reset_armed);
    press(&mut app, SpAction::Reset);
    press(&mut app, SpAction::Reset);
    assert!(!save.is_file(), "generated entries keep the last retail folder, as the source does");
    assert_eq!(app.world().resource::<UiState>().menu.sp.note.as_deref(), Some("Deleted the saved world for Generate Island."));
}

#[test]
fn saved_islands_list_newest_first_and_delete_only_generated_maps() {
    let root = std::env::temp_dir().join(format!("ug-islands-{}-{:?}", std::process::id(), std::thread::current().id()));
    let _ = std::fs::remove_dir_all(&root);
    let make = |name: &str, seed: i32, marker: bool| {
        let dir = root.join(name);
        std::fs::create_dir_all(dir.join("objects")).unwrap();
        std::fs::write(dir.join("finite.json"), format!("{{\"name\":\"{name}\"}}")).unwrap();
        if marker {
            std::fs::write(dir.join("objects").join(format!("editor_{name}_proc.txt")), seed.to_string()).unwrap();
        }
        dir
    };
    make("Island 5", 5, true);
    std::thread::sleep(std::time::Duration::from_millis(20));
    make("Island 9", 9, true);
    let hand = make("Hand Built", 0, false);
    let list = singleplayer::list_islands(&root);
    assert_eq!(list.iter().map(|(n, s, _)| (n.as_str(), *s)).collect::<Vec<_>>(), [("Island 9", 9), ("Island 5", 5)]);
    assert_eq!(singleplayer::unique(&root, "Island 9"), "Island 9 2");
    assert_eq!(singleplayer::unique(&root, "Island 1"), "Island 1");
    assert_eq!(singleplayer::sanitise("../evil/name"), Some("evilname".into()));
    assert!(singleplayer::delete_island(&hand).is_err() && hand.is_dir());
    singleplayer::delete_island(&list[0].2).unwrap();
    assert_eq!(singleplayer::list_islands(&root).len(), 1);
    let _ = std::fs::remove_dir_all(&root);
}

#[test]
fn map_options_clamp_values_from_an_older_wider_schema() {
    let _guard = FILE.lock().unwrap();
    std::fs::create_dir_all(MapOptions::path().parent().unwrap()).unwrap();
    std::fs::write(MapOptions::path(), r#"{"zombies":2,"day":9,"cheats":true}"#).unwrap();
    let options = MapOptions::load();
    assert_eq!((options.zombies, options.day, options.cheats), (1, 3, true));
    assert!(!options.zombies_enabled());
    let _ = std::fs::remove_file(MapOptions::path());
}

#[test]
fn seed_field_types_and_menu_open_hook_selects_pages() {
    let mut state = UiState::default();
    super::main_menu::apply_open(&mut state, "map", Some("proc"));
    assert_eq!((state.menu.page, state.menu.sp.category), (Page::Singleplayer, 1));
    super::main_menu::apply_open(&mut state, "keybinds", None);
    assert_eq!((state.screen, state.settings_tab), (Screen::Settings, Tab::KeyBinds));
    let mut app = App::new();
    app.insert_resource(UiState::default()).add_message::<KeyboardInput>();
    app.world_mut().resource_mut::<UiState>().menu.sp.seed.clear();
    for ch in ["4", "2"] {
        app.world_mut().write_message(KeyboardInput {
            key_code: KeyCode::Digit4,
            logical_key: Key::Character(ch.into()),
            state: bevy::input::ButtonState::Pressed,
            text: None,
            repeat: false,
            window: Entity::PLACEHOLDER,
        });
    }
    app.add_systems(Update, |mut s: ResMut<UiState>, mut r: MessageReader<KeyboardInput>| singleplayer::typed(&mut s, &mut r));
    app.update();
    assert_eq!(app.world().resource::<UiState>().menu.sp.seed, "42");
}
